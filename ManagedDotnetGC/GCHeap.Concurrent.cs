using System.Numerics;
using System.Runtime.InteropServices;
using static ManagedDotnetGC.Log;

namespace ManagedDotnetGC;

unsafe partial class GCHeap
{
    // Concurrent full cycles (SPEC-M6 v2): full collections split into a root pause,
    // a mark window (concurrent with mutators), and a remark+reclaim pause, orchestrated
    // by the triggering thread. Default-on via stock DOTNET_gcConcurrent since stage 3;
    // DOTNET_GCConcurrentCycles overrides either way for A/B runs.
    private bool _concurrentCycles;

    // Concurrent card pre-drain (SPEC-M6 §9): consume window-dirtied cards while the
    // world runs, so pause B's remark only sees the last pass's re-dirty delta and the
    // buffer-time mark skip covers the window-born survivors the pre-drain marked.
    //
    // Default-OFF after measurement (2026-07-06): the 13–15 ms "drain2" this was built
    // to reclaim turned out to be a Sleep(1) timer quantum in the drain termination
    // (see ParallelDrainMark), not marking work. With that fixed, the STW remark card
    // scan costs 3–6 ms on the densest scenario, while pre-drain passes stretched the
    // window 3–5× on store-heavy workloads and the longer window *grew* the remark
    // delta (9–18 ms card scans). Kept behind DOTNET_GCCardPreDrain for A/B runs and
    // for a future huge-heap/low-store profile where the trade could reverse.
    private bool _cardPreDrain;

    // Pause-A snapshot of each region's shape: -1 = never scan (Free, SpanExtension,
    // or carved after pause A), 0 = bump/size-class, k > 0 = span of k regions. The
    // pre-drain trusts only this — the entry fields of regions carved *during* the
    // window (pool recycles included) can be mid-publication when a worker reads them,
    // and consuming a card without scanning its objects loses remark information, so
    // window-born regions keep their cards for pause B. Kinds recorded here are stable
    // through the window: regions only change kind at a sweep, and sweeps are STW.
    private int[]? _preDrainPlan;
    private int _preDrainPlanCount;

    private const int PreDrainMaxPasses = 4;
    private const int PreDrainRepeatThreshold = 64; // cards; ~128 KB of re-dirtied heap

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

        if (_cardPreDrain)
        {
            BuildPreDrainPlan();
        }

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

        // Card pre-drain passes (§9, opt-in — see the field note): consume the cards
        // the window's stores dirtied and trace the marked objects under them, so pause
        // B's root re-scan skips the window-born survivors (buffer-time mark skip) and
        // its card scan only sees the re-dirty delta since the last pass. Repeat while
        // a pass keeps finding real volume; the cap bounds store-heavy workloads where
        // the sequence cannot converge.
        if (_cardPreDrain)
        {
            var tPreDrain = GcStats.Timestamp();
            var passes = 0;
            var totalConsumed = 0L;
            int consumed;

            do
            {
                consumed = PreDrainCardsPass();
                totalConsumed += consumed;
                passes++;
            }
            while (consumed >= PreDrainRepeatThreshold && passes < PreDrainMaxPasses);

            if (GcStats.Enabled)
            {
                GcStats.CyclePreDrainTicks = GcStats.Timestamp() - tPreDrain;
                GcStats.CyclePreDrainCards = totalConsumed;
                GcStats.CyclePreDrainPasses = passes;
            }
        }

        var tWindowEnd = GcStats.Timestamp();
        var windowMarked = GcStats.Enabled ? Volatile.Read(ref GcStats.MarkedCount) : 0;

        // ---- Pause B: remark + reclaim ----
        _gcToClr.SuspendEE(SUSPEND_REASON.SUSPEND_FOR_GC);
        _regionAllocator.EnterGateForCollection();

        GcStats.CycleSuspendBTicks = GcStats.Timestamp() - tWindowEnd;

        Write("Full cycle: pause B (remark + reclaim)");

        // Contexts handed out while the world ran get plugged like always
        FixAllocContexts();

        // Remark (§5.4): roots may have moved during the window — re-capture and trace
        // the delta, then scan every card the window's stores dirtied. Fresh regions
        // included: the young-scan skip is only sound when marking is entirely STW.
        // No second BeforeGcScanRoots: the bracket notifications are once-per-GC (the
        // remark is our re-scan of the same logical root set; extra GcScanRoots calls
        // are normal, stock issues several per GC).
        var tRescan = GcStats.Timestamp();
        BufferStrongRoots(scanRootsCallback, condemned, &scanContext, skipMarked: true);

