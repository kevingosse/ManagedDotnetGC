namespace ManagedDotnetGC;

internal class Segment : IDisposable
{
    private readonly NativeAllocator _allocator;
    public readonly IntPtr Start;
    public readonly IntPtr ObjectStart;
    public readonly IntPtr End;
    public IntPtr Current;

    // Each byte covers 255 pointer-aligned slots (~2040 bytes on 64-bit).
    // Byte value 0 = no object. Values 1-255 = 1-based position of the last object in the chunk.
    private const int SlotsPerChunk = 255;

    public Segment(nint size, NativeAllocator allocator)
    {
        _allocator = allocator;

        var totalSlots = size / IntPtr.Size;
        var brickTableLength = (int)((totalSlots + SlotsPerChunk - 1) / SlotsPerChunk);
        var brickTableAlignedSize = (nint)((brickTableLength + IntPtr.Size - 1) & ~(IntPtr.Size - 1));

        Start = allocator.Allocate(size + brickTableAlignedSize);
        ObjectStart = Start + brickTableAlignedSize;
        Current = ObjectStart;
        End = Start + size + brickTableAlignedSize;
    }

    // The brick table is stored at [Start, ObjectStart) in native memory.
    public unsafe Span<byte> GetBrickTable() => new((void*)Start, (int)(ObjectStart - Start));

    public void Dispose()
    {
        _allocator.Free(Start, End - Start);
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