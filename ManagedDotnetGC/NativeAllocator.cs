using System.Runtime.InteropServices;

namespace ManagedDotnetGC;

internal unsafe class NativeAllocator
{
    private const uint MEM_COMMIT = 0x00001000;
    private const uint MEM_RESERVE = 0x00002000;
    private const uint MEM_RESET = 0x00080000;

    private const uint PAGE_READWRITE = 0x04;

    private static readonly int PageSize = Environment.SystemPageSize;

    private nint _nextFreeAddress;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAlloc(
        IntPtr lpAddress,
        UIntPtr dwSize,
        uint flAllocationType,
        uint flProtect);

    public NativeAllocator(long size)
    {
        LowestAddress = VirtualAlloc(IntPtr.Zero, (UIntPtr)size, MEM_RESERVE, PAGE_READWRITE);

        IsAddressRangeExclusive = LowestAddress != IntPtr.Zero;

        if (IsAddressRangeExclusive)
        {
            HighestAddress = LowestAddress + (nint)size;
            _nextFreeAddress = LowestAddress;
        }
    }

    public bool IsAddressRangeExclusive { get; private set; }

    public nint LowestAddress { get; private set; }

    public nint HighestAddress { get; private set; }

    public bool IsInRange(nint ptr) => ptr >= LowestAddress && ptr < HighestAddress;

    public nint Allocate(nint size)
    {
        if (size <= 0)
        {
            return IntPtr.Zero;
        }

        if (!IsAddressRangeExclusive)
        {
            // Fallback to regular heap
            var ptr = (nint)NativeMemory.AllocZeroed((nuint)size);

            if (ptr < LowestAddress)
            {
                LowestAddress = ptr;
            }

            if (ptr + size > HighestAddress)
            {
                HighestAddress = ptr + size;
            }

            return ptr;
        }

        var alignedSize = (size + (nint.Size - 1)) & ~(nint.Size - 1);

        var address = _nextFreeAddress;
        var end = address + alignedSize;

        if (end > HighestAddress)
        {
            IsAddressRangeExclusive = false;
            return Allocate(size);
        }

        var commitStart = address & ~(nint)(PageSize - 1);
        var commitEnd = (end + PageSize - 1) & ~(nint)(PageSize - 1);

        var result = VirtualAlloc(commitStart, (UIntPtr)(commitEnd - commitStart), MEM_COMMIT, PAGE_READWRITE);

        if (result == IntPtr.Zero)
        {
            throw new OutOfMemoryException("VirtualAlloc failed to commit memory");
        }

        _nextFreeAddress = end;
        return address;
    }
}
