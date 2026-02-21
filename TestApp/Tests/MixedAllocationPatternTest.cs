using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// Tests interleaved allocation of small, medium, and large objects
/// </summary>
public class MixedAllocationPatternTest : TestBase
{
    public MixedAllocationPatternTest()
        : base("Mixed Allocation Patterns")
    {
    }

    public override void Run()
    {
        var objects = new object[300];

        for (int i = 0; i < 100; i++)
        {
            // Small object
            objects[i * 3] = new byte[100];

            // Medium object
            objects[i * 3 + 1] = new byte[10_000];

            // Large object (LOH)
            objects[i * 3 + 2] = new byte[100_000];

            // Periodic GC
            if (i % 10 == 0)
            {
                GC.Collect();
            }
        }

        // Final GC
        GC.Collect();

        // Verify all objects still accessible
        for (int i = 0; i < objects.Length; i++)
        {
            if (objects[i] == null)
                throw new Exception($"objects[{i}] is null after final GC");

            var array = (byte[])objects[i];
            if (array.Length == 0)
                throw new Exception($"objects[{i}] has zero length after final GC");
        }
    }
}
