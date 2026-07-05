using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SnapshotBench;

// Measures the OS primitives underpinning the single-pause snapshot collector design.
// See ../DESIGN.md. Subcommands: pss | veh | all (default: all)

internal static unsafe class Program
{
    const nuint PageSize = 4096;

    static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0] : "all";

        Console.WriteLine($"SnapshotBench on {RuntimeInformation.OSDescription}");
        Console.WriteLine($"CPU: {Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER")}, {Environment.ProcessorCount} logical cores");
        var mem = new MEMORYSTATUSEX { Length = (uint)sizeof(MEMORYSTATUSEX) };
        Native.GlobalMemoryStatusEx(ref mem);
        Console.WriteLine($"RAM: {mem.TotalPhys / (1024.0 * 1024 * 1024):F1} GB total, {mem.AvailPhys / (1024.0 * 1024 * 1024):F1} GB available");
        Console.WriteLine($"Timer resolution: {1e9 / Stopwatch.Frequency:F1} ns/tick");
        Console.WriteLine();

        if (sizeof(PSS_THREAD_ENTRY) != 120)
        {
            Console.WriteLine($"WARNING: PSS_THREAD_ENTRY size is {sizeof(PSS_THREAD_ENTRY)}, expected 120 — thread walk may fail");
        }

        // Cap the largest test region to half of available physical memory.
        long maxGb = Math.Min(16, (long)(mem.AvailPhys / (1024.0 * 1024 * 1024) / 2));

        switch (mode)
        {
            case "pss":
                RunPssBench(maxGb);
                break;
            case "pssflags":
                RunPssFlagsBench();
                break;
            case "veh":
                RunVehBench(maxGb);
                break;
            case "all":
                RunPssBench(maxGb);
                RunPssFlagsBench();
                RunVehBench(maxGb);
                break;
            default:
                Console.WriteLine("usage: SnapshotBench [pss|pssflags|veh|all]");
                return 1;
        }

        return 0;
    }

    // ================================================================
    // PSS benchmark
    // ================================================================

    static void RunPssBench(long maxGb)
    {
        Console.WriteLine("==== PSS VA-clone benchmark ====");

        var sizesGb = new List<long>();
        for (long gb = 1; gb <= maxGb; gb *= 2)
        {
            sizesGb.Add(gb);
        }

        using var heartbeat = new Heartbeat();
        var counter = new CounterThread();

        foreach (var gb in sizesGb)
        {
            nuint size = (nuint)gb << 30;
            byte* region = AllocTouched(size, out double touchMs);
            Console.WriteLine($"\n--- Region: {gb} GB committed+touched (touch took {touchMs:F0} ms) ---");

            counter.MoveSlotInto(region);

            // Plant a recognizable pattern to verify snapshot semantics.
            long patternOffset = (long)size / 2;
            *(ulong*)(region + patternOffset) = 0xDEADBEEF_CAFEF00D;

            for (int iter = 0; iter < 3; iter++)
            {
                RunOnePssCapture(region, size, patternOffset, heartbeat, counter,
                    measureExtras: iter == 0 && gb == sizesGb[^1]);
            }

            counter.MoveSlotOut();
            Native.VirtualFree(region, 0, Native.MEM_RELEASE);
        }

        // How does cost scale with *committed but untouched* memory?
        {
            nuint size = (nuint)Math.Min(8, maxGb) << 30;
            byte* region = (byte*)Native.VirtualAlloc(null, size, Native.MEM_RESERVE | Native.MEM_COMMIT, Native.PAGE_READWRITE);
            if (region != null)
            {
                Console.WriteLine($"\n--- Region: {(long)size >> 30} GB committed, UNTOUCHED ---");
                *(ulong*)region = 1; // touch a single page so the pattern check has something to chew on
                for (int iter = 0; iter < 2; iter++)
                {
                    RunOnePssCapture(region, size, 0, heartbeat, counter, measureExtras: false);
                }
                Native.VirtualFree(region, 0, Native.MEM_RELEASE);
            }
        }

        // Pure reservation cost.
        {
            nuint size = (nuint)32 << 30;
            byte* region = (byte*)Native.VirtualAlloc(null, size, Native.MEM_RESERVE, Native.PAGE_READWRITE);
            if (region != null)
            {
                Console.WriteLine($"\n--- Region: 32 GB RESERVED only ---");
                RunOnePssCapture(null, 0, 0, heartbeat, counter, measureExtras: false);
                Native.VirtualFree(region, 0, Native.MEM_RELEASE);
            }
        }

        counter.Stop();
    }

    static void RunOnePssCapture(byte* region, nuint size, long patternOffset, Heartbeat heartbeat, CounterThread counter, bool measureExtras)
    {
        heartbeat.Reset();
        Thread.Sleep(50); // let heartbeats settle

        long tCaptureStart = Stopwatch.GetTimestamp();
        int err = Native.PssCaptureSnapshot(
            Native.GetCurrentProcess(),
            Native.PSS_CAPTURE_VA_CLONE | Native.PSS_CAPTURE_THREADS | Native.PSS_CAPTURE_THREAD_CONTEXT | Native.PSS_CREATE_MEASURE_PERFORMANCE,
            Native.CONTEXT_CONTROL_INTEGER,
            out IntPtr snapshot);
        long tCaptureEnd = Stopwatch.GetTimestamp();

        if (err != 0)
        {
            Console.WriteLine($"  PssCaptureSnapshot FAILED: win32 error {err}");
            return;
        }

        double captureMs = TicksToMs(tCaptureEnd - tCaptureStart);

        // Parent-side memory value right after capture, for the atomicity analysis.
        long counterAfterCapture = counter.ReadParentValue();

        Thread.Sleep(30); // let heartbeats record the post-capture era
        var (maxGapMs, gaps) = heartbeat.Collect(tCaptureStart, tCaptureEnd);

        // Internal performance counters.
        var perf = default(PSS_PERFORMANCE_COUNTERS);
        double vaCloneMs = -1, totalMs = -1;
        if (Native.PssQuerySnapshot(snapshot, Native.PSS_QUERY_PERFORMANCE_COUNTERS, &perf, (uint)sizeof(PSS_PERFORMANCE_COUNTERS)) == 0)
        {
            vaCloneMs = perf.VaClonePeriod / 10000.0;   // FILETIME 100ns units
            totalMs = perf.TotalWallClockPeriod / 10000.0;
        }

        // Clone process handle.
        IntPtr cloneHandle = IntPtr.Zero;
        {
            PSS_VA_CLONE_INFORMATION info;
            if (Native.PssQuerySnapshot(snapshot, Native.PSS_QUERY_VA_CLONE_INFORMATION, &info, (uint)sizeof(PSS_VA_CLONE_INFORMATION)) == 0)
            {
                cloneHandle = info.VaCloneHandle;
            }
        }

        Console.WriteLine($"  capture: api={captureMs:F1} ms, internal: vaClone={vaCloneMs:F1} ms, total={totalMs:F1} ms");
        Console.WriteLine($"  mutator freeze (heartbeat max gap during capture): {maxGapMs:F2} ms" +
                          (gaps.Count > 0 ? $" [gaps >0.2ms: {string.Join(", ", gaps.Select(g => $"{g:F1}ms"))}]" : ""));

        if (cloneHandle == IntPtr.Zero)
        {
            Console.WriteLine("  no VA clone handle!");
        }
        else
        {
            // Snapshot semantics check: modify parent, clone must still see the old value.
            if (region != null)
            {
                ulong original = *(ulong*)(region + patternOffset);
                *(ulong*)(region + patternOffset) = 0x1111111111111111;
                ulong fromClone = 0;
                Native.ReadProcessMemory(cloneHandle, region + patternOffset, &fromClone, 8, out _);
                Console.WriteLine($"  snapshot isolation: clone sees {(fromClone == original ? "PRE-image (correct)" : $"WRONG value 0x{fromClone:X}")}");
                *(ulong*)(region + patternOffset) = original;
            }

            // Thread walk: are contexts there, and are they atomic with the VA clone?
            AnalyzeThreads(snapshot, cloneHandle, counter, counterAfterCapture);

            if (measureExtras && region != null)
            {
                MeasureRpm(cloneHandle, region, size);
                MeasureCowBreaks(region, size);
            }
        }

        // Teardown (watch for mutator stalls here too).
        heartbeat.Reset();
        long tFreeStart = Stopwatch.GetTimestamp();
        Native.PssFreeSnapshot(Native.GetCurrentProcess(), snapshot);
        long tFreeEnd = Stopwatch.GetTimestamp();
        Thread.Sleep(30);
        var (freeGapMs, _) = heartbeat.Collect(tFreeStart, tFreeEnd);
        Console.WriteLine($"  teardown: {TicksToMs(tFreeEnd - tFreeStart):F1} ms (mutator max gap {freeGapMs:F2} ms)");
    }

    static void AnalyzeThreads(IntPtr snapshot, IntPtr cloneHandle, CounterThread counter, long counterAfterCapture)
    {
        if (Native.PssWalkMarkerCreate(IntPtr.Zero, out IntPtr marker) != 0)
        {
            Console.WriteLine("  PssWalkMarkerCreate failed");
            return;
        }

        int threads = 0, withContext = 0;
        long bestDelta = long.MaxValue;
        ulong bestReg = 0;
        long cloneCounterValue = 0;

        // The counter thread's value in the *cloned memory* = value at freeze instant.
        Native.ReadProcessMemory(cloneHandle, counter.Slot, &cloneCounterValue, 8, out _);

        var entry = default(PSS_THREAD_ENTRY);
        while (Native.PssWalkSnapshot(snapshot, Native.PSS_WALK_THREADS, marker, &entry, (uint)sizeof(PSS_THREAD_ENTRY)) == 0)
        {
            threads++;
            if (entry.ContextRecord != null && entry.SizeOfContextRecord > 0)
            {
                withContext++;
                if (entry.ThreadId == counter.ThreadId)
                {
                    // Scan GPRs (CONTEXT offsets 0x78..0xF0: Rax..R15) for the loop counter.
                    for (int off = 0x78; off <= 0xF0; off += 8)
                    {
                        ulong reg = *(ulong*)(entry.ContextRecord + off);
                        long delta = Math.Abs((long)reg - cloneCounterValue);
                        if (delta < bestDelta)
                        {
                            bestDelta = delta;
                            bestReg = reg;
                        }
                    }
                }
            }
        }

        Native.PssWalkMarkerFree(marker);

        Console.WriteLine($"  threads: {threads} captured, {withContext} with context record");
        if (bestDelta != long.MaxValue)
        {
            // cloneCounterValue = counter at memory-freeze instant; bestReg = counter at
            // context-capture instant. Delta in iterations ≈ time skew × iteration rate.
            double rate = counter.IterationsPerSecond;
            string verdict = bestDelta < (long)(rate * 0.001) ? "ATOMIC (<1ms skew)"
                           : bestDelta < (long)(rate * 0.050) ? $"~{bestDelta / rate * 1000:F1} ms skew"
                           : $"NOT atomic ({bestDelta / rate * 1000:F0} ms skew)";
            Console.WriteLine($"  context atomicity: mem@freeze={cloneCounterValue}, closest GPR={bestReg}, " +
                              $"delta={bestDelta} iters → {verdict} (rate {rate / 1e6:F0}M/s, mem@api-return={counterAfterCapture})");
        }
        else
        {
            Console.WriteLine($"  context atomicity: counter thread context not found or counter not register-resident (inconclusive)");
        }
    }

    static void MeasureRpm(IntPtr cloneHandle, byte* region, nuint size)
    {
        Console.WriteLine("  -- ReadProcessMemory from clone --");
        nuint total = Math.Min(size, (nuint)1 << 30); // read up to 1 GB
        byte* buffer = (byte*)NativeMemory.Alloc(1 << 20);

        foreach (nuint chunk in new nuint[] { 4096, 65536, 1 << 20 })
        {
            long t0 = Stopwatch.GetTimestamp();
            nuint done = 0;
            while (done < total)
            {
                Native.ReadProcessMemory(cloneHandle, region + done, buffer, chunk, out nuint read);
                done += chunk;
            }
            double ms = MsSince(t0);
            Console.WriteLine($"     sequential {chunk / 1024,5} KB chunks: {total / (1024.0 * 1024 * 1024) / (ms / 1000):F2} GB/s");
        }

        // Random 4KB reads — the marking access pattern.
        var rng = new Random(42);
        int reads = 100_000;
        long tr = Stopwatch.GetTimestamp();
        for (int i = 0; i < reads; i++)
        {
            nuint offset = (nuint)rng.NextInt64((long)(size / PageSize)) * PageSize;
            Native.ReadProcessMemory(cloneHandle, region + offset, buffer, PageSize, out _);
        }
        double rms = MsSince(tr);
        Console.WriteLine($"     random 4KB reads: {rms * 1000 / reads:F2} us/read, {reads * 4096 / (1024.0 * 1024 * 1024) / (rms / 1000):F2} GB/s");

        NativeMemory.Free(buffer);
    }

    static void MeasureCowBreaks(byte* region, nuint size)
    {
        Console.WriteLine("  -- kernel COW break cost (writes to cloned pages) --");
        int pages = (int)Math.Min((nuint)100_000, size / PageSize / 4);

        // First write per page while the clone is alive → COW break.
        long t0 = Stopwatch.GetTimestamp();
        for (int i = 0; i < pages; i++)
        {
            region[(nuint)i * PageSize] = 0x42;
        }
        double firstMs = MsSince(t0);

        // Second write to the same pages → already broken, baseline.
        long t1 = Stopwatch.GetTimestamp();
        for (int i = 0; i < pages; i++)
        {
            region[(nuint)i * PageSize] = 0x43;
        }
        double secondMs = MsSince(t1);

        Console.WriteLine($"     first-write (COW break): {firstMs * 1000 / pages:F2} us/page over {pages} pages ({pages * 4096 / (1024.0 * 1024) / (firstMs / 1000):F0} MB/s of dirtied pages)");
        Console.WriteLine($"     second-write (baseline): {secondMs * 1000 / pages:F3} us/page");
    }

    // Which part of the capture causes the freeze? Compare capture flag combinations.
    static void RunPssFlagsBench()
    {
        Console.WriteLine("\n==== PSS capture-flags comparison (4 GB touched) ====");
        nuint size = (nuint)4 << 30;
        byte* region = AllocTouched(size, out _);
        using var heartbeat = new Heartbeat();

        (string name, uint flags)[] flavors =
        [
            ("VA_CLONE only                ", Native.PSS_CAPTURE_VA_CLONE),
            ("VA_CLONE + THREADS           ", Native.PSS_CAPTURE_VA_CLONE | Native.PSS_CAPTURE_THREADS),
            ("VA_CLONE + THREADS + CONTEXT ", Native.PSS_CAPTURE_VA_CLONE | Native.PSS_CAPTURE_THREADS | Native.PSS_CAPTURE_THREAD_CONTEXT),
        ];

        foreach (var (name, flags) in flavors)
        {
            for (int iter = 0; iter < 2; iter++)
            {
                heartbeat.Reset();
                Thread.Sleep(50);
                long t0 = Stopwatch.GetTimestamp();
                int err = Native.PssCaptureSnapshot(Native.GetCurrentProcess(), flags, Native.CONTEXT_CONTROL_INTEGER, out IntPtr snapshot);
                long t1 = Stopwatch.GetTimestamp();
                if (err != 0)
                {
                    Console.WriteLine($"  {name}: FAILED err={err}");
                    continue;
                }
                Thread.Sleep(30);
                var (gapMs, _) = heartbeat.Collect(t0, t1);
                Native.PssFreeSnapshot(Native.GetCurrentProcess(), snapshot);
                Console.WriteLine($"  {name}: api={TicksToMs(t1 - t0),7:F1} ms, mutator freeze={gapMs,7:F1} ms");
            }
        }

        Native.VirtualFree(region, 0, Native.MEM_RELEASE);
    }

    // ================================================================
    // VEH + VirtualProtect benchmark
    // ================================================================

    static byte* s_vehRegionStart;
    static byte* s_vehRegionEnd;
    static byte* s_preimageBuffer;
    static long s_preimageIndex;
    static int s_copyPreimage;
    static long s_faultCount;

    [UnmanagedCallersOnly]
    static int VehHandler(EXCEPTION_POINTERS* info)
    {
        var record = info->ExceptionRecord;
        if (record->ExceptionCode != 0xC0000005)
        {
            return 0; // EXCEPTION_CONTINUE_SEARCH
        }

        byte* address = (byte*)record->ExceptionInformation[1];
        if (address < s_vehRegionStart || address >= s_vehRegionEnd)
        {
            return 0;
        }

        byte* page = (byte*)((nuint)address & ~(PageSize - 1));

        if (s_copyPreimage != 0)
        {
            long index = Interlocked.Increment(ref s_preimageIndex) - 1;
            Unsafe.CopyBlock(s_preimageBuffer + (index << 12), page, 4096);
        }

        Native.VirtualProtect(page, PageSize, Native.PAGE_READWRITE, out _);
        Interlocked.Increment(ref s_faultCount);
        return -1; // EXCEPTION_CONTINUE_EXECUTION
    }

    static void RunVehBench(long maxGb)
    {
        Console.WriteLine("\n==== VEH + VirtualProtect benchmark ====");

        // 1) VirtualProtect over large ranges (the pause contribution of design A).
        Console.WriteLine("\n--- VirtualProtect whole-range cost (committed+touched) ---");
        for (long gb = 1; gb <= Math.Min(8, maxGb); gb *= 2)
        {
            nuint size = (nuint)gb << 30;
            byte* region = AllocTouched(size, out _);

            long t0 = Stopwatch.GetTimestamp();
            Native.VirtualProtect(region, size, Native.PAGE_READONLY, out _);
            double protectMs = MsSince(t0);

            long t1 = Stopwatch.GetTimestamp();
            Native.VirtualProtect(region, size, Native.PAGE_READWRITE, out _);
            double unprotectMs = MsSince(t1);

            Console.WriteLine($"  {gb} GB: protect {protectMs:F2} ms, unprotect {unprotectMs:F2} ms");
            Native.VirtualFree(region, 0, Native.MEM_RELEASE);
        }

        // 2) Baseline: single-page VirtualProtect syscall cost.
        {
            byte* page = (byte*)Native.VirtualAlloc(null, PageSize, Native.MEM_RESERVE | Native.MEM_COMMIT, Native.PAGE_READWRITE);
            *page = 1;
            int n = 20_000;
            long t0 = Stopwatch.GetTimestamp();
            for (int i = 0; i < n; i++)
            {
                Native.VirtualProtect(page, PageSize, (i & 1) == 0 ? Native.PAGE_READONLY : Native.PAGE_READWRITE, out _);
            }
            Console.WriteLine($"\n  single-page VirtualProtect: {MsSince(t0) * 1000 / n:F2} us/call");
            Native.VirtualFree(page, 0, Native.MEM_RELEASE);
        }

        // 3) VEH write-fault cost, with and without 4KB pre-image copy.
        nuint vehSize = (nuint)256 << 20; // 256 MB = 65536 pages
        byte* vehRegion = AllocTouched(vehSize, out _);
        s_vehRegionStart = vehRegion;
        s_vehRegionEnd = vehRegion + vehSize;
        s_preimageBuffer = (byte*)NativeMemory.Alloc(vehSize);

        IntPtr handler = Native.AddVectoredExceptionHandler(1, (IntPtr)(delegate* unmanaged<EXCEPTION_POINTERS*, int>)&VehHandler);
        if (handler == IntPtr.Zero)
        {
            Console.WriteLine("  AddVectoredExceptionHandler FAILED");
            return;
        }

        int vehPages = (int)(vehSize / PageSize);

        // The faulting writes go through a P/Invoked native memset: a CoreCLR thread in
        // cooperative mode cannot enter an UnmanagedCallersOnly VEH handler ("Invalid
        // Program" fatal error). The real GC doesn't have this problem — its handler
        // lives in the NativeAOT GC DLL, which attaches foreign threads on entry, and
        // mutator faults happen regardless of the CLR's notion of thread mode. The
        // P/Invoke adds ~10-20ns per write, same for the baseline below.
        foreach (bool copy in new[] { false, true })
        {
            s_copyPreimage = copy ? 1 : 0;
            s_preimageIndex = 0;
            s_faultCount = 0;

            Native.VirtualProtect(vehRegion, vehSize, Native.PAGE_READONLY, out _);

            long t0 = Stopwatch.GetTimestamp();
            for (int i = 0; i < vehPages; i++)
            {
                Native.Memset(vehRegion + (nuint)i * PageSize, 0x42, 1);
            }
            double ms = MsSince(t0);

            Console.WriteLine($"\n  VEH fault storm ({(copy ? "with" : "no")} 4KB pre-image copy): " +
                              $"{ms * 1000 / vehPages:F2} us/page over {vehPages} pages, {s_faultCount} faults" +
                              $" ({vehPages * 4096 / (1024.0 * 1024) / (ms / 1000):F0} MB/s of dirtied pages)");
        }

        // 4) Baseline: same write loop through the same native call, nothing protected.
        {
            long t0 = Stopwatch.GetTimestamp();
            for (int i = 0; i < vehPages; i++)
            {
                Native.Memset(vehRegion + (nuint)i * PageSize, 0x44, 1);
            }
            Console.WriteLine($"  unprotected write baseline: {MsSince(t0) * 1000 / vehPages:F3} us/page");
        }

        // 5) Can the whole-range protect be parallelized to shrink the pause?
        {
            Native.VirtualProtect(vehRegion, vehSize, Native.PAGE_READWRITE, out _);
            nuint bigSize = (nuint)Math.Min(8, maxGb) << 30;
            byte* big = AllocTouched(bigSize, out _);
            foreach (int workers in new[] { 1, 4, 8, 16 })
            {
                long t0 = Stopwatch.GetTimestamp();
                nuint chunk = bigSize / (nuint)workers;
                Parallel.For(0, workers, w =>
                {
                    Native.VirtualProtect(big + (nuint)w * chunk, chunk, Native.PAGE_READONLY, out _);
                });
                double protectMs = MsSince(t0);
                Native.VirtualProtect(big, bigSize, Native.PAGE_READWRITE, out _);
                Console.WriteLine($"  parallel protect {(long)bigSize >> 30} GB with {workers,2} threads: {protectMs:F1} ms");
            }
            Native.VirtualFree(big, 0, Native.MEM_RELEASE);
        }

        Native.RemoveVectoredExceptionHandler(handler);
        NativeMemory.Free(s_preimageBuffer);
        Native.VirtualFree(vehRegion, 0, Native.MEM_RELEASE);
    }

    // ================================================================
    // Helpers
    // ================================================================

    static byte* AllocTouched(nuint size, out double touchMs)
    {
        byte* region = (byte*)Native.VirtualAlloc(null, size, Native.MEM_RESERVE | Native.MEM_COMMIT, Native.PAGE_READWRITE);
        if (region == null)
        {
            throw new OutOfMemoryException($"VirtualAlloc({size >> 30} GB) failed, error {Marshal.GetLastWin32Error()}");
        }

        long t0 = Stopwatch.GetTimestamp();
        long pageCount = (long)(size / PageSize);
        Parallel.For(0, 8, worker =>
        {
            long from = pageCount * worker / 8, to = pageCount * (worker + 1) / 8;
            for (long i = from; i < to; i++)
            {
                *(ulong*)(region + (nuint)i * PageSize) = (ulong)i;
            }
        });
        touchMs = MsSince(t0);
        return region;
    }

    static double TicksToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
    static double MsSince(long t0) => TicksToMs(Stopwatch.GetTimestamp() - t0);

    // Threads that continuously timestamp, to measure actual mutator freezes.
    sealed class Heartbeat : IDisposable
    {
        const int MaxGaps = 256;
        readonly Thread[] _threads;
        volatile bool _stop;
        readonly object _lock = new();
        long _maxGapTicks;
        readonly List<(long start, long end)> _gaps = new();

        public Heartbeat()
        {
            _threads = new Thread[2];
            for (int i = 0; i < _threads.Length; i++)
            {
                _threads[i] = new Thread(Loop) { IsBackground = true, Priority = ThreadPriority.AboveNormal };
                _threads[i].Start();
            }
        }

        void Loop()
        {
            long threshold = Stopwatch.Frequency / 5000; // 0.2 ms
            long prev = Stopwatch.GetTimestamp();
            while (!_stop)
            {
                long now = Stopwatch.GetTimestamp();
                long gap = now - prev;
                if (gap > threshold)
                {
                    lock (_lock)
                    {
                        if (gap > _maxGapTicks) _maxGapTicks = gap;
                        if (_gaps.Count < MaxGaps) _gaps.Add((prev, now));
                    }
                }
                prev = now;
            }
        }

        public void Reset()
        {
            lock (_lock)
            {
                _maxGapTicks = 0;
                _gaps.Clear();
            }
        }

        /// Returns max gap overlapping [windowStart-slack, windowEnd+slack] in ms.
        public (double maxGapMs, List<double> gapsMs) Collect(long windowStart, long windowEnd)
        {
            lock (_lock)
            {
                long slack = Stopwatch.Frequency / 100; // 10 ms
                var relevant = _gaps.Where(g => g.end >= windowStart - slack && g.start <= windowEnd + slack)
                                    .Select(g => TicksToMs(g.end - g.start))
                                    .OrderDescending()
                                    .Take(8)
                                    .ToList();
                return (relevant.Count > 0 ? relevant[0] : 0, relevant);
            }
        }

        public void Dispose()
        {
            _stop = true;
        }
    }

    // A thread keeping a counter in a register AND in memory, to test whether
    // PSS thread contexts are captured atomically with the VA clone.
    sealed class CounterThread
    {
        long* _slot;
        readonly long* _parkingSlot;
        volatile int _generation;
        volatile bool _stop;
        public uint ThreadId { get; private set; }
        public double IterationsPerSecond { get; private set; }
        public long* Slot => _slot;

        public CounterThread()
        {
            _parkingSlot = (long*)NativeMemory.AllocZeroed(8);
            _slot = _parkingSlot;
            var thread = new Thread(Loop) { IsBackground = true };
            thread.Start();
            CalibrateRate();
        }

        void Loop()
        {
            ThreadId = Native.GetCurrentThreadId();
            long c = 0;
            while (!_stop)
            {
                long* slot = _slot;
                int generation = _generation;
                // Tight inner loop: c should be register-resident.
                while (_generation == generation)
                {
                    c++;
                    *slot = c;
                }
            }
        }

        void CalibrateRate()
        {
            Thread.Sleep(200);
            long v0 = Volatile.Read(ref *_slot);
            long t0 = Stopwatch.GetTimestamp();
            Thread.Sleep(500);
            long v1 = Volatile.Read(ref *_slot);
            IterationsPerSecond = (v1 - v0) / (TicksToMs(Stopwatch.GetTimestamp() - t0) / 1000);
        }

        public void MoveSlotInto(byte* region)
        {
            _slot = (long*)(region + 4096 * 17); // arbitrary page inside the region
            _generation++;
            Thread.Sleep(20);
        }

        public void MoveSlotOut()
        {
            _slot = _parkingSlot;
            _generation++;
            Thread.Sleep(20);
        }

        public long ReadParentValue() => Volatile.Read(ref *_slot);

        public void Stop()
        {
            _stop = true;
            _generation++;
        }
    }
}

