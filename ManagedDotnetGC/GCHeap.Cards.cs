using System.Numerics;

namespace ManagedDotnetGC;

unsafe partial class GCHeap
{
    /// <summary>
    /// Scans the remembered set for a young collection (SPEC-M4). Old→young references only
    /// exist where a mutator stored into an old object after the young target was born, and
    /// with the ephemeral range widened to the whole heap the barrier dirtied the
    /// destination's card for every such store. So: for every region that may hold old
    /// objects (Old or Reopened) with a dirty card, enumerate the references of
    /// sticky-marked objects overlapping dirty cards and trace whatever they reach. Fresh
    /// regions need no scan (their live objects are traced from roots or cards directly),
    /// and unmarked objects need none either (they are dead, or they are young and their
    /// fields get traced when something marks them).
    ///
    /// The set of marked objects a young collection starts from is fixed (sticky marks never
    /// appear mid-collection on old objects), so one pass over the cards is complete.
    ///
    /// Parallel (M5): regions are dispensed in chunks to the worker pool; each worker
    /// traces through its own mark stack with CAS-claimed marking, and collectible
    /// LoaderAllocator edges — the one EE call in the trace — are deferred to the GC
    /// thread, because workers must never call into the EE.
    ///
    /// Remark mode (SPEC-M6 v2 §5.4, <paramref name="includeFresh"/>): a concurrent
    /// cycle's pause-B remark must scan dirty cards over Fresh regions too. The young
    /// skip is only sound because young marking is entirely STW — an object traced
    /// early in the pause cannot be mutated later. A concurrently-traced object can,
    /// and its re-dirtied card is the only record of the mutation.
    /// </summary>
    private void ScanCards(bool includeFresh = false)
    {
        var cardBase = _regionAllocator.CardTableStorage;

        if (cardBase == 0)
        {
            return;
        }

        var count = _regionAllocator.CarvedCount;

        if (_workerPool is null || _cardScanStacks is null)
        {
            ScanCardRange(cardBase, 0, count, _markStack, deferredCollectible: null, includeFresh);
            return;
        }

        var cursor = 0;
        var workerId = -1;

        _workerPool.Run(() =>
        {
            const int Chunk = 8;

            var id = Interlocked.Increment(ref workerId);
            var stack = _cardScanStacks[id];
            var deferred = _cardScanDeferred![id];

            while (true)
            {
                var start = Interlocked.Add(ref cursor, Chunk) - Chunk;

                if (start >= count)
                {
                    break;
                }

                ScanCardRange(cardBase, start, Math.Min(start + Chunk, count), stack, deferred, includeFresh);
            }
        });

        // The deferred collectible edges call into the EE, so only the GC thread takes
        // them; anything they reach traces inline (and further collectible edges too)
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
    }

    private void ScanCardRange(nint cardBase, int start, int end, MarkStack stack, List<nint>? deferredCollectible, bool includeFresh = false)
    {
        for (int i = start; i < end; i++)
        {
            var entry = _regionAllocator.GetEntry(i);

            if (!includeFresh && entry->Age == RegionAge.Fresh)
            {
                continue;
            }

            switch (entry->Kind)
            {
                case RegionKind.Bump:
                    // The lookback bound is the WINDOW size, not BumpMaxSize: alloc
                    // contexts span whole windows and the EE's inline fast path places
                    // any object that fits below alloc_limit — bump regions legally
                    // hold ref-bearing objects far above the 32 KB routing threshold
                    // (a 32 KB bound shipped for ~an hour and let Kestrel's large
                    // object[]s smuggle old→young refs past the head search)
                    if (RegionHasDirtyCard(cardBase, i, 1))
                    {
                        ScanRegionCardRuns(cardBase, i, entry->Cursor, Region.WindowSize, stack, deferredCollectible);
                    }
                    break;

                case RegionKind.SizeClass:
                    if (RegionHasDirtyCard(cardBase, i, 1))
                    {
                        ScanRegionCardRuns(cardBase, i, _regionAllocator.RegionBase(i) + Region.Size,
                            Region.ClassSizes[entry->SizeClass], stack, deferredCollectible);
                    }
                    break;

                case RegionKind.SpanStart:
                    if (RegionHasDirtyCard(cardBase, i, entry->SpanCount))
                    {
                        ScanSpanCards(cardBase, i, entry, stack, deferredCollectible);
                    }
                    break;

                // Free regions hold no objects; extensions are covered by their SpanStart
            }
        }
    }

