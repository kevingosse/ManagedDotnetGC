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
        : base("Static Roots", "Verifies that objects referenced by static fields survive GC")
    {
    }

    public override bool Run()
    {
        var weakRef = AllocateAndSetStaticRoot();

        if (!weakRef.IsAlive)
        {
            return false;
        }

        // Object should survive GC because it's referenced by a static field
        GC.Collect();

        if (!weakRef.IsAlive)
        {
            return false;
        }

        // Clear the static root
        _staticRoot = null;

        // Now the object should be collected
        GC.Collect();

        if (weakRef.IsAlive)
        {
            return false;
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AllocateAndSetStaticRoot()
    {
        var obj = new object();
        _staticRoot = obj;
        return new WeakReference(obj);
    }
}
