# Milestone graph — computed geomeans (2026-07-06)

All custom-GC milestone rows backfilled TODAY (2026-07-06) via git worktree + Release 
publish + `bench-gcperfsim.ps1`, same machine, same sitting, except M6.5 which reuses 
today's `m65s2-final` rows (current HEAD build). Stock reference rows reuse today's 
`svrgap` rows (also same sitting). Geomean = geometric mean of the 4 scenario medians 
(3 iterations each).

## SHA notes / corrections

- M2 baseline, M4 sticky gens, M5 parallel: SHAs as given (407cf59, 5db0ed9, 344ce8b) are themselves the code-landing commits — used as-is.

- **M6 concurrent**: the CSV's `m6s3-fairness` label records sha `b889921`, but that commit is a **docs-only** commit ("ROADMAP reflects M6 stages 0-2 landed") that chronologically **precedes** the actual "M6 stage 3 default-on" code (`1aedac9`, "concurrent cycles default-on; free-region decommit leaves the pauses") by ~47 minutes. Checking out `b889921` would silently drop the milestone's defining change. Backfilled against **`1aedac9`** instead.

- **M7 exchange+quantum**: likewise, the CSV's `m7-quantum` label records sha `79ce1dc` ("docs: M7 memory exchange rate"), which is an **ancestor** of `7689ab1` ("M7: kill the drain-termination Sleep(1) quantum") — i.e. it predates the quantum fix that gives the milestone its name (confirmed via `git diff --stat 79ce1dc 7689ab1`: `GCHeap.Mark.cs` and the new `GCHeap.Concurrent.cs` pre-drain only land in `7689ab1`). Both `79ce1dc` and `1aedac9`/`b889921` bench UTC timestamps predate their own commit's timestamp, consistent with this repo's habit of benching against uncommitted local edits and committing afterward with a convenience `-Sha`. Backfilled against **`7689ab1`** instead, which is the first commit that actually contains the quantum fix.


## Per-scenario medians (wall seconds, backfilled today unless noted)

| Milestone | sha used | soh | lohmix | pin | pinheavy | geomean |
|---|---|---|---|---|---|---|
| M2 baseline | `407cf59` | 8.499 | 8.042 | 8.514 | 7.232 | **8.054** |
| M4 sticky gens | `5db0ed9` | 5.388 | 5.478 | 5.478 | 5.068 | **5.350** |
| M5 parallel | `344ce8b` | 1.984 | 2.253 | 1.976 | 1.918 | **2.029** |
| M6 concurrent | `1aedac9` | 1.917 | 1.854 | 1.964 | 1.980 | **1.928** |
| M7 tuning | `7689ab1` | 1.881 | 1.817 | 1.870 | 1.835 | **1.851** |
| M6.5 sweep-assist | `765f680` | 1.785 | 1.787 | 1.846 | 1.847 | **1.816** |
| faster allocation (M7 mutator war) | `e65fa79` | 1.379 | 1.366 | 1.400 | 1.534 | **1.418** |
| sharded supply (M7) | `98517ee` | 1.241 | 1.181 | 1.315 | 1.177 | **1.227** |

## Stock reference medians (today's `svrgap` rows, sha 765f680)

| Config | soh | lohmix | pin | pinheavy | geomean |
|---|---|---|---|---|---|
| Workstation GC | 2.307 | 2.422 | 2.312 | 2.565 | **2.399** |
| Server GC (DATAS) | 2.041 | 1.970 | 1.962 | 2.555 | **2.119** |
| Server GC (8 heaps) | 1.306 | 1.431 | 1.274 | 1.638 | **1.405** |
| Server GC (32 heaps) | 1.768 | 1.625 | 1.583 | 2.106 | **1.759** |

## Footnotes

- M2 baseline predates the `pinheavy` scenario's existence in the archive's protocol (added 2026-07-06), but today's backfill ran it anyway against the old GC build using the current `bench-gcperfsim.ps1`/GCPerfSim — it completed normally, so no scenario is actually missing in the final chart.
- **sharded supply (M7)** (`98517ee`) is a *second sitting* late the same evening. Cross-sitting drift was measured, not assumed: the stock anchors were re-run in that sitting (label `m7-shards-anchor`) and came back +2.7–2.9% slower (WKS geomean 2.399 → 2.465, SVR-h8 1.405 → 1.445), so charting the point raw against the earlier sitting's reference lines is conservative. Same-sitting ratio vs tuned SVR-h8: **0.849** (per-scenario 0.95/0.82/0.96/0.69 — the first full-matrix win).
