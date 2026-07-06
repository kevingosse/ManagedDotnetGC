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
    /// </summary>
    private void ScanCards()
    {
        var cardBase = _regionAllocator.CardTableStorage;

        if (cardBase == 0)
        {
            return;
        }

        var count = _regionAllocator.CarvedCount;

        for (int i = 0; i < count; i++)
        {
            var entry = _regionAllocator.GetEntry(i);

            if (entry->Age == RegionAge.Fresh)
            {
                continue;
            }

            switch (entry->Kind)
            {
                case RegionKind.Bump:
                    if (RegionHasDirtyCard(cardBase, i, 1))
                    {
                        ScanBumpRegionCards(cardBase, i, entry);
                    }
                    break;

                case RegionKind.SizeClass:
                    if (RegionHasDirtyCard(cardBase, i, 1))
                    {
                        ScanSizeClassRegionCards(cardBase, i, entry);
                    }
                    break;

                case RegionKind.SpanStart:
                    if (RegionHasDirtyCard(cardBase, i, entry->SpanCount))
                    {
                        ScanSpanCards(cardBase, i);
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

    private bool RangeHasDirtyCard(nint cardBase, nint start, nint end)
    {
        var heapBase = _regionAllocator.HeapBase;
        var first = (start - heapBase) >> 11;
        var last = (end - 1 - heapBase) >> 11;

        for (var card = first; card <= last; card++)
        {
            if (*(byte*)(cardBase + card) != 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Scans one dirty old bump region by dirty-card runs: the card-offset table maps each
    /// run's first card to a nearby object start, so only the dirty neighborhoods get
    /// walked — a sparsely mutated region costs a few object hops instead of a 2 MB walk.
    /// An object spanning two runs can be enumerated twice; the mark-stack pop side
    /// deduplicates via IsMarked, so that is waste, not a bug.
    /// </summary>
    private void ScanBumpRegionCards(nint cardBase, int index, RegionEntry* entry)
    {
        var regionBase = _regionAllocator.RegionBase(index);
        var end = entry->Cursor;
        var cards = (byte*)(cardBase + ((nint)index << (Region.Shift - 11)));
        var cardCount = (int)Math.Min(Region.Size >> 11, ((end - regionBase) + 2047) >> 11);

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
            var runLimit = Math.Min(regionBase + ((nint)runEnd << 11), end);

            var ptr = _regionAllocator.FindBumpObjectAtOrBefore(index, runStart);

            while (ptr < runLimit)
            {
                var obj = (GCObject*)ptr;
                var size = (nint)obj->ComputeSize();

                // Free plugs and dead-or-untraced-young objects are never card sources;
                // marked objects only matter where a card under them is dirty
                if (ptr + size > runStart
                    && obj->IsMarked() && RangeHasDirtyCard(cardBase, ptr, ptr + size))
                {
                    GCObject.EnumerateObjectReferences(obj, _markStack);
                }

                ptr = Align(ptr + size);
            }

            c = runEnd;
        }

        FinishRegionCardScan();
    }

    private void ScanSizeClassRegionCards(nint cardBase, int index, RegionEntry* entry)
    {
        var regionBase = _regionAllocator.RegionBase(index);
        var classSize = Region.ClassSizes[entry->SizeClass];
        var bitmap = entry->AllocatedBlocks;

        while (bitmap != 0)
        {
            var bit = BitOperations.TrailingZeroCount(bitmap);
            bitmap &= bitmap - 1;

            var obj = (GCObject*)(regionBase + (nint)bit * classSize + IntPtr.Size);
            var size = (nint)obj->ComputeSize();

            if (obj->IsMarked() && RangeHasDirtyCard(cardBase, (nint)obj, (nint)obj + size))
            {
                GCObject.EnumerateObjectReferences(obj, _markStack);
            }
        }

        FinishRegionCardScan();
    }

    private void ScanSpanCards(nint cardBase, int index)
    {
        // One object per span; a single dirty card re-enumerates the whole object (a large
        // ref array pays full enumeration — card-sliced scanning is a known follow-up)
        var obj = (GCObject*)(_regionAllocator.RegionBase(index) + IntPtr.Size);
        var size = (nint)obj->ComputeSize();

        if (obj->IsMarked() && RangeHasDirtyCard(cardBase, (nint)obj, (nint)obj + size))
        {
            GCObject.EnumerateObjectReferences(obj, _markStack);
        }

        FinishRegionCardScan();
    }

    /// <summary>Traces everything the region's dirty objects referenced, keeping the mark
    /// stack bounded to one region's worth of pushes.</summary>
    private void FinishRegionCardScan()
    {
        DrainMarkStack();

        if (GcStats.Enabled)
        {
            GcStats.CardRegionsScanned++;
        }
    }
}
