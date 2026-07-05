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
    public const nint MinLinkedHole = 4 * 1024;                 // smaller holes: plugged, not carved
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
/// One entry per 2 MB region, in a flat table indexed by (addr - heapBase) >> Region.Shift.
/// The fields at offsets 8 and 16 are kind-specific unions (SPEC-M2 §2).
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 32)]
internal struct RegionEntry
{
    [FieldOffset(0)] public RegionKind Kind;
    [FieldOffset(1)] public byte SizeClass;        // SizeClass only
    [FieldOffset(2)] public byte IsCommitted;      // Free only: 0 after decommit
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
