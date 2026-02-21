using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// Stress test with many allocations
/// </summary>
public class StressTest() : TestBase("Stress Test")
{
    public override void Run()
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
                        throw new Exception($"Small alloc {i}: obj[{j}] = {obj[j]}, expected {(byte)i}");
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
                        throw new Exception($"Medium alloc {i}: obj[{j}] = {obj[j]}, expected {(byte)(i % 256)}");
                }
            }
        }

        // Final collection
        GC.Collect();
    }
}
