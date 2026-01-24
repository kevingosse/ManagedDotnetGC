using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ManagedDotnetGC;

[StructLayout(LayoutKind.Sequential)]
public unsafe struct GCObject
{
    public MethodTable* RawMethodTable;
    public uint Length;

    public readonly MethodTable* MethodTable => (MethodTable*)((nint)RawMethodTable & ~1);

    public bool IsMarked() => ((nint)RawMethodTable & 1) != 0;

    public void Mark() => RawMethodTable = (MethodTable*)((nint)MethodTable | 1);

    public void Unmark() => RawMethodTable = (MethodTable*)((nint)MethodTable & ~1);

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

    public static void EnumerateObjectReferences(GCObject* obj, Action<IntPtr> callback)
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
                        callback(target);
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
                            callback(target);
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
    public static unsafe void EnumerateObjectReferences(ref this GCObject obj, Action<IntPtr> callback)
    {
        GCObject.EnumerateObjectReferences((GCObject*)Unsafe.AsPointer(ref obj), callback);
    }
}