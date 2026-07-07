# Milestone graph — computed geomeans (2026-07-07)

Every row in this file — all ten milestones (the eight from the 2026-07-06 backfill plus the two new M8 stages) and all four stock anchors — was benched TODAY (2026-07-07) in ONE sitting via git worktree + Release publish + `bench-gcperfsim.ps1`, same machine. Cross-sitting wall times are never comparable, so nothing here is reused from the 2026-07-06 backfill's numbers even where the milestone and sha are unchanged. Geomean = geometric mean of the 4 scenario medians (3 iterations each). Two metrics are tracked: wall_s (lower is better) and peak_ws_mb (lower is better; the memory companion — a wall-only view flatters memory-hungry collectors).

## SHA notes / corrections

- M2 baseline, M4 sticky gens, M5 parallel: SHAs as given (407cf59, 5db0ed9, 344ce8b) are themselves the code-landing commits — used as-is.

- **M6 concurrent**: the CSV's `m6s3-fairness` label records sha `b889921`, but that commit is a **docs-only** commit ("ROADMAP reflects M6 stages 0-2 landed") that chronologically **precedes** the actual "M6 stage 3 default-on" code (`1aedac9`, "concurrent cycles default-on; free-region decommit leaves the pauses") by ~47 minutes. Checking out `b889921` would silently drop the milestone's defining change. Backfilled against **`1aedac9`** instead.

- **M7 exchange+quantum**: likewise, the CSV's `m7-quantum` label records sha `79ce1dc` ("docs: M7 memory exchange rate"), which is an **ancestor** of `7689ab1` ("M7: kill the drain-termination Sleep(1) quantum") — i.e. it predates the quantum fix that gives the milestone its name (confirmed via `git diff --stat 79ce1dc 7689ab1`: `GCHeap.Mark.cs` and the new `GCHeap.Concurrent.cs` pre-drain only land in `7689ab1`). Both `79ce1dc` and `1aedac9`/`b889921` bench UTC timestamps predate their own commit's timestamp, consistent with this repo's habit of benching against uncommitted local edits and committing afterward with a convenience `-Sha`. Backfilled against **`7689ab1`** instead, which is the first commit that actually contains the quantum fix.

