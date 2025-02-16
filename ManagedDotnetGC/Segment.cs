using System.Runtime.InteropServices;

namespace ManagedDotnetGC;

public unsafe class Segment
{
    public IntPtr Start;
    public IntPtr Current;
    public IntPtr End;

    public Segment(nint size)
    {
        Start = (IntPtr)NativeMemory.AllocZeroed((nuint)size);
        Current = Start;
        End = Start + size;
    }
}