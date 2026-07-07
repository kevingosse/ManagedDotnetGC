using ManagedDotnetGC.Dac;
using NativeObjects;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using static ManagedDotnetGC.Log;

namespace ManagedDotnetGC;

internal unsafe partial class GCHeap : Interfaces.IGCHeap
{
    internal static readonly int SizeOfObject = sizeof(nint) * 3;

    private readonly ManualResetEventSlim _gcEvent = new(false);

    private IGCToCLRInvoker _gcToClr;
    private readonly GCHandleManager _gcHandleManager;

    private readonly IGCHeap _nativeObject;
    private DacManager? _dacManager;

    private MethodTable* _freeObjectMethodTable;

    private readonly RegionAllocator _regionAllocator;

    // Sharded allocation supply (M7): one lock per supply shard, the reservoir lock,
    // and the global pool lock. Threads are round-robined across shards at first
    // allocation; the affinity rides in the per-thread stash struct ([ThreadStatic]
    // dies on EE threads).
    private GcAwareLock[] _shardLocks;
    private readonly GcAwareLock _reservoirLock;
    private readonly GcAwareLock _poolLock;
    private readonly GcAwareLock _gcLock;
    private long _allocatedSinceGC;
    private long _totalAllocatedBytes;
    private long _lastLiveBytes;
    private long _budget = Region.MinGCBudget;

    // Sticky-generation accounting (SPEC-M4): live bytes measured by the last full
    // collection, and survivor bytes promoted by young collections since then. Their sum
    // estimates total live (old deaths are invisible until the next full collection).
    private long _liveAtLastFull;
    private long _promotedSinceFull;

    // The starvation full trigger (M7 memory exchange rate). The census run measured why
    // uncapped committed reached 13× live on soh: no bump region is ever wholly dead
    // (smeared survivors), so between full collections the heap only reuses what young
    // sweeps relink — and refilled holes fragment below the linking floor after roughly
    // one survivor generation, leaving young supply ~200 MB/cycle short of the budget.
    // Only a full sweep re-coalesces those strands (the census full recovered 2.2 GB of
    // holes without freeing one region). So: once committed outgrows a multiple of live,
    // run a full whenever the recovered free capacity cannot absorb the next runway —
    // collect strands back instead of committing fresh regions. Set post-trim (the honest
    // post-collection capacity), consumed by the next trigger decision.
    private bool _fullForStarvation;

    // A full that could not restock free capacity (hostile scatter, genuinely growing
    // live set) must not re-fire on every collection: stand down until the heap has
    // grown a budget past what that full could reach.
    private bool _starvationMuted;
    private long _starvationRearm;

    // DOTNET_GCFullRatio: committed/live percent gate on the starvation trigger, default
    // 250. Below the gate, growing the heap is cheaper than extra full collections.
    private long _fullRatioPercent = 250;

    // DOTNET_GCgen0size: latency knob overriding the young allocation budget, 0 = adaptive
    private long _youngBudgetCap;

    // Adaptive nursery boost (M8, TechEmpower): max(64 MB, live) collects 100+ times a
    // second on web workloads (tiny live set, huge alloc rate), and each young pause has
    // a fixed cost — stack scan over every Kestrel thread — that survivors don't explain:
    // the 2026-07-07 fortunes run burned 55% of wall STW marking <1 MB per collection.
    // A young collection that is both FREQUENT (interval below _boostGrowIntervalTicks)
    // and FUTILE (survivors under 2% of the budget it closed out) was pure fixed cost, so
    // the budget doubles; real survival (>8%) or a sparse cadence shrinks it back. The
    // boost rides on top of ComputeBudget for young AND full paths — budget and the trim
    // demand (2×budget) must move together or fulls flap the retention target and
    // manufacture starved collections (the g256 queries run: 113 of 114 fulls starved).
    // GCPerfSim shapes never engage it: their young survivors are far past 8%.
    private long _youngBudgetBoost;
    private long _lastYoungSurvivors;
    private long _lastYoungGcTimestamp;
    private const long BoostCapBytes = 448L * 1024 * 1024;

    // Parallel collection workers (M5), null = serial. The threads belong to the GC dll's
    // own runtime and never call into the EE. Each participant gets its own mark stack
    // and collectible-deferral list for the parallel card scan.
    private GcWorkerPool? _workerPool;
    private GcRegionZeroer? _regionZeroer;
    private MarkStack[]? _cardScanStacks;
    private List<nint>[]? _cardScanDeferred;
    private MarkShareQueue? _markShareQueue;

    // Set while a buffered (parallel) full mark enumerates roots: ScanRoots accumulates
    // instead of draining inline. Only touched on the GC thread during suspension.
    private bool _bufferMarkRoots;

    // Set only during the remark's root re-scan (SPEC-M6 §9): already-marked roots are
    // dropped at buffer time instead of paying the drain round-trip to discover them.
    private bool _skipMarkedRootBuffering;

    private GCHandle _handle;
    private readonly MarkStack _markStack = new();

    // The process has exactly one GCHeap (standalone GC contract). The per-root EE
    // callbacks (ScanRootsCallback fires once per reported stack slot — thousands per
    // young pause) resolve the instance through this static instead of paying
    // GCHandle.FromIntPtr(...).Target per invocation.
    private static GCHeap s_instance = null!;

    private readonly NativeAllocator _nativeAllocator;

