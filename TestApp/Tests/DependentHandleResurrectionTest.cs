using System.Runtime;
using System.Runtime.CompilerServices;
using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// Tests the interaction between dependent handles and finalization: while the primary awaits
/// finalization (it was resurrected into the f-reachable queue), the secondary must stay alive —
/// this requires the dependent-handle scan to run again after the finalization scan.
///
/// Note that the secondary is promoted as part of the resurrection wave, which happens *after*
/// short weak references are severed: short weak references to both the primary and the secondary
/// are cleared in that same GC, and only trackResurrection weak references observe the objects
/// while the primary sits in the finalization queue. (Validated against the stock GC.)
/// </summary>
public class DependentHandleResurrectionTest() : TestBase("DependentHandle Resurrection", GcFeature.DependentHandles)
{
    private class Finalizable
    {
        ~Finalizable()
        {
        }
    }

    public override void Run()
    {
        var (handle, shortPrimary, longPrimary, longSecondary) = Create();

        try
        {
            GC.Collect();

            // The primary is now pending finalization. A short weak reference to it must have been
            // cleared (severed before the finalization scan)...
            if (shortPrimary.IsAlive)
            {
                throw new Exception("Short weak reference to a pending-finalization primary should have been cleared");
            }

            // ...but a trackResurrection weak reference tracks it through the finalization queue...
            if (!longPrimary.IsAlive)
            {
                throw new Exception("Long weak reference to a pending-finalization primary should still be alive");
            }

            // ...and the dependent secondary is kept alive by the resurrected primary.
            if (!longSecondary.IsAlive)
            {
                throw new Exception("Dependent secondary died while its primary was awaiting finalization");
            }

            GC.WaitForPendingFinalizers();
            GC.Collect();

            // The primary has been finalized and collected: the secondary must die with it.
            if (longPrimary.IsAlive)
            {
                throw new Exception("Primary survived after being finalized");
            }

            if (longSecondary.IsAlive)
            {
                throw new Exception("Dependent secondary survived after its primary was finalized and collected");
            }
        }
        finally
        {
            handle.Dispose();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (DependentHandle Handle, WeakReference ShortPrimary, WeakReference LongPrimary, WeakReference LongSecondary) Create()
    {
        var primary = new Finalizable();
        var secondary = new object();

        var handle = new DependentHandle(primary, secondary);

        return (handle,
            new WeakReference(primary),
            new WeakReference(primary, trackResurrection: true),
            new WeakReference(secondary, trackResurrection: true));
    }
}
