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
    /// Allocates a span of contiguous regions for a single large object (SPEC-M2 §4.3).
    /// </summary>
    public bool TryAllocSpan(int count, out nint spanBase)
    {
        // Frontier runs are contiguous by construction. TODO(S5): scan the pool for a
        // contiguous run first, so recycled spans get reused.
        spanBase = 0;

        if (_frontier + count > Region.Count || !EnsureTableCommitted(_frontier + count))
        {
            return false;
        }

        var start = _frontier;
        var startBase = RegionBase(start);

        if (!_memory.TryCommit(startBase, (nint)count << Region.Shift))
        {
            return false;
        }

        CommittedRegionBytes += (long)count << Region.Shift;

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

        _frontier = start + count;
        spanBase = startBase;
        return true;
    }

    private bool TryCarveRegion(out int index)
    {
        if (_poolCount > 0)
        {
            index = _pool[--_poolCount];
            var entry = GetEntry(index);

            if (entry->IsCommitted == 0)
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

        CommittedRegionBytes += Region.Size;
        _frontier = index + 1;
        return true;
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
