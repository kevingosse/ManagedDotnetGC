using System.Runtime.CompilerServices;
using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// Tests GC.AllocateArray / GC.AllocateUninitializedArray, in particular the pinned:true variant
/// (GC_ALLOC_PINNED_OBJECT_HEAP): the array's data must stay at a stable address across
/// collections, with contents intact.
/// </summary>
public class PohPinnedAllocTest() : TestBase("POH Pinned Allocation", GcFeature.Allocation)
{
    public override void Run()
    {
        TestPinnedArrayAddressStability();
        TestUninitializedArrays();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static unsafe void TestPinnedArrayAddressStability()
    {
        var array = GC.AllocateArray<byte>(1024, pinned: true);

        for (int i = 0; i < array.Length; i++)
        {
            array[i] = (byte)(i % 256);
        }

        nint before = GetDataAddress(array);

        AllocateGarbage();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        nint after = GetDataAddress(array);

        if (before != after)
        {
            throw new Exception($"Pinned array moved: {before:x} -> {after:x}");
        }

        for (int i = 0; i < array.Length; i++)
        {
            if (array[i] != (byte)(i % 256))
            {
                throw new Exception($"Pinned array content corrupted at index {i}: expected {(byte)(i % 256)}, got {array[i]}");
            }
        }

        GC.KeepAlive(array);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void TestUninitializedArrays()
    {
        var uninit = GC.AllocateUninitializedArray<byte>(1024);

        if (uninit.Length != 1024)
        {
            throw new Exception($"AllocateUninitializedArray returned wrong length: {uninit.Length}");
        }

        var uninitPinned = GC.AllocateUninitializedArray<int>(256, pinned: true);

        if (uninitPinned.Length != 256)
        {
            throw new Exception($"AllocateUninitializedArray(pinned) returned wrong length: {uninitPinned.Length}");
        }

        // The arrays must be fully writable and readable
        for (int i = 0; i < uninitPinned.Length; i++)
        {
            uninitPinned[i] = i;
        }

        GC.Collect();

        for (int i = 0; i < uninitPinned.Length; i++)
        {
            if (uninitPinned[i] != i)
            {
                throw new Exception($"Uninitialized pinned array corrupted at index {i}");
            }
        }

        GC.KeepAlive(uninit);
        GC.KeepAlive(uninitPinned);
    }

    private static unsafe nint GetDataAddress(byte[] array)
    {
        fixed (byte* p = array)
        {
            return (nint)p;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AllocateGarbage()
    {
        for (int i = 0; i < 1000; i++)
        {
            _ = new byte[1024];
        }
    }
}
