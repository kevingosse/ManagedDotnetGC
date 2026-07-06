using System.Runtime.InteropServices;
using NUnit.Framework;
using Shouldly;

namespace ManagedDotnetGC.Tests;

/// <summary>
/// SPEC-M2 §12 allocator tests. These drive a real RegionAllocator over a real (small)
/// reservation, replicating the GCHeap window protocol (§3): the first object ref sits at
/// window + 8, the EE bumps until alloc_limit = window + length − 16, and the remainder is
/// plugged with a free object exactly like FixAllocContext does. No EE anywhere.
/// </summary>
[TestFixture]
public unsafe class RegionAllocatorTests
{
    private const uint TestEpoch = 40;
    private const int MinObjectSize = 3 * 8; // sync block slot + MT + one field

    private NativeAllocator _memory = null!;
    private RegionAllocator _allocator = null!;
    private MethodTable* _freeMT;
    private MethodTable* _byteMT;

    // The synthetic alloc context (mirrors gc_alloc_context alloc_ptr/alloc_limit)
    private nint _ctxPtr;
    private nint _ctxLimit;

    [SetUp]
    public void SetUp()
    {
        _memory = new NativeAllocator(256 * 1024 * 1024);
        _allocator = new RegionAllocator(_memory);

        // Array-like method tables (component size 1): any size >= 24 is representable,
        // which is exactly how the real free object works
        _freeMT = MakeMethodTable(hasComponentSize: true);
        _byteMT = MakeMethodTable(hasComponentSize: true);
        _allocator.SetFreeObjectMethodTable(_freeMT);

        GCObject.CurrentEpoch = TestEpoch;
        _ctxPtr = 0;
        _ctxLimit = 0;
    }

    [TearDown]
    public void TearDown()
    {
        _allocator.Dispose();
        _memory.Dispose();
        NativeMemory.Free(_freeMT);
        NativeMemory.Free(_byteMT);
    }

    // ----- §3 window/plug arithmetic: the property test -----

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    public void RandomCarveSequences_KeepEveryRegionWalkable(int seed)
    {
        var random = new Random(seed * 1000 + 123);
        var live = new Dictionary<nint, nint>(); // ref -> size

        // Three alloc/sweep rounds: round 2+ carves from swept holes (§4.4)
        for (int round = 0; round < 3; round++)
        {
            for (int i = 0; i < 1500; i++)
            {
                var size = Align(random.Next(MinObjectSize, (int)Region.BumpMaxSize + 1));
                var obj = AllocObject(size);

                obj.ShouldNotBe(0);
                live.Add(obj, size);
            }

            FixContext();
            AssertAllBumpRegionsWalkable(live);

            // Random survivors, stamped with a fresh epoch like a real mark phase
            GCObject.CurrentEpoch = TestEpoch + (uint)round + 1;
            var survivors = new Dictionary<nint, nint>();

            foreach (var (obj, size) in live)
            {
                if (random.Next(100) < 40)
                {
                    MarkLive(obj);
                    survivors.Add(obj, size);
                }
            }

            _allocator.Sweep();
            live = survivors;

            AssertAllBumpRegionsWalkable(live);

            // Survivor integrity: sweep must not have touched live payloads
            foreach (var (obj, size) in live)
            {
                ((GCObject*)obj)->ComputeSize().ShouldBe((uint)size);
                AssertPayloadPattern(obj, size);
            }
        }
    }

    // ----- §4.4/§7.1 hole coalescing, linking, and carving -----

    [Test]
    public void Sweep_CoalescesAdjacentDeadObjectsIntoOnePlug()
    {
        // Twenty 30000-byte objects span five windows; killing 1..18 coalesces a dead run
        // across window boundaries (the inter-window remainder plugs are never marked, so
        // they merge for free) into one ~550 KB hole
        var size = (nint)30000;
        var objects = new List<nint>();

        for (int i = 0; i < 20; i++)
        {
            objects.Add(AllocObject(size));
        }

        FixContext();

        _allocator.TryGetIndex(objects[0], out var index).ShouldBeTrue();
        var entry = _allocator.GetEntry(index);

        MarkLive(objects[0]);
        MarkLive(objects[19]);
        _allocator.Sweep();

        // One plug at objects[1] covering [objects[1] - 8, objects[19] - 8)
        var plug = (GCObject*)objects[1];
        ((nint)plug->RawMethodTable).ShouldBe((nint)_freeMT);
        plug->ComputeSize().ShouldBe((uint)(objects[19] - objects[1]));

        // It is the only linked hole: the dead tail after objects[19] is far below one
        // window, so it was plugged but not linked (M4 floor)
        entry->FirstHole.ShouldBe(objects[1]);
        entry->HoleBytes.ShouldBe((int)(objects[19] - objects[1]));
        (*(nint*)(objects[1] + 2 * IntPtr.Size)).ShouldBe(0);

        // Live objects and the region survive
        entry->Kind.ShouldBe(RegionKind.Bump);
        AssertPayloadPattern(objects[0], size);
        AssertPayloadPattern(objects[19], size);
        WalkBumpRegion(index);
    }

