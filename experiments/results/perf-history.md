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

**Memory is half the comparison** (rule added 2026-07-06 after the beat-WKS milestone):
every step note must report peak and average working set alongside wall ratios — the CSV
records `peak_ws_mb` and `avg_ws_mb` per iteration. This becomes non-negotiable for the
Server-GC rows: SVR trades memory for throughput exactly like we do, so a wall-only table
would flatter whichever collector wastes more.

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
| stock WKS re-reference (2026-07-06, matrix sitting) | — | 2.41 | 2.61 | 2.46 |
| **M5 slice 3: parallel full mark + trigger mute** | `344ce8b` | 1.83 (**0.76×**) | 2.17 (**0.83×**) | 1.82 (**0.74×**) |
| **Span zero-at-carve (outside the alloc lock)** | `9251e10` | 1.81 (**0.75×**) | 1.99 (**0.76×**) | 1.76 (**0.71×**) |
| stock WKS re-reference (2026-07-06 midday, zeroer sitting) | — | 2.19 | 2.32 | 2.13 |
| **M7: block zero-at-carve + background zeroer (pool + holes)** | `df8920e` | 1.74 (**0.79×**) | 1.66 (**0.72×**) | 1.75 (**0.82×**) |

`pinheavy` matrix sitting: stock WKS 2.66, ours **1.87 (0.70×)** at `344ce8b`,
**1.79 (0.67×)** at `9251e10` (1.67 with 32 workers — **0.92× of stock SVR-h8**, the
first win against the strongest stock config). Zeroer sitting: stock WKS 2.34, ours
**1.76 (0.75×)**.

Zeroer sitting, same-sitting Server GC (fresh 3-iter medians): default SVR-32 —
soh 1.72, lohmix 1.69, pin 1.68, pinheavy 1.72 → ours **1.01× / 0.98× / 1.04× / 1.02×**:
**parity with default Server GC on every scenario**. SVR-h8 — 1.31 / 1.37 / 1.31 / 1.56 →
ours 1.33× / 1.22× / 1.34× / 1.13×: the tuned 8-heap config is the remaining wall target.

## The M7 fairness matrix (2026-07-06, one sitting, commit `344ce8b`)

Stock WKS was never the real opponent for a GC that uses 8 phase threads. The matrix adds
the configurations that are: Server GC at its default heap count (32 = cores), Server GC
with background collections (the production default), and — the strongest stock config on
this workload — Server GC capped to 8 heaps, matching our thread count. Memory columns per
the rule above. Cell = median wall s (avg WS GB / peak WS GB), 3 iterations:

| Config | soh | lohmix | pin | pinheavy |
|---|---|---|---|---|
| stock WKS | 2.41 (0.8/1.1) | 2.61 (1.1/1.4) | 2.46 (0.8/1.1) | 2.66 (2.4/4.3) |
| stock SVR (32 heaps) | 2.04 (0.9/1.2) | **1.77** (1.0/1.4) | 1.95 (0.9/1.2) | 1.99 (2.4/4.5) |
| stock SVR+BGC | 1.86 (1.0/1.3) | 1.93 (1.0/1.4) | 1.69 (1.0/1.4) | 2.49 (2.5/4.4) |
| stock SVR, 8 heaps | **1.40** (1.8/2.2) | 1.52 (1.7/2.1) | **1.36** (1.8/2.2) | 1.81 (2.6/4.3) |
| ours (8 workers) | 1.83 (3.8/6.7) | 2.17 (3.8/6.9) | 1.82 (3.7/6.7) | 1.87 (4.2/8.5) |
| ours (32 workers) | 1.63 (3.6/6.7) | 2.02 (3.6/6.9) | 1.66 (3.7/6.8) | **1.87** (4.4/8.5) |
| ours, capped at SVR-h8's peak | 2.38 (1.8/2.0) | 3.01 (1.8/2.0) | 2.46 (1.8/2.0) | 1.97 (3.1/4.1) |

Read honestly:

- **vs WKS we now win everywhere** (0.70–0.83×); vs *default* SVR-32 we win 3 of 4 (soh
  0.80×, pin 0.85×, pinheavy 0.94×) and lose lohmix (1.14×); vs SVR+BGC likewise 3 of 4.
