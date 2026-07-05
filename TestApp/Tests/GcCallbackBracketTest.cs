using ManagedDotnetGC.Api;
using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// Verifies that the GC issues the EE bracket notifications (GcStartWork, BeforeGcScanRoots,
/// AfterGcScanRoots, GcDone) exactly once per collection and in the right order, using counters
/// recorded by the GC itself. Self-instrumentation: it proves the calls happen, not that the EE
/// liked them — the EE-observable side is covered by ComWrappersTest.
/// (docs/missing-features.md item 2.1)
/// </summary>
public class GcCallbackBracketTest() : TestBase("GC Callback Brackets", GcFeature.GcInternals)
{
    public override bool RequiresCustomGcApi => true;

    public override void Run()
    {
        var gc = GcApi.TryCreate() ?? throw new Exception("Failed to initialize GC API");

        var baseline = Snapshot(gc);

        const int collections = 3;

        for (int i = 0; i < collections; i++)
        {
            GC.Collect();
        }

        var after = Snapshot(gc);

        foreach (var kind in new[] { GcCallbackKind.GcStartWork, GcCallbackKind.BeforeGcScanRoots, GcCallbackKind.AfterGcScanRoots, GcCallbackKind.GcDone })
        {
            var delta = after[(int)kind] - baseline[(int)kind];

            if (delta != collections)
            {
                throw new Exception(
                    $"{kind} was issued {delta} times across {collections} collections, expected exactly {collections} " +
                    "(the EE bracket notifications must be wired into the collection cycle)");
            }
        }

        var violations = after[(int)GcCallbackKind.OrderViolation] - baseline[(int)GcCallbackKind.OrderViolation];

        if (violations != 0)
        {
            throw new Exception(
                $"{violations} bracket notification(s) were issued out of order " +
                "(expected GcStartWork -> BeforeGcScanRoots -> AfterGcScanRoots -> GcDone)");
        }
    }

    private static int[] Snapshot(GcApi gc)
    {
        var counts = new int[(int)GcCallbackKind.OrderViolation + 1];

        for (int i = 0; i < counts.Length; i++)
        {
            counts[i] = gc.GetGcCallbackCount((GcCallbackKind)i);
        }

        return counts;
    }
}
