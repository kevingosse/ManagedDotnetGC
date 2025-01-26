using System.Runtime.InteropServices;

namespace ManagedDotnetGC;

[StructLayout(LayoutKind.Sequential)]
public readonly struct GcDacVars
{
    public readonly byte Major_version_number;
    public readonly byte Minor_version_number;
    public readonly nint Generation_size;
    public readonly nint Total_generation_count;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct VersionInfo
{
    public int MajorVersion;
    public int MinorVersion;
    public int BuildVersion;
    public byte* Name;
}