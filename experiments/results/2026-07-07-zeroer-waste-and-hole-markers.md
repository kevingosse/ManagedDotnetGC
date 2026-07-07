# The zeroer waste hunt: ETW mutator diff, per-hole zeroed markers (M8.3)

**Date**: 2026-07-07 (twelfth session, afternoon). **Branch**: claude/experiments.

## Why

M8.2 cut young pause p50 by 25% but RPS moved only +0.5–2.7%: the pause side is
nearly exhausted and the remaining fortunes/queries gap to tuned SVR-h8 (~0.80×)
lives on the mutator side. The handoff's next lever was an ETW CPU diff of the
request path, custom vs stock.

## Instruments

- `experiments/profile-techempower.ps1` — mirrors the bench's app/env setup, then
  collects a PerfView kernel CPU-sampling trace (elevated via gsudo) inside a
  bombardier load window, reporting the window's RPS so every trace is anchored.
  Two traps burned into the script as comments: gsudo's elevated host is itself a
  framework-dependent .NET app (a leaked `DOTNET_GCName` kills it with 0x8007007E
  before PerfView ever launches — scrub the session env right after the app
  inherits it), and PerfView.exe is a GUI-subsystem exe (gsudo returns immediately;
  poll the process, and keep the app alive through rundown+merge).
- `experiments/tools/EtlCpu` — TraceEvent-based aggregator: per-process CPU,
  module rollup, exclusive/inclusive symbols (CLR rundown resolves JIT frames,
  msdl+native-pdb for coreclr/our dll), per-thread rollup, `--match` symbol
  families, `--callers` chains.

## What the diff said (fortunes, same-sitting, 25 s windows)

| | custom | stock-svr-h8 |
|---|---|---|
| RPS under profiler | 67.1k | 78.5k |
| process CPU | 458.4 s | 428.8 s |
| **CPU µs/request** | **235.6** | **189.0 (+25% for us)** |

- Machine ~74% utilized — the CPU/req overhead is the throughput limiter, not cores.
- Stock's entire in-process GC cost: `SVR::gc_heap` 0.53% + `coreclr!memset` 0.75%
  (their inline alloc-quantum zeroing) ≈ **2.4 µs/req**. Ours: `manageddotnetgc`
  module 7.15% ≈ **16.8 µs/req**, of which `Zeroing.Clear` alone 2.31% (10.6 s vs
  stock's 3.2 s of memset — **~4× stock's zeroed volume** for identical app work).
- Write barrier a non-story: 0.77% us vs 0.98% stock (checked barrier + bulk moves).
- `--callers Zeroing__Clear`: **82% of zeroing CPU is the background GcRegionZeroer
  re-zeroing hole regions** (`TryZeroHoleRegion → ZeroCheckedOutHoleBodies`), ~9 s
  of a dedicated core streaming NT stores. Mutator-inline (`AllocFromWindow`) is 17%.
- Everything else — EF, Kestrel, encoders, dispatch helpers — costs a flat
  **+20–25% per request** vs stock (e.g. `system.text.encodings.web` 0.91 vs 0.68
  µs/req). A uniform IPC/memory-system tax on all mutator threads, not extra code.

Root cause of the zeroing volume, in code: `SweepBumpRegion` cleared
`HolesZeroedFlag` **unconditionally** on every swept region, so after every young
collection the zeroer re-zeroed every linked hole of every Reopened region
(~590 on this workload) — regardless of whether anything new died there. Starved
fulls (~1.5/s on web) did it to the whole reservoir.

## Knob A/B round 1: the blunt instruments (fortunes/queries medians, interleaved A/B/B/A)

| config | fortunes | queries |
|---|---|---|
| zbase (first run of sitting) | 69.2k | 6625 |
| `GCHoleZero=0` (no hole pre-zeroing) | 72.5k | 6319 |
| + `GCCarveTemporal=1` | 63.8k | 5137 (+errors) |
| zbase2 (drift check) | 72.3k | 6758 |

- **CarveTemporal: rejected decisively** (−8% fortunes, −22% queries). The M7
  census was right: temporal zeroing's RFO reads on the mutator's critical path
  cost more than NT's cache invalidation of imminent allocations. Knob stays as
  documentation.
- **HoleZero=0: fortunes neutral (the zbase→zbase2 rebound says round-1 zbase was
  a cold outlier), queries −5–7% real.** Pre-zeroed holes genuinely pay on the
  high-churn endpoint; killing the zeroer outright forfeits that. The waste, not
  the mechanism, is the bug.
- Notable non-result: removing the ~9 s/29 s background NT firehose did NOT lift
  fortunes — the flat +20% mutator tax is **not** primarily zeroer bandwidth.
  The remaining suspects (young-GC suspension frequency, concurrent mark/sweep
  cache streaming) are the next session's diagnostic.

## The fix: per-hole zeroed markers (default-on)

A marker word in the free plug's padding at `ref+12` (inside the 32-byte prefix
every consumer already preserves): set only by the zeroer after zeroing a body,
written 0 by every other plug writer (`ClosePlug`, carve remainder,
`SealActiveBumps` tails, `AllocateFreeObject`), and **inherited across sweeps only
when the rebuilt extent is byte-identical to the marked plug it re-covers** (same
free-MT position, same length — any new death changes the extent and fails the
match; the dynamic `_minLinkedHole` floors make "was linked" undecidable from
geometry, which is why the marker is stored, not inferred). `HolesZeroedFlag`
becomes the AND of the rebuilt holes' markers instead of constant-false, carves
decide `needsZero` per hole and propagate the marker to remainders, and
`ZeroCheckedOutHoleBodies` skips already-marked holes — a region with one new
death re-zeroes one hole.

