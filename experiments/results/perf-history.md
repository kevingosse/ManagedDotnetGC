# Performance history — one entry per step

The per-step performance archive for the eventual write-up: every milestone that could
plausibly move performance gets benchmarked with the **same protocol** and appended here.
Raw per-iteration rows live in [perf-history.csv](perf-history.csv); this file holds the
curated medians and the story.

**Protocol** (`experiments/bench-gcperfsim.ps1`): vendored GCPerfSim, 3 iterations/scenario,
median wall clock measured outside the process; workstation, non-concurrent, win-x64,
**Release** NativeAOT GC builds only. Scenarios (fixed — add new ones, never edit these):

- `soh` — pure small-object churn: `-tc 4 -tagb 20 -tlgb 0.5 -sohsi 50 -sohsr 100-4000 -tk time`
- `lohmix` — soh + LOH-band: `+ -lohar 50 -lohsr 100000-2000000 -lohsi 50`
- `pin` — soh + pinning: `+ -sohpi 100`
- `pinheavy` (added 2026-07-06) — rotating long-lived pinned survivors, 1 GB live:
  `-tc 4 -tagb 20 -tlgb 1 -sohsi 50 -sohsr 100-4000 -sohpi 10`

Machine: AMD Ryzen 9 7950X3D (16C/32T), 96 GB RAM, Windows 11. Re-benchmark the stock
reference in the same sitting as any new step — absolute numbers drift with machine state;
ratios are the archive's currency.

## Median wall seconds (ratio vs same-day stock)

| Step | Commit | soh | lohmix | pin |
|---|---|---|---|---|
| stock WKS reference (2026-07-05) | — | 2.82 | 3.04 | 3.29 |
| M2 core (region heap: triggering, reuse, OOM) | `407cf59` | 8.99 (3.19×) | 8.34 (2.75×) | 8.63 (2.62×) |
| M1 part 1: nine gates (stubs, SuppressFinalize, f-reachable roots, frozen deps, types 10/11, accounting) | `88ae0ef` | 8.75 (3.11×) | 8.04 (2.65×) | 8.70 (2.64×) |
| M1 complete (EE brackets, card/bundle tables, collectible mark edge, ref-counted scan) | `3c25f87` | 9.01 (3.20×) | 8.42 (2.77×) | 8.97 (2.72×) |
| stock WKS reference (2026-07-06) | — | 2.00 | 2.23 | 1.98 |
| M4 sticky generations (young GCs via cards, Reopened regions, 64 KB hole floor, zero-at-carve) | `5db0ed9` | 5.04 (2.52×) | 4.96 (2.23×) | 5.01 (2.53×) |

`pinheavy` (same sitting): stock 2.92 s / **4.53 GB peak WS, 4.32 GB final heap**; M4
4.92 s (1.69×) / 8.5 GB peak, **1.39 GB final**. Wall vs memory: see
[2026-07-06-pinning-structural-win.md](2026-07-06-pinning-structural-win.md) — with a
4 GB hard cap and 50% pinned survivors, stock OOMs and we complete.

## Step notes

- **M2 core → the 3× starting line.** ~50 full collections (each marking the whole ~0.5 GB
  live set) vs stock's ~470 gen0s. The gap is generational by construction; M4 sticky
  generations is the planned answer. Footprint counterpoint: final heap ≈ 136 MB vs stock's
  ≈ 1 GB on `soh` (budget converges to ≈ 2× live).
- **M1 nine gates: free.** Deltas vs M2 core are within run-to-run noise (−1 to −3%,
  direction inconsistent across scenarios). Early f-reachable marking and accounting
  counters don't show up at this scale.
- **M1 complete: ≈ +3% (consistent across all three scenarios).** The correctness features
  that touch hot paths: a `Collectible` flag test per marked object, four EE bracket
  calls per collection (each an UnmanagedCallersOnly round trip), the ref-counted handle
  scan per collection, and real card/bundle writes in the EE's bulk-copy path. Three
  iterations can't fully separate +3% from noise, but the sign was consistent in every
  scenario. Candidate for M7 micro-tuning (e.g. hoisting the collectible test behind a
  "any collectible types seen" flag); not worth attention before M4.
- **M4 sticky generations: 3.20× → 2.52× on soh (2.77× → 2.23× lohmix, 2.72× → 2.53× pin),
  and STW zeroing eliminated.** Five design iterations in one day, each profiler-driven —
  the full story (nursery smearing by hole-first carving, the Reopened age state, the
  CLT-concentrated survivor gaps that defeated a 128 KB hole floor, the doubling-trigger
  ladder, the alloc-lock zeroing convoy) is in
  [2026-07-06-m4-sticky-generations.md](2026-07-06-m4-sticky-generations.md). Note both
  absolute walls dropped vs 07-05 (stock 2.82 → 2.00) — machine-state drift; ratios are
  the comparison. Cost: peak committed ~6.5 GB on soh (uniform-random survivor scatter is
  near-adversarial for a non-moving heap; the write barrier now pays a card write per ref
  store). The remaining gap is walk-bound: card-scan and sweep walks of
  allocation-touched regions — card-offset tables and survivor packing are the M5/M7
  levers.

## How to add a step

```powershell
# after any perf-relevant commit (GC dll = Release publish):
dotnet publish .\ManagedDotnetGC /p:SelfContained=true -r win-x64 -c Release
.\experiments\bench-gcperfsim.ps1 -Label stock                # fresh same-day reference
.\experiments\bench-gcperfsim.ps1 -Label <step-name> -GcDll .\ManagedDotnetGC\bin\Release\net10.0\win-x64\publish\ManagedDotnetGC.dll
# then add the medians + a step note here
```

Historical builds can be backfilled with `git worktree add <dir> <sha>` + publish, as done
for the three rows above.
