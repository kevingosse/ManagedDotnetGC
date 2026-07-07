using System.Numerics;

namespace ManagedDotnetGC;

/// <summary>
/// The region heap (SPEC-M2 §2-§4): a flat table of 2 MB regions carved from one 2 TB
/// reservation. Thread safety (M7 sharded supply): the carveable supply — recycled hole
/// regions, size-class lists, active bump regions — is split across shards, each guarded
/// by its own lock; the pool and the frontier stay global behind the pool lock (span run
/// scans, the trim and zeroer checkouts need one coherent pool). Lock order: at most one
/// shard lock, then the pool lock; the only cross-shard operation (stealing) TryAcquires
/// its victim and skips on contention. Sweeps mutate everything with no locks, under a
/// suspension with the zeroer gated. The unit tests never call <see cref="SetLocks"/> and
/// drive a single unlocked shard single-threaded.
/// </summary>
internal unsafe class RegionAllocator : IDisposable
{
    private readonly NativeAllocator _memory;
    private readonly RegionEntry* _table;
    private nint _tableCommittedEnd;

    // LIFO stack of free region indices. Committed members hold stale recycled contents
    // (M4 zero-at-carve): every tier zeroes what it hands out, outside the pool lock
    private readonly int* _pool;
    private int _poolCount;

    // Next never-carved region index; the frontier only grows
    private int _frontier;

    // Sharded carveable supply (M7): handouts stopped convoying on one global lock when
    // each shard got its own private slice of the supply behind its own lock. New
    // threads are round-robined across shards by GCHeap; a dry shard pulls a batch from
    // the reservoir, then steals from siblings, before touching the pool — so
    // hole-first stays a whole-heap policy.
    private AllocShard[] _shards;
    private GcAwareLock[]? _shardLocks;
    private GcAwareLock? _poolLock;

    // The supply reservoir (M7 sharded supply): sweeps splice ALL rebuilt supply here,
    // and shards pull private batches on demand. The first cut spliced sweep output
    // round-robin across the shards themselves, and the soak ratcheted +600 MB at
    // +30% handout wait: supply landed uniformly while demand concentrated, so hot
    // shards drained the pool while cold shards hoarded holes. Demand-directed pulls
    // are what make N private lists consume like the old single global list.
    private int _reservoirRecycledHead = -1;
    private readonly int[] _reservoirClassHeads = new int[Region.ClassCount];
    private GcAwareLock? _reservoirLock;

    private sealed class AllocShard
    {
        // Active fresh bump region being carved into windows, -1 if none
        public int ActiveBump = -1;

        // Head of the intrusive list of bump regions with linked holes (via NextRecycled)
        public int RecycledHead = -1;

        // Head of the intrusive list of regions with free blocks, per size class
        public readonly int[] ClassHeads = new int[Region.ClassCount];

        // Assist scratch: only touched while holding this shard's lock
        public readonly List<int> AssistFreed = new();

        public AllocShard() => ClassHeads.AsSpan().Fill(-1);
    }

    // Set once at startup, needed to write plugs when carving holes and sweeping
    private MethodTable* _freeObjectMethodTable;

    /// <summary>Per-hole zeroed-body marker (M8.3), stored in the free plug's padding
    /// word at ref+12 (between the 4-byte Length and the ref+16 link — inside the
    /// 32-byte prefix every zeroed-hole consumer preserves). Value semantics: marker
    /// set ⟹ the hole body beyond the 32-byte prefix is zero. Every plug writer must
    /// initialize this word (the padding is stale memory otherwise); only
    /// <see cref="ZeroCheckedOutHoleBodies"/> sets it, and <see cref="ClosePlug"/>
    /// inherits it across sweeps when the rebuilt extent is byte-identical to the plug
    /// it re-covers — the fix for the sweep invalidating every reopened region's
    /// pre-zeroed holes every young cycle (2026-07-07 web profile: the zeroer re-zeroed
    /// ~4× the allocation volume).</summary>
    internal const uint ZeroedHoleMarker = 0x5EED_ED01;

    /// <summary>DOTNET_GCHoleMarkerInherit=0 disables marker inheritance across sweeps
    /// (every rebuilt hole is treated as dirty, restoring the pre-M8.3 re-zero-everything
    /// behavior) — bisect insurance: a false inherited marker is a type-safety smear.</summary>
    internal static bool HoleMarkerInheritance = true;

    internal static uint HoleMarker(nint holeRef) => *(uint*)(holeRef + IntPtr.Size + sizeof(uint));

    internal static void SetHoleMarker(nint holeRef, uint value) => *(uint*)(holeRef + IntPtr.Size + sizeof(uint)) = value;

    // DOTNET_GCHeapHardLimit: cap on committed region bytes, 0 = none
    private long _hardLimit;

    // The linked-hole floor in force (M4): Region.MinLinkedHole normally, but when the
    // committed heap closes on the hard limit every sweep links holes down to the plug
    // minimum — slower handouts beat OutOfMemoryException
    private nint _minLinkedHole = Region.MinLinkedHole;
    private const nint PressureMinLinkedHole = 4 * 1024;

    // Card table storage (unbiased base), committed alongside the region frontier so the
    // EE's bulk-copy card writes always land on writable pages (missing-features 7.1)
    private nint _cardTableStorage;
    private nint _cardTableCommittedEnd;

    // Committed bytes currently sitting in the free pool (drives the retention trim)
    private long _pooledCommittedBytes;

    // Carveable linked-hole bytes across all bump regions: rebuilt by every sweep,
    // drawn down by hole carves. With the pool this is the space the next allocation
    // runway can use without committing fresh regions — the full-collection starvation
    // trigger reads it (M7 memory exchange rate). Size-class free blocks are not counted:
    // they only serve their own class, and the churn tiers here are bump and span.
    private long _linkedHoleBytes;

    // Side mark bitmap (SPEC-M6 §3): 1 bit per 8 heap bytes → 32 KB per region,
    // committed alongside the frontier. See GCObject.MarkBitmap for the contract.
    private readonly ulong* _markBitmap;
    private nint _markBitmapCommittedEnd;

    // The armed concurrent-sweep plan (M6.5 stage 2): set under STW at pause B, consumed
    // after RestartEE by the worker pool (SweepConcurrentFull) and by allocating threads
    // whose supply runs dry mid-sweep (TrySweepAssist). The assist is what makes the
    // ungated sweep safe on smear heaps: the window's overshoot drains the plan instead
    // of fresh-committing regions, which is the ratchet that forced the M6.5 pool gate.
    private int[]? _sweepPlan;
    private int _sweepPlanCount;
    private int _sweepCursor;
    private volatile bool _sweepInFlight;

    public RegionAllocator(NativeAllocator memory)
    {
        _memory = memory;

        _table = (RegionEntry*)NativeAllocator.OsReserve((nint)Region.Count * sizeof(RegionEntry));
        _tableCommittedEnd = (nint)_table;

        _pool = (int*)NativeAllocator.OsReserve((nint)Region.Count * sizeof(int));

        _markBitmap = (ulong*)NativeAllocator.OsReserve((nint)Region.Count << 15);
        _markBitmapCommittedEnd = (nint)_markBitmap;

        if (_table == null || _pool == null || _markBitmap == null
            || !NativeAllocator.OsCommit((nint)_pool, (nint)Region.Count * sizeof(int)))
        {
            // Initialization-time failure: the process cannot run without these
            throw new OutOfMemoryException("Failed to reserve GC region metadata");
        }

        _shards = [new AllocShard()];
        _reservoirClassHeads.AsSpan().Fill(-1);

        // Mark operations live on GCObject but the storage is ours: one heap per process
        // in production; the unit tests create allocators sequentially
        GCObject.MarkBitmap = _markBitmap;
        GCObject.MarkHeapBase = memory.LowestAddress;
    }

    /// <summary>Arms production locking (M7 sharded supply): one lock per shard, the
    /// reservoir lock, and the global pool lock. Called once, before any allocation
    /// reaches the heap; the unit tests never call it and keep the single unlocked
    /// shard. Lock order: at most one shard lock, then reservoir or pool (never both
    /// nested); cross-shard steals only TryAcquire.</summary>
    public void SetLocks(GcAwareLock[] shardLocks, GcAwareLock reservoirLock, GcAwareLock poolLock)
    {
        _shardLocks = shardLocks;
        _reservoirLock = reservoirLock;
        _poolLock = poolLock;
        _shards = new AllocShard[shardLocks.Length];

        for (int i = 0; i < _shards.Length; i++)
        {
            _shards[i] = new AllocShard();
        }
    }

    public int ShardCount => _shards.Length;

    /// <summary>Releases the metadata reservations. Production never disposes (the GC
    /// lives as long as the process); this exists for the unit tests.</summary>
    public void Dispose()
    {
        NativeAllocator.OsRelease((nint)_table);
        NativeAllocator.OsRelease((nint)_pool);
        NativeAllocator.OsRelease((nint)_markBitmap);
    }

    public void SetFreeObjectMethodTable(MethodTable* methodTable) => _freeObjectMethodTable = methodTable;

    public void SetCardTable(nint storage)
    {
        _cardTableStorage = storage;
        _cardTableCommittedEnd = storage;
    }

    /// <summary>Unbiased card storage base: byte k covers heap bytes [k &lt;&lt; 11, (k+1) &lt;&lt; 11).
    /// Region i's cards are the 1 KB at storage + (i &lt;&lt; 10). Zero when tests run without one.</summary>
    public nint CardTableStorage => _cardTableStorage;

    /// <summary>
    /// Clears every committed card byte. Runs under STW at the end of a collection: all
    /// old→young references as of the suspension were just traced (young collections) or
    /// made irrelevant by a full mark, and mutators cannot dirty cards until RestartEE.
    /// </summary>
    public void ClearCards()
    {
        if (_cardTableStorage != 0)
        {
            var length = _cardTableCommittedEnd - _cardTableStorage;

            if (length > 0)
            {
                Zeroing.Clear(_cardTableStorage, length);
            }
        }
    }

    public void SetHardLimit(long limit) => _hardLimit = limit;

    private bool CanCommit(nint bytes) => _hardLimit == 0 || CommittedRegionBytes + bytes <= _hardLimit;

    /// <summary>True within an eighth of the hard limit. The caller should prefer full
    /// collections (young ones cannot reclaim floating old garbage or stranded holes),
    /// and sweeps link every carveable hole (see Sweep).</summary>
    public bool UnderMemoryPressure => _hardLimit > 0 && CommittedRegionBytes > _hardLimit - (_hardLimit >> 3);