// ================================================================
// Native interop
// ================================================================

internal static unsafe class Native
{
    public const uint MEM_COMMIT = 0x1000, MEM_RESERVE = 0x2000, MEM_RELEASE = 0x8000;
    public const uint PAGE_READONLY = 0x02, PAGE_READWRITE = 0x04;

    public const uint PSS_CAPTURE_VA_CLONE = 0x00000001;
    public const uint PSS_CAPTURE_THREADS = 0x00000080;
    public const uint PSS_CAPTURE_THREAD_CONTEXT = 0x00000100;
    public const uint PSS_CREATE_MEASURE_PERFORMANCE = 0x40000000;
    public const uint CONTEXT_CONTROL_INTEGER = 0x00100003; // CONTEXT_AMD64 | CONTROL | INTEGER

    public const int PSS_QUERY_VA_CLONE_INFORMATION = 1;
    public const int PSS_QUERY_PERFORMANCE_COUNTERS = 7;
    public const int PSS_WALK_THREADS = 3;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern void* VirtualAlloc(void* address, nuint size, uint allocationType, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool VirtualFree(void* address, nuint size, uint freeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool VirtualProtect(void* address, nuint size, uint newProtect, out uint oldProtect);

    [DllImport("kernel32.dll")]
    public static extern IntPtr AddVectoredExceptionHandler(uint first, IntPtr handler);

    [DllImport("kernel32.dll")]
    public static extern uint RemoveVectoredExceptionHandler(IntPtr handle);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadProcessMemory(IntPtr process, void* baseAddress, void* buffer, nuint size, out nuint bytesRead);

    [DllImport("kernel32.dll")]
    public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    [DllImport("kernel32.dll")]
    public static extern int PssCaptureSnapshot(IntPtr processHandle, uint captureFlags, uint threadContextFlags, out IntPtr snapshotHandle);

    [DllImport("kernel32.dll")]
    public static extern int PssFreeSnapshot(IntPtr processHandle, IntPtr snapshotHandle);

    [DllImport("kernel32.dll")]
    public static extern int PssQuerySnapshot(IntPtr snapshotHandle, int informationClass, void* buffer, uint bufferLength);

    [DllImport("kernel32.dll")]
    public static extern int PssWalkMarkerCreate(IntPtr allocator, out IntPtr walkMarkerHandle);

    [DllImport("kernel32.dll")]
    public static extern int PssWalkMarkerFree(IntPtr walkMarkerHandle);

    [DllImport("kernel32.dll")]
    public static extern int PssWalkSnapshot(IntPtr snapshotHandle, int informationClass, IntPtr walkMarkerHandle, void* buffer, uint bufferLength);

    [DllImport("msvcrt.dll", EntryPoint = "memset", CallingConvention = CallingConvention.Cdecl)]
    public static extern void* Memset(void* dest, int value, nuint count);
}

[StructLayout(LayoutKind.Sequential)]
internal struct MEMORYSTATUSEX
{
    public uint Length;
    public uint MemoryLoad;
    public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct EXCEPTION_RECORD
{
    public uint ExceptionCode;
    public uint ExceptionFlags;
    public EXCEPTION_RECORD* Next;
    public void* ExceptionAddress;
    public uint NumberParameters;
    public fixed ulong ExceptionInformation[15];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct EXCEPTION_POINTERS
{
    public EXCEPTION_RECORD* ExceptionRecord;
    public void* ContextRecord;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PSSFILETIME
{
    public uint Low, High;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct PSS_VA_CLONE_INFORMATION
{
    public IntPtr VaCloneHandle;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PSS_PERFORMANCE_COUNTERS
{
    public ulong TotalCycleCount, TotalWallClockPeriod;
    public ulong VaCloneCycleCount, VaClonePeriod;
    public ulong VaSpaceCycleCount, VaSpaceWallClockPeriod;
    public ulong AuxPagesCycleCount, AuxPagesWallClockPeriod;
    public ulong HandlesCycleCount, HandlesWallClockPeriod;
    public ulong ThreadsCycleCount, ThreadsWallClockPeriod;
}

// processsnapshot.h layout, 120 bytes on x64.
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct PSS_THREAD_ENTRY
{
    public uint ExitStatus;
    public void* TebBaseAddress;
    public uint ProcessId;
    public uint ThreadId;
    public nuint AffinityMask;
    public int Priority;
    public int BasePriority;
    public void* LastSyscallFirstArgument;
    public ushort LastSyscallNumber;
    public PSSFILETIME CreateTime;
    public PSSFILETIME ExitTime;
    public PSSFILETIME KernelTime;
    public PSSFILETIME UserTime;
    public void* Win32StartAddress;
    public PSSFILETIME CaptureTime;
    public uint Flags;
    public ushort SuspendCount;
    public ushort SizeOfContextRecord;
    public byte* ContextRecord;
}
