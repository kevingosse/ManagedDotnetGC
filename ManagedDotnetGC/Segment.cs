using System.Runtime.InteropServices;

namespace ManagedDotnetGC;

[StructLayout(LayoutKind.Sequential)]
internal struct SegmentHeader
{
    public nint ObjectStart;
    public nint End;
    public nint Current;
}

internal readonly unsafe struct Segment
{
    private readonly nint _start;

    // Each byte covers 255 pointer-aligned slots (~2040 bytes on 64-bit).
    // Byte value 0 = no object. Values 1-255 = 1-based position of the last object in the chunk.
    internal const int SlotsPerChunk = 255;

    internal Segment(nint start) => _start = start;

    private SegmentHeader* Header => (SegmentHeader*)_start;

    public bool IsNull => _start == 0;

    public nint Start => _start;
    public nint ObjectStart => Header->ObjectStart;
    public nint End => Header->End;
    public ref nint Current => ref Header->Current;

    // The brick table is stored between the header and ObjectStart.
    public Span<byte> GetBrickTable()
    {
        var brickTableStart = _start + sizeof(SegmentHeader);
        return new Span<byte>((void*)brickTableStart, (int)(ObjectStart - brickTableStart));
    }

    public void MarkObject(IntPtr addr)
    {
        var slotIndex = (addr - ObjectStart) / IntPtr.Size;
        var chunkIndex = (int)(slotIndex / SlotsPerChunk);
        var posInChunk = (byte)(slotIndex % SlotsPerChunk + 1);

        var brickTable = GetBrickTable();

        if (brickTable[chunkIndex] == 0 || posInChunk > brickTable[chunkIndex])
        {
            brickTable[chunkIndex] = posInChunk;
        }
    }

    public IntPtr FindClosestObjectBelow(IntPtr addr)
    {
        var slotIndex = (addr - ObjectStart) / IntPtr.Size;
        var chunkIndex = (int)(slotIndex / SlotsPerChunk);
        var posInChunk = slotIndex % SlotsPerChunk;

        var brickTable = GetBrickTable();

        if (brickTable[chunkIndex] != 0 && brickTable[chunkIndex] - 1 <= posInChunk)
        {
            return ObjectStart + (chunkIndex * SlotsPerChunk + (brickTable[chunkIndex] - 1)) * IntPtr.Size;
        }

        var i = brickTable[..chunkIndex].LastIndexOfAnyExcept((byte)0);

        if (i >= 0)
        {
            return ObjectStart + (i * SlotsPerChunk + (brickTable[i] - 1)) * IntPtr.Size;
        }

        return ObjectStart;
    }
}
