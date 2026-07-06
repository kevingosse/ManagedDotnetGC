using System.Numerics;

namespace ManagedDotnetGC;

/// <summary>
/// The region heap (SPEC-M2 §2-§4): a flat table of 2 MB regions carved from one 2 TB
/// reservation. Not thread-safe by itself — every mutating call runs either under the
/// GCHeap allocation lock or during a suspension (sweep).
/// </summary>
internal unsafe class RegionAllocator : IDisposable
{
    private readonly NativeAllocator _memory;
    private readonly RegionEntry* _table;
    private nint _tableCommittedEnd;

    // LIFO stack of free region indices. Committed members hold stale recycled contents
    // (M4 zero-at-carve): every tier zeroes what it hands out, outside the alloc lock
    private readonly int* _pool;
    private int _poolCount;

    // Next never-carved region index; the frontier only grows
    private int _frontier;

    // Active fresh bump region being carved into windows, -1 if none
    private int _activeBump = -1;

    // Head of the intrusive list of regions with free blocks, per size class
    private readonly int[] _classHeads = new int[Region.ClassCount];

    // Head of the intrusive list of bump regions with linked holes (via NextRecycled)
    private int _recycledHead = -1;

    // Set once at startup, needed to write plugs when carving holes and sweeping
    private MethodTable* _freeObjectMethodTable;

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

    // Assist scratch: only touched under the allocation lock
    private readonly List<int> _assistFreed = new();

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

        _classHeads.AsSpan().Fill(-1);