    public GCHeap(IGCToCLRInvoker gcToClr)
    {
        s_instance = this;
        _handle = GCHandle.Alloc(this);
        _gcToClr = gcToClr;
        _gcLock = new GcAwareLock(gcToClr);
        _shardLocks = [new GcAwareLock(gcToClr)];
        _reservoirLock = new GcAwareLock(gcToClr);
        _poolLock = new GcAwareLock(gcToClr);
        _gcHandleManager = new GCHandleManager();
        _nativeAllocator = new(Region.HeapReserveSize);
        _regionAllocator = new RegionAllocator(_nativeAllocator);
        _regionAllocator.SetLocks(_shardLocks, _reservoirLock, _poolLock);

        _nativeObject = IGCHeap.Wrap(this);
        _freeObjectMethodTable = (MethodTable*)gcToClr.GetFreeObjectMethodTable();
        _regionAllocator.SetFreeObjectMethodTable(_freeObjectMethodTable);
        Write($"Free Object Method Table: {(nint)_freeObjectMethodTable:x2}");

        InitializeManagedApi();
    }

    public IntPtr IGCHeapObject => _nativeObject;
    public IntPtr IGCHandleManagerObject => _gcHandleManager.IGCHandleManagerObject;

    public HResult Initialize()
    {
        Write("Initialize GCHeap");

        GcStats.Initialize();

        if (DacManager.TryLoad(out var dacManager))
        {
            _dacManager = dacManager;
        }

        // Honor DOTNET_GCHeapHardLimit as a cap on committed region bytes (SPEC-M2 §9)
        fixed (byte* privateKey = "GCHeapHardLimit"u8)
        fixed (byte* publicKey = "System.GC.HeapHardLimit"u8)
        {
            if (_gcToClr.GetIntConfigValue(privateKey, publicKey, out var hardLimit) && hardLimit > 0)
            {
                Write($"Heap hard limit: {hardLimit}");
                _regionAllocator.SetHardLimit(hardLimit);
            }
        }

        // Honor DOTNET_GCgen0size as the young-budget latency knob (see Collect)
        fixed (byte* privateKey = "GCgen0size"u8)
        fixed (byte* publicKey = "System.GC.Gen0Size"u8)
        {
            if (_gcToClr.GetIntConfigValue(privateKey, publicKey, out var gen0Size) && gen0Size > 0)
            {
                Write($"Young budget cap: {gen0Size}");
                _youngBudgetCap = gen0Size;
            }
        }

        // DOTNET_GCFullRatio: starvation-trigger gate as a percent of the live estimate
        // (memory/throughput exchange knob; config ints parse as hex — 250 = 0xFA)
        fixed (byte* privateKey = "GCFullRatio"u8)
        fixed (byte* publicKey = "System.GC.FullRatio"u8)
        {
            if (_gcToClr.GetIntConfigValue(privateKey, publicKey, out var ratio) && ratio > 0)
            {
                Write($"Full-trigger ratio: {ratio}%");
                _fullRatioPercent = ratio;
            }
        }

        // Parallel collection phases (M5): half the machine by default, DOTNET_GCHeapCount
        // overrides (the standard knob for GC parallelism)
        var participants = Math.Min(8, Environment.ProcessorCount / 2);

        fixed (byte* privateKey = "GCHeapCount"u8)
        fixed (byte* publicKey = "System.GC.HeapCount"u8)
        {
            if (_gcToClr.GetIntConfigValue(privateKey, publicKey, out var heapCount) && heapCount > 0)
            {
                participants = (int)Math.Min(heapCount, Environment.ProcessorCount);
            }
        }

        // Concurrent full cycles (SPEC-M6 v2) honor the stock knob — default-on, since
        // the EE reports gcConcurrent's default as true, matching stock BGC. Flipped
        // per spec §6.1 after the stage-2/3 exits held (2026-07-06 histograms: pause A
        // 1–4 ms and worst-case pause cut on all four scenarios, young pauses and
        // throughput unchanged).
        fixed (byte* privateKey = "gcConcurrent"u8)
        fixed (byte* publicKey = "System.GC.Concurrent"u8)
        {
            if (_gcToClr.GetBooleanConfigValue(privateKey, publicKey, out var concurrent))
            {
                _concurrentCycles = concurrent;
            }
        }

        // The staging knob survives as an explicit override (either direction) for A/B runs
        fixed (byte* privateKey = "GCConcurrentCycles"u8)
        fixed (byte* publicKey = "System.GC.ConcurrentCycles"u8)
        {
            if (_gcToClr.GetBooleanConfigValue(privateKey, publicKey, out var concurrentCycles))
            {
                _concurrentCycles = concurrentCycles;
            }
        }

        // Concurrent card pre-drain (SPEC-M6 §9): default-off — measured a net loss once
        // the drain-termination quantum was fixed (see GCHeap.Concurrent.cs); the knob
        // stays for A/B runs
        fixed (byte* privateKey = "GCCardPreDrain"u8)
        fixed (byte* publicKey = "System.GC.CardPreDrain"u8)
        {
            if (_gcToClr.GetBooleanConfigValue(privateKey, publicKey, out var preDrain))
            {
                _cardPreDrain = preDrain;
            }
        }

        // Per-thread window stash (M7): default-on; =0 restores one-window-per-lock
        // handouts for A/B runs and bisects
        fixed (byte* privateKey = "GCWindowStash"u8)
        fixed (byte* publicKey = "System.GC.WindowStash"u8)
        {
            if (_gcToClr.GetBooleanConfigValue(privateKey, publicKey, out var windowStash))
            {
                _windowStashEnabled = windowStash;
            }
        }

        // Non-temporal-store zeroing (M7 census): default-on; =0 restores Span.Clear
        // everywhere for A/B runs and bisects
        fixed (byte* privateKey = "GCNtZero"u8)
        fixed (byte* publicKey = "System.GC.NtZero"u8)
        {
            if (_gcToClr.GetBooleanConfigValue(privateKey, publicKey, out var ntZero))
            {
                Zeroing.Enabled = ntZero;
            }
        }

        // Concurrent full-cycle sweep (M6.5): default-on since stage 2 (mutator
        // sweep-assist); =0 forces the in-pause sweep for A/B runs
        fixed (byte* privateKey = "GCConcurrentSweep"u8)
        fixed (byte* publicKey = "System.GC.ConcurrentSweep"u8)
        {
            if (_gcToClr.GetBooleanConfigValue(privateKey, publicKey, out var concurrentSweep))
            {
                _concurrentSweep = concurrentSweep;
            }
        }

        // Sharded allocation supply (M7): sized to the machine, not the GC worker pool —
        // the contenders are allocating app threads. DOTNET_GCAllocShards overrides
        // (hex, like every config int); =1 restores a single supply lock for A/B runs.
        var shardCount = Math.Min(Environment.ProcessorCount, 16);

        fixed (byte* privateKey = "GCAllocShards"u8)
        fixed (byte* publicKey = "System.GC.AllocShards"u8)
        {
            if (_gcToClr.GetIntConfigValue(privateKey, publicKey, out var shards) && shards > 0)
            {
                Write($"Alloc shards: {shards}");
                shardCount = (int)Math.Min(shards, 64);
            }
        }

        if (shardCount != _shardLocks.Length)
        {
            // Nothing has allocated yet (Initialize precedes managed code), so the
            // ctor's single-shard supply can be replaced wholesale
            _shardLocks = new GcAwareLock[shardCount];

            for (int i = 0; i < shardCount; i++)
            {
                _shardLocks[i] = new GcAwareLock(_gcToClr);
            }

            _regionAllocator.SetLocks(_shardLocks, _reservoirLock, _poolLock);
        }

        if (participants > 1)
        {
            _workerPool = new GcWorkerPool(participants - 1);
            _markShareQueue = new MarkShareQueue();
            _cardScanStacks = new MarkStack[participants];
            _cardScanDeferred = new List<nint>[participants];

            for (int i = 0; i < participants; i++)
            {
                _cardScanStacks[i] = new MarkStack();
                _cardScanDeferred[i] = [];
            }
        }

        if (Environment.ProcessorCount > 1)
        {
            // Background pre-zeroing (M7): an idle core zeroes recycled regions between
            // collections so carves hand out clean memory instead of paying the memset
            // on the allocating thread
            _regionZeroer = new GcRegionZeroer(_regionAllocator, _reservoirLock, _poolLock);
        }

        // The Initialize contract wants real heap bounds and a non-null card table — debug
        // runtimes assert on them (missing-features 7.1), and the EE's bulk-copy helper
        // (InlinedSetCardsAfterBulkCopyHelper, gchelpers.inl) dirties BOTH tables
        // unconditionally for in-bounds destinations — so both must be writable for committed
        // heap ranges. Cards (1 byte / 2 KB → 1 GB worst case) are reserved in full and
        // committed lazily alongside the region frontier; card bundles (1 byte / 2 MB → 1 MB
        // total) are committed eagerly. The biased pointers make table[addr >> shift] land
        // inside the storage for in-heap addresses.
        //
        // The ephemeral range is the whole heap (SPEC-M4): the stock barrier dirties the
        // destination's card whenever the stored reference falls inside it, which turns the
        // card table into the old→young remembered set for sticky young collections without
        // touching a line of barrier code. Refs to frozen segments above the heap dirty
        // cards too (the PreGrow barrier has no upper-bounds check) — spurious but harmless.
        var heapLow = _nativeAllocator.LowestAddress;
        var heapHigh = _nativeAllocator.HighestAddress;
        var cardTableStorage = NativeAllocator.OsReserve((heapHigh - heapLow) >> 11);
        _regionAllocator.SetCardTable(cardTableStorage);

        var bundleTableStorage = NativeAllocator.OsReserve((heapHigh - heapLow) >> 21);

        if (cardTableStorage == 0 || bundleTableStorage == 0
            || !NativeAllocator.OsCommit(bundleTableStorage, (heapHigh - heapLow) >> 21))
        {
            return HResult.E_OUTOFMEMORY;
        }

        var parameters = new WriteBarrierParameters
        {
            operation = WriteBarrierOp.Initialize,
            is_runtime_suspended = true,
            card_table = (uint*)(cardTableStorage - (heapLow >> 11)),
            card_bundle_table = (uint*)(bundleTableStorage - (heapLow >> 21)),
            lowest_address = heapLow,
            highest_address = heapHigh,
            ephemeral_low = heapLow,
            ephemeral_high = heapHigh
        };

        StompWriteBarrier(parameters);

        return HResult.S_OK;
    }

