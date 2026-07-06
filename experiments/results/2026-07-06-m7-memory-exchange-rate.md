# M7: the memory exchange rate — census, the linking-floor law, and the starvation trigger

**Date**: 2026-07-06 (third session of the day) · **Commits**: `61a51e1` (floor +
trigger + remark skip), `f4d5528` (retention = demand) · **Protocol**:
`bench-gcperfsim.ps1`, labels `m7-exchange` / `m7-exchange2` in perf-history.csv.

The top M7 item after M6 shipped: uncapped peak working set was 6.6–8.5 GB against
stock's 1.1–4.4 GB on the GCPerfSim scenarios (0.5–1 GB live). This session found the
mechanism, derived the law that governs it, and moved the default to stock-tuned memory
at a bounded throughput cost — while setting a new throughput record on the ASP.NET
soak.

## 1. Census: where 13× live actually went

New per-collection stats columns (`pool_mb`, bump-region counts by age, linked-hole
bytes, tail bytes, class/span occupancy, budget, full-trigger reason) attributed the soh
peak in one run:

- **No bump region is ever wholly dead.** `pool_mb` read 0.0 on every row: ~2% survival
  smeared uniformly leaves ~20 survivors in every 2 MB region, so the wholesale-recycle
  path never fires and no full collection can ever decommit anything. The heap's only
  recycling currency is holes.
- **Young sweeps run a chronic hole deficit.** Each young cycle relinked ~400–500 MB of
  holes against a ~520–590 MB budget. The deficit is sub-floor stranding: a ~100 KB hole
  that gets refilled and receives even one survivor splits into two strands below the
  64 KB linking floor (`Region.MinLinkedHole`). The deficit — ~200 MB/cycle — was
  exactly the observed committed growth.
- **Fulls ran far too late and could not recover the strands anyway.** The old triggers
  (promotion-doubling, committed ≥ 8× live with a self-muting ratchet) let committed
  reach 4.9 GB before the first steady-state full — which then relinked only 2.2 GB of
  the ~6.4 GB dead, *freeing zero regions*, because at that smear density (~250k
  survivors over 3460 regions) the mean fully-coalesced survivor gap is **~26 KB —
  below the 64 KB floor**. The muted trigger then re-armed one budget higher each time:
  4.9 → 5.9 → 6.6 GB. The "peak" was just wherever the run ended.

## 2. The law: committed equilibrium ≈ survivor count × (floor + object size)

The heap grows until the mean survivor gap clears the linking floor — only then does
hole supply meet allocation demand. Measured on soh (250k live objects, ~2 KB mean):

| full-sweep linking floor | predicted equilibrium | observed |
|---|---|---|
| 64 KB (old behavior) | ~16 GB | still ratcheting at 6.9 GB when the run ended |
| 16 KB | ~4.5 GB | converging on 4.5 GB (flat stretches between fulls) |
| 4 KB | ~1.5–2 GB + working headroom | **2.3–2.6 GB, converged and flat** |

The floor is the memory dial. It went to 4 KB for **full sweeps only**
(`Region.FullMinLinkedHole`, matching the existing hard-limit pressure floor); young
sweeps keep the 64 KB floor so the hot handout path keeps fat windows. Fulls are the
recovery engine: their strands are unreachable until the *next* full, while a young
sweep's get another chance every cycle.

## 3. The starvation trigger: collect strands instead of committing fresh

With recovery solved, the trigger had to fire fulls at the pace the allocator actually
drains holes. Iterations, each measured with the census:

1. **Starvation trigger alone** (full when post-trim free capacity — pool + linked
   holes — drops under one budget, gated on committed ≥ 250% of live): fired correctly
   but each full recovered only ~650 MB at the 64 KB floor and the concurrent window's
   allocate-through ran on fresh commits. Same ratchet slope, more collections.
2. **+ 16 KB, then 4 KB floor**: recovery became near-total (post-full unaccounted
   space ~135 MB), committed perfectly flat between fulls — but still stepped up at
   every full-cycle row: the trigger fired with holes exhausted, so the pre-trigger
   runway tail and the ~35 ms concurrent window (~300+ MB at GCPerfSim's allocation
   rate) still committed fresh regions.
3. **Two budgets of headroom** (`free capacity < 2 × budget`): one budget for the
   runway that trips the next collection, one for the window's allocate-through.
   Committed converged: soh flat at ~2.6 GB through the end of the run, fulls every
   ~3rd collection, `starved` on every full-trigger reason as designed.
4. **Retention = demand** (`f4d5528`): the first soak exposed a self-contradiction —
   the trim retained *one* budget of pool slack against the trigger's *two*-budget
   demand, so on wholesale-recycling workloads (the server: pool-fed, hole-poor) every
   trim manufactured starvation: **45% of all collections ran as starved fulls**.
   GCPerfSim never sees it (smeared heaps keep the pool empty). One number now feeds
   both. Soak fulls fell to 21%, pause B max 36.6 → 20.9 ms, and throughput set a
   record (§6).

A full that fails to restock the headroom mutes the trigger until committed grows a
budget past that point (hostile-scatter storm guard, same shape as the old mute).
`DOTNET_GCFullRatio` (percent of live estimate, default 250, **hex** like every config
int) gates how bloated the heap must be before starvation fulls engage.

## 4. Remark-drain buffer-time mark skip (SPEC-M6 §9 item, landed here)

The extra full cadence made pause B's cost matter more, so the queued §9 item landed:
the remark's root re-scan checks the mark bitmap at buffer time and drops
already-marked roots — marked-at-remark means fully traced (the window drain terminated
on a global idle quorum), and window-era stores are the card remark's job either way.
Remark buffering dropped to ~0.4 ms. The remaining `drain2` (~15 ms on GCPerfSim, 13.3
ms on the soak) is *genuine* marking of objects allocated during the window — that's
the concurrent card pre-drain's job (§9, still queued), not queue overhead. Pause A
stays a dumb push by design.