    [Test]
    public void TryGetWindow_CarvesFromHolesFirstAndUnlinksExhaustedRegions()
    {
        var size = (nint)30000;
        var objects = new List<nint>();

        for (int i = 0; i < 20; i++)
        {
            objects.Add(AllocObject(size));
        }

        FixContext();

        _allocator.TryGetIndex(objects[0], out var index).ShouldBeTrue();
        var entry = _allocator.GetEntry(index);
        var cursor = entry->Cursor;

        MarkLive(objects[0]);
        MarkLive(objects[19]);
        _allocator.Sweep();

        // Hole-first policy: windows carve through the big hole front to back, each handing
        // out zeroed memory (zero-at-carve), until the remainder drops below one window and
        // the region unlinks
        var extent = objects[19] - objects[1];
        var next = objects[1] - IntPtr.Size;
        nint consumed = 0;

        while (entry->FirstHole != 0)
        {
            TryGetZeroedWindow(1000, out var window, out var length).ShouldBeTrue();
            window.ShouldBe(next);
            AssertZero(window, length);

            next += length;
            consumed += length;
            (consumed <= extent).ShouldBeTrue();
        }

        // The unlinked leftover is smaller than a window — floating until a full sweep
        (extent - consumed < Region.MinLinkedHole).ShouldBeTrue();

        // Exhausted: the next window comes from the cursor
        TryGetZeroedWindow(1000, out var fromCursor, out _).ShouldBeTrue();
        fromCursor.ShouldBe(cursor);
    }

    [Test]
    public void Sweep_DoesNotLinkHolesSmallerThanMinLinkedHole()
    {
        // Alternate live/dead 256-byte objects: dead extents are 256 bytes each, far below
        // the floor — they must be plugged (walkability) but not linked (carving). Only the
        // ~119 KB dead window tail clears the floor.
        var size = (nint)256;
        var objects = new List<nint>();

        for (int i = 0; i < 32; i++)
        {
            objects.Add(AllocObject(size));
        }

        FixContext();

        _allocator.TryGetIndex(objects[0], out var index).ShouldBeTrue();
        var entry = _allocator.GetEntry(index);

        for (int i = 0; i < objects.Count; i += 2)
        {
            MarkLive(objects[i]);
        }

        _allocator.Sweep();

        // The tail run starts at the last dead object (it coalesced with the context plug)
        entry->FirstHole.ShouldBe(objects[31]);
        (*(nint*)(objects[31] + 2 * IntPtr.Size)).ShouldBe(0); // no second hole in the list

        // But every dead extent was plugged: the region walks end-to-end
        WalkBumpRegion(index);
    }

    // ----- §4.2/§7.2 size-class bitmap alloc/free/full-unlink -----

