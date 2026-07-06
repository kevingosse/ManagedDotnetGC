using System.Numerics;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

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

    /// <summary>Fast whole-region test (vectorized, M7): 8 OR-and-test strides over the
    /// 1 KB slice, so clean old regions cost ~¼ µs to skip. Region i's cards are the
    /// 1 KB at storage + (i &lt;&lt; 10) — slices are 1 KB multiples, so 128-byte strides
    /// divide exactly.</summary>
    private static bool RegionHasDirtyCard(nint cardBase, int regionIndex, int regionCount)
    {
        var cards = (byte*)(cardBase + ((nint)regionIndex << (Region.Shift - 11)));
        var bytes = (nint)regionCount << (Region.Shift - 11);

        if (Avx2.IsSupported)
        {
            for (nint b = 0; b < bytes; b += 128)
            {
                var acc = Avx2.Or(
                    Avx2.Or(Avx.LoadVector256(cards + b), Avx.LoadVector256(cards + b + 32)),
                    Avx2.Or(Avx.LoadVector256(cards + b + 64), Avx.LoadVector256(cards + b + 96)));

                if (!Avx.TestZ(acc, acc))
                {
                    return true;
                }
            }

            return false;
        }

        var words = (ulong*)cards;

        for (nint w = 0; w < bytes >> 3; w++)
        {
            if (words[w] != 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Index of the first dirty card in [start, end), or end.</summary>
    private static int NextDirtyCard(byte* cards, int start, int end)
    {
        var c = start;

        if (Avx2.IsSupported)
        {
            for (; c + 32 <= end; c += 32)
            {
                var zeroes = (uint)Avx2.MoveMask(Avx2.CompareEqual(Avx.LoadVector256(cards + c), Vector256<byte>.Zero));

                if (zeroes != uint.MaxValue)
                {
                    return c + BitOperations.TrailingZeroCount(~zeroes);
                }
            }
        }

        for (; c < end; c++)
        {
            if (cards[c] != 0)
            {
                break;
            }
        }

        return c;
    }

    /// <summary>Index of the first clean card in [start, end), or end.</summary>
    private static int NextCleanCard(byte* cards, int start, int end)
    {
        var c = start;

        if (Avx2.IsSupported)
        {
            for (; c + 32 <= end; c += 32)
            {
                var zeroes = (uint)Avx2.MoveMask(Avx2.CompareEqual(Avx.LoadVector256(cards + c), Vector256<byte>.Zero));

                if (zeroes != 0)
                {
                    return c + BitOperations.TrailingZeroCount(zeroes);
                }
            }
        }

        for (; c < end; c++)
        {
            if (cards[c] == 0)
            {
                break;
            }
        }

        return c;
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

        var bitmap = GCObject.MarkBitmap;
        var heapBase = GCObject.MarkHeapBase;
        var regionFirstBit = (long)(regionBase - heapBase) >> 3;

        // Forward-carry head state (M7): bits below scannedBit have been examined once
        // and carryBit is the highest set bit found there. Each run's head search only
        // covers its own unseen gap, so a region's bitmap words are read at most once
        // per scan — the per-run backward searches this replaces re-walked up to the
        // full 128 KB lookback for every run on smear regions (runs ~2 KB apart,
        // survivors ~100 KB apart).
        var scannedBit = regionFirstBit;
        var carryBit = -1L;

        for (int c = 0; c < cardCount;)
        {
            c = NextDirtyCard(cards, c, cardCount);

            if (c >= cardCount)
            {
                break;
            }

            var runEnd = NextCleanCard(cards, c + 1, cardCount);

            var runStart = regionBase + ((nint)c << 11);
            var runLimit = Math.Min(regionBase + ((nint)runEnd << 11), limit);

            var firstBit = (long)(runStart - heapBase) >> 3;
            var lowBit = Math.Max(regionFirstBit, firstBit - (maxObjectBytes >> 3));

            if (scannedBit < firstBit)
            {
                // Unseen gap: bounded backward search over it, early-exiting at the
                // highest set bit (a word or two in dense old regions). Bits below a
                // mid-word floor were already covered by the carry, so a stale find
                // there loses to the max.
                carryBit = Math.Max(carryBit, FindLastSetBitBefore(bitmap, Math.Max(lowBit, scannedBit), firstBit));
                scannedBit = firstBit;
            }

            // Head: a marked object starting before the run can overlap into it, but
            // never from further back than the largest object the region kind can hold
            if (carryBit >= lowBit)
            {
                var obj = (GCObject*)(heapBase + (nint)(carryBit << 3));

                if ((nint)obj + (nint)obj->ComputeSize() > runStart)
                {
                    GCObject.EnumerateObjectReferencesInRange(obj, stack, runStart, runLimit);
                }
            }

            EnumerateMarkedRange(heapBase, runStart, runLimit, ref carryBit, stack);

            var endBit = (long)(runLimit - heapBase) >> 3;

            if (endBit > scannedBit)
            {
                scannedBit = endBit;
            }

            c = runEnd;
        }

        FinishRegionCardScan(stack, deferredCollectible);
    }

    /// <summary>
    /// Enumerates the references of every marked object starting in [start, end), from
    /// the mark bitmap alone, clamping enumerated slots to the range. STW sibling of the
    /// pre-drain's <see cref="ScanMarkedRange"/>; the bitmap read races nothing here, and
    /// bits set mid-scan by parallel card workers marking young objects only add
    /// enumerations whose tracer covers them anyway (waste, not a bug — same argument as
    /// the CAS dedup on the pop side). The first word is masked to the range so sub-start
    /// bits are never re-enumerated (they belong to the caller's head carry), and
    /// <paramref name="lastSetBit"/> carries the highest bit seen out to the caller.
    /// </summary>
    private static void EnumerateMarkedRange(nint heapBase, nint start, nint end, ref long lastSetBit, MarkStack stack)
    {
        var bitmap = GCObject.MarkBitmap;
        var firstBit = (long)(start - heapBase) >> 3;
        var endBit = (long)(end - heapBase) >> 3;

        var w = firstBit >> 6;
        var mask = ~0ul << (int)(firstBit & 63);

        for (; w << 6 < endBit; w++)
        {
            var word = bitmap[w] & mask;
            mask = ulong.MaxValue;

            while (word != 0)
            {
                var bit = BitOperations.TrailingZeroCount(word);
                word &= word - 1;

                var bitIndex = (w << 6) + bit;

                if (bitIndex >= endBit)
                {
                    return;
                }

                lastSetBit = bitIndex;
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
                c = NextDirtyCard(cards, c, cardCount);

                if (c >= cardCount)
                {
                    break;
                }

                var runEnd = NextCleanCard(cards, c + 1, cardCount);

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
