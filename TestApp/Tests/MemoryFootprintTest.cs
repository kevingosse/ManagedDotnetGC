using System.Diagnostics;
using System.Runtime.CompilerServices;
using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// Tests that memory freed by collections is actually reused (or returned to the OS): churning
/// through 2 GB of short-lived allocations with periodic explicit collections must not grow the
/// process footprint by anywhere near 2 GB. Deliberately uses explicit GC.Collect calls so it does
/// not depend on the allocation-trigger feature. (docs/missing-features.md item 1.2)
/// </summary>
public class MemoryFootprintTest() : TestBase("Memory Footprint Bounded", GcFeature.MemoryReuse)
{
    private const long TotalChurnBytes = 2L * 1024 * 1024 * 1024;
    private const long MaxAllowedGrowthBytes = 512L * 1024 * 1024;

    public override void Run()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long baseline = GetPrivateBytes();

        const int rounds = 8;
        const int arraysPerRound = 256; // 256 x 1 MB per round, 8 rounds = 2 GB total

        for (int round = 0; round < rounds; round++)
        {
            AllocateRound(arraysPerRound);
            GC.Collect();
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long final = GetPrivateBytes();
        long growth = final - baseline;

        if (growth > MaxAllowedGrowthBytes)
        {
            throw new Exception(
                $"Process grew by {growth / (1024 * 1024)} MB after churning through " +
                $"{TotalChurnBytes / (1024 * 1024)} MB of dead allocations with explicit collections " +
                $"(baseline {baseline / (1024 * 1024)} MB, final {final / (1024 * 1024)} MB). " +
                "Swept memory is not being reused or returned to the OS.");
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AllocateRound(int count)
    {
        for (int i = 0; i < count; i++)
        {
            var array = new byte[1024 * 1024];
            array[0] = (byte)i;
        }
    }

    private static long GetPrivateBytes()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return process.PrivateMemorySize64;
    }
}