Second-order lesson, caught by the soak A/B: the zeroer was only ever bounded by
*accident* — the hole pass ate every kick before the pool pass could run. Fixing
the hole waste un-bounded the pool pass, which then pre-zeroed the entire pool
every cycle (WS 2.38→2.84 GB climbing, −3% soak RPS: NT-touching pages the trim
wants cold). Pool pre-zeroing is now capped at a **32-region (64 MB) float at the
pool top** — pops consume the zeroed prefix, so the float refills exactly with
demand (`ZeroedPoolFloat`).

`DOTNET_GCHoleMarkerInherit=0` is the bisect-insurance knob (a falsely inherited
marker is a type-safety smear, same class as the card-lookback incident).

## Gates

- Test suite 56/56 green.
- ASP.NET soak 120 s: 6.05M requests, 0 errors, WS plateau ~2.45 GB — vs
  same-sitting knob-off baseline 6.17M / 2.38 GB (within run noise; the
  pre-float-cap build was the one that climbed).
- GcStress tight (30 s, 16k forced fulls) and young-heavy (45 s, interval 50):
  0 checksum failures.
- Web A/B/A (interleaved, same sitting): TBD below.

## Results

Web A/B/A, same sitting, same dll, knob-only (medians of 3):

| | fortunes | queries | |
|---|---|---|---|
| zmark (markers on) | 70.2k | 6552 | first label of the batch (cold-first pattern) |
| zmark0 (`HoleMarkerInherit=0`) | 71.9k | 6454 | |
| zmark2 (markers on) | 71.5k | 6275 | queries noise grew through the sitting (WS drift 1452→1750 MB across configs, one 691-error iter) |

**Neutral on web RPS within the sitting's noise** — the fix removes the waste
without giving back the queries win that killing the zeroer outright forfeited
(round 1's −5–7%). What it buys at equal RPS: the ~9 s-per-29 s background core
and its ~10 GB/s NT-store DRAM stream are gone from steady state. On this
machine the box ran ~74% utilized, so freed background CPU cannot become RPS;
on a saturated box (the real TechEmpower rig) it should.

GCPerfSim guard (vs same-day references): mixed 1.192 s vs 1.172 (+1.7%, inside
the ±4% iteration spread documented this morning); soh median 1.281 vs 1.206
with fully overlapping iteration ranges (1.165–1.289 vs 1.191–1.304) and one
WS-ratcheted iteration (the known bimodality). No regression signal; soh worth
re-reading at the next full backfill.

## The finding that survives: the +20% tax is NOT zeroer bandwidth

Round 1's cleanest negative result: removing the entire background NT firehose
(`GCHoleZero=0`) did not lift fortunes at ~74% machine utilization. The flat
+20–25% per-request cost on all app code has another mechanism. Next-session
diagnostics, in order:

1. **Young-GC suspension frequency — MEASURED, same session (`EtlCpu --gcstats`,
   SuspendEEStart→RestartEEStop pairs, EE-fired so present for both GCs):**

   | | custom | stock-svr-h8 |
   |---|---|---|
   | STW episodes | 474 (**16.4/s**) | 34 (**1.2/s**, all gen0/AllocSmall) |
   | total STW | 1137 ms (3.9% of wall) | 77 ms (0.3%) |
   | mean / max | 2.40 / 4.01 ms | 2.25 / 3.83 ms |

   Our pauses are as short as stock's — and we take **14× as many**. The direct
   STW share explains only ~3.6 points of the gap; the rest of the flat tax is
   the indirect cost of parking 24 loaded threads 16×/s (ring transitions,
   run-queue churn, per-thread cache refill after every restart). This also
   explains why M8.2's −25% pause p50 bought almost no RPS.
2. Therefore the next mechanism is **fewer young collections per unit work at
   bounded WS**, not shorter ones: stock's effective nursery turns over ~14×
   less often at comparable footprint. Candidates: suspension-rate as a
   first-class adaptive-nursery signal (target a GCs/s ceiling, not just
   futility), decommit-aware large nurseries (young regions are transient — a
   big budget whose regions decommit at sweep bounds WS), or partial young
   collections. Revisit [[adaptive-nursery-design]]'s grow gates with this datum.
3. Still queued behind (1): concurrent mark/sweep cache streaming.

## Files

- `ManagedDotnetGC/RegionAllocator.cs` — marker constant + helpers, ClosePlug
  inheritance, sweep flag recompute, carve per-hole needsZero + remainder marker,
  zeroer skip-marked, pool float cap.
- `ManagedDotnetGC/GcRegionZeroer.cs` — `HoleZeroingEnabled` knob, `ZeroBackground`.
- `ManagedDotnetGC/Zeroing.cs` — `CarveTemporal` (rejected, default-off), `ClearCarve`.
- `ManagedDotnetGC/GCHeap.cs` — knob parsing, `AllocateFreeObject` marker init.
- `experiments/profile-techempower.ps1`, `experiments/tools/EtlCpu/` — new instruments.
