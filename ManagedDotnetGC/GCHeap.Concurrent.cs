using System.Runtime.InteropServices;
using static ManagedDotnetGC.Log;

namespace ManagedDotnetGC;

unsafe partial class GCHeap
{
    // DOTNET_GCConcurrentCycles (SPEC-M6 v2): full collections split into a root pause,
    // a mark window, and a remark+reclaim pause, orchestrated by the triggering thread.
    // Stage 1 keeps the window empty (the whole trace runs STW in pause B) to validate
    // the two-suspension EE choreography; stage 2 moves the trace into the window.
    private bool _concurrentCycles;

    // True from pause A until the end of pause B (§6.3): budget-triggered collect
    // requests return immediately instead of queueing on _gcLock — a young collection
    // could not run (the cycle holds the lock), and what is reclaimable is exactly what
    // the in-flight cycle is computing.
    private volatile bool _fullCycleInFlight;

    /// <summary>
    /// The two-pause full collection (SPEC-M6 v2 §5). Callers hold <c>_gcLock</c> and
    /// run on an EE thread in preemptive mode — the standard GC-induction state, from
    /// which SuspendEE/RestartEE may be called repeatedly.
    ///
    /// Correctness (§4): pause A buffers every strong root; the trace reads the live
    /// heap; every ref stored while the world runs dirties a card through the existing
    /// barrier; pause B re-scans roots (a ref can retreat into a register) and scans
    /// dirty cards over marked objects of every region age, then runs the unchanged
    /// mark-dependent tail and sweep on final marks.
    /// </summary>
    private void CollectFullCycle()
    {
        const int condemned = 2;

        GcStats.BeginCollection();
        var tStart = GcStats.Timestamp();

        _fullCycleInFlight = true;

        ScanContext scanContext = default;
        scanContext.promotion = true;
        scanContext._unused1 = GCHandle.ToIntPtr(_handle);

        var scanRootsCallback = (delegate* unmanaged<GCObject**, ScanContext*, uint, void>)&ScanRootsCallback;

        // ---- Pause A: root capture ----
        _gcToClr.SuspendEE(SUSPEND_REASON.SUSPEND_FOR_GC);
        _regionAllocator.EnterGateForCollection();

        var tSuspended = GcStats.Timestamp();

        Write("Full cycle: pause A (root capture)");
        NotifyGcStartWork(condemned, 2);

        // Full marks rebuild liveness from scratch. Cards cleared here make the
        // pause-B remark set exactly the window's writes (§5.2): pre-window edges are
        // subsumed by the full trace.
        _regionAllocator.ClearMarks();
        _regionAllocator.ResetLiveBytes();
        _regionAllocator.ClearCards();

        FixAllocContexts();

        // One bracket per collection, same face as the inline path (GcCallbackBracketTest;
        // whether is_bgc/is_concurrent buy EE-side behavior we want is a §8 item)
        NotifyBeforeGcScanRoots(condemned, isBgc: false, isConcurrent: false);
        BufferStrongRoots(scanRootsCallback, condemned, &scanContext);

        var tPauseAEnd = GcStats.Timestamp();

        _regionAllocator.ExitGateForCollection();
        _gcToClr.RestartEE(finishedGC: false);

        // ---- Mark window: the closure over the pause-A roots, concurrent with the
        // mutators (§5.3). Safe because nothing reclaims or moves memory here: sweep,
        // decommit and plug rewrites of live extents all happen under the pauses, so a
        // popped ref always points at an intact object (dead-by-now = floating
        // garbage); mark bits and LiveBytes go to GC-private memory; every mutator ref
        // store lands in the card table for pause B's remark. The one EE call — the
        // deferred collectible LoaderAllocator edges — runs on this (EE) thread, and
        // collectible unloading cannot race the very GC that must prove it dead.
        ParallelDrainMark();

        // ---- Pause B: remark + reclaim ----
        _gcToClr.SuspendEE(SUSPEND_REASON.SUSPEND_FOR_GC);
        _regionAllocator.EnterGateForCollection();

        Write("Full cycle: pause B (remark + reclaim)");

        // Contexts handed out while the world ran get plugged like always
        FixAllocContexts();

        // Remark (§5.4): roots may have moved during the window — re-capture and trace
        // the delta, then scan every card the window's stores dirtied. Fresh regions
        // included: the young-scan skip is only sound when marking is entirely STW.
        // No second BeforeGcScanRoots: the bracket notifications are once-per-GC (the
        // remark is our re-scan of the same logical root set; extra GcScanRoots calls
        // are normal, stock issues several per GC).
        BufferStrongRoots(scanRootsCallback, condemned, &scanContext);
        ParallelDrainMark();
        ScanCards(includeFresh: true);

        // Conditionally-live wrappers, evaluated once on the completed strong closure
        ScanRefCountedHandles();

        var tMarked = GcStats.Timestamp();

        MarkTail(condemned, &scanContext);

        Write("Full cycle: sweep");
        SweepAndAccount(young: false);

        var tSwept = GcStats.Timestamp();

        ApplyBudgetAndTrim(young: false);

        var gcNumber = _gcCount;
        Interlocked.Increment(ref _gcCount);

        NotifyGcDone(condemned);

        _fullCycleInFlight = false;

        _regionAllocator.ExitGateForCollection();
        _gcToClr.RestartEE(finishedGC: true);

        if (GcStats.Enabled)
        {
            // Column mapping for cycle rows: fix_ms ≈ pause A tail (root capture),
            // mark_ms spans the gap + trace + remark
            GcStats.RecordCollection(gcNumber, "full-cycle",
                tStart, tSuspended, tPauseAEnd, tMarked, tSwept, GcStats.Timestamp(),
                Volatile.Read(ref GcStats.ZeroBytes), Volatile.Read(ref GcStats.ZeroTicks),
                _lastLiveBytes, _regionAllocator.CommittedRegionBytes);
        }

        _gcToClr.EnableFinalization(GetNumberOfFinalizable() > 0);

        _regionZeroer?.Kick();
    }

    /// <summary>
    /// Enumerates every strong root — stacks/registers/statics, strong + pinned
    /// handles, the f-reachable queues — onto <see cref="_markStack"/> without
    /// draining (M5's buffering mechanism). Runs under STW; the buffer is GC-private,
    /// so it safely spans the resume between pause A and the trace.
    /// </summary>
    private void BufferStrongRoots(delegate* unmanaged<GCObject**, ScanContext*, uint, void> scanRootsCallback, int condemned, ScanContext* scanContext)
    {
        _bufferMarkRoots = true;

        _gcToClr.GcScanRoots((IntPtr)scanRootsCallback, condemned, 2, scanContext);
        MarkFReachableQueues();
        ScanHandles();

        _bufferMarkRoots = false;
    }
}
