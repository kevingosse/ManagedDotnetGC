using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace ManagedDotnetGC;

partial class GCHeap
{
    private readonly ConcurrentDictionary<nint, StrongBox<(nint start, nint end)>> _frozenSegments = new();
    private int _frozenSegmentIndex;

    public unsafe nint RegisterFrozenSegment(segment_info* pseginfo)
    {
        var handle = Interlocked.Increment(ref _frozenSegmentIndex);
        _frozenSegments.TryAdd(handle, new((pseginfo->ibFirstObject, pseginfo->ibCommit)));
        return handle;
    }

    public unsafe void UnregisterFrozenSegment(nint seg)
    {
        _frozenSegments.TryRemove(seg, out _);
    }

    public unsafe bool IsInFrozenSegment(GCObject* obj)
    {
        foreach (var segment in _frozenSegments.Values)
        {
            if ((nint)obj >= segment.Value.start && (nint)obj < segment.Value.end)
            {
                return true;
            }
        }

        return false;
    }

    public void UpdateFrozenSegment(nint seg, nint allocated, nint committed)
    {
        if (_frozenSegments.TryGetValue(seg, out var segment))
        {
            segment.Value.start = allocated;
            segment.Value.end = committed;
        }
    }
}
