using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// Tests different GCHandle types
/// </summary>
public class GCHandleTest : TestBase
{
    public GCHandleTest()
        : base("GCHandle Types", "Verifies Strong, Weak, and Normal GCHandle behavior")
    {
    }

    public override bool Run()
    {
        // Test Strong handle - should keep object alive
        if (!TestStrongHandle())
        {
            return false;
        }

        // Test Weak handle - should not keep object alive
        if (!TestWeakHandle())
        {
            return false;
        }

        // Test Normal handle (should be strong)
        if (!TestNormalHandle())
        {
            return false;
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool TestStrongHandle()
    {
        var weakRef = AllocateWithStrongHandle(out var handle);

        try
        {
            // Object should be alive due to strong handle
            GC.Collect();

            if (!weakRef.IsAlive)
            {
                return false; // Strong handle should keep it alive
            }

            // Verify we can still access it
            var target = handle.Target;
            if (target == null)
            {
                return false;
            }

            return true;
        }
        finally
        {
            handle.Free();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AllocateWithStrongHandle(out GCHandle handle)
    {
        var obj = new object();
        handle = GCHandle.Alloc(obj, GCHandleType.Normal);
        return new WeakReference(obj);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool TestWeakHandle()
    {
        var weakRef = AllocateWithWeakHandle(out var handle);

        try
        {
            // Weak handle should not keep object alive
            GC.Collect();

            if (weakRef.IsAlive)
            {
                return false; // Should have been collected
            }

            // Handle target should be null
            var target = handle.Target;
            if (target != null)
            {
                return false;
            }

            return true;
        }
        finally
        {
            handle.Free();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AllocateWithWeakHandle(out GCHandle handle)
    {
        var obj = new object();
        handle = GCHandle.Alloc(obj, GCHandleType.Weak);
        return new WeakReference(obj);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool TestNormalHandle()
    {
        var weakRef = AllocateWithNormalHandle(out var handle);

        try
        {
            GC.Collect();

            if (!weakRef.IsAlive)
            {
                return false; // Normal handle should keep it alive
            }

            return true;
        }
        finally
        {
            handle.Free();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AllocateWithNormalHandle(out GCHandle handle)
    {
        var obj = new object();
        handle = GCHandle.Alloc(obj);
        return new WeakReference(obj);
    }
}
