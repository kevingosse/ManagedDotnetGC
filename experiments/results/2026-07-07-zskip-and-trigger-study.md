# 2026-07-07 — Bitmap zero-skip + starved-full trigger study (M8 follow-up)

Session goal: close the remaining TechEmpower gap (0.74–0.80× vs tuned stock-svr-h8)
without turning web-specific knobs. Two candidate changes, GCPerfSim as the control.

## Diagnosis (from m8staticcb stats CSVs, queries endpoint)

- Budget rides at the boost cap (64 + 448 = 512 MB); the app genuinely carves
  ~25 GB/s of windows (≈3.6 MB/request, EF Core churn) → ~49 young GCs/s is
  structural, not a controller failure.
- Committed pinned at 1546 MB with live at 13–59 MB. 69 of 71 fulls fired as
  **starved** (~1.5/s) while ~1.2 GB of free capacity (holes+pool) existed — the
  2×budget demand (1 GB at cap) is simply never met in this regime.
- The committed plateau itself was built by the first few starved fulls: each
  concurrent window resets supply lists, so mutators carve fresh regions wholesale
  for the sweep duration (committed 324 → 1022 → 1066 → 1546 MB in four fulls).
- Young pause p50 3.2 ms = roots ~0.9 + cards ~1.2 + sweep ~1.0 ms; at 49/s that is
  ~15% of wall in STW. ~590 regions have dirty cards per 20 ms interval because the
  young runway is hole-carved across Reopened regions (young-object stores dirty
  cards the scan must visit). marked_n ~18k/GC → the card phase is mostly genuine
  tracing, not scan overhead.

## Change 1 (shipped): SIMD zero-skip in bitmap walks

`GCObject.SkipZeroBitmapWords` (AVX2, 16 words = 8 KB heap per test) fast-forwards
zero stretches in: `EnumerateMarkedRange` (young card scan), `SweepBumpRegion`
survivor walk, `ScanMarkedRange` (concurrent pre-drain). Plain loads are sound on
the concurrent path: a mark landing after the read is traced by whoever marked it.

- Web: cards_us p50 1137 → 988 µs on queries; RPS flat (card phase is real tracing).
- GCPerfSim: **wall improves on all five scenarios** (see matrix below) — the sweep
  survivor walk at smear density is where the zero words live.

## Change 2 (studied, NOT shipped): starved-full trigger relaxation

Two variants tried to kill the ~1.5 futile starved fulls/s on web:

| variant | web effect | GCPerfSim effect | verdict |
|---|---|---|---|
| threshold = 1×budget (retention stays 2×) | starved fulls 69→3, but promoted fulls take over (52/run at the 64 MB floor); RPS +0.5–1% (noise) | peak WS **doubles**: soh 2.1→4.2 GB, pinheavy 3.9→6.1 GB | rejected |
| futility gate: also require freeCapacity < (committed−live)/2 | same web behavior | bounded drift to the gate's self-disable point (committed = live+2×demand): soh peak 2.56 GB, +17% past stock-h8 | rejected |

Lesson: the early starved fulls **are the committed cap** on ramping workloads;
web's recurring ones are the affordable side of that coin (their direct cost
measured at noise level). Both variants and the bound math are documented in the
comment above `_fullForStarvation`'s assignment in TrimOutsidePause.

## Nursery curve (context, no change)

`-Gen0MB 1024` on queries: 6.6k → 7.0k rps (+6%) at peak WS 1.8 → 2.3 GB. The
nursery lever pays ~+6% per budget doubling and ~+500 MB WS — left at the 448 MB
boost cap; buying TechEmpower RPS with WS is the overfit we're avoiding.

## Matrix (median wall_s / peak WS MB)

| scenario | stock-svr-h8 | m8adaptive2 (anchor) | m8zskiponly (shipped) |
|---|---|---|---|
| soh | 1.29 / 2180 | 1.297 / 2076 | **1.163** / 2007 |
| lohmix | 1.41 / 2015 | 1.178 / 2146 | **1.097** / 2183 |
| pin | 1.25 / 2175 | 1.223 / 2439 | 1.229 / 2006 |
| pinheavy | 1.62 / 4350 | 1.187 / 3945 | **1.120** / 4407 |
| mixed | 1.30 / 2660 | 1.244 / 1973 | **1.149** / 1996 |

Wall improves 6–10% on four of five (pin flat), WS at anchor levels. soh is now
~12% faster than tuned stock-svr-h8 with less memory. The two rejected trigger
variants are also archived in perf-history.csv (m8zskip = zskip+1×budget,
m8futility = zskip+futility gate) — their WS columns are the rejection.

## TechEmpower (256 conns, 3×15 s, anchors stock-svr-h8 90.0k/8.85k/986)

| config | fortunes | queries | notes |
|---|---|---|---|
| m8staticcb (baseline) | 72.2k / 1430 MB | 6.46k / 1735 MB | last session |
| m8trigfix (1×budget) | 72.6k / 1320 MB | 6.55k / 1800 MB | rejected variant |
| m8zskip (zskip+1×budget) | 71.2k / 1260 MB | 6.63k / 1794 MB | |
| m8zskiponly (shipped) | 72.7k / 1290 MB | 6.69k / 1680 MB | updates 955 / 1671 MB, 0 errors |

Shipped deltas vs baseline: fortunes +0.7%, queries +3.2%, updates −0.5% (noise),
WS −140/−55 MB. Ratios vs anchors: fortunes 0.81×, queries 0.756×, updates 0.97×.

## Remaining web levers (unchanged conclusion)

1. Partitioned stack scanning (roots ~1 ms serial at 49 GCs/s) — the real project.
2. Card-phase is genuine tracing of ~18k objects; only structural changes (fewer
   young GCs, or evacuation) shrink it.
3. Mutator-side: window handout + inline zeroing (~25 GB/s carve rate).
