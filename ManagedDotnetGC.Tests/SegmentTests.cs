using NUnit.Framework;
using Shouldly;

namespace ManagedDotnetGC.Tests;

[TestFixture]
public class SegmentTests
{
    private const nint SegmentSize = 64 * 1024; // 64 KB
    private NativeAllocator _allocator = null!;
    private SegmentManager _segmentManager = null!;

    [SetUp]
    public void SetUp()
    {
        _allocator = new NativeAllocator(16 * 1024 * 1024);
        _segmentManager = new SegmentManager(_allocator);
    }

    [TearDown]
    public void TearDown()
    {
        _allocator.Dispose();
    }

    [Test]
    public void Constructor_InitializesFields()
    {
        var segment = _segmentManager.AllocateSegment(SegmentSize);

        segment.Start.ShouldNotBe(IntPtr.Zero);
        segment.ObjectStart.ShouldBeGreaterThan(segment.Start);
        segment.Current.ShouldBe(segment.ObjectStart);
        (segment.End - segment.ObjectStart).ShouldBe(SegmentSize);
        var totalSlots = SegmentSize / IntPtr.Size;
        var expectedLength = (int)((totalSlots + 255 - 1) / 255);
        segment.GetBrickTable().Length.ShouldBeGreaterThanOrEqualTo(expectedLength);
    }

    [Test]
    public void Constructor_BrickTableStartsEmpty()
    {
        var segment = _segmentManager.AllocateSegment(SegmentSize);

        segment.GetBrickTable().ToArray().ShouldAllBe(b => b == 0);
    }

    [Test]
    public void MarkObject_SetsBrickTableEntry()
    {
        var segment = _segmentManager.AllocateSegment(SegmentSize);

        var addr = segment.ObjectStart + IntPtr.Size;
        segment.MarkObject(addr);

        segment.GetBrickTable().ToArray().ShouldContain(b => b != 0);
    }

    [Test]
    public void MarkObject_KeepsLatestObjectInChunk()
    {
        var segment = _segmentManager.AllocateSegment(SegmentSize);

        var first = segment.ObjectStart + IntPtr.Size;
        var second = segment.ObjectStart + IntPtr.Size * 10;

        // Mark first, then second — the map should keep the latest
        segment.MarkObject(first);
        var valueAfterFirst = segment.GetBrickTable()[0];

        segment.MarkObject(second);
        var valueAfterSecond = segment.GetBrickTable()[0];

        valueAfterSecond.ShouldBeGreaterThan(valueAfterFirst);
    }

    [Test]
    public void MarkObject_DoesNotOverwriteWithEarlierObject()
    {
        var segment = _segmentManager.AllocateSegment(SegmentSize);

        var first = segment.ObjectStart + IntPtr.Size;
        var second = segment.ObjectStart + IntPtr.Size * 10;

        segment.MarkObject(second);
        var valueAfterSecond = segment.GetBrickTable()[0];

        segment.MarkObject(first);
        var valueAfterFirst = segment.GetBrickTable()[0];

        valueAfterFirst.ShouldBe(valueAfterSecond);
    }

    [Test]
    public void FindClosestObjectBelow_ReturnsMarkedObject()
    {
        var segment = _segmentManager.AllocateSegment(SegmentSize);

        var objAddr = segment.ObjectStart + IntPtr.Size * 5;
        segment.MarkObject(objAddr);

        var result = segment.FindClosestObjectBelow(objAddr);

        result.ShouldBe(objAddr);
    }

    [Test]
    public void FindClosestObjectBelow_ReturnsLastObjectWhenQueryIsAboveInSameChunk()
    {
        var segment = _segmentManager.AllocateSegment(SegmentSize);

        var first = segment.ObjectStart + IntPtr.Size * 5;
        var last = segment.ObjectStart + IntPtr.Size * 80;
        segment.MarkObject(first);
        segment.MarkObject(last);

        var queryAddr = segment.ObjectStart + IntPtr.Size * 100;
        var result = segment.FindClosestObjectBelow(queryAddr);

        result.ShouldBe(last);
    }

