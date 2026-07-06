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
| M4 + card-offset tables (card scan by dirty runs) | `5ab6a54` | 4.46 (2.23×) | 4.55 (2.04×) | 4.86 (2.45×)* |
| stock WKS re-reference (2026-07-06 evening) | — | 2.22 | — | 2.13 |
| M5 slice: parallel sweep (8 participants) | `266284a` | 2.89 (**1.30×**) | 3.21 (~1.44×†) | 3.00 (1.41×) |
| stock WKS re-reference (2026-07-06 night) | — | 2.36 | 2.54 | 2.37 |
| **M5 slice 2: parallel card scan** | `8e45e7f` | 2.29 (**0.97×**) | 2.56 (**1.01×**) | 2.28 (**0.96×**) |

`pinheavy` night sitting: stock 2.78, ours **1.94 (0.70×)**. First beat-stock across the
suite: soh/pin under 1×, lohmix tied, pinheavy won by 30% — young pauses p50 13 ms.

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
- **Card-offset tables: soh 2.52× → 2.23×, card scan 2.60 s → 0.96 s.** One ushort per
  card back-links to the nearest object start (rebuilt free inside sweep walks, stamped
  at window carves); the card scan walks dirty runs only. `pin`'s median (*) had one
  outlier iteration (5.97 s vs 4.44) — machine noise or full-GC alignment; re-measure
  with the next step. `pinheavy` same sitting: 4.53 s (1.55×). Remaining young-pause
  pools are now the sweep walk of allocation-touched regions and densely-dirty reopened
  regions — parallelism (M5) is the next lever.
- **Parallel sweep: soh 2.23× → 1.30×, sweep pauses ÷7.** Persistent worker threads on
  the GC dll's own runtime (invisible to the EE); region-chunk dispenser, per-worker
  list building, lock-free pool pushes. Stock re-referenced the same evening (2.22 —
  it drifted from 2.00 within the day, hence the fresh row; † lohmix ratio uses the
  morning stock). **`pinheavy`: 2.90 vs stock 2.92 — first parity — with ~1.4 GB final
  heap vs stock's 4.3 GB.** Young pauses are now ~70% card scan (0.96 s): parallel card
  scan (per-worker mark stacks + CAS marking, collectible edges deferred to the GC
  thread) is the queued next step, projected to put soh near ~1.2×.
- **Parallel card scan: the beat-stock line.** Cards 0.96 s → 0.18 s; young pauses
  p50 13 ms / max 17 ms on soh. Honesty box: (a) stock here is single-threaded WKS
  non-concurrent — our GC uses up to 8 phase threads; a Server-GC comparison is owed
  before any public "faster than .NET's GC" claim (M7); (b) we spend more memory
  (soh peak WS ~6.5 GB vs stock ~1.7 GB — the 8×-live trigger; `pinheavy` reverses it:
  stock 4.5 GB *permanent* vs our recyclable floats); (c) machine slowed through the
  day (stock 2.00 → 2.36), so only same-sitting ratios are valid — and same-sitting
  says soh 0.97×, pin 0.96×, pinheavy 0.70×, lohmix 1.01×. Remaining pause pool: the
  serial full-GC mark (~66 ms at 500 MB live) — parallel root mark is the rest of M5.

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
