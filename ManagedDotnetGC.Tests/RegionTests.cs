using NUnit.Framework;
using Shouldly;

namespace ManagedDotnetGC.Tests;

/// <summary>Pure-logic tests for the region constants and formulas (SPEC-M2 §12).</summary>
[TestFixture]
public class RegionTests
{
    // An object of size S goes to the smallest class with classSize >= S + 8: the +8 is
    // the pre-header slot of the object placed at blockStart + 8 (SPEC-M2 §4.2)
    [TestCase(32 * 1024 - 8, 0)]       // below the bump/class boundary, still maps to class 0
    [TestCase(32 * 1024, 0)]
    [TestCase(32 * 1024 + 8, 0)]       // first size the bump tier rejects
    [TestCase(40 * 1024 - 8, 0)]       // exact fit: needed == 40 KB
    [TestCase(40 * 1024 - 7, 1)]       // one byte over: next class
    [TestCase(40 * 1024, 1)]
    [TestCase(1024 * 1024 - 8, 19)]    // exact fit in the last class
    [TestCase(1024 * 1024 - 7, -1)]    // one byte over: span tier
    [TestCase(2 * 1024 * 1024, -1)]
    public void SelectClass_MapsBoundariesCorrectly(int size, int expectedClass)
    {
        Region.SelectClass(size).ShouldBe(expectedClass);
    }

    [Test]
    public void SelectClass_NeverWastesMoreThanAClassStep()
    {
        // Walk every allocatable size (8-byte aligned) and check the class invariants
        for (nint size = Region.BumpMaxSize + 8; size + IntPtr.Size <= Region.SizeClassMaxSize; size += 8)
        {
            var cls = Region.SelectClass(size);

            cls.ShouldBeInRange(0, Region.ClassCount - 1);
            ((nint)Region.ClassSizes[cls]).ShouldBeGreaterThanOrEqualTo(size + IntPtr.Size);

            if (cls > 0)
            {
                // The class below must be too small, or the object is in the wrong class
                ((nint)Region.ClassSizes[cls - 1]).ShouldBeLessThan(size + IntPtr.Size);
            }
        }
    }

    [Test]
    public void ClassSizes_AllDivideIntoAtMost64Blocks()
    {
        // The per-region allocation bitmap is a single ulong, so no class may yield more
        // than 64 blocks; and every class must fit at least 2 blocks per region or the
        // size-class tier degenerates into the span tier. (Classes need not divide the
        // region evenly — the tail waste is a recorded §13 tuning knob.)
        for (int i = 0; i < Region.ClassCount; i++)
        {
            RegionAllocator.BlockCount(i).ShouldBeInRange(2, 64);
        }
    }

    [Test]
    public void FullMask_MatchesBlockCount()
    {
        for (int i = 0; i < Region.ClassCount; i++)
        {
            var mask = RegionAllocator.FullMask(i);
            System.Numerics.BitOperations.PopCount(mask).ShouldBe(RegionAllocator.BlockCount(i));
        }

        // The largest class: 2 MB / 1 MB = 2 blocks
        RegionAllocator.FullMask(Region.ClassCount - 1).ShouldBe(0b11ul);
    }

    [TestCase(1024 * 1024 - 7, 1)]                 // smallest span-tier object
    [TestCase(2 * 1024 * 1024 - 8, 1)]             // largest single-region span (+8 pre-header = 2 MB)
    [TestCase(2 * 1024 * 1024 - 7, 2)]             // one byte over: two regions
    [TestCase(4 * 1024 * 1024, 3)]                 // 4 MB + 8 needs a third region
    [TestCase(100 * 1024 * 1024, 51)]
    public void SpanRegionCount_MapsBoundariesCorrectly(int size, int expectedCount)
    {
        Region.SpanRegionCount(size).ShouldBe(expectedCount);
    }

    [Test]
    public void ComputeBudget_FloorsAtMinBudget()
    {
        Region.ComputeBudget(0).ShouldBe(Region.MinGCBudget);
        Region.ComputeBudget(Region.MinGCBudget - 1).ShouldBe(Region.MinGCBudget);
        Region.ComputeBudget(Region.MinGCBudget + 1).ShouldBe(Region.MinGCBudget + 1);
        Region.ComputeBudget(10L * 1024 * 1024 * 1024).ShouldBe(10L * 1024 * 1024 * 1024);
    }

    [Test]
    public void NextEpoch_IncrementsAndSkipsZeroOnWrap()
    {
        GCObject.NextEpoch(1).ShouldBe(2u);
        GCObject.NextEpoch(uint.MaxValue - 1).ShouldBe(uint.MaxValue);

        // 0 is what freshly-zeroed memory reads, so the sequence must skip it on wrap
        GCObject.NextEpoch(uint.MaxValue).ShouldBe(1u);
    }

    [Test]
    public void NextEpoch_WrapIsDetectableByOrdering()
    {
        // AdvanceEpoch detects the wrap (and clears all stamps) via next < current
        GCObject.NextEpoch(uint.MaxValue).ShouldBeLessThan(uint.MaxValue);
        GCObject.NextEpoch(12345).ShouldBeGreaterThan(12345u);
    }
}
