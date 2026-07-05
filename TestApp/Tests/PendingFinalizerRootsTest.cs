using System.Runtime.CompilerServices;
using TestApp.TestFramework;

namespace TestApp.Tests;

/// <summary>
/// Tests that objects sitting in the finalization queue (queued, finalizer not run yet) are strong
/// roots from the very start of the next collection: a short weak reference created during the
/// pending-finalization window (via a trackResurrection reference) must survive a collection that
/// happens while the object still waits for its finalizer.
/// (docs/missing-features.md item 3.3)
/// </summary>
public class PendingFinalizerRootsTest() : TestBase("Pending Finalizer Roots", GcFeature.FinalizationQueueRoots)
{
    private static readonly ManualResetEventSlim GateEvent = new(false);
    private static volatile bool _gateEntered;
    private static volatile bool _payloadFinalized;

    private sealed class Gate
    {
        ~Gate()
        {
            _gateEntered = true;
            GateEvent.Wait();
        }
    }

    private sealed class Payload
    {
        ~Payload() => _payloadFinalized = true;
    }

    public override void Run()
    {
        GateEvent.Reset();
        _gateEntered = false;
        _payloadFinalized = false;

        // First provably block the finalizer thread: queue the gate and wait until its finalizer
        // has *entered*. Only then queue the payload — it can never be dequeued before we're done.
        BlockFinalizerThread();

        var longWeak = CreatePayload();

        GC.Collect();

        if (_payloadFinalized)
        {
            throw new Exception("Test setup failed: the payload was finalized despite the blocked finalizer thread");
        }

        // The payload waits for its finalizer. A trackResurrection reference still sees it...
        if (!longWeak.IsAlive)
        {
            throw new Exception("Long weak reference to a pending-finalization object was cleared");
        }

        // ...so we can legally hand out new references. Wrap one in a *short* weak reference.
        var shortWeak = CreateShortWeak(longWeak);

        GC.Collect();

        // The object is still strongly reachable from the GC's perspective (it sits in the
        // finalization queue), so the short weak reference must survive this collection.
        if (!shortWeak.IsAlive)
        {
            throw new Exception(
                "Short weak reference to an object awaiting finalization was cleared: " +
                "the finalization queue must be scanned as roots at the start of the mark phase");
        }

        GateEvent.Set();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        if (!_payloadFinalized)
        {
            throw new Exception("Payload finalizer never ran after the gate was released");
        }

        if (shortWeak.IsAlive || longWeak.IsAlive)
        {
            throw new Exception("Payload survived after being finalized and collected");
        }
    }

    public override void Cleanup()
    {
        // Never leave the finalizer thread blocked, even if the test failed
        GateEvent.Set();
        GC.WaitForPendingFinalizers();
    }

    private static void BlockFinalizerThread()
    {
        CreateGate();

        GC.Collect();

        var deadline = Environment.TickCount64 + 10_000;

        while (!_gateEntered)
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new Exception("Test setup failed: the gate finalizer did not run within 10 seconds");
            }

            Thread.Sleep(10);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CreateGate()
    {
        _ = new Gate();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreatePayload()
    {
        return new WeakReference(new Payload(), trackResurrection: true);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateShortWeak(WeakReference longWeak)
    {
        var target = longWeak.Target
            ?? throw new Exception("Long weak reference lost its target while the object was pending finalization");

        return new WeakReference(target, trackResurrection: false);
    }
}
