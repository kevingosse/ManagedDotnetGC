using System.Runtime.InteropServices;

namespace ManagedDotnetGC;

public unsafe class Segment
{
    public IntPtr Start;
    public IntPtr Current;
    public IntPtr End;
    public byte[] Map;

    public Segment(nint size)
    {
        Start = (IntPtr)NativeMemory.AllocZeroed((nuint)size);
        Current = Start;
        End = Start + size;

        // Every alloc context is aligned on IntPtr.Size
        // Map is a bitmap indicating where alloc contexts are located
        // The size of the map is (size / AlignSize) / 8 bytes
        Map = new byte[size / IntPtr.Size / 8];
    }

    public void MarkObject(IntPtr addr)
    {
        var index = (addr - Start) / IntPtr.Size;
        var byteIndex = index / 8;
        var bitIndex = (byte)(index % 8);
        Map[byteIndex] |= (byte)(1 << bitIndex);
    }

    public IntPtr FindClosestObjectBelow(IntPtr addr)
    {
        var index = (addr - Start) / IntPtr.Size;
        var byteIndex = index / 8;
        var bitIndex = (byte)(index % 8);
        for (var i = byteIndex; i >= 0; i--)
        {
            var b = Map[i];
            for (var j = (i == byteIndex ? bitIndex : (byte)7); j < 8; j--)
            {
                if ((b & (1 << j)) != 0)
                {
                    var foundIndex = i * 8 + j;
                    return Start + foundIndex * IntPtr.Size;
                }
            }
        }

        return Start;
    }
}