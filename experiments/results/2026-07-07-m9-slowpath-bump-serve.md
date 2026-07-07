# M9: slow-path bump-serve — the 14× suspension gap was a 20× carve inflation

**Date:** 2026-07-07 (fourteenth session) · **Branch:** claude/experiments
**Mission input:** last session's ETW diff — custom 16.4 STW/s vs stock-svr-h8 1.2/s
at equal mean pause; flat +20%/req tax on app code attributed to parking 24 loaded
threads 16×/s. Task: fewer young collections at bounded WS, without overfitting.

## The finding: windows were discarded 96% empty

Ground truth first. `EtlCpu --gcstats` grew an `AllocationTick` summer; the kept
stock ETL says the app truly allocates **552 MB/s (~6.4 KB/req)**. Our per-collection
stats (new `cold_n/fitdisc_n/disc_mb/req_mb` forensics columns) said we were carving
and zeroing **~11 GB/s** — 4,530 window handouts (~566 MB) per 45 ms GC interval,
of which **97% were "fitting discards"**: the thread's current window still had room
for the requested size (requests averaged ~70 bytes; 468 MB of good remainders
plugged per interval).

Mechanism: some allocations always enter the GC's slow path even when the EE's
inline bump has room — finalizable objects foremost (their JIT helper never
inline-bumps). Fortunes hits ~1.45 of these per request. Stock's slow path serves
them by bumping the same alloc context; our `AllocFromWindow` unconditionally
plugged the context and handed a fresh ~113 KB window. One 70-byte finalizable
object = one forfeited window. The 512 MB budget lasted 45 ms → 22 STW/s.

This also re-explains M8.3's "zeroer re-zeroed ~4× the app's allocation volume":
the excess was never zeroer over-eagerness alone — the carve volume itself was
inflated ~20× over true allocation.

## Fix 1 (the rock): serve the slow path from the context remainder

`AllocWithRetry` now bump-serves `size ≤ BumpMaxSize` from the live context exactly
like the EE fast path, carving only on genuine exhaustion. Effect on fortunes,
same sitting, stats on: handouts 4,530 → ~560 per interval, fitting discards → 0,
zeroed volume 510 → 58 MB/interval, total GCs 1689 → 1075, peak WS 1310 → 463 MB,
RPS 68.9k → 74.4k.

## Fix 2: the boost controller had two fixed points; honest accounting picked the bad one

With honest carve volume the adaptive nursery collapsed to the 64 MB floor:
survivors are a **constant** ~2.4 MB (bounded by in-flight requests, not by
interval), which reads as 3.7% of a floor budget (→ shrink) but 0.47% of a boosted
one (→ grow). A ratio-to-budget futility gate is self-referential — pre-fix it only
reached the good fixed point because inflation made every young GC look futile.

Replaced the ratio with physical gates:

- **grow**: frequent (<250 ms) AND cheap pause (<10 ms) AND **absolute survivor
  mass < 8 MB** (the mass whose marking fits inside a cheap pause) AND **base
  budget at floor** (`ComputeBudget(live) ≤ 2×MinGCBudget`) — the boost exists
  only for tiny-live/huge-alloc heaps; a live-sized budget already sets cadence.
- **shrink**: sparse (>1 s) OR survivors > 16 MB.

The floor-bound gate keeps the soak (live 268→641 MB, cheap 3 ms pauses) excluded
by construction, replacing the accidental protection the old ratio gave it.

## Fix 3: trim demand was double-charging the boost

Retention/starvation demand was `2×budget`; the doubling bought allocate-through
headroom for a concurrent cycle, sized when a cycle window consumed a whole budget
of carve. Honest windows consume ~12 MB. Demand is now `2×budget − boost`
(boost retains ×1). Unboosted workloads — every GCPerfSim shape, the soak — keep
the byte-identical historical demand, so the retention==starvation-threshold scar
stays intact. Starved fulls on fortunes: 103/run → 9/run.

## Results (all same-sitting, interleaved A/B/A)

| | M8.3 ref | M9 | stock-svr-h8 |
|---|---|---|---|
| fortunes RPS | 68.1k | **79.1k** | 84.3k |
| fortunes peak WS | 1273 MB | 974 MB | 728 MB |
| fortunes p99 | 11.6 ms | 10.2 ms | 9.0 ms |
| queries RPS | — | **7.88k** | 8.12k |
| queries peak WS | — | **511 MB** | 686 MB |
| young GCs (fortunes run) | 1625 | **131** (~1.7/s loaded) | (1.2/s, ETW) |

**fortunes 0.80× → 0.94×, queries 0.75× → 0.97×** (queries now at *lower* WS than
stock), updates 0.98× (cross-sitting). Suspension rate at stock scale; budget pegs
at 512 MB with survivors 2.7 MB constant.

Gates: suite 56/56; GcStress 30 s ×3 modes (0/50/5 ms) clean; GCPerfSim soh/mixed
same-sitting neutral (soh 1.302 vs ref 1.317, mixed 1.284 vs 1.294) with no WS
ratchet; soak 120 s = 6.10M reqs, 0 errors, WS lands on the known csweep-on
1.86 GB, boost never engaged.

## Open

- fortunes WS 974 vs stock 728: the remaining premium is demand-retention
  (576 MB for a 512 MB budget) + churn margin. A 384 MB boost cap or an
  honest-window concurrent allowance would shave ~100–200 MB; untested.
- fortunes RPS gap 0.94×: suspensions are now stock-rate, so the residual is
  elsewhere (alloc path, card scans, EF pins?) — profile before touching anything.
- The forensics columns stay (stats-off = free): `fitdisc_n` nonzero is the
  regression alarm for this entire class of bug.

## Files

- `ManagedDotnetGC/GCHeap.cs` — bump-serve in `AllocWithRetry`, handout forensics,
  mass/floor grow gates, boost-aware trim demand.
- `ManagedDotnetGC/GcStats.cs` — forensics counters + CSV columns.
- `experiments/tools/EtlCpu/Program.cs` — `--gcstats` AllocationTick summer.
