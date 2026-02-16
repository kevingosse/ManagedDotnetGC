namespace ManagedDotnetGC;

internal unsafe class SegmentManager
{
    private readonly NativeAllocator _nativeAllocator;
    private readonly List<Segment> _segments = new();
    private readonly nint _segmentTable;
    private nint _segmentTableCommittedEnd;

    private const int SegmentTableLength = (int)(GCHeap.HeapReserveSize / GCHeap.SegmentSize);

    public SegmentManager(NativeAllocator nativeAllocator)
    {
        _nativeAllocator = nativeAllocator;
        _segmentTable = nativeAllocator.Reserve((nint)SegmentTableLength * sizeof(nint));
        _segmentTableCommittedEnd = _segmentTable;
    }

    public List<Segment> Segments => _segments;

    public Segment AllocateSegment(nint size)
    {
        var headerSize = (nint)sizeof(SegmentHeader);
        var totalSlots = size / IntPtr.Size;
        var brickTableLength = (int)((totalSlots + Segment.SlotsPerChunk - 1) / Segment.SlotsPerChunk);
        var brickTableAlignedSize = (nint)((brickTableLength + IntPtr.Size - 1) & ~(IntPtr.Size - 1));

        var start = _nativeAllocator.Allocate(headerSize + brickTableAlignedSize + size);

        var header = (SegmentHeader*)start;
        header->ObjectStart = start + headerSize + brickTableAlignedSize;
        header->End = start + headerSize + brickTableAlignedSize + size;
        header->Current = header->ObjectStart;

        var segment = new Segment(start);

        _segments.Add(segment);
        RegisterSegment(segment);

        return segment;
    }

    public void FreeSegment(Segment segment)
    {
        _nativeAllocator.Free(segment.Start, segment.End - segment.Start);
    }

    public Segment FindSegmentContaining(nint addr)
    {
        var index = (int)((addr - _nativeAllocator.LowestAddress) / GCHeap.SegmentSize);

        if ((uint)index >= SegmentTableLength)
            return default;

        var table = (nint*)_segmentTable;

        var segmentStart = table[index];

        if (segmentStart != 0)
        {
            var segment = new Segment(segmentStart);

            if (addr >= segment.Start && addr < segment.End)
                return segment;
        }

        // The address may fall in the tail of the previous chunk's segment
        // when two segments share a chunk boundary
        if (index > 0)
        {
            segmentStart = table[index - 1];

            if (segmentStart != 0)
            {
                var segment = new Segment(segmentStart);

                if (addr >= segment.Start && addr < segment.End)
                    return segment;
            }
        }

        return default;
    }

    private void RegisterSegment(Segment segment)
    {
        var baseAddr = _nativeAllocator.LowestAddress;
        var startIndex = (int)((segment.Start - baseAddr) / GCHeap.SegmentSize);
        var endIndex = (int)((segment.End - 1 - baseAddr) / GCHeap.SegmentSize);

        EnsureSegmentTableCommitted(endIndex + 1);

        var table = (nint*)_segmentTable;
        for (int i = startIndex; i <= endIndex; i++)
        {
            table[i] = segment.Start;
        }
    }

    private void EnsureSegmentTableCommitted(int requiredEntries)
    {
        var requiredEnd = _segmentTable + (nint)requiredEntries * sizeof(nint);

        if (requiredEnd <= _segmentTableCommittedEnd)
            return;

        var pageSize = (nint)Environment.SystemPageSize;
        var alignedEnd = (requiredEnd + pageSize - 1) & ~(pageSize - 1);

        _nativeAllocator.Commit(_segmentTableCommittedEnd, alignedEnd - _segmentTableCommittedEnd);
        _segmentTableCommittedEnd = alignedEnd;
    }
}
