# Pinning: the first structural win (2026-07-06)

ROADMAP fight #1 is pinning-heavy workloads: a non-moving GC is structurally immune to
pinning, a moving GC is not. The old `pin` microbench (`-sohpi 100`, 1% of survivors)
never showed it — this session designed the shape that does, and it produced the
project's first "stock loses outright" result.

## The scenario

Rotating long-lived pinned survivors amid churn — the async-socket-buffer pattern:

- `pinheavy` (now canonical in bench-gcperfsim.ps1): `-tc 4 -tagb 20 -tlgb 1 -sohsi 50
  -sohsr 100-4000 -sohpi 10` — 10% of a 1 GB live set pinned, pins live as long as their
  objects (many gen0 lifetimes).
- `pin-half` (exploration): same with `-sohpi 2` — 50% of survivors pinned.

## Results (single runs, same sitting; protocol rows for pinheavy in perf-history.csv)

**Uncapped, `pin-half`** — pinning cost to each collector, against its own no-pin run:

| | wall | peak WS | final heap | GC counts |
|---|---|---|---|---|
| stock, no pins | 2.41 s | 1.77 GB | 1.68 GB | 456/107/4 |
| stock, pin-half | 2.64 s (+10%) | **7.67 GB** | **7.45 GB (4.4×)** | 491/**486**/4 |
| ManagedDotnetGC, no pins | 4.69 s | 8.5 GB | 1.39 GB | 22 |
| ManagedDotnetGC, pin-half | 4.81 s (+2.5%) | 8.5 GB | **1.39 GB (unchanged to the MB)** | 22 |

Pins wreck stock's ephemeral compaction: gen1 collections explode 107 → 486 and the heap
settles at **4.4× its no-pin size, permanently** — fragmentation a moving collector can
never squeeze out while the pin population rotates. Our collector's numbers are
*identical with and without pins* because pinning is a no-op for a non-moving design:
a pinned handle is scanned like any strong handle and nothing else changes.

(Our high uncapped *peak* is the 8×-live full-GC trigger — a policy knob, equal in both
rows. Stock's bloat is structural, not tunable.)

**Capped at 4 GB (`DOTNET_GCHeapHardLimit=0x100000000`), `pin-half`** — the container
question:

| | outcome |
|---|---|
| stock | **OutOfMemoryException** after 8.7 s of thrashing (needs 7.4 GB) |
| ManagedDotnetGC | **completes**: 5.60 s wall, peak WS 3.7 GB, final heap 0.52 GB |

## The fix that made the capped run survive: the adaptive hole floor

Our first capped attempt *also* OOMed: the M4 64 KB linked-hole floor strands sub-window
holes until a full collection, so on scattered survivors the heap could not run below
~3× live. Under memory pressure that is the wrong trade — so the floor is now adaptive:
within an eighth of the hard limit, sweeps link every hole down to 4 KB (handout cadence
degrades, the heap densifies) and budget collections go full (young ones cannot reclaim
floating garbage). With that, both capped runs (pins and no pins) complete comfortably.

## Honest framing

- On raw wall clock stock still wins this scenario uncapped (2.6 s vs 4.8 s ≈ 1.8×) —
  the generic throughput gap (see M4 results) dominates until the memory ceiling bites.
- The claim this measures: **under heavy pinning, stock trades ~4.4× memory for its
  speed and dies where memory is bounded; a non-moving collector pays zero for pins.**
  That is exactly the structural-win thesis, now with numbers.
- Next escalations for the article: longer runs (fragmentation compounds), a
  latency-oriented variant (stock's 486 gen1s vs our 22 collections), and stock
  Server/BGC flavors for fairness.
