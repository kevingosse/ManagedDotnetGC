namespace ManagedDotnetGC;

/// <summary>
/// Persistent worker threads for parallel collection phases (M5). The threads live on the
/// GC dll's own NativeAOT runtime — the target EE never sees them — and they must never
/// call into the EE: phase bodies may only touch GC-owned memory and the OS.
/// Run is called by the collecting thread during a suspension; it participates in the
/// work itself and returns when every worker has finished the phase.
/// </summary>
internal sealed class GcWorkerPool
{
    private readonly int _workerCount;
    private readonly SemaphoreSlim _start = new(0);
    private Action? _work;
    private CountdownEvent? _done;

    public GcWorkerPool(int workerCount)
    {
        _workerCount = workerCount;

        for (int i = 0; i < workerCount; i++)
        {
            new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = $"ManagedDotnetGC worker {i}",
            }.Start();
        }
    }

    /// <summary>Total threads that execute a phase body (workers + the calling thread).</summary>
    public int ParticipantCount => _workerCount + 1;

    public void Run(Action work)
    {
        var done = new CountdownEvent(_workerCount);

        _done = done;
        _work = work;
        _start.Release(_workerCount);

        work();

        done.Wait();

        _work = null;
        _done = null;
    }

    private void WorkerLoop()
    {
        while (true)
        {
            _start.Wait();

            var work = _work;
            var done = _done;

            try
            {
                work!();
            }
            finally
            {
                done!.Signal();
            }
        }
    }
}
