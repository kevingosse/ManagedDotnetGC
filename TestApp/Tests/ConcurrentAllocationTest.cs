using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// Tests allocation from multiple threads (if threading is supported)
/// </summary>
public class ConcurrentAllocationTest : TestBase
{
    public ConcurrentAllocationTest()
        : base("Concurrent Allocation", "Tests allocation from multiple threads simultaneously")
    {
    }

    public override bool Run()
    {
        const int threadCount = 4;
        const int allocationsPerThread = 100;

        var threads = new Thread[threadCount];
        var results = new object[threadCount][];

        // Start threads
        for (int i = 0; i < threadCount; i++)
        {
            int threadIndex = i;
            results[threadIndex] = new object[allocationsPerThread];

            threads[i] = new Thread(() =>
            {
                for (int j = 0; j < allocationsPerThread; j++)
                {
                    results[threadIndex][j] = new byte[1000 + j];
                    Thread.Yield(); // Give other threads a chance
                }
            });

            threads[i].Start();
        }

        // Wait for all threads
        foreach (var thread in threads)
        {
            thread.Join();
        }

        // Verify all allocations succeeded
        for (int i = 0; i < threadCount; i++)
        {
            for (int j = 0; j < allocationsPerThread; j++)
            {
                if (results[i][j] == null)
                {
                    return false;
                }

                var array = (byte[])results[i][j];
                if (array.Length != 1000 + j)
                {
                    return false;
                }
            }
        }

        GC.Collect();

        // Verify everything still valid after GC
        for (int i = 0; i < threadCount; i++)
        {
            for (int j = 0; j < allocationsPerThread; j++)
            {
                if (results[i][j] == null)
                {
                    return false;
                }
            }
        }

        return true;
    }
}
