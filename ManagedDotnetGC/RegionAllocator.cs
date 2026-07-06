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

    // LIFO stack of free region indices (fully zero or decommitted — Invariant P)
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

    // Card table storage (unbiased base), committed alongside the region frontier so the
    // EE's bulk-copy card writes always land on writable pages (missing-features 7.1)
    private nint _cardTableStorage;
    private nint _cardTableCommittedEnd;

    // Committed bytes currently sitting in the free pool (drives the retention trim)
    private long _pooledCommittedBytes;

    public RegionAllocator(NativeAllocator memory)
    {
        _memory = memory;

        _table = (RegionEntry*)NativeAllocator.OsReserve((nint)Region.Count * sizeof(RegionEntry));
        _tableCommittedEnd = (nint)_table;

        _pool = (int*)NativeAllocator.OsReserve((nint)Region.Count * sizeof(int));

        if (_table == null || _pool == null || !NativeAllocator.OsCommit((nint)_pool, (nint)Region.Count * sizeof(int)))
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

    public static int BlockCount(int sizeClass) => (int)(Region.Size / Region.ClassSizes[sizeClass]);

    public static ulong FullMask(int sizeClass)
    {
        var count = BlockCount(sizeClass);
        return count == 64 ? ulong.MaxValue : (1ul << count) - 1;
    }

    public nint HeapBase => _memory.LowestAddress;

    public int CarvedCount => _frontier;

    public long CommittedRegionBytes { get; private set; }

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
        if (TryCarveFromHoles(needed, out window, out length))
        {
            needsZero = true;
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
                    needsZero = entry->BumpIsDirty != 0;

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
            fresh->BumpIsDirty = dirty ? (byte)1 : (byte)0;
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
    /// </summary>
    private bool TryCarveFromHoles(nint needed, out nint window, out nint length)
    {
        window = 0;
        length = 0;

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

                    if (length < extent)
                    {
                        // Re-plug the remainder; its stale bytes stay (zero-at-carve). The
                        // epoch word is cleared so a stale stamp cannot alias CurrentEpoch.
                        var remainderStart = window + length;
                        var remainder = (GCObject*)(remainderStart + IntPtr.Size);
                        remainder->RawMethodTable = _freeObjectMethodTable;
                        remainder->Length = (uint)(extent - length - 3 * IntPtr.Size);
                        remainder->Epoch = 0;

                        if (extent - length >= Region.MinLinkedHole)
                        {
                            *(nint*)(remainderStart + 3 * IntPtr.Size) = entry->FirstHole;
                            entry->FirstHole = remainderStart + IntPtr.Size;
                            entry->HoleBytes += (int)(extent - length);
                        }
                        // else: a sub-window remainder floats until the next full collection
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
    /// Allocates one block from a size-class region (SPEC-M2 §4.2). Block memory is fully
    /// zero while free (Invariant P), so this writes nothing but the region-table bit.
    /// </summary>
    public bool TryAllocBlock(int sizeClass, out nint block)
    {
        var head = _classHeads[sizeClass];

        if (head < 0)
        {
            if (!TryCarveRegion(out var index, out var dirty))
            {
                block = 0;
                return false;
            }

            if (dirty)
            {
                // The block tier keeps "free blocks are zero" (sweep re-zeroes dead
                // blocks), so a recycled region must be cleaned once up front
                ZeroMemory(RegionBase(index), Region.Size);
            }

            var fresh = GetEntry(index);
            fresh->Kind = RegionKind.SizeClass;
            fresh->Age = RegionAge.Fresh;
            fresh->SizeClass = (byte)sizeClass;
            fresh->LiveBytes = 0;
            fresh->AllocatedBlocks = 0;
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
    /// construction).
    /// </summary>
    public bool TryAllocSpan(int count, out nint spanBase)
    {
        spanBase = 0;

        if (!TryTakeFreeRun(count, out var start))
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

            CommittedRegionBytes += (long)count << Region.Shift;
            _frontier = start + count;

            for (int i = 0; i < count; i++)
            {
                GetEntry(start + i)->IsCommitted = 1;
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
            if (GetEntry(i)->Kind != RegionKind.Free)
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
        // Members that stayed committed hold stale recycled contents (M4 zero-at-carve)
        // and are cleaned here — a span's object memory is handed out in full.
        for (int i = 0; i < count; i++)
        {
            var entry = GetEntry(start + i);

            if (entry->IsCommitted == 0)
            {
                if (!CanCommit(Region.Size) || !_memory.TryCommit(RegionBase(start + i), Region.Size))
                {
                    return false;
                }

                entry->IsCommitted = 1;
                CommittedRegionBytes += Region.Size;
                _pooledCommittedBytes += Region.Size;
            }
            else
            {
                ZeroMemory(RegionBase(start + i), Region.Size);
            }
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

            if (entry->IsCommitted != 0)
            {
                // Recycled content is stale: sweeps no longer zero (M4 zero-at-carve)
                dirty = true;
                _pooledCommittedBytes -= Region.Size;
            }
            else
            {
                if (!CanCommit(Region.Size) || !_memory.TryCommit(RegionBase(index), Region.Size))
                {
                    _poolCount++; // put it back
                    return false;
                }

                entry->IsCommitted = 1;
                CommittedRegionBytes += Region.Size;
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

        GetEntry(index)->IsCommitted = 1;
        CommittedRegionBytes += Region.Size;
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
    public long Sweep(bool youngOnly = false)
    {
        long liveTotal = 0;

        // Hole lists and class lists are rebuilt from scratch on every sweep
        _recycledHead = -1;
        _classHeads.AsSpan().Fill(-1);

        for (int i = 0; i < _frontier; i++)
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
                            entry->NextRecycled = _recycledHead;
                            _recycledHead = i;
                        }
                        break;

                    case RegionKind.SizeClass:
                        if ((entry->AllocatedBlocks & FullMask(entry->SizeClass)) != FullMask(entry->SizeClass))
                        {
                            entry->NextInClassList = _classHeads[entry->SizeClass];
                            _classHeads[entry->SizeClass] = i;
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
                        liveTotal += entry->LiveBytes;
                        SweepBumpRegion(i);
                    }
                    break;

                case RegionKind.SizeClass:
                    liveTotal += entry->LiveBytes;
                    SweepSizeClassRegion(i);
                    break;

                case RegionKind.SpanStart:
                    if (entry->LiveBytes == 0)
                    {
                        RecycleSpan(i);
                    }
                    else
                    {
                        liveTotal += entry->LiveBytes;
                        entry->LiveBytes = 0;
                        entry->Age = RegionAge.Old;
                    }
                    break;
            }
        }

        return liveTotal;
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

    private void SweepBumpRegion(int index)
    {
        var entry = GetEntry(index);

        entry->FirstHole = 0;
        entry->HoleBytes = 0;
        entry->NextRecycled = -1;

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
                    ClosePlug(entry, deadStart, ptr - IntPtr.Size);
                    deadStart = 0;
                }
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
            ClosePlug(entry, deadStart, end);
        }

        if (entry->FirstHole != 0)
        {
            entry->NextRecycled = _recycledHead;
            _recycledHead = index;
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
    private void ClosePlug(RegionEntry* entry, nint start, nint end)
    {
        var extent = end - start;

        var plug = (GCObject*)(start + IntPtr.Size);
        plug->RawMethodTable = _freeObjectMethodTable;
        plug->Length = (uint)(extent - 3 * IntPtr.Size);
        plug->Epoch = 0;

        if (extent >= Region.MinLinkedHole)
        {
            // The link lives in the plug's dead body, at ref + 16 (SPEC-M2 §4.4)
            *(nint*)(start + 3 * IntPtr.Size) = entry->FirstHole;
            entry->FirstHole = start + IntPtr.Size;
            entry->HoleBytes += (int)extent;
        }
    }

    private void SweepSizeClassRegion(int index)
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

            var blockBase = regionBase + (nint)bit * classSize;
            var obj = (GCObject*)(blockBase + IntPtr.Size);

            if (!obj->IsMarked())
            {
                // Re-establish Invariant P: only the object's extent was ever dirtied
                ZeroMemory(blockBase, IntPtr.Size + Align((nint)obj->ComputeSize()));
                entry->AllocatedBlocks &= ~(1ul << bit);
            }
        }

        entry->LiveBytes = 0;
        entry->Age = RegionAge.Old;

        var fullMask = FullMask(sizeClass);

        if (entry->AllocatedBlocks == 0)
        {
            MakeFree(index); // every block was zeroed as it died
        }
        else if ((entry->AllocatedBlocks & fullMask) != fullMask)
        {
            entry->NextInClassList = _classHeads[sizeClass];
            _classHeads[sizeClass] = index;
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
            CommittedRegionBytes -= spanBytes;

            for (int i = 0; i < count; i++)
            {
                GetEntry(index + i)->IsCommitted = 0;
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

        _pool[_poolCount++] = index;

        if (entry->IsCommitted != 0)
        {
            _pooledCommittedBytes += Region.Size;
        }
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

            if (entry->IsCommitted != 0 && _memory.Decommit(RegionBase(_pool[i]), Region.Size))
            {
                entry->IsCommitted = 0;
                _pooledCommittedBytes -= Region.Size;
                CommittedRegionBytes -= Region.Size;
            }
        }
    }

    /// <summary>Zeroes a recycled window before first use (M4 zero-at-carve). Called by
    /// the allocation path after releasing the allocation lock: the window is private to
    /// the requesting thread by then, and concurrent windows zero in parallel.</summary>
    public static void ZeroWindow(nint window, nint length) => ZeroMemory(window, length);

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
            // Callers all run under the alloc lock or STW, so plain adds are safe
            GcStats.ZeroBytes += total;
            GcStats.ZeroTicks += GcStats.Timestamp() - tStart;
        }
    }

    private bool EnsureTableCommitted(int requiredEntries)
    {
        if (!EnsureCardTableCommitted(requiredEntries))
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

    private static nint Align(nint address) => (address + (IntPtr.Size - 1)) & ~(IntPtr.Size - 1);
}
