using System.Diagnostics;

namespace TestApp;

/// <summary>
/// Long-running mixed-size allocation churn with a rotating retained working set
/// (SPEC-M2 §12): size mix ≈ 90% ≤ 1 KB, 9% 1–32 KB, 0.9% 32 KB–1 MB, 0.1% spans.
/// Asserts the process footprint plateaus (second-half peak ≈ first-half peak) instead of
/// growing with total allocations. Usage: TestApp --soak [seconds]
/// </summary>
public static class SoakRunner
{
    public const string Argument = "--soak";

    public static int Run(int seconds)
    {
        var random = new Random(12345);
        var retained = new object?[4096];
        long totalAllocated = 0;
        long iterations = 0;

        long firstHalfPeak = 0;
        long secondHalfPeak = 0;

        var stopwatch = Stopwatch.StartNew();
        var duration = TimeSpan.FromSeconds(seconds);

        while (stopwatch.Elapsed < duration)
        {
            var roll = random.Next(1000);

            var size = roll switch
            {
                < 900 => random.Next(16, 1024),
                < 990 => random.Next(1024, 32 * 1024),
                < 999 => random.Next(32 * 1024, 1024 * 1024),
                _ => random.Next(1024 * 1024, 8 * 1024 * 1024),
            };

            var array = new byte[size];
            array[0] = 1;
            array[^1] = 2;
            totalAllocated += size;

            if (roll % 10 == 0)
            {
                // ~10% of allocations survive for a while, so objects die across epochs
                retained[random.Next(retained.Length)] = array;
            }

            if (++iterations % 200_000 == 0)
            {
                var privateBytes = GetPrivateBytes();

                if (stopwatch.Elapsed < duration / 2)
                {
                    firstHalfPeak = Math.Max(firstHalfPeak, privateBytes);
                }
                else
                {
                    secondHalfPeak = Math.Max(secondHalfPeak, privateBytes);
                }

                Console.WriteLine(
                    $"[{stopwatch.Elapsed:mm\\:ss}] allocated {totalAllocated >> 20} MB total, " +
                    $"private {privateBytes >> 20} MB, collections {GC.CollectionCount(0)}");
            }
        }

        Console.WriteLine(
            $"Soak done: {totalAllocated >> 20} MB allocated over {stopwatch.Elapsed.TotalSeconds:F0} s, " +
            $"{GC.CollectionCount(0)} collections, peaks {firstHalfPeak >> 20} / {secondHalfPeak >> 20} MB");

        // A leak grows monotonically: the second-half peak would dwarf the first-half peak
        if (secondHalfPeak > firstHalfPeak * 5 / 4)
        {
            Console.Error.WriteLine(
                $"FAIL: footprint kept growing ({firstHalfPeak >> 20} MB -> {secondHalfPeak >> 20} MB)");
            return 1;
        }

        if (secondHalfPeak > 2L * 1024 * 1024 * 1024)
        {
            Console.Error.WriteLine($"FAIL: footprint exceeded 2 GB ({secondHalfPeak >> 20} MB)");
            return 1;
        }

        Console.WriteLine("PASS: footprint plateaued");
        return 0;
    }

    private static long GetPrivateBytes()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return process.PrivateMemorySize64;
    }
}