    public void Shutdown() => Write("Shutdown");

    public HResult GarbageCollect(int generation, bool low_memory_p, int mode)
    {
        Write($"GarbageCollect({generation}, {low_memory_p}, {mode})");

        // Explicit collections always collect (the caller is entitled to a collection that
        // starts after its call), so the snapshot is ignored via force. They are also always
        // full: callers of GC.Collect expect dead old objects to be reclaimed and finalized,
        // whatever generation they pass (we report GetMaxGeneration = 0).
        Collect(_gcCount, force: true, requireFull: true);

        return HResult.S_OK;
    }

    /// <summary>
    /// The single entry point for collections (SPEC-M2 §8.2). Safe to call from cooperative
    /// mode (allocation-triggered GCs): the preemptive switch happens before SuspendEE.
    /// When not forced, the collection is skipped if another thread completed one since the
    /// caller read <paramref name="gcCountSnapshot"/> — that is what stops racing allocators
    /// from running back-to-back collections.
    ///
    /// Sticky-generation policy (SPEC-M4): budget-triggered collections run young — same
    /// epoch, so objects marked by any earlier collection stay live for free; only young
    /// objects need discovering (roots + dirty cards) and only young regions are swept.
    /// Dead old objects float until the next full collection, so a full one runs once the
    /// bytes promoted since the last full match the live set it measured (heap ≈ doubled).
    /// </summary>
    private void Collect(uint gcCountSnapshot, bool force = false, bool requireFull = false)
    {
        // Allocate-through (SPEC-M6 v2 §6.3): while a full cycle is in flight its
        // trigger thread holds _gcLock; a budget-triggered request could only queue
        // behind it and then skip via the count snapshot — return instead so allocation
        // proceeds (the overshoot is bounded by the window length). Forced requests
        // still queue: their callers are entitled to a collection after their call.
        if (!force && _fullCycleInFlight)
        {
            return;
        }

        // Switch before acquiring so the lock's own restore leaves us preemptive: calling
        // SuspendEE from cooperative mode would self-deadlock
        var wasCooperative = _gcToClr.EnablePreemptiveGC();

        _gcLock.Acquire();

        if (force || Volatile.Read(ref _gcCount) == gcCountSnapshot)
        {
            // Young until (a) promotion has ~doubled the live estimate — floating old
            // garbage is invisible to young collections — or (b) the last collection left
            // the allocator starved (see _fullForStarvation): the next runway would grow
            // the committed heap when a full sweep could restock it from strands instead.
            var young = !requireFull
                && !_regionAllocator.UnderMemoryPressure
                && _promotedSinceFull < Math.Max(Region.MinGCBudget, _liveAtLastFull)
                && !_fullForStarvation;

            if (GcStats.Enabled)
            {
                GcStats.FullReason = young ? ""
                    : requireFull ? "forced"
                    : _regionAllocator.UnderMemoryPressure ? "pressure"
                    : _promotedSinceFull >= Math.Max(Region.MinGCBudget, _liveAtLastFull) ? "promoted"
                    : "starved";
            }

            // EE notifications get condemned = 0 for young collections (like a stock gen0:
            // skips the full-only EE work) and 2 for full ones, so JIT code-heap cleanup and
            // ComWrappers reference tracking engage (missing-features 2.1)
            var condemned = young ? 0 : 2;

            if (!young && _concurrentCycles && _workerPool is not null)
            {
                // SPEC-M6 v2 §5: the triggering thread orchestrates the two-pause cycle
                // itself — a normal EE thread in preemptive mode is the standard
                // GC-induction caller of SuspendEE, so no coordinator thread is needed
                CollectFullCycle();
            }
            else
            {
                CollectInline(young, condemned);
            }
        }

        _gcLock.Release();

        if (wasCooperative)
        {
            _gcToClr.DisablePreemptiveGC();
        }
    }

