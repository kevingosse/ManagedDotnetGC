using NativeObjects;

namespace ManagedDotnetGC;

/// <summary>
/// A lock that is safe to wait on from a thread in cooperative GC mode. Waiting always happens
/// in preemptive mode, so SuspendEE never deadlocks on a queued waiter. The switch back to
/// cooperative mode happens after the acquire: a thread that wins the lock while a GC is in
/// flight parks in DisablePreemptiveGC before it can touch any protected state, which is what
/// lets the collector mutate that state without taking the lock itself while the world is
/// stopped (see SPEC-M2 §8.1). With the world running, the collector goes through the lock
/// like any mutator — the post-restart pool trim does exactly that (M6 stage 3).
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
            // Yield-first backoff: Sleep(1) parks a full ~15.6 ms timer quantum,
            // three orders of magnitude past any alloc-lock hold (the concurrent
            // sweep's publish traffic measured exactly that stall). Reserve it for
            // genuinely long waits — a GC.Collect queued on _gcLock behind an entire
            // concurrent cycle — reached only after ~1 ms of yielding.
            Thread.Sleep(++iterations >= 1024 && (iterations & 0x1F) == 0 ? 1 : 0);
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
