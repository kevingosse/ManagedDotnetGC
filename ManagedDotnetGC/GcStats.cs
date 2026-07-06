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

    // --- Window-source split (M7, written under the alloc lock): hole carves always zero
    // inline; clean windows arrived pre-zeroed (fresh commit or the background zeroer) ---
    public static long HoleWindowCount;
    public static long CleanWindowCount;

    // --- Zeroing accumulators: only written under the alloc lock or during STW ---
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
            _writer.WriteLine("gc,kind,pause_us,suspend_us,fixctx_us,roots_us,freach_us,handles_us,dep_us,after_us,weak_us,cards_us,card_regions,sweep_us,zero_us,zero_mb,marked_n,marked_mb,live_mb,committed_mb,win_n,win_ms,blk_n,span_n,hole_n,clean_n");
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
    }

    // zeroBytes/zeroTicks are the cumulative alloc-path totals (M4 zero-at-carve moved all
    // zeroing outside pauses, so per-collection deltas would always read zero)
    public static void RecordCollection(
        uint gcNumber, string kind,
        long start, long afterSuspend, long afterFix, long afterMark, long afterSweep, long end,
        long zeroBytes, long zeroTicks, long liveBytes, long committedBytes)
    {
        if (_writer is null)
        {
            return;
        }

        var row = string.Create(CultureInfo.InvariantCulture,
            $"{gcNumber},{kind},{ToUs(end - start):F0},{ToUs(afterSuspend - start):F0},{ToUs(afterFix - afterSuspend):F0},{ToUs(RootsTicks):F0},{ToUs(FReachableTicks):F0},{ToUs(HandleTicks):F0},{ToUs(DependentTicks):F0},{ToUs(AfterScanTicks):F0},{ToUs(WeakTicks):F0},{ToUs(CardScanTicks):F0},{CardRegionsScanned},{ToUs(afterSweep - afterMark):F0},{ToUs(zeroTicks):F0},{zeroBytes / 1048576.0:F1},{MarkedCount},{MarkedBytes / 1048576.0:F1},{liveBytes / 1048576.0:F1},{committedBytes / 1048576.0:F1},{Volatile.Read(ref WindowCount)},{ToUs(Volatile.Read(ref WindowTicks)) / 1000.0:F1},{Volatile.Read(ref BlockCount)},{Volatile.Read(ref SpanCount)},{Volatile.Read(ref HoleWindowCount)},{Volatile.Read(ref CleanWindowCount)}");

        _writer.WriteLine(row);
    }
}
