using System.Runtime.CompilerServices;
using System.Runtime.ConstrainedExecution;
using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// Tests CriticalFinalizerObject behavior.
/// The CLR guarantees that critical finalizers run after all regular finalizers
/// for the same GC cycle.
/// </summary>
public class CriticalFinalizerTest : TestBase
{
    private static int _criticalFinalizerCount;
    private static int _regularFinalizerCount;

    // Monotonically increasing sequence counter shared between both finalizer types.
    // Whichever finalizer runs first gets a lower sequence number.
    private static int _seqCounter;
    private static int _regularSeq;
    private static int _criticalSeq;

    public CriticalFinalizerTest()
        : base("Critical Finalizers", "Verifies that CriticalFinalizerObject finalizers run after all regular finalizers")
    {
    }

    public override void Setup()
    {
        _criticalFinalizerCount = 0;
        _regularFinalizerCount = 0;
        _seqCounter = 0;
        _regularSeq = 0;
        _criticalSeq = 0;
    }

    public override bool Run()
    {
        if (!TestCriticalFinalizerRuns())
        {
            return false;
        }

        if (!TestCriticalRunsAfterRegular())
        {
            return false;
        }

        return true;
    }

    // A CriticalFinalizerObject subclass must have its finalizer invoked.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool TestCriticalFinalizerRuns()
    {
        _criticalFinalizerCount = 0;
        AllocateCriticalObject();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        return Volatile.Read(ref _criticalFinalizerCount) == 1;
    }

    // When both a regular finalizable object and a critical finalizable object are
    // collected in the same GC cycle, the regular finalizer must run first.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool TestCriticalRunsAfterRegular()
    {
        _seqCounter = 0;
        _regularSeq = 0;
        _criticalSeq = 0;

        AllocateBothObjects();
        GC.Collect();
        GC.WaitForPendingFinalizers();

        int regular = Volatile.Read(ref _regularSeq);
        int critical = Volatile.Read(ref _criticalSeq);

        // Both must have run
        if (regular == 0 || critical == 0)
        {
            return false;
        }

        // Critical must have a higher (later) sequence number
        return critical > regular;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AllocateCriticalObject()
    {
        _ = new TrackingCriticalObject();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AllocateBothObjects()
    {
        _ = new TrackingRegularObject();
        _ = new TrackingCriticalObject();
    }

    private class TrackingRegularObject
    {
        ~TrackingRegularObject()
        {
            Interlocked.Increment(ref _regularFinalizerCount);
            Volatile.Write(ref _regularSeq, Interlocked.Increment(ref _seqCounter));
        }
    }

    private class TrackingCriticalObject : CriticalFinalizerObject
    {
        ~TrackingCriticalObject()
        {
            Interlocked.Increment(ref _criticalFinalizerCount);
            Volatile.Write(ref _criticalSeq, Interlocked.Increment(ref _seqCounter));
        }
    }
}
