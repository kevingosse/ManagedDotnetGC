using System.Runtime.InteropServices;

namespace ManagedDotnetGC;

/// <summary>
/// Owns the heap reservation and wraps the OS memory primitives. Since SPEC-M6 §3 the
/// heap is double-mapped: every committed region is a 2 MB pagefile-backed section mapped
/// at its front address (the view the EE and mutators see, and the one M6 stages 1+
/// protect) and again at front + <see cref="AliasOffset"/> (the GC's always-writable
/// alias — every heap write made by GC code goes through it, so GC threads never take
/// write faults). Both 2 TB ranges are placeholder reservations (VirtualAlloc2); commit
/// maps a fresh section into both placeholders, decommit unmaps both views and closes
/// the section, which is what releases the commit charge (section pages cannot be
/// MEM_DECOMMITted through a view — the per-region sections exist so decommit works at
/// exactly the granularity the region pool recycles at).
///
/// The reservations are over-sized by one region so the usable range can be aligned to
/// region boundaries (placeholders only guarantee 64 KB alignment, region index math
/// needs 2 MB). All post-initialization operations are non-throwing (SPEC-M2 §9).
/// </summary>
internal partial class NativeAllocator : IDisposable
{
    private const uint MEM_COMMIT = 0x00001000;
    private const uint MEM_RESERVE = 0x00002000;
    private const uint MEM_RELEASE = 0x00008000;
    private const uint MEM_RESERVE_PLACEHOLDER = 0x00040000;
    private const uint MEM_REPLACE_PLACEHOLDER = 0x00004000;
    private const uint MEM_PRESERVE_PLACEHOLDER = 0x00000002;

    private const uint PAGE_NOACCESS = 0x01;
    private const uint PAGE_READWRITE = 0x04;
    private const uint SEC_COMMIT = 0x08000000;

    private static readonly IntPtr CurrentProcess = -1;
    private static readonly IntPtr InvalidHandleValue = -1;

    private nint _frontReservation;
    private nint _aliasReservation;
    private readonly nint _lowestAddress;
    private readonly nint _highestAddress;
    private readonly nint _aliasOffset;

    // Per-region mapping state, indexed by (front - lowest) >> Region.Shift. A region's
    // 2 MB placeholder is split out of the enclosing reservation once (first commit) and
    // then survives every unmap via MEM_PRESERVE_PLACEHOLDER.
    private readonly nint[] _sections;
    private readonly bool[] _splitFront;
    private readonly bool[] _splitAlias;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr VirtualAlloc(IntPtr lpAddress, UIntPtr dwSize, uint flAllocationType, uint flProtect);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool VirtualFree(IntPtr lpAddress, UIntPtr dwSize, uint dwFreeType);

    [LibraryImport("kernelbase.dll", SetLastError = true)]
    private static partial IntPtr VirtualAlloc2(IntPtr process, IntPtr baseAddress, UIntPtr size, uint allocationType, uint pageProtection, IntPtr extendedParameters, uint parameterCount);

    [LibraryImport("kernelbase.dll", SetLastError = true)]
    private static partial IntPtr MapViewOfFile3(IntPtr fileMapping, IntPtr process, IntPtr baseAddress, ulong offset, UIntPtr viewSize, uint allocationType, uint pageProtection, IntPtr extendedParameters, uint parameterCount);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnmapViewOfFileEx(IntPtr baseAddress, uint unmapFlags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr CreateFileMappingW(IntPtr hFile, IntPtr lpAttributes, uint flProtect, uint dwMaximumSizeHigh, uint dwMaximumSizeLow, IntPtr lpName);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    public NativeAllocator(long size)
    {
        _frontReservation = VirtualAlloc2(IntPtr.Zero, IntPtr.Zero, (UIntPtr)(size + Region.Size), MEM_RESERVE | MEM_RESERVE_PLACEHOLDER, PAGE_NOACCESS, IntPtr.Zero, 0);
        _aliasReservation = VirtualAlloc2(IntPtr.Zero, IntPtr.Zero, (UIntPtr)(size + Region.Size), MEM_RESERVE | MEM_RESERVE_PLACEHOLDER, PAGE_NOACCESS, IntPtr.Zero, 0);

        if (_frontReservation == 0 || _aliasReservation == 0)
        {
            // Initialization-time failure: the process cannot run without the heap
            throw new OutOfMemoryException("Failed to reserve memory");
        }

        _lowestAddress = (_frontReservation + Region.Size - 1) & ~(Region.Size - 1);
        _highestAddress = _lowestAddress + (nint)size;

        var aliasLowest = (_aliasReservation + Region.Size - 1) & ~(Region.Size - 1);
        _aliasOffset = aliasLowest - _lowestAddress;

        var regionCount = (int)(size >> Region.Shift);
        _sections = new nint[regionCount];
        _splitFront = new bool[regionCount];
        _splitAlias = new bool[regionCount];

        // Object-level GC writes (epoch stamps, the finalizer-run header bit) offset
        // through this static: one heap per process in production, and the tests that
        // exercise marking create their allocators sequentially
        GCObject.WriteAliasOffset = _aliasOffset;
    }

    public nint LowestAddress => _lowestAddress;

    public nint HighestAddress => _highestAddress;

    /// <summary>Delta from a front (mutator-visible) heap address to the GC's
    /// always-writable alias mapping of the same memory (SPEC-M6 §3).</summary>
    public nint AliasOffset => _aliasOffset;

    public bool IsInRange(nint ptr) => ptr >= LowestAddress && ptr < HighestAddress;

    /// <summary>
    /// Commits a region-aligned range by mapping one fresh (OS-zeroed) section per region
    /// into both views. Regions already mapped are skipped, so a caller retrying after a
    /// partial failure converges instead of leaking.
    /// </summary>
    public bool TryCommit(nint address, nint size)
    {
        var first = (int)((address - _lowestAddress) >> Region.Shift);
        var count = (int)(size >> Region.Shift);

        for (int i = first; i < first + count; i++)
        {
            if (_sections[i] == 0 && !CommitRegion(i))
            {
                return false;
            }
        }

        return true;
    }

    private bool CommitRegion(int index)
    {
        var front = _lowestAddress + ((nint)index << Region.Shift);
        var alias = front + _aliasOffset;

        if (!_splitFront[index])
        {
            if (!VirtualFree(front, (UIntPtr)Region.Size, MEM_RELEASE | MEM_PRESERVE_PLACEHOLDER))
            {
                return false;
            }

            _splitFront[index] = true;
        }

        if (!_splitAlias[index])
        {
            if (!VirtualFree(alias, (UIntPtr)Region.Size, MEM_RELEASE | MEM_PRESERVE_PLACEHOLDER))
            {
                return false;
            }

            _splitAlias[index] = true;
        }

        var section = CreateFileMappingW(InvalidHandleValue, IntPtr.Zero, PAGE_READWRITE | SEC_COMMIT, 0, (uint)Region.Size, IntPtr.Zero);

        if (section == 0)
        {
            return false;
        }

        if (MapViewOfFile3(section, CurrentProcess, front, 0, (UIntPtr)Region.Size, MEM_REPLACE_PLACEHOLDER, PAGE_READWRITE, IntPtr.Zero, 0) == 0)
        {
            CloseHandle(section);
            return false;
        }

        if (MapViewOfFile3(section, CurrentProcess, alias, 0, (UIntPtr)Region.Size, MEM_REPLACE_PLACEHOLDER, PAGE_READWRITE, IntPtr.Zero, 0) == 0)
        {
            UnmapViewOfFileEx(front, MEM_PRESERVE_PLACEHOLDER);
            CloseHandle(section);
            return false;
        }

        _sections[index] = section;
        return true;
    }

    /// <summary>
    /// Decommits a region-aligned range. Thread-safe for disjoint ranges (parallel sweep
    /// workers decommit different spans), like the VirtualFree it replaced.
    /// </summary>
    public bool Decommit(nint address, nint size)
    {
        var first = (int)((address - _lowestAddress) >> Region.Shift);
        var count = (int)(size >> Region.Shift);
        var ok = true;

        for (int i = first; i < first + count; i++)
        {
            var section = _sections[i];

            if (section == 0)
            {
                continue;
            }

            var front = _lowestAddress + ((nint)i << Region.Shift);

            ok &= UnmapViewOfFileEx(front, MEM_PRESERVE_PLACEHOLDER);
            ok &= UnmapViewOfFileEx(front + _aliasOffset, MEM_PRESERVE_PLACEHOLDER);
            ok &= CloseHandle(section);
            _sections[i] = 0;
        }

        return ok;
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

    public static void OsRelease(nint address)
    {
        VirtualFree(address, UIntPtr.Zero, MEM_RELEASE);
    }

    public void Dispose()
    {
        if (_frontReservation == 0)
        {
            return;
        }

        // Close every live section so the commit charge is released (this is what the
        // unit tests need). The placeholder fragments left by splits cannot be released
        // with a single VirtualFree; production never disposes, and for tests the
        // leftover reservations are inert address space.
        for (int i = 0; i < _sections.Length; i++)
        {
            if (_sections[i] != 0)
            {
                var front = _lowestAddress + ((nint)i << Region.Shift);
                UnmapViewOfFileEx(front, MEM_PRESERVE_PLACEHOLDER);
                UnmapViewOfFileEx(front + _aliasOffset, MEM_PRESERVE_PLACEHOLDER);
                CloseHandle(_sections[i]);
                _sections[i] = 0;
            }
        }

        VirtualFree(_frontReservation, UIntPtr.Zero, MEM_RELEASE);
        VirtualFree(_aliasReservation, UIntPtr.Zero, MEM_RELEASE);
        _frontReservation = 0;
        _aliasReservation = 0;
    }
}
