using System.Diagnostics;
using System.Globalization;

namespace ManagedDotnetGC;

/// <summary>
/// Opt-in per-collection phase profile (M3): set DOTNET_GCStatsFile to a path and every
/// collection appends one CSV row (written after RestartEE, so file I/O never extends the
/// pause it measures). Alloc-path counters are cumulative and repeated on every row, so the
/// last row doubles as the process summary even on abnormal exit. When the variable is not
/// set, every instrumentation site reduces to one predictable branch.
/// </summary>
internal static class GcStats
{
    public static bool Enabled { get; private set; }

    private static StreamWriter? _writer;

    // --- Alloc-path counters: updated by mutator threads, hence interlocked ---
    public static long WindowCount;
    public static long WindowTicks;   // includes lock waits: this is what app threads lose
    public static long BlockCount;
    public static long SpanCount;

    // --- win_ms interior (M7 census): lock WAIT vs carve WORK under the lock vs inline
    // zero-at-carve. wait+carve+zero < win; the remainder is FixAllocContext + timestamps.
    // This split decides which big rock is next: NT zeroing or supply sharding. ---
    public static long WindowWaitTicks;
    public static long WindowCarveTicks;
    public static long WindowZeroTicks;

    // --- Window-source split (M7, interlocked — shard locks don't serialize these):
    // hole carves always zero inline; clean windows arrived pre-zeroed ---
    public static long HoleWindowCount;
    public static long CleanWindowCount;

    // --- Zeroing accumulators: interlocked (zeroer + concurrent carves) ---
    public static long ZeroBytes;
    public static long ZeroTicks;

    // --- Mark-phase scratch, filled by MarkPhase under STW ---
    public static long RootsTicks;
    public static long FReachableTicks;
    public static long HandleTicks;
    public static long DependentTicks;
    public static long AfterScanTicks;
    public static long WeakTicks;
    public static long MarkedCount;
    public static long MarkedBytes;

    // --- Card-scan scratch (young collections, M4) ---
    public static long CardScanTicks;
    public static long CardRegionsScanned;

    // --- Concurrent-cycle scratch (SPEC-M6 v2), filled by CollectFullCycle; zero on
    // inline collections. pause_us stays the whole-cycle wall for those rows; these
    // columns are the honest split. ---
    public static long CyclePauseATicks;
    public static long CycleWindowTicks;
    public static long CyclePauseBTicks;
    public static long CycleWindowMarked; // objects marked concurrently (rest = remark)

    // --- Pause-B interior (added after the first default-on soak measured ~62 ms of
    // pause B unaccounted: cards/sweep/MarkTail columns explained <5 ms of it) ---
    public static long CycleSuspendBTicks; // the second SuspendEE
    public static long CycleRescanTicks;   // BufferStrongRoots #2: root re-scan + handles + f-reachable
    public static long CycleDrain2Ticks;   // remark ParallelDrainMark over the re-buffered roots

    // --- Concurrent card pre-drain (SPEC-M6 §9): world-running time inside the window,
    // spent so the pause-B rescan/drain2/cards slices shrink ---
    public static long CyclePreDrainTicks;
    public static long CyclePreDrainCards;
    public static long CyclePreDrainPasses;

    // --- Concurrent sweep (M6.5): world-running time after RestartEE rebuilding the
    // allocation supply; sweep_us reads ~0 on these cycles ---
    public static long CycleConcurrentSweepTicks;

    // Post-restart free-region decommit (TrimOutsidePause), every collection kind. That
    // soak's verdict: trim was 46 of pause B's 66 ms — so it left the pause entirely;
    // this column is world-running time on the triggering thread, not pause time.
    public static long TrimTicks;

    // --- Memory exchange-rate attribution (M7): why this collection ran full ("" on
    // young rows), set by the trigger decision in Collect ---
    public static string FullReason = "";

