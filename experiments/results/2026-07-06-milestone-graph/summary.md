# Milestone graph — 'mixed' scenario medians (2026-07-07, afternoon sitting)

Every row in this file — all eleven milestones and all four stock anchors — was benched 2026-07-07 afternoon in ONE sitting via git worktree + Release publish + the HEAD copy of `bench-gcperfsim.ps1 -Scenario mixed`, same machine. Cross-sitting wall times are never comparable, so nothing is reused from earlier backfills. **Charted metric = median of 3 iterations of the single 'mixed' scenario** (95% ordinary / 5% pinned allocations): pin-dedicated scenarios greatly advantage this non-moving GC, so a geomean including them would flatter the public artifact (protocol change 2026-07-07). Two metrics are tracked: wall_s (lower is better) and peak_ws_mb (lower is better; the memory companion — a wall-only view flatters memory-hungry collectors).

## SHA notes / corrections (carried from the 2026-07-07 morning backfill)

- M2 baseline, M4 sticky gens, M5 parallel: SHAs as given (407cf59, 5db0ed9, 344ce8b) are themselves the code-landing commits — used as-is.

- **M6 concurrent**: the CSV's original `m6s3-fairness` label recorded sha `b889921`, a **docs-only** commit that precedes the actual "M6 stage 3 default-on" code. Backfilled against **`1aedac9`** instead.

- **M7 exchange+quantum**: the original recorded sha `79ce1dc` (docs) predates the quantum fix that names the milestone. Backfilled against **`7689ab1`**.

- **M6.5 sweep-assist**: original sha `765f680` is docs-only and its parent ships the feature default-OFF; the mutator sweep-assist + default-on land in **`d95dddb`** — used instead.

- **Adaptive nursery** (`ceffbfc`), **Vectorized bitmap skip** (`68696ff`), **Partitioned stack scan** (`8b8bcbd`): all verified code-bearing commits.

- **Backfill integrity**: each milestone's publish output is timestamp-verified before benching (an earlier attempt this sitting silently re-benched a stale dll after a failed publish — those rows were purged from the archive).


## 'mixed' medians (wall seconds) — milestones

| Milestone | sha used | mixed |
|---|---|---|
| M2 baseline | `407cf59` | **7.824** |
| M4 sticky gens | `5db0ed9` | **5.201** |
| M5 parallel | `344ce8b` | **1.811** |
| M6 concurrent | `1aedac9` | **1.809** |
| M7 tuning | `7689ab1` | **1.783** |
| M6.5 sweep-assist | `d95dddb` | **1.427** |
| faster allocation (M7 mutator war) | `e65fa79` | **1.382** |
| sharded supply (M7) | `98517ee` | **1.239** |
| Adaptive nursery | `ceffbfc` | **1.225** |
| Vectorized bitmap skip | `68696ff` | **1.146** |
| Partitioned stack scan | `8b8bcbd` | **1.172** |

## 'mixed' medians (wall seconds) — stock anchors (same sitting)

| Config | mixed |
|---|---|
| Workstation GC | **2.593** |
| Server GC (DATAS) | **1.935** |
| Server GC (8 heaps) | **1.319** |
| Server GC (32 heaps) | **1.758** |

## 'mixed' medians (peak working-set MB) — milestones

| Milestone | sha used | mixed |
|---|---|---|
| M2 baseline | `407cf59` | **1373.4** |
| M4 sticky gens | `5db0ed9` | **6534.8** |
| M5 parallel | `344ce8b` | **6811.6** |
| M6 concurrent | `1aedac9` | **7002.1** |
| M7 tuning | `7689ab1` | **2150.0** |
| M6.5 sweep-assist | `d95dddb` | **2137.1** |
| faster allocation (M7 mutator war) | `e65fa79` | **2946.4** |
| sharded supply (M7) | `98517ee` | **2003.1** |
| Adaptive nursery | `ceffbfc` | **2169.7** |
| Vectorized bitmap skip | `68696ff` | **2181.7** |
| Partitioned stack scan | `8b8bcbd` | **2534.7** |

## 'mixed' medians (peak working-set MB) — stock anchors (same sitting)

| Config | mixed |
|---|---|
| Workstation GC | **2267.0** |
| Server GC (DATAS) | **2400.7** |
| Server GC (8 heaps) | **2628.0** |
| Server GC (32 heaps) | **7122.8** |