    [Test]
    public void SizeClassRegion_FillsUnlinksSweepsAndRelinks()
    {
        const int sizeClass = 0; // 40 KB blocks, 51 per region
        var blockCount = RegionAllocator.BlockCount(sizeClass);
        var objectSize = (nint)30000; // aligned, fits class 0 with room to spare

        var blocks = new List<nint>();

        for (int i = 0; i < blockCount; i++)
        {
            _allocator.TryAllocBlock(sizeClass, out var block).ShouldBeTrue();
            WriteObject(block + IntPtr.Size, objectSize);
            blocks.Add(block);
        }

        // All in the same region, at bitmap-computed offsets
        _allocator.TryGetIndex(blocks[0], out var index).ShouldBeTrue();

        for (int i = 0; i < blockCount; i++)
        {
            blocks[i].ShouldBe(_allocator.RegionBase(index) + i * (nint)Region.ClassSizes[sizeClass]);
        }

        var (bitmap, _) = _allocator.GetSizeClassInfo(index);
        bitmap.ShouldBe(RegionAllocator.FullMask(sizeClass));

        // The region is full and unlinked: the next block carves a fresh region
        _allocator.TryAllocBlock(sizeClass, out var overflow).ShouldBeTrue();
        _allocator.TryGetIndex(overflow, out var overflowIndex).ShouldBeTrue();
        overflowIndex.ShouldNotBe(index);
        WriteObject(overflow + IntPtr.Size, objectSize); // the EE always writes the MT before a GC can run

        // Keep blocks 3 and 17, kill the rest
        MarkLive(blocks[3] + IntPtr.Size);
        MarkLive(blocks[17] + IntPtr.Size);
        _allocator.Sweep();

        (bitmap, _) = _allocator.GetSizeClassInfo(index);
        bitmap.ShouldBe((1ul << 3) | (1ul << 17));

        // Dead blocks were re-zeroed over their dirtied extent (Invariant P)
        AssertZero(blocks[0], IntPtr.Size + objectSize);

        // Survivors intact
        ((GCObject*)(blocks[3] + IntPtr.Size))->ComputeSize().ShouldBe((uint)objectSize);
        AssertPayloadPattern(blocks[3] + IntPtr.Size, objectSize);

        // The overflow region was all-dead: recycled wholesale
        _allocator.GetEntry(overflowIndex)->Kind.ShouldBe(RegionKind.Free);

        // The swept region was relinked: the next alloc reuses its lowest free block
        _allocator.TryAllocBlock(sizeClass, out var reused).ShouldBeTrue();
        reused.ShouldBe(blocks[0]);
    }

    // ----- §7.1 wholesale recycle + zero-at-carve -----

    [Test]
    public void Sweep_RecyclesAllDeadBumpRegionWholesale_AndCarveZeroesReuse()
    {
        var objects = new List<nint>();

        for (int i = 0; i < 100; i++)
        {
            objects.Add(AllocObject(1024));
        }

        FixContext();

        _allocator.TryGetIndex(objects[0], out var index).ShouldBeTrue();

        // Nothing marked, LiveBytes stays 0: the wholesale-recycle path
        _allocator.Sweep();

        var entry = _allocator.GetEntry(index);
        entry->Kind.ShouldBe(RegionKind.Free);

        // The pool is LIFO: the next carve hands the same region back, flagged dirty
        // (its dead contents stayed in place), and the carved window is zeroed exactly
        // where it is handed out (M4 zero-at-carve)
        TryGetZeroedWindow(1024, out var window, out var length).ShouldBeTrue();
        _allocator.TryGetIndex(window, out var reusedIndex).ShouldBeTrue();
        reusedIndex.ShouldBe(index);
        entry->BumpIsDirty.ShouldBe((byte)1);
        AssertZero(window, length);
    }

    // ----- §4.3/§7.3 spans -----

    [Test]
    public void Span_AllocatesContiguousRegionsAndRecycles()
    {
        _allocator.TryAllocSpan(3, out var spanBase).ShouldBeTrue();
        _allocator.TryGetIndex(spanBase, out var index).ShouldBeTrue();

        var start = _allocator.GetEntry(index);
        start->Kind.ShouldBe(RegionKind.SpanStart);
        start->SpanCount.ShouldBe(3);

        for (int i = 1; i < 3; i++)
        {
            var extension = _allocator.GetEntry(index + i);
            extension->Kind.ShouldBe(RegionKind.SpanExtension);
            extension->SpanStartIndex.ShouldBe(index);
        }

        // A span holds exactly one object, at spanBase + 8
        var objectSize = (nint)(3 * Region.Size - 4 * IntPtr.Size);
        WriteObject(spanBase + IntPtr.Size, objectSize);

        // Survives one collection when marked...
        MarkLive(spanBase + IntPtr.Size);
        _allocator.Sweep();
        start->Kind.ShouldBe(RegionKind.SpanStart);
        ((GCObject*)(spanBase + IntPtr.Size))->ComputeSize().ShouldBe((uint)objectSize);

        // ...dies on the next one: all three regions return to the pool with their dead
        // contents (M4 zero-at-carve), and a new span cleans them at allocation
        GCObject.CurrentEpoch++;
        _allocator.Sweep();

        for (int i = 0; i < 3; i++)
        {
            _allocator.GetEntry(index + i)->Kind.ShouldBe(RegionKind.Free);
        }

        _allocator.TryAllocSpan(3, out var reused).ShouldBeTrue();
        reused.ShouldBe(spanBase);
        AssertZero(reused, 3 * Region.Size);
    }

