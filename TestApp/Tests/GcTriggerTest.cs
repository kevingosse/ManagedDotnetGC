using System.Runtime.CompilerServices;
using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// Tests that the GC triggers collections on its own when the allocation budget is exhausted,
/// without anyone calling GC.Collect. This is the core requirement for running arbitrary
/// applications for an arbitrary amount of time: an app that never induces a collection must still
/// be collected. (docs/missing-features.md item 1.1)
/// </summary>
public class GcTriggerTest() : TestBase("GC Trigger Policy", GcFeature.GcTriggering)
{
    public override void Run()
    {
        // Settle so pending work doesn't pollute the counters
        GC.Collect();
        GC.WaitForPendingFinalizers();

        int before = GC.CollectionCount(0);

        AllocateChurn();

        int after = GC.CollectionCount(0);

        if (after <= before)
        {
            throw new Exception(
                $"Allocating 256 MB did not trigger any collection (gen0 count stayed at {before}). " +
                "The GC must collect on its own when the allocation budget is exhausted.");
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AllocateChurn()
    {
        // 4096 x 64 KB = 256 MB of immediately-dropped small-object-heap allocations.
        // 64 KB stays below the 85000-byte LOH threshold on the stock GC.
        for (int i = 0; i < 4096; i++)
        {
            var array = new byte[64 * 1024];
            array[0] = (byte)i;
        }
    }
}
