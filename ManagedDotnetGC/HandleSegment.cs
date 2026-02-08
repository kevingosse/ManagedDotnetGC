using System.Runtime.InteropServices;
using System.Threading;

namespace ManagedDotnetGC;

/// <summary>
/// A fixed-size segment of ObjectHandle slots, allocated via native memory so that
/// handles are pinned and never move. Segments are linked together to form a growable
/// list. Free slots are tracked with an embedded freelist (the ExtraInfo field
/// holds the next-free index when the slot is vacant, and Type is set to HNDTYPE_FREE).
/// </summary>
public unsafe class HandleSegment
{
    private readonly ObjectHandle* _buffer;
    private readonly int _capacity;
    private int _freeHead; // index of the first free slot, or -1

    public HandleSegment? Next;

    public HandleSegment(int capacity)
    {
        _capacity = capacity;
        _buffer = (ObjectHandle*)NativeMemory.AllocZeroed((nuint)capacity, (nuint)sizeof(ObjectHandle));

        // Build the freelist: each free slot's Type is HNDTYPE_FREE,
        // and ExtraInfo holds the index of the next free slot.
        for (int i = 0; i < capacity; i++)
        {
            _buffer[i].Type = HandleType.HNDTYPE_FREE;
            _buffer[i].ExtraInfo = i + 1;
        }

        _buffer[capacity - 1].ExtraInfo = -1;
        _freeHead = 0;
    }

    public bool IsFull => Volatile.Read(ref _freeHead) == -1;

    public bool ContainsHandle(ObjectHandle* handle)
    {
        return handle >= _buffer && handle < _buffer + _capacity;
    }

    /// <summary>
    /// Try to allocate a slot from this segment using a lock-free CAS loop.
    /// Returns null if the segment is full.
    /// </summary>
    public ObjectHandle* TryAllocate()
    {
        while (true)
        {
            var head = Volatile.Read(ref _freeHead);

            if (head == -1)
            {
                return null;
            }

            var slot = _buffer + head;
            var next = (int)slot->ExtraInfo;

            if (Interlocked.CompareExchange(ref _freeHead, next, head) == head)
            {
                slot->Clear();
                return slot;
            }
        }
    }

    /// <summary>
    /// Return a slot to the freelist using a lock-free CAS loop.
    /// </summary>
    public void Free(ObjectHandle* handle)
    {
        handle->Object = null;
        var index = (int)(handle - _buffer);

        while (true)
        {
            var head = Volatile.Read(ref _freeHead);
            handle->ExtraInfo = head;
            handle->Type = HandleType.HNDTYPE_FREE;

            if (Interlocked.CompareExchange(ref _freeHead, index, head) == head)
            {
                return;
            }
        }
    }

    private bool IsSlotFree(int index) => _buffer[index].Type == HandleType.HNDTYPE_FREE;

    /// <summary>
    /// Enumerate all live handles in this segment.
    /// </summary>
    public Enumerator GetEnumerator() => new(this);

    public ref struct Enumerator
    {
        private readonly HandleSegment _segment;
        private int _index;

        internal Enumerator(HandleSegment segment)
        {
            _segment = segment;
            _index = -1;
        }

        public ObjectHandle* Current => _index == -1 ? null : &_segment._buffer[_index];

        public bool MoveNext()
        {
            for (int i = _index + 1; i < _segment._capacity; i++)
            {
                if (!_segment.IsSlotFree(i))
                {
                    _index = i;
                    return true;
                }
            }

            _index = _segment._capacity;
            return false;
        }
    }
}
