using System.Runtime;
using System.Runtime.CompilerServices;
using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// Tests DependentHandle behavior - the dependent object is kept alive
/// only as long as the target object is alive.
/// </summary>
public class DependentHandleTest() : TestBase("DependentHandle")
{
    public override void Run()
    {
        TestDependentKeptAliveByTarget();
        TestDependentCollectedWithTarget();
        TestHandleClearedAfterTargetCollection();
        TestSetTargetToNull();
        TestTargetAndDependentAtomic();
        TestDispose();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void TestDependentKeptAliveByTarget()
    {
        var target = new object();
        var weakDependent = CreateHandleAndGetWeakRef(target, out var handle);

        try
        {
            GC.Collect();

            // The dependent should still be alive because the target is alive
            if (!weakDependent.TryGetTarget(out _))
                throw new Exception("DependentKeptAliveByTarget: dependent was collected while target is still alive");

            if (handle.Dependent == null)
                throw new Exception("DependentKeptAliveByTarget: handle.Dependent is null while target is still alive");

            GC.KeepAlive(target);
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
    private static void TestDependentCollectedWithTarget()
    {
        var refs = CreateHandleWithNoRoots(out var handle);

        try
        {
            GC.Collect();

            if (refs.weakTarget.TryGetTarget(out _))
                throw new Exception("DependentCollectedWithTarget: target was not collected");

            if (refs.weakDependent.TryGetTarget(out _))
                throw new Exception("DependentCollectedWithTarget: dependent was not collected when target was collected");
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
    private static void TestHandleClearedAfterTargetCollection()
    {
        var handle = CreateHandleWithCollectibleTarget();

        try
        {
            GC.Collect();

            if (handle.Target != null)
                throw new Exception("HandleClearedAfterTargetCollection: handle.Target is non-null after target was collected");

            if (handle.Dependent != null)
                throw new Exception("HandleClearedAfterTargetCollection: handle.Dependent is non-null after target was collected");
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
    private static void TestSetTargetToNull()
    {
        var target = new object();
        var weakDependent = CreateHandleAndSetTargetNull(target, out var handle);

        try
        {
            GC.Collect();

            if (weakDependent.TryGetTarget(out _))
                throw new Exception("SetTargetToNull: dependent still alive after target was set to null and GC ran");

            GC.KeepAlive(target);
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
    private static void TestTargetAndDependentAtomic()
    {
        var target = new object();
        var dependent = new object();
        using var handle = new DependentHandle(target, dependent);

        var (t, d) = handle.TargetAndDependent;

        if (!ReferenceEquals(t, target))
            throw new Exception("TargetAndDependentAtomic: TargetAndDependent.Target is not the original target");

        if (!ReferenceEquals(d, dependent))
            throw new Exception("TargetAndDependentAtomic: TargetAndDependent.Dependent is not the original dependent");

        GC.KeepAlive(target);
        GC.KeepAlive(dependent);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void TestDispose()
    {
        var target = new object();
        var dependent = new object();
        var handle = new DependentHandle(target, dependent);

        if (!handle.IsAllocated)
            throw new Exception("Dispose: handle.IsAllocated is false before Dispose");

        handle.Dispose();

        if (handle.IsAllocated)
            throw new Exception("Dispose: handle.IsAllocated is true after Dispose");

        GC.KeepAlive(target);
        GC.KeepAlive(dependent);
    }
}
