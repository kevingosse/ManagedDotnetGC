using System.Runtime.InteropServices;

namespace ManagedDotnetGC;

internal static class Region
{
    public const int Shift = 21;
    public const nint Size = 1 << Shift;                        // 2 MiB
    public const long HeapReserveSize = 2L << 40;               // 2 TiB
    public const int Count = (int)(HeapReserveSize >> Shift);   // 1,048,576

    public const nint BumpMaxSize = 32 * 1024;                  // ≤ 32 KB objects → bump tier
    public const nint WindowSize = 128 * 1024;                  // default alloc-context window

    // Holes below half a window are plugged but never carved (M4): serving tiny holes
    // multiplies the handout cadence and smears the nursery across every old region, which
    // is exactly what sticky young collections cannot afford (each re-opened region gets
    // card-scanned and swept). Sub-floor holes stay dead until a full collection. Half a
    // window (not a full one) because survivor gaps cluster tightly around their mean —
    // a floor above the typical gap strands nearly all of the reclaimable bytes.
    public const nint MinLinkedHole = WindowSize / 2;
    public const nint GuardBytes = 16;                          // reserved tail of every bump region

    public const long MinGCBudget = 64L * 1024 * 1024;

    // 20 size classes, 4 linear steps per power of two (SPEC-M2 §4.2). An object of size S
    // goes to the smallest class with classSize ≥ S + 8 (the +8 leaves room for the
    // pre-header of the object placed at blockStart + 8).
    public static ReadOnlySpan<int> ClassSizes =>
    [
         40 * 1024,  48 * 1024,  56 * 1024,  64 * 1024,
         80 * 1024,  96 * 1024, 112 * 1024, 128 * 1024,
        160 * 1024, 192 * 1024, 224 * 1024, 256 * 1024,
        320 * 1024, 384 * 1024, 448 * 1024, 512 * 1024,
        640 * 1024, 768 * 1024, 896 * 1024, 1024 * 1024,
    ];

    public const int ClassCount = 20;
    public const nint SizeClassMaxSize = 1024 * 1024;

    /// <summary>Post-collection allocation budget (SPEC-M2 §8.3): the heap converges to
    /// ≈ 2× live. Also the pool's committed-slack retention target (M4): the next cycle
    /// carves a budget's worth of regions back out of the pool.</summary>
    public static long ComputeBudget(long liveBytes) => Math.Max(MinGCBudget, liveBytes);

    /// <summary>Regions needed for a span object of the given size (SPEC-M2 §4.3): the
    /// object ref sits at spanBase + 8, so the pre-header byte counts toward the span.</summary>
    public static int SpanRegionCount(nint size) => (int)((size + IntPtr.Size + Size - 1) >> Shift);

    public static int SelectClass(nint size)
    {
        var needed = size + IntPtr.Size;

        var classes = ClassSizes;

        for (int i = 0; i < classes.Length; i++)
        {
            if (classes[i] >= needed)
            {
                return i;
            }
        }

        return -1;
    }
}

internal enum RegionKind : byte
{
    Free = 0,
    Bump,
    SizeClass,
    SpanStart,
    SpanExtension,
}

/// <summary>
/// Region age for sticky-generation collections (SPEC-M4). Young collections do object
/// work (mark accounting, sweep, recycle) only in non-Old regions; the card scan covers
/// non-Fresh regions (anything that may hold sticky-marked old objects).
/// </summary>
internal enum RegionAge : byte
{
    /// <summary>Sealed by a sweep: holds only sticky-marked survivors and their holes.</summary>
    Old = 0,
    /// <summary>Carved since the last collection: holds only young objects.</summary>
    Fresh = 1,
    /// <summary>Old region re-opened for allocation (hole carve or bump-tail continuation):
    /// mixed ages — card-scanned like Old, swept like Fresh. The sweep walk is safe because
    /// sticky marks keep the old survivors marked; the wholesale-recycle shortcut is not
    /// (LiveBytes only counts objects marked this cycle), so it is gated to Fresh.</summary>
    Reopened = 2,
}

/// <summary>
/// One entry per 2 MB region, in a flat table indexed by (addr - heapBase) >> Region.Shift.
/// The fields at offsets 8 and 16 are kind-specific unions (SPEC-M2 §2).
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 32)]
internal struct RegionEntry
{
    [FieldOffset(0)] public RegionKind Kind;
    [FieldOffset(1)] public byte SizeClass;        // SizeClass only
    [FieldOffset(1)] public byte BumpIsDirty;      // Bump only: 1 = recycled without zeroing,
                                                   // windows must be zeroed at carve (M4)
    [FieldOffset(1)] public byte SpanIsDirty;      // SpanStart/SpanExtension only: 1 = carved
                                                   // with stale contents; the object extent is
                                                   // zeroed outside the alloc lock (M4)
    [FieldOffset(2)] public byte IsCommitted;      // Free only: 0 after decommit
    [FieldOffset(3)] public RegionAge Age;         // sticky-generation age (SPEC-M4)
    [FieldOffset(4)] public int LiveBytes;         // rebuilt by every mark phase, consumed by sweep

    [FieldOffset(8)] public nint Cursor;           // Bump: allocation high-water mark (absolute)
    [FieldOffset(8)] public ulong AllocatedBlocks; // SizeClass: bit i set = block i allocated
    [FieldOffset(8)] public int SpanCount;         // SpanStart: regions in the span (incl. self)
    [FieldOffset(8)] public int SpanStartIndex;    // SpanExtension: region index of the SpanStart

    [FieldOffset(16)] public nint FirstHole;       // Bump: head of hole list (plug object ref, 0 = none)
    [FieldOffset(16)] public int NextInClassList;  // SizeClass: next region index in class alloc list (-1 = end)

    [FieldOffset(24)] public int HoleBytes;        // Bump: total carveable extent bytes
    [FieldOffset(28)] public int NextRecycled;     // Bump: next region index in recycled list (-1 = end)
}
