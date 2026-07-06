using System.Runtime.CompilerServices;

namespace ManagedDotnetGC;

/// <summary>
/// Native LIFO stack for the mark phase (SPEC-M2 §6). No managed allocation may happen while
/// marking: delegates or Stack&lt;T&gt; growth would allocate on the GC's own NativeAOT heap
/// mid-collection. Commit failure while marking is unrecoverable by design (a half-marked
/// heap cannot be swept), hence the fail-fast.
/// </summary>
internal sealed unsafe class MarkStack
{
    private const nint ReserveBytes = 256 * 1024 * 1024;
    private const nint CommitChunk = 1024 * 1024;

    private readonly nint* _base;
    private long _committedSlots;
    private long _count;

    public MarkStack()
    {
        _base = (nint*)NativeAllocator.OsReserve(ReserveBytes);

        if (_base == null || !NativeAllocator.OsCommit((nint)_base, CommitChunk))
        {
            // Initialization-time failure: the process cannot run without a mark stack
            throw new OutOfMemoryException("Failed to reserve the GC mark stack");
        }

        _committedSlots = CommitChunk / sizeof(nint);
    }

    public bool IsEmpty => _count == 0;

    public long Count => _count;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Push(nint value)
    {
        if (_count == _committedSlots)
        {
            Grow();
        }

        _base[_count++] = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public nint Pop() => _base[--_count];

    /// <summary>
    /// Removes the <paramref name="count"/> oldest entries into <paramref name="dest"/>,
    /// for donating work to <see cref="MarkShareQueue"/>. The bottom of the stack holds the
    /// coarsest subtrees (pushed nearest the roots), so donating from there hands stealers
    /// large units of work while the owner keeps its cache-hot top. Only the owning worker
    /// may call this.
    /// </summary>
    public void RemoveBottom(nint* dest, int count)
    {
        Buffer.MemoryCopy(_base, dest, count * sizeof(nint), count * sizeof(nint));
        Buffer.MemoryCopy(_base + count, _base, (_count - count) * sizeof(nint), (_count - count) * sizeof(nint));
        _count -= count;
    }

    private void Grow()
    {
        var committedBytes = (nint)(_committedSlots * sizeof(nint));

        if (committedBytes >= ReserveBytes || !NativeAllocator.OsCommit((nint)_base + committedBytes, CommitChunk))
        {
            Environment.FailFast("ManagedDotnetGC: mark stack exhausted during collection");
        }

        _committedSlots += CommitChunk / sizeof(nint);
    }
}