    /// <summary>Fast whole-region test: 128 word reads per region, so clean old regions
    /// cost ~1 µs to skip. Region i's cards are the 1 KB at storage + (i &lt;&lt; 10).</summary>
    private static bool RegionHasDirtyCard(nint cardBase, int regionIndex, int regionCount)
    {
        var cards = (ulong*)(cardBase + ((nint)regionIndex << (Region.Shift - 11)));
        var words = regionCount << (Region.Shift - 11 - 3);

        for (int w = 0; w < words; w++)
        {
            if (cards[w] != 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Scans one dirty bump or size-class region by dirty-card runs, enumerating marked
    /// objects straight off the mark bitmap (the pre-drain's walk, SPEC-M6 §9): set bits
    /// are exactly marked object starts, so the dead neighborhoods the old card-offset
    /// hop walked object-by-object — the dominant scan cost at smear density — are never
    /// touched. Enumeration is clamped to the run: slots under clean cards cannot hold a
    /// reference the scan needs (the barrier dirties the slot's card on every ref store),
    /// which keeps one dirty card on a large array from re-walking every element.
    /// An object spanning two runs is enumerated once per run over disjoint slot ranges.
    /// <paramref name="limit"/> is the region's walkable end (bump cursor / region end).
    /// </summary>
    private void ScanRegionCardRuns(nint cardBase, int index, nint limit, nint maxObjectBytes, MarkStack stack, List<nint>? deferredCollectible)
    {
        var regionBase = _regionAllocator.RegionBase(index);
        var cards = (byte*)(cardBase + ((nint)index << (Region.Shift - 11)));
        var cardCount = (int)Math.Min(Region.Size >> 11, ((limit - regionBase) + 2047) >> 11);

        for (int c = 0; c < cardCount;)
        {
            if (cards[c] == 0)
            {
                c++;
                continue;
            }

            var runEnd = c + 1;

            while (runEnd < cardCount && cards[runEnd] != 0)
            {
                runEnd++;
            }

            var runStart = regionBase + ((nint)c << 11);
            var runLimit = Math.Min(regionBase + ((nint)runEnd << 11), limit);

            ScanMarkedRangeClamped(regionBase, runStart, runLimit, maxObjectBytes, stack);

            c = runEnd;
        }

        FinishRegionCardScan(stack, deferredCollectible);
    }

    /// <summary>
    /// Enumerates the references of every marked object overlapping [start, end), from
    /// the mark bitmap alone, clamped to the range. STW sibling of the pre-drain's
    /// <see cref="ScanMarkedRange"/>; the bitmap read races nothing here, and bits set
    /// mid-scan by parallel card workers marking young objects only add enumerations
    /// whose tracer covers them anyway (waste, not a bug — same argument as the CAS
    /// dedup on the pop side).
    /// </summary>
    private static void ScanMarkedRangeClamped(nint regionBase, nint start, nint end, nint maxObjectBytes, MarkStack stack)
    {
        var bitmap = GCObject.MarkBitmap;
        var heapBase = GCObject.MarkHeapBase;

        // Head: a marked object starting before the run can overlap into it. The
        // backward search is bounded by the largest object the region kind can hold —
        // on smear heaps survivors sit ~100 KB apart, and without the bound the search
        // walks all of it once per run.
        var firstBit = (long)(start - heapBase) >> 3;
        var lowBit = Math.Max((long)(regionBase - heapBase) >> 3, firstBit - (maxObjectBytes >> 3));
        var prev = FindLastSetBitBefore(bitmap, lowBit, firstBit);

        if (prev >= 0)
        {
            var obj = (GCObject*)(heapBase + (nint)(prev << 3));

            if ((nint)obj + (nint)obj->ComputeSize() > start)
            {
                GCObject.EnumerateObjectReferencesInRange(obj, stack, start, end);
            }
        }

        // Body: every set bit in [start, end) is a marked object start
        var w = firstBit >> 6;
        var endBit = (long)(end - heapBase) >> 3;

        for (; w << 6 < endBit; w++)
        {
            var word = bitmap[w];

            while (word != 0)
            {
                var bit = BitOperations.TrailingZeroCount(word);
                word &= word - 1;

                var bitIndex = (w << 6) + bit;

                if (bitIndex >= endBit)
                {
                    return;
                }

                var obj = (GCObject*)(heapBase + (nint)(bitIndex << 3));
                GCObject.EnumerateObjectReferencesInRange(obj, stack, start, end);
            }
        }
    }

    private void ScanSpanCards(nint cardBase, int index, RegionEntry* entry, MarkStack stack, List<nint>? deferredCollectible)
    {
        // One object per span, so no bitmap search: each dirty run enumerates its slice
        // of the object (the clamp is what keeps a huge dirtied ref array from paying
        // full enumeration for a single dirty card)
        var spanBase = _regionAllocator.RegionBase(index);
        var obj = (GCObject*)(spanBase + IntPtr.Size);

        if (obj->IsMarked())
        {
            var cards = (byte*)(cardBase + ((nint)index << (Region.Shift - 11)));
            var cardCount = entry->SpanCount << (Region.Shift - 11);

            for (int c = 0; c < cardCount;)
            {
                if (cards[c] == 0)
                {
                    c++;
                    continue;
                }

                var runEnd = c + 1;

                while (runEnd < cardCount && cards[runEnd] != 0)
                {
                    runEnd++;
                }

                GCObject.EnumerateObjectReferencesInRange(obj, stack,
                    spanBase + ((nint)c << 11), spanBase + ((nint)runEnd << 11));

                c = runEnd;
            }
        }

        FinishRegionCardScan(stack, deferredCollectible);
    }

    /// <summary>Traces everything the region's dirty objects referenced, keeping each
    /// worker's mark stack bounded to one region's worth of pushes.</summary>
    private void FinishRegionCardScan(MarkStack stack, List<nint>? deferredCollectible)
    {
        DrainMarkStack(stack, deferredCollectible);

        if (GcStats.Enabled)
        {
            Interlocked.Increment(ref GcStats.CardRegionsScanned);
        }
    }
}
