using System.Runtime.CompilerServices;
using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// Tests multiple GC collections in succession
/// </summary>
public class MultipleCollectionTest : TestBase
{
    public MultipleCollectionTest()
        : base("Multiple Collections", "Verifies that multiple GC.Collect() calls work correctly")
    {
    }

    public override bool Run()
    {
        var weakRefs = new List<WeakReference>();

        // Allocate objects and create weak references
        for (int i = 0; i < 10; i++)
        {
            weakRefs.Add(AllocateObject());
        }

        // All should be dead after allocation function returns
        GC.Collect();

        foreach (var weakRef in weakRefs)
        {
            if (weakRef.IsAlive)
            {
                return false;
            }
        }

        // Multiple collections should not cause issues
        GC.Collect();
        GC.Collect();
        GC.Collect();

        // Allocate new objects
        var newWeakRefs = new List<WeakReference>();
        for (int i = 0; i < 10; i++)
        {
            newWeakRefs.Add(AllocateObject());
        }

        GC.Collect();

        foreach (var weakRef in newWeakRefs)
        {
            if (weakRef.IsAlive)
            {
                return false;
            }
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AllocateObject()
    {
        var obj = new byte[1024];
        return new WeakReference(obj);
    }
}
