# The drain-termination quantum, and why the card pre-drain shipped off (2026-07-06)

**TL;DR.** The 13–15 ms "drain2" slice at the top of every pause B — the slice the
concurrent card pre-drain (spec-m6 §9) was built to reclaim — was not marking work.
It was one Windows timer quantum: idle workers in `ParallelDrainMark`'s idle-quorum
termination used `SpinWait.SpinOnce()`, which escalates to `Thread.Sleep(1)` (~15.6 ms
at default timer resolution), and any worker idle for more than ~50 µs slept through
the join. `SpinOnce(sleep1Threshold: -1)` — spin/yield, never Sleep(1) — cut drain2
from 15 ms to 20–60 µs and full-cycle pause B on soh from 24–41 ms to 9–22 ms. The
pre-drain itself, built and stress-proven first, turned out to be a net loss once the
quantum was gone; it shipped **default-off** behind `DOTNET_GCCardPreDrain`.

## 1. How the quantum was found

The M7 handoff put "concurrent card pre-drain" at the top of the value list: drain2
(the remark drain over re-buffered roots) measured 13–15 ms on the soak and scaled
with window allocation, so it read as genuine marking of window-era objects. The
pre-drain was implemented (§3 below), and its first censused soh run produced this
anomaly (`predrain_cards` = cards consumed concurrently, before pause B):

| gc | drain2_us | cards at pause B (us) | predrain_cards | remark-marked objects |
|----|-----------|----------------------|----------------|----------------------|
| 18 | 15,609    | 79                   | 0              | **1**                |

15.6 ms of "drain" to mark **one object**, on a cycle with zero dirty cards. The
baseline run (pre-drain off) showed the same fingerprint on every cycle that marked
almost nothing at pause B: drain2 pinned at 14.4–15.6 ms regardless of whether the
remark marked 1 object or 101,564. A constant ~15.6 ms independent of work is a
Windows timer quantum, and the drain termination protocol contains exactly one
primitive that can sleep: `SpinWait.SpinOnce()`, which after a few dozen spins
escalates to `Thread.Sleep(1)`. A worker that runs dry while the last worker still
drains reaches the sleep within ~50 µs; the pool join then waits for it to wake.

Every buffered-root drain paid this: pause B's remark drain (always), the M5 inline
full mark's drain (when concurrent cycles are off), and each concurrent pre-drain
pass (stretching the window by a quantum per pass).

**Fix**: `spin.SpinOnce(sleep1Threshold: -1)` in the termination loop — spins, then
alternates `Yield`/`Sleep(0)`, never `Sleep(1)`. Idle workers still surrender their
core to runnable mutator threads during a concurrent window; they just never nap
through the join. One line.

## 2. Measured effect of the fix (GCPerfSim soh, 4 threads, 20 GB allocated)

Full-cycle pause B interior, before → after (pre-drain off in both):

| slice       | before (µs)      | after (µs)      |
|-------------|------------------|-----------------|
| rescan      | 267–498          | 129–352         |
| **drain2**  | **13,738–15,590**| **19–59**       |
| cards       | 56–9,642         | 16–8,065        |
| **pause B** | **24,012–41,362**| **9,292–22,639**|

Wall clock, 3-iteration medians vs same-sitting stock WKS (`m7-quantum` rows,
perf-history.csv):

| scenario | custom | stock-wks | ratio | m7-exchange ratio (previous) |
|----------|--------|-----------|-------|------------------------------|
| soh      | 2.058  | 2.455     | **0.84×** | ~0.97 |
| lohmix   | 2.360  | 2.616     | **0.90×** | ~0.95 |
| pin      | 2.198  | 2.428     | **0.91×** | ~0.97 |
| pinheavy | 2.107  | 3.134     | **0.67×** | ~0.73 |

The soh/pin throughput given back by the M7 memory-exchange work is fully reclaimed
(the quantum was being paid 10–17 times per run at the new full cadence). Memory
stays at the M7 equilibrium: peak WS 2.1–2.2 GB on soh/lohmix/pin, 3.9 GB on
pinheavy (still below every stock config there). Machine noise caveat: single
sitting, iters spread up to ±20% on two scenarios; medians quoted.

## 3. The card pre-drain: built, proven sound, shipped off

The mechanism (all landed, behind `DOTNET_GCCardPreDrain`):