## 5. Matrix (`m7-exchange` + `m7-exchange2` pooled)

The box ran noisy this sitting: per-scenario stock-wks medians swung both directions
between two anchor runs 30 minutes apart (pin 2.28 → 2.50, lohmix 2.72 → 2.51), and an
interleaved A/B of the two custom builds on soh showed them within ~2% (sim-reported
2.10/2.15 vs 2.26/2.12). So custom and wks rows below pool six iterations across both
runs; svr/bgc/h8 rows are the three-iteration 15:40 anchors (±15% variance on
svr/bgc rows, as every sitting).

Median wall seconds (median peak WS MB):

| gc | soh | lohmix | pin | pinheavy |
|---|---|---|---|---|
| custom | 2.26 (2129) | 2.12 (2258) | 2.23 (2190) | 2.03 (4086) |
| stock-wks | 2.34 (1058) | 2.64 (1380) | 2.42 (1061) | 2.79 (4317) |
| stock-svr | 1.73 (1192) | 1.97 (1405) | 1.64 (1180) | 1.73 (4405) |
| stock-svr-bgc | 1.52 (1430) | 1.61 (1451) | 1.47 (1450) | 1.73 (4241) |
| stock-svr-h8 | 1.29 (2181) | 1.41 (2014) | 1.25 (2175) | 1.62 (4333) |

Custom wall as a multiple of each reference (m6s3-fairness value in parentheses):

| | soh | lohmix | pin | pinheavy |
|---|---|---|---|---|
| vs stock-wks | **0.97** (0.86) | **0.80** (0.74) | **0.92** (0.84) | **0.73** (0.77) |
| vs stock-svr | 1.31 (1.10) | 1.08 (1.01) | 1.36 (1.11) | 1.17 (1.09) |
| vs stock-svr-bgc | 1.49 (1.43) | 1.32 (1.05) | 1.52 (1.28) | 1.17 (0.88) |
| vs stock-svr-h8 | 1.75 (1.47) | 1.50 (1.26) | 1.78 (1.50) | 1.25 (1.17) |

Readings:

- **Peak memory: 6.7–6.8 GB → 2.1–2.3 GB on the 0.5 GB-live scenarios (3.1×), 8.4 →
  4.1 GB on pinheavy (2.1×).** Custom now sits at 0.94–1.12× of *tuned SVR-h8's*
  footprint on every scenario and below every stock config on pinheavy. The uncapped
  default now occupies the memory point the fairness matrix previously needed
  `-cap2200m`/`-cap4400m` for — at a better wall than either cap row ever achieved
  (soh: 2.26 uncapped vs 2.66 capped at the same 2.2 GB).
- **Still beats WKS on all four scenarios** (0.73–0.97×) at ≤ 2.1× its memory (was
  ~6.3×). soh is the narrow one; pinheavy widened.
- **The throughput give-back is real but bounded**: the WKS margin on soh/pin shrank
  by roughly 5–10 points (noise makes the exact figure mushy — the pooled soh ratio is
  0.97 against 0.92/1.02 in the two individual pairings). Cost decomposition: ~13–15
  starved fulls per GCPerfSim run, each paying pause B ≈ 33 ms (drain2 15 + sweep 13 +
  cards 5) plus window CPU. Both big slices have queued fixes (concurrent card
  pre-drain, M6.5 concurrent sweep) — the exchange rate should keep improving without
  giving the memory back.
- Young pauses *improved* at the smaller heap (soh avg 12.0 ms vs 13–15 baseline);
  pause A unchanged (~1 ms).

## 6. ASP.NET soak (final build, 300 s, 32 workers)

| | m6s3 (pre-exchange) | first exchange soak (`61a51e1`) | final (`f4d5528`) |
|---|---|---|---|
| req/s steady | ~45.2 k | ~43.4 k | **46.5–47.5 k (record)** |
| errors | 0 | 0 | 0 (13.9 M reqs) |
| server WS | 1.6–2.1 GB | 1.17–1.33 GB | **flat 1.50 GB** (p50 = max committed 1408 MB) |
| full cadence | — | 45% of collections (retention bug) | 21% |
| pause B p50/p99/max | 19 / — / — ms | 18.4 / 20.4 / 36.6 ms | **18.4 / 20.0 / 20.9 ms** |
| young p50/p99/max | 6 / 10 / 12.7 ms | 5.7 / 8.7 / 11.9 | 5.6 / 8.9 / 11.7 ms |

The record throughput at −25% working set with a tighter pause-B tail is the
retention-fix story: the pool now holds the whole working headroom, so the server
recycles wholesale-dead regions instead of running starved fulls or committing fresh.

## 7. Follow-ups, in value order

1. **Concurrent card pre-drain** (§9): drain2 is now the largest pause-B slice and
   scales with window allocation; pre-draining dirty cards during the window would cut
   most of it on store-heavy workloads.
2. **M6.5 concurrent sweep**: 13 ms of every pause B at the new cadence.
3. **`DOTNET_GCFullRatio` sweep**: the wall/memory frontier is now a knob; publish the
   curve (250% default vs 400–800% for throughput-first deployments).
4. Young-pause p50 (roots+cards+sweep) — unchanged priority from m6s3.

Raw per-collection CSVs archived in
[2026-07-06-m7-exchange-stats.zip](2026-07-06-m7-exchange-stats.zip): `census-soh-v1..v6`
(the iteration story of §1–§3), `stats-soak-exchange{,2}.csv` (§6).