        var tDrain2 = GcStats.Timestamp();
        GcStats.CycleRescanTicks = tDrain2 - tRescan;

        ParallelDrainMark();
        GcStats.CycleDrain2Ticks = GcStats.Timestamp() - tDrain2;

        var tCards = GcStats.Timestamp();
        ScanCards(includeFresh: true);
        GcStats.CardScanTicks = GcStats.Timestamp() - tCards;

        // Conditionally-live wrappers, evaluated once on the completed strong closure
        ScanRefCountedHandles();

        var tMarked = GcStats.Timestamp();

        MarkTail(condemned, &scanContext);

        Write("Full cycle: sweep");
        SweepAndAccount(young: false);

        var tSwept = GcStats.Timestamp();

        ApplyBudget(young: false);

        var gcNumber = _gcCount;
        Interlocked.Increment(ref _gcCount);

        NotifyGcDone(condemned);

        _fullCycleInFlight = false;

        var tPauseBEnd = GcStats.Timestamp();

        _regionAllocator.ExitGateForCollection();
        _gcToClr.RestartEE(finishedGC: true);

        _gcToClr.EnableFinalization(GetNumberOfFinalizable() > 0);

        TrimOutsidePause(young: false);

        if (GcStats.Enabled)
        {
            // pause_us on cycle rows is whole-cycle wall; the honest split lives in
            // the pause_a/window/pause_b columns
            GcStats.CyclePauseATicks = tPauseAEnd - tStart;
            GcStats.CycleWindowTicks = tWindowEnd - tPauseAEnd;
            GcStats.CyclePauseBTicks = tPauseBEnd - tWindowEnd;
            GcStats.CycleWindowMarked = windowMarked;

            GcStats.RecordCollection(gcNumber, "full-cycle",
                tStart, tSuspended, tPauseAEnd, tMarked, tSwept, GcStats.Timestamp(),
                Volatile.Read(ref GcStats.ZeroBytes), Volatile.Read(ref GcStats.ZeroTicks),
                _lastLiveBytes, _regionAllocator.CommittedRegionBytes,
                _regionAllocator.TakeCensus(), _budget);
        }