    public static int BlockCount(int sizeClass) => (int)(Region.Size / Region.ClassSizes[sizeClass]);

    public static ulong FullMask(int sizeClass)
    {
        var count = BlockCount(sizeClass);
        return count == 64 ? ulong.MaxValue : (1ul << count) - 1;
    }

    public nint HeapBase => _memory.LowestAddress;

    public int CarvedCount => _frontier;

    // Field-backed so parallel sweep workers can adjust it with interlocked adds
    private long _committedRegionBytes;

    public long CommittedRegionBytes => Volatile.Read(ref _committedRegionBytes);

    /// <summary>Bytes the next allocation runway can be served from without growing the
    /// committed heap: retained pool regions plus carveable linked holes. Approximate by
    /// design (read with the world running for the full-trigger heuristic).</summary>
    public long FreeCapacityBytes => Volatile.Read(ref _pooledCommittedBytes) + Volatile.Read(ref _linkedHoleBytes);

    /// <summary>Committed pool bytes alone — the concurrent sweep's window gate (M6.5):
    /// unlike <see cref="FreeCapacityBytes"/> it ignores linked holes, which a sweep is
    /// about to invalidate and rebuild.</summary>
    public long PooledCommittedBytes => Volatile.Read(ref _pooledCommittedBytes);

    public nint RegionBase(int index) => HeapBase + ((nint)index << Region.Shift);

    public RegionEntry* GetEntry(int index) => _table + index;

    /// <summary>
    /// Maps an address to its region index. True only for addresses inside carved regions;
    /// the caller still has to check the entry's Kind (Free regions hold no objects).
    /// </summary>
    public bool TryGetIndex(nint addr, out int index)
    {
        index = (int)((addr - HeapBase) >> Region.Shift);
        return (uint)index < (uint)_frontier;
    }

    /// <summary>Unit-test compatibility: shard 0 (tests run single-shard).</summary>
    public bool TryGetWindow(nint size, out nint window, out nint length, out bool needsZero)
        => TryGetWindow(0, size, out window, out length, out needsZero);

    /// <summary>
    /// Hands out a bump window of at least Align(size) + 24 bytes (SPEC-M2 §3-§4.1).
    /// When <paramref name="needsZero"/> is true the window holds recycled garbage and the
    /// caller must zero it before use — after releasing the shard lock, since the
    /// window is private to the caller from this point (M4 zero-at-carve).
    /// Caller holds <paramref name="shard"/>'s lock.
    /// </summary>
    public bool TryGetWindow(int shard, nint size, out nint window, out nint length, out bool needsZero)
    {
        var needed = Align(size) + 3 * IntPtr.Size;
        var s = _shards[shard];

        // Hole-first policy (SPEC-M2 §4.1): reuse swept holes before touching fresh memory
        if (TryCarveFromHoles(s, needed, out window, out length, out needsZero))
        {
            NoteHoleWindow(needsZero);
            return true;
        }

        while (true)
        {
            // Dry shard: restock from the reservoir, else a sibling's private list,
            // BEFORE consuming the active bump region — hole-first is a whole-heap
            // policy, not a per-shard one (ordering the active bump above the restock
            // measured +640 MB on the soak: hot shards chained fresh 2 MB regions off
            // the pool while holes piled up elsewhere).
            if ((TryPullFromReservoir(shard, needed) || TryStealRecycled(shard, needed))
                && TryCarveFromHoles(s, needed, out window, out length, out needsZero))
            {
                NoteHoleWindow(needsZero);
                return true;
            }

            if (s.ActiveBump >= 0)
            {
                var entry = GetEntry(s.ActiveBump);
                var dataEnd = RegionBase(s.ActiveBump) + Region.Size - Region.GuardBytes;
                var available = dataEnd - entry->Cursor;

                if (available >= needed)
                {
                    if (entry->Age == RegionAge.Old)
                    {
                        // The active region was promoted by a sweep mid-carve: it now mixes
                        // sticky-marked survivors with the young objects about to land here
                        entry->Age = RegionAge.Reopened;
                    }

                    length = Math.Min(Math.Max(Region.WindowSize, needed), available);
                    window = entry->Cursor;
                    entry->Cursor += length;
                    needsZero = (entry->BumpFlags & RegionEntry.BumpDirtyFlag) != 0;

                    if (GcStats.Enabled && !needsZero)
                    {
                        Interlocked.Increment(ref GcStats.CleanWindowCount);
                    }

                    return true;
                }

                // Abandon the tail: it is below every walk bound, so it needs no plug; it
                // is reclaimed when the region dies (SPEC-M2 §4.1)
                s.ActiveBump = -1;
            }

            // Dry supply during a concurrent sweep: drain the plan instead of
            // fresh-committing, then retry holes first — on smear heaps the assist
            // publishes holes, not pool regions
            if (Volatile.Read(ref _poolCount) == 0 && TrySweepAssist(shard))
            {
                if (TryCarveFromHoles(s, needed, out window, out length, out needsZero))
                {
                    NoteHoleWindow(needsZero);
                    return true;
                }

                continue;
            }

            if (!TryCarveRegion(RegionKind.Bump, out var index, out var dirty))
            {
                window = 0;
                length = 0;
                needsZero = false;
                return false;
            }

            // Kind, Cursor and the hole fields were stamped under the pool lock
            // (see InitializeCarvedEntry); the rest is ours under the shard lock
            var fresh = GetEntry(index);
            fresh->BumpFlags = dirty ? RegionEntry.BumpDirtyFlag : (byte)0;
            fresh->LiveBytes = 0;
            fresh->NextRecycled = -1;

            s.ActiveBump = index;
        }
    }

    private static void NoteHoleWindow(bool needsZero)
    {
        if (GcStats.Enabled)
        {
            Interlocked.Increment(ref GcStats.HoleWindowCount);

            if (!needsZero)
            {
                Interlocked.Increment(ref GcStats.CleanWindowCount);
            }
        }
    }

    /// <summary>Hole-only carve for stash refills (M7): redistributes committed supply
    /// without deepening a drought — no active-bump, steal, assist, pool or frontier
    /// fallthrough, so a dry hole list just means no refill. (The first stash shipped
    /// refills through the full TryGetWindow and two of three lohmix runs ratcheted
    /// +0.5 GB: 4 fresh 128 KB carves per acquisition outran the concurrent sweep's
    /// publications exactly like the pre-assist M6.5 gate story.)
    /// Caller holds <paramref name="shard"/>'s lock.</summary>
    public bool TryGetStashWindow(int shard, nint size, out nint window, out nint length, out bool needsZero)
    {
        var needed = Align(size) + 3 * IntPtr.Size;

        if (!TryCarveFromHoles(_shards[shard], needed, out window, out length, out needsZero))
        {
            return false;
        }

        NoteHoleWindow(needsZero);
        return true;
    }

    private const int MaxStealProbes = 8;
    private const int PullBatch = 4;

    /// <summary>
    /// Restocks a dry shard from the reservoir (M7 sharded supply): first-fit walk for a
    /// region holding a hole ≥ <paramref name="needed"/> — the same walk the old global
    /// recycled list did — then that region plus up to <see cref="PullBatch"/>-1 of its
    /// followers move to the shard's private list. The batch is what amortizes the
    /// reservoir lock down to one acquisition per dozens of carves. Caller holds the
    /// shard's lock; the reservoir lock nests inside (shard → reservoir order).
    /// </summary>
    private bool TryPullFromReservoir(int shard, nint needed)
    {
        // Racy peeks gate the lock: when the heap has no carveable holes at all
        // (post-full, pool-fed phases) every window would otherwise pay a failed walk
        if (Volatile.Read(ref _reservoirRecycledHead) < 0 || Volatile.Read(ref _linkedHoleBytes) < needed)
        {
            return false;
        }

        var s = _shards[shard];

        _reservoirLock?.Acquire();

        try
        {
            var previous = -1;
            var i = _reservoirRecycledHead;

            while (i >= 0)
            {
                var entry = GetEntry(i);

                if (entry->HoleBytes >= needed && HasHole(entry, needed))
                {
                    // Take [i .. i+PullBatch) in list order: the fitting region plus
                    // followers whatever their hole sizes — they serve future requests
                    var tail = i;
                    var taken = 1;

                    while (taken < PullBatch && GetEntry(tail)->NextRecycled >= 0)
                    {
                        tail = GetEntry(tail)->NextRecycled;
                        taken++;
                    }

                    var next = GetEntry(tail)->NextRecycled;

                    if (previous < 0)
                    {
                        _reservoirRecycledHead = next;
                    }
                    else
                    {
                        GetEntry(previous)->NextRecycled = next;
                    }

                    GetEntry(tail)->NextRecycled = s.RecycledHead;
                    s.RecycledHead = i;
                    return true;
                }

                previous = i;
                i = entry->NextRecycled;
            }

            return false;
        }
        finally
        {
            _reservoirLock?.Release();
        }
    }

    /// <summary>Class-list flavor of <see cref="TryPullFromReservoir"/>: adopts one
    /// reservoir region of the class — guaranteed useful, a listed region always has a
    /// free block.</summary>
    private bool TryPullClassRegion(int shard, int sizeClass)
    {
        if (Volatile.Read(ref _reservoirClassHeads[sizeClass]) < 0)
        {
            return false;
        }

        _reservoirLock?.Acquire();

        try
        {
            var head = _reservoirClassHeads[sizeClass];

            if (head < 0)
            {
                return false;
            }

            var entry = GetEntry(head);
            _reservoirClassHeads[sizeClass] = entry->NextInClassList;

            var s = _shards[shard];
            entry->NextInClassList = s.ClassHeads[sizeClass];
            s.ClassHeads[sizeClass] = head;
            return true;
        }
        finally
        {
            _reservoirLock?.Release();
        }
    }

