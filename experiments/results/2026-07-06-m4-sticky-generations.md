# M4: sticky generations — five iterations from 3.2× to 2.5× (2026-07-06)

M4 landed in one day but not in one shot: the first working implementation was *slower*
than no generations at all, and the road to the final numbers is a tour of what
generational collection means for a **non-moving** heap. Every step below is one commit's
worth of design change, each driven by the `GcStats` phase profiler (see
[2026-07-06-soh-profile.md](2026-07-06-soh-profile.md) for the pre-M4 baseline it
diagnosed: pauses 82% of wall, sweep-dominated).

All numbers: canonical `soh` scenario, single instrumented runs (protocol medians at the
bottom), Ryzen 9 7950X3D. Pre-M4 baseline: **8.26 s wall** (53 full GCs, steady pause
~150 ms, stock ≈ 2.8 s).

## The design

- **Sticky mark bits via epochs.** Young collections do not advance the mark epoch, so
  every object marked by an earlier collection stays "live" for free; only young
  (epoch-0) objects need discovering. Dead old objects float until a full collection.
- **Remembered set = the stock write barrier, unmodified.** `Initialize` reports
  `ephemeral_low/high` = the whole heap, so the JIT barrier dirties the destination card
  for *every* ref store. Young mark = roots + refs of sticky-marked objects overlapping
  dirty cards in old regions; all cards clear at the end of every collection.
- **Region age, three-valued** (`Old` / `Fresh` / `Reopened` — see below; the third state
  is iteration 2's lesson).
- **Young sweep**: object-level work only in Fresh/Reopened regions; Old regions get a
  metadata-only hole/class-list relink. Survivor regions promote to Old.
- **Policy**: explicit and OOM collections are always full. Budget-triggered ones are
  young until promoted-since-full ≈ live-at-last-full (heap doubled by survivors), or
  committed > 8× live (floating garbage only a full pass can see).

## The five iterations

**1. Naive sticky + cards (6.78 s, committed 10.6 GB and climbing).** The hole-first
allocator carves young objects into old regions' holes. Consequences: (a) young
constructor stores dirty *old* cards everywhere, so the card scan walked 2,000+ regions
per young GC — a whole-heap walk in disguise; (b) dead young-in-old objects are invisible
to young sweeps and the promotion trigger (~10 MB/cycle) never fires a full — the heap
leaks its own nursery.

**2. `RegionAge.Reopened` (7.30 s — worse!).** Any old region that receives allocation
becomes Reopened: card-scanned like Old (its old objects' new refs must be seen), swept
like Fresh (sticky marks keep its old survivors alive through an age-blind walk — the
mixed-age sweep is *sound for free* under sticky epochs; only the wholesale-recycle
shortcut must be gated to Fresh, since a Reopened region's LiveBytes doesn't count its
old survivors). Memory fixed, but the profiler showed the real disease: 1.4M window
handouts at ~30 KB apiece smeared the nursery across ~950 regions per cycle, each walked
**twice** (cards + sweep), plus 23.5 GB of re-zeroing (every sweep re-zeroed
already-zero plugs).

**3. Hole floor + zero-at-carve (4.66 s, zero-in-pause = 0.00 s, but committed 16 GB).**
Two structural fixes: holes below a floor are plugged but never carved (dense fresh-region
nursery, 6–16× fewer handouts), and sweeps stop zeroing entirely — the carve path zeroes
exactly the bytes it hands out, on mutator threads, cache-warm, in parallel. STW zeroing
went to literally zero. But the floor was one full window (128 KB), and this workload's
dead runs are ~49 objects ≈ 98 ± 8 KB — **central-limit-concentrated just under the
floor**. Nearly every reclaimable byte was stranded; committed exploded again.

**4. Committed-doubling full trigger (4.62 s, peak still 15.7 GB).** Full GCs every
committed-doubling sounds right until you watch one: a full sweep of a heap whose every
region holds ~20 scattered sticky survivors reclaims *nothing* — non-moving heaps cannot
pack survivors. The ladder just climbed 2→4→8→16 GB.

**5. Floor = 64 KB, trigger = 8× live, zero outside the lock (4.85 s, peak 6.5 GB).**
The 64 KB floor sits below the workload's typical gap, so holes flow again and committed
stabilizes. The 8× multiple accepts what a non-moving heap is: on hostile scatter,
committed legitimately sits at several times live (a 4× trigger degenerated every
collection to full — measured, 35 fulls out of 45). And iteration 3's zeroing had been
running *inside* the global alloc lock, serializing all four mutator threads (3.4 s of
handout time); moving it after the lock release (the window is thread-private by then)
cut handout time 3×.

## Where it lands (single stats-on runs)

| | pre-M4 | M4 final |
|---|---|---|
| wall | 8.26 s | **4.85 s** |
| total pause | 6.80 s | 3.71 s (12 full + 27 young) |
| STW zeroing | 1.98 s | **0** (moved to carve, mutator-side) |
| pause p50 | 137 ms | ~100 ms |
| window handouts | 2.02 M | 218 k |
| peak committed | ~1.35 GB | **6.5 GB** ← the price |

The footprint regression is the deliberate, documented trade of this milestone on *this
workload shape*: GCPerfSim's uniform-random replacement scatters ~20 survivors into every
2 MB region — near-adversarial for wholesale recycling and for any non-moving design.
Real workloads (request-scoped death) cluster; the ASP.NET soak holds a flat footprint.
The structural answers (size-class survivor packing, opportunistic evacuation of
near-empty regions, card-offset tables to kill the per-region card walk) are M7 material.

## Protocol medians (bench-gcperfsim.ps1, 3 iterations, same-day stock)

| Scenario | Stock (2026-07-06) | M4 | Ratio (was, at M1-complete) |
|---|---|---|---|
| soh | 2.00 s | 5.04 s | **2.52×** (3.20×) |
| lohmix | 2.23 s | 4.96 s | **2.23×** (2.77×) |
| pin | 1.98 s | 5.01 s | **2.53×** (2.72×) |

Both columns' absolute walls dropped versus the 07-05 sitting (stock 2.82 → 2.00 s) —
machine-state drift, which is why the protocol insists on same-day references and treats
ratios as the archive's currency. The in-day instrumented comparison (8.26 → 4.85 s on
identical machine state) and the ratio movement tell the same ~40% story.

## Correctness evidence

- Unit suite: 65/65 (5 new young-sweep tests incl. the wholesale-recycle-gate regression
  test — a Reopened region with zero young survivors must never be recycled).
- EE suite: 56/56 with the full M4 barrier + young paths.
- ASP.NET soak (10 min, 32 workers, commit `5db0ed9`): **14.51 M requests, 0 errors**,
  ~23.8 k req/s sustained (pre-M4 evidence was ~20 k). Working set oscillates
  1.6–1.9 GB — stable, periodically pulled down by full collections; the higher plateau
  vs the pre-M4 ~700 MB is the 8×-live policy at work, not a leak.
