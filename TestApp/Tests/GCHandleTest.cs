using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// Tests different GCHandle types - verifies Strong, Weak, and Normal GCHandle behavior
/// </summary>
public class GCHandleTest : TestBase
{
    public GCHandleTest()
        : base("GCHandle Types")
    {
    }

    public override void Run()
    {
        TestStrongHandle();
        TestWeakHandle();
        TestNormalHandle();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void TestStrongHandle()
    {
        var weakRef = AllocateWithStrongHandle(out var handle);

        try
        {
            // Object should be alive due to strong handle
            GC.Collect();

            if (!weakRef.IsAlive)
                throw new Exception("Strong GCHandle: object was collected despite active handle");

            // Verify we can still access it
            var target = handle.Target;
            if (target == null)
                throw new Exception("Strong GCHandle: handle.Target returned null after GC");
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
    private static void TestWeakHandle()
    {
        var weakRef = AllocateWithWeakHandle(out var handle);

        try
        {
            // Weak handle should not keep object alive
            GC.Collect();

            if (weakRef.IsAlive)
                throw new Exception("Weak GCHandle: object was not collected after GC");

            // Handle target should be null
            var target = handle.Target;
            if (target != null)
                throw new Exception("Weak GCHandle: handle.Target is non-null after object was collected");
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
    private static void TestNormalHandle()
    {
        var weakRef = AllocateWithNormalHandle(out var handle);

        try
        {
            GC.Collect();

            if (!weakRef.IsAlive)
                throw new Exception("Normal GCHandle: object was collected despite active handle");
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
