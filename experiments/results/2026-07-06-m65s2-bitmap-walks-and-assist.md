# M6.5 stage 2: bitmap walks + mutator sweep-assist — the run at Server GC

2026-07-06, sixth session. Goal set by Kevin: beat the Server GC's throughput. Commit
range: this doc's commit. Everything below is one sitting; stock anchors re-run fresh
(`svrgap` rows, moderate machine noise, medians of 3).

## 1. Where the morning stood

Fresh matrix at session start (`svrgap`), median wall seconds (peak WS MB):

| gc | soh | lohmix | pin | pinheavy |
|---|---|---|---|---|
| stock-wks | 2.205 (1064) | 2.378 (1382) | 2.099 (1063) | 2.437 (4363) |
| stock-svr (32 heaps) | 1.696 (1185) | 1.722 (1259) | 1.659 (1190) | 1.742 (4434) |
| stock-svr-h8 | 1.234 (2179) | 1.339 (2102) | 1.275 (2175) | 1.562 (4312) |
| custom (HEAD `765f680`) | 1.774 (2123) | 1.724 (2139) | 1.774 (2183) | 1.741 (3929) |
| custom + csweep knob | 1.804 | 1.692 | 1.736 | 1.759 |

Two facts drove the session:

- **The csweep knob was a bench no-op**: the M6.5 stage-1 pool gate keeps every full
  in-pause on smear-shaped heaps — exactly the scenarios in the matrix.
- **The census said the entire SVR-h8 gap was pause mass.** soh: 542 ms total STW on a
  0.54 s wall gap — young 300 ms (27 × 11.1: cards 5.7 + sweep 5.0) + full 242 ms
  (A 18 + B 224, B = sweep 11.3 avg + cards 3.0 avg). Mutator time already equaled
  SVR-h8's *entire wall*. And 56% of all pause was sweep, 37% cards — two walks whose
  cost was corpse-hopping, not live work.

## 2. The three changes

**Bitmap-guided, range-clamped card scan** (`ScanRegionCardRuns` /
`ScanMarkedRangeClamped`). The STW card scan walked dirty neighborhoods with
card-offset hops + `ComputeSize` through every dead object (survivors every ~50
objects at soh density). The card pre-drain had already proven the alternative: mark
bitmap set bits *are* the marked object starts. The STW scan now reuses that walk, plus
the M4-era follow-up the old walk never got: **enumeration clamped to the dirty run**
(`EnumerateObjectReferencesInRange`) — sound because the barrier dirties the card of
the slot address on every ref store, so slots under clean cards cannot hold anything a
remembered-set scan needs. A dirtied large array now walks only the slots under dirty
cards; spans get the same treatment per-run (the "large ref array pays full
enumeration" follow-up from M4, closed). The head-overlap search is bounded by the
region kind's max object.

**The bound that took Kestrel down.** The first version bounded the bump lookback at
`BumpMaxSize` (32 KB) — "objects above that route to the class tier." Wrong: alloc
contexts span whole 128 KB windows and **the EE's inline fast path places any object
that fits below `alloc_limit` without ever calling the GC**, so bump regions legally
hold ref-bearing objects far above the routing threshold. GCPerfSim (≤ 4 KB objects)
and the forced-full stress never allocate that shape; Kestrel does constantly
(object[] queue segments, buffer tables), and its soak died in ~15 s with two
corruption signatures (a marked object with a zeroed MethodTable in a card-scan
worker; a managed vtable dispatch landing on an UnmanagedCallersOnly entry): the head
search stopped 32 KB back, missed the big object's start, and its old→young refs were
never traced. Bisected by knob (csweep off → still dies → not the assist; old sweep
walk → still dies → not the sweep; unclamped enumeration → still dies → the
*discovery*), then found by re-reading the window handout. The bound is the window
size. Lesson recorded: **the bench suite's object-size distribution is a blind spot —
any card/bitmap-geometry change needs the soak before it needs the bench.**

**Bitmap-guided sweep** (`SweepBumpRegion`). Same insight, applied to the sweep walk:
survivors come straight off the bitmap, dead extents are the gaps between set bits,
free plugs coalesce for free (never marked). A smear region now touches ~20 live
headers instead of ~1000 corpse headers. This made the **card-offset table dead** —
the bitmap scan was its last reader — so the table, its maintenance in every sweep and
window/hole carve (inside the alloc lock), and its 2 KB/region reservation are all
deleted.

**Mutator sweep-assist; concurrent sweep ungated and default-on** (M6.5 stage 2). The
stage-1 gate existed because the sweep window's overshoot fresh-committed on smear
heaps (+1.1 GB ratchet). Now the plan is *armed* under STW at pause B
(`BeginConcurrentSweep`), and any carve that runs dry mid-sweep — bump window, class
block, or span — claims a 2-region chunk of the plan through the same cursor the
workers use, sweeps it right there under the alloc lock, and retries its supply
(`TrySweepAssist`). Overshoot drains the plan instead of committing. The gate is gone;
every full cycle sweeps off-pause; `DOTNET_GCConcurrentSweep=0` keeps the in-pause
path for A/B runs. Assist chunk size matters: at 8 regions the mutators competed with
the workers for the plan (cumulative alloc-path time +273 ms on soh); at 2 regions
they only bridge to the workers' publications (+126 ms, and the walls dropped
another 4-7%).

