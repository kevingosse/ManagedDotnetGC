# M8.2: Partitioned stack scanning across GC workers (2026-07-07)

**Landed.** Young-pause root scanning now fans out across the M5 worker pool using
stock server GC's `ScanContext.thread_number` partitioning scheme. Same-sitting
results (fortunes, 256 conns): roots p50 **1550 → 469 µs (−70%)**, young pause p50
**~4.75 → ~3.56 ms (−25%)**, fortunes p99 −5–8%. RPS: queries **+2.7%**, fortunes
**+0.5%**, updates flat. GCPerfSim matrix and soak (6.45M req / 0 err) unchanged.

## Mechanism

The scoping question from the handoff ("can our foreign worker threads call
GcScanRoots?") answers YES, and the partitioning is entirely GC-side:

- `GCToEEInterface::GcScanRoots` (runtime `vm/gcenv.ee.cpp:295`) walks the FULL
  thread list on every call and asks the GC, per thread, whether it belongs to
  `sc->thread_number` via `IsThreadUsingAllocationContextHeap` — the predicate's
  only call site in the VM. We previously stubbed it `return true` (one serial call
  scans everything).
- Stock server GC heap threads are foreign to the EE too: `CreateNonSuspendableThread`
  makes a raw utility thread with **no EE Thread object** (just a `ThreadType_GC`
  thread_local, which only matters to debug asserts and stresslog sizing).
  `ScanStackRoots`' precondition explicitly admits scanners with
  `GetThreadNULLOk() == NULL` — exactly what our NativeAOT workers look like.
  This is the one sanctioned exception to M5's "workers never call the EE" rule.
- The collecting thread's own stack may be claimed by a worker: it is preemptive
  for the whole collection, so its managed frames sit frozen below the transition
  frame like any blocked mutator's (same situation as stock server GC's triggering
  mutator).
- Statics: `MarkShouldCompeteForStatics()` = `IsServerHeap() && procs >= 2`; we run
  as workstation so `EnumAllStaticGCRefs` never fires (statics are covered by our
  handle scan, as every full GC to date proves). If a user sets gcServer=1, each
  participant re-enumerates statics on full GCs — duplicate pushes dedupe at trace.

Implementation (`PartitionedScanRoots`): each participant calls `GcScanRoots` with
its own `ScanContext{thread_number=id}`; the promote callback routes roots to
`_cardScanStacks[sc->thread_number]` (single-writer per stack). Young marks trace
in the same fan-out via the share-queue drain protocol (extracted from
`ParallelDrainMark` as `DrainWithSharing`); buffered full marks and both concurrent
pauses just fill the stacks and let `ParallelDrainMark` trace as before.

## Balance: round-robin lost to first-come claiming

Round-robin dealing (per-participant cursor over the identical-order thread list)
only cut roots p50 1550 → ~580 µs: fortunes has ~68–83 EE threads but the cost
lives in the ~dozen deep MVC/EF request stacks (~3× per-thread cost skew), so
count-balance ≠ cost-balance. Switching the predicate to first-come CAS claiming
(each participant CASes the thread's list-position slot; the EE loop scans a
claimed thread before advancing, so a participant stuck on a fat stack stops
claiming) got roots p50 to 469 µs and the slowest participant's scan to ~385 µs —
roughly the fattest-single-stack floor plus change. Further cuts would need
intra-stack parallelism, which the EE contract doesn't offer.

## Why RPS moved less than the pause did

Young pause p50 −25% at ~45–50 GCs/s ≈ −5% of wall STW, but measured RPS only
+0.5–2.7%. Part of the gap is bench noise (see below); the rest says the mutator
side, not the pause side, now owns the web gap — consistent with the handoff's
lever #2 (window handout wait/carve + inline zeroing on the request path).

## Bench-integrity scars (both fixed/recorded)

1. **bench-techempower -GcDll rename trap (FIXED in script):** `Copy-Item $GcDll
   $appDir` kept the source filename while the app loads `ManagedDotnetGC.dll`, so
   a -GcDll not literally named ManagedDotnetGC.dll silently benched the stale dll
   in the app dir. Three "A/B" runs this sitting benched the same old dll (identical
   RPS, zero instrumentation — that's what exposed it). The script now copies to an
   explicit `ManagedDotnetGC.dll` destination.
2. **Same-sitting drift exists too:** queries baseline drifted 6.8k → 6.3k over
   ~40 min (docker/thermal state). A/B runs must be **interleaved** — the morning
   baseline vs afternoon candidate comparison inverted the sign of the result.
   GCPerfSim pinheavy showed the same effect (round medians +6.8% → +1.9% → −0.1%).

## Numbers (same-sitting, interleaved)

| endpoint | baseline (pss-v2-base) | partitioned (pss-v2-a/b) | delta |
|---|---|---|---|
| fortunes RPS | 68.2/69.5/69.7k | 69.4/69.6/69.7/69.8/70.3/70.3k | +0.5% |
| queries RPS | 6.20/6.29/6.41k | 6.42–6.55k | +2.7% |
| updates RPS | 961/970/974 | 948–976 | ~0 |
| fortunes p99 | 11.7–12.0 ms | 10.9–11.4 ms | −5–8% |

Phase split (instrumented, fortunes, young p50): roots 469 µs (was ~1550), slowest
participant scan 385 µs, fan-out total 468 µs, cards ~1714, sweep ~1447. Young
pause p50 ~3.56 ms. Cards is now the largest young-pause slice again.

GCPerfSim medians (baseline → partitioned): soh 1.209→1.206, lohmix 1.149→1.152,
pin 1.225→1.228, pinheavy 1.128→1.148 (13-iter medians, rounds trend to 0 — noise),
mixed 1.225→1.254 then 1.194→1.164 on re-run (noise). No regression.

Gates: unit 70/70, suite 56/56, soak 120 s = 6.45M req / 0 err / server alive.
