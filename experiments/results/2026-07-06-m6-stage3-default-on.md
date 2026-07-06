# M6 stage 3: histograms everywhere, default-on, and the 46 ms hiding in pause B (2026-07-06)

Stage 3's remaining questions (spec-m6 §7): do the stage-2 pause claims hold on *all four*
canonical scenarios (only soh had been profiled), and does the evidence justify flipping the
gate from the private staging knob to stock `DOTNET_gcConcurrent` — whose EE-side default
reads as true, i.e. default-on for every app, like stock BGC. Answering them surfaced a
third, better question: what was actually inside pause B.

Protocol: same-sitting A/B runs, Release GC dll, canonical GCPerfSim scenarios plus the
ASP.NET soak, `DOTNET_GCStatsFile` per-collection rows. "Off" = inline STW full collections
(M5 path), "on" = two-pause concurrent cycles. Numbers are per-collection distributions
within single runs, not cross-run medians.

## 1. The default-on evidence

Young pauses are untouched by the mode:

| scenario | young p50 off → on | young max off → on |
|---|---|---|
| soh | 13.1 → 13.8 ms | 17.5 → 19.4 ms |
| lohmix | 14.3 → 12.8 ms | 17.0 → 18.4 ms |
| pin | 14.0 → 14.1 ms | 18.9 → 23.0 ms |
| pinheavy | 26.3 → 25.9 ms | 41.1 → 37.3 ms |

