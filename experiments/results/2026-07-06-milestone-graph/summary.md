# Milestone graph — 'mixed' scenario medians (2026-07-07, backfill sitting)

Every row in this file — all thirteen milestones and all four stock anchors — was benched 2026-07-07 in ONE sitting via git worktree + Release publish + the HEAD copy of `bench-gcperfsim.ps1 -Scenario mixed`, same machine. Cross-sitting wall times are never comparable, so nothing is reused from earlier backfills — this sitting supersedes the 2026-07-07 afternoon sitting's numbers wholesale, including the eleven milestones it repeats. **Charted metric = median of 3 iterations of the single 'mixed' scenario** (95% ordinary / 5% pinned allocations): pin-dedicated scenarios greatly advantage this non-moving GC, so a geomean including them would flatter the public artifact (protocol change 2026-07-07). Two metrics are tracked: wall_s (lower is better) and peak_ws_mb (lower is better; the memory companion — a wall-only view flatters memory-hungry collectors).

## SHA notes / corrections (carried from the 2026-07-07 morning backfill)

- M2 baseline, M4 sticky gens, M5 parallel: SHAs as given (407cf59, 5db0ed9, 344ce8b) are themselves the code-landing commits — used as-is.

- **M6 concurrent**: the CSV's original `m6s3-fairness` label recorded sha `b889921`, a **docs-only** commit that precedes the actual "M6 stage 3 default-on" code. Backfilled against **`1aedac9`** instead.

- **M7 exchange+quantum**: the original recorded sha `79ce1dc` (docs) predates the quantum fix that names the milestone. Backfilled against **`7689ab1`**.

- **M6.5 sweep-assist**: original sha `765f680` is docs-only and its parent ships the feature default-OFF; the mutator sweep-assist + default-on land in **`d95dddb`** — used instead.

- **Adaptive nursery** (`ceffbfc`), **Vectorized bitmap skip** (`68696ff`), **Partitioned stack scan** (`8b8bcbd`): all verified code-bearing commits.

- **Zeroed-hole markers** (`8868c22`, M8.3) and **Slow-path bump-serve** (`6b6b106`, M9) — new rows added this sitting: both are themselves the code-landing commits, pre-verified per the backfill brief, used as-is.

- **Backfill integrity**: each milestone's publish output is timestamp-verified before benching (an earlier attempt silently re-benched a stale dll after a failed publish — those rows were purged from the archive). This sitting's stale-dll check flagged identical 1.194 s wall medians at Vectorized bitmap skip and Partitioned stack scan; the raw iteration sets differ entirely (1.175/1.194/1.269 vs 1.150/1.280/1.194, distinct GC counts and sim_s), so it is a genuine coincidence of overlapping medians, not a stale build.


## 'mixed' medians (wall seconds) — milestones

| Milestone | sha used | mixed |
|---|---|---|
| M2 baseline | `407cf59` | **7.311** |
| M4 sticky gens | `5db0ed9` | **5.289** |
| M5 parallel | `344ce8b` | **1.801** |
| M6 concurrent | `1aedac9` | **1.870** |
| M7 tuning | `7689ab1` | **1.801** |
| M6.5 sweep-assist | `d95dddb` | **1.400** |
| faster allocation (M7 mutator war) | `e65fa79` | **1.421** |
| sharded supply (M7) | `98517ee` | **1.223** |
| Adaptive nursery | `ceffbfc` | **1.181** |
| Vectorized bitmap skip | `68696ff` | **1.194** |
| Partitioned stack scan | `8b8bcbd` | **1.194** |
| Zeroed-hole markers | `8868c22` | **1.234** |
| Slow-path bump-serve | `6b6b106` | **1.104** |

## 'mixed' medians (wall seconds) — stock anchors (same sitting)

| Config | mixed |
|---|---|
| Workstation GC | **2.595** |
| Server GC (DATAS) | **1.827** |
| Server GC (8 heaps) | **1.338** |
| Server GC (32 heaps) | **1.970** |

## 'mixed' medians (peak working-set MB) — milestones

| Milestone | sha used | mixed |
|---|---|---|
| M2 baseline | `407cf59` | **1368.7** |
| M4 sticky gens | `5db0ed9` | **6551.8** |
| M5 parallel | `344ce8b` | **6770.9** |
| M6 concurrent | `1aedac9` | **6819.5** |
| M7 tuning | `7689ab1` | **2149.6** |
| M6.5 sweep-assist | `d95dddb` | **2404.6** |
| faster allocation (M7 mutator war) | `e65fa79` | **2186.5** |
| sharded supply (M7) | `98517ee` | **2016.1** |
| Adaptive nursery | `ceffbfc` | **1997.5** |
| Vectorized bitmap skip | `68696ff` | **2452.5** |
| Partitioned stack scan | `8b8bcbd` | **2019.6** |
| Zeroed-hole markers | `8868c22` | **2018.3** |
| Slow-path bump-serve | `6b6b106` | **1993.0** |

## 'mixed' medians (peak working-set MB) — stock anchors (same sitting)

| Config | mixed |
|---|---|
| Workstation GC | **2214.9** |
| Server GC (DATAS) | **2396.3** |
| Server GC (8 heaps) | **2610.4** |
| Server GC (32 heaps) | **7116.2** |
