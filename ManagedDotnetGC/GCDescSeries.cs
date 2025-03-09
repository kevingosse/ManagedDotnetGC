using System.Runtime.InteropServices;

namespace ManagedDotnetGC;

[StructLayout(LayoutKind.Sequential)]
internal struct GCDescSeries
{
    public nint Size;
    public nint Offset;

    public void Deconstruct(out nint size, out nint offset)
    {
        size = Size;
        offset = Offset;
    }
}

internal struct ValSerieItem(nint value)
{
    public uint Nptrs => IntPtr.Size == 4 ? (ushort)(value & 0xFFFF) : (uint)(value & 0xFFFFFFFF);

    public uint Skip => IntPtr.Size == 4 ? (ushort)((value >> 16) & 0xFFFF) : (uint)(((long)value >> 32) & 0xFFFFFFFF);
}