    /// <summary>The classic single-suspension collection: every phase under one STW.</summary>
    private void CollectInline(bool young, int condemned)
    {
        {
            GcStats.BeginCollection();
            var tStart = GcStats.Timestamp();

            // Not GcStats.Timestamp(): the adaptive boost's cheap-pause gate must work
            // with stats disabled (Timestamp() returns 0 then)
            var pauseStart = Stopwatch.GetTimestamp();

            _gcToClr.SuspendEE(SUSPEND_REASON.SUSPEND_FOR_GC);

            // The background zeroer is not an EE thread, so SuspendEE does not park it:
            // holding its gate for the whole suspension is what lets the sweep push to
            // the pool lock-free without racing a checkout (M7). The pool trim runs
            // after RestartEE under the allocation lock (M6 stage 3).
            _regionAllocator.EnterGateForCollection();

            var tSuspended = GcStats.Timestamp();

            NotifyGcStartWork(condemned, 2);

            if (!young)
            {
                // Full marks rebuild liveness from scratch: clear the sticky mark bitmap
                // (SPEC-M6 §3 — this replaces the epoch advance) and the LiveBytes that
                // young collections credited without any sweep ever consuming
                _regionAllocator.ClearMarks();
                _regionAllocator.ResetLiveBytes();
            }

            FixAllocContexts();

            var tFixed = GcStats.Timestamp();

            Write("Mark phase");
            MarkPhase(young);

            var tMarked = GcStats.Timestamp();

            Write("Sweep phase");
            SweepAndAccount(young);

            var tSwept = GcStats.Timestamp();

            ApplyBudget(young, pauseStart);

            var gcNumber = _gcCount;
            Interlocked.Increment(ref _gcCount);

            NotifyGcDone(condemned);

            _regionAllocator.ExitGateForCollection();

            _gcToClr.RestartEE(finishedGC: true);

            var tEnd = GcStats.Timestamp();

            _gcToClr.EnableFinalization(GetNumberOfFinalizable() > 0);

            TrimOutsidePause(young);

            if (GcStats.Enabled)
            {
                GcStats.RecordCollection(gcNumber, young ? "young" : "full",
                    tStart, tSuspended, tFixed, tMarked, tSwept, tEnd,
                    Volatile.Read(ref GcStats.ZeroBytes), Volatile.Read(ref GcStats.ZeroTicks),
                    _lastLiveBytes, _regionAllocator.CommittedRegionBytes,
                    _regionAllocator.TakeCensus(), _budget);
            }

            // The sweep just refilled the pool with dirty regions (and the trim just
            // shrank them to the retained slack, so nothing zeroed here gets decommitted)
            _regionZeroer?.Kick();
        }
    }

    public void SetWaitForGCEvent() => _gcEvent.Set();

    public void ResetWaitForGCEvent() => _gcEvent.Reset();

    public uint WaitUntilGCComplete(bool considerGCStart = false)
    {
        _gcEvent.Wait();
        return 0;
    }

    public int GetLOHCompactionMode() => _lohCompactionMode;

    public void SetLOHCompactionMode(int newLOHCompactionMode)
    {
        // No LOH, so this is pure bookkeeping: the property must round-trip (6.1)
        _lohCompactionMode = newLOHCompactionMode;
    }