## 3. Measured (same sitting as §1)

soh census, before → after (bitmap walks + assist, default-on):

| slice | before | after |
|---|---|---|
| young pause avg | 11.1 ms (cards 5.7, sweep 5.0) | **5.0 ms** (cards 3.8, sweep 0.75) |
| full pause B avg | 14.9 ms (sweep 11.3 in-pause) | **2.5 ms** (sweep ~0 in-pause, walk 3.7 ms world-running) |
| total STW / run | 542 ms | **~185 ms** |
| committed equilibrium | 2242 MB | **2124 MB** (no ratchet — the stage-1 failure mode is gone) |

Final matrix (`m65s2-final`, after the window-bound fix; the earlier
`m7-bitmapwalks`/`m65s2-assist*` rows in the CSV carry the 32 KB-bound bug and read
~0-3% optimistic), median wall (peak WS):

| | soh | lohmix | pin | pinheavy |
|---|---|---|---|---|
| custom | 1.440 (2111) | 1.393 (2193) | 1.461 (2067) | 1.461 (3981) |
| vs stock-wks | **0.65** | **0.59** | **0.70** | **0.60** |
| vs stock-svr (default) | **0.85** | **0.81** | **0.88** | **0.84** |
| vs stock-svr-h8 | 1.17 | 1.04 | 1.15 | **0.94** |

Read honestly:

- **Default Server GC is beaten on every scenario, by 12-19%** — at ~1.8× its peak
  working set (2.1 vs 1.2 GB; pinheavy: 4.0 vs 4.4 GB, *below* it). One session ago
  the best case was parity.
- **The tuned SVR-h8 row is beaten on pinheavy (0.94×) and matched on lohmix
  (1.04×)** — at byte-identical peak memory (ours 2067-2193 MB, theirs 2102-2179).
  soh (1.17) and pin (1.15) remain, and their remaining gap is measured, not
  mysterious: ~115 ms of card scanning (young 94 + remark 21) plus the alloc-lock
  path (win_ms ≈ 1.71 s across 4 threads ≈ 25% of mutator time — SVR's per-heap
  contexts pay nothing comparable).
- Wall deltas vs session start: **−19/−19/−18/−16%.**

Validation (final binary): unit 70/70, suite 56/56 (Release publish); stress
(experiments/GcStress, now committed — `GC.Collect` loop vs 4 checksummed store-heavy
mutator threads crossing all three size tiers + pinned churn) clean in all three
modes — tight-loop forced fulls (11,221 fulls / 157 K verifications / 0 failures),
**young-heavy cadence** (250 ms collect interval — the mode that would have caught
the window-bound bug had it existed this morning: 119 fulls + natural youngs, 463 K
verifications, 0 failures), and knob-off legacy (10,513 fulls, 0 failures). ASP.NET
soak: §4.

## 4. Soak (the record, and the exchange rate)

2-minute ASP.NET soak, 32 workers, shipping defaults (concurrent cycles + concurrent
sweep with assist):

- **6,079,720 requests, 0 errors, ~51,100 req/s sustained** — the previous records
  were 48.8 k (stage-1 knob-on) and 47.1 k (m7-quantum defaults). **+8.5% over the
  shipping default one session ago.**
- **Young pause p50 3.3 / p99 5.4 / max 6.6 ms** (was p50 5.7 / max 26 at m7-quantum;
  stage 1 got max to 10.6). **Full pause B p50 1.9 / p99 5.4 / max 5.7 ms** (was p50
  5.5 / max 10.6). Every pause of the whole soak — young or full, 1,583 collections —
  was under 7 ms. All 182 fulls swept concurrently.
- WS ~1.86 GB flat (was 1.49 GB with csweep off, 1.95 GB with the stage-1 gate): the
  known csweep equilibrium shift, now bought with +8.5% throughput and halved pauses
  instead of stage 1's cadence halving. csweep is **default-on** on this evidence;
  `DOTNET_GCConcurrentSweep=0` restores the 1.49 GB profile if the memory matters
  more than the throughput on some deployment.

## 5. What remains against SVR-h8 (soh/pin)

1. **Young cards ~3.8 ms avg** — now real work (scattered old→young stores force
   ~900 dirty regions/collection for ~9.5 MB of survivors), not walk waste. Levers:
   single-pass-per-region bitmap×card intersection instead of per-run head searches,
   or accepting it as the remembered-set floor this workload shape costs everyone
   (SVR pays a comparable scan *plus* survivor copying).
2. **The alloc lock**: 978 K global-lock window carves per soh run (~20 KB per hole
   window). Per-thread hole caches / multi-hole handouts are the structural answer.
3. Young sweep off-pause (18.8 ms/run) and pause A (20.8 ms/run) — small, real.
4. `DOTNET_GCFullRatio` frontier: unchanged trade, still available.
