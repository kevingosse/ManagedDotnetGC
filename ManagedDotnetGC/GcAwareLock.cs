using NativeObjects;

namespace ManagedDotnetGC;

/// <summary>
/// A lock that is safe to wait on from a thread in cooperative GC mode. Waiting always happens
/// in preemptive mode, so SuspendEE never deadlocks on a queued waiter. The switch back to
/// cooperative mode happens after the acquire: a thread that wins the lock while a GC is in
/// flight parks in DisablePreemptiveGC before it can touch any protected state, which is what
/// lets the collector mutate that state without taking the lock itself (see SPEC-M2 §8.1).
/// Critical sections must do bounded work and never block; after any acquire, previously-read
/// protected state must be treated as stale.
/// </summary>
internal class GcAwareLock
{
    private readonly IGCToCLRInvoker _gcToClr;
    private int _state;

    public GcAwareLock(IGCToCLRInvoker gcToClr)
    {
        _gcToClr = gcToClr;
    }

    public void Acquire()
    {
        // Brief spin in the current mode: sections are short, most acquires succeed here
        for (int i = 0; i < 30; i++)
        {
            if (TryAcquire())
            {
                return;
            }

            Thread.SpinWait(20);
        }

        // Contended: wait in preemptive mode so a suspending GC never waits on us
        var wasCooperative = _gcToClr.EnablePreemptiveGC();

        var iterations = 0;

        while (!TryAcquire())
        {
            Thread.Sleep((++iterations & 0x1F) == 0 ? 1 : 0);
        }

        if (wasCooperative)
        {
            // May park here until an in-flight GC completes, while already owning the lock.
            // That is by design: the GC never acquires this lock, and we cannot proceed into
            // the critical section until the world restarts.
            _gcToClr.DisablePreemptiveGC();
        }
    }

    public bool TryAcquire() => Interlocked.CompareExchange(ref _state, 1, 0) == 0;

    public void Release() => Volatile.Write(ref _state, 0);
}