    public void FixAllocContext(gc_alloc_context* acontext, void* arg, void* heap)
    {
        FixAllocContext(ref Unsafe.AsRef<gc_alloc_context>(acontext));
    }

    public bool IsThreadUsingAllocationContextHeap(gc_alloc_context* acontext, int thread_number)
    {
        return true;
    }

    public GCObject* Alloc(ref gc_alloc_context acontext, nint size, GC_ALLOC_FLAGS flags)
    {
        var obj = AllocWithRetry(ref acontext, size);

        if (obj != null && flags.HasFlag(GC_ALLOC_FLAGS.GC_ALLOC_FINALIZE))
        {
            if (!RegisterForFinalization(0, obj))
            {
                // Couldn't grow the finalization queue: surface as OOM (SPEC-M2 §9.1).
                // The object has no MethodTable yet (the EE writes it after we return), so
                // plug its extent from the requested size to keep the heap walkable.
                AllocateFreeObject((nint)obj, (uint)(Align(size) - SizeOfObject));
                return null;
            }
        }

        return obj;
    }

    /// <summary>
    /// The collect-and-retry protocol (SPEC-M2 §8.3 + §9.1): one budget-triggered collection
    /// per call at most, then one forced collection between the first genuine allocation
    /// failure and surrendering with null (the EE turns null into OutOfMemoryException).
    /// </summary>
    private GCObject* AllocWithRetry(ref gc_alloc_context acontext, nint size)
    {
        var budgetChecked = false;
        var collectedForOom = false;

        while (true)
        {
            var snapshot = Volatile.Read(ref _gcCount);

            if (!budgetChecked)
            {
                budgetChecked = true;

                // Reads outside the alloc lock are approximate; the trigger doesn't care
                if (_allocatedSinceGC > _budget)
                {
                    Collect(snapshot);
                    continue;
                }
            }

            // Three tiers per SPEC-M2 §4; the direct tiers leave the caller's context
            // alone (it may still serve small allocations from its remaining window)
            var obj = size <= Region.BumpMaxSize
                ? AllocFromWindow(ref acontext, size)
                : size + IntPtr.Size <= Region.SizeClassMaxSize
                    ? AllocBlock(ref acontext, size)
                    : AllocSpan(ref acontext, size);

            if (obj != null)
            {
                return obj;
            }

            if (collectedForOom)
            {
                return null;
            }

            // The last stand before OOM must be a full collection: a young one cannot
            // reclaim dead old objects, and budget-triggered collections are usually young
            collectedForOom = true;
            Collect(snapshot, force: true, requireFull: true);
        }
    }

    // --- Per-thread window stash (M7): the census put the global alloc lock at 82% of
    // the remaining handout cost (wait 384 + carve 156 of win 659 ms/run on soh, ~1M
    // acquisitions at ~20 KB avg), so carves batch extra windows out under the same
    // acquisition and later handouts pop them with no lock at all. A stashed window is
    // indistinguishable from an unconsumed alloc-context window: private to its thread,
    // unmarked, reclaimed by the next sweep like any dead extent. The epoch is what
    // keeps that sound — every collection rebuilds the supply lists under STW and
    // relinks stashed extents as holes, so stashes from before the bump are forfeit
    // (using one would double-hand memory the sweep already re-published). The stash
    // rides in the context's GC-reserved slot, like the stock GC's heap affinity:
    // per-EE-thread state with no thread-local machinery in the handout path. ---
    private const int WindowStashCapacity = 3;

    private struct WindowStash
    {
        public int Epoch;
        public int Count;
        public uint ZeroMask;
        public int Shard;
        public fixed long Window[WindowStashCapacity];
        public fixed long Length[WindowStashCapacity];
    }

    // Bumped under STW by every path that rebuilds the allocation supply
    private static int _stashEpoch;

    // Round-robins new threads across supply shards (also rotates span-path assists)
    private static int _nextShard;

    private bool _windowStashEnabled = true;

    /// <summary>The per-thread alloc state riding <c>gc_reserved_1</c>: shard affinity
    /// plus the window stash. Allocated on first use, freed never — contexts die with
    /// their threads, and 72 stale bytes are cheaper than guessing at a retirement
    /// hook. Lives in native memory because [ThreadStatic] dies on EE threads.</summary>
    private WindowStash* GetThreadAllocState(ref gc_alloc_context acontext)
    {
        var stash = (WindowStash*)acontext.gc_reserved_1;

        if (stash == null)
        {
            stash = (WindowStash*)NativeMemory.AllocZeroed((nuint)sizeof(WindowStash));
            stash->Shard = (int)((uint)Interlocked.Increment(ref _nextShard) % (uint)_shardLocks.Length);
            acontext.gc_reserved_1 = stash;
        }

        return stash;
    }

