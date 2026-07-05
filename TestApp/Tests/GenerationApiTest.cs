using System.Runtime.CompilerServices;
using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// Tests the generation-numbering APIs. The custom GC is non-generational and reports a single
/// generation (MaxGeneration = 0); the stock GC reports 2. What actually matters is consistency,
/// which is what this test pins: GetGeneration never exceeds MaxGeneration (the EE indexes arrays
/// sized MaxGeneration + 1 with per-object generations), survivors converge to MaxGeneration,
/// frozen-segment objects report int.MaxValue, and per-generation collection counts increase
/// monotonically.
/// (docs/missing-features.md items 6.1/6.2)
/// </summary>
public class GenerationApiTest() : TestBase("Generation APIs", GcFeature.GenerationApis)
{
    public override void Run()
    {
        TestMaxGeneration();
        TestObjectGeneration();
        TestFrozenObjectGeneration();
        TestCollectionCounts();
    }

    private static void TestMaxGeneration()
    {
        // 2 on the stock GC, 0 on the non-generational custom GC. Diagnostics assume at most
        // three generations (GCMemoryInfo/EventCounters report gen0/1/2), hence the upper bound.
        if (GC.MaxGeneration < 0 || GC.MaxGeneration > 2)
        {
            throw new Exception($"GC.MaxGeneration should be within [0, 2], got {GC.MaxGeneration}");
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void TestObjectGeneration()
    {
        var obj = new object();

        int generation = GC.GetGeneration(obj);

        if (generation < 0 || generation > GC.MaxGeneration)
        {
            throw new Exception($"GC.GetGeneration returned {generation} for a fresh object, expected [0, {GC.MaxGeneration}]");
        }

        // A surviving object must end up reported as MaxGeneration after enough full collections
        GC.Collect();
        GC.Collect();

        generation = GC.GetGeneration(obj);

        if (generation != GC.MaxGeneration)
        {
            throw new Exception($"GC.GetGeneration returned {generation} for an object that survived two full collections, expected {GC.MaxGeneration}");
        }

        GC.KeepAlive(obj);
    }

    private static void TestFrozenObjectGeneration()
    {
        // String literals live on the frozen object heap since .NET 8; the GC reports non-heap
        // objects as int.MaxValue (validated against the stock GC).
        int generation = GC.GetGeneration("GenerationApiTest frozen literal");

        if (generation != int.MaxValue)
        {
            throw new Exception($"GC.GetGeneration returned {generation} for a frozen string literal, expected int.MaxValue");
        }
    }

    private static void TestCollectionCounts()
    {
        var before = new int[GC.MaxGeneration + 1];

        for (int gen = 0; gen <= GC.MaxGeneration; gen++)
        {
            before[gen] = GC.CollectionCount(gen);

            if (before[gen] < 0)
            {
                throw new Exception($"GC.CollectionCount({gen}) returned a negative value: {before[gen]}");
            }
        }

        GC.Collect();

        for (int gen = 0; gen <= GC.MaxGeneration; gen++)
        {
            int after = GC.CollectionCount(gen);

            if (after <= before[gen])
            {
                throw new Exception($"GC.CollectionCount({gen}) did not increase after a full collection ({before[gen]} -> {after})");
            }
        }
    }
}