    /// <summary>
    /// Straggler drain (M7 sharded supply): when the reservoir is dry, moves one recycled
    /// region holding a hole ≥ <paramref name="needed"/> from a sibling's private list —
    /// privates hold at most a pull batch each, but without the steal they would strand
    /// through a whole young cycle. Victims are TryAcquired — a contended victim is
    /// skipped, never waited on (the caller holds its own shard lock, so a blocking wait
    /// here could deadlock two thieves).
    /// </summary>
    private bool TryStealRecycled(int shard, nint needed)
    {
        if (_shardLocks is null || Volatile.Read(ref _linkedHoleBytes) < needed)
        {
            return false;
        }

        var shardCount = _shards.Length;

        for (int k = 1; k < shardCount; k++)
        {
            var victimIndex = (shard + k) % shardCount;
            var victim = _shards[victimIndex];

            if (Volatile.Read(ref victim.RecycledHead) < 0 || !_shardLocks[victimIndex].TryAcquire())
            {
                continue;
            }

            try
            {
                var previous = -1;
                var probes = 0;
                var i = victim.RecycledHead;

                while (i >= 0 && probes++ < MaxStealProbes)
                {
                    var entry = GetEntry(i);
                    var next = entry->NextRecycled;

                    if (entry->HoleBytes >= needed && HasHole(entry, needed))
                    {
                        if (previous < 0)
                        {
                            victim.RecycledHead = next;
                        }
                        else
                        {
                            GetEntry(previous)->NextRecycled = next;
                        }

                        var s = _shards[shard];
                        entry->NextRecycled = s.RecycledHead;
                        s.RecycledHead = i;
                        return true;
                    }

                    previous = i;
                    i = next;
                }
            }
            finally
            {
                _shardLocks[victimIndex].Release();
            }
        }

        return false;
    }