    private GCObject* AllocFromWindow(ref gc_alloc_context acontext, nint size)
    {
        var tStart = GcStats.Timestamp();

        // Plug the context's remainder to keep the heap walkable before replacing it
        FixAllocContext(ref acontext);

        nint window, length;
        bool needsZero;
        long tBeforeLock = 0, tLocked = 0;

        var stash = GetThreadAllocState(ref acontext);

        if (stash->Epoch != Volatile.Read(ref _stashEpoch))
        {
            // A collection ran since the refill: the sweep owns those extents now
            stash->Count = 0;
        }

        if (stash->Count > 0 && (nint)stash->Length[stash->Count - 1] >= Align(size) + 3 * IntPtr.Size)
        {
            var i = --stash->Count;
            window = (nint)stash->Window[i];
            length = (nint)stash->Length[i];
            needsZero = (stash->ZeroMask & (1u << i)) != 0;

            if (!needsZero)
            {
                // Pre-zeroed window: only the stash plug's header slots are stale
                // (same cleanup TryCarveFromHoles does for pre-zeroed holes)
                new Span<byte>((void*)window, 4 * IntPtr.Size).Clear();
            }
        }
        else
        {
            var shard = stash->Shard;
            var shardLock = _shardLocks[shard];

            tBeforeLock = GcStats.Timestamp();
            shardLock.Acquire();
            tLocked = GcStats.Timestamp();

            try
            {
                if (!_regionAllocator.TryGetWindow(shard, size, out window, out length, out needsZero))
                {
                    return null;
                }

                var carvedBytes = (long)length;

                // Refill under the same acquisition. Epoch is re-read after Acquire: a
                // suspension can complete inside a contended Acquire, and the stash must
                // belong to the supply lists this refill actually carves from. Budget
                // accounting happens here (the trigger reads are approximate anyway);
                // alloc_bytes accrues per window at pop, where the EE's thread owns it.
                stash->Epoch = Volatile.Read(ref _stashEpoch);

                while (_windowStashEnabled && stash->Count < WindowStashCapacity
                    && _regionAllocator.TryGetStashWindow(shard, size, out var extra, out var extraLength, out var extraZero))
                {
                    var i = stash->Count++;
                    stash->Window[i] = extra;
                    stash->Length[i] = extraLength;

                    if (extraZero)
                    {
                        stash->ZeroMask |= 1u << i;
                    }
                    else
                    {
                        stash->ZeroMask &= ~(1u << i);
                    }

                    // Plug the stashed extent: interior-pointer resolution walks bump
                    // regions object-by-object, and unlike the primary window a stashed
                    // one is not an alloc context FixAllocContext will plug at the next
                    // suspension. (Found the hard way: GcStress's pinned interior roots
                    // walked into an unplugged stash extent and died on a garbage
                    // MethodTable at pause A.)
                    AllocateFreeObject(extra + IntPtr.Size, (uint)(extraLength - 3 * IntPtr.Size));

                    carvedBytes += extraLength;
                }

                Interlocked.Add(ref _allocatedSinceGC, carvedBytes);
                Interlocked.Add(ref _totalAllocatedBytes, carvedBytes);
            }
            finally
            {
                shardLock.Release();
            }
        }

        // SPEC-M2 §3: first object ref at window + 8; the -16 pairs with the plug
        // formula in FixAllocContext to keep the region walkable end-to-end
        acontext.alloc_ptr = Align(window + IntPtr.Size + size);
        acontext.alloc_limit = window + length - 2 * IntPtr.Size;
        acontext.alloc_bytes += length;

        var tCarved = GcStats.Timestamp();

        if (needsZero)
        {
            // Zero-at-carve (M4), outside the lock: the window is private to this thread,
            // no GC can run while it is in cooperative mode, and concurrent handouts zero
            // in parallel instead of convoying on the allocation lock
            RegionAllocator.ZeroWindow(window, length);
        }

        if (GcStats.Enabled)
        {
            // The full handout cost an app thread feels: plugging, lock wait, carving,
            // zeroing. Stash pops report zero wait/carve — that is the point of them.
            var tEnd = GcStats.Timestamp();
            Interlocked.Increment(ref GcStats.WindowCount);
            Interlocked.Add(ref GcStats.WindowTicks, tEnd - tStart);
            Interlocked.Add(ref GcStats.WindowWaitTicks, tLocked - tBeforeLock);
            Interlocked.Add(ref GcStats.WindowCarveTicks, tLocked == 0 ? 0 : tCarved - tLocked);
            Interlocked.Add(ref GcStats.WindowZeroTicks, tEnd - tCarved);
        }

        return (GCObject*)(window + IntPtr.Size);
    }

    private GCObject* AllocBlock(ref gc_alloc_context acontext, nint size)
    {
        var sizeClass = Region.SelectClass(size);
        nint block;
        bool needsZero;

        var shard = GetThreadAllocState(ref acontext)->Shard;
        var shardLock = _shardLocks[shard];

        shardLock.Acquire();

        try
        {
            if (!_regionAllocator.TryAllocBlock(shard, sizeClass, out block, out needsZero))
            {
                return null;
            }

            if (GcStats.Enabled)
            {
                Interlocked.Increment(ref GcStats.BlockCount);
            }

            var classSize = Region.ClassSizes[sizeClass];
            acontext.alloc_bytes_uoh += classSize;
            Interlocked.Add(ref _allocatedSinceGC, classSize);
            Interlocked.Add(ref _totalAllocatedBytes, classSize);
        }
        finally
        {
            shardLock.Release();
        }

        if (needsZero)
        {
            // Zero-at-carve (M4), outside the lock: only the object's extent — the block's
            // tail beyond it is never read (every reader gates on the allocated bitmap and
            // walks per-block, not contiguously)
            RegionAllocator.ZeroWindow(block, IntPtr.Size + Align(size));
        }

        return (GCObject*)(block + IntPtr.Size);
    }

