using System.Runtime.CompilerServices;
using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// Tests GC behavior under fragmentation scenarios
/// </summary>
public class FragmentationTest : TestBase
{
    public FragmentationTest()
        : base("Fragmentation Handling")
    {
    }

    public override void Run()
    {
        // Allocate many objects
        var phase1 = AllocateMany(100);

        // Free every other object
        for (int i = 0; i < phase1.Length; i += 2)
        {
            phase1[i] = null!;
        }

        GC.Collect();

        // Verify odd indices still alive
        for (int i = 1; i < phase1.Length; i += 2)
        {
            if (phase1[i] == null)
                throw new Exception($"phase1[{i}] (odd index) is null after GC");
        }

        // Allocate more objects (should fit in gaps)
        var phase2 = AllocateMany(50);

        GC.Collect();

        // Verify phase2 objects alive
        for (int i = 0; i < phase2.Length; i++)
        {
            if (phase2[i] == null)
                throw new Exception($"phase2[{i}] is null after GC");
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static object[] AllocateMany(int count)
    {
        var result = new object[count];
        for (int i = 0; i < count; i++)
        {
            result[i] = new byte[1000];
        }
        return result;
    }
}
