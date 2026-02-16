using NUnit.Framework;
using Shouldly;

namespace ManagedDotnetGC.Tests;

[TestFixture]
public class SegmentManagerTests
{
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
    public void AllocateSegment_AddsToSegmentsList()
    {
        _segmentManager.Segments.Count.ShouldBe(0);

        var segment = _segmentManager.AllocateSegment(64 * 1024);

        _segmentManager.Segments.Count.ShouldBe(1);
        _segmentManager.Segments[0].Start.ShouldBe(segment.Start);
    }

    [Test]
    public void AllocateSegment_MultipleSegments_ProducesDistinctNonOverlapping()
    {
        var segment1 = _segmentManager.AllocateSegment(64 * 1024);
        var segment2 = _segmentManager.AllocateSegment(64 * 1024);

        _segmentManager.Segments.Count.ShouldBe(2);
        segment1.Start.ShouldNotBe(segment2.Start);
        segment1.End.ShouldBeLessThanOrEqualTo(segment2.Start);
    }

    [Test]
    public void FindSegmentContaining_ReturnsSegment_ForAddressInObjectSpace()
    {
        var segment = _segmentManager.AllocateSegment(64 * 1024);

        var result = _segmentManager.FindSegmentContaining(segment.ObjectStart + IntPtr.Size * 10);

        result.IsNull.ShouldBeFalse();
        result.Start.ShouldBe(segment.Start);
    }

    [Test]
    public void FindSegmentContaining_ReturnsSegment_ForAddressAtStart()
    {
        var segment = _segmentManager.AllocateSegment(64 * 1024);

        var result = _segmentManager.FindSegmentContaining(segment.Start);

        result.IsNull.ShouldBeFalse();
        result.Start.ShouldBe(segment.Start);
    }

    [Test]
    public void FindSegmentContaining_ReturnsSegment_ForLastByteInSegment()
    {
        var segment = _segmentManager.AllocateSegment(64 * 1024);

        var result = _segmentManager.FindSegmentContaining(segment.End - 1);

        result.IsNull.ShouldBeFalse();
        result.Start.ShouldBe(segment.Start);
    }

    [Test]
    public void FindSegmentContaining_ReturnsNull_ForAddressAtEnd()
    {
        var segment = _segmentManager.AllocateSegment(64 * 1024);

        var result = _segmentManager.FindSegmentContaining(segment.End);

        result.IsNull.ShouldBeTrue();
    }

    [Test]
    public void FindSegmentContaining_ReturnsNull_ForAddressBeforeFirstSegment()
    {
        _segmentManager.AllocateSegment(64 * 1024);

        // Address in the segment table area (before any segments)
        var result = _segmentManager.FindSegmentContaining(_allocator.LowestAddress + 100);

        result.IsNull.ShouldBeTrue();
    }

    [Test]
    public void FindSegmentContaining_ReturnsNull_ForZeroAddress()
    {
        _segmentManager.AllocateSegment(64 * 1024);

        var result = _segmentManager.FindSegmentContaining(0);

        result.IsNull.ShouldBeTrue();
    }

    [Test]
    public void FindSegmentContaining_MultipleSegments_FindsCorrectOne()
    {
        // Use SegmentSize so each segment occupies its own chunk in the segment table
        var segment1 = _segmentManager.AllocateSegment(GCHeap.SegmentSize);
        var segment2 = _segmentManager.AllocateSegment(GCHeap.SegmentSize);

        var result1 = _segmentManager.FindSegmentContaining(segment1.ObjectStart + IntPtr.Size);
        var result2 = _segmentManager.FindSegmentContaining(segment2.ObjectStart + IntPtr.Size);

        result1.Start.ShouldBe(segment1.Start);
        result2.Start.ShouldBe(segment2.Start);
    }

    [Test]
    public void FindSegmentContaining_ChunkBoundaryFallback()
    {
        // Allocate a segment large enough to cross a SegmentSize (4MB) chunk boundary.
        // The segment table itself occupies chunk 0. A 4MB segment starting at chunk 1
        // will extend into chunk 2 due to header + brick table overhead.
        var largeSegment = _segmentManager.AllocateSegment(GCHeap.SegmentSize);

        // Allocate another segment — it starts in chunk 2, overwriting that table entry
        var nextSegment = _segmentManager.AllocateSegment(64 * 1024);

        // Look up an address near the end of the large segment (in the overwritten chunk).
        // The direct table lookup at that chunk index finds nextSegment, which doesn't
        // contain the address. The fallback to the previous chunk recovers largeSegment.
        var addrNearEnd = largeSegment.End - IntPtr.Size;
        var result = _segmentManager.FindSegmentContaining(addrNearEnd);

        result.IsNull.ShouldBeFalse();
        result.Start.ShouldBe(largeSegment.Start);

        // The next segment should also be findable
        var result2 = _segmentManager.FindSegmentContaining(nextSegment.ObjectStart + IntPtr.Size);
        result2.IsNull.ShouldBeFalse();
        result2.Start.ShouldBe(nextSegment.Start);
    }
}