    private GCObject* AllocSpan(ref gc_alloc_context acontext, nint size)
    {
        // The caller's context is left untouched: it may still serve small allocations
        var regionCount = Region.SpanRegionCount(size);
        nint spanBase;

        // The span tier runs on the pool lock inside TryAllocSpan (M7 sharded supply).
        // Its old inline sweep assists moved out here: an assist splices supply into a
        // shard, which must never be waited on while holding the pool lock, so a dry
        // pool retries through standalone assists and reaches the frontier only once
        // the plan is exhausted — the same ordering the single-lock path enforced.
        while (!_regionAllocator.TryAllocSpan(regionCount, out spanBase, allowFrontier: false))
        {
            if (!AssistSweepStandalone())
            {
                if (!_regionAllocator.TryAllocSpan(regionCount, out spanBase, allowFrontier: true))
                {
                    return null;
                }

                break;
            }
        }

        if (GcStats.Enabled)
        {
            Interlocked.Increment(ref GcStats.SpanCount);
        }

        var allocated = (long)regionCount << Region.Shift;
        acontext.alloc_bytes_uoh += allocated;
        Interlocked.Add(ref _allocatedSinceGC, allocated);
        Interlocked.Add(ref _totalAllocatedBytes, allocated);

        // Recycled members hold stale contents; zeroing happens out here so concurrent
        // span carves clean in parallel instead of serializing the supply locks
        _regionAllocator.ZeroSpanCarve(spanBase, size);

        return (GCObject*)(spanBase + IntPtr.Size);
    }

    /// <summary>One sweep-assist chunk on behalf of the span path, under a rotating
    /// shard lock and nothing else: the chunk's supply splices into that shard, freed
    /// regions reach the pool for the caller's retry.</summary>
    private bool AssistSweepStandalone()
    {
        var shard = (int)((uint)Interlocked.Increment(ref _nextShard) % (uint)_shardLocks.Length);
        var shardLock = _shardLocks[shard];

        shardLock.Acquire();

        try
        {
            return _regionAllocator.TrySweepAssist(shard);
        }
        finally
        {
            shardLock.Release();
        }
    }

    /// <summary>Sweep + the sticky-generation live accounting + the card reset, shared
    /// by the inline STW collection and pause B of a concurrent cycle (SPEC-M6 v2).
    /// Runs under STW with the zeroer gate held.</summary>
    private void SweepAndAccount(bool young)
    {
        // The sweep relinks every stashed window's extent as a hole: stashes carved
        // before this point must never be consumed again (M7 window stash)
        Interlocked.Increment(ref _stashEpoch);

        var swept = _regionAllocator.Sweep(youngOnly: young, _workerPool);

        if (young)
        {
            _promotedSinceFull += swept;
            _lastLiveBytes = _liveAtLastFull + _promotedSinceFull;
            _lastYoungSurvivors = swept;
        }
        else
        {
            _liveAtLastFull = swept;
            _promotedSinceFull = 0;
            _lastLiveBytes = swept;
        }

        // Every traced old→young edge is now marked or dead; cards restart from clean
        _regionAllocator.ClearCards();
    }

    /// <summary>Post-sweep budget/trim/trigger policy (SPEC-M2 §8.3), shared like
    /// <see cref="SweepAndAccount"/>. <paramref name="pauseStart"/> (a Stopwatch
    /// timestamp taken before SuspendEE, 0 when unavailable) feeds the adaptive boost's
    /// cheap-pause gate; the concurrent full path passes 0 — fulls never adjust the
    /// boost, they only inherit it.</summary>
    private void ApplyBudget(bool young, long pauseStart = 0)
    {
        _allocatedSinceGC = 0;

        if (young && _youngBudgetCap == 0)
        {
            // Frequent-futile-cheap detector (see _youngBudgetBoost). The interval is
            // young-start-to-young-start wall time; _budget still holds the budget this
            // collection just closed out, so survivors compare against the right base.
            // All three grow gates are load-bearing: GCPerfSim's soh survival (~2% of
            // allocated) sits exactly on a 2% futility threshold and boosted its WS 2 GB
            // past the h8 anchor before the <1% + cheap-pause gates excluded it — its
            // young pauses mark real survivors (25–73 ms), web's are fixed cost (2–5 ms).
            var now = Stopwatch.GetTimestamp();
            var interval = now - _lastYoungGcTimestamp;
            _lastYoungGcTimestamp = now;

            var growCadence = interval < Stopwatch.Frequency / 4;   // < 250 ms
            var sparseCadence = interval > Stopwatch.Frequency;     // > 1 s
            var cheapPause = pauseStart != 0
                && now - pauseStart < Stopwatch.Frequency / 100;    // < 10 ms so far

            if (growCadence && cheapPause && _lastYoungSurvivors < _budget / 100)
            {
                _youngBudgetBoost = Math.Min(BoostCapBytes,
                    Math.Max(Region.MinGCBudget, _youngBudgetBoost * 2));
            }
            else if (sparseCadence || _lastYoungSurvivors > _budget / 50)
            {
                _youngBudgetBoost /= 2;

                if (_youngBudgetBoost < Region.MinGCBudget)
                {
                    _youngBudgetBoost = 0;
                }
            }
        }

        // SPEC-M2 §8.3: the heap converges to ≈ 2× live. A configured gen0 size overrides
        // the young budget instead: young pauses scale with the nursery while total work
        // per allocated byte does not, so DOTNET_GCgen0size trades throughput
        // (~+12% wall on soh at live/4) for young pauses in proportion (73 → 25 ms p50)
        _budget = Region.ComputeBudget(_lastLiveBytes) + _youngBudgetBoost;

        if (young && _youngBudgetCap > 0)
        {
            _budget = Math.Max(Region.MinGCBudget, _youngBudgetCap);
        }
    }

