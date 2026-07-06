namespace ManagedDotnetGC;

/// <summary>
/// Fixed-capacity native buffer through which parallel markers share excess work (M5, full
/// mark). The full-mark entry points are few and fat — a handful of stack slots and statics
/// own the whole live graph — so unlike the card scan there is no natural partitioning: the
/// worker that lands the big root donates slices of its mark stack here and idle workers
/// take them. A plain lock is fine: donations move thousands of entries per hold, so the
/// lock is taken orders of magnitude less often than objects are marked.
///
/// A full queue silently refuses donations (the work just stays local, which is always
/// correct); an empty take returns 0.
/// </summary>
internal sealed unsafe class MarkShareQueue
{
    private const int CapacitySlots = 1 << 20; // 8 MB — takers drain continuously

    private readonly object _sync = new();
    private readonly nint* _base;
    private long _count;

    public MarkShareQueue()
    {
        _base = (nint*)NativeAllocator.OsReserve(CapacitySlots * sizeof(nint));

        if (_base == null || !NativeAllocator.OsCommit((nint)_base, CapacitySlots * sizeof(nint)))
        {
            throw new OutOfMemoryException("Failed to allocate the GC mark share queue");
        }
    }

    public bool IsEmpty => Volatile.Read(ref _count) == 0;

    /// <summary>Moves up to <paramref name="count"/> of the donor's oldest entries into the
    /// queue; entries that don't fit stay on the donor's stack.</summary>
    public void DonateFrom(MarkStack stack, long count)
    {
        lock (_sync)
        {
            var n = (int)Math.Min(count, CapacitySlots - _count);

            if (n <= 0)
            {
                return;
            }

            stack.RemoveBottom(_base + _count, n);
            _count += n;
        }
    }

    /// <summary>Moves up to <paramref name="max"/> entries onto <paramref name="stack"/>;
    /// returns how many were taken.</summary>
    public int TakeInto(MarkStack stack, int max)
    {
        lock (_sync)
        {
            var n = (int)Math.Min(max, _count);

            for (int i = 0; i < n; i++)
            {
                stack.Push(_base[--_count]);
            }

            return n;
        }
    }
}