    [Test]
    public void Span_BigSpansAreDecommittedOnRecycleAndRecommitOnReuse()
    {
        _allocator.TryAllocSpan(4, out var spanBase).ShouldBeTrue();
        _allocator.TryGetIndex(spanBase, out var index).ShouldBeTrue();
        WriteObject(spanBase + IntPtr.Size, 1024);

        var committedBefore = _allocator.CommittedRegionBytes;

        _allocator.Sweep(); // nothing live: the >= 4-region path decommits instead of zeroing

        _allocator.CommittedRegionBytes.ShouldBe(committedBefore - 4 * Region.Size);

        for (int i = 0; i < 4; i++)
        {
            var entry = _allocator.GetEntry(index + i);
            entry->Kind.ShouldBe(RegionKind.Free);
            entry->IsCommitted.ShouldBe((byte)0);
        }

        // Reusing the decommitted run recommits it; the OS hands back zero pages
        _allocator.TryAllocSpan(2, out var reused).ShouldBeTrue();
        reused.ShouldBe(spanBase); // first fit over the table finds the old run
        AssertZero(reused, 2 * Region.Size);
    }

    // ----- M4 sticky-generation young sweep -----

    [Test]
    public void YoungSweep_RecyclesFreshRegionsAndSweepsReopenedOnes()
    {
        // Old region: five objects, all live, sealed by a full sweep
        var size = (nint)2048;
        var oldObjects = new List<nint>();

        for (int i = 0; i < 5; i++)
        {
            oldObjects.Add(AllocObject(size));
        }

        FixContext();
        _allocator.TryGetIndex(oldObjects[0], out var oldIndex).ShouldBeTrue();

        foreach (var obj in oldObjects)
        {
            MarkLive(obj);
        }

        _allocator.Sweep();

        var oldEntry = _allocator.GetEntry(oldIndex);
        oldEntry->Age.ShouldBe(RegionAge.Old);

        // Fill the old region's holes and bump tail until a fresh young region carves;
        // allocation into the old region re-opens it (mixed ages)
        var youngIndex = -1;
        var youngInReopened = new List<nint>();

        while (youngIndex < 0)
        {
            var obj = AllocObject(size);
            obj.ShouldNotBe(0);
            _allocator.TryGetIndex(obj, out var index).ShouldBeTrue();

            if (index != oldIndex)
            {
                youngIndex = index;
            }
            else
            {
                youngInReopened.Add(obj);
            }
        }

        FixContext();
        oldEntry->Age.ShouldBe(RegionAge.Reopened);
        _allocator.GetEntry(youngIndex)->Age.ShouldBe(RegionAge.Fresh);

        // Nothing young is marked: same epoch, no new stamps (sticky marks stay valid)
        _allocator.Sweep(youngOnly: true);

        // The fresh region died wholesale; the reopened one was swept in place
        _allocator.GetEntry(youngIndex)->Kind.ShouldBe(RegionKind.Free);
        oldEntry->Kind.ShouldBe(RegionKind.Bump);
        oldEntry->Age.ShouldBe(RegionAge.Old);

        // Sticky survivors are intact without any re-marking...
        foreach (var obj in oldObjects)
        {
            ((GCObject*)obj)->ComputeSize().ShouldBe((uint)size);
            AssertPayloadPattern(obj, size);
        }

        // ...and the dead young objects among them were absorbed into plugs by this very
        // sweep — no floating garbage waiting for a full collection. The walk sees the
        // sticky survivors and hops straight over the coalesced dead run.
        var walked = WalkBumpRegion(oldIndex);
        walked.ShouldNotContain(youngInReopened[0]);

        foreach (var obj in oldObjects)
        {
            walked.ShouldContain(obj);
        }
    }

