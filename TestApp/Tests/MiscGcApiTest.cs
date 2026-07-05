using System.Runtime;
using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// No-crash and documented-outcome checks for the rest of the public GC API surface: Collect
/// overloads, memory pressure, RefreshMemoryLimit, NoGC-region callback registration, full-GC
/// notifications and LOH compaction mode. Under NativeAOT, a NotImplementedException escaping any
/// of these is a process fail-fast, so "returns a sane value" is the feature.
/// (docs/missing-features.md item 6.1)
/// </summary>
public class MiscGcApiTest() : TestBase("Misc GC APIs", GcFeature.ApiSurface)
{
    public override void Run()
    {
        TestCollectOverloads();
        TestMemoryPressure();
        TestRefreshMemoryLimit();
        TestNoGCRegionCallbackOutsideRegion();
        TestFullGCNotification();
        TestLohCompactionMode();
    }

    private static void TestCollectOverloads()
    {
        GC.Collect(0);
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced);
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized);
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: false);
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }

    private static void TestMemoryPressure()
    {
        GC.AddMemoryPressure(100 * 1024 * 1024);
        GC.RemoveMemoryPressure(100 * 1024 * 1024);
    }

    private static void TestRefreshMemoryLimit()
    {
        // No configuration changed: must succeed (refresh_success) without throwing
        GC.RefreshMemoryLimit();
    }

    private static void TestNoGCRegionCallbackOutsideRegion()
    {
        try
        {
            GC.RegisterNoGCRegionCallback(1024 * 1024, () => { });
            throw new Exception("GC.RegisterNoGCRegionCallback outside a NoGC region should have thrown InvalidOperationException");
        }
        catch (InvalidOperationException)
        {
            // Expected (not_started)
        }
    }

    private static void TestFullGCNotification()
    {
        // Documented outcomes: success (then cancel), or InvalidOperationException when the GC
        // doesn't support notifications (stock throws this when concurrent GC is enabled).
        try
        {
            GC.RegisterForFullGCNotification(10, 10);
            GC.CancelFullGCNotification();
        }
        catch (InvalidOperationException)
        {
            // Expected on configurations without notification support
        }
    }

    private static void TestLohCompactionMode()
    {
        var original = GCSettings.LargeObjectHeapCompactionMode;

        try
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;

            var read = GCSettings.LargeObjectHeapCompactionMode;

            if (read != GCLargeObjectHeapCompactionMode.CompactOnce)
            {
                throw new Exception($"LargeObjectHeapCompactionMode did not round-trip: set CompactOnce, read {read}");
            }
        }
        finally
        {
            GCSettings.LargeObjectHeapCompactionMode = original;
        }
    }
}