        _regionZeroer?.Kick();
    }

    /// <summary>
    /// Enumerates every strong root — stacks/registers/statics, strong + pinned
    /// handles, the f-reachable queues — onto <see cref="_markStack"/> without
    /// draining (M5's buffering mechanism). Runs under STW; the buffer is GC-private,
    /// so it safely spans the resume between pause A and the trace.
    ///
    /// <paramref name="skipMarked"/> is the remark's buffer-time mark skip (SPEC-M6 §9):
    /// ~97% of re-buffered roots are already marked, and marked-at-remark means fully
    /// traced — the window drain terminated on a global idle quorum, and refs stored
    /// during the window are the card remark's job either way. Skipping them here saves
    /// the push/deal/pop/lookup round-trip that measured 13.5 ms of pause B. Pause A
    /// never skips: its dumb-push speed is the design goal, and nothing is marked yet.
    /// </summary>
    private void BufferStrongRoots(delegate* unmanaged<GCObject**, ScanContext*, uint, void> scanRootsCallback, int condemned, ScanContext* scanContext, bool skipMarked = false)
    {
        _bufferMarkRoots = true;
        _skipMarkedRootBuffering = skipMarked;

        _gcToClr.GcScanRoots((IntPtr)scanRootsCallback, condemned, 2, scanContext);
        MarkFReachableQueues();
        ScanHandles();

        _bufferMarkRoots = false;
        _skipMarkedRootBuffering = false;
    }

    /// <summary>
    /// Records, under pause A's suspension, which regions the concurrent pre-drain may
    /// touch (see <see cref="_preDrainPlan"/>). Everything the scan will trust — span
    /// counts included — is captured here while it cannot move.
    /// </summary>
    private void BuildPreDrainPlan()
    {
        var count = _regionAllocator.CarvedCount;

        if (_preDrainPlan is null || _preDrainPlan.Length < count)
        {
            _preDrainPlan = new int[Math.Max(count + (count >> 1), 256)];
        }

        for (int i = 0; i < count; i++)
        {
            var entry = _regionAllocator.GetEntry(i);

            _preDrainPlan[i] = entry->Kind switch
            {
                RegionKind.Bump or RegionKind.SizeClass => 0,
                RegionKind.SpanStart => entry->SpanCount,
                _ => -1,
            };
        }

        _preDrainPlanCount = count;
    }

    /// <summary>
    /// One concurrent pass over the card table (SPEC-M6 §9 card pre-drain), inside the
    /// mark window: workers consume dirty cards region by region and trace the marked
    /// objects under them, marking whatever those objects now reference. Returns the
    /// number of cards consumed.
    ///
    /// Soundness is the pairing "a card is only cleared by someone who then scans the
    /// marked objects overlapping it, after a full fence":
    /// - A store whose card-dirty lands after our clear re-dirties the card; pause B
    ///   scans it. A store whose dirty we consumed was globally visible before the
    ///   clear (mutator order: slot first, card second), hence before the fence, hence
    ///   its slot value is seen by our post-fence reads.
    /// - If the covering object is marked by scan time, we enumerate its current
    ///   fields and see that value. If it is unmarked, then either it gets marked
    ///   later — and its tracer reads the fields after the mark, seeing the value or a
    ///   successor whose store re-dirtied a card — or it stays unmarked through the
    ///   window and pause B's remark traces it (root re-scan) on the frozen heap, or
    ///   it is garbage. The same case analysis as §4, plus the fence.
    /// </summary>
    private int PreDrainCardsPass()
    {
        var cardBase = _regionAllocator.CardTableStorage;

        if (cardBase == 0 || _preDrainPlan is null)
        {
            return 0; // unit tests drive the heap without a card table
        }

        var count = _preDrainPlanCount;
        var consumed = 0;
        var cursor = 0;
        var workerId = -1;

        _workerPool!.Run(() =>
        {
            const int Chunk = 8;

            var id = Interlocked.Increment(ref workerId);
            var stack = _cardScanStacks![id];
            var deferred = _cardScanDeferred![id];
            var local = 0;

            while (true)
            {
                var start = Interlocked.Add(ref cursor, Chunk) - Chunk;

                if (start >= count)
                {
                    break;
                }

                var end = Math.Min(start + Chunk, count);

                for (int i = start; i < end; i++)
                {
                    var plan = _preDrainPlan[i];

                    if (plan == 0)
                    {
                        local += PreDrainRegion(cardBase, i, stack, deferred);
                    }
                    else if (plan > 0)
                    {
                        local += PreDrainSpan(cardBase, i, plan, stack, deferred);
                    }
                }
            }

            if (local > 0)
            {
                Interlocked.Add(ref consumed, local);
            }
        });

        // The deferred collectible edges call into the EE, so only the GC thread takes
        // them (this is the coordinator — an EE thread, same as the window drain's)
        foreach (var deferred in _cardScanDeferred!)
        {
            foreach (var ptr in deferred)
            {
                var loaderAllocator = (GCObject*)_gcToClr.GetLoaderAllocatorObjectForGC((GCObject*)ptr);

                if (loaderAllocator != null)
                {
                    _markStack.Push((nint)loaderAllocator);
                }
            }

            deferred.Clear();
        }

        DrainMarkStack(_markStack, deferredCollectible: null);

        return consumed;
    }

    /// <summary>
    /// Consumes one bump/size-class region's dirty cards with the world running:
    /// snapshot the dirty bytes, clear them byte-granular (a word-wide clear could wipe
    /// a card the barrier dirtied between our read and our store), fence, then scan.
    /// The fence closes the x64 store→load reordering hole: without it our heap reads
    /// could pass the card clear, see pre-store values, and the racing store's dirty —
    /// overwritten by the clear — would be the only record of the mutation.
    /// </summary>
    private int PreDrainRegion(nint cardBase, int index, MarkStack stack, List<nint> deferred)
    {
        const int CardCount = 1 << (Region.Shift - 11); // 1 KB of cards per region

        var cards = (byte*)(cardBase + ((nint)index << (Region.Shift - 11)));
        var snapshot = stackalloc byte[CardCount];
        var consumed = 0;

        for (int w = 0; w < CardCount / 8; w++)
        {
            var word = ((ulong*)cards)[w];
            ((ulong*)snapshot)[w] = word;

            if (word == 0)
            {
                continue;
            }

            for (int b = 0; b < 8; b++)
            {
                if ((word & (0xFFul << (b * 8))) != 0)
                {
                    cards[w * 8 + b] = 0;
                    consumed++;
                }
            }
        }

        if (consumed == 0)
        {
            return 0;
        }

        Thread.MemoryBarrier();

        var regionBase = _regionAllocator.RegionBase(index);

        for (int c = 0; c < CardCount;)
        {
            if (snapshot[c] == 0)
            {
                c++;
                continue;
            }

            var runEnd = c + 1;

            while (runEnd < CardCount && snapshot[runEnd] != 0)
            {
                runEnd++;
            }

            ScanMarkedRange(regionBase, regionBase + ((nint)c << 11), regionBase + ((nint)runEnd << 11), stack);

            c = runEnd;
        }

        DrainMarkStack(stack, deferred);

        return consumed;
    }

    /// <summary>
    /// Span flavor of <see cref="PreDrainRegion"/>: one object per span, so any dirty
    /// card re-enumerates the whole object (same policy as the pause-path scan).
    /// Consuming an unmarked span's cards is covered by the pairing argument — a later
    /// marker traces its current fields, or pause B's root re-scan does.
    /// </summary>
    private int PreDrainSpan(nint cardBase, int index, int spanCount, MarkStack stack, List<nint> deferred)
    {
        var cards = (byte*)(cardBase + ((nint)index << (Region.Shift - 11)));
        var words = spanCount << (Region.Shift - 11 - 3);
        var consumed = 0;

        for (int w = 0; w < words; w++)
        {
            var word = ((ulong*)cards)[w];

            if (word == 0)
            {
                continue;
            }

            for (int b = 0; b < 8; b++)
            {
                if ((word & (0xFFul << (b * 8))) != 0)
                {
                    cards[w * 8 + b] = 0;
                    consumed++;
                }
            }
        }

        if (consumed == 0)
        {
            return 0;
        }

        Thread.MemoryBarrier();

        var obj = (GCObject*)(_regionAllocator.RegionBase(index) + IntPtr.Size);

        if (obj->IsMarked())
        {
            GCObject.EnumerateObjectReferences(obj, stack);
            DrainMarkStack(stack, deferred);
        }

        return consumed;
    }

    /// <summary>
    /// Enumerates the references of every marked object overlapping [start, end) using
    /// the mark bitmap alone. The pause-path walks (card-offset hop + linear ComputeSize
    /// hops) are unsound with the world running — bump cursors advance and hole carves
    /// rewrite plug headers mid-walk — but set bits are exactly marked object starts,
    /// and a marked object is published (§4 store order) and size-stable. Objects the
    /// walk misses because their mark lands concurrently are traced by whoever marked
    /// them. [start, end) is card-aligned, so the bit range is word-aligned.
    /// </summary>
    private static void ScanMarkedRange(nint regionBase, nint start, nint end, MarkStack stack)
    {
        var bitmap = GCObject.MarkBitmap;
        var heapBase = GCObject.MarkHeapBase;

        // Head: a marked object starting before the range can overlap into it
        var firstBit = (start - heapBase) >> 3;
        var prev = FindLastSetBitBefore(bitmap, (regionBase - heapBase) >> 3, firstBit);

        if (prev >= 0)
        {
            var obj = (GCObject*)(heapBase + (nint)(prev << 3));

            if ((nint)obj + (nint)obj->ComputeSize() > start)
            {
                GCObject.EnumerateObjectReferences(obj, stack);
            }
        }

        // Body: every set bit in the range is a marked object start; every card under
        // the range is dirty by construction (runs are maximal), so no per-object
        // overlap check is needed
        var w = firstBit >> 6;
        var wEnd = (end - heapBase) >> 9; // (>> 3 bits, >> 6 words)

        for (; w < wEnd; w++)
        {
            var word = Volatile.Read(ref bitmap[w]);

            while (word != 0)
            {
                var bit = BitOperations.TrailingZeroCount(word);
                word &= word - 1;

                var obj = (GCObject*)(heapBase + (nint)(((w << 6) + bit) << 3));
                GCObject.EnumerateObjectReferences(obj, stack);
            }
        }
    }

    /// <summary>Highest set bit in [lowBit, highBit), or -1. lowBit is region-aligned
    /// (word-aligned by construction); the search is bounded by one region's bitmap
    /// slice and typically ends within a word or two in dense old regions.</summary>
    private static long FindLastSetBitBefore(ulong* bitmap, long lowBit, long highBit)
    {
        if (highBit <= lowBit)
        {
            return -1;
        }

        var w = (highBit - 1) >> 6;
        var lowW = lowBit >> 6;

        // Mask off bits at or above highBit in the first word visited
        var mask = (highBit & 63) == 0 ? ulong.MaxValue : (1ul << (int)(highBit & 63)) - 1;

        for (; w >= lowW; w--)
        {
            var word = bitmap[w] & mask;
            mask = ulong.MaxValue;

            if (word != 0)
            {
                return (w << 6) + 63 - BitOperations.LeadingZeroCount(word);
            }
        }

        return -1;
    }
}
