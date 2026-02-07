using System.Runtime;
using System.Runtime.CompilerServices;
using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// Tests DependentHandle behavior - the dependent object is kept alive
/// only as long as the target object is alive.
/// </summary>
public class DependentHandleTest : TestBase
{
    public DependentHandleTest()
        : base("DependentHandle", "Verifies that DependentHandle correctly ties dependent lifetime to target lifetime")
    {
    }

    public override bool Run()
    {
        // Test 1: Dependent stays alive while target is alive
        if (!TestDependentKeptAliveByTarget())
        {
            return false;
        }

        // Test 2: Dependent is collected when target is collected
        if (!TestDependentCollectedWithTarget())
        {
            return false;
        }

        // Test 3: Handle properties return null after target is collected
        if (!TestHandleClearedAfterTargetCollection())
        {
            return false;
        }

        // Test 4: Setting target to null releases the dependent
        if (!TestSetTargetToNull())
        {
            return false;
        }

        // Test 5: TargetAndDependent returns consistent results
        if (!TestTargetAndDependentAtomic())
        {
            return false;
        }

        // Test 6: Dispose releases the handle
        if (!TestDispose())
        {
            return false;
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool TestDependentKeptAliveByTarget()
    {
        var target = new object();
        var result = CreateHandleAndGetWeakRef(target, out var handle);
        var weakDependent = result;

        try
        {
            GC.Collect();

            // The dependent should still be alive because the target is alive
            if (!weakDependent.TryGetTarget(out _))
            {
                return false;
            }

            // The handle should report the dependent
            if (handle.Dependent == null)
            {
                return false;
            }

            GC.KeepAlive(target);
            return true;
        }
        finally
        {
            handle.Dispose();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<object> CreateHandleAndGetWeakRef(object target, out DependentHandle handle)
    {
        var dependent = new object();
        handle = new DependentHandle(target, dependent);
        return new WeakReference<object>(dependent);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool TestDependentCollectedWithTarget()
    {
        var refs = CreateHandleWithNoRoots(out var handle);

        try
        {
            GC.Collect();

            // Both target and dependent should be collected
            if (refs.weakTarget.TryGetTarget(out _))
            {
                return false;
            }

            if (refs.weakDependent.TryGetTarget(out _))
            {
                return false;
            }

            return true;
        }
        finally
        {
            handle.Dispose();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference<object> weakTarget, WeakReference<object> weakDependent) CreateHandleWithNoRoots(out DependentHandle handle)
    {
        var target = new object();
        var dependent = new object();
        handle = new DependentHandle(target, dependent);
        return (new WeakReference<object>(target), new WeakReference<object>(dependent));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool TestHandleClearedAfterTargetCollection()
    {
        var handle = CreateHandleWithCollectibleTarget();

        try
        {
            GC.Collect();

            // After target is collected, both Target and Dependent should return null
            if (handle.Target != null)
            {
                return false;
            }

            if (handle.Dependent != null)
            {
                return false;
            }

            return true;
        }
        finally
        {
            handle.Dispose();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static DependentHandle CreateHandleWithCollectibleTarget()
    {
        var target = new object();
        var dependent = new object();
        return new DependentHandle(target, dependent);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool TestSetTargetToNull()
    {
        var target = new object();
        var weakDependent = CreateHandleAndSetTargetNull(target, out var handle);

        try
        {
            GC.Collect();

            // After setting target to null, the dependent should be collectible
            if (weakDependent.TryGetTarget(out _))
            {
                return false;
            }

            GC.KeepAlive(target);
            return true;
        }
        finally
        {
            handle.Dispose();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<object> CreateHandleAndSetTargetNull(object target, out DependentHandle handle)
    {
        var dependent = new object();
        handle = new DependentHandle(target, dependent);
        var weakDependent = new WeakReference<object>(dependent);

        // Clear the target, which should release the dependent
        handle.Target = null;
        return weakDependent;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool TestTargetAndDependentAtomic()
    {
        var target = new object();
        var dependent = new object();
        using var handle = new DependentHandle(target, dependent);

        var (t, d) = handle.TargetAndDependent;

        if (!ReferenceEquals(t, target))
        {
            return false;
        }

        if (!ReferenceEquals(d, dependent))
        {
            return false;
        }

        GC.KeepAlive(target);
        GC.KeepAlive(dependent);
        return true;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool TestDispose()
    {
        var target = new object();
        var dependent = new object();
        var handle = new DependentHandle(target, dependent);

        if (!handle.IsAllocated)
        {
            return false;
        }

        handle.Dispose();

        if (handle.IsAllocated)
        {
            return false;
        }

        GC.KeepAlive(target);
        GC.KeepAlive(dependent);
        return true;
    }
}