    public static void Initialize()
    {
        var path = Environment.GetEnvironmentVariable("DOTNET_GCStatsFile");

        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            _writer = new StreamWriter(path, append: false) { AutoFlush = true };
            _writer.WriteLine("gc,kind,pause_us,suspend_us,fixctx_us,roots_us,freach_us,handles_us,dep_us,after_us,weak_us,cards_us,card_regions,sweep_us,zero_us,zero_mb,marked_n,marked_mb,live_mb,committed_mb,win_n,win_ms,wait_ms,carve_ms,wzero_ms,blk_n,span_n,hole_n,clean_n,pause_a_us,window_us,pause_b_us,window_marked_n,bsusp_us,rescan_us,drain2_us,predrain_us,predrain_cards,predrain_passes,csweep_us,trim_us,pool_mb,bfresh_n,breo_n,bold_n,holes_mb,tail_mb,cls_n,clsfree_mb,spanreg_n,budget_mb,reason");
            Enabled = true;
        }
        catch
        {
            // Stats are diagnostics: an unwritable path must never take the process down
        }
    }

    public static long Timestamp() => Enabled ? Stopwatch.GetTimestamp() : 0;

    private static double ToUs(long ticks) => ticks * 1_000_000.0 / Stopwatch.Frequency;

    public static void BeginCollection()
    {
        RootsTicks = 0;
        FReachableTicks = 0;
        HandleTicks = 0;
        DependentTicks = 0;
        AfterScanTicks = 0;
        WeakTicks = 0;
        MarkedCount = 0;
        MarkedBytes = 0;
        CardScanTicks = 0;
        CardRegionsScanned = 0;
        CyclePauseATicks = 0;
        CycleWindowTicks = 0;
        CyclePauseBTicks = 0;
        CycleWindowMarked = 0;
        CycleSuspendBTicks = 0;
        CycleRescanTicks = 0;
        CycleDrain2Ticks = 0;
        CyclePreDrainTicks = 0;
        CyclePreDrainCards = 0;
        CyclePreDrainPasses = 0;
        CycleConcurrentSweepTicks = 0;
        TrimTicks = 0;
    }

    // zeroBytes/zeroTicks are the cumulative alloc-path totals (M4 zero-at-carve moved all
    // zeroing outside pauses, so per-collection deltas would always read zero)
    public static void RecordCollection(
        uint gcNumber, string kind,
        long start, long afterSuspend, long afterFix, long afterMark, long afterSweep, long end,
        long zeroBytes, long zeroTicks, long liveBytes, long committedBytes,
        in RegionAllocator.RegionCensus census, long budget)
    {
        if (_writer is null)
        {
            return;
        }

        var row = string.Create(CultureInfo.InvariantCulture,
            $"{gcNumber},{kind},{ToUs(end - start):F0},{ToUs(afterSuspend - start):F0},{ToUs(afterFix - afterSuspend):F0},{ToUs(RootsTicks):F0},{ToUs(FReachableTicks):F0},{ToUs(HandleTicks):F0},{ToUs(DependentTicks):F0},{ToUs(AfterScanTicks):F0},{ToUs(WeakTicks):F0},{ToUs(CardScanTicks):F0},{CardRegionsScanned},{ToUs(afterSweep - afterMark):F0},{ToUs(zeroTicks):F0},{zeroBytes / 1048576.0:F1},{MarkedCount},{MarkedBytes / 1048576.0:F1},{liveBytes / 1048576.0:F1},{committedBytes / 1048576.0:F1},{Volatile.Read(ref WindowCount)},{ToUs(Volatile.Read(ref WindowTicks)) / 1000.0:F1},{ToUs(Volatile.Read(ref WindowWaitTicks)) / 1000.0:F1},{ToUs(Volatile.Read(ref WindowCarveTicks)) / 1000.0:F1},{ToUs(Volatile.Read(ref WindowZeroTicks)) / 1000.0:F1},{Volatile.Read(ref BlockCount)},{Volatile.Read(ref SpanCount)},{Volatile.Read(ref HoleWindowCount)},{Volatile.Read(ref CleanWindowCount)},{ToUs(CyclePauseATicks):F0},{ToUs(CycleWindowTicks):F0},{ToUs(CyclePauseBTicks):F0},{CycleWindowMarked},{ToUs(CycleSuspendBTicks):F0},{ToUs(CycleRescanTicks):F0},{ToUs(CycleDrain2Ticks):F0},{ToUs(CyclePreDrainTicks):F0},{CyclePreDrainCards},{CyclePreDrainPasses},{ToUs(CycleConcurrentSweepTicks):F0},{ToUs(TrimTicks):F0},{census.PooledCommittedBytes / 1048576.0:F1},{census.BumpFresh},{census.BumpReopened},{census.BumpOld},{census.LinkedHoleBytes / 1048576.0:F1},{census.BumpTailBytes / 1048576.0:F1},{census.ClassRegions},{census.ClassFreeBytes / 1048576.0:F1},{census.SpanRegions},{budget / 1048576.0:F1},{FullReason}");

        _writer.WriteLine(row);
    }
}