- **Pause-A plan**: a snapshot of each region's kind taken under suspension
  (-1 skip / 0 bump+size-class / k span-of-k). The concurrent scan trusts only this —
  entries of regions carved *during* the window can be mid-publication when a worker
  reads them (`SpanCount` shares a union offset), and a consumed card must always be
  paired with a scan of its objects, so window-born regions keep their cards for
  pause B.
- **Consume protocol**: per region, snapshot the dirty card bytes, clear them
  byte-granular (a word-wide clear would wipe a card the barrier dirtied between read
  and write-back), then a full fence. The fence closes the x64 store→load hole: heap
  reads may otherwise pass the card clear, see pre-store values, and the racing
  store's card — overwritten by the clear — would be the mutation's only record.
  Any store whose dirty lands after the clear re-dirties the card for pause B; any
  store consumed was visible before the fence, so the post-fence scan sees its value.
- **Bitmap-guided scan**: the pause-path region walks (card-offset hop + linear
  `ComputeSize` hops) are unsound with mutators allocating — bump cursors advance and
  hole carves rewrite plug headers mid-walk. Set bits in the side mark bitmap are
  exactly marked-object starts, and marked objects are published and size-stable, so
  the scan enumerates set bits over each dirty run plus one backward search for a
  marked object straddling the run head. This works uniformly for bump and
  size-class regions; spans keep the whole-object treatment.
- **Bounded passes** after the window drain: repeat while a pass consumes ≥ 64 cards,
  max 4 passes.

Stress (tight `GC.Collect` loop racing 4 store-heavy mutator threads with checksummed
nodes, 60 s per mode): pre-drain off — 135 M ops, 429 forced full cycles, clean;
pre-drain on — 427 M ops, 65 full cycles, clean.

Why it shipped off (soh A/B, quantum already fixed):

| metric | pre-drain ON | pre-drain OFF |
|--------|--------------|---------------|
| window | 25–124 ms (passes inside) | 5–36 ms |
| cards at pause B | 16 µs – 18.3 ms | 16 µs – 8.1 ms |
| pause B | 8.5–39 ms | 9–23 ms |
| wall   | 2.08 s | 2.12 s |

On a store-heavy workload the passes cannot converge — each pass consumes roughly
what the previous pass's duration let mutators re-dirty — so the window stretches
3–5×, window-era allocation grows with it, and the pause-B remark set gets *bigger*
than just letting the (parallel, card-offset-guided, STW) scan handle it. The stress
run shows the same shape: forced cycles ran ~7× longer with passes on. With the
quantum gone, the honest remark card cost is 3–6 ms on the densest scenario — there
is nothing worth reclaiming at these heap sizes. The trade could reverse on a
huge-heap/low-store server profile; the knob and the machinery stay.

## 4. ASP.NET soak (300 s, 32 workers, shipping defaults)

14,130,263 requests, **0 errors**, ~47.1 k req/s average (the record range), server
WS flat 1481→1490 MB. 666 full cycles (18% of 3,627 collections), all concurrent:

|          | p50    | p90    | p99    | max        |
|----------|--------|--------|--------|------------|
| pause A  | 2.4 ms | 3.4 ms | —      | 7.0 ms     |
| pause B  | 5.5 ms | 7.2 ms | 9.1 ms | **10.6 ms**|
| young    | 5.7 ms | 7.5 ms | 9.5 ms | 26.1 ms    |

Pause B was ~19 ms typical / 20.9 ms max the previous session — the quantum was more
than half of every full pause on the server. Slice averages now: rescan 1.4 ms,
drain2 93 µs, cards 0.9 ms, sweep 3.1 ms. **A full collection's pauses now sit at
young-pause scale on the server workload** (full-B p50 5.5 ms vs young p50 5.7 ms) —
the M6 pause goal extended from pause A to the whole cycle.

## 5. Follow-ups

- The `GcAwareLock` slow path also contains a `Thread.Sleep(1)` backoff (every 32nd
  spin) — mutator-side allocation tail latency may carry the same quantum; worth a
  histogram pass some session.
- M6.5 concurrent sweep is now clearly the top pause-B slice on GCPerfSim-density
  heaps (4–15 ms, avg 11, of the 9–23 ms pause B); the soak's sweep is ~3 ms.
- Unit-test fallout from M7's 4 KB full-sweep floor fixed (4 stale expectations in
  RegionAllocatorTests encoded the old 64 KB floor against full sweeps).
