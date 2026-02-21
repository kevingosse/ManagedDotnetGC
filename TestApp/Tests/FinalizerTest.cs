using System.Runtime.CompilerServices;
using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// Tests that finalizers are called correctly for collected objects
/// </summary>
public class FinalizerTest() : TestBase("Finalizers")
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

    public override void Run()
    {
        TestFinalizerRuns();
        TestMultipleFinalizersRun();
        TestSuppressFinalize();
        TestReRegisterForFinalize();
    }

    // A single finalizable object goes out of scope; its finalizer must run.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void TestFinalizerRuns()
    {
        _finalizerCallCount = 0;
        AllocateFinalizableObject();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var count = Volatile.Read(ref _finalizerCallCount);
        if (count != 1)
            throw new Exception($"TestFinalizerRuns: finalizer ran {count} time(s), expected 1");
    }

    // Multiple finalizable objects go out of scope; every finalizer must run.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void TestMultipleFinalizersRun()
    {
        _finalizerCallCount = 0;

        for (int i = 0; i < 5; i++)
        {
            AllocateFinalizableObject();
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();

        var count = Volatile.Read(ref _finalizerCallCount);
        if (count != 5)
            throw new Exception($"TestMultipleFinalizersRun: {count} finalizer(s) ran, expected 5");
    }

    // GC.SuppressFinalize must prevent the finalizer from running.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void TestSuppressFinalize()
    {
        _suppressedFinalizerCallCount = 0;

        AllocateSuppressedObject();
        GC.Collect();
        GC.WaitForPendingFinalizers();

        var count = Volatile.Read(ref _suppressedFinalizerCallCount);
        if (count != 0)
            throw new Exception($"TestSuppressFinalize: finalizer ran {count} time(s) despite GC.SuppressFinalize, expected 0");
    }

    // GC.ReRegisterForFinalize after GC.SuppressFinalize must restore finalization.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void TestReRegisterForFinalize()
    {
        _reregisteredFinalizerCallCount = 0;

        AllocateSuppressedThenReregisteredObject();
        GC.Collect();
        GC.WaitForPendingFinalizers();

        var count = Volatile.Read(ref _reregisteredFinalizerCallCount);
        if (count != 1)
            throw new Exception($"TestReRegisterForFinalize: finalizer ran {count} time(s) after GC.ReRegisterForFinalize, expected 1");
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