    [Test]
    public void YoungSweep_NeverWholesaleRecyclesReopenedRegions()
    {
        // Five sticky-marked survivors sealed into an old region
        var size = (nint)2048;
        var oldObjects = new List<nint>();

        for (int i = 0; i < 5; i++)
        {
            oldObjects.Add(AllocObject(size));
        }

        FixContext();
        _allocator.TryGetIndex(oldObjects[0], out var oldIndex).ShouldBeTrue();

        foreach (var obj in oldObjects)
        {
            MarkLive(obj);
        }

        _allocator.Sweep();

        // One young object re-opens the region, then dies unmarked: LiveBytes stays 0 for
        // this cycle even though five old objects are alive — the wholesale-recycle
        // shortcut must not fire or it would zero them all
        var young = AllocObject(size);
        FixContext();

        _allocator.TryGetIndex(young, out var youngIndex).ShouldBeTrue();
        youngIndex.ShouldBe(oldIndex);
        _allocator.GetEntry(oldIndex)->Age.ShouldBe(RegionAge.Reopened);

        _allocator.Sweep(youngOnly: true);

        var entry = _allocator.GetEntry(oldIndex);
        entry->Kind.ShouldBe(RegionKind.Bump);
        entry->Age.ShouldBe(RegionAge.Old);

        foreach (var obj in oldObjects)
        {
            AssertPayloadPattern(obj, size);
        }

        // The dead young object was absorbed into a plug (the walk hops over it)
        WalkBumpRegion(oldIndex).ShouldNotContain(young);
    }

    [Test]
    public void YoungSweep_KeepsOldRegionHolesCarveable()
    {
        // Old region with one window-sized linked hole built by a full sweep
        var size = (nint)30000;
        var objects = new List<nint>();

        for (int i = 0; i < 20; i++)
        {
            objects.Add(AllocObject(size));
        }

        FixContext();

        MarkLive(objects[0]);
        MarkLive(objects[19]);
        _allocator.Sweep();

        // A young sweep rebuilds the recycled list from scratch; old regions must be
        // relinked by the metadata-only path or their holes would be stranded until the
        // next full collection
        _allocator.Sweep(youngOnly: true);

        TryGetZeroedWindow(1000, out var window, out _).ShouldBeTrue();
        window.ShouldBe(objects[1] - IntPtr.Size); // carved from the hole, not the cursor
    }

    [Test]
    public void YoungSweep_RelinkOldSizeClassRegionsWithFreeBlocks()
    {
        const int sizeClass = 0;
        var objectSize = (nint)30000;
        var blocks = new nint[3];

        for (int i = 0; i < 3; i++)
        {
            _allocator.TryAllocBlock(sizeClass, out blocks[i]).ShouldBeTrue();
            WriteObject(blocks[i] + IntPtr.Size, objectSize);
        }

        _allocator.TryGetIndex(blocks[0], out var index).ShouldBeTrue();
        _allocator.GetEntry(index)->Age.ShouldBe(RegionAge.Fresh);

        MarkLive(blocks[0] + IntPtr.Size);
        MarkLive(blocks[2] + IntPtr.Size);

        // Young sweep: the region is young, so it gets real object work and is promoted
        _allocator.Sweep(youngOnly: true);

        var entry = _allocator.GetEntry(index);
        entry->Age.ShouldBe(RegionAge.Old);
        entry->AllocatedBlocks.ShouldBe((1ul << 0) | (1ul << 2));

        // Second young sweep: the region is old now — the metadata relink must keep its
        // free blocks allocatable, and allocating from it re-opens it
        _allocator.Sweep(youngOnly: true);

        _allocator.TryAllocBlock(sizeClass, out var reused).ShouldBeTrue();
        reused.ShouldBe(blocks[1]);
        entry->Age.ShouldBe(RegionAge.Reopened);
    }

    [Test]
    public void YoungSweep_RecyclesDeadYoungSpansAndSkipsOldOnes()
    {
        var spanObjectSize = (nint)(2 * Region.Size - 4 * IntPtr.Size);

        _allocator.TryAllocSpan(2, out var deadSpan).ShouldBeTrue();
        WriteObject(deadSpan + IntPtr.Size, spanObjectSize);

        _allocator.TryAllocSpan(2, out var liveSpan).ShouldBeTrue();
        WriteObject(liveSpan + IntPtr.Size, spanObjectSize);
        MarkLive(liveSpan + IntPtr.Size);

        _allocator.TryGetIndex(deadSpan, out var deadIndex).ShouldBeTrue();
        _allocator.TryGetIndex(liveSpan, out var liveIndex).ShouldBeTrue();

        _allocator.Sweep(youngOnly: true);

        _allocator.GetEntry(deadIndex)->Kind.ShouldBe(RegionKind.Free);

        var liveEntry = _allocator.GetEntry(liveIndex);
        liveEntry->Kind.ShouldBe(RegionKind.SpanStart);
        liveEntry->Age.ShouldBe(RegionAge.Old);

        // The next young sweep skips the promoted span even though nothing re-marked it
        _allocator.Sweep(youngOnly: true);

        liveEntry->Kind.ShouldBe(RegionKind.SpanStart);
        ((GCObject*)(liveSpan + IntPtr.Size))->ComputeSize().ShouldBe((uint)spanObjectSize);
    }

