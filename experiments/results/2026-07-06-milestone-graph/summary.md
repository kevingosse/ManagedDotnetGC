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

## Peak working set (GB)

Same rows/SHAs as the wall-time table above (see SHA notes for the M6/M7 label corrections). Values are the median `peak_ws_mb` per scenario (3 iterations each), geomean of the 4 scenario medians, converted to GB (`/1024`).

**This series is NOT monotonic like the wall-time chart.** M2 (first working allocator) has the *lowest* peak working set of all six points — lower than the current build. Working set then more than quadruples across M4/M5/M6 (generational through concurrent marking, ~6.9-7.0 GB) before M7 ("memory diet + tuning") cuts it back down by ~2.8x. See the full callout below.

| Milestone | soh | lohmix | pin | pinheavy | geomean (GB) |
|---|---|---|---|---|---|
| M2 baseline | 1.336 | 1.357 | 1.344 | 2.413 | **1.557** |
| M4 sticky gens | 6.410 | 6.626 | 6.372 | 8.314 | **6.887** |
| M5 parallel | 6.521 | 6.683 | 6.563 | 8.255 | **6.971** |
| M6 concurrent | 6.690 | 6.483 | 6.590 | 8.230 | **6.964** |
| M7 tuning | 2.336 | 2.077 | 2.113 | 3.866 | **2.509** |
| M6.5 sweep-assist | 2.064 | 2.121 | 2.067 | 3.877 | **2.434** |

| Stock config | soh | lohmix | pin | pinheavy | geomean (GB) |
|---|---|---|---|---|---|
| stock WKS | 1.040 | 1.351 | 1.036 | 4.271 | **1.579** |
| stock Server GC (32 heaps) | 1.153 | 1.223 | 1.171 | 4.331 | **1.635** |
| stock Server GC (8 heaps) | 2.128 | 2.021 | 2.125 | 4.219 | **2.492** |

### What's surprising here

- **M2's footprint is not high — it's the lowest of the whole series (1.56 GB), and close to stock Workstation GC's 1.58 GB.** The "first working allocator" milestone apparently ran tight (likely simple/non-generational, more frequent full collections, no reserved generational structure), so despite being by far the *slowest* build (7.67s wall time), it was not memory-hungry.
- **Going generational (M4) roughly 4.4x's the footprint** (1.56 GB -> 6.89 GB) and it stays there through M5 (parallel) and M6 (concurrent marking) — all three cluster at 6.9-7.0 GB, well above every other point in the chart including the stock GCs.
- **M7 ("memory diet + tuning") is the milestone that actually earns its name**: it cuts peak working set by ~2.8x in one step (6.96 GB -> 2.51 GB), the single largest change of any kind (wall-time or memory) in either chart.
- **M6.5 (current build) at 2.43 GB is barely below M7**, and lands almost exactly on top of stock Server GC with 8 heaps (2.49 GB) — the dashed reference line for that config passes right through the last two data points. We've matched the memory profile of an 8-heap Server GC, but we're still ~1.5x above stock Workstation GC (1.58 GB) and stock Server GC with 32 heaps (1.64 GB), even though we already beat both of those configs on wall time.
- Net effect: the wall-time chart tells a clean "monotonically improving" story; the memory chart tells a "cost of going generational, later partially repaid" story — generational/concurrent collection bought the wall-time win at a real, multi-GB memory cost that only the M7 diet pass clawed back (not all the way to M2's floor, and not down to stock WKS/Server-32h levels).
