using System.Runtime.CompilerServices;
using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// Tests that finalizers are called correctly for collected objects
/// </summary>
public class FinalizerTest() : TestBase("Finalizers", GcFeature.Finalization)
{
    private static int _finalizerCallCount;
    private static int _suppressedFinalizerCallCount;
    private static int _reregisteredFinalizerCallCount;
    private static int _reregisterFromFinalizerCallCount;

    public override void Setup()
    {
        _finalizerCallCount = 0;
        _suppressedFinalizerCallCount = 0;
        _reregisteredFinalizerCallCount = 0;
        _reregisterFromFinalizerCallCount = 0;
    }

    public override void Run()
    {
        TestFinalizerRuns();
        TestMultipleFinalizersRun();
        TestSuppressFinalize();
        TestReRegisterForFinalize();
        TestReRegisterFromFinalizer();
        TestBackgroundFinalizationWithoutWait();
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

    // A finalizer that calls SuppressFinalize followed by *two* ReRegisterForFinalize
    // must re-arm finalization. Net effect on the "finalizer run" bit / queue:
    //   SuppressFinalize      -> sets the bit (logical "don't finalize")
    //   ReRegisterForFinalize -> bit was set, so clear it WITHOUT re-queueing
    //   ReRegisterForFinalize -> bit now clear, so re-queue the object
    // i.e. one net extra registration, so the object is finalized a second time.
    // We re-arm only on the first finalization so the count is deterministic; doing
    // it unconditionally (as in the obvious version of this finalizer) would re-arm
    // every time and finalize the object once per GC forever. Behavior verified
    // against the real .NET runtime: the finalizer runs exactly twice here.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void TestReRegisterFromFinalizer()
    {
        _reregisterFromFinalizerCallCount = 0;

        AllocateReRegisterFromFinalizerObject();

        // The first finalization re-arms the object; it then needs another GC to be
        // moved back to the f-reachable queue before the second finalization runs.
        // Collect several times so both finalizations have a chance to happen.
        for (int i = 0; i < 5; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        var count = Volatile.Read(ref _reregisterFromFinalizerCallCount);
        if (count != 2)
            throw new Exception(
                $"TestReRegisterFromFinalizer: finalizer ran {count} time(s), expected 2 " +
                "(SuppressFinalize + ReRegisterForFinalize + ReRegisterForFinalize inside the finalizer must re-arm it exactly once)");
    }

    // Finalizers must run in the background after a GC even when the program never
    // calls GC.WaitForPendingFinalizers. That relies on the GC waking the finalizer
    // thread via IGCToCLR.EnableFinalization. The other tests above all mask this:
    // GC.WaitForPendingFinalizers itself calls EnableFinalization on the runtime side,
    // so it wakes the thread for us. This test deliberately never calls it.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void TestBackgroundFinalizationWithoutWait()
    {
        _finalizerCallCount = 0;

        // Park the finalizer thread on its long wait first. Each finalizer-thread
        // iteration starts with a ~2s timed wait before dropping into an infinite
        // wait; if we collected while still inside that window the thread could pick
        // up the object via the timeout rather than via EnableFinalization, hiding the
        // bug. So drain any pending work, then sleep past the 2s window.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        Thread.Sleep(3000);

        AllocateFinalizableObject();
        GC.Collect();
        // Intentionally NOT calling GC.WaitForPendingFinalizers(): that path wakes the
        // finalizer thread itself. Here only the GC's EnableFinalization call can do it.

        // Poll for up to ~5s for the background finalizer to run.
        int count = 0;
        for (int i = 0; i < 100; i++)
        {
            count = Volatile.Read(ref _finalizerCallCount);
            if (count >= 1)
                break;
            Thread.Sleep(50);
        }

        // Drain the queue so a delayed finalizer (in the failing case) can't bleed into
        // later tests that share _finalizerCallCount.
        GC.WaitForPendingFinalizers();

        if (count != 1)
            throw new Exception(
                $"TestBackgroundFinalizationWithoutWait: finalizer ran {count} time(s) within the timeout, expected 1. " +
                "The GC is likely not calling EnableFinalization to wake the finalizer thread.");
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

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AllocateReRegisterFromFinalizerObject()
    {
        _ = new ReRegisterFromFinalizerObject();
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

    private class ReRegisterFromFinalizerObject
    {
        ~ReRegisterFromFinalizerObject()
        {
            // Re-arm only on the first finalization to keep the count deterministic.
            if (Interlocked.Increment(ref _reregisterFromFinalizerCallCount) == 1)
            {
                GC.SuppressFinalize(this);
                GC.ReRegisterForFinalize(this);
                GC.ReRegisterForFinalize(this);
            }
        }
    }
}