    [Test]
    public void FullSweep_AfterYoungCollections_ReclaimsFloatingGarbage()
    {
        // Old region A with all five objects live
        var size = (nint)2048;
        var oldObjects = new List<nint>();

        for (int i = 0; i < 5; i++)
        {
            oldObjects.Add(AllocObject(size));
        }

        FixContext();
        _allocator.TryGetIndex(oldObjects[0], out var oldIndex).ShouldBeTrue();

        foreach (var obj in oldObjects)
        {
            MarkLive(obj);
        }

        _allocator.Sweep();

        // Two young objects carved into A's dead tail (re-opening it): y1 survives a young
        // collection and is promoted in place, y2 dies and is plugged by that same sweep
        var y1 = AllocObject(size);
        var y2 = AllocObject(size);
        FixContext();

        _allocator.TryGetIndex(y1, out var yIndex).ShouldBeTrue();
        yIndex.ShouldBe(oldIndex);

        MarkLive(y1);
        _allocator.Sweep(youngOnly: true);

        ((nint)((GCObject*)y2)->RawMethodTable).ShouldBe((nint)_freeMT);
        AssertPayloadPattern(y1, size);

        // o2..o5 now die, but a young collection cannot see old deaths: they float until
        // the full protocol runs (as GCHeap drives it — clear stale accumulators, advance
        // the epoch, re-mark the true live set, full sweep)
        _allocator.Sweep(youngOnly: true);
        ((nint)((GCObject*)oldObjects[1])->RawMethodTable).ShouldBe((nint)_byteMT); // floats

        _allocator.ResetLiveBytes();
        GCObject.CurrentEpoch++;
        MarkLive(oldObjects[0]);
        MarkLive(y1);

        _allocator.Sweep();

        // Survivors intact, everything else (the floating old o2..o5) plugged
        AssertPayloadPattern(oldObjects[0], size);
        AssertPayloadPattern(y1, size);
        ((nint)((GCObject*)oldObjects[1])->RawMethodTable).ShouldBe((nint)_freeMT);
        WalkBumpRegion(oldIndex);
    }

    // ----- harness -----

    /// <summary>Mirrors the EE bump + GCHeap.AllocFromWindow protocol (§3, §4.1).</summary>
    private nint AllocObject(nint size)
    {
        size.ShouldBe(Align(size), "test bug: sizes must be 8-byte aligned");

        if (_ctxPtr != 0 && _ctxPtr + size <= _ctxLimit)
        {
            var bumped = _ctxPtr;
            WriteObject(bumped, size);
            _ctxPtr = Align(bumped + size);
            return bumped;
        }

        FixContext();

        if (!TryGetZeroedWindow(size, out var window, out var length))
        {
            return 0;
        }

        // §3 window invariants: room for the object, its pre-header, and a closing plug,
        // inside the region and clear of the guard tail
        (length >= Align(size) + 3 * IntPtr.Size).ShouldBeTrue();
        _allocator.TryGetIndex(window, out var index).ShouldBeTrue();
        (window >= _allocator.RegionBase(index)).ShouldBeTrue();
        (window + length <= _allocator.RegionBase(index) + Region.Size - Region.GuardBytes).ShouldBeTrue();

        var result = window + IntPtr.Size;
        WriteObject(result, size);
        _ctxPtr = Align(result + size);
        _ctxLimit = window + length - 2 * IntPtr.Size;
        return result;
    }

    /// <summary>Requests a window and zeroes it when flagged, exactly like
    /// GCHeap.AllocFromWindow does after releasing its allocation lock.</summary>
    private bool TryGetZeroedWindow(nint size, out nint window, out nint length)
    {
        if (!_allocator.TryGetWindow(size, out window, out length, out var needsZero))
        {
            return false;
        }

        if (needsZero)
        {
            RegionAllocator.ZeroWindow(window, length);
        }

        return true;
    }

