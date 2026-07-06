using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ManagedDotnetGC;

[StructLayout(LayoutKind.Sequential)]
public struct ObjectHeader
{
    private const uint BIT_SBLK_GC_RESERVE = 0x20000000;
    private const uint BIT_SBLK_FINALIZER_RUN = 0x40000000;

    private uint _value;

    public bool HasFinalizerRun
    {
        get => (_value & BIT_SBLK_FINALIZER_RUN) != 0;
        set
        {
            if (value)
            {
                _value |= BIT_SBLK_FINALIZER_RUN;
            }
            else
            {
                _value &= ~BIT_SBLK_FINALIZER_RUN;
            }
        }
    }
}

[StructLayout(LayoutKind.Sequential)]
public unsafe ref struct GCObject
{
    /// <summary>
    /// The side mark bitmap (SPEC-M6 §3): one bit per 8 heap bytes, GC-private memory
    /// owned by the RegionAllocator (32 KB per region, committed alongside the frontier,
    /// statics set by its constructor). Marking never writes heap pages — the
    /// load-bearing property for M6: a marker stamping marks into protected heap pages
    /// would fault once per live page and force O(live set) pre-image copies, while
    /// bitmap writes touch neither. Callers must range-check addresses against the heap
    /// before consulting marks (the bitmap only covers carved regions).
    ///
    /// Marks are sticky (SPEC-M4): young collections accumulate into the same bitmap, a
    /// full collection starts by clearing it (RegionAllocator.ClearMarks — this replaces
    /// the epoch advance, and there is no wrap case), and recycled regions clear their
    /// slice at carve so bits from a previous life cannot resurrect dead objects between
    /// fulls.
    /// </summary>
    internal static ulong* MarkBitmap;
    internal static nint MarkHeapBase;

    public MethodTable* RawMethodTable;
    public uint Length;

    public ObjectHeader* Header
    {
        get
        {
            var ptr = (int*)Unsafe.AsPointer(ref this);
            return (ObjectHeader*)(ptr - 1);
        }
    }

    public readonly MethodTable* MethodTable => RawMethodTable;

    private ulong* MarkWordPtr(out ulong mask)
    {
        var bit = ((nint)Unsafe.AsPointer(ref this) - MarkHeapBase) >> 3;
        mask = 1ul << (int)(bit & 63);
        return MarkBitmap + (bit >> 6);
    }

    public bool IsMarked()
    {
        var word = MarkWordPtr(out var mask);
        return (Volatile.Read(ref *word) & mask) != 0;
    }

    public void Mark()
    {
        var word = MarkWordPtr(out var mask);
        Interlocked.Or(ref *word, mask);
    }

    /// <summary>
    /// Claims the object for marking (M5): true exactly once per collection, whichever
    /// thread wins the interlocked bit set. The read-first fast path keeps duplicate
    /// pops (the common case on shared mark stacks) off the interlocked operation.
    /// </summary>
    public bool TryMark()
    {
        var word = MarkWordPtr(out var mask);

        if ((Volatile.Read(ref *word) & mask) != 0)
        {
            return false;
        }

        return (Interlocked.Or(ref *word, mask) & mask) == 0;
    }

    public readonly uint ComputeSize()
    {
        var methodTable = MethodTable;

        if (!methodTable->HasComponentSize)
        {
            // Fixed-size object
            return methodTable->BaseSize;
        }

        // Variable-size object
        return methodTable->BaseSize + Length * methodTable->ComponentSize;
    }

    /// <summary>
    /// Range-clamped flavor of <see cref="EnumerateObjectReferences"/> for card scans:
    /// pushes only references held in slots whose address lies in [rangeStart, rangeEnd).
    /// Sound for remembered-set scans because the barrier dirties the card of the slot
    /// address on every reference store — a slot under a clean card cannot hold a
    /// reference the scan is looking for (anything stored there was already visible to
    /// the collection that last cleared the card). This is what keeps a large dirtied
    /// array from re-walking every element for one dirty card.
    /// </summary>
    internal static void EnumerateObjectReferencesInRange(GCObject* obj, MarkStack callback, nint rangeStart, nint rangeEnd)
    {
        if (!obj->MethodTable->ContainsGCPointers)
        {
            return;
        }

        var mt = (nint*)obj->MethodTable;
        var objectSize = obj->ComputeSize();

        var seriesCount = mt[-1];

        if (seriesCount > 0)
        {
            var series = (GCDescSeries*)(mt - 1);

            for (int i = 1; i <= seriesCount; i++)
            {
                var (seriesSize, seriesOffset) = series[-i];
                seriesSize += (int)objectSize;

                var ptr = (nint*)((nint)obj + seriesOffset);

                // Slots sit at ptr + 8j; both bounds round up so the clamp is exact for
                // any 8-aligned range (negative deltas shift toward -inf, Max covers)
                var first = Math.Max(0, (long)(rangeStart - (nint)ptr + IntPtr.Size - 1) >> 3);
                var last = Math.Min((long)(seriesSize / IntPtr.Size), (long)(rangeEnd - (nint)ptr + IntPtr.Size - 1) >> 3);

                for (var j = first; j < last; j++)
                {
                    var target = ptr[j];

                    if (target != 0)
                    {
                        callback.Push(target);
                    }
                }
            }
        }
        else
        {
            // Value-type series (struct arrays with refs) are rare and their items
            // small: a per-slot compare beats stride arithmetic here
            var offset = mt[-2];
            var valSeries = (ValSerieItem*)(mt - 2) - 1;

            var ptr = (nint*)((nint)obj + offset);
            var length = obj->Length;

            for (int item = 0; item < length; item++)
            {
                for (int i = 0; i > seriesCount; i--)
                {
                    var valSerieItem = valSeries + i;

                    for (int j = 0; j < valSerieItem->Nptrs; j++)
                    {
                        if ((nint)ptr >= rangeStart && (nint)ptr < rangeEnd)
                        {
                            var target = *ptr;

                            if (target != 0)
                            {
                                callback.Push(target);
                            }
                        }

                        ptr++;
                    }

                    ptr = (nint*)((nint)ptr + valSerieItem->Skip);
                }
            }
        }
    }

    internal static void EnumerateObjectReferences(GCObject* obj, MarkStack callback)
    {
        if (!obj->MethodTable->ContainsGCPointers)
        {
            return;
        }

        var mt = (nint*)obj->MethodTable;
        var objectSize = obj->ComputeSize();

        var seriesCount = mt[-1];

        if (seriesCount > 0)
        {
            var series = (GCDescSeries*)(mt - 1);

            for (int i = 1; i <= seriesCount; i++)
            {
                var (seriesSize, seriesOffset) = series[-i];
                seriesSize += (int)objectSize;

                var ptr = (nint*)((nint)obj + seriesOffset);

                for (int j = 0; j < seriesSize / IntPtr.Size; j++)
                {
                    var target = ptr[j];

                    if (target != 0)
                    {
                        callback.Push(target);
                    }
                }
            }
        }
        else
        {
            var offset = mt[-2];
            var valSeries = (ValSerieItem*)(mt - 2) - 1;

            var ptr = (nint*)((nint)obj + offset);
            var length = obj->Length;

            for (int item = 0; item < length; item++)
            {
                for (int i = 0; i > seriesCount; i--)
                {
                    var valSerieItem = valSeries + i;

                    for (int j = 0; j < valSerieItem->Nptrs; j++)
                    {
                        var target = *ptr;

                        if (target != 0)
                        {
                            callback.Push(target);
                        }

                        ptr++;
                    }

                    ptr = (nint*)((nint)ptr + valSerieItem->Skip);
                }
            }
        }
    }
}


public static class GCObjectExtensions
{
    internal static unsafe void EnumerateObjectReferences(ref this GCObject obj, MarkStack callback)
    {
        GCObject.EnumerateObjectReferences((GCObject*)Unsafe.AsPointer(ref obj), callback);
    }
}