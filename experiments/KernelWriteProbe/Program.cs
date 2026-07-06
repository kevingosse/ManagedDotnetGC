// Does a kernel-mediated write into a PAGE_READONLY user page reach a user-mode
// vectored exception handler (where a COW-snapshot GC could unprotect and resume), or
// does the syscall just fail?
//
// This decides whether VEH+VirtualProtect page protection (the 2026-07-05 snapshot
// design, spec-m6 stages 1+) is sound for arbitrary .NET apps: managed byte[] buffers
// are passed to kernel-writing APIs constantly (sync FileStream reads pin with `fixed`
// and hand the heap page to ReadFile; sockets, registry, console do the same). If the
// kernel path fails instead of faulting into the VEH, every such syscall breaks while
// a page is protected, and no handler can save it.

using System.Runtime.InteropServices;

internal static unsafe partial class Program
{
    private const uint MEM_COMMIT = 0x1000;
    private const uint MEM_RESERVE = 0x2000;
    private const uint PAGE_READWRITE = 0x04;
    private const uint PAGE_READONLY = 0x02;
    private const uint EXCEPTION_CONTINUE_SEARCH = 0;

    private static int _vehWriteFaults;
    private static nint _page;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint VirtualAlloc(nint addr, nuint size, uint type, uint protect);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool VirtualProtect(nint addr, nuint size, uint newProtect, out uint oldProtect);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint AddVectoredExceptionHandler(uint first, delegate* unmanaged<ExceptionPointers*, uint> handler);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateFileW(string name, uint access, uint share, nint sec, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ReadFile(nint file, nint buffer, uint toRead, out uint read, nint overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint h);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetComputerNameW(nint buffer, ref uint size);

    // The control write must fault from NATIVE code: a CoreCLR thread executing managed
    // code cannot enter an UnmanagedCallersOnly VEH handler (same constraint the
    // 2026-07-05 SnapshotBench hit; the real GC's handler lives in a NativeAOT dll)
    [LibraryImport("kernel32.dll")]
    private static partial void RtlFillMemory(nint dest, nuint length, byte fill);

    [StructLayout(LayoutKind.Sequential)]
    private struct ExceptionPointers
    {
        public ExceptionRecord* Record;
        public nint Context;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExceptionRecord
    {
        public uint Code;
        public uint Flags;
        public nint Next;
        public nint Address;
        public uint ParameterCount;
        public nint Param0; // 1 = write fault
        public nint Param1; // faulting address
    }

    [UnmanagedCallersOnly]
    private static uint Handler(ExceptionPointers* info)
    {
        // Behave exactly like the GC's would: on a write fault to our page, unprotect
        // and resume the faulting instruction. If a kernel-mediated write gives the
        // handler this chance, the syscall completes and the design is sound; if the
        // kernel path fails the syscall without ever reaching user mode, it cannot.
        if (info->Record->Code == 0xC0000005 && info->Record->Param0 == 1
            && info->Record->Param1 >= _page && info->Record->Param1 < _page + 4096)
        {
            Interlocked.Increment(ref _vehWriteFaults);
            VirtualProtect(_page, 4096, PAGE_READWRITE, out _);
            return unchecked((uint)-1); // EXCEPTION_CONTINUE_EXECUTION
        }

        return EXCEPTION_CONTINUE_SEARCH;
    }

    private static void Main()
    {
        AddVectoredExceptionHandler(1, &Handler);

        _page = VirtualAlloc(0, 4096, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        var page = _page;
        *(byte*)page = 0x42; // resident + writable baseline

        var tempPath = Path.Combine(Path.GetTempPath(), "kernel-write-probe.bin");
        File.WriteAllBytes(tempPath, new byte[4096]);

        // ---- Control: user-mode write must round-trip through the VEH fix-up ----
        VirtualProtect(page, 4096, PAGE_READONLY, out _);
        RtlFillMemory(page, 1, 1);
        Console.WriteLine($"control: native user-mode write to protected page, VEH fixed it up (vehWriteFaults={_vehWriteFaults}, expect 1)");

        // ---- Test 1: sync ReadFile into the protected page ----
        VirtualProtect(page, 4096, PAGE_READONLY, out _);
        var before = _vehWriteFaults;
        var file = CreateFileW(tempPath, 0x80000000 /* GENERIC_READ */, 1, 0, 3 /* OPEN_EXISTING */, 0, 0);
        var ok = ReadFile(file, page, 4096, out var read, 0);
        var err = Marshal.GetLastWin32Error();
        CloseHandle(file);
        Console.WriteLine($"sync ReadFile into protected page: ok={ok} read={read} lastError={err} vehDelta={_vehWriteFaults - before}");

        // ---- Test 2: another kernel writer (GetComputerNameW) ----
        VirtualProtect(page, 4096, PAGE_READONLY, out _);
        before = _vehWriteFaults;
        var size = 1024u;
        ok = GetComputerNameW(page, ref size);
        err = Marshal.GetLastWin32Error();
        Console.WriteLine($"GetComputerNameW into protected page: ok={ok} lastError={err} vehDelta={_vehWriteFaults - before}");

        // ---- Test 3: sanity — same call succeeds on a writable page ----
        VirtualProtect(page, 4096, PAGE_READWRITE, out _);
        file = CreateFileW(tempPath, 0x80000000, 1, 0, 3, 0, 0);
        ok = ReadFile(file, page, 4096, out read, 0);
        CloseHandle(file);
        Console.WriteLine($"sync ReadFile after unprotect: ok={ok} read={read}");

        File.Delete(tempPath);
    }
}
