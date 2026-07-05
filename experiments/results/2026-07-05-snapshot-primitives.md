# Snapshot primitives benchmark — results

2026-07-05, Ryzen 9 7950X3D (32 logical), 95 GB RAM, Windows 11 26200, .NET 10.
Harness: [SnapshotBench](../SnapshotBench/). Raw numbers below are steady-state
(first iteration of each configuration runs a few % slower).

## 1. PSS VA-clone (`PssCaptureSnapshot`, design B)

### Capture freeze scales with *resident* pages: ~30 ms/GB

| Region | API call | actual mutator freeze (heartbeat) |
|---|---|---|
| 1 GB touched | 12–39 ms | 10–21 ms |
| 2 GB touched | 47–59 ms | 46–54 ms |
| 4 GB touched | 117–148 ms | 116–138 ms |
| 8 GB touched | 216–256 ms | 215–245 ms |
| 16 GB touched | 476–566 ms | 474–540 ms |
| 8 GB committed, untouched | 2.1–2.5 ms | 0.8–1.2 ms |
| 32 GB reserved only | 2.1 ms | 0.8 ms |

- The freeze is the address-space clone itself: capture-flags comparison at 4 GB shows
  `VA_CLONE` alone costs the same (~113 ms) as `VA_CLONE|THREADS|THREAD_CONTEXT`.
  Thread/context capture is free by comparison.
- Cost tracks resident (PTE-populated) pages, not committed VA — untouched commit and
  pure reservations are free.
- Internal perf counters (`VaClonePeriod`) report only ~10% of the wall time; the other
  90% is unattributed but definitely freezes mutators.
- Teardown (`PssFreeSnapshot`): 0 ms observed, no mutator stalls.

**Criteo datapoint resolved:** the raw primitive at 10–20 GB is ~0.3–0.6 s, not ~1 min —
ProcDump's dump-writing machinery owned the rest. But 0.6 s/cycle still disqualifies PSS
as the per-cycle snapshot mechanism for large heaps.

### Thread-context atomicity: CONFIRMED

A thread keeping a counter in a register and mirroring it to cloned memory: across every
capture, the closest GPR in the PSS-captured context was within ~1M iterations
(≈0.2 ms at 5.1 G iters/s) of the value frozen in the clone. **Registers are captured
atomically with the heap snapshot** — a bare `PssCaptureSnapshot` is a complete,
consistent (conservative) root+heap snapshot with no runtime cooperation.

### Snapshot semantics + reading the clone

- Isolation verified: parent writes after capture are invisible to the clone.
- `ReadProcessMemory` from clone: 1.7 GB/s @4 KB chunks, 9.8 GB/s @64 KB, 11.5 GB/s @1 MB;
  random 4 KB reads (≈ marking access pattern): **2.7 µs/read**.
- Kernel COW break (first parent write to a cloned page): **3.4 µs/page**;
  subsequent writes ~10 ns.

## 2. VEH + VirtualProtect (design A)

| Measurement | Result |
|---|---|
| VEH write-fault round-trip, handler unprotects page | **2.7 µs/page** |
| same + 4 KB pre-image copy | **3.6 µs/page** |
| unprotected write baseline (same P/Invoke path) | 0.014 µs |
| single-page `VirtualProtect` | 0.59 µs/call |
| whole-range protect (touched) | ~28 ms/GB (1 GB: 24, 2: 57, 4: 109, 8: 225 ms) |
| whole-range unprotect | ~17 ms/GB |
| parallel protect, 8 GB × 16 threads | 206 ms vs 243 ms single — kernel-serialized, parallelism useless |

Note: benchmark faults are raised from a P/Invoked native `memset` because a CoreCLR
thread in cooperative mode can't enter an `UnmanagedCallersOnly` VEH handler. Not an
issue for the real GC (handler lives in the NativeAOT GC DLL, which attaches foreign
threads — same mechanism as every GC callback).

## Analysis

1. **User-mode COW matches kernel COW.** VEH fault + 4 KB pre-image copy (3.6 µs) ≈
   kernel COW break (3.4 µs). The kernel has no magic; we lose nothing by doing COW
   ourselves, and we gain in-process snapshot reads (RPM random reads are ~30× slower
   than local memory access for pointer chasing).

2. **Naive design A has the same pause problem as PSS.** Protecting the whole heap
   inside the pause costs ~28 ms/GB — same order as the PSS clone freeze, and
   parallelism doesn't help (kernel VAD/PTE lock).

3. **The fix — steady-state protection (the design-shaping result):** keep the heap
   protected *permanently*. Faults unprotect pages one by one as the mutator writes
   them (that's the 3.6 µs tax, paid once per page per cycle — bounded by write rate).
   At the next cycle's pause, only the pages dirtied since the last snapshot need
   re-protecting: **the pause becomes O(per-cycle write set), not O(heap)**, at
   0.59 µs/page before range-coalescing. A cycle that dirtied 256 MB re-protects 65k
   pages ≈ 39 ms worst case, far less with coalesced ranges — and independent of
   whether the heap is 4 GB or 400 GB. There is no unprotect phase at all.
   (Early/concurrent re-protection before the pause is unsound: a pre-pause pre-image
   is older than the snapshot instant and can miss references stored in the gap.)

## Verdict

| | PSS clone (B) | VEH manual COW, steady-state (A) |
|---|---|---|
| pause | ~30 ms/GB resident — kills it at scale | O(write set): sub-ms to tens of ms |
| mutator tax | 3.4 µs/dirty page/cycle | 3.6 µs/dirty page/cycle (tie) |
| marker reads | RPM, 2.7 µs random | local memory (~30× faster) |
| root atomicity | free & confirmed (conservative) | needs SuspendEE + GcScanRoots (precise) |
| ops | process spawn/cycle, EDR noise | VEH handler correctness burden |

**Design A with steady-state protection wins for the collector.** PSS survives as a
diagnostics superpower (atomic conservative whole-process snapshot in one call — cheap
heap dumps, offline mark verification) and as an occasional consistency-check pass.

## Next steps

1. Side mark bitmap on the real GC (prerequisite for any concurrent design; also needed
   because marking must not write to protected/snapshot pages).
2. Prototype the VEH+pre-image snapshot in the GC DLL (NativeAOT VEH, allocation-free
   handler, pre-image buffer + page translation for the marker).
3. Measure a real cycle: SuspendEE + GcScanRoots + re-protect(dirty set) pause vs the
   current full-STW collection, on TestApp workloads.
4. Sequential-write mitigation in the handler (unprotect + pre-image K pages ahead on
   sequential fault patterns) if the 3.6 µs tax bites on array-heavy workloads.
