using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// Tests OOM handling: with DOTNET_GCHeapHardLimit=128MB (must be set before runtime startup, hence
/// the child process), exhausting the heap must surface as a catchable OutOfMemoryException — not a
/// fail-fast — and the heap must remain usable afterwards. This requires the GC to honor the hard
/// limit and to return null from Alloc on failure so the EE can throw the managed exception.
/// (docs/missing-features.md item 1.3)
/// </summary>
public class HeapHardLimitOomTest() : TestBase("Hard Limit OOM", GcFeature.HardLimitOom)
{
    public const string ChildArgument = "--oom-child";

    private const int ChildSuccessExitCode = 42;
    private const string HardLimitHex = "8000000"; // DOTNET_ numeric configs are hexadecimal: 0x8000000 = 128 MB

    public override void Run()
    {
        var result = ChildProcess.Run(
            ChildArgument,
            new Dictionary<string, string> { ["DOTNET_GCHeapHardLimit"] = HardLimitHex },
            TimeSpan.FromSeconds(60));

        if (result.ExitCode != ChildSuccessExitCode)
        {
            throw new Exception($"OOM child process {result.Describe()}");
        }
    }

    /// <summary>
    /// Child-process entry point, dispatched from Program.cs when the first argument is
    /// <see cref="ChildArgument"/>. Runs with the 128 MB hard limit already applied.
    /// </summary>
    public static int RunChild()
    {
        try
        {
            var chunks = new List<byte[]>();

            // Try to allocate 4 GB; with a 128 MB hard limit this must fail long before the end.
            for (int i = 0; i < 4096; i++)
            {
                try
                {
                    chunks.Add(new byte[1024 * 1024]);
                }
                catch (OutOfMemoryException)
                {
                    // The exception was catchable (no fail-fast). Now verify the heap still works.
                    chunks.Clear();
                    chunks = null!;

                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();

                    var recovery = new byte[1024];
                    recovery[0] = 1;
                    GC.KeepAlive(recovery);

                    Console.WriteLine($"Caught OutOfMemoryException after {i} MB and recovered");
                    return ChildSuccessExitCode;
                }
            }

            Console.Error.WriteLine("Allocated 4 GB without OutOfMemoryException - the hard limit was not honored");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"OOM child failed unexpectedly: {ex}");
            return 2;
        }
    }
}