    /// <summary>Plugs the context remainder exactly like GCHeap.FixAllocContext.</summary>
    private void FixContext()
    {
        if (_ctxPtr == 0)
        {
            return;
        }

        var plug = (GCObject*)_ctxPtr;
        plug->RawMethodTable = _freeMT;
        plug->Length = (uint)(_ctxLimit - _ctxPtr);

        _ctxPtr = 0;
        _ctxLimit = 0;
    }

    private void WriteObject(nint address, nint size)
    {
        var obj = (GCObject*)address;
        obj->RawMethodTable = _byteMT;
        obj->Length = (uint)(size - MinObjectSize);

        // Deterministic payload pattern so sweeps that clobber live data get caught
        var payload = (byte*)(address + 2 * IntPtr.Size);
        var count = Math.Min(64, size - MinObjectSize);

        for (int i = 0; i < count; i++)
        {
            payload[i] = (byte)((address >> 3) + i);
        }
    }

    private void AssertPayloadPattern(nint address, nint size)
    {
        var payload = (byte*)(address + 2 * IntPtr.Size);
        var count = Math.Min(64, size - MinObjectSize);

        for (int i = 0; i < count; i++)
        {
            payload[i].ShouldBe((byte)((address >> 3) + i));
        }
    }

    private void MarkLive(nint address)
    {
        var obj = (GCObject*)address;
        obj->Epoch = GCObject.CurrentEpoch;

        _allocator.TryGetIndex(address, out var index).ShouldBeTrue();
        _allocator.GetEntry(index)->LiveBytes += (int)Align((nint)obj->ComputeSize());
    }

    private void AssertAllBumpRegionsWalkable(Dictionary<nint, nint> expectedLive)
    {
        var seen = new HashSet<nint>();

        for (int i = 0; i < _allocator.CarvedCount; i++)
        {
            if (_allocator.GetEntry(i)->Kind != RegionKind.Bump)
            {
                continue;
            }

            foreach (var obj in WalkBumpRegion(i))
            {
                seen.Add(obj);
            }
        }

        // Every live object must be reachable by the walk (sweep depends on it)
        foreach (var obj in expectedLive.Keys)
        {
            seen.Contains(obj).ShouldBeTrue($"live object {obj:x} not found by the region walk");
        }
    }

    /// <summary>
    /// Walks a bump region object-by-object from base + 8 (§3) and asserts it is seamless:
    /// every hop lands on a valid method table and the walk ends exactly at Cursor + 8
    /// (the closing plug of the last window always overhangs the cursor by one pre-header).
    /// </summary>
    private List<nint> WalkBumpRegion(int index)
    {
        var entry = _allocator.GetEntry(index);
        var ptr = _allocator.RegionBase(index) + IntPtr.Size;
        var end = entry->Cursor;
        var objects = new List<nint>();
        var guard = 200_000;

        while (ptr < end)
        {
            var obj = (GCObject*)ptr;
            var mt = (nint)obj->RawMethodTable;

            (mt == (nint)_byteMT || mt == (nint)_freeMT).ShouldBeTrue(
                $"walk of region {index} hit a corrupt method table {mt:x} at {ptr:x}");

            var size = (nint)obj->ComputeSize();
            (size >= MinObjectSize).ShouldBeTrue();

            if (mt == (nint)_byteMT)
            {
                objects.Add(ptr);
            }

            ptr = Align(ptr + size);
            (--guard > 0).ShouldBeTrue("runaway walk");
        }

        ptr.ShouldBe(end + IntPtr.Size, $"region {index} walk must end exactly one pre-header past the cursor");
        return objects;
    }

    private static void AssertZero(nint start, nint length)
    {
        new ReadOnlySpan<byte>((void*)start, (int)length).IndexOfAnyExcept((byte)0).ShouldBe(-1);
    }

    private static MethodTable* MakeMethodTable(bool hasComponentSize)
    {
        var mt = (MethodTable*)NativeMemory.AllocZeroed(64);

        if (hasComponentSize)
        {
            // Array-like: HasComponentSize flag + component size 1, base size 24 —
            // ComputeSize = 24 + Length, the same shape as the real free object
            *(uint*)mt = 0x80000000u | 1;
            mt->BaseSize = (uint)MinObjectSize;
        }

        return mt;
    }

    private static nint Align(nint address) => (address + (IntPtr.Size - 1)) & ~(IntPtr.Size - 1);
}
