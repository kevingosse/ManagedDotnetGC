using System.Runtime;
using System.Runtime.CompilerServices;
using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// Tests GC.TryStartNoGCRegion / EndNoGCRegion. Declining the region (returning false) is a
/// documented, legal outcome — so this test accepts both a working implementation and a declining
/// one; what it does not accept is a crash, or a region that claims success but still collects.
/// (docs/missing-features.md item 6.1)
/// </summary>
public class NoGCRegionTest() : TestBase("NoGC Region", GcFeature.NoGCRegion)
{
    public override void Run()
    {
        TestEndOutsideRegionThrows();
        TestRegion();
        TestTooLargeRequest();
    }

    private static void TestEndOutsideRegionThrows()
    {
        try
        {
            GC.EndNoGCRegion();
            throw new Exception("GC.EndNoGCRegion outside a region should have thrown InvalidOperationException");
        }
        catch (InvalidOperationException)
        {
            // Expected
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void TestRegion()
    {
        bool started = GC.TryStartNoGCRegion(16 * 1024 * 1024);

        if (!started)
        {
            // Declining is a documented outcome (start_no_gc_no_memory -> false)
            return;
        }

        try
        {
            if (GCSettings.LatencyMode != GCLatencyMode.NoGCRegion)
            {
                throw new Exception($"Inside a NoGC region, GCSettings.LatencyMode should be NoGCRegion, got {GCSettings.LatencyMode}");
            }

            int countBefore = GC.CollectionCount(0);

            // Allocate ~1 MB, well within the 16 MB budget: no collection may happen
            for (int i = 0; i < 16; i++)
            {
                var array = new byte[64 * 1024];
                array[0] = (byte)i;
            }

            int countAfter = GC.CollectionCount(0);

            if (countAfter != countBefore)
            {
                throw new Exception($"A collection happened inside a NoGC region ({countBefore} -> {countAfter})");
            }
        }
        finally
        {
            // The region may have been exited already if the budget was somehow exceeded
            try
            {
                GC.EndNoGCRegion();
            }
            catch (InvalidOperationException)
            {
            }
        }

        if (GCSettings.LatencyMode == GCLatencyMode.NoGCRegion)
        {
            throw new Exception("GCSettings.LatencyMode still reports NoGCRegion after EndNoGCRegion");
        }
    }

    private static void TestTooLargeRequest()
    {
        try
        {
            bool started = GC.TryStartNoGCRegion(1L * 1024 * 1024 * 1024 * 1024); // 1 TB

            if (started)
            {
                GC.EndNoGCRegion();
                throw new Exception("GC.TryStartNoGCRegion(1 TB) unexpectedly succeeded");
            }

            // false is acceptable for an implementation that declines with start_no_gc_no_memory
        }
        catch (ArgumentOutOfRangeException)
        {
            // The stock behavior: start_no_gc_too_large -> ArgumentOutOfRangeException
        }
    }
}
