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
    private nint _lowestAddress;
    private nint _highestAddress;
    private int _isAddressRangeExclusive;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAlloc(
        IntPtr lpAddress,
        UIntPtr dwSize,
        uint flAllocationType,
        uint flProtect);

    public NativeAllocator(long size)
    {
        _lowestAddress = VirtualAlloc(IntPtr.Zero, (UIntPtr)size, MEM_RESERVE, PAGE_READWRITE);

        _isAddressRangeExclusive = _lowestAddress != IntPtr.Zero ? 1 : 0;

        if (IsAddressRangeExclusive)
        {
            _highestAddress = _lowestAddress + (nint)size;
            _nextFreeAddress = _lowestAddress;
        }
    }

    public bool IsAddressRangeExclusive => Volatile.Read(ref _isAddressRangeExclusive) == 1;

    public nint LowestAddress => Volatile.Read(ref _lowestAddress);

    public nint HighestAddress => Volatile.Read(ref _highestAddress);

    public bool IsInRange(nint ptr) => ptr >= LowestAddress && ptr < HighestAddress;

    public nint Allocate(nint size)
    {
        if (size <= 0)
        {
            return IntPtr.Zero;
        }

        if (!IsAddressRangeExclusive)
        {
            return AllocateFallback(size);
        }

        var alignedSize = (size + (PageSize - 1)) & ~(nint)(PageSize - 1);
        nint address;

        while (true)
        {
            address = Volatile.Read(ref _nextFreeAddress);
            var end = address + alignedSize;

            if (end > HighestAddress)
            {
                Interlocked.Exchange(ref _isAddressRangeExclusive, 0);
                return AllocateFallback(size);
            }

            if (Interlocked.CompareExchange(ref _nextFreeAddress, end, address) == address)
            {
                break;
            }
        }

        var result = VirtualAlloc(address, (UIntPtr)alignedSize, MEM_COMMIT, PAGE_READWRITE);

        if (result == IntPtr.Zero)
        {
            throw new OutOfMemoryException("VirtualAlloc failed to commit memory");
        }

        return address;
    }

    private nint AllocateFallback(nint size)
    {
        var ptr = (nint)NativeMemory.AllocZeroed((nuint)size);

        nint current;

        do
        {
            current = Volatile.Read(ref _lowestAddress);

            if (ptr >= current)
            {
                break;
            }
        } while (Interlocked.CompareExchange(ref _lowestAddress, ptr, current) != current);

        var ptrEnd = ptr + size;

        do
        {
            current = Volatile.Read(ref _highestAddress);

            if (ptrEnd <= current)
            {
                break;
            }
        } while (Interlocked.CompareExchange(ref _highestAddress, ptrEnd, current) != current);

        return ptr;
    }
}
