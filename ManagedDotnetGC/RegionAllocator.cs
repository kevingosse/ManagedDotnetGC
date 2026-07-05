using System.Numerics;

namespace ManagedDotnetGC;

/// <summary>
/// The region heap (SPEC-M2 §2-§4): a flat table of 2 MB regions carved from one 2 TB
/// reservation. Not thread-safe by itself — every mutating call runs either under the
/// GCHeap allocation lock or during a suspension (sweep).
/// </summary>
internal unsafe class RegionAllocator
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
    /// </summary>
    public bool TryGetWindow(nint size, out nint window, out nint length)
    {
        var needed = Align(size) + 3 * IntPtr.Size;

        while (true)
        {
            if (_activeBump >= 0)
            {
                var entry = GetEntry(_activeBump);
                var dataEnd = RegionBase(_activeBump) + Region.Size - Region.GuardBytes;
                var available = dataEnd - entry->Cursor;

                if (available >= needed)
                {
                    length = Math.Min(Math.Max(Region.WindowSize, needed), available);
                    window = entry->Cursor;
                    entry->Cursor += length;
                    return true;
                }

                // Abandon the tail: it is virgin zero and below no walk bound, so it needs
                // no plug; it is reclaimed when the region dies (SPEC-M2 §4.1)
                _activeBump = -1;
            }

            if (!TryCarveRegion(out var index))
            {
                window = 0;
                length = 0;
                return false;
            }

            var fresh = GetEntry(index);
            fresh->Kind = RegionKind.Bump;
            fresh->LiveBytes = 0;
            fresh->Cursor = RegionBase(index);
            fresh->FirstHole = 0;
            fresh->HoleBytes = 0;
            fresh->NextRecycled = -1;

            _activeBump = index;
        }
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
            if (!TryCarveRegion(out var index))
            {
                block = 0;
                return false;
            }

            var fresh = GetEntry(index);
            fresh->Kind = RegionKind.SizeClass;
            fresh->SizeClass = (byte)sizeClass;
            fresh->LiveBytes = 0;
            fresh->AllocatedBlocks = 0;
            fresh->NextInClassList = -1;

            _classHeads[sizeClass] = index;
            head = index;
        }

        var entry = GetEntry(head);
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

            if (!_memory.TryCommit(RegionBase(start), (nint)count << Region.Shift))
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

        // Commit any decommitted member first: a failure here leaves the pool intact
        for (int i = 0; i < count; i++)
        {
            var entry = GetEntry(start + i);

            if (entry->IsCommitted == 0)
            {
                if (!_memory.TryCommit(RegionBase(start + i), Region.Size))
                {
                    return false;
                }

                entry->IsCommitted = 1;
                CommittedRegionBytes += Region.Size;
                _pooledCommittedBytes += Region.Size;
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

    private bool TryCarveRegion(out int index)
    {
        if (_poolCount > 0)
        {
            index = _pool[--_poolCount];
            var entry = GetEntry(index);

            if (entry->IsCommitted != 0)
            {
                _pooledCommittedBytes -= Region.Size;
            }
            else
            {
                if (!_memory.TryCommit(RegionBase(index), Region.Size))
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
    /// Sweeps every carved region (SPEC-M2 §7). Runs under STW after marking and takes no
    /// locks (see GcAwareLock for why that is safe). Returns total live bytes.
    /// </summary>
    public long Sweep(MethodTable* freeObjectMethodTable)
    {
        long liveTotal = 0;

        // Hole lists and class lists are rebuilt from scratch on every sweep
        _recycledHead = -1;
        _classHeads.AsSpan().Fill(-1);

        for (int i = 0; i < _frontier; i++)
        {
            var entry = GetEntry(i);

            switch (entry->Kind)
            {
                case RegionKind.Bump:
                    if (entry->LiveBytes == 0)
                    {
                        // The wholesale-recycle path: no per-object work (SPEC-M2 §7.1)
                        RecycleBumpRegion(i);
                    }
                    else
                    {
                        liveTotal += entry->LiveBytes;
                        SweepBumpRegion(i, freeObjectMethodTable);
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
                    }
                    break;
            }
        }

        TrimPool(liveTotal);

        return liveTotal;
    }

    private void RecycleBumpRegion(int index)
    {
        var entry = GetEntry(index);
        var regionBase = RegionBase(index);

        // Only [base, Cursor) was ever written; the tail is still virgin zero
        ZeroMemory(regionBase, entry->Cursor - regionBase);

        if (_activeBump == index)
        {
            _activeBump = -1;
        }

        MakeFree(index);
    }

    private void SweepBumpRegion(int index, MethodTable* freeObjectMethodTable)
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
                    ClosePlug(entry, deadStart, ptr - IntPtr.Size, freeObjectMethodTable);
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
            ClosePlug(entry, deadStart, end, freeObjectMethodTable);
        }

        if (entry->FirstHole != 0)
        {
            entry->NextRecycled = _recycledHead;
            _recycledHead = index;
        }

        entry->LiveBytes = 0;
    }

    /// <summary>
    /// Zeroes a dead extent [start, end), writes the free-object plug over it, and links it
    /// as a carveable hole when big enough (SPEC-M2 §7.1).
    /// </summary>
    private void ClosePlug(RegionEntry* entry, nint start, nint end, MethodTable* freeObjectMethodTable)
    {
        var extent = end - start;

        ZeroMemory(start, extent);

        var plug = (GCObject*)(start + IntPtr.Size);
        plug->RawMethodTable = freeObjectMethodTable;
        plug->Length = (uint)(extent - 3 * IntPtr.Size);

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

        // Big spans: decommit — the OS re-zeroes lazily and we skip a giant memset
        if (count >= 4 && _memory.Decommit(spanBase, spanBytes))
        {
            CommittedRegionBytes -= spanBytes;

            for (int i = 0; i < count; i++)
            {
                GetEntry(index + i)->IsCommitted = 0;
            }
        }
        else
        {
            ZeroMemory(spanBase, spanBytes);
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
    /// Shrinks the pool's committed slack to max(64 MB, live/4) by decommitting the oldest
    /// (coldest) pool entries first (SPEC-M2 §7.4).
    /// </summary>
    private void TrimPool(long liveBytes)
    {
        var target = Math.Max(Region.MinGCBudget, liveBytes / 4);

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

    private static void ZeroMemory(nint start, nint length)
    {
        while (length > 0)
        {
            var chunk = (int)Math.Min(length, int.MaxValue & ~7);
            new Span<byte>((void*)start, chunk).Clear();
            start += chunk;
            length -= chunk;
        }
    }

    private bool EnsureTableCommitted(int requiredEntries)
    {
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

    private static nint Align(nint address) => (address + (IntPtr.Size - 1)) & ~(IntPtr.Size - 1);
}
