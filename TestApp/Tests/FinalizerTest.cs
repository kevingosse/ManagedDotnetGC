using System.Runtime.CompilerServices;
using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// Tests that finalizers are called correctly for collected objects
/// </summary>
public class FinalizerTest()
    : TestBase("Finalizers", "Verifies that finalizers are invoked for collected objects")
{
    private static int _finalizerCallCount;
    private static int _suppressedFinalizerCallCount;
    private static int _reregisteredFinalizerCallCount;

    public override void Setup()
    {
        _finalizerCallCount = 0;
        _suppressedFinalizerCallCount = 0;
        _reregisteredFinalizerCallCount = 0;
    }

    public override bool Run()
    {
        if (!TestFinalizerRuns())
        {
            return false;
        }

        if (!TestMultipleFinalizersRun())
        {
            return false;
        }

        if (!TestSuppressFinalize())
        {
            return false;
        }

        if (!TestReRegisterForFinalize())
        {
            return false;
        }

        return true;
    }

    // A single finalizable object goes out of scope; its finalizer must run.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool TestFinalizerRuns()
    {
        _finalizerCallCount = 0;
        AllocateFinalizableObject();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        return Volatile.Read(ref _finalizerCallCount) == 1;
    }

    // Multiple finalizable objects go out of scope; every finalizer must run.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool TestMultipleFinalizersRun()
    {
        _finalizerCallCount = 0;

        for (int i = 0; i < 5; i++)
        {
            AllocateFinalizableObject();
        }
        
        GC.Collect();
        GC.WaitForPendingFinalizers();

        return Volatile.Read(ref _finalizerCallCount) == 5;
    }

    // GC.SuppressFinalize must prevent the finalizer from running.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool TestSuppressFinalize()
    {
        _suppressedFinalizerCallCount = 0;

        AllocateSuppressedObject();
        GC.Collect();
        GC.WaitForPendingFinalizers();

        return Volatile.Read(ref _suppressedFinalizerCallCount) == 0;
    }

    // GC.ReRegisterForFinalize after GC.SuppressFinalize must restore finalization.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool TestReRegisterForFinalize()
    {
        _reregisteredFinalizerCallCount = 0;

        AllocateSuppressedThenReregisteredObject();
        GC.Collect();
        GC.WaitForPendingFinalizers();

        return Volatile.Read(ref _reregisteredFinalizerCallCount) == 1;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AllocateFinalizableObject()
    {
        _ = new TrackingFinalizableObject();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AllocateSuppressedObject()
    {
        var obj = new SuppressedFinalizableObject();
        GC.SuppressFinalize(obj);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AllocateSuppressedThenReregisteredObject()
    {
        var obj = new ReregisteredFinalizableObject();
        GC.SuppressFinalize(obj);
        GC.ReRegisterForFinalize(obj);
    }

    private class TrackingFinalizableObject
    {
        ~TrackingFinalizableObject()
        {
            Interlocked.Increment(ref _finalizerCallCount);
        }
    }

    private class SuppressedFinalizableObject
    {
        ~SuppressedFinalizableObject()
        {
            Interlocked.Increment(ref _suppressedFinalizerCallCount);
        }
    }

    private class ReregisteredFinalizableObject
    {
        ~ReregisteredFinalizableObject()
        {
            Interlocked.Increment(ref _reregisteredFinalizerCallCount);
        }
    }
}
