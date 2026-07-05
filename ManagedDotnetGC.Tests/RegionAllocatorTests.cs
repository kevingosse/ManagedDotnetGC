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
        // One window: o1 | o2 o3 o4 (dead run) | o5 | remainder plug
        var size = (nint)2048;
        var o1 = AllocObject(size);
        var o2 = AllocObject(size);
        var o3 = AllocObject(size);
        var o4 = AllocObject(size);
        var o5 = AllocObject(size);
        var tailPlug = _ctxPtr;
        FixContext();

        _allocator.TryGetIndex(o1, out var index).ShouldBeTrue();
        var entry = _allocator.GetEntry(index);
        var cursor = entry->Cursor;

        MarkLive(o1);
        MarkLive(o5);
        _allocator.Sweep();

        // The dead run coalesced into a single plug at o2 covering [o2 - 8, o5 - 8)
        var plug = (GCObject*)o2;
        ((nint)plug->RawMethodTable).ShouldBe((nint)_freeMT);
        plug->ComputeSize().ShouldBe((uint)(3 * size));

        // The dead tail [tailPlug - 8, cursor) also became a hole (the old remainder plug
        // is never marked, so it coalesces for free); it was closed last, so it heads the
        // hole list, and its link word (ref + 16) points to the o2 hole
        var tailExtent = cursor - (tailPlug - IntPtr.Size);
        entry->FirstHole.ShouldBe(tailPlug);
        entry->HoleBytes.ShouldBe((int)(3 * size + tailExtent));
        (*(nint*)(tailPlug + 2 * IntPtr.Size)).ShouldBe(o2);

        // Live objects and the region survive
        entry->Kind.ShouldBe(RegionKind.Bump);
        ((GCObject*)o1)->ComputeSize().ShouldBe((uint)size);
        ((GCObject*)o5)->ComputeSize().ShouldBe((uint)size);
    }

    [Test]
    public void TryGetWindow_CarvesFromHolesFirstAndUnlinksExhaustedRegions()
    {
        var size = (nint)2048;
        var o1 = AllocObject(size);
        var o2 = AllocObject(size);
        var o3 = AllocObject(size);
        var o4 = AllocObject(size);
        var o5 = AllocObject(size);
        var tailPlug = _ctxPtr;
        FixContext();

        _allocator.TryGetIndex(o1, out var index).ShouldBeTrue();
        var entry = _allocator.GetEntry(index);
        var cursor = entry->Cursor;

        MarkLive(o1);
        MarkLive(o5);
        _allocator.Sweep();

        // First carve: hole-first policy takes the head hole (the big dead tail)
        _allocator.TryGetWindow(1000, out var window1, out var length1).ShouldBeTrue();
        window1.ShouldBe(tailPlug - IntPtr.Size);
        length1.ShouldBe(cursor - window1); // whole extent: it is under the 128 KB window size

        // Second carve: the only hole left is the coalesced o2..o4 run
        _allocator.TryGetWindow(1000, out var window2, out var length2).ShouldBeTrue();
        window2.ShouldBe(o2 - IntPtr.Size);
        length2.ShouldBe(3 * size);

        // Carved windows hand out zeroed memory (Invariant P for holes)
        AssertZero(window2, length2);

        // No holes left: the region is unlinked, the next window comes from the cursor
        entry->FirstHole.ShouldBe(0);
        _allocator.TryGetWindow(1000, out var window3, out _).ShouldBeTrue();
        window3.ShouldBe(cursor);
    }

    [Test]
    public void Sweep_DoesNotLinkHolesSmallerThanMinLinkedHole()
    {
        // Alternate live/dead 256-byte objects: dead extents are 256 bytes each, far below
        // MinLinkedHole — they must be plugged (walkability) but not linked (carving)
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

        // The only linked hole is the big dead tail of the window; every small extent
        // between live objects is a plug, not a carve candidate
        var tail = entry->FirstHole;
        tail.ShouldNotBe(0);
        (*(nint*)(tail + 2 * IntPtr.Size)).ShouldBe(0); // no second hole in the list

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

    // ----- §7.1 wholesale recycle + Invariant P -----

    [Test]
    public void Sweep_RecyclesAllDeadBumpRegionWholesale_AndMemoryIsZero()
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

        // Invariant P: a pooled region is fully zero
        AssertZero(_allocator.RegionBase(index), Region.Size);

        // The pool is LIFO: the next carve hands the same region back, still zero
        _allocator.TryGetWindow(1024, out var window, out var length).ShouldBeTrue();
        _allocator.TryGetIndex(window, out var reusedIndex).ShouldBeTrue();
        reusedIndex.ShouldBe(index);
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

        // ...dies on the next one: all three regions return to the pool, zeroed
        GCObject.CurrentEpoch++;
        _allocator.Sweep();

        for (int i = 0; i < 3; i++)
        {
            _allocator.GetEntry(index + i)->Kind.ShouldBe(RegionKind.Free);
        }

        AssertZero(spanBase, 3 * Region.Size);
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

        if (!_allocator.TryGetWindow(size, out var window, out var length))
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
