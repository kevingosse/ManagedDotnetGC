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
