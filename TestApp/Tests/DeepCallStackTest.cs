using System.Runtime.CompilerServices;
using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// Tests that GC correctly scans deep call stacks
/// </summary>
public class DeepCallStackTest() : TestBase("Deep Call Stack")
{
    public override void Run()
    {
        var weakRef = RecursiveAllocate(50);

        // Object should still be alive (it's on the stack)
        if (!weakRef.IsAlive)
            throw new Exception("Object not alive after recursive GC at depth 50");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference RecursiveAllocate(int depth)
    {
        var obj = new DataObject { Value = depth };
        var weakRef = new WeakReference(obj);

        if (depth > 0)
        {
            // Recurse deeper
            RecursiveAllocate(depth - 1);
        }

        // Trigger GC at various stack depths
        if (depth % 10 == 0)
        {
            GC.Collect();
        }

        // Object should still be alive
        GC.KeepAlive(obj);
        return weakRef;
    }

    private class DataObject
    {
        public int Value;
    }
}
