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
| M2 baseline | `407cf59` | 8.044 | 7.639 | 8.092 | 6.970 | **7.673** |
| M4 sticky gens | `5db0ed9` | 5.356 | 5.412 | 5.524 | 5.229 | **5.379** |
| M5 parallel | `344ce8b` | 1.962 | 2.302 | 2.034 | 2.135 | **2.104** |
| M6 concurrent | `1aedac9` | 1.942 | 1.892 | 1.972 | 1.901 | **1.926** |
| M7 tuning | `7689ab1` | 1.754 | 1.774 | 1.832 | 1.816 | **1.794** |
| M6.5 sweep-assist | `765f680` | 1.440 | 1.393 | 1.461 | 1.461 | **1.438** |

## Stock reference medians (today's `svrgap` rows, sha 765f680)

| Config | soh | lohmix | pin | pinheavy | geomean |
|---|---|---|---|---|---|
| stock WKS | 2.205 | 2.378 | 2.099 | 2.437 | **2.276** |
| stock Server GC | 1.696 | 1.722 | 1.659 | 1.742 | **1.704** |
| stock Server GC (8 heaps) | 1.234 | 1.339 | 1.275 | 1.562 | **1.347** |

## Footnotes

- M2 baseline predates the `pinheavy` scenario's existence in the archive's protocol (added 2026-07-06), but today's backfill ran it anyway against the old GC build using the current `bench-gcperfsim.ps1`/GCPerfSim — it completed normally, so no scenario is actually missing in the final chart.