        // Mark operations live on GCObject but the storage is ours: one heap per process
        // in production; the unit tests create allocators sequentially
        GCObject.MarkBitmap = _markBitmap;
        GCObject.MarkHeapBase = memory.LowestAddress;
    }

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

    /// <summary>
    /// Hands out a bump window of at least Align(size) + 24 bytes (SPEC-M2 §3-§4.1).
    /// When <paramref name="needsZero"/> is true the window holds recycled garbage and the
    /// caller must zero it before use — after releasing the allocation lock, since the
    /// window is private to the caller from this point (M4 zero-at-carve).
    /// </summary>
    public bool TryGetWindow(nint size, out nint window, out nint length, out bool needsZero)
    {
        var needed = Align(size) + 3 * IntPtr.Size;

        // Hole-first policy (SPEC-M2 §4.1): reuse swept holes before touching fresh memory
        if (TryCarveFromHoles(needed, out window, out length, out needsZero))
        {
            if (GcStats.Enabled)
            {
                GcStats.HoleWindowCount++;

                if (!needsZero)
                {
                    GcStats.CleanWindowCount++;
                }
            }

            return true;
        }

        while (true)
        {
            if (_activeBump >= 0)
            {
                var entry = GetEntry(_activeBump);
                var dataEnd = RegionBase(_activeBump) + Region.Size - Region.GuardBytes;
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
                        GcStats.CleanWindowCount++;
                    }

                    return true;
                }

                // Abandon the tail: it is below every walk bound, so it needs no plug; it
                // is reclaimed when the region dies (SPEC-M2 §4.1)
                _activeBump = -1;
            }

            // Dry supply during a concurrent sweep: drain the plan instead of
            // fresh-committing, then retry holes first — on smear heaps the assist
            // publishes holes, not pool regions
            if (_poolCount == 0 && TrySweepAssist())
            {
                if (TryCarveFromHoles(needed, out window, out length, out needsZero))
                {
                    if (GcStats.Enabled)
                    {
                        GcStats.HoleWindowCount++;

                        if (!needsZero)
                        {
                            GcStats.CleanWindowCount++;
                        }
                    }

                    return true;
                }

                continue;
            }

            if (!TryCarveRegion(out var index, out var dirty))
            {
                window = 0;
                length = 0;
                needsZero = false;
                return false;
            }

            var fresh = GetEntry(index);
            fresh->Kind = RegionKind.Bump;
            fresh->BumpFlags = dirty ? RegionEntry.BumpDirtyFlag : (byte)0;
            fresh->Age = RegionAge.Fresh;
            fresh->LiveBytes = 0;
            fresh->Cursor = RegionBase(index);
            fresh->FirstHole = 0;
            fresh->HoleBytes = 0;
            fresh->NextRecycled = -1;

            _activeBump = index;
        }
    }

    /// <summary>Hole-only carve for stash refills (M7): redistributes committed supply
    /// without deepening a drought — no active-bump, assist, pool or frontier
    /// fallthrough, so a dry hole list just means no refill. (The first stash shipped
    /// refills through the full TryGetWindow and two of three lohmix runs ratcheted
    /// +0.5 GB: 4 fresh 128 KB carves per acquisition outran the concurrent sweep's
    /// publications exactly like the pre-assist M6.5 gate story.)</summary>
    public bool TryGetStashWindow(nint size, out nint window, out nint length, out bool needsZero)
    {
        var needed = Align(size) + 3 * IntPtr.Size;

        if (!TryCarveFromHoles(needed, out window, out length, out needsZero))
        {
            return false;
        }

        if (GcStats.Enabled)
        {
            GcStats.HoleWindowCount++;

            if (!needsZero)
            {
                GcStats.CleanWindowCount++;
            }
        }

        return true;
    }

    /// <summary>
    /// Carves a window from the first hole that fits, per SPEC-M2 §4.4. A hole is a linked
    /// free-object plug in a swept bump region: extent [ref - 8, ref + 16 + Length).
    /// When the background zeroer pre-zeroed the region's hole bodies (M7), only the
    /// 32-byte plug header + link prefix is stale — cleaned right here — and the window
    /// goes out with no zeroing debt.
    /// </summary>
    private bool TryCarveFromHoles(nint needed, out nint window, out nint length, out bool needsZero)
    {
        window = 0;
        length = 0;
        needsZero = true;

        var regionIndex = _recycledHead;
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
                    _linkedHoleBytes -= extent;

                    if (length < extent)
                    {
                        // Re-plug the remainder; its stale bytes stay (zero-at-carve), and
                        // its mark bits are clear by construction (hole extents are dead)
                        var remainderStart = window + length;
                        var remainder = (GCObject*)(remainderStart + IntPtr.Size);
                        remainder->RawMethodTable = _freeObjectMethodTable;
                        remainder->Length = (uint)(extent - length - 3 * IntPtr.Size);

                        if (extent - length >= _minLinkedHole)
                        {
                            *(nint*)(remainderStart + 3 * IntPtr.Size) = entry->FirstHole;
                            entry->FirstHole = remainderStart + IntPtr.Size;
                            entry->HoleBytes += (int)(extent - length);
                            _linkedHoleBytes += extent - length;
                        }
                        // else: a sub-window remainder floats until the next full collection
                    }

                    if ((entry->BumpFlags & RegionEntry.HolesZeroedFlag) != 0)
                    {
                        // Pre-zeroed hole body (M7): only the plug's preheader, header and
                        // link bytes at the window start are stale
                        new Span<byte>((void*)window, 4 * IntPtr.Size).Clear();
                        needsZero = false;
                    }

                    if (entry->FirstHole == 0)
                    {
                        // No holes left: unlink the region from the recycled list
                        if (previousRegion < 0)
                        {
                            _recycledHead = entry->NextRecycled;
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

    /// <summary>
    /// Allocates one block from a size-class region (SPEC-M2 §4.2). When
    /// <paramref name="needsZero"/> is true the block holds stale recycled contents and the
    /// caller must zero the object extent before use — after releasing the allocation lock,
    /// since the block is private to the caller from this point (M4 zero-at-carve).
    /// </summary>
    public bool TryAllocBlock(int sizeClass, out nint block, out bool needsZero)
    {
        var head = _classHeads[sizeClass];

        // Dry class list during a concurrent sweep: the plan may hold partially-free
        // regions of this class — drain it before carving a fresh region
        while (head < 0 && _poolCount == 0 && TrySweepAssist())
        {
            head = _classHeads[sizeClass];
        }

        if (head < 0)
        {
            if (!TryCarveRegion(out var index, out var dirty))
            {
                block = 0;
                needsZero = false;
                return false;
            }

            var fresh = GetEntry(index);
            fresh->Kind = RegionKind.SizeClass;
            fresh->Age = RegionAge.Fresh;
            fresh->SizeClass = (byte)sizeClass;
            fresh->LiveBytes = 0;
            fresh->AllocatedBlocks = 0;
            fresh->DirtyBlocks = dirty ? ulong.MaxValue : 0;
            fresh->NextInClassList = -1;

            _classHeads[sizeClass] = index;
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
            _classHeads[sizeClass] = entry->NextInClassList;
            entry->NextInClassList = -1;
        }

        block = RegionBase(head) + (nint)i * Region.ClassSizes[sizeClass];
        return true;
    }

    /// <summary>Safe accessor for iterating a size-class region's allocated blocks.</summary>
    public (ulong bitmap, int classSize) GetSizeClassInfo(int index)
    {
        var entry = GetEntry(index);
        return (entry->AllocatedBlocks, Region.ClassSizes[entry->SizeClass]);
    }

    /// <summary>
    /// Allocates a span of contiguous regions for a single large object (SPEC-M2 §4.3):
    /// first a contiguous run of pooled free regions, else the frontier (contiguous by
    /// construction). Recycled members come back with stale contents and their
    /// <see cref="RegionEntry.SpanIsDirty"/> set — the caller zeroes the object extent
    /// via <see cref="ZeroSpanCarve"/> *after releasing the allocation lock* (M4
    /// zero-at-carve, extended to spans).
    /// </summary>
    public bool TryAllocSpan(int count, out nint spanBase)
    {
        spanBase = 0;
        int start;

        if (count == 1)
        {
            // The common case (spans ≤ 2 MB): any one free region works, so take the
            // pool's LIFO top instead of scanning the table for a run. A dry pool
            // during a concurrent sweep drains the plan first (dead spans it frees
            // land in the pool) instead of committing past the frontier.
            while (_poolCount == 0 && TrySweepAssist())
            {
            }

            if (!TryCarveRegion(out start, out var dirty))
            {
                return false;
            }

            GetEntry(start)->SpanIsDirty = dirty ? (byte)1 : (byte)0;
        }
        else if (TryTakeFreeRunWithAssist(count, out start))
        {
            // Only the start region can hold the span object's mark bit; extension
            // slices are never consulted (no object starts there)
            ClearRegionMarks(start);
        }
        else
        {
            if (_frontier + count > Region.Count || !EnsureTableCommitted(_frontier + count))
            {
                return false;
            }

            start = _frontier;

            if (!CanCommit((nint)count << Region.Shift) || !_memory.TryCommit(RegionBase(start), (nint)count << Region.Shift))
            {
                return false;
            }

            _committedRegionBytes += (long)count << Region.Shift;
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
    private bool TryTakeFreeRunWithAssist(int count, out int start)
    {
        while (true)
        {
            if (TryTakeFreeRun(count, out start))
            {
                return true;
            }

            // Multi-region spans: drain the sweep plan hunting for a contiguous free
            // run before committing fresh regions past the frontier
            if (!TrySweepAssist())
            {
                return false;
            }
        }
    }

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

                _committedRegionBytes += Region.Size;
                _pooledCommittedBytes += Region.Size;
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
            _pooledCommittedBytes -= Region.Size;
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

    private bool TryCarveRegion(out int index, out bool dirty)
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
                _pooledCommittedBytes -= Region.Size;
            }
            else
            {
                if (!CanCommit(Region.Size) || !_memory.TryCommit(RegionBase(index), Region.Size))
                {
                    _poolCount++; // put it back
                    return false;
                }

                entry->IsCommitted = RegionEntry.CommitDirty;
                _committedRegionBytes += Region.Size;
            }

            // Sticky marks from the region's previous life would resurrect dead objects
            // (only full collections clear the bitmap); recycled regions restart clean.
            // Frontier regions below skip this: their bitmap slice is freshly committed.
            ClearRegionMarks(index);

            return true;
        }

        index = _frontier;

        if (index >= Region.Count
            || !CanCommit(Region.Size)
            || !EnsureTableCommitted(index + 1)
            || !_memory.TryCommit(RegionBase(index), Region.Size))
        {
            return false;
        }

        GetEntry(index)->IsCommitted = RegionEntry.CommitDirty;
        _committedRegionBytes += Region.Size;
        _frontier = index + 1;
        return true;
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
        _recycledHead = -1;
        _linkedHoleBytes = 0;
        _classHeads.AsSpan().Fill(-1);

        var frontier = _frontier;

        if (pool is null)
        {
            var lists = NewSweepLists();
            SweepRange(0, frontier, youngOnly, ref lists, deferredFrees: null, plan: null);
            SpliceSweepLists(ref lists);
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
                SpliceSweepLists(ref lists);
                liveTotal += lists.Live;
            }
        });

        return liveTotal;
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
        _recycledHead = -1;
        _linkedHoleBytes = 0;
        _classHeads.AsSpan().Fill(-1);
        _activeBump = -1;

        // Arm the assist before the world restarts: the first post-restart carve can
        // hit a dry supply before the worker pool has published anything
        _sweepPlan = plan;
        _sweepPlanCount = planCount;
        _sweepCursor = 0;
        _sweepInFlight = true;
    }

    /// <summary>
    /// Mutator sweep-assist (M6.5 stage 2): called from the carve slow paths — alloc
    /// lock held — when supply runs dry while a concurrent sweep is in flight. Claims
    /// one plan chunk, sweeps it right here and publishes inline (the caller already
    /// holds the publication lock), so allocation demand drains the plan instead of
    /// fresh-committing. Returns true when a chunk was swept — the caller retries its
    /// supply — and false once the plan is exhausted. Safe against cycle turnover: a
    /// claim runs entirely inside one allocation call, and the next cycle's plan is
    /// rebuilt under a suspension that waits for every such call to drain.
    /// </summary>
    public bool TrySweepAssist()
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

        // Much smaller than the worker chunk: this runs under the allocation lock on
        // an application thread, and two smear regions' holes (~2-4 MB) already serve
        // dozens of carves — the assist exists to bridge to the workers' publications,
        // not to compete with them for the plan
        const int Chunk = 2;

        var start = Interlocked.Add(ref _sweepCursor, Chunk) - Chunk;

        if (start >= planCount)
        {
            return false;
        }

        var lists = NewSweepLists();
        _assistFreed.Clear();

        SweepRange(start, Math.Min(start + Chunk, planCount), youngOnly: false, ref lists, _assistFreed, plan);

        SpliceSweepLists(ref lists);

        foreach (var index in _assistFreed)
        {
            MakeFree(index);
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
    /// to skip, and planned regions' cursors cannot move (only the active bump
    /// region's does, and it was abandoned). Workers publish their chunk's supply —
    /// list splices and pool pushes — under <paramref name="publishLock"/> (the
    /// allocation lock), so carves see complete per-chunk results; the zeroer must be
    /// gated out by the caller for the whole sweep (it walks hole memory without the
    /// lock). The return value is Σ LiveBytes over swept regions, for parity checks —
    /// budgets already consumed <see cref="SumLiveBytes"/> at pause B.
    /// </summary>
    public long SweepConcurrentFull(GcAwareLock publishLock, GcWorkerPool? pool)
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

                // Publish this chunk's supply: recycled/class list splices and pool
                // pushes become visible to carves atomically per chunk
                publishLock.Acquire();

                try
                {
                    SpliceSweepLists(ref lists);

                    foreach (var index in freed)
                    {
                        MakeFree(index);
                    }
                }
                finally
                {
                    publishLock.Release();
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

    private void SpliceSweepLists(ref SweepLists lists)
    {
        _linkedHoleBytes += lists.LinkedHoleBytes;

        if (lists.RecycledHead >= 0)
        {
            GetEntry(lists.RecycledTail)->NextRecycled = _recycledHead;
            _recycledHead = lists.RecycledHead;
        }

        for (int c = 0; c < Region.ClassCount; c++)
        {
            if (lists.ClassHeads[c] >= 0)
            {
                GetEntry(lists.ClassTails[c])->NextInClassList = _classHeads[c];
                _classHeads[c] = lists.ClassHeads[c];
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
        // place; the next carve zeroes the windows it hands out
        if (_activeBump == index)
        {
            _activeBump = -1;
        }

        MakeFree(index, deferredFrees);
    }

    private void SweepBumpRegion(int index, ref SweepLists lists)
    {
        var entry = GetEntry(index);

        entry->FirstHole = 0;
        entry->HoleBytes = 0;
        entry->NextRecycled = -1;

        // The rebuilt holes cover freshly dead objects: their bodies are dirty again
        entry->BumpFlags &= unchecked((byte)~RegionEntry.HolesZeroedFlag);

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

        // Pre-header slot of the first unswept byte; a dead run's extent starts at the
        // slot before its first object ref (SPEC-M2 §3)
        var deadStart = regionBase;

        for (; w << 6 < endBit; w++)
        {
            var word = bitmap[w];

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
                    ClosePlug(entry, index, deadStart, ptr - IntPtr.Size);
                }

                deadStart = next - IntPtr.Size;
            }
        }

        // Tail: when the last survivor ends at the cursor, only its successor's
        // never-materialized pre-header slot remains — not a dead extent
        if (end - deadStart > IntPtr.Size)
        {
            ClosePlug(entry, index, deadStart, end);
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
    private void ClosePlug(RegionEntry* entry, int regionIndex, nint start, nint end)
    {
        var extent = end - start;

        var plug = (GCObject*)(start + IntPtr.Size);
        plug->RawMethodTable = _freeObjectMethodTable;
        plug->Length = (uint)(extent - 3 * IntPtr.Size);

        if (extent >= _minLinkedHole)
        {
            // The link lives in the plug's dead body, at ref + 16 (SPEC-M2 §4.4)
            *(nint*)(start + 3 * IntPtr.Size) = entry->FirstHole;
            entry->FirstHole = start + IntPtr.Size;
            entry->HoleBytes += (int)extent;
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
        // each other; the concurrent sweep reaches here holding the alloc lock instead.
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
    /// <see cref="ZeroerGate"/> and the allocation lock.
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
        // the pre-zeroed prefix the zeroer builds up is skipped on each pass
        for (int i = _poolCount - 1; i >= 0; i--)
        {
            var entry = GetEntry(_pool[i]);

            if (entry->IsCommitted == RegionEntry.CommitDirty)
            {
                index = _pool[i];
                _pool[i] = _pool[--_poolCount];
                entry->ZeroerCheckedOut = 1;
                _pooledCommittedBytes -= Region.Size;
                regionBase = RegionBase(index);
                return true;
            }
        }

        return false;
    }

    /// <summary>Returns a checked-out region to the pool top, pre-zeroed: the next carve
    /// hands its memory out with no zeroing debt. Caller must hold <see cref="ZeroerGate"/>
    /// and the allocation lock.</summary>
    public void CheckInZeroedRegion(int index)
    {
        var entry = GetEntry(index);
        entry->ZeroerCheckedOut = 0;
        entry->IsCommitted = RegionEntry.CommitZeroed;
        _pool[_poolCount++] = index;
        _pooledCommittedBytes += Region.Size;
    }

    /// <summary>
    /// Unlinks the first recycled region whose hole bodies are still dirty (M7): out of the
    /// recycled list, its holes are unreachable to carves, while window carves of its virgin
    /// tail (it may be the active bump region) touch disjoint memory and fields. Unlike pool
    /// checkouts this must not straddle a collection — the sweep rebuilds the recycled list
    /// and walks hole memory — so the zeroer holds <see cref="ZeroerGate"/> from here through
    /// <see cref="CheckInZeroedHoleRegion"/>. Caller must also hold the allocation lock.
    /// </summary>
    public bool TryCheckOutDirtyHoleRegion(out int index)
    {
        var previous = -1;

        for (index = _recycledHead; index >= 0; index = GetEntry(index)->NextRecycled)
        {
            var entry = GetEntry(index);

            if ((entry->BumpFlags & RegionEntry.HolesZeroedFlag) == 0)
            {
                if (previous < 0)
                {
                    _recycledHead = entry->NextRecycled;
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
            // Hole extent is [ref - 8, ref + 16 + Length); the body starts after the link
            ZeroMemory(holeRef + 3 * IntPtr.Size, (nint)((GCObject*)holeRef)->Length - IntPtr.Size);
        }
    }

    /// <summary>Relinks a checked-out hole region at the recycled-list head with its holes
    /// flagged pre-zeroed. Caller must hold <see cref="ZeroerGate"/> (continuously since the
    /// checkout) and the allocation lock.</summary>
    public void CheckInZeroedHoleRegion(int index)
    {
        var entry = GetEntry(index);
        entry->BumpFlags |= RegionEntry.HolesZeroedFlag;
        entry->NextRecycled = _recycledHead;
        _recycledHead = index;
    }

    /// <summary>
    /// One bounded batch of the pool trim: decommits the oldest (coldest) pool entries
    /// first until committed slack reaches the target (SPEC-M2 §7.4, retargeted by M4).
    /// The caller passes the post-collection allocation budget: young collections recycle
    /// a whole budget's worth of regions every cycle, and trimming below the next cycle's
    /// demand just converts the slack into decommit/recommit churn.
    ///
    /// Batched and called under the allocation lock with the world running (M6 stage 3:
    /// the whole-pool trim measured ~46 ms inside pause B on the ASP.NET soak, and
    /// decommitting free regions needs no stopped world — only the allocator's lock
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

    /// <returns>true while entries above target remain (call again for the next batch)</returns>
    public bool TrimPoolBatch(long target, int maxDecommits, ref int cursor)
    {
        var done = 0;

        for (; cursor < _poolCount && _pooledCommittedBytes > target; cursor++)
        {
            if (done == maxDecommits)
            {
                return true;
            }

            var entry = GetEntry(_pool[cursor]);

            if (entry->IsCommitted != RegionEntry.CommitNone && _memory.Decommit(RegionBase(_pool[cursor]), Region.Size))
            {
                entry->IsCommitted = RegionEntry.CommitNone;
                _pooledCommittedBytes -= Region.Size;
                _committedRegionBytes -= Region.Size;
                done++;
            }
        }

        return false;
    }

    /// <summary>Zeroes a recycled window or block extent before first use (M4
    /// zero-at-carve). Called by the allocation path after releasing the allocation lock:
    /// the memory is private to the requesting thread by then, and concurrent carves zero
    /// in parallel.</summary>
    public static void ZeroWindow(nint window, nint length) => ZeroMemory(window, length);

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

    private static void ZeroMemory(nint start, nint length)
    {
        var tStart = GcStats.Timestamp();

        Zeroing.Clear(start, length);

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