- **M6.5 sweep-assist** (found during this 2026-07-07 sitting's SHA verification pass): the CSV's `m65s2-final` label records sha `765f680` ("docs: M6.5 stage 1 results"), which is itself docs-only. Its direct parent `2a62c92` ("M6.5 stage 1: gated concurrent full-cycle sweep") is a real code commit, but stage 1 ships `DOTNET_GCConcurrentSweep` **default-off** — `bench-gcperfsim.ps1` sets no such env var, so benching either `765f680` or `2a62c92` would silently measure the old in-pause sweep, not the feature the milestone is named for. The "mutator sweep-assist" work and the default-on flip land one commit later, in `d95dddb` ("M6.5 stage 2: bitmap walks, mutator sweep-assist, concurrent sweep default-on") — same pattern as the M6/M7 corrections above. Backfilled against **`d95dddb`** instead.

- **Adaptive nursery** (`ceffbfc`) and **Vectorized bitmap skip** (`68696ff`): both verified as code-bearing commits — `git show <sha> --stat` shows `ManagedDotnetGC/GCHeap*.cs` / `GCObject.cs` changes, not just docs/results.


## Per-scenario medians (wall seconds) — milestones

| Milestone | sha used | soh | lohmix | pin | pinheavy | geomean |
|---|---|---|---|---|---|---|
| M2 baseline | `407cf59` | 7.518 | 6.951 | 7.288 | 6.439 | **7.037** |
| M4 sticky gens | `5db0ed9` | 5.076 | 5.326 | 5.010 | 4.951 | **5.089** |
| M5 parallel | `344ce8b` | 1.791 | 2.169 | 1.790 | 1.822 | **1.887** |
| M6 concurrent | `1aedac9` | 1.849 | 1.829 | 1.793 | 1.833 | **1.826** |
| M7 tuning | `7689ab1` | 1.794 | 1.693 | 1.769 | 1.716 | **1.743** |
| M6.5 sweep-assist | `d95dddb` | 1.507 | 1.425 | 1.459 | 1.477 | **1.467** |
| faster allocation (M7 mutator war) | `e65fa79` | 1.400 | 1.286 | 1.342 | 1.441 | **1.366** |
| sharded supply (M7) | `98517ee` | 1.194 | 1.165 | 1.190 | 1.103 | **1.162** |
| Adaptive nursery | `ceffbfc` | 1.228 | 1.157 | 1.204 | 1.119 | **1.176** |
| Vectorized bitmap skip | `68696ff` | 1.187 | 1.161 | 1.182 | 1.140 | **1.167** |

## Per-scenario medians (wall seconds) — stock anchors (today, fresh)

| Config | soh | lohmix | pin | pinheavy | geomean |
|---|---|---|---|---|---|
| Workstation GC | 2.200 | 2.314 | 2.067 | 2.414 | **2.245** |
| Server GC (DATAS) | 1.902 | 1.806 | 1.713 | 1.856 | **1.818** |
| Server GC (8 heaps) | 1.271 | 1.376 | 1.250 | 1.586 | **1.365** |
| Server GC (32 heaps) | 1.612 | 1.587 | 1.824 | 2.019 | **1.752** |

## Per-scenario medians (peak working-set MB) — milestones

| Milestone | sha used | soh | lohmix | pin | pinheavy | geomean |
|---|---|---|---|---|---|---|
| M2 baseline | `407cf59` | 1372.4 | 1391.2 | 1363.6 | 2438.9 | **1587.4** |
| M4 sticky gens | `5db0ed9` | 6543.9 | 6829.6 | 6544.6 | 8517.2 | **7064.9** |
| M5 parallel | `344ce8b` | 6632.7 | 6816.3 | 6776.2 | 8532.0 | **7150.2** |
| M6 concurrent | `1aedac9` | 7055.5 | 6774.9 | 6798.8 | 8443.6 | **7237.7** |
| M7 tuning | `7689ab1` | 2114.1 | 2126.0 | 2164.1 | 3988.7 | **2495.7** |
| M6.5 sweep-assist | `d95dddb` | 2097.7 | 2168.1 | 2110.9 | 3960.8 | **2483.2** |
| faster allocation (M7 mutator war) | `e65fa79` | 2360.5 | 2246.2 | 2314.1 | 4079.9 | **2659.9** |
| sharded supply (M7) | `98517ee` | 2001.6 | 2163.4 | 2056.5 | 4252.9 | **2480.7** |
| Adaptive nursery | `ceffbfc` | 2108.3 | 2201.4 | 2020.6 | 4126.0 | **2494.1** |
| Vectorized bitmap skip | `68696ff` | 2028.6 | 2175.4 | 2107.5 | 4508.3 | **2544.7** |

## Per-scenario medians (peak working-set MB) — stock anchors (today, fresh)

| Config | soh | lohmix | pin | pinheavy | geomean |
|---|---|---|---|---|---|
| Workstation GC | 1062.7 | 1378.9 | 1061.8 | 4358.4 | **1613.7** |
| Server GC (DATAS) | 1197.1 | 1264.5 | 1184.1 | 4378.7 | **1673.8** |
| Server GC (8 heaps) | 2176.8 | 2047.7 | 2177.7 | 4353.7 | **2549.7** |
| Server GC (32 heaps) | 7115.7 | 4659.6 | 7125.7 | 7644.4 | **6519.0** |

## Footnotes

- M2 baseline predates the `pinheavy` scenario's existence in the archive's protocol (added 2026-07-06), but it ran anyway against the old GC build using the current `bench-gcperfsim.ps1`/GCPerfSim — it completed normally, so no scenario is actually missing in the final chart.
