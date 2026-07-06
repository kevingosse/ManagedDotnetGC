using ManagedDotnetGC.Dac;
using NativeObjects;
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
    private readonly GcAwareLock _allocLock;
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

    // The committed>8×live full trigger mutes itself when a full collection proves unable
    // to get committed back under the line (scattered survivors pin regions open — a
    // structural state on a non-moving heap, not reclaimable garbage). Left armed it fires
    // on every subsequent collection: a permanent full-GC storm. It re-arms once committed
    // grows a budget past what that impotent full could reach.
    private bool _committedTriggerMuted;
    private long _committedTriggerRearm;

    // DOTNET_GCgen0size: latency knob capping the young allocation budget, 0 = uncapped
    private long _youngBudgetCap;

    // Parallel collection workers (M5), null = serial. The threads belong to the GC dll's
    // own runtime and never call into the EE. Each participant gets its own mark stack
    // and collectible-deferral list for the parallel card scan.
    private GcWorkerPool? _workerPool;
    private MarkStack[]? _cardScanStacks;
    private List<nint>[]? _cardScanDeferred;
    private MarkShareQueue? _markShareQueue;

    // Set while a buffered (parallel) full mark enumerates roots: ScanRoots accumulates
    // instead of draining inline. Only touched on the GC thread during suspension.
    private bool _bufferMarkRoots;

    private GCHandle _handle;
    private readonly MarkStack _markStack = new();
    private uint _currentEpoch = 1;

    private readonly NativeAllocator _nativeAllocator;

    public GCHeap(IGCToCLRInvoker gcToClr)
    {
        _handle = GCHandle.Alloc(this);
        _gcToClr = gcToClr;
        _gcLock = new GcAwareLock(gcToClr);
        _allocLock = new GcAwareLock(gcToClr);
        _gcHandleManager = new GCHandleManager();
        _nativeAllocator = new(Region.HeapReserveSize);
        _regionAllocator = new RegionAllocator(_nativeAllocator);

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
        // Switch before acquiring so the lock's own restore leaves us preemptive: calling
        // SuspendEE from cooperative mode would self-deadlock
        var wasCooperative = _gcToClr.EnablePreemptiveGC();

        _gcLock.Acquire();

        if (force || Volatile.Read(ref _gcCount) == gcCountSnapshot)
        {
            // Young until (a) promotion has ~doubled the live estimate, or (b) the
            // committed heap outgrew 8× live — floating old garbage and sub-floor holes
            // are invisible to young collections, so only a full pass can shrink them.
            // The multiple is deliberately loose: a non-moving heap cannot pack scattered
            // survivors, so on hostile scatter committed legitimately sits at several
            // times live and a tight bound just degenerates every collection to full.
            // When even a full pass can't get back under the line the trigger mutes
            // (see _committedTriggerMuted) until the heap has actually grown since.
            if (_committedTriggerMuted
                && _regionAllocator.CommittedRegionBytes >= _committedTriggerRearm)
            {
                _committedTriggerMuted = false;
            }

            var young = !requireFull
                && !_regionAllocator.UnderMemoryPressure
                && _promotedSinceFull < Math.Max(Region.MinGCBudget, _liveAtLastFull)
                && (_committedTriggerMuted
                    || _regionAllocator.CommittedRegionBytes
                        < 8 * Math.Max(Region.MinGCBudget, _lastLiveBytes));

            // EE notifications get condemned = 0 for young collections (like a stock gen0:
            // skips the full-only EE work) and 2 for full ones, so JIT code-heap cleanup and
            // ComWrappers reference tracking engage (missing-features 2.1)
            var condemned = young ? 0 : 2;

            GcStats.BeginCollection();
            var tStart = GcStats.Timestamp();

            _gcToClr.SuspendEE(SUSPEND_REASON.SUSPEND_FOR_GC);

            var tSuspended = GcStats.Timestamp();

            NotifyGcStartWork(condemned, 2);

            if (!young)
            {
                AdvanceEpoch();

                // Young collections credit LiveBytes to old regions without any sweep ever
                // consuming it; a full mark must re-accumulate from zero
                _regionAllocator.ResetLiveBytes();
            }

            FixAllocContexts();

            var tFixed = GcStats.Timestamp();

            Write("Mark phase");
            MarkPhase(young);

            var tMarked = GcStats.Timestamp();
            var zeroBytesBefore = GcStats.ZeroBytes;
            var zeroTicksBefore = GcStats.ZeroTicks;

            Write("Sweep phase");
            var swept = _regionAllocator.Sweep(youngOnly: young, _workerPool);

            if (young)
            {
                _promotedSinceFull += swept;
                _lastLiveBytes = _liveAtLastFull + _promotedSinceFull;
            }
            else
            {
                _liveAtLastFull = swept;
                _promotedSinceFull = 0;
                _lastLiveBytes = swept;
            }

            // Every traced old→young edge is now marked or dead; cards restart from clean
            _regionAllocator.ClearCards();

            var tSwept = GcStats.Timestamp();

            // SPEC-M2 §8.3: the heap converges to ≈ 2× live. A configured gen0 size caps
            // the young budget instead: young pauses scale with the nursery while total
            // work per allocated byte does not, so DOTNET_GCgen0size trades throughput
            // (~+12% wall on soh at live/4) for young pauses in proportion (73 → 25 ms p50)
            _allocatedSinceGC = 0;
            _budget = Region.ComputeBudget(_lastLiveBytes);

            if (young && _youngBudgetCap > 0)
            {
                _budget = Math.Min(_budget, Math.Max(Region.MinGCBudget, _youngBudgetCap));
            }

            // Retain a budget's worth of committed pool slack: the next cycle carves
            // exactly that much back out, so trimming lower is pure recommit churn
            _regionAllocator.TrimPool(_budget);

            if (!young)
            {
                // Post-trim is the honest measure of what this full pass achieved: if
                // committed still exceeds the trigger line, re-firing would only repeat
                // this collection's work, so the trigger stands down until the heap grows
                var committed = _regionAllocator.CommittedRegionBytes;
                _committedTriggerMuted = committed >= 8 * Math.Max(Region.MinGCBudget, _lastLiveBytes);
                _committedTriggerRearm = committed + _budget;
            }

            var gcNumber = _gcCount;
            Interlocked.Increment(ref _gcCount);

            NotifyGcDone(condemned);

            _gcToClr.RestartEE(finishedGC: true);

            if (GcStats.Enabled)
            {
                GcStats.RecordCollection(gcNumber, young ? "young" : "full",
                    tStart, tSuspended, tFixed, tMarked, tSwept, GcStats.Timestamp(),
                    GcStats.ZeroBytes - zeroBytesBefore, GcStats.ZeroTicks - zeroTicksBefore,
                    _lastLiveBytes, _regionAllocator.CommittedRegionBytes);
            }

            _gcToClr.EnableFinalization(GetNumberOfFinalizable() > 0);
        }

        _gcLock.Release();

        if (wasCooperative)
        {
            _gcToClr.DisablePreemptiveGC();
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

    private GCObject* AllocFromWindow(ref gc_alloc_context acontext, nint size)
    {
        var tStart = GcStats.Timestamp();

        // Plug the context's remainder to keep the heap walkable before replacing it
        FixAllocContext(ref acontext);

        nint window, length;
        bool needsZero;

        _allocLock.Acquire();

        try
        {
            if (!_regionAllocator.TryGetWindow(size, out window, out length, out needsZero))
            {
                return null;
            }

            // SPEC-M2 §3: first object ref at window + 8; the -16 pairs with the plug
            // formula in FixAllocContext to keep the region walkable end-to-end
            acontext.alloc_ptr = Align(window + IntPtr.Size + size);
            acontext.alloc_limit = window + length - 2 * IntPtr.Size;
            acontext.alloc_bytes += length;

            _allocatedSinceGC += length;
            _totalAllocatedBytes += length;
        }
        finally
        {
            _allocLock.Release();
        }

        if (needsZero)
        {
            // Zero-at-carve (M4), outside the lock: the window is private to this thread,
            // no GC can run while it is in cooperative mode, and concurrent handouts zero
            // in parallel instead of convoying on the allocation lock
            RegionAllocator.ZeroWindow(window, length);
        }

        if (GcStats.Enabled)
        {
            // The full handout cost an app thread feels: plugging, lock wait, carving, zeroing
            Interlocked.Increment(ref GcStats.WindowCount);
            Interlocked.Add(ref GcStats.WindowTicks, GcStats.Timestamp() - tStart);
        }

        return (GCObject*)(window + IntPtr.Size);
    }

    private GCObject* AllocBlock(ref gc_alloc_context acontext, nint size)
    {
        var sizeClass = Region.SelectClass(size);

        _allocLock.Acquire();

        try
        {
            if (!_regionAllocator.TryAllocBlock(sizeClass, out var block))
            {
                return null;
            }

            if (GcStats.Enabled)
            {
                GcStats.BlockCount++;
            }

            var classSize = Region.ClassSizes[sizeClass];
            acontext.alloc_bytes_uoh += classSize;
            _allocatedSinceGC += classSize;
            _totalAllocatedBytes += classSize;

            return (GCObject*)(block + IntPtr.Size);
        }
        finally
        {
            _allocLock.Release();
        }
    }

    private GCObject* AllocSpan(ref gc_alloc_context acontext, nint size)
    {
        // The caller's context is left untouched: it may still serve small allocations
        var regionCount = Region.SpanRegionCount(size);

        _allocLock.Acquire();

        try
        {
            if (!_regionAllocator.TryAllocSpan(regionCount, out var spanBase))
            {
                return null;
            }

            if (GcStats.Enabled)
            {
                GcStats.SpanCount++;
            }

            var allocated = (long)regionCount << Region.Shift;
            acontext.alloc_bytes_uoh += allocated;
            _allocatedSinceGC += allocated;
            _totalAllocatedBytes += allocated;

            return (GCObject*)(spanBase + IntPtr.Size);
        }
        finally
        {
            _allocLock.Release();
        }
    }

    /// <summary>
    /// Starts a new mark epoch (SPEC-M2 §5). Runs under STW. On uint wrap, stale stamps
    /// from 2³² collections ago could alias the new epoch, so all stamps are cleared first.
    /// </summary>
    private void AdvanceEpoch()
    {
        var next = GCObject.NextEpoch(_currentEpoch);

        if (next < _currentEpoch)
        {
            // Wrapped: clear every stale stamp so it cannot alias the restarted sequence
            foreach (var ptr in WalkHeapObjects())
            {
                ((GCObject*)ptr)->Epoch = 0;
            }
        }

        _currentEpoch = next;
        GCObject.CurrentEpoch = next;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static nint Align(nint address) => (address + (IntPtr.Size - 1)) & ~(IntPtr.Size - 1);

    [UnmanagedCallersOnly]
    private static void FixAllocContextCallback(gc_alloc_context* acontext, IntPtr arg)
    {
        var handle = GCHandle.FromIntPtr(arg);
        var gcHeap = (GCHeap)handle.Target!;
        gcHeap.FixAllocContext(ref Unsafe.AsRef<gc_alloc_context>(acontext));
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
