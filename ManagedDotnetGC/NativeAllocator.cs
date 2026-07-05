using System.Runtime.InteropServices;

namespace ManagedDotnetGC;

/// <summary>
/// Owns the heap reservation and wraps the OS memory primitives. The reservation is
/// over-sized by one region so the usable range can be aligned to region boundaries
/// (VirtualAlloc only guarantees 64 KB alignment, region index math needs 2 MB).
/// All post-initialization operations are non-throwing (SPEC-M2 §9).
/// </summary>
internal partial class NativeAllocator : IDisposable
{
    private const uint MEM_COMMIT = 0x00001000;
    private const uint MEM_RESERVE = 0x00002000;
    private const uint MEM_DECOMMIT = 0x00004000;
    private const uint MEM_RELEASE = 0x00008000;

    private const uint PAGE_READWRITE = 0x04;

    private nint _reservation;
    private readonly nint _lowestAddress;
    private readonly nint _highestAddress;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr VirtualAlloc(IntPtr lpAddress, UIntPtr dwSize, uint flAllocationType, uint flProtect);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool VirtualFree(IntPtr lpAddress, UIntPtr dwSize, uint dwFreeType);

    public NativeAllocator(long size)
    {
        _reservation = VirtualAlloc(IntPtr.Zero, (UIntPtr)(size + Region.Size), MEM_RESERVE, PAGE_READWRITE);

        if (_reservation == IntPtr.Zero)
        {
            // Initialization-time failure: the process cannot run without the heap
            throw new OutOfMemoryException("Failed to reserve memory");
        }

        _lowestAddress = (_reservation + Region.Size - 1) & ~(Region.Size - 1);
        _highestAddress = _lowestAddress + (nint)size;
    }

    public nint LowestAddress => _lowestAddress;

    public nint HighestAddress => _highestAddress;

    public bool IsInRange(nint ptr) => ptr >= LowestAddress && ptr < HighestAddress;

    public bool TryCommit(nint address, nint size)
    {
        return VirtualAlloc(address, (UIntPtr)size, MEM_COMMIT, PAGE_READWRITE) != IntPtr.Zero;
    }

    public bool Decommit(nint address, nint size)
    {
        return VirtualFree(address, (UIntPtr)size, MEM_DECOMMIT);
    }

    /// <summary>Reserves address space outside the heap range (metadata tables).</summary>
    public static nint OsReserve(nint size)
    {
        return VirtualAlloc(IntPtr.Zero, (UIntPtr)size, MEM_RESERVE, PAGE_READWRITE);
    }

    public static bool OsCommit(nint address, nint size)
    {
        return VirtualAlloc(address, (UIntPtr)size, MEM_COMMIT, PAGE_READWRITE) != IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_reservation != IntPtr.Zero)
        {
            VirtualFree(_reservation, UIntPtr.Zero, MEM_RELEASE);
            _reservation = IntPtr.Zero;
        }
    }
}
