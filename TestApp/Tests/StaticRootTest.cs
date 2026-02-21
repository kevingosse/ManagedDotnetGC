using System.Runtime.CompilerServices;
using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// Tests that static roots keep objects alive
/// </summary>
public class StaticRootTest : TestBase
{
    private static object? _staticRoot;

    public StaticRootTest()
        : base("Static Roots")
    {
    }

    public override void Run()
    {
        var weakRef = AllocateAndSetStaticRoot();

        if (!weakRef.IsAlive)
            throw new Exception("Object not alive immediately after allocation");

        // Object should survive GC because it's referenced by a static field
        GC.Collect();

        if (!weakRef.IsAlive)
            throw new Exception("Object not alive after GC while referenced by static field");

        // Clear the static root
        _staticRoot = null;

        // Now the object should be collected
        GC.Collect();

        if (weakRef.IsAlive)
            throw new Exception("Object still alive after GC when static root was cleared");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AllocateAndSetStaticRoot()
    {
        var obj = new object();
        _staticRoot = obj;
        return new WeakReference(obj);
    }
}
