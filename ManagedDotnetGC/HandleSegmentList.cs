namespace ManagedDotnetGC;

/// <summary>
/// A growable, segment-based linked list of handles for a single HandleType.
/// </summary>
public unsafe class HandleSegmentList : IDisposable
{
    private const int SegmentCapacity = 256;

    private readonly HandleSegment _head;
    private HandleSegment _tail;
    private readonly Lock _growLock = new();

    public HandleSegmentList()
    {
        _head = new HandleSegment(SegmentCapacity);
        _tail = _head;
    }

    public void Dispose()
    {
        var segment = _head;

        while (segment != null)
        {
            var next = segment.Next;
            segment.Dispose();
            segment = next;
        }
    }

    /// <summary>
    /// Allocate a new handle in this list. Grows by adding a new segment if needed.
    /// The hot path (segment has space) is lock-free. A lock is only taken when
    /// a new segment must be appended.
    /// </summary>
    public ObjectHandle* Allocate()
    {
        while (true)
        {
            // Fast path: try the tail segment (most common case)
            var tail = Volatile.Read(ref _tail);
            var result = tail.TryAllocate();

            if (result != null)
            {
                return result;
            }

            // Scan all segments lock-free for a slot freed by another thread
            var segment = _head;

            while (segment != null)
            {
                result = segment.TryAllocate();

                if (result != null)
                {
                    return result;
                }

                segment = segment.Next;
            }

            // Slow path: all segments are full, take a lock to grow
            lock (_growLock)
            {
                // If tail changed, another thread already grew the list — retry lock-free
                if (tail != Volatile.Read(ref _tail))
                {
                    continue;
                }

                // Still the same tail — append a new segment
                var newSegment = new HandleSegment(SegmentCapacity);

                // Allocate the handle *before* publicating the segment
                // Otherwise there is a small risk that the segment gets full before we allocate
                var handle = newSegment.TryAllocate()!;

                tail.Next = newSegment;
                Volatile.Write(ref _tail, newSegment);

                return handle;
            }
        }
    }

    /// <summary>
    /// Free a handle that belongs to one of this list's segments.
    /// </summary>
    public void Free(ObjectHandle* handle)
    {
        var segment = FindSegment(handle);
        segment?.Free(handle);
    }

    public HandleSegment? FindSegment(ObjectHandle* handle)
    {
        var segment = _head;

        while (segment != null)
        {
            if (segment.ContainsHandle(handle))
            {
                return segment;
            }

            segment = segment.Next;
        }

        return null;
    }

    /// <summary>
    /// Enumerate all live handles across all segments.
    /// </summary>
    public Enumerator GetEnumerator() => new(_head);

    public ref struct Enumerator
    {
        private HandleSegment? _currentSegment;
        private HandleSegment.Enumerator _segmentEnumerator;

        internal Enumerator(HandleSegment head)
        {
            _currentSegment = head;
            _segmentEnumerator = head.GetEnumerator();
        }

        public ObjectHandle* Current => _segmentEnumerator.Current;

        public bool MoveNext()
        {
            while (_currentSegment != null)
            {
                if (_segmentEnumerator.MoveNext())
                {
                    return true;
                }

                _currentSegment = _currentSegment.Next;
                if (_currentSegment != null)
                {
                    _segmentEnumerator = _currentSegment.GetEnumerator();
                }
            }

            return false;
        }
    }
}