    /// <summary>
    /// The decommit half of budget application, run on the triggering thread after
    /// RestartEE but still under <c>_gcLock</c> (M6 stage 3: the in-pause trim measured
    /// ~46 ms of pause B on the ASP.NET soak — 70% of the pause — and free regions can
    /// be decommitted while the world runs). Batches hold the pool lock so carves
    /// and the zeroer interleave; no Collect can start (we hold <c>_gcLock</c>), so the
    /// zeroer's lock-free suspension escape never engages against us.
    /// </summary>
    private void TrimOutsidePause(bool young)
    {
        var tStart = GcStats.Timestamp();

        // Two budgets of working headroom: one for the runway that will trigger the
        // next collection, one for what mutators allocate through a concurrent cycle's
        // window (the v4 census measured ~a window of fresh commits per full — the
        // entire remaining ratchet — when fulls fired with holes already exhausted).
        var demand = 2 * _budget;

        // Retain the same headroom in committed pool slack. The retention target and
        // the starvation demand must be one number: retaining less makes the trim
        // itself manufacture starvation on wholesale-recycling workloads (the first
        // exchange-rate soak retained one budget against a two-budget demand and ran
        // 45% of its collections as starved fulls — pool-fed heaps have few holes, so
        // post-trim free capacity could never reach the demand).
        var cursor = 0;

        // Each batch takes the pool lock internally, so carves and the zeroer interleave
        while (_regionAllocator.TrimPoolBatch(demand, maxDecommits: 8, ref cursor))
        {
        }

        GcStats.TrimTicks = GcStats.Timestamp() - tStart;

        // The starvation trigger reads post-trim state: the pool now holds exactly the
        // retained slack, so free capacity is what the next runway can genuinely use.
        // Written under _gcLock, like every reader.
        var committed = _regionAllocator.CommittedRegionBytes;
        var freeCapacity = _regionAllocator.FreeCapacityBytes;

        if (!young)
        {
            // Post-trim is the honest measure of what this full pass achieved: if it
            // could not restock the working headroom, re-firing would only repeat this
            // collection's work — stand down until the heap grows a budget past here.
            _starvationMuted = freeCapacity < demand;
            _starvationRearm = committed + _budget;
        }
        else if (_starvationMuted && committed >= _starvationRearm)
        {
            _starvationMuted = false;
        }

        _fullForStarvation = !_starvationMuted
            && freeCapacity < demand
            && committed >= _fullRatioPercent * Math.Max(Region.MinGCBudget, _lastLiveBytes) / 100;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static nint Align(nint address) => (address + (IntPtr.Size - 1)) & ~(IntPtr.Size - 1);

    [UnmanagedCallersOnly]
    private static void FixAllocContextCallback(gc_alloc_context* acontext, IntPtr arg)
    {
        s_instance.FixAllocContext(ref Unsafe.AsRef<gc_alloc_context>(acontext));
    }

    private void FixAllocContexts()
    {
        var callback = (delegate* unmanaged<gc_alloc_context*, IntPtr, void>)&FixAllocContextCallback;
        _gcToClr.GcEnumAllocContexts((IntPtr)callback, GCHandle.ToIntPtr(_handle));
    }

    private void FixAllocContext(ref gc_alloc_context acontext)
    {
        if (acontext.alloc_ptr == 0)
        {
            return;
        }

        // Un-account the window remainder the context never consumed (SPEC-M2 §4.1)
        acontext.alloc_bytes -= acontext.alloc_limit - acontext.alloc_ptr;

        AllocateFreeObject(acontext.alloc_ptr, (uint)(acontext.alloc_limit - acontext.alloc_ptr));

        // Invalidate the allocation context so threads get a fresh one
        acontext.alloc_ptr = 0;
        acontext.alloc_limit = 0;
    }

    private void AllocateFreeObject(nint address, uint length)
    {
        var freeObject = (GCObject*)address;
        freeObject->RawMethodTable = _freeObjectMethodTable;
        freeObject->Length = length;
    }

    private IEnumerable<IntPtr> WalkHeapObjects()
    {
        var count = _regionAllocator.CarvedCount;

        for (int i = 0; i < count; i++)
        {
            var (kind, start, end) = GetWalkRange(i);

            switch (kind)
            {
                case RegionKind.Bump:
                    foreach (var obj in WalkHeapObjects(start, end))
                    {
                        yield return obj;
                    }
                    break;

                case RegionKind.SpanStart:
                    yield return start;
                    break;

                case RegionKind.SizeClass:
                {
                    var (bitmap, classSize) = _regionAllocator.GetSizeClassInfo(i);
                    var regionBase = _regionAllocator.RegionBase(i);

                    while (bitmap != 0)
                    {
                        var bit = System.Numerics.BitOperations.TrailingZeroCount(bitmap);
                        bitmap &= bitmap - 1;
                        yield return regionBase + (nint)bit * classSize + IntPtr.Size;
                    }

                    break;
                }
            }
        }
    }

    private (RegionKind kind, nint start, nint end) GetWalkRange(int index)
    {
        var entry = _regionAllocator.GetEntry(index);

        return entry->Kind switch
        {
            // Objects live in [base + 8, Cursor); the tail past Cursor is virgin zero
            RegionKind.Bump => (RegionKind.Bump, _regionAllocator.RegionBase(index) + IntPtr.Size, entry->Cursor),
            // A span holds exactly one object; size-class regions are walked via their bitmap
            RegionKind.SpanStart => (RegionKind.SpanStart, _regionAllocator.RegionBase(index) + IntPtr.Size, 0),
            _ => (entry->Kind, 0, 0),
        };
    }

    private static IEnumerable<IntPtr> WalkHeapObjects(nint objectStart, nint end)
    {
        var ptr = objectStart;

        while (ptr < end)
        {
            yield return ptr;
            ptr = FindNextObject(ptr);
        }

        static unsafe nint FindNextObject(nint current)
        {
            var obj = (GCObject*)current;
            return Align(current + (nint)obj->ComputeSize());
        }
    }

    private void DumpHeap()
    {
        foreach (var ptr in WalkHeapObjects())
        {
            var obj = (GCObject*)ptr;
            bool isFreeObject = obj->MethodTable == _freeObjectMethodTable;

            var name = isFreeObject ? "Free" : _dacManager?.GetObjectName(new(ptr));

            Write($"{ptr:x2} - {name?.PadRight(50)}");
        }
    }
}