- **The tuned SVR-h8 row still beats us on every scenario** (we're 1.03–1.33× at our
  default memory), and it does so on **half our memory**. At *enforced* memory parity
  (hard limit = SVR-h8's peak) we're 1.1× on pinheavy but 1.7–2.0× elsewhere: our wall
  wins are partly bought with the 8×-live trigger's headroom. The memory-throughput
  exchange rate is the M7 fight.
- SVR at 32 heaps is over-partitioned for a 4-thread allocator: capping it to 8 heaps is
  *faster* (soh 2.04 → 1.40) at higher WS — per-heap budgets concentrate instead of
  fragmenting. Any public comparison must include the tuned row, not just defaults.
- BGC is not free for stock either: it *hurts* SVR on pinheavy (1.99 → 2.49).
- Caveat for yesterday's pinning narrative: WKS's 4.1 GB permanent pinheavy fragmentation
  does not carry to SVR, which holds 2.4–2.6 GB avg. The structural-win story vs SVR rests
  on the capped/OOM behavior, not steady-state footprint — re-verify at M7.
- **Pinning-relevance caveat (Kevin, 2026-07-06)**: `pinheavy` models *classic* pinning
  (`GCHandle.Pinned`/`fixed` on ordinary heap objects). Since .NET 5 the Pinned Object
  Heap exists precisely to drain that pattern — modern code (Kestrel buffer pools, newer
  socket paths) allocates long-lived pinned buffers via `GC.AllocateArray(pinned: true)`,
  which doesn't fragment the ephemeral heap at all. Classic pinning is still everywhere
  in interop and older libraries, but our strongest scenario models a *shrinking*
  population, and the write-up must weight it accordingly. Two sides to publish: (a) a
  POH scenario (`-pohar` — the vendored GCPerfSim supports it; our non-moving heap needs
  no code change since every allocation trivially satisfies "won't move") to measure the
  modern-pinning matchup fairly; (b) the architectural point that POH is a *workaround
  the stock GC needed* — API + developer migration — for a problem a non-moving design
  dissolves: we give classic pinning POH-like behavior with no code changes.

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
- **The fairness matrix found a full-GC storm.** GCStats on the matrix build: 0.98 s of a
  2.2 s soh run was STW pause, and 14 of 41 collections were full — GCs 29–40 ran full
  *back-to-back* (~550 ms) because committed (6.5 GB) could never get under 8× live
  (0.5 GB) once survivors scattered: the committed trigger re-fired forever, by design.
  Mutator time was already roughly competitive with SVR-h8; the whole gap was pause.
- **M5 slice 3 (parallel full mark, `344ce8b`) + trigger mute (`183ee3a`): soh full count
  14 → 3, total pause 0.98 → 0.55 s, wall 2.30 → 1.83 (0.76× WKS, 0.80× default SVR).**
  Full marks buffer strong roots during root/handle enumeration and the worker pool traces
  them together; because a couple of stack slots own the whole live graph, drains donate
  the bottom half of any stack past 4096 entries into a native share queue and idle
  workers take bites until every participant idles at once. The remaining full-mark floor
  is the *serial* EE-side enumeration (`GcScanRoots` stack walks — the standalone API
  offers no per-thread partitioning). Young pauses unchanged (p50 ~13 ms; cards+sweep).
  Evidence: suite 56/56, unit 69/69, 3-min soak 5.58 M req 0 err @ 31 k req/s (WS stable
  ~1.7 GB). Next levers, in expected-value order: the lohmix gap (only scenario lost to
  default SVR), the memory exchange rate (capped rows 1.7–2.0×), young cards+sweep p50.
- **Span zero-at-carve (`9251e10`): the lohmix loss was the alloc lock, not the GC.**
  The lohmix profile showed *less* pause than soh — the gap was mutator time: every span
  carve memset whole recycled regions *inside* the global allocation lock (~10 GB of it
  on lohmix) while bump windows had zeroed outside the lock since M4. Spans now flag
  stale members (`SpanIsDirty`) and the allocating thread zeroes only the object extent
  after release; ≤ 2 MB spans pop the pool LIFO instead of scanning the table for a run.
  lohmix 2.17 → 1.99 (1.05× of default SVR-32, from 1.22×); pinheavy at 32 workers 1.67 —
  **0.92× of SVR-h8, the first scenario won against the strongest stock config**. The
  ASP.NET soak went 31 k → **45.5 k req/s (+47%)**: Kestrel's LOH-band buffers live on
  this path. Remaining lohmix costs: the per-thread zeroing itself and the O(table) run
  scan for 2-region spans.

- **Zeroing off the app threads (`df8920e`): parity with default Server GC on every
  scenario.** Profiling the lohmix remainder: 14.5 GB/run of zero-at-carve memset cost the
  app threads ~1.0 s (win_ms 1210 on a 1.6 s × 4-thread run), and a new window-source
  stat showed **73% of windows carve from holes** — pool-level fixes can't reach them.
  (Also learned: `-lohar` is per-mille, so lohmix's LOH band is ~1 GB, not 10 — the
  planned multi-region run-scan fix is moot for this scenario; all its spans are
  single-region.) Three moves: (1) the block tier joins zero-at-carve via a `DirtyBlocks`
  bitmap — no more 2 MB memset inside the alloc lock when a recycled region enters the
  block tier, no more dead-block zeroing in the sweep pause (STW zero_us now reads 0
  structurally); (2) **a background zeroer thread** (BelowNormal, on the GC dll's own
  runtime, EE-invisible) checks dirty regions out of the pool and — the part that
  actually paid — pre-zeroes **hole bodies** region-by-region under a gate the collector
  holds during suspensions, preserving each plug's 32-byte header+link prefix; hole
  carves clean just that prefix inline and hand out windows with no zeroing debt. lohmix
  windows 27% → 67% pre-zeroed, win_ms 1208 → 867. Purely opportunistic: a starved
  zeroer degrades to inline zeroing. (3) **The soak found a deadlock** in the first
  build (~1k requests): a contended `GcAwareLock` winner parks in `DisablePreemptiveGC`
  *holding the lock* until the collection ends; the collector waited on the zeroer's
  gate; the zeroer spun on the parked thread's lock — a GC → zeroer → parked-mutator
  cycle. Fix: once `CollectorWaitingForGate` is set, the world is suspended and
  allocator state is zeroer-exclusive (anyone winning the alloc lock afterwards parks
  before touching it), so the zeroer's spin bails out to lock-free completion. Walls
  (same sitting, all stock refs re-run): soh 1.74 / lohmix 1.66 / pin 1.75 / pinheavy
  1.76 → **0.72–0.82× of WKS and 0.98–1.04× of default SVR-32** — the lohmix loss to
  default SVR is erased. SVR-h8 still leads (1.13–1.34×). Memory unchanged (soh avg WS
  ~3.7 GB vs SVR-32 ~1.0 / SVR-h8 ~1.6). Evidence: suite 56/56, unit 72/72, soak 7.95 M
  req / 0 err / 44.2 k req/s / WS ~1.8 GB. GCStats: zero_us/zero_mb are cumulative
  alloc-path totals now; hole_n/clean_n columns added.

- **M6 stage 0 — side mark bitmap; two-view substrate measured and rejected.** The
  concurrent-marking design (docs/spec-m6-concurrent-mark.md) needs marking to stop
  writing heap pages. First attempt was the DESIGN.md "two-view aliasing" variant:
  every region a 2 MB pagefile section mapped twice (protected front + GC-writable
  alias, VirtualAlloc2 placeholders + MapViewOfFile3), all GC heap writes via the
  alias. **Failed its parity gate 2.2–2.6×** (`m6s0-base` vs `m6s0-substrate`: soh
  2.205 → 4.849, pinheavy 2.189 → 5.774) with **WS doubled** (peak 6.8 → 13.2 GB —
  every page resident through both mappings). The `m6s0-frontzero` control (bulk
  zeroing via the front view, marks/plugs still aliased) killed the WS inflation
  (→ 8.6 GB) but wall stayed ~2×: **section-page soft faults are several times
  pricier than private demand-zero faults**, and recycle/trim churn lives on that
  path — intrinsic, reverted. Replaced epochs with a **side mark bitmap** instead
  (1 bit / 8 heap bytes, 32 KB/region committed with the frontier, cleared wholesale
  at full-mark start and per-region at carve): `m6s0-bitmap` came in **0.90–0.93× of
  the epoch baseline** (soh 1.99 / lohmix 1.91 / pin 1.91 / pinheavy 1.97), identical
  GC counts, WS within ~2% (+1.6% committed for the bitmap). Dense bitmap words beat
  scattered obj−8 stamps: dup-checks never touch the object, sweep/card liveness
  reads are sequential. Bonus: epoch wrap machinery deleted, obj−8 freed. Note for
  cross-sitting comparisons: this sitting's stock-free baseline (`m6s0-base`, code =
  `df8920e`) ran ~2.2 s vs the morning sitting's 1.74 s — compare within sittings.

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