    [Test]
    public void FindClosestObjectBelow_ReturnsStartWhenNoObjectsMarked()
    {
        var segment = _segmentManager.AllocateSegment(SegmentSize);

        var queryAddr = segment.ObjectStart + IntPtr.Size * 100;
        var result = segment.FindClosestObjectBelow(queryAddr);

        result.ShouldBe(segment.ObjectStart);
    }

    [Test]
    public void FindClosestObjectBelow_ReturnsObjectFromPreviousChunk()
    {
        var segment = _segmentManager.AllocateSegment(SegmentSize);

        // Mark an object in chunk 0
        var objAddr = segment.ObjectStart + IntPtr.Size * 10;
        segment.MarkObject(objAddr);

        // Query from a slot in chunk 1 (slots 255+)
        var queryAddr = segment.ObjectStart + IntPtr.Size * 300;
        var result = segment.FindClosestObjectBelow(queryAddr);

        result.ShouldBe(objAddr);
    }

    [Test]
    public void FindClosestObjectBelow_PicksClosestChunkObject()
    {
        var segment = _segmentManager.AllocateSegment(SegmentSize);

        // Mark an object in chunk 0
        var obj1 = segment.ObjectStart + IntPtr.Size * 5;
        segment.MarkObject(obj1);

        // Mark two objects in chunk 1 (slot 255+)
        var obj2a = segment.ObjectStart + IntPtr.Size * 260;
        var obj2b = segment.ObjectStart + IntPtr.Size * 350;
        segment.MarkObject(obj2a);
        segment.MarkObject(obj2b);

        // Query from chunk 1, after both — should return the last one (obj2b)
        var queryAddr = segment.ObjectStart + IntPtr.Size * 400;
        var result = segment.FindClosestObjectBelow(queryAddr);

        result.ShouldBe(obj2b);
    }

    [Test]
    public void FindClosestObjectBelow_AtExactChunkBoundary()
    {
        var segment = _segmentManager.AllocateSegment(SegmentSize);

        var objAddr = segment.ObjectStart + IntPtr.Size * 255;
        segment.MarkObject(objAddr);

        var result = segment.FindClosestObjectBelow(objAddr);

        result.ShouldBe(objAddr);
    }

    [Test]
    public void FindClosestObjectBelow_MultipleChunks_SkipsEmptyChunks()
    {
        var segment = _segmentManager.AllocateSegment(SegmentSize);

        // Mark object in chunk 0 only
        var objAddr = segment.ObjectStart + IntPtr.Size * 3;
        segment.MarkObject(objAddr);

        // Query from chunk 2 (slot 510+), chunk 1 is empty
        var queryAddr = segment.ObjectStart + IntPtr.Size * 520;
        var result = segment.FindClosestObjectBelow(queryAddr);

        result.ShouldBe(objAddr);
    }

    [Test]
    public void MarkObject_DifferentChunks_AreIndependent()
    {
        var segment = _segmentManager.AllocateSegment(SegmentSize);

        var obj1 = segment.ObjectStart + IntPtr.Size * 10;   // chunk 0
        var obj2 = segment.ObjectStart + IntPtr.Size * 260;  // chunk 1

        segment.MarkObject(obj1);
        segment.MarkObject(obj2);

        // Each chunk byte should reflect its own first object
        segment.GetBrickTable()[0].ShouldNotBe((byte)0);
        segment.GetBrickTable()[1].ShouldNotBe((byte)0);

        // Querying within chunk 0 returns obj1 (it's the only and thus the last object in chunk 0)
        segment.FindClosestObjectBelow(segment.ObjectStart + IntPtr.Size * 200).ShouldBe(obj1);

        // Querying within chunk 1 returns obj2 (it's the only and thus the last object in chunk 1)
        segment.FindClosestObjectBelow(segment.ObjectStart + IntPtr.Size * 400).ShouldBe(obj2);
    }

    [Test]
    public void FindClosestObjectBelow_AtStartOfSegment()
    {
        var segment = _segmentManager.AllocateSegment(SegmentSize);

        var objAddr = segment.ObjectStart;
        segment.MarkObject(objAddr);

        var result = segment.FindClosestObjectBelow(segment.ObjectStart);

        result.ShouldBe(segment.ObjectStart);
    }
}
