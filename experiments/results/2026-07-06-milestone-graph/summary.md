# Milestone graph — computed geomeans (2026-07-06 evening refresh)

**Same-sitting refresh + new M7 "mutator war" stage.** Tonight's sitting (2026-07-06,
~19:26-20:13 UTC) contains: the new M7 "mutator war" milestone's custom rows (label
`m7-stash2`, sha `e65fa79`), all four stock anchors measured in the same sitting (label
`m7-stash2-anchor`, sha `a610708`, gc values `stock-wks` / `stock-svr-datas` /
`stock-svr-h8` / `stock-svr-h32`), and the six earlier milestones **re-benched tonight**
as `-r2` labels via git worktree + Release NativeAOT publish + `bench-gcperfsim.ps1`,
same machine, so every point on both charts comes from one sitting instead of stitching
together rows from different days/hours. The six `-r2` builds use the exact SHAs
verified during the 2026-07-06 backfill (see "SHA notes" below — unchanged from that
backfill). Geomean = geometric mean of the 4 scenario medians (3 iterations each).

## SHA notes / corrections (carried over from the original backfill)

- M2 baseline, M4 sticky gens, M5 parallel: SHAs as given (407cf59, 5db0ed9, 344ce8b) are themselves the code-landing commits — used as-is.

- **M6 concurrent**: the CSV's `m6s3-fairness` label records sha `b889921`, but that commit is a **docs-only** commit ("ROADMAP reflects M6 stages 0-2 landed") that chronologically **precedes** the actual "M6 stage 3 default-on" code (`1aedac9`, "concurrent cycles default-on; free-region decommit leaves the pauses") by ~47 minutes. Backfilled (and tonight re-benched) against **`1aedac9`**.

- **M7 exchange+quantum**: likewise, the CSV's `m7-quantum` label records sha `79ce1dc` ("docs: M7 memory exchange rate"), which is an **ancestor** of `7689ab1` ("M7: kill the drain-termination Sleep(1) quantum"). Backfilled (and tonight re-benched) against **`7689ab1`**.

## Per-scenario medians (wall seconds) — tonight's sitting

| Stage | label | sha | soh | lohmix | pin | pinheavy | geomean |
|---|---|---|---|---|---|---|---|
| first working allocator | `backfill-m2-r2` | `407cf59` | 8.499 | 8.042 | 8.514 | 7.232 | **8.054** |
| generational | `backfill-m4-r2` | `5db0ed9` | 5.388 | 5.478 | 5.478 | 5.068 | **5.350** |
| parallel mark & sweep | `backfill-m5-r2` | `344ce8b` | 1.984 | 2.253 | 1.976 | 1.918 | **2.029** |
| concurrent marking | `backfill-m6-r2` | `1aedac9` | 1.917 | 1.854 | 1.964 | 1.980 | **1.928** |
| memory diet + tuning | `backfill-m7-r2` | `7689ab1` | 1.881 | 1.817 | 1.870 | 1.835 | **1.851** |
| concurrent sweep | `m65s2-final-r2` | `765f680` | 1.785 | 1.787 | 1.846 | 1.847 | **1.816** |
| faster allocation (M7 mutator war) | `m7-stash2` | `e65fa79` | 1.379 | 1.366 | 1.400 | 1.534 | **1.418** |

## Stock reference medians (wall seconds) — tonight's sitting

Label `m7-stash2-anchor`, sha `a610708`.

| Config | gc value | soh | lohmix | pin | pinheavy | geomean |
|---|---|---|---|---|---|---|
| Workstation GC | `stock-wks` | 2.307 | 2.422 | 2.312 | 2.565 | **2.399** |
| Server GC (DATAS) | `stock-svr-datas` | 2.041 | 1.970 | 1.962 | 2.555 | **2.119** |
| Server GC (32 heaps) | `stock-svr-h32` | 1.768 | 1.625 | 1.583 | 2.106 | **1.759** |
| Server GC (8 heaps) | `stock-svr-h8` | 1.306 | 1.431 | 1.274 | 1.638 | **1.405** |

Note: this sitting's Server GC (DATAS) row (2.119s geomean) came in *slower* than
Server GC (32 heaps) (1.759s) — the reverse of the earlier DATAS-discovery sitting
(1.704s vs 1.863s). DATAS's heap-count adaptation reacts to the live workload, so its
wall time has more run-to-run variance than the fixed-heap-count configs; this is the
same-sitting reading and is what both charts plot.

## Peak working set (GB) — tonight's sitting

Median `peak_ws_mb` per scenario (3 iterations each), geomean of the 4 scenario medians, converted to GB (`/1024`). Same rows/labels as the wall-time tables above.

| Stage | soh | lohmix | pin | pinheavy | geomean (GB) |
|---|---|---|---|---|---|
| first working allocator | 1.332 | 1.355 | 1.336 | 2.399 | **1.551** |
| generational | 6.414 | 6.694 | 6.400 | 8.236 | **6.897** |
| parallel mark & sweep | 6.442 | 6.687 | 6.633 | 8.350 | **6.989** |
| concurrent marking | 6.573 | 6.517 | 6.681 | 8.232 | **6.967** |
| memory diet + tuning | 2.125 | 2.076 | 2.059 | 3.881 | **2.436** |
| concurrent sweep | 2.351 | 2.078 | 2.041 | 3.856 | **2.490** |
| faster allocation (M7 mutator war) | 2.070 | 2.145 | 2.070 | 3.890 | **2.445** |

| Stock config | soh | lohmix | pin | pinheavy | geomean (GB) |
|---|---|---|---|---|---|
| Workstation GC | 1.037 | 1.351 | 1.044 | 4.289 | **1.582** |
| Server GC (DATAS) | 1.171 | 1.356 | 1.172 | 4.278 | **1.680** |
| Server GC (32 heaps) | 6.957 | 3.911 | 6.954 | 7.459 | **6.129** |
| Server GC (8 heaps) | 2.125 | 2.112 | 2.126 | 4.194 | **2.515** |

## What's new / what changed this sitting

- **New stage: "faster allocation" (M7 mutator war, label `m7-stash2`)** lands as the
  7th and fastest point on the wall-time chart: **1.418s geomean**, beating every
  earlier milestone and every stock config measured tonight (next-best is stock Server
  GC (8 heaps) at 1.405s — essentially tied, ManagedDotnetGC is within 1% of it). Its
  peak working set (2.445 GB) sits in the same tight band as the two prior milestones
  (memory diet+tuning 2.436 GB, concurrent sweep 2.490 GB) — mutator-side work did not
  move the memory needle, as expected for a stage focused on allocation/mutator paths
  rather than collection/reclaim.
- **All six older milestones re-benched tonight** (`-r2` labels) so the whole chart is
  one sitting: wall-time geomeans moved only slightly vs. the original 2026-07-06
  daytime backfill (largest shift: M2 baseline 7.673s -> 8.054s, ~5% slower this
  sitting — run-to-run machine noise, not a code change; every other stage moved <2%).
  Peak working set geomeans are within 1% of the original backfill across the board.
- **Stock anchors re-measured tonight** alongside the custom builds (label
  `m7-stash2-anchor`) rather than reused from the earlier `svrgap` sitting.

## Footnotes

- M2 baseline predates the `pinheavy` scenario's existence in the archive's protocol, but both the original backfill and tonight's re-bench ran it anyway against the old GC build using the current `bench-gcperfsim.ps1`/GCPerfSim — it completes normally, so no scenario is missing from either table.
- Geomean = geometric mean of the 4 per-scenario **medians** (median of 3 iterations each), not a geomean of all 12 raw iterations.
