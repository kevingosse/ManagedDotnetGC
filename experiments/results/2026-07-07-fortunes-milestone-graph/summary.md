# TechEmpower Fortunes — milestone RPS + working set (2026-07-07, backfill sitting)

## Protocol

Every row in this file — all thirteen milestones and all four stock anchors — was benched
2026-07-07 in ONE sitting via git worktree + Release NativeAOT publish + the HEAD copy of
`experiments/bench-techempower.ps1 -Endpoint fortunes`, same machine, the same sitting as the
GCPerfSim mixed-scenario backfill in `2026-07-06-milestone-graph/`. Cross-sitting numbers are
never comparable on this machine (~±5% drift), so nothing is reused from earlier sessions
(the exploratory numbers in `2026-07-07-techempower-and-mixed.md` are superseded here).

Workload: TechEmpower `aspnetcore/Mvc` app (ASP.NET MVC + EF Core + Npgsql) **fortunes**
endpoint — Razor render + 12-row query, string/HTML churn — against the local Postgres 16
container (`tfb-database`). Driver: bombardier, **256 connections, 10 s warmup + median of
3×15 s runs**; one throwaway warmup pass ran (and was discarded) before the loop because the
first run of a sitting is a cold outlier. Peak/avg working set sampled inline from the app
process during each bombardier run. **Charted metrics = median rps (higher is better) and
median peak working set (lower is better).** The working-set chart ships alongside the RPS
one deliberately: a GC that never collects would "win" RPS on memory it never gives back.

Build integrity: each milestone's publish output is timestamp-verified (< 3 min old) before
benching; no stage was benched against a stale dll. All 13 stages built and ran; none failed
outright under load.

## Stock anchors (same sitting)

| Config | rps | peak ws (MB) |
|---|---:|---:|
| Workstation GC | **76,822** | **197** |
| Server GC (DATAS) | **86,736** | **280** |
| Server GC (8 heaps) | **88,057** | **726** |
| Server GC (32 heaps) | **80,309** | **2,108** |

## Milestones

| # | Milestone | sha | rps | peak ws (MB) | notes |
|---|---|---|---:|---:|---|
| 1 | M2 baseline | `407cf59` | **25,972** | **273** | one timeout burst (452 errs total, ~0.04%) |
| 2 | Sticky generations | `5db0ed9` | **13,774** | **568** | persistent timeout storms — see below |
| 3 | Parallel mark & sweep | `344ce8b` | **31,016** | **585** | one burst (978 errs) |
| 4 | Concurrent marking | `1aedac9` | **29,143** | **884** | one burst (788 errs) |
| 5 | Memory tuning | `7689ab1` | **30,809** | **721** | clean |
| 6 | Concurrent sweep | `d95dddb` | **37,840** | **762** | one burst (316 errs) |
| 7 | Faster allocation | `e65fa79` | **40,248** | **745** | clean |
| 8 | Sharded supply | `98517ee` | **39,315** | **836** | one burst (1,482 errs); retry median 38,991 confirms |
| 9 | Adaptive nursery | `ceffbfc` | **70,199** | **1,611** | clean |
| 10 | Vectorized bitmap skip | `68696ff` | **70,007** | **1,214** | clean |
| 11 | Partitioned stack scan | `8b8bcbd` | **72,137** | **1,262** | clean |
| 12 | Zeroed-hole markers | `8868c22` | **71,749** | **1,198** | clean |
| 13 | Slow-path bump-serve | `6b6b106` | **80,595** | **990** | clean |

### Error annotations (no failed stages)

- **Sticky generations (`5db0ed9`)**: thousands of bombardier timeouts every run
  (13,241 across the 3 kept iterations). Re-built and re-benched once per the retry
  policy: same character (median 13,571, errors 8,417/1,520/2,846) — the timeout storms
  are that build's genuine behavior under 256-connection Kestrel load, not a bad run.
  Original rows kept.
- **Sharded supply (`98517ee`)**: single-iteration burst (1,482 errs). Retry: median
  38,991 with one 529-err iteration — occasional bursts are characteristic, rps
  confirmed. Original rows kept.
- Stages 1/3/4/6 had sub-0.15% single-iteration bursts (452/978/788/316 errs) —
  annotated per the precedent in `2026-07-07-techempower-and-mixed.md`, not re-run.
- Stages 9–13 (adaptive nursery onward): zero errors in every iteration.

## Reading

- The web workload was the collector's weak spot as recently as sharded supply
  (0.45× h8, with error bursts). The two big levers visible here: **adaptive nursery**
  (+79% rps in one stage — the frequent-futile-cheap boost was built for exactly this
  shape) and **slow-path bump-serve** (+12% over the zeroed-hole stage, killing the 20×
  carve inflation that drove suspension frequency).
- Final stage vs anchors: **0.92× Server GC (8 heaps)**, 0.93× DATAS, and now **ahead of
  both Workstation GC (1.05×) and Server GC (32 heaps) (1.00×)** on rps — with zero
  errors.
- Memory is the honest companion: 990 MB peak vs 726 MB (h8), 280 MB (DATAS), 197 MB
  (wks). The adaptive nursery bought its throughput with the series' working-set peak
  (1.6 GB); M9 clawed ~270 MB of that back while also gaining rps. Only Server GC
  (32 heaps) (2.1 GB) is hungrier.

Machine state: normal desktop background; Docker Desktop (Linux engine) running the
Postgres container during all rows, including the stock anchors. Raw rows (labels
`msf-1`…`msf-13`, `msf-2-retry`, `msf-8-retry`, `msf-stock-*`) in
`experiments/results/techempower-history.csv`.
