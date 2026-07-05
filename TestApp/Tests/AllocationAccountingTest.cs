using System.Runtime.CompilerServices;
using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// Tests allocation-byte accounting: GC.GetAllocatedBytesForCurrentThread must be non-negative and
/// grow with allocations (it goes negative when the GC hands out allocation-context windows without
/// maintaining alloc_bytes), GC.GetTotalAllocatedBytes must be monotonic, and GC.GetTotalMemory
/// must reflect live data. (docs/missing-features.md items 8.1, 6.1)
/// </summary>
public class AllocationAccountingTest() : TestBase("Allocation Accounting", GcFeature.AllocationAccounting)
{
    private const long ChurnBytes = 10L * 1024 * 1024;

    public override void Run()
    {
        TestPerThreadAllocatedBytes();
        TestTotalAllocatedBytes();
        TestTotalMemory();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void TestPerThreadAllocatedBytes()
    {
        long before = GC.GetAllocatedBytesForCurrentThread();

        if (before < 0)
        {
            throw new Exception($"GC.GetAllocatedBytesForCurrentThread returned a negative value: {before}");
        }

        AllocateChurn();

        long after = GC.GetAllocatedBytesForCurrentThread();

        if (after < 0)
        {
            throw new Exception($"GC.GetAllocatedBytesForCurrentThread returned a negative value after allocating: {after}");
        }

        if (after - before < ChurnBytes)
        {
            throw new Exception(
                $"GC.GetAllocatedBytesForCurrentThread grew by {after - before} bytes after allocating {ChurnBytes} bytes");
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void TestTotalAllocatedBytes()
    {
        long before = GC.GetTotalAllocatedBytes(precise: true);

        AllocateChurn();

        long after = GC.GetTotalAllocatedBytes(precise: true);

        if (after < before)
        {
            throw new Exception($"GC.GetTotalAllocatedBytes went backwards: {before} -> {after}");
        }

        if (after - before < ChurnBytes)
        {
            throw new Exception(
                $"GC.GetTotalAllocatedBytes grew by {after - before} bytes after allocating {ChurnBytes} bytes");
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void TestTotalMemory()
    {
        long withLive = HoldLiveDataAndMeasure(out var liveData);

        if (withLive < ChurnBytes)
        {
            throw new Exception(
                $"GC.GetTotalMemory(false) returned {withLive} while at least {ChurnBytes} bytes are live");
        }

        GC.KeepAlive(liveData);
        liveData = null;

        long afterRelease = GC.GetTotalMemory(forceFullCollection: true);

        if (afterRelease < 0)
        {
            throw new Exception($"GC.GetTotalMemory(true) returned a negative value: {afterRelease}");
        }

        if (withLive - afterRelease < ChurnBytes / 2)
        {
            throw new Exception(
                $"GC.GetTotalMemory did not decrease after releasing {ChurnBytes} bytes of live data " +
                $"({withLive} -> {afterRelease})");
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long HoldLiveDataAndMeasure(out List<byte[]> liveData)
    {
        liveData = new List<byte[]>();

        for (int i = 0; i < ChurnBytes / (64 * 1024); i++)
        {
            liveData.Add(new byte[64 * 1024]);
        }

        return GC.GetTotalMemory(forceFullCollection: false);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AllocateChurn()
    {
        for (int i = 0; i < ChurnBytes / (64 * 1024); i++)
        {
            var array = new byte[64 * 1024];
            array[0] = (byte)i;
        }
    }
}