Full collections — one STW block becomes pause A + concurrent window + pause B (each cell
lists the run's individual full collections in order):

| scenario | full STW pauses (off) | on: pause A | on: window (not a pause) | on: pause B |
|---|---|---|---|---|
| soh | 14.9 / 40.4 / 51.7 ms | 1.0 / 3.9 ms | 21.7 / 41.1 ms | 31.8 / 40.2 ms |
| lohmix | 11.5 / 39.1 / 51.4 ms | 0.8 / 4.1 ms | 25.2 / 27.7 ms | 31.7 / 36.9 ms |
| pin | 24.8 / 44.3 / 47.7 ms | 0.9 / 3.5 ms | 24.1 / 36.1 ms | 32.9 / 41.3 ms |
| pinheavy | 26.0 / 74.2 ms | 0.8 / 1.8 ms | 23.0 / 55.7 ms | 34.7 / 44.2 ms |

Readings: **pause A sits at 0.8–4.1 ms on every scenario** — an order of magnitude under
the young pause, which was the design goal. The worst STW event shrinks everywhere, most
where it matters (pinheavy, 1 GB live: 74.2 → 44.2 ms). Steady-state cycles mark 96–100%
of objects inside the window; the first cycle of a run manages only ~50% (it fires while
the allocation rate is wildest) and hands the rest to remark. Throughput did not pay:
stage-2 benches measured −4–9% wall, GC counts/policy unchanged.

**Flipped.** `GCHeap` now reads stock `gcConcurrent` / `System.GC.Concurrent` (EE default:
true) and keeps `DOTNET_GCConcurrentCycles` as an explicit two-way override for A/B runs
(verified: `gcConcurrent=1` + `GCConcurrentCycles=0` runs inline). Suite 56/56 in default /
stock-off / override-off modes.

One trap surfaced by verification rather than assumption: **both sample apps carried
`<ConcurrentGarbageCollection>false</ConcurrentGarbageCollection>`** from before the GC had
a concurrent mode, which pins `System.GC.Concurrent=false` in runtimeconfig and silently
opts them out of the new default (the first "default-on" verification run measured the
inline path because of it). Removed from AspNetSample and GCPerfSim; bench-gcperfsim.ps1
now sets `DOTNET_gcConcurrent` explicitly per row (custom rows measure the shipping
default = on) and clears leftover `DOTNET_GCConcurrentCycles`.

## 2. The soak found 46 ms of decommit inside pause B

Default-config soak (Release dll, 7 min, 32 workers): **18.98 M requests, 0 errors,
~45.2 k req/s, WS flat at 1.6–2.1 GB** — throughput on par with the pre-M6 best (45.5 k).
All 153 full collections ran as concurrent cycles. But the per-collection rows showed
pause B at 46–90 ms while its known contents (remark cards ~1 ms, sweep ~3.3 ms, weak/
dependent tail ~0) explained less than 5 ms of it — ~62 ms was unaccounted, so pause B
got interior columns (`bsusp/rescan/drain2/trim`). The apportionment (150 s soak,
medians over 50 steady-state cycles):

| pause B = 66.1 ms | |
|---|---|
| second SuspendEE | 0.09 ms |
| root re-scan (stacks + handles + f-reachable) | 1.19 ms |
| remark drain (`ParallelDrainMark` #2) | 13.33 ms |
| card scan | 0.97 ms |
| dependent/after/weak tail | 0.04 ms |
| sweep | 3.34 ms |
| **`ApplyBudgetAndTrim` — free-region decommit** | **46.35 ms** |

**`TrimPool` was 70% of the pause.** The server churns ~1.2 GB of free regions per full
cycle; decommitting them down to the retained slack is hundreds of `VirtualFree` calls,
all paid under STW — and the same call sat in every *young* pause, where it produced the
young tail (max 53 ms). Decommitting a free region needs no stopped world; it needs the
allocator's lock discipline.

Fix: the budget reset stays in the pause; the decommit loop moved to the triggering
thread *after* `RestartEE`, still under `_gcLock` (so no next collection can start, and
the zeroer's lock-free suspension escape can never engage against it), taking the
allocation lock in 8-region batches so carves and the zeroer interleave. When the pool is
already at target the whole thing is one comparison — which is every young collection's
case, and why GCPerfSim (committed ≈ 2× live, slack ≤ budget) shows `trim=0` throughout:
this was a server-workload pathology, invisible on the bench harness.

Same soak after the fix:

| | before | after |
|---|---|---|
| pause B (median / max) | 66 / ~90 ms | **19.0 / 21.4 ms** |
| young p99 / max | 17.7 / 53.3 ms | **10.0 / 12.7 ms** |
| pause A (median) | 2.4 ms | 2.6 ms |
| trim (now world-running) | — | 52.6 ms |
| committed (cycle median / peak) | 1504 / 2100 MB | 1496 / 2092 MB |
| throughput, errors | ~46 k/s, 0 | ~46 k/s, 0 |

Committed profile identical — the trim is synchronous on the triggering thread, merely
outside the pause. Suite 56/56 both modes after the change.

The full-collection pause story on the server is now: **A ≈ 2.6 ms, B ≈ 19 ms**, vs the
66–90 ms single STW block a day ago. Pause B's remaining mass is the remark drain
(13.5 ms, ~constant across workloads — mostly popping an already-97%-marked root buffer
through the queue) and sweep (3.4 ms server / 15–22 ms on GCPerfSim's denser heaps).
Follow-ups, in measured-value order: skip already-marked roots when buffering the remark
re-scan (attacks the 13.5 ms; buffer-time mark reads are exactly the drain's dup-check,
done before the queue round-trip instead of after), then concurrent sweep (M6.5).

## 3. Final-build histograms (trim outside pauses, GCPerfSim)

| scenario | young p50/max | cycles: A max | B max | B composition (drain2/cards/sweep, worst cycle) |
|---|---|---|---|---|
| soh | 14.3 / 17.9 ms | 5.2 ms | 36.7 ms | 15.1 / 5.7 / 15.4 ms |
| lohmix | 13.7 / 18.7 ms | 3.0 ms | 39.5 ms | 15.7 / 7.4 / 16.2 ms |
| pin | 14.0 / 18.6 ms | 6.8 ms | 42.2 ms | 14.1 / 7.4 / 18.8 ms |
| pinheavy | 26.4 / 33.4 ms | 2.8 ms | 44.6 ms | 14.6 / 7.9 / 21.7 ms |

## 4. Fairness matrix rerun (m6s3-fairness)

Same protocol as `m7-fairness` (perf-history.csv), all rows same-sitting, custom at its
shipping default (concurrent on). Stock anchors moved ≤ 4% between the two sittings
(wks soh 2.414 → 2.424, svr-h8 soh 1.401 → 1.410), so cross-matrix deltas are real.

Median wall seconds (peak WS MB):

| gc | soh | lohmix | pin | pinheavy |
|---|---|---|---|---|
| custom | 2.078 (6696) | 1.996 (6792) | 2.046 (6788) | 2.081 (8404) |
| custom-h32 | 1.873 (6597) | 1.873 (6942) | 2.001 (6882) | 1.967 (8450) |
| custom-cap2200m | 2.656 (2274) | 2.902 (2262) | 2.584 (2187) | 2.899 (2280) |
| custom-cap4400m | 2.309 (4337) | 2.091 (4297) | 2.368 (4309) | 2.145 (4411) |
| stock-wks | 2.424 (1059) | 2.715 (1377) | 2.433 (1071) | 2.713 (4322) |
| stock-svr | 1.897 (1160) | 1.975 (1290) | 1.844 (1182) | 1.909 (4446) |
| stock-svr-bgc | 1.456 (1490) | 1.901 (1540) | 1.601 (1450) | 2.376 (4075) |
| stock-svr-h8 | 1.410 (2175) | 1.582 (2007) | 1.364 (2173) | 1.786 (4436) |

Custom wall as a multiple of each reference, with the m7-fairness value in parentheses:

| | soh | lohmix | pin | pinheavy |
|---|---|---|---|---|
| vs stock-wks | **0.86** (0.95) | **0.74** (0.93) | **0.84** (0.95) | **0.77** (0.77) |
| vs stock-svr | 1.10 (1.13) | 1.01 (1.37) | 1.11 (1.19) | 1.09 (1.03) |
| vs stock-svr-bgc | 1.43 (1.24) | 1.05 (1.26) | 1.28 (1.38) | **0.88** (0.82) |
| vs stock-svr-h8 | 1.47 (1.64) | 1.26 (1.60) | 1.50 (1.71) | 1.17 (1.14) |

Readings:

- **The day's work moved custom −9.5% (soh), −17.8% (lohmix), −12% (pin), flat
  (pinheavy)** with the GC-count fingerprint unchanged (38/36/38/21 full cycles) — all
  throughput, no policy drift.
- **vs WKS: winning all four scenarios**, now by 14–26%.
- **vs default SVR: effectively even on lohmix (1.01), within 9–11% elsewhere** — this
  was a 1.37× loss on lohmix one matrix ago. SVR and BGC rows are high-variance this
  sitting (iters spread ±15%); treat single-cell movements against them gently.
- **vs the tuned SVR-h8 row: still behind everywhere (1.17–1.50)** but the gap closed
  meaningfully (was 1.14–1.71). This remains the honest target.
- **Memory parity improved most**: at a 2.2 GB cap we now run soh 2.656 (was 3.436) and
  at 4.4 GB pinheavy 2.145 (was 2.922) — 1.12× of default SVR at comparable working set,
  vs 1.47× before. The uncapped memory exchange rate (6.6–8.5 GB peak vs stock's
  1.1–4.4 GB) is unchanged and stays the top M7 item.

Raw per-collection CSVs archived in
[2026-07-06-m6s3-histograms.zip](2026-07-06-m6s3-histograms.zip) (`stats-cc-*` = decision
histograms knob-on, `stats-stw-*` = knob-off, `stats-final-*` = final build,
`stats-soak-*` = the three soak captures).
