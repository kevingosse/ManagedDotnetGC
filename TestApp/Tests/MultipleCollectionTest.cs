using System.Runtime.CompilerServices;
using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// Tests multiple GC collections in succession
/// </summary>
public class MultipleCollectionTest : TestBase
{
    public MultipleCollectionTest()
        : base("Multiple Collections")
    {
    }

    public override void Run()
    {
        var weakRefs = new List<WeakReference>();

        // Allocate objects and create weak references
        for (int i = 0; i < 10; i++)
        {
            weakRefs.Add(AllocateObject());
        }

        // All should be dead after allocation function returns
        GC.Collect();

        for (int i = 0; i < weakRefs.Count; i++)
        {
            if (weakRefs[i].IsAlive)
                throw new Exception($"weakRefs[{i}] is still alive after GC with no roots");
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

        for (int i = 0; i < newWeakRefs.Count; i++)
        {
            if (newWeakRefs[i].IsAlive)
                throw new Exception($"newWeakRefs[{i}] is still alive after second GC with no roots");
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AllocateObject()
    {
        var obj = new byte[1024];
        return new WeakReference(obj);
    }
}
