using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// Stress test with many allocations
/// </summary>
public class StressTest : TestBase
{
    public StressTest()
        : base("Stress Test", "Allocates many objects to stress test the GC")
    {
    }

    public override bool Run()
    {
        // Allocate many small objects
        for (int i = 0; i < 1000; i++)
        {
            var obj = new byte[100];
            Array.Fill(obj, (byte)i);

            // Verify some of them
            if (i % 100 == 0)
            {
                for (int j = 0; j < obj.Length; j++)
                {
                    if (obj[j] != (byte)i)
                    {
                        return false;
                    }
                }
            }
        }

        // Trigger GC
        GC.Collect();

        // Allocate many medium objects
        for (int i = 0; i < 100; i++)
        {
            var obj = new byte[10_000];
            Array.Fill(obj, (byte)(i % 256));

            if (i % 10 == 0)
            {
                for (int j = 0; j < obj.Length; j++)
                {
                    if (obj[j] != (byte)(i % 256))
                    {
                        return false;
                    }
                }
            }
        }

        // Final collection
        GC.Collect();

        return true;
    }
}
