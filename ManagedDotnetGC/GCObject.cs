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
    /// The epoch of the collection currently in progress. Only meaningful (and only read)
    /// while the world is stopped; never 0, so freshly-zeroed objects are never "marked".
    /// </summary>
    internal static uint CurrentEpoch = 1;

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

    /// <summary>
    /// The GC word (SPEC-M2 §5): the 4 free bytes at ref-8, the x64 padding half of the
    /// pre-header word (the sync block index lives in the upper half, at ref-4).
    /// Holds the epoch stamp of the last collection that proved this object live.
    /// </summary>
    public uint Epoch
    {
        get => *((uint*)Unsafe.AsPointer(ref this) - 2);
        set => *((uint*)Unsafe.AsPointer(ref this) - 2) = value;
    }

    public readonly MethodTable* MethodTable => RawMethodTable;

    public bool IsMarked() => Epoch == CurrentEpoch;

    public void Mark() => Epoch = CurrentEpoch;

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