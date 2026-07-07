# OrchardCore: first real-application benchmark — 0.94× server GC throughput, 2.7× better p99

First measurement against a real product instead of a synthetic or benchmark-derived
workload: OrchardCore CMS 3.0 (net10), Blog recipe on SQLite, full CMS middleware +
Razor + YesSql per request. Setup: `E:\git\oc-bench\publish` (OcBench.dll, auto-setup,
App_Data must be preserved and CWD must be the publish dir); harness =
`experiments/bench-orchard.ps1` (bench-techempower conventions: dll-deploy trap fix,
inline WS sampler, interleaved A/B/A). 128 connections, blog homepage (`/`).

## Results (same-sitting 2026-07-07 evening, GC dll = M9.1/70be506, medians of 6 iters across 2 brackets)

| GC | rps | p50 | p99 | peak WS |
|---|---|---|---|---|
| stock-wks | 1694 | 67 ms | 222 ms | ~400 MB |
| **custom** | **4940** | **24 ms** | **64 ms** | ~985 MB |
| stock-svr-h8 | 5236 | 18 ms | **172 ms** | ~950 MB |
| stock-svr-datas (3 iters, later same sitting) | 5421 | 18 ms | 77 ms (60/77/172) | **462 MB** |

60 s sustained (1 iter each, interleaved): custom 4823 rps / p99 91 ms / 1013 MB;
stock-h8 5009 rps / p99 185 ms / 933 MB → 0.96×, p99 2×. DATAS 60 s (added on
Kevin's question, ~25 min later): 5337 rps / p99 63 ms / 463 MB.

## Reading

- **Throughput ratio replicates TechEmpower exactly (0.94×)** — the M9.1 profile's
  diffuse-scheduler-tax diagnosis transfers unchanged to a real app. Nothing about a
  10× heavier request (Razor + YesSql + CMS pipeline vs raw fortunes) changed the gap.
- **The tail flips in our favor vs PINNED h8: p99 64 ms vs 172 ms.** Stock-h8 spikes
  p99 ≥170 ms in 4 of 6 iterations (and 185 ms over the 60 s run) — the episodic-gen2
  signature on a heap holding real mid-life state (content items, YesSql session
  caches). Our small frequent pauses keep p99 flat at 60–67 ms. Invisible on
  TechEmpower fortunes (trivial live set, ~10 ms p99 both sides); it is the
  [[snapshot-collector-design]] / M6 win-axis showing up unprompted.
- **BUT: DATAS (the actual .NET 10 server default) is the strongest stock config here
  and reopens every axis.** vs DATAS we are 0.91× rps at 2.1× the WS, and the tail
  advantage is NOT established: DATAS 15 s iters still show the episodic spike
  (60/77/172) but its single 60 s window came in at 63 ms vs our 91 ms. Single windows
  are too noisy for a tail verdict — the h8-vs-DATAS spread (185 vs 63 over 60 s)
  shows the spikes are episodic. **Open question, needs replicated long runs (e.g.
  5×60 s interleaved + p99.9): does DATAS's smaller heap (fewer gen2s? cheaper ones?)
  genuinely dodge the mid-life tail, or did one window get lucky?** Until answered,
  the honest claim is: we beat pinned-h8 tails, we match-ish DATAS, at 2× its memory.
- **Workstation GC (1-heap default) collapses: 2.9× slower than us** at the same task.
  Only fair to note ASP.NET defaults to server GC.
- WS: parity with server-h8 (~1 GB both). The boost controller behaves on a real app
  (fires on the web shape as designed; no runaway).

## Excluded: the single-post endpoint (`/blog/post-1`)

Pathological under ALL THREE GCs identically (~200 rps, p99 3–6 s, degrading across
iterations, e.g. custom 247→259→129, stock-h8 221→243→186). App-level bottleneck —
smells like SQLite write contention or a lock convoy on the content-item path, not GC
signal. Future: rerun the post endpoint with Postgres or diagnose the write; until
then only the homepage differentiates GCs.

## The bottleneck, measured (gcstats, added same evening on Kevin's question)

NOT the TechEmpower scheduler tax. Per-collection stats (-StatsDir) under load:
**~15% of wall is STW.** 976 young GCs (~10/s) at p50 12.3 ms + 236 fulls (1.6/s,
217 'promoted') at p50 33 ms. Young pause breakdown: **roots 5.4 ms** (deep
Razor/DI/middleware stacks — 11× the TFB post-partition floor of 469 µs) +
**cards 4.3 ms** (real old→young graph, ~253 card regions of mid-life content
objects) + sweep 1.6 ms.

Why 10 young GCs/s: **the boost controller has this workload backwards.** In-flight
survivor mass is concurrency-bound (~18-19 MB regardless of interval — confirmed:
same marked_mb at 64 MB and 256 MB budgets), but 18 MB > the 16 MB shrink gate
(fires on 661/976 GCs) and 12 ms > the 10 ms cheap-pause gate — so the boost can
never grow and keeps collapsing the budget to 64-137 MB. A bigger budget marks the
same mass fewer times AND promotes less (fewer 'promoted' fulls); the gates,
calibrated on TFB's 2.4 MB in-flight, do the opposite here.

Confirmation with contamination: -Gen0MB 256 (controller off) → young 976→395,
RPS +7% (4550→4881 stats-run-to-stats-run), p50 27→22 ms — DESPITE unleashing the
known fixed-gen0size scar (demand=2×budget=512 that this heap can't retain →
393 starved fulls, ~3/s). The +7% survived a full-cycle storm; the proper fix is
controller recalibration (grow gates for concurrency-bound survivor mass — rate- or
live-relative, not absolute; rethink the 10 ms cheap gate, which blocks growth
exactly when a real app needs it most), whose boost path moves demand correctly.
Next-session item #1.

## Repro

```
# app: E:\git\oc-bench\publish (OcBench.dll, ASPNETCORE_URLS=http://127.0.0.1:9080,
#      CWD = publish dir or AutoSetup re-triggers; wwwroot/ must exist after republish)
experiments\bench-orchard.ps1 -Label <x> -GcDll <Release publish dll>
experiments\bench-orchard.ps1 -Label <x> -ServerGC -HeapCount 8
```
