using System.Runtime.CompilerServices;
using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// Tests GC.SuppressFinalize semantics at the finalization scan: a suppressed dead object must be
/// dropped outright — not resurrected for a spurious extra collection — its finalizer must never
/// run, and suppress + ReRegisterForFinalize must compose correctly.
/// (docs/missing-features.md item 5.1)
/// </summary>
public class SuppressFinalizeSemanticsTest() : TestBase("SuppressFinalize Semantics", GcFeature.SuppressFinalizeDrop)
{
    private static volatile int _finalizeCount;

    private sealed class Suppressible
    {
        ~Suppressible() => _finalizeCount++;
    }

    public override void Run()
    {
        TestSuppressedObjectIsNotResurrected();
        TestSuppressThenReRegisterRunsOnce();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void TestSuppressedObjectIsNotResurrected()
    {
        int countBefore = _finalizeCount;

        var longWeak = CreateSuppressed();

        GC.Collect();

        // The stock GC drops a suppressed dead object at the finalization scan without
        // resurrecting it: it must be fully gone after a single collection, so even a
        // trackResurrection weak reference is cleared.
        if (longWeak.IsAlive)
        {
            throw new Exception(
                "A suppressed dead object was resurrected: it must be dropped at the finalization " +
                "scan, not moved to the finalization queue");
        }

        GC.WaitForPendingFinalizers();

        if (_finalizeCount != countBefore)
        {
            throw new Exception("The finalizer of a suppressed object ran");
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateSuppressed()
    {
        var obj = new Suppressible();
        GC.SuppressFinalize(obj);
        return new WeakReference(obj, trackResurrection: true);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void TestSuppressThenReRegisterRunsOnce()
    {
        int countBefore = _finalizeCount;

        CreateSuppressedAndReRegistered();

        GC.Collect();
        GC.WaitForPendingFinalizers();

        if (_finalizeCount != countBefore + 1)
        {
            throw new Exception(
                $"Suppress followed by ReRegisterForFinalize should run the finalizer exactly once, " +
                $"ran {_finalizeCount - countBefore} time(s)");
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CreateSuppressedAndReRegistered()
    {
        var obj = new Suppressible();
        GC.SuppressFinalize(obj);
        GC.ReRegisterForFinalize(obj);
        GC.KeepAlive(obj);
    }
}
