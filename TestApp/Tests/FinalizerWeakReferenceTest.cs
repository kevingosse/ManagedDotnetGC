using System.Runtime.CompilerServices;
using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// Tests the interaction between finalizers and weak references.
///
/// Short weak references (trackResurrection: false / GCHandleType.Weak):
///   Cleared when the object becomes f-reachable — i.e. before the finalizer runs.
///
/// Long weak references (trackResurrection: true / GCHandleType.WeakTrackResurrection):
///   Stay valid while the object is in the finalization queue and are only cleared after
///   finalization has completed and the object has been reclaimed.
///
/// Resurrection:
///   A finalizer can store a new strong reference to 'this', preventing collection.
///   A long weak reference created before resurrection remains valid after the finalizer runs.
/// </summary>
public class FinalizerWeakReferenceTest : TestBase
{
    private static int _finalizerCallCount;
    private static ResurrectableObject? _resurrectedInstance;

    public FinalizerWeakReferenceTest()
        : base("Finalizer Weak References")
    {
    }

    public override void Setup()
    {
        _finalizerCallCount = 0;
        _resurrectedInstance = null;
    }

    public override void Run()
    {
        TestShortWeakRefClearedBeforeFinalization();
        TestLongWeakRefTracksFinalization();
        TestResurrection();
    }

    /// <summary>
    /// A short weak reference (trackResurrection: false) must be null after GC.Collect()
    /// even though the finalizer has not yet had a chance to run.  The object is still
    /// "alive" in the f-reachable queue but is no longer considered reachable through
    /// normal roots, so the short weak reference is cleared immediately.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void TestShortWeakRefClearedBeforeFinalization()
    {
        _finalizerCallCount = 0;
        CreateShortWeakRef(out var shortRef);

        GC.Collect();

        // Short weak ref must already be null: object is f-reachable, not reachable
        if (shortRef.IsAlive)
            throw new Exception("TestShortWeakRefClearedBeforeFinalization: short weak ref still alive after GC (expected cleared before finalizer runs)");

        GC.WaitForPendingFinalizers();

        // Confirm the finalizer actually ran (distinguishes from plain collection)
        var count = Volatile.Read(ref _finalizerCallCount);
        if (count != 1)
            throw new Exception($"TestShortWeakRefClearedBeforeFinalization: finalizer ran {count} time(s), expected 1");
    }

    /// <summary>
    /// A long weak reference (trackResurrection: true) must remain valid after GC.Collect()
    /// while the object is sitting in the finalization queue.  It is only cleared once
    /// finalization has completed and a subsequent collection reclaims the object.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void TestLongWeakRefTracksFinalization()
    {
        _finalizerCallCount = 0;
        CreateLongWeakRef(out var longRef);

        GC.Collect();

        // Long weak ref must still be alive: object is pending finalization
        if (!longRef.IsAlive)
            throw new Exception("TestLongWeakRefTracksFinalization: long weak ref cleared after GC (expected alive while object is pending finalization)");

        GC.WaitForPendingFinalizers();
        GC.Collect(); // reclaim the now-finalized object

        // Long weak ref must now be null
        if (longRef.IsAlive)
            throw new Exception("TestLongWeakRefTracksFinalization: long weak ref still alive after finalization and second GC");
    }

    /// <summary>
    /// An object can resurrect itself by storing a strong reference in its finalizer.
    /// After resurrection the object is reachable again, and a long weak reference
    /// created before resurrection must still be valid.  Once the last strong reference
    /// is dropped the object is collected normally (the finalizer does not run again
    /// unless GC.ReRegisterForFinalize is called).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void TestResurrection()
    {
        _resurrectedInstance = null;
        _finalizerCallCount = 0;

        CreateResurrectable(out var longRef);

        GC.Collect();
        GC.WaitForPendingFinalizers();

        // Finalizer ran exactly once
        var count = Volatile.Read(ref _finalizerCallCount);
        if (count != 1)
            throw new Exception($"TestResurrection: finalizer ran {count} time(s) before resurrection check, expected 1");

        // Object was resurrected
        if (_resurrectedInstance == null)
            throw new Exception("TestResurrection: object was not resurrected (static root not set in finalizer)");

        GC.Collect();

        // Long weak ref is still alive because the object is now strongly reachable
        if (!longRef.IsAlive)
            throw new Exception("TestResurrection: long weak ref not alive after resurrection (object should be strongly reachable)");

        // Drop the last strong reference and verify final collection
        _resurrectedInstance = null;
        GC.Collect();
        GC.WaitForPendingFinalizers(); // finalizer does not re-run (no ReRegisterForFinalize)
        GC.Collect();

        if (longRef.IsAlive)
            throw new Exception("TestResurrection: long weak ref still alive after dropping resurrected reference and final GC");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CreateShortWeakRef(out WeakReference weakRef)
    {
        var obj = new FinalizableTracker();
        weakRef = new WeakReference(obj, trackResurrection: false);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CreateLongWeakRef(out WeakReference weakRef)
    {
        var obj = new FinalizableTracker();
        weakRef = new WeakReference(obj, trackResurrection: true);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CreateResurrectable(out WeakReference longRef)
    {
        var obj = new ResurrectableObject();
        longRef = new WeakReference(obj, trackResurrection: true);
    }

    private class FinalizableTracker
    {
        ~FinalizableTracker()
        {
            Interlocked.Increment(ref _finalizerCallCount);
        }
    }

    private class ResurrectableObject
    {
        ~ResurrectableObject()
        {
            Interlocked.Increment(ref _finalizerCallCount);
            _resurrectedInstance = this; // resurrect by creating a new strong reference
        }
    }
}