    private static bool HasHole(RegionEntry* entry, nint needed)
    {
        // Bounded: a post-full region can hold hundreds of floor-sized holes (deep
        // linking), and a fragmented victim must not turn a failed steal into thousands
        // of pointer chases under two locks. A miss just skips the region.
        var probes = 0;

        for (var holeRef = entry->FirstHole; holeRef != 0 && probes++ < 16; holeRef = *(nint*)(holeRef + 2 * IntPtr.Size))
        {
            if ((nint)((GCObject*)holeRef)->Length + 3 * IntPtr.Size >= needed)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Carves a window from the first hole that fits, per SPEC-M2 §4.4. A hole is a linked
    /// free-object plug in a swept bump region: extent [ref - 8, ref + 16 + Length).
    /// When the background zeroer pre-zeroed the region's hole bodies (M7), only the
    /// 32-byte plug header + link prefix is stale — cleaned right here — and the window
    /// goes out with no zeroing debt.
    /// </summary>
    private bool TryCarveFromHoles(AllocShard shard, nint needed, out nint window, out nint length, out bool needsZero)
    {
        window = 0;
        length = 0;
        needsZero = true;

        var regionIndex = shard.RecycledHead;
        var previousRegion = -1;

        while (regionIndex >= 0)
        {
            var entry = GetEntry(regionIndex);

            var holeRef = entry->FirstHole;
            nint previousHoleRef = 0;

            while (holeRef != 0)
            {
                var plug = (GCObject*)holeRef;
                var extent = (nint)plug->Length + 3 * IntPtr.Size;
                var next = *(nint*)(holeRef + 2 * IntPtr.Size);

                if (extent >= needed)
                {
                    // Read before any plug/prefix writes below can touch it (M8.3)
                    var holeWasZero = HoleMarker(holeRef) == ZeroedHoleMarker;

                    if (entry->Age == RegionAge.Old)
                    {
                        // Young objects are about to land among old survivors: the region
                        // must be card-scanned (old sources) AND swept (young dead) — see
                        // RegionAge.Reopened
                        entry->Age = RegionAge.Reopened;
                    }

                    window = holeRef - IntPtr.Size;

                    length = Math.Min(Math.Max(Region.WindowSize, needed), extent);

                    if (extent - length < 6 * IntPtr.Size)
                    {
                        // A remainder too small to re-plug-and-link must not be left behind
                        length = extent;
                    }

                    // Unlink the hole
                    if (previousHoleRef == 0)
                    {
                        entry->FirstHole = next;
                    }
                    else
                    {
                        *(nint*)(previousHoleRef + 2 * IntPtr.Size) = next;
                    }

                    entry->HoleBytes -= (int)extent;
                    Interlocked.Add(ref _linkedHoleBytes, -extent);

                    if (length < extent)
                    {
                        // Re-plug the remainder; its stale bytes stay (zero-at-carve), and
                        // its mark bits are clear by construction (hole extents are dead)
                        var remainderStart = window + length;
                        var remainder = (GCObject*)(remainderStart + IntPtr.Size);
                        remainder->RawMethodTable = _freeObjectMethodTable;
                        remainder->Length = (uint)(extent - length - 3 * IntPtr.Size);
                        // The remainder body inherits the source hole's zeroed-ness;
                        // its own 32-byte prefix is written here either way (M8.3)
                        SetHoleMarker(remainderStart + IntPtr.Size, holeWasZero ? ZeroedHoleMarker : 0);

                        if (extent - length >= _minLinkedHole)
                        {
                            *(nint*)(remainderStart + 3 * IntPtr.Size) = entry->FirstHole;
                            entry->FirstHole = remainderStart + IntPtr.Size;
                            entry->HoleBytes += (int)(extent - length);
                            Interlocked.Add(ref _linkedHoleBytes, extent - length);
                        }
                        // else: a sub-window remainder floats until the next full collection
                    }

                    if (holeWasZero)
                    {
                        // Pre-zeroed hole body (M7, per-hole since M8.3): only the plug's
                        // preheader, header, marker and link bytes at the window start
                        // are stale
                        new Span<byte>((void*)window, 4 * IntPtr.Size).Clear();
                        needsZero = false;
                    }

                    if (entry->FirstHole == 0)
                    {
                        // No holes left: unlink the region from the recycled list
                        if (previousRegion < 0)
                        {
                            shard.RecycledHead = entry->NextRecycled;
                        }
                        else
                        {
                            GetEntry(previousRegion)->NextRecycled = entry->NextRecycled;
                        }

                        entry->NextRecycled = -1;
                    }

                    return true;
                }

                previousHoleRef = holeRef;
                holeRef = next;
            }

            previousRegion = regionIndex;
            regionIndex = entry->NextRecycled;
        }

        return false;
    }

    /// <summary>Unit-test compatibility: shard 0 (tests run single-shard).</summary>
    public bool TryAllocBlock(int sizeClass, out nint block, out bool needsZero)
        => TryAllocBlock(0, sizeClass, out block, out needsZero);

    /// <summary>
    /// Allocates one block from a size-class region (SPEC-M2 §4.2). When
    /// <paramref name="needsZero"/> is true the block holds stale recycled contents and the
    /// caller must zero the object extent before use — after releasing the shard lock,
    /// since the block is private to the caller from this point (M4 zero-at-carve).
    /// Caller holds <paramref name="shard"/>'s lock.
    /// </summary>
    public bool TryAllocBlock(int shard, int sizeClass, out nint block, out bool needsZero)
    {
        var s = _shards[shard];
        var head = s.ClassHeads[sizeClass];

        // Dry shard: restock from the reservoir, else a sibling, before carving fresh —
        // same restock-before-pool rationale as the window tier
        if (head < 0 && (TryPullClassRegion(shard, sizeClass) || TryStealClassRegion(shard, sizeClass)))
        {
            head = s.ClassHeads[sizeClass];
        }

        // Dry class list during a concurrent sweep: the plan may hold partially-free
        // regions of this class — drain it before carving a fresh region
        while (head < 0 && Volatile.Read(ref _poolCount) == 0 && TrySweepAssist(shard))
        {
            head = s.ClassHeads[sizeClass];
        }

        if (head < 0)
        {
            if (!TryCarveRegion(RegionKind.SizeClass, out var index, out var dirty))
            {
                block = 0;
                needsZero = false;
                return false;
            }

            // Kind, SizeClass and AllocatedBlocks were stamped under the pool lock
            // (see InitializeCarvedEntry)
            var fresh = GetEntry(index);
            fresh->SizeClass = (byte)sizeClass;
            fresh->LiveBytes = 0;
            fresh->DirtyBlocks = dirty ? ulong.MaxValue : 0;
            fresh->NextInClassList = -1;

            s.ClassHeads[sizeClass] = index;
            head = index;
        }

        var entry = GetEntry(head);

        if (entry->Age == RegionAge.Old)
        {
            // A young block is joining sticky-marked old blocks (see RegionAge.Reopened)
            entry->Age = RegionAge.Reopened;
        }

        var fullMask = FullMask(sizeClass);

        // The head of a class list always has a free block: full regions are unlinked
        var i = BitOperations.TrailingZeroCount(~entry->AllocatedBlocks & fullMask);
        entry->AllocatedBlocks |= 1ul << i;

        // Once handed out the block is dirty for every future tenant; dead blocks keep
        // their contents (sweeps no longer zero), so the bit never clears
        needsZero = (entry->DirtyBlocks & (1ul << i)) != 0;
        entry->DirtyBlocks |= 1ul << i;

        if ((entry->AllocatedBlocks & fullMask) == fullMask)
        {
            s.ClassHeads[sizeClass] = entry->NextInClassList;
            entry->NextInClassList = -1;
        }

        block = RegionBase(head) + (nint)i * Region.ClassSizes[sizeClass];
        return true;
    }

    /// <summary>Class-list flavor of <see cref="TryStealRecycled"/>: adopts a sibling's
    /// head region for the class — guaranteed useful, a listed region always has a free
    /// block. Same TryAcquire-only discipline.</summary>
    private bool TryStealClassRegion(int shard, int sizeClass)
    {
        if (_shardLocks is null)
        {
            return false;
        }

        var shardCount = _shards.Length;

        for (int k = 1; k < shardCount; k++)
        {
            var victimIndex = (shard + k) % shardCount;
            var victim = _shards[victimIndex];

            if (Volatile.Read(ref victim.ClassHeads[sizeClass]) < 0 || !_shardLocks[victimIndex].TryAcquire())
            {
                continue;
            }

            try
            {
                var head = victim.ClassHeads[sizeClass];

                if (head < 0)
                {
                    continue; // emptied between the peek and the acquire
                }

                var entry = GetEntry(head);
                victim.ClassHeads[sizeClass] = entry->NextInClassList;

                var s = _shards[shard];
                entry->NextInClassList = s.ClassHeads[sizeClass];
                s.ClassHeads[sizeClass] = head;
                return true;
            }
            finally
            {
                _shardLocks[victimIndex].Release();
            }
        }

        return false;
    }

    /// <summary>Safe accessor for iterating a size-class region's allocated blocks.</summary>
    public (ulong bitmap, int classSize) GetSizeClassInfo(int index)
    {
        var entry = GetEntry(index);
        return (entry->AllocatedBlocks, Region.ClassSizes[entry->SizeClass]);
    }

    /// <summary>Unit-test compatibility (and the OOM last stand): frontier allowed.</summary>
    public bool TryAllocSpan(int count, out nint spanBase) => TryAllocSpan(count, out spanBase, allowFrontier: true);

    /// <summary>
    /// Allocates a span of contiguous regions for a single large object (SPEC-M2 §4.3):
    /// first a contiguous run of pooled free regions, else the frontier (contiguous by
    /// construction). Recycled members come back with stale contents and their
    /// <see cref="RegionEntry.SpanIsDirty"/> set — the caller zeroes the object extent
    /// via <see cref="ZeroSpanCarve"/> *after releasing the pool lock* (M4
    /// zero-at-carve, extended to spans).
    ///
    /// The whole operation runs under the pool lock (M7 sharded supply). The inline sweep
    /// assists this tier used to run moved to the caller's retry loop: an assist splices
    /// supply into a shard, and shard locks must never be waited on while holding the pool
    /// lock. With <paramref name="allowFrontier"/> false a dry pool fails instead of
    /// committing past the frontier — the caller still has assists to try.
    /// </summary>
    public bool TryAllocSpan(int count, out nint spanBase, bool allowFrontier)
    {
        _poolLock?.Acquire();

        try
        {
            return TryAllocSpanLocked(count, out spanBase, allowFrontier);
        }
        finally
        {
            _poolLock?.Release();
        }
    }

    private bool TryAllocSpanLocked(int count, out nint spanBase, bool allowFrontier)
    {
        spanBase = 0;
        int start;

        if (count == 1)
        {
            // The common case (spans ≤ 2 MB): any one free region works, so take the
            // pool's LIFO top instead of scanning the table for a run
            if (!TryCarveRegionLocked(RegionKind.SpanStart, out start, out var dirty, allowFrontier))
            {
                return false;
            }

            GetEntry(start)->SpanIsDirty = dirty ? (byte)1 : (byte)0;
        }
        else if (TryTakeFreeRun(count, out start))
        {
            // Only the start region can hold the span object's mark bit; extension
            // slices are never consulted (no object starts there)
            ClearRegionMarks(start);
        }
        else
        {
            if (!allowFrontier || _frontier + count > Region.Count || !EnsureTableCommitted(_frontier + count))
            {
                return false;
            }

            start = _frontier;

            if (!CanCommit((nint)count << Region.Shift) || !_memory.TryCommit(RegionBase(start), (nint)count << Region.Shift))
            {
                return false;
            }

            Interlocked.Add(ref _committedRegionBytes, (long)count << Region.Shift);
            _frontier = start + count;

            for (int i = 0; i < count; i++)
            {
                var entry = GetEntry(start + i);
                entry->IsCommitted = RegionEntry.CommitDirty;
                entry->SpanIsDirty = 0; // freshly committed: OS-zeroed
            }
        }

        var startEntry = GetEntry(start);
        startEntry->Kind = RegionKind.SpanStart;
        startEntry->Age = RegionAge.Fresh;
        startEntry->LiveBytes = 0;
        startEntry->SpanCount = count;

        for (int i = 1; i < count; i++)
        {
            var entry = GetEntry(start + i);
            entry->Kind = RegionKind.SpanExtension;
            entry->LiveBytes = 0;
            entry->SpanStartIndex = start;
        }

        spanBase = RegionBase(start);
        return true;
    }

    /// <summary>
    /// Finds a contiguous run of pooled free regions, commits any decommitted member, and
    /// removes the run from the pool. Every Free region is in the pool, so a run of Free
    /// table entries is a run of pool members.
    /// </summary>
    private bool TryTakeFreeRun(int count, out int start)
    {
        start = -1;

        if (_poolCount < count)
        {
            return false;
        }

        var runLength = 0;

        for (int i = 0; i < _frontier; i++)
        {
            // A checked-out region is Free but not in the pool (the background zeroer
            // owns its memory): taking it here would hand the span a region that is
            // being memset concurrently
            if (GetEntry(i)->Kind != RegionKind.Free || GetEntry(i)->ZeroerCheckedOut != 0)
            {
                runLength = 0;
            }
            else if (++runLength >= count)
            {
                start = i - count + 1;
                break;
            }
        }

        if (start < 0)
        {
            return false;
        }

        // Commit any decommitted member first: a failure here leaves the pool intact.
        // Members that stayed committed hold stale recycled contents; they are only
        // flagged here — the caller zeroes the object extent after releasing the
        // allocation lock (M4 zero-at-carve, extended to spans).
        for (int i = 0; i < count; i++)
        {
            var entry = GetEntry(start + i);

            if (entry->IsCommitted == RegionEntry.CommitNone)
            {
                if (!CanCommit(Region.Size) || !_memory.TryCommit(RegionBase(start + i), Region.Size))
                {
                    return false;
                }

                Interlocked.Add(ref _committedRegionBytes, Region.Size);
                Interlocked.Add(ref _pooledCommittedBytes, Region.Size);
                entry->SpanIsDirty = 0; // freshly committed: OS-zeroed
            }
            else
            {
                entry->SpanIsDirty = entry->IsCommitted == RegionEntry.CommitDirty ? (byte)1 : (byte)0;
            }

            entry->IsCommitted = RegionEntry.CommitDirty;
        }

        for (int i = 0; i < count; i++)
        {
            RemoveFromPool(start + i);
            Interlocked.Add(ref _pooledCommittedBytes, -Region.Size);
        }

        return true;
    }

    private void RemoveFromPool(int index)
    {
        for (int i = 0; i < _poolCount; i++)
        {
            if (_pool[i] == index)
            {
                _pool[i] = _pool[--_poolCount];
                return;
            }
        }
    }

    private bool TryCarveRegion(RegionKind kind, out int index, out bool dirty)
    {
        _poolLock?.Acquire();

        try
        {
            return TryCarveRegionLocked(kind, out index, out dirty, allowFrontier: true);
        }
        finally
        {
            _poolLock?.Release();
        }
    }

    private bool TryCarveRegionLocked(RegionKind kind, out int index, out bool dirty, bool allowFrontier)
    {
        dirty = false;

        if (_poolCount > 0)
        {
            index = _pool[--_poolCount];
            var entry = GetEntry(index);

            if (entry->IsCommitted != RegionEntry.CommitNone)
            {
                // Recycled content is stale (M4 zero-at-carve) unless the background
                // zeroer got to it first (M7); either way its tenant dirties it now
                dirty = entry->IsCommitted == RegionEntry.CommitDirty;
                entry->IsCommitted = RegionEntry.CommitDirty;
                Interlocked.Add(ref _pooledCommittedBytes, -Region.Size);
            }
            else
            {
                if (!CanCommit(Region.Size) || !_memory.TryCommit(RegionBase(index), Region.Size))
                {
                    _poolCount++; // put it back
                    return false;
                }

                entry->IsCommitted = RegionEntry.CommitDirty;
                Interlocked.Add(ref _committedRegionBytes, Region.Size);
            }

            // Sticky marks from the region's previous life would resurrect dead objects
            // (only full collections clear the bitmap); recycled regions restart clean.
            // Frontier regions below skip this: their bitmap slice is freshly committed.
            ClearRegionMarks(index);
            InitializeCarvedEntry(index, kind);

            return true;
        }

        index = _frontier;

        if (!allowFrontier
            || index >= Region.Count
            || !CanCommit(Region.Size)
            || !EnsureTableCommitted(index + 1)
            || !_memory.TryCommit(RegionBase(index), Region.Size))
        {
            return false;
        }

        GetEntry(index)->IsCommitted = RegionEntry.CommitDirty;
        Interlocked.Add(ref _committedRegionBytes, Region.Size);
        _frontier = index + 1;
        InitializeCarvedEntry(index, kind);
        return true;
    }

    /// <summary>
    /// Stamps, still under the pool lock, the entry fields other threads read without the
    /// carver's shard lock: the span run scan trusts Kind == Free ⇒ pooled, and the census
    /// reads kind-specific fields with the world running — a Kind observed with a stale
    /// union from the region's previous life (e.g. a SizeClass byte ≥ ClassCount) must
    /// never be visible. The carver finishes the rest under its own shard lock.
    /// </summary>
    private void InitializeCarvedEntry(int index, RegionKind kind)
    {
        var entry = GetEntry(index);
        entry->Age = RegionAge.Fresh;

        if (kind == RegionKind.Bump)
        {
            entry->BumpFlags = 0;
            entry->Cursor = RegionBase(index);
            entry->FirstHole = 0;
            entry->HoleBytes = 0;
        }
        else if (kind == RegionKind.SizeClass)
        {
            entry->SizeClass = 0;
            entry->AllocatedBlocks = 0;
        }
        else if (kind == RegionKind.SpanStart)
        {
            entry->SpanCount = 1; // exact: multi-region spans never carve through here
        }

        entry->Kind = kind;
    }

    /// <summary>
    /// Sweeps carved regions (SPEC-M2 §7). Runs under STW after marking and takes no
    /// locks (see GcAwareLock for why that is safe). Returns total live bytes swept.
    ///
    /// In young mode (M4 sticky generations) object-level work — the walk, the zeroing,
    /// the recycling — happens only in Fresh and Reopened regions; their survivors are
    /// promoted (Age = Old). The walk is age-blind: sticky marks keep old survivors in
    /// Reopened regions IsMarked, so they sweep as live without re-marking. Old regions
    /// keep their objects untouched and only get their hole/class lists relinked. The
    /// returned total is therefore the young survivor (promoted) byte count, not whole-heap
    /// live: old objects are skipped by the mark (already marked) and never re-counted.
    /// </summary>
    public long Sweep(bool youngOnly = false, GcWorkerPool? pool = null)
    {
        // Memory pressure: within an eighth of the hard limit, stop stranding sub-window
        // holes — link everything carveable so the heap can run dense instead of dying.
        // Full sweeps always link deep (see Region.FullMinLinkedHole).
        _minLinkedHole = UnderMemoryPressure ? PressureMinLinkedHole
            : youngOnly ? Region.MinLinkedHole
            : Region.FullMinLinkedHole;

        // Hole lists and class lists are rebuilt from scratch on every sweep
        ResetSupplyLists();

        var frontier = _frontier;

        if (pool is null)
        {
            var lists = NewSweepLists();
            SweepRange(0, frontier, youngOnly, ref lists, deferredFrees: null, plan: null);
            SpliceToReservoir(ref lists);
            return lists.Live;
        }

        // Parallel (M5): regions are dispensed in chunks; every region is touched by
        // exactly one worker, so object work needs no synchronization. Workers build
        // their own intrusive lists (spliced under a lock at the end), push freed
        // regions into the pool lock-free (MakeFree), and adjust the shared byte
        // counters with interlocked adds (RecycleSpan).
        var cursor = 0;
        long liveTotal = 0;
        var mergeLock = new Lock();

        pool.Run(() =>
        {
            const int Chunk = 16;
            var lists = NewSweepLists();

            while (true)
            {
                var start = Interlocked.Add(ref cursor, Chunk) - Chunk;

                if (start >= frontier)
                {
                    break;
                }

                SweepRange(start, Math.Min(start + Chunk, frontier), youngOnly, ref lists, deferredFrees: null, plan: null);
            }

            lock (mergeLock)
            {
                // All sweep supply goes to the reservoir — shards pull on demand; the
                // merge lock (not the reservoir lock) suffices under STW
                SpliceToReservoir(ref lists);
                liveTotal += lists.Live;
            }
        });

        return liveTotal;
    }

    /// <summary>Resets every shard's private supply and the reservoir. Runs under STW
    /// with the zeroer gated, so no locks are needed: a mutator that won a shard lock
    /// during the suspension is parked before touching supply state (see GcAwareLock).</summary>
    private void ResetSupplyLists()
    {
        Volatile.Write(ref _linkedHoleBytes, 0);
        _reservoirRecycledHead = -1;
        _reservoirClassHeads.AsSpan().Fill(-1);

        foreach (var shard in _shards)
        {
            shard.RecycledHead = -1;
            shard.ClassHeads.AsSpan().Fill(-1);
        }
    }

    /// <summary>
    /// Abandons every shard's active bump region for a concurrent sweep — sealed, not
    /// dropped: the tail above the cursor is plugged and the cursor advanced to the data
    /// end, so the following full sweep folds the never-carved space into the trailing
    /// hole. Merely dropping the reference measured +780 MB of standing strands on the
    /// ASP.NET soak: one abandoned active per full cycle was noise in the single-lock
    /// world, but sixteen per cycle — refilling slowly because carves prefer holes —
    /// strands most of each region until its survivors happen to die.
    /// Runs under STW at pause B (walkability: the plug is in place before the world
    /// restarts; the region is unreachable to carves once the lists reset).
    /// </summary>
    private void SealActiveBumps()
    {
        foreach (var shard in _shards)
        {
            var index = shard.ActiveBump;

            if (index < 0)
            {
                continue;
            }

            shard.ActiveBump = -1;

            var entry = GetEntry(index);
            var dataEnd = RegionBase(index) + Region.Size - Region.GuardBytes;
            var tail = dataEnd - entry->Cursor;

            if (tail >= 3 * IntPtr.Size)
            {
                // Same plug formula as ClosePlug: extent [Cursor, dataEnd), object ref
                // at Cursor + 8. A sub-plug tail (< 24 bytes) stays below the old walk
                // bound and floats until the region dies, like an inline abandon.
                var plug = (GCObject*)(entry->Cursor + IntPtr.Size);
                plug->RawMethodTable = _freeObjectMethodTable;
                plug->Length = (uint)(tail - 3 * IntPtr.Size);
                // Sealed tails cover never-carved (possibly stale) memory (M8.3)
                SetHoleMarker(entry->Cursor + IntPtr.Size, 0);
                entry->Cursor = dataEnd;
            }
        }
    }

    /// <summary>Whole-heap live bytes as accumulated by the mark phase's interlocked
    /// adds — the same number a full sweep would return, available at pause B before
    /// any sweeping (M6.5): budgets and the sticky accounting no longer wait for the
    /// sweep. Free entries hold zero by construction.</summary>
    public long SumLiveBytes()
    {
        long total = 0;

        for (int i = 0; i < _frontier; i++)
        {
            total += GetEntry(i)->LiveBytes;
        }

        return total;
    }

    /// <summary>
    /// The STW half of a concurrent full sweep (M6.5), run inside pause B: reset the
    /// allocation supply — hole lists, class lists, the active bump region — so
    /// nothing the concurrent walk will rewrite is reachable to carves. Post-restart
    /// allocation runs on the pool, the frontier, and whatever
    /// <see cref="SweepConcurrentFull"/> has published so far. Abandoning the active
    /// bump region wastes at most its virgin tail until the region recycles.
    /// </summary>
    public void BeginConcurrentSweep(int[] plan, int planCount)
    {
        _minLinkedHole = UnderMemoryPressure ? PressureMinLinkedHole : Region.FullMinLinkedHole;
        SealActiveBumps();
        ResetSupplyLists();

        // Arm the assist before the world restarts: the first post-restart carve can
        // hit a dry supply before the worker pool has published anything
        _sweepPlan = plan;
        _sweepPlanCount = planCount;
        _sweepCursor = 0;
        _sweepInFlight = true;
    }

    /// <summary>
    /// Mutator sweep-assist (M6.5 stage 2): called from the carve slow paths — the
    /// caller's shard lock held — when supply runs dry while a concurrent sweep is in
    /// flight. Claims one plan chunk, sweeps it right here, splices the supply into the
    /// caller's own shard (demand-driven distribution) and publishes freed regions under
    /// the pool lock, so allocation demand drains the plan instead of fresh-committing.
    /// Returns true when a chunk was swept — the caller retries its supply — and false
    /// once the plan is exhausted. Safe against cycle turnover: a claim runs entirely
    /// inside one allocation call, and the next cycle's plan is rebuilt under a
    /// suspension that waits for every such call to drain.
    /// </summary>
    public bool TrySweepAssist(int shard)
    {
        if (!_sweepInFlight)
        {
            return false;
        }

        var plan = _sweepPlan;
        var planCount = _sweepPlanCount;

        if (plan is null)
        {
            return false;
        }

        // Much smaller than the worker chunk: this runs on an application thread, and
        // two smear regions' holes (~2-4 MB) already serve dozens of carves — the
        // assist exists to bridge to the workers' publications, not to compete with
        // them for the plan
        const int Chunk = 2;

        var start = Interlocked.Add(ref _sweepCursor, Chunk) - Chunk;

        if (start >= planCount)
        {
            return false;
        }

        var s = _shards[shard];
        var lists = NewSweepLists();
        s.AssistFreed.Clear();

        SweepRange(start, Math.Min(start + Chunk, planCount), youngOnly: false, ref lists, s.AssistFreed, plan);

        SpliceSweepLists(ref lists, shard);

        if (s.AssistFreed.Count > 0)
        {
            _poolLock?.Acquire();

            try
            {
                foreach (var index in s.AssistFreed)
                {
                    MakeFree(index);
                }
            }
            finally
            {
                _poolLock?.Release();
            }
        }

        return true;
    }

    /// <summary>
    /// STW pre-pass of the concurrent sweep (M6.5), inside pause B after
    /// <see cref="BeginConcurrentSweep"/>: wholesale-recycle every region the marks
    /// left empty — O(1) per region, and on churn workloads that is the entire dead
    /// nursery — so the allocation runway reopens the moment the world restarts.
    /// Without this the first A/B ran +1.1 GB committed: mutators fresh-committed
    /// through the whole sweep window while every recyclable region sat unpublished.
    /// Regions needing a walk stay in the plan for the concurrent phase.
    /// </summary>
    public void RecycleWholesaleDead(int[] plan, int planCount)
    {
        for (int i = 0; i < planCount; i++)
        {
            if (plan[i] < 0)
            {
                continue;
            }

            var entry = GetEntry(i);

            if (entry->LiveBytes != 0)
            {
                continue;
            }

            if (entry->Kind == RegionKind.Bump)
            {
                RecycleBumpRegion(i, deferredFrees: null);
                plan[i] = -1;
            }
            else if (entry->Kind == RegionKind.SpanStart)
            {
                RecycleSpan(i, deferredFrees: null);
                plan[i] = -1;
            }
        }
    }

    /// <summary>
    /// The concurrent half (M6.5): sweeps exactly the regions the pause-B plan marked
    /// in-use, with the world running. Safe because none of them is reachable to
    /// allocation until published: <see cref="BeginConcurrentSweep"/> reset the supply
    /// lists under STW, pool pops during the sweep only produce regions the plan says
    /// to skip, and planned regions' cursors cannot move (only active bump regions'
    /// do, and they were abandoned). Workers publish each chunk's supply under locks —
    /// list splices into a rotating shard (spreading the rebuilt supply), pool pushes
    /// under the pool lock — so carves see complete per-chunk results; the zeroer must
    /// be gated out by the caller for the whole sweep (it walks hole memory without
    /// locks). The return value is Σ LiveBytes over swept regions, for parity checks —
    /// budgets already consumed <see cref="SumLiveBytes"/> at pause B.
    /// </summary>
    public long SweepConcurrentFull(GcWorkerPool? pool)
    {
        var plan = _sweepPlan!;
        var planCount = _sweepPlanCount;
        long liveTotal = 0;

        void Worker()
        {
            const int Chunk = 16;
            var lists = NewSweepLists();
            var freed = new List<int>();
            long live = 0;

            while (true)
            {
                // The cursor is shared with TrySweepAssist: allocating threads whose
                // supply ran dry claim chunks through the same counter
                var start = Interlocked.Add(ref _sweepCursor, Chunk) - Chunk;

                if (start >= planCount)
                {
                    break;
                }

                SweepRange(start, Math.Min(start + Chunk, planCount), youngOnly: false, ref lists, freed, plan);
                live += lists.Live;

                // Publish this chunk's supply. The worker never holds two locks at
                // once, so it can never participate in a lock-order cycle with carves
                // (which nest shard → reservoir/pool).
                _reservoirLock?.Acquire();

                try
                {
                    SpliceToReservoir(ref lists);
                }
                finally
                {
                    _reservoirLock?.Release();
                }

                if (freed.Count > 0)
                {
                    _poolLock?.Acquire();

                    try
                    {
                        foreach (var index in freed)
                        {
                            MakeFree(index);
                        }
                    }
                    finally
                    {
                        _poolLock?.Release();
                    }
                }

                lists = NewSweepLists();
                freed.Clear();
            }

            Interlocked.Add(ref liveTotal, live);
        }

        if (pool is null)
        {
            Worker();
        }
        else
        {
            pool.Run(Worker);
        }

        // Stops new assist claims; a claim already made completes inside its owner's
        // current allocation call (it publishes under the alloc lock either way)
        _sweepInFlight = false;
        _sweepPlan = null;

        return liveTotal;
    }

    /// <summary>Per-worker sweep accumulator: intrusive list heads/tails built over a
    /// disjoint region subset, spliced into the shared lists once per worker.</summary>
    private struct SweepLists
    {
        public int RecycledHead;
        public int RecycledTail;
        public fixed int ClassHeads[Region.ClassCount];
        public fixed int ClassTails[Region.ClassCount];
        public long Live;
        public long LinkedHoleBytes;
    }

    private static SweepLists NewSweepLists()
    {
        var lists = default(SweepLists);
        lists.RecycledHead = -1;
        lists.RecycledTail = -1;

        for (int c = 0; c < Region.ClassCount; c++)
        {
            lists.ClassHeads[c] = -1;
            lists.ClassTails[c] = -1;
        }

        return lists;
    }

    private void LinkRecycled(ref SweepLists lists, int index)
    {
        GetEntry(index)->NextRecycled = lists.RecycledHead;
        lists.RecycledHead = index;

        if (lists.RecycledTail < 0)
        {
            lists.RecycledTail = index;
        }
    }

    private void LinkClassList(ref SweepLists lists, int sizeClass, int index)
    {
        GetEntry(index)->NextInClassList = lists.ClassHeads[sizeClass];
        lists.ClassHeads[sizeClass] = index;

        if (lists.ClassTails[sizeClass] < 0)
        {
            lists.ClassTails[sizeClass] = index;
        }
    }

    /// <summary>Splices a sweep harvest into the reservoir. Callers hold the reservoir
    /// lock (concurrent sweep workers) or run under STW (the merge lock serializes
    /// parallel workers there).</summary>
    private void SpliceToReservoir(ref SweepLists lists)
    {
        if (lists.LinkedHoleBytes != 0)
        {
            Interlocked.Add(ref _linkedHoleBytes, lists.LinkedHoleBytes);
        }

        if (lists.RecycledHead >= 0)
        {
            GetEntry(lists.RecycledTail)->NextRecycled = _reservoirRecycledHead;
            _reservoirRecycledHead = lists.RecycledHead;
        }

        for (int c = 0; c < Region.ClassCount; c++)
        {
            if (lists.ClassHeads[c] >= 0)
            {
                GetEntry(lists.ClassTails[c])->NextInClassList = _reservoirClassHeads[c];
                _reservoirClassHeads[c] = lists.ClassHeads[c];
            }
        }
    }

    /// <summary>Splices an assist harvest into the caller's own shard (demand-driven:
    /// the assisting thread is about to consume it). Caller holds that shard's lock.</summary>
    private void SpliceSweepLists(ref SweepLists lists, int shard)
    {
        if (lists.LinkedHoleBytes != 0)
        {
            Interlocked.Add(ref _linkedHoleBytes, lists.LinkedHoleBytes);
        }

        var s = _shards[shard];

        if (lists.RecycledHead >= 0)
        {
            GetEntry(lists.RecycledTail)->NextRecycled = s.RecycledHead;
            s.RecycledHead = lists.RecycledHead;
        }

        for (int c = 0; c < Region.ClassCount; c++)
        {
            if (lists.ClassHeads[c] >= 0)
            {
                GetEntry(lists.ClassTails[c])->NextInClassList = s.ClassHeads[c];
                s.ClassHeads[c] = lists.ClassHeads[c];
            }
        }
    }

    /// <summary>
    /// <paramref name="deferredFrees"/> collects freed region indices instead of pushing
    /// them to the pool inline — the concurrent sweep publishes them under the alloc
    /// lock, because carves pop the pool while it runs (STW sweeps pass null).
    /// <paramref name="plan"/> is the pause-B in-use snapshot: with the world running,
    /// entries the plan skips can be mid-recarve by mutators and must not be read.
    /// </summary>
    private void SweepRange(int start, int end, bool youngOnly, ref SweepLists lists, List<int>? deferredFrees, int[]? plan)
    {
        for (int i = start; i < end; i++)
        {
            if (plan is not null && plan[i] < 0)
            {
                // Free/extension at pause B — either still free (a pool pop during the
                // sweep may be re-carving it right now) or covered by its span start
                continue;
            }

            var entry = GetEntry(i);

            if (youngOnly && entry->Age == RegionAge.Old)
            {
                // Old region: metadata-only relink, never touch object memory. LiveBytes is
                // left stale — it is only meaningful to a full sweep, and every full mark
                // starts from ResetLiveBytes.
                switch (entry->Kind)
                {
                    case RegionKind.Bump:
                        if (entry->FirstHole != 0)
                        {
                            LinkRecycled(ref lists, i);
                            lists.LinkedHoleBytes += entry->HoleBytes;
                        }
                        break;

                    case RegionKind.SizeClass:
                        if ((entry->AllocatedBlocks & FullMask(entry->SizeClass)) != FullMask(entry->SizeClass))
                        {
                            LinkClassList(ref lists, entry->SizeClass, i);
                        }
                        break;
                }

                continue;
            }

            switch (entry->Kind)
            {
                case RegionKind.Bump:
                    if (entry->LiveBytes == 0 && (!youngOnly || entry->Age == RegionAge.Fresh))
                    {
                        // The wholesale-recycle path: no per-object work (SPEC-M2 §7.1).
                        // Gated to Fresh in young mode: a Reopened region's LiveBytes only
                        // counts this cycle's marks, not its sticky-marked old survivors.
                        RecycleBumpRegion(i, deferredFrees);
                    }
                    else
                    {
                        lists.Live += entry->LiveBytes;
                        SweepBumpRegion(i, ref lists);
                    }
                    break;

                case RegionKind.SizeClass:
                    lists.Live += entry->LiveBytes;
                    SweepSizeClassRegion(i, ref lists, deferredFrees);
                    break;

                case RegionKind.SpanStart:
                    if (entry->LiveBytes == 0)
                    {
                        RecycleSpan(i, deferredFrees);
                    }
                    else
                    {
                        lists.Live += entry->LiveBytes;
                        entry->LiveBytes = 0;
                        entry->Age = RegionAge.Old;
                    }
                    break;
            }
        }
    }

    /// <summary>
    /// Clears every mark bit below the frontier. Full collections start here (this
    /// replaces the M4 epoch advance, with no wrap case): sticky young marks accumulate
    /// in the bitmap until the next full mark rebuilds liveness from scratch. Runs under
    /// STW; ~1.5 ms per committed GB, the price of the side-bitmap representation.
    /// </summary>
    public void ClearMarks()
    {
        // NT stores (M7): the bitmap is bigger than any cache level on the heaps where
        // this matters, and marking rewrites it scattered either way
        Zeroing.Clear((nint)_markBitmap, (nint)_frontier << 15);
    }

    /// <summary>Clears one region's 32 KB bitmap slice at carve: sticky marks from the
    /// region's previous life would otherwise resurrect dead objects (the bitmap is only
    /// cleared wholesale by full collections).</summary>
    private void ClearRegionMarks(int index)
    {
        // Runs under the allocation lock (pool carves): the NT path keeps the 32 KB
        // slice from evicting the carve's hot metadata on its way through
        Zeroing.Clear((nint)_markBitmap + ((nint)index << 15), 1 << 15);
    }

    /// <summary>
    /// Zeroes every region's mark-time accumulator. Young collections credit LiveBytes to
    /// old regions (hole-carved young objects) without a sweep ever consuming it, so every
    /// full mark must start from a clean slate.
    /// </summary>
    public void ResetLiveBytes()
    {
        for (int i = 0; i < _frontier; i++)
        {
            GetEntry(i)->LiveBytes = 0;
        }
    }

    private void RecycleBumpRegion(int index, List<int>? deferredFrees)
    {
        // Zero-at-carve (M4): the region returns to the pool with its dead contents in
        // place; the next carve zeroes the windows it hands out. The racy reads are
        // benign on the concurrent path: a planned region can never be a shard's active
        // bump mid-sweep (actives were abandoned at BeginConcurrentSweep and pool pops
        // skip planned regions), so a match only ever happens under STW.
        foreach (var shard in _shards)
        {
            if (shard.ActiveBump == index)
            {
                shard.ActiveBump = -1;
            }
        }

        MakeFree(index, deferredFrees);
    }

    private void SweepBumpRegion(int index, ref SweepLists lists)
    {
        var entry = GetEntry(index);

        entry->FirstHole = 0;
        entry->HoleBytes = 0;
        entry->NextRecycled = -1;

        // Every linked hole that inherits its zeroed marker keeps the region's flag
        // alive (M8.3); only holes covering freshly dead bytes reset it — the sweep no
        // longer forfeits the zeroer's work on regions where nothing new died
        var allLinkedHolesZero = true;

        var regionBase = RegionBase(index);
        var end = entry->Cursor;

        // Walk survivors straight off the mark bitmap: set bits are exactly the marked
        // object starts and dead extents are the gaps between them, so the ComputeSize
        // hop through every corpse — the dominant sweep cost at smear density, where a
        // region holds ~20 survivors among ~1000 dead — never happens. Free plugs are
        // never marked, so they coalesce into the gaps for free, exactly like the old
        // object walk. Safe on the concurrent path for the same reason the old walk
        // was: marks are final and planned regions are unreachable to allocation.
        var bitmap = GCObject.MarkBitmap;
        var heapBase = GCObject.MarkHeapBase;

        var w = (long)(regionBase - heapBase) >> 9;
        var endBit = (long)(end - heapBase) >> 3;
        var wEnd = (endBit + 63) >> 6;

        // Pre-header slot of the first unswept byte; a dead run's extent starts at the
        // slot before its first object ref (SPEC-M2 §3)
        var deadStart = regionBase;

        for (; w < wEnd; w++)
        {
            var word = bitmap[w];

            if (word == 0)
            {
                // Survivors are sparse at smear density; the whole dead gap between two
                // of them is zero words the vector skip crosses without touching plugs
                w = GCObject.SkipZeroBitmapWords(bitmap, w + 1, wEnd) - 1;
                continue;
            }

            while (word != 0)
            {
                var bit = BitOperations.TrailingZeroCount(word);
                word &= word - 1;

                var bitIndex = (w << 6) + bit;

                if (bitIndex >= endBit)
                {
                    break;
                }

                var ptr = heapBase + (nint)(bitIndex << 3);
                var next = Align(ptr + (nint)((GCObject*)ptr)->ComputeSize());

                if (ptr - IntPtr.Size > deadStart)
                {
                    // The extent ends at the live object's pre-header slot
                    ClosePlug(entry, index, deadStart, ptr - IntPtr.Size, ref allLinkedHolesZero);
                }

                deadStart = next - IntPtr.Size;
            }
        }

        // Tail: when the last survivor ends at the cursor, only its successor's
        // never-materialized pre-header slot remains — not a dead extent
        if (end - deadStart > IntPtr.Size)
        {
            ClosePlug(entry, index, deadStart, end, ref allLinkedHolesZero);
        }

        if (allLinkedHolesZero)
        {
            entry->BumpFlags |= RegionEntry.HolesZeroedFlag;
        }
        else
        {
            entry->BumpFlags &= unchecked((byte)~RegionEntry.HolesZeroedFlag);
        }

        if (entry->FirstHole != 0)
        {
            LinkRecycled(ref lists, index);
            lists.LinkedHoleBytes += entry->HoleBytes;
        }

        entry->LiveBytes = 0;
        entry->Age = RegionAge.Old;
    }

    /// <summary>
    /// Writes the free-object plug over a dead extent [start, end) and links it as a
    /// carveable hole when big enough (SPEC-M2 §7.1). Zero-at-carve (M4): the extent's
    /// stale contents stay in place — whoever carves a window out of the hole zeroes
    /// exactly the bytes handed out, outside the pause. The extent's mark bits are clear
    /// by construction: everything under it just swept as dead.
    /// </summary>
    private void ClosePlug(RegionEntry* entry, int regionIndex, nint start, nint end, ref bool allLinkedHolesZero)
    {
        var extent = end - start;

        var plug = (GCObject*)(start + IntPtr.Size);

        // Inherit the zeroed-body marker (M8.3) when this extent is byte-identical to
        // the marked plug it re-covers — same start (the free MT sits exactly at
        // start+8) and same length. Nothing writes a linked hole's body between sweeps
        // (zero-at-carve consumers unlink and re-plug, changing the extent), and any
        // new death coalesces into a different extent, so an exact match means the
        // body is still zero. The read precedes the overwrite below.
        var stillZero = HoleMarkerInheritance
            && extent >= 4 * IntPtr.Size
            && plug->RawMethodTable == _freeObjectMethodTable
            && plug->Length == (uint)(extent - 3 * IntPtr.Size)
            && HoleMarker(start + IntPtr.Size) == ZeroedHoleMarker;

        plug->RawMethodTable = _freeObjectMethodTable;
        plug->Length = (uint)(extent - 3 * IntPtr.Size);
        SetHoleMarker(start + IntPtr.Size, stillZero ? ZeroedHoleMarker : 0);

        if (extent >= _minLinkedHole)
        {
            // The link lives in the plug's dead body, at ref + 16 (SPEC-M2 §4.4)
            *(nint*)(start + 3 * IntPtr.Size) = entry->FirstHole;
            entry->FirstHole = start + IntPtr.Size;
            entry->HoleBytes += (int)extent;

            if (!stillZero)
            {
                allLinkedHolesZero = false;
            }
        }
    }

    private void SweepSizeClassRegion(int index, ref SweepLists lists, List<int>? deferredFrees)
    {
        var entry = GetEntry(index);
        var regionBase = RegionBase(index);
        var sizeClass = entry->SizeClass;
        var classSize = Region.ClassSizes[sizeClass];

        var bitmap = entry->AllocatedBlocks;

        while (bitmap != 0)
        {
            var bit = BitOperations.TrailingZeroCount(bitmap);
            bitmap &= bitmap - 1;

            var obj = (GCObject*)(regionBase + (nint)bit * classSize + IntPtr.Size);

            if (!obj->IsMarked())
            {
                // Zero-at-carve (M4): the dead block keeps its stale contents (its
                // DirtyBlocks bit is already set); nothing reads a block whose
                // AllocatedBlocks bit is clear
                entry->AllocatedBlocks &= ~(1ul << bit);
            }
        }

        entry->LiveBytes = 0;
        entry->Age = RegionAge.Old;

        var fullMask = FullMask(sizeClass);

        if (entry->AllocatedBlocks == 0)
        {
            MakeFree(index, deferredFrees); // stale contents stay; the next carve flags it dirty
        }
        else if ((entry->AllocatedBlocks & fullMask) != fullMask)
        {
            LinkClassList(ref lists, sizeClass, index);
        }
        else
        {
            entry->NextInClassList = -1;
        }
    }

    private void RecycleSpan(int index, List<int>? deferredFrees)
    {
        var entry = GetEntry(index);
        var count = entry->SpanCount;
        var spanBase = RegionBase(index);
        var spanBytes = (nint)count << Region.Shift;

        // Big spans: decommit — the OS re-zeroes lazily. Small spans stay committed with
        // their dead contents; the next carve zeroes what it hands out (M4 zero-at-carve).
        // Safe with the world running too: the span was dead at pause B, so no mutator
        // holds a reference into it, and the entries stay unreachable until MakeFree.
        if (count >= 4 && _memory.Decommit(spanBase, spanBytes))
        {
            Interlocked.Add(ref _committedRegionBytes, -spanBytes); // parallel sweep workers race here

            for (int i = 0; i < count; i++)
            {
                GetEntry(index + i)->IsCommitted = RegionEntry.CommitNone;
            }
        }

        for (int i = 0; i < count; i++)
        {
            MakeFree(index + i, deferredFrees);
        }
    }

    private void MakeFree(int index, List<int>? deferredFrees = null)
    {
        if (deferredFrees is not null)
        {
            // Concurrent sweep (M6.5): carves pop the pool while the sweep runs, so
            // the push happens later, under the publish (alloc) lock
            deferredFrees.Add(index);
            return;
        }

        var entry = GetEntry(index);
        entry->Kind = RegionKind.Free;
        entry->LiveBytes = 0;

        // The offset-8 union held a Cursor/bitmap from the region's last life; stale bits
        // there would make the span run scan treat the region as checked out forever
        entry->ZeroerCheckedOut = 0;

        // Lock-free push: parallel sweep workers free regions concurrently. STW sweeps
        // run with allocation stopped and the zeroer gated, so pushes only race with
        // each other; the concurrent sweep reaches here holding the pool lock instead.
        var slot = Interlocked.Increment(ref _poolCount) - 1;
        _pool[slot] = index;

        if (entry->IsCommitted != RegionEntry.CommitNone)
        {
            Interlocked.Add(ref _pooledCommittedBytes, Region.Size);
        }
    }

    /// <summary>The background zeroer's exclusion against STW pool mutation (M7): the
    /// collecting thread holds this for the whole suspension (SuspendEE does not park the
    /// zeroer — it is not an EE thread), and the zeroer holds it around every checkout and
    /// checkin. Both sides do bounded work under it.</summary>
    public Lock ZeroerGate { get; } = new();

    private bool _collectorWaitingForGate;

    /// <summary>True while a collection is trying to enter (or holds) the gate. The zeroer
    /// polls this and stands down instead of re-entering — an unfair lock could otherwise
    /// let a busy zeroer starve the pause start; the post-collection kick resumes it.</summary>
    public bool CollectorWaitingForGate => Volatile.Read(ref _collectorWaitingForGate);

    public void EnterGateForCollection()
    {
        Volatile.Write(ref _collectorWaitingForGate, true);
        ZeroerGate.Enter();
    }

    public void ExitGateForCollection()
    {
        Volatile.Write(ref _collectorWaitingForGate, false);
        ZeroerGate.Exit();
    }

    /// <summary>
    /// Removes the topmost committed-dirty pool region and flags it checked out (M7): still
    /// Kind Free, but owned by the zeroer — carves cannot pop it and the span run scan
    /// skips it. Refuses when the pool is nearly empty or the heap is under memory
    /// pressure, so checkouts never push an allocation to the frontier (or to a spurious
    /// OOM collection) that the pool could have served. Caller must hold
    /// <see cref="ZeroerGate"/> and the pool lock.
    /// </summary>
    public bool TryCheckOutDirtyRegion(out int index, out nint regionBase)
    {
        index = 0;
        regionBase = 0;

        if (_poolCount <= 8 || UnderMemoryPressure)
        {
            return false;
        }

        // Top-down: LIFO carves consume the top first, so zeroing there pays off soonest;
        // the pre-zeroed prefix the zeroer builds up is skipped on each pass. The float
        // is CAPPED (M8.3): once the hole pass stopped eating every kick, an unbounded
        // pool pass zeroed the whole pool each cycle — touching (and keeping resident)
        // pages the trim wants cold, for regions often decommitted before any carve.
        // Pops consume the zeroed prefix, so the float refills exactly with demand.
        var skipped = 0;

        for (int i = _poolCount - 1; i >= 0; i--)
        {
            var entry = GetEntry(_pool[i]);

            if (entry->IsCommitted == RegionEntry.CommitDirty)
            {
                index = _pool[i];
                _pool[i] = _pool[--_poolCount];
                entry->ZeroerCheckedOut = 1;
                Interlocked.Add(ref _pooledCommittedBytes, -Region.Size);
                regionBase = RegionBase(index);
                return true;
            }

            if (++skipped >= ZeroedPoolFloat)
            {
                return false; // the next ZeroedPoolFloat carves are already covered
            }
        }

        return false;
    }

    /// <summary>Cap on the pre-zeroed prefix the background zeroer maintains at the pool
    /// top (M8.3): 32 regions = 64 MB of carve runway per collection cycle.</summary>
    private const int ZeroedPoolFloat = 32;

    /// <summary>Returns a checked-out region to the pool top, pre-zeroed: the next carve
    /// hands its memory out with no zeroing debt. Caller must hold <see cref="ZeroerGate"/>
    /// and the pool lock.</summary>
    public void CheckInZeroedRegion(int index)
    {
        var entry = GetEntry(index);
        entry->ZeroerCheckedOut = 0;
        entry->IsCommitted = RegionEntry.CommitZeroed;
        _pool[_poolCount++] = index;
        Interlocked.Add(ref _pooledCommittedBytes, Region.Size);
    }

    /// <summary>
    /// Unlinks the first reservoir region whose hole bodies are still dirty (M7): out of
    /// the reservoir, its holes are unreachable to carves, while window carves of its
    /// virgin tail touch disjoint memory and fields. Shards' private lists are not
    /// walked: supply passes through the reservoir, and privates hold at most a pull
    /// batch each. Unlike pool checkouts this must not straddle a collection — the sweep
    /// rebuilds the recycled lists and walks hole memory — so the zeroer holds
    /// <see cref="ZeroerGate"/> from here through <see cref="CheckInZeroedHoleRegion"/>.
    /// Caller must also hold the reservoir lock.
    /// </summary>
    public bool TryCheckOutDirtyHoleRegion(out int index)
    {
        var previous = -1;

        for (index = _reservoirRecycledHead; index >= 0; index = GetEntry(index)->NextRecycled)
        {
            var entry = GetEntry(index);

            if ((entry->BumpFlags & RegionEntry.HolesZeroedFlag) == 0)
            {
                if (previous < 0)
                {
                    _reservoirRecycledHead = entry->NextRecycled;
                }
                else
                {
                    GetEntry(previous)->NextRecycled = entry->NextRecycled;
                }

                entry->NextRecycled = -1;
                return true;
            }

            previous = index;
        }

        return false;
    }

    /// <summary>Zeroes every linked hole's body in a checked-out region, preserving the
    /// 32-byte prefix each carve expects: preheader slot, plug header and next-hole link.
    /// Called with <see cref="ZeroerGate"/> held but not the allocation lock — the list is
    /// frozen (unlinked from carves, and no sweep can start while the gate is held).</summary>
    public void ZeroCheckedOutHoleBodies(int index)
    {
        for (var holeRef = GetEntry(index)->FirstHole; holeRef != 0; holeRef = *(nint*)(holeRef + 2 * IntPtr.Size))
        {
            if (HoleMarker(holeRef) == ZeroedHoleMarker)
            {
                // Still zero from a previous pass (M8.3): the sweep only resets markers
                // on holes covering freshly dead bytes
                continue;
            }

            // Hole extent is [ref - 8, ref + 16 + Length); the body starts after the link
            ZeroMemory(holeRef + 3 * IntPtr.Size, (nint)((GCObject*)holeRef)->Length - IntPtr.Size);
            SetHoleMarker(holeRef, ZeroedHoleMarker);
        }
    }

    /// <summary>Relinks a checked-out hole region at the reservoir head with its holes
    /// flagged pre-zeroed. Caller must hold <see cref="ZeroerGate"/> (continuously since
    /// the checkout) and the reservoir lock.</summary>
    public void CheckInZeroedHoleRegion(int index)
    {
        var entry = GetEntry(index);
        entry->BumpFlags |= RegionEntry.HolesZeroedFlag;
        entry->NextRecycled = _reservoirRecycledHead;
        _reservoirRecycledHead = index;
    }

    /// <summary>
    /// One bounded batch of the pool trim: decommits the oldest (coldest) pool entries
    /// first until committed slack reaches the target (SPEC-M2 §7.4, retargeted by M4).
    /// The caller passes the post-collection allocation budget: young collections recycle
    /// a whole budget's worth of regions every cycle, and trimming below the next cycle's
    /// demand just converts the slack into decommit/recommit churn.
    ///
    /// Batched under the pool lock with the world running (M6 stage 3: the whole-pool
    /// trim measured ~46 ms inside pause B on the ASP.NET soak, and decommitting free
    /// regions needs no stopped world — only the allocator's lock
    /// discipline). Pops and zeroer checkouts between batches can shuffle entries past
    /// the caller's cursor; that only under-trims, and the next collection's trim picks
    /// up the remainder. When the pool is already at target the batch is a single
    /// comparison.
    /// </summary>
    /// <summary>Diagnostics snapshot for the per-collection stats row (M7 memory work):
    /// where every committed region byte sits. Read with the world running — approximate
    /// by design, like the committed counter on the same row.</summary>
    public struct RegionCensus
    {
        public int BumpFresh, BumpReopened, BumpOld; // in-use bump regions by sticky age
        public long LinkedHoleBytes;                 // carveable (≥ floor) hole bytes
        public long BumpTailBytes;                   // never-carved space above each cursor
        public int ClassRegions;
        public long ClassFreeBytes;                  // free blocks in size-class regions
        public int SpanRegions;                      // includes extensions
        public long PooledCommittedBytes;
    }

    public RegionCensus TakeCensus()
    {
        var census = new RegionCensus { PooledCommittedBytes = Volatile.Read(ref _pooledCommittedBytes) };

        for (int i = 0; i < _frontier; i++)
        {
            var entry = GetEntry(i);

            switch (entry->Kind)
            {
                case RegionKind.Bump:
                    switch (entry->Age)
                    {
                        case RegionAge.Fresh: census.BumpFresh++; break;
                        case RegionAge.Reopened: census.BumpReopened++; break;
                        default: census.BumpOld++; break;
                    }

                    census.LinkedHoleBytes += entry->HoleBytes;
                    census.BumpTailBytes += RegionBase(i) + Region.Size - Region.GuardBytes - entry->Cursor;
                    break;

                case RegionKind.SizeClass:
                    census.ClassRegions++;
                    var freeBlocks = BlockCount(entry->SizeClass)
                        - BitOperations.PopCount(entry->AllocatedBlocks & FullMask(entry->SizeClass));
                    census.ClassFreeBytes += (long)freeBlocks * Region.ClassSizes[entry->SizeClass];
                    break;

                case RegionKind.SpanStart:
                    census.SpanRegions += entry->SpanCount;
                    break;
            }
        }

        return census;
    }

    /// <returns>true while entries above target remain (call again for the next batch).
    /// Each batch holds the pool lock internally (M7 sharded supply).</returns>
    public bool TrimPoolBatch(long target, int maxDecommits, ref int cursor)
    {
        _poolLock?.Acquire();

        try
        {
            var done = 0;

            for (; cursor < _poolCount && Volatile.Read(ref _pooledCommittedBytes) > target; cursor++)
            {
                if (done == maxDecommits)
                {
                    return true;
                }

                var entry = GetEntry(_pool[cursor]);

                if (entry->IsCommitted != RegionEntry.CommitNone && _memory.Decommit(RegionBase(_pool[cursor]), Region.Size))
                {
                    entry->IsCommitted = RegionEntry.CommitNone;
                    Interlocked.Add(ref _pooledCommittedBytes, -Region.Size);
                    Interlocked.Add(ref _committedRegionBytes, -Region.Size);
                    done++;
                }
            }

            return false;
        }
        finally
        {
            _poolLock?.Release();
        }
    }

    /// <summary>Zeroes a recycled window or block extent before first use (M4
    /// zero-at-carve). Called by the allocation path after releasing the allocation lock:
    /// the memory is private to the requesting thread by then, and concurrent carves zero
    /// in parallel. Honors <see cref="Zeroing.CarveTemporal"/> — the caller allocates
    /// from this memory next.</summary>
    public static void ZeroWindow(nint window, nint length) => ZeroMemory(window, length, carve: true);

    /// <summary>Zeroing for the background zeroer: always non-temporal — nothing reads
    /// this memory soon, so cache-bypassing stores are strictly right here.</summary>
    public static void ZeroBackground(nint start, nint length) => ZeroMemory(start, length);

    /// <summary>
    /// Zeroes the stale members of a just-carved span, called by the allocating thread
    /// after releasing the allocation lock — the span is private to that thread until the
    /// EE publishes the object, and the EE cannot suspend it before then (same envelope
    /// as window zero-at-carve). Only the object's extent is zeroed: the tail of the last
    /// region is never handed out, and freshly committed members are OS-zeroed already.
    /// </summary>
    public void ZeroSpanCarve(nint spanBase, nint objectSize)
    {
        TryGetIndex(spanBase, out var start);

        var count = GetEntry(start)->SpanCount;
        var end = spanBase + IntPtr.Size + Align(objectSize);

        for (int i = 0; i < count; i++)
        {
            var entry = GetEntry(start + i);

            if (entry->SpanIsDirty == 0)
            {
                continue;
            }

            entry->SpanIsDirty = 0;

            var regionBase = RegionBase(start + i);
            var zeroEnd = Math.Min(regionBase + Region.Size, end);

            if (zeroEnd > regionBase)
            {
                ZeroMemory(regionBase, zeroEnd - regionBase);
            }
        }
    }

    private static void ZeroMemory(nint start, nint length, bool carve = false)
    {
        var tStart = GcStats.Timestamp();

        if (carve)
        {
            Zeroing.ClearCarve(start, length);
        }
        else
        {
            Zeroing.Clear(start, length);
        }

        if (GcStats.Enabled)
        {
            // Callers include the background zeroer and post-lock window zeroing
            Interlocked.Add(ref GcStats.ZeroBytes, length);
            Interlocked.Add(ref GcStats.ZeroTicks, GcStats.Timestamp() - tStart);
        }
    }

    private bool EnsureTableCommitted(int requiredEntries)
    {
        if (!EnsureCardTableCommitted(requiredEntries))
        {
            return false;
        }

        if (!EnsureCommitted(ref _markBitmapCommittedEnd, (nint)_markBitmap + ((nint)requiredEntries << 15)))
        {
            return false;
        }

        var requiredEnd = (nint)(_table + requiredEntries);

        if (requiredEnd <= _tableCommittedEnd)
        {
            return true;
        }

        var pageSize = (nint)Environment.SystemPageSize;
        var alignedEnd = (requiredEnd + pageSize - 1) & ~(pageSize - 1);

        if (!NativeAllocator.OsCommit(_tableCommittedEnd, alignedEnd - _tableCommittedEnd))
        {
            return false;
        }

        _tableCommittedEnd = alignedEnd;
        return true;
    }

    /// <summary>
    /// Commits the card bytes covering every region below the new frontier: one card byte
    /// per 2 KB of heap, so one region needs 1 KB of cards. Pooled regions recommitted later
    /// are always below the frontier and therefore already covered.
    /// </summary>
    private bool EnsureCardTableCommitted(int requiredEntries)
    {
        if (_cardTableStorage == 0)
        {
            return true; // tests drive the allocator without a card table
        }

        var requiredEnd = _cardTableStorage + ((nint)requiredEntries << (Region.Shift - 11));

        if (requiredEnd <= _cardTableCommittedEnd)
        {
            return true;
        }

        var pageSize = (nint)Environment.SystemPageSize;
        var alignedEnd = (requiredEnd + pageSize - 1) & ~(pageSize - 1);

        if (!NativeAllocator.OsCommit(_cardTableCommittedEnd, alignedEnd - _cardTableCommittedEnd))
        {
            return false;
        }

        _cardTableCommittedEnd = alignedEnd;
        return true;
    }

    private static bool EnsureCommitted(ref nint committedEnd, nint requiredEnd)
    {
        if (requiredEnd <= committedEnd)
        {
            return true;
        }

        var pageSize = (nint)Environment.SystemPageSize;
        var alignedEnd = (requiredEnd + pageSize - 1) & ~(pageSize - 1);

        if (!NativeAllocator.OsCommit(committedEnd, alignedEnd - committedEnd))
        {
            return false;
        }

        committedEnd = alignedEnd;
        return true;
    }

    private static nint Align(nint address) => (address + (IntPtr.Size - 1)) & ~(IntPtr.Size - 1);
}
