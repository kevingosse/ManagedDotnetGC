using System.Runtime;
using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// Tests GCSettings.LatencyMode: the getter must return a defined value and the setter must
/// round-trip. A non-generational blocking GC can ignore the modes behaviorally, but real libraries
/// toggle SustainedLowLatency, so the property must not crash.
/// (docs/missing-features.md item 6.1)
/// </summary>
public class LatencyModeTest() : TestBase("GC Latency Mode", GcFeature.LatencyMode)
{
    public override void Run()
    {
        var original = GCSettings.LatencyMode;

        if (!Enum.IsDefined(original))
        {
            throw new Exception($"GCSettings.LatencyMode returned an undefined value: {original}");
        }

        try
        {
            foreach (var mode in new[] { GCLatencyMode.Batch, GCLatencyMode.Interactive, GCLatencyMode.SustainedLowLatency })
            {
                GCSettings.LatencyMode = mode;

                var read = GCSettings.LatencyMode;

                if (read != mode)
                {
                    throw new Exception($"GCSettings.LatencyMode did not round-trip: set {mode}, read {read}");
                }
            }
        }
        finally
        {
            GCSettings.LatencyMode = original;
        }
    }
}
