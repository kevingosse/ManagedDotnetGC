using System.Runtime.InteropServices;

namespace ManagedDotnetGC;

internal partial class NativeAllocator : IDisposable
{
    private const uint MEM_COMMIT = 0x00001000;
    private const uint MEM_RESERVE = 0x00002000;
    private const uint MEM_DECOMMIT = 0x00004000;
    private const uint MEM_RELEASE = 0x00008000;

    private const uint PAGE_READWRITE = 0x04;

    private static readonly int PageSize = Environment.SystemPageSize;

    private nint _nextFreeAddress;
    private nint _lowestAddress;
    private nint _highestAddress;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr VirtualAlloc(IntPtr lpAddress, UIntPtr dwSize, uint flAllocationType, uint flProtect);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool VirtualFree(IntPtr lpAddress, UIntPtr dwSize, uint dwFreeType);

    public NativeAllocator(long size)
    {
        _lowestAddress = VirtualAlloc(IntPtr.Zero, (UIntPtr)size, MEM_RESERVE, PAGE_READWRITE);

        if (_lowestAddress == IntPtr.Zero)
        {
            throw new OutOfMemoryException("Failed to reserve memory");
        }

        _highestAddress = _lowestAddress + (nint)size;
        _nextFreeAddress = _lowestAddress;
    }

    public nint LowestAddress => _lowestAddress;

    public nint HighestAddress => _highestAddress;

    public bool IsInRange(nint ptr) => ptr >= LowestAddress && ptr < HighestAddress;

    public nint Allocate(nint size)
    {
        if (size <= 0)
        {
            return IntPtr.Zero;
        }

        var alignedSize = (size + (PageSize - 1)) & ~(nint)(PageSize - 1);
        nint address;

        while (true)
        {
            address = Volatile.Read(ref _nextFreeAddress);
            var end = address + alignedSize;

            if (end > HighestAddress)
            {
                throw new OutOfMemoryException("Not enough memory to allocate");
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

    public void Free(nint address, nint size)
    {
        if (address == IntPtr.Zero || _lowestAddress == IntPtr.Zero)
        {
            return;
        }

        if (address < _lowestAddress || address >= _highestAddress)
        {
            throw new InvalidOperationException($"Address {address:x2} is out of reserved range");
        }

        var alignedSize = (size + (PageSize - 1)) & ~(nint)(PageSize - 1);

        if (!VirtualFree(address, (UIntPtr)alignedSize, MEM_DECOMMIT))
        {
            throw new InvalidOperationException("VirtualFree failed to decommit memory");
        }
    }

    public void Dispose()
    {
        if (_lowestAddress != IntPtr.Zero)
        {
            VirtualFree(_lowestAddress, UIntPtr.Zero, MEM_RELEASE);
            _lowestAddress = IntPtr.Zero;
            _highestAddress = IntPtr.Zero;
        }
    }
}
