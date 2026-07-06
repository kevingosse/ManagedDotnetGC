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

    // Card-offset table (M4): one ushort per card = 8-byte words from the card's start
    // back to the nearest object start at or before it, 0xFFFF = saturated (≥ 512 KB,
    // only large coalesced plugs). Rebuilt for bump regions by every sweep walk and
    // refreshed at window carves, so the card scan can jump into a dirty region instead
    // of walking it from the base. 2 KB per region, committed alongside the frontier.
    private readonly ushort* _cardOffsets;
    private nint _cardOffsetsCommittedEnd;

    // Committed bytes currently sitting in the free pool (drives the retention trim)
    private long _pooledCommittedBytes;

    public RegionAllocator(NativeAllocator memory)
    {
        _memory = memory;

        _table = (RegionEntry*)NativeAllocator.OsReserve((nint)Region.Count * sizeof(RegionEntry));
        _tableCommittedEnd = (nint)_table;

        _pool = (int*)NativeAllocator.OsReserve((nint)Region.Count * sizeof(int));

        _cardOffsets = (ushort*)NativeAllocator.OsReserve((nint)Region.Count << 11);
        _cardOffsetsCommittedEnd = (nint)_cardOffsets;

        if (_table == null || _pool == null || _cardOffsets == null
            || !NativeAllocator.OsCommit((nint)_pool, (nint)Region.Count * sizeof(int)))
        {
            // Initialization-time failure: the process cannot run without these
            throw new OutOfMemoryException("Failed to reserve GC region metadata");
        }

        _classHeads.AsSpan().Fill(-1);
    }

    /// <summary>Releases the metadata reservations. Production never disposes (the GC
    /// lives as long as the process); this exists for the unit tests.</summary>
    public void Dispose()
    {
        NativeAllocator.OsRelease((nint)_table);
        NativeAllocator.OsRelease((nint)_pool);
        NativeAllocator.OsRelease((nint)_cardOffsets);
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
                new Span<byte>((void*)_cardTableStorage, (int)length).Clear();
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

    /// <summary>Maps a front heap address to the GC's always-writable alias view
    /// (SPEC-M6 §3). Every heap write made by GC code goes through it; addresses handed
    /// to the EE, stored in lists, or read from are always front addresses.</summary>
    public nint Alias(nint addr) => addr + _memory.AliasOffset;

    public int CarvedCount => _frontier;

    // Field-backed so parallel sweep workers can adjust it with interlocked adds
    private long _committedRegionBytes;

    public long CommittedRegionBytes => Volatile.Read(ref _committedRegionBytes);

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

                    // Card offsets for the new window resolve to its first object (M4):
                    // beyond the last sweep's extent the table would otherwise be stale.
                    // The interval runs to the next window's first ref so consecutive
                    // stamps tile without a pre-header gap.
                    WriteCardOffsets(_activeBump, window + IntPtr.Size, window + length + IntPtr.Size);

                    return true;
                }

                // Abandon the tail: it is below every walk bound, so it needs no plug; it
                // is reclaimed when the region dies (SPEC-M2 §4.1)
                _activeBump = -1;
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

                    // Unlink the hole (the link lives in heap memory: write via alias)
                    if (previousHoleRef == 0)
                    {
                        entry->FirstHole = next;
                    }
                    else
                    {
                        *(nint*)Alias(previousHoleRef + 2 * IntPtr.Size) = next;
                    }

                    entry->HoleBytes -= (int)extent;

                    if (length < extent)
                    {
                        // Re-plug the remainder; its stale bytes stay (zero-at-carve). The
                        // epoch word is cleared so a stale stamp cannot alias CurrentEpoch.
                        // Plug and link writes go via the alias; list values stay front.
                        var remainderStart = window + length;
                        var remainder = (GCObject*)Alias(remainderStart + IntPtr.Size);
                        remainder->RawMethodTable = _freeObjectMethodTable;
                        remainder->Length = (uint)(extent - length - 3 * IntPtr.Size);
                        remainder->Epoch = 0;

                        if (extent - length >= _minLinkedHole)
                        {
                            *(nint*)Alias(remainderStart + 3 * IntPtr.Size) = entry->FirstHole;
                            entry->FirstHole = remainderStart + IntPtr.Size;
                            entry->HoleBytes += (int)(extent - length);
                        }
                        // else: a sub-window remainder floats until the next full collection

                        // The remainder keeps its own card-offset interval
                        WriteCardOffsets(regionIndex, remainderStart + IntPtr.Size, holeRef + extent);
                    }

                    // The window's card offsets resolve to its first object (M4): tighter
                    // than the stale entries pointing at the consumed hole's plug. The
                    // interval runs one ref past the window so stamps tile gap-free.
                    WriteCardOffsets(regionIndex, window + IntPtr.Size, window + length + IntPtr.Size);

                    if ((entry->BumpFlags & RegionEntry.HolesZeroedFlag) != 0)
                    {
                        // Pre-zeroed hole body (M7): only the plug's preheader, header and
                        // link bytes at the window start are stale
                        new Span<byte>((void*)Alias(window), 4 * IntPtr.Size).Clear();
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
            // pool's LIFO top instead of scanning the table for a run
            if (!TryCarveRegion(out start, out var dirty))
            {
                return false;
            }

            GetEntry(start)->SpanIsDirty = dirty ? (byte)1 : (byte)0;
        }
        else if (!TryTakeFreeRun(count, out start))
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
        // holes — link everything carveable so the heap can run dense instead of dying
        _minLinkedHole = UnderMemoryPressure ? PressureMinLinkedHole : Region.MinLinkedHole;

        // Hole lists and class lists are rebuilt from scratch on every sweep
        _recycledHead = -1;
        _classHeads.AsSpan().Fill(-1);

        var frontier = _frontier;

        if (pool is null)
        {
            var lists = NewSweepLists();
            SweepRange(0, frontier, youngOnly, ref lists);
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

                SweepRange(start, Math.Min(start + Chunk, frontier), youngOnly, ref lists);
            }

            lock (mergeLock)
            {
                SpliceSweepLists(ref lists);
                liveTotal += lists.Live;
            }
        });

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

    private void SweepRange(int start, int end, bool youngOnly, ref SweepLists lists)
    {
        for (int i = start; i < end; i++)
        {
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
                        RecycleBumpRegion(i);
                    }
                    else
                    {
                        lists.Live += entry->LiveBytes;
                        SweepBumpRegion(i, ref lists);
                    }
                    break;

                case RegionKind.SizeClass:
                    lists.Live += entry->LiveBytes;
                    SweepSizeClassRegion(i, ref lists);
                    break;

                case RegionKind.SpanStart:
                    if (entry->LiveBytes == 0)
                    {
                        RecycleSpan(i);
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

    private void RecycleBumpRegion(int index)
    {
        // Zero-at-carve (M4): the region returns to the pool with its dead contents in
        // place; the next carve zeroes the windows it hands out
        if (_activeBump == index)
        {
            _activeBump = -1;
        }

        MakeFree(index);
    }

    private void SweepBumpRegion(int index, ref SweepLists lists)
    {
        var entry = GetEntry(index);

        entry->FirstHole = 0;
        entry->HoleBytes = 0;
        entry->NextRecycled = -1;

        // The rebuilt holes cover freshly dead objects: their bodies are dirty again
        entry->BumpFlags &= unchecked((byte)~RegionEntry.HolesZeroedFlag);

        var ptr = RegionBase(index) + IntPtr.Size;
        var end = entry->Cursor;
        nint deadStart = 0;

        while (ptr < end)
        {
            var obj = (GCObject*)ptr;
            var next = Align(ptr + (nint)obj->ComputeSize());

            if (obj->IsMarked())
            {
                if (deadStart != 0)
                {
                    // The extent ends at the live object's pre-header slot (SPEC-M2 §3)
                    ClosePlug(entry, index, deadStart, ptr - IntPtr.Size);
                    deadStart = 0;
                }

                // The survivor's card-offset interval ends where the next object ref
                // starts, so live and plug intervals tile the region without gaps
                WriteCardOffsets(index, ptr, next);
            }
            else if (deadStart == 0)
            {
                // Free plugs are never marked, so they coalesce into the extent for free
                deadStart = ptr - IntPtr.Size;
            }

            ptr = next;
        }

        if (deadStart != 0)
        {
            ClosePlug(entry, index, deadStart, end);
        }

        if (entry->FirstHole != 0)
        {
            LinkRecycled(ref lists, index);
        }

        entry->LiveBytes = 0;
        entry->Age = RegionAge.Old;
    }

    /// <summary>
    /// Writes the free-object plug over a dead extent [start, end) and links it as a
    /// carveable hole when big enough (SPEC-M2 §7.1). Zero-at-carve (M4): the extent's
    /// stale contents stay in place — whoever carves a window out of the hole zeroes
    /// exactly the bytes handed out, outside the pause. Only the plug's epoch word must
    /// be cleared: a stale stamp there could alias the current epoch and make the plug
    /// look live to a sweep walk or card scan.
    /// </summary>
    private void ClosePlug(RegionEntry* entry, int regionIndex, nint start, nint end)
    {
        var extent = end - start;

        // Plug and link writes are GC heap writes: via the alias (SPEC-M6 §3)
        var plug = (GCObject*)Alias(start + IntPtr.Size);
        plug->RawMethodTable = _freeObjectMethodTable;
        plug->Length = (uint)(extent - 3 * IntPtr.Size);
        plug->Epoch = 0;

        // The plug's card-offset interval runs to the ref after its extent (M4)
        WriteCardOffsets(regionIndex, start + IntPtr.Size, end + IntPtr.Size);

        if (extent >= _minLinkedHole)
        {
            // The link lives in the plug's dead body, at ref + 16 (SPEC-M2 §4.4)
            *(nint*)Alias(start + 3 * IntPtr.Size) = entry->FirstHole;
            entry->FirstHole = start + IntPtr.Size;
            entry->HoleBytes += (int)extent;
        }
    }

    private void SweepSizeClassRegion(int index, ref SweepLists lists)
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
            MakeFree(index); // stale contents stay; the next carve flags it dirty
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

    private void RecycleSpan(int index)
    {
        var entry = GetEntry(index);
        var count = entry->SpanCount;
        var spanBase = RegionBase(index);
        var spanBytes = (nint)count << Region.Shift;

        // Big spans: decommit — the OS re-zeroes lazily. Small spans stay committed with
        // their dead contents; the next carve zeroes what it hands out (M4 zero-at-carve).
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
            MakeFree(index + i);
        }
    }

    private void MakeFree(int index)
    {
        var entry = GetEntry(index);
        entry->Kind = RegionKind.Free;
        entry->LiveBytes = 0;

        // The offset-8 union held a Cursor/bitmap from the region's last life; stale bits
        // there would make the span run scan treat the region as checked out forever
        entry->ZeroerCheckedOut = 0;

        // Lock-free push: parallel sweep workers free regions concurrently. Allocation
        // never runs during a sweep (STW) and the zeroer gate is held by the collecting
        // thread, so pushes only race with each other.
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
            ZeroMemory(Alias(holeRef + 3 * IntPtr.Size), (nint)((GCObject*)holeRef)->Length - IntPtr.Size);
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
    /// Shrinks the pool's committed slack to the given target by decommitting the oldest
    /// (coldest) pool entries first (SPEC-M2 §7.4, retargeted by M4). The caller passes the
    /// post-collection allocation budget: young collections recycle a whole budget's worth
    /// of regions every cycle, and trimming below the next cycle's demand just converts the
    /// slack into decommit/recommit churn.
    /// </summary>
    public void TrimPool(long target)
    {
        for (int i = 0; i < _poolCount && _pooledCommittedBytes > target; i++)
        {
            var entry = GetEntry(_pool[i]);

            if (entry->IsCommitted != RegionEntry.CommitNone && _memory.Decommit(RegionBase(_pool[i]), Region.Size))
            {
                entry->IsCommitted = RegionEntry.CommitNone;
                _pooledCommittedBytes -= Region.Size;
                _committedRegionBytes -= Region.Size;
            }
        }
    }

    /// <summary>Zeroes a recycled window, block or pool-region extent before first use
    /// (M4 zero-at-carve), writing through the alias view (SPEC-M6 §3). Called after
    /// releasing the allocation lock: the memory is private to the requesting thread by
    /// then, and concurrent carves zero in parallel.</summary>
    public void ZeroCarve(nint address, nint length) => ZeroMemory(Alias(address), length);

    /// <summary>Raw front-view memset, kept for the unit tests (production zeroing goes
    /// through <see cref="ZeroCarve"/> — the front view is protected from M6 stage 1
    /// on, but tests never protect).</summary>
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
                ZeroMemory(Alias(regionBase), zeroEnd - regionBase);
            }
        }
    }

    /// <summary>
    /// Records, for every card whose start falls inside [objStart, intervalEnd), the
    /// distance back to objStart (the card-offset table, M4). Intervals written by a sweep
    /// tile the region seamlessly — each live object's interval ends at the next object
    /// ref, each plug's at the ref after its extent — so every card of the swept range
    /// gets a current entry.
    /// </summary>
    private void WriteCardOffsets(int regionIndex, nint objStart, nint intervalEnd)
    {
        var offsets = _cardOffsets + ((nint)regionIndex << 10);
        var regionBase = RegionBase(regionIndex);

        // First card boundary at or after objStart
        var b = (objStart - regionBase + 2047) & ~(nint)2047;

        for (; regionBase + b < intervalEnd; b += 1 << 11)
        {
            var back = (regionBase + b - objStart) >> 3;
            offsets[b >> 11] = back < 0xFFFF ? (ushort)back : (ushort)0xFFFF;
        }
    }

    /// <summary>
    /// Maps an address inside a card-scanned bump region to an object start at or before
    /// it, via the card-offset table (M4). The result is always a valid walk boundary:
    /// exact after a sweep, conservatively earlier (a window's first object) for memory
    /// carved since — walking forward from it always lands on real object headers.
    /// </summary>
    public nint FindBumpObjectAtOrBefore(int regionIndex, nint addr)
    {
        var offsets = _cardOffsets + ((nint)regionIndex << 10);
        var regionBase = RegionBase(regionIndex);
        var card = (int)((addr - regionBase) >> 11);

        // 0xFFFF = the covering object starts ≥ 512 KB − 2 KB before this card (a large
        // coalesced plug): hop back and retry; a few hops cross the whole region
        while (card > 0 && offsets[card] == 0xFFFF)
        {
            card = Math.Max(0, card - 255);
        }

        var objStart = regionBase + ((nint)card << 11) - ((nint)offsets[card] << 3);

        // The first object of a region sits at base + 8; card 0 (whose start precedes it)
        // is never written and resolves here via the clamp
        return Math.Max(objStart, regionBase + IntPtr.Size);
    }

    private static void ZeroMemory(nint start, nint length)
    {
        var tStart = GcStats.Timestamp();
        var total = length;

        while (length > 0)
        {
            var chunk = (int)Math.Min(length, int.MaxValue & ~7);
            new Span<byte>((void*)start, chunk).Clear();
            start += chunk;
            length -= chunk;
        }

        if (GcStats.Enabled)
        {
            // Callers include parallel sweep workers and post-lock window zeroing
            Interlocked.Add(ref GcStats.ZeroBytes, total);
            Interlocked.Add(ref GcStats.ZeroTicks, GcStats.Timestamp() - tStart);
        }
    }

    private bool EnsureTableCommitted(int requiredEntries)
    {
        if (!EnsureCardTableCommitted(requiredEntries))
        {
            return false;
        }

        if (!EnsureCommitted(ref _cardOffsetsCommittedEnd, (nint)_cardOffsets + ((nint)requiredEntries << 11)))
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
