# Roadmap: a .NET GC that outperforms the stock GC (claude/experiments)

Mission (set 2026-07-05): evolve ManagedDotnetGC into a fully featured .NET GC that beats
the stock GC — ideally everywhere, acceptably on specialized workloads.

Constraints:
- Windows x64 only. Linux is a bonus, 32-bit is a non-goal.
- No runtime fork — everything through the standalone GC API.
- C# (NativeAOT). Switching any part to a native language requires a demonstrated wall.

## Why this is winnable (thesis)

The stock GC is excellent at gen0/gen1 churn and has 25 years of tuning, but it carries
structural debt we don't have to carry:

- **The LOH**: an 85,000-byte cliff from another era, with its own collection policy,
  fragmentation pathologies, and historically-off compaction.
- **Pinning**: wrecks ephemeral compaction; heavy pinners (sockets, interop) fragment the
  ephemeral segments. A non-moving GC is structurally immune — pinning costs *nothing*.
- **Multi-architecture conservatism**: it can't bet on x64-only tricks (flat address-based
  region table over a fixed reservation, the 4 free bytes at object−8, 2 TB VA profligacy).
- **Blocking gen2**: even with BGC, large heaps see foreground full compactions.
- **Incrementalism**: 25 years of accretion, two maintainers, decisions never revisited.

Honest positioning — where we fight and in what order of confidence:
1. **Pinning-heavy workloads** (servers with pinned buffers): structural win.
2. **Large-allocation churn** (the LOH band, 85 KB–few MB): structural win — no cliff.
3. **Large-heap tail latency**: win once concurrent marking lands (phase M6).
4. **Steady-state throughput on mixed workloads**: fight on even terms.
5. **Pure gen0 microbenchmark churn**: hardest fight — a copying collector never touches
   dead objects. Our counter is wholesale region recycling (below).

## Architecture bet

Region-based, size-class segregated, **non-moving**, sticky-generation mark & sweep:

- **Regions** (2 MB, aligned) carved from one huge reservation; region metadata in a flat
  array indexed by `(addr − base) >> 21` — O(1) address→region for marking, interior
  pointers, and sweep (replaces bricks-per-segment).
- **Allocation**: per-thread alloc contexts bump-allocating in fresh regions (this is the
  gen0-speed answer; the GC API hands us alloc contexts natively). Survivor space uses
  per-size-class regions with free-list/bitmap allocation, mimalloc-style sharding to
  avoid contention.
- **No LOH**: size classes up to a threshold, multi-region spans above it, one policy for
  everything. The 85 KB cliff simply doesn't exist.
- **Generational without moving** ("sticky" collection): object age = epoch stamp
  (candidate home: the x64 padding word at object−8). A young collection marks from roots
  + dirty cards and sweeps only young regions; old, previously-marked objects are
  implicitly live. Remembered set = the stock card-marking barrier with the ephemeral
  range widened to the whole heap — the card table is memory *we* allocate, so we get a
  functioning generational barrier without touching barrier code.
- **The gen0-churn answer**: a bump region whose objects all died sweeps in O(1) — flip
  it back to the free region pool wholesale, no per-object work. Typical nurseries die
  wholesale, so young collection cost ≈ copying-collector cost for the common case
  (Immix's insight, minus evacuation).
- **Pauses**: STW-parallel first (M5), then concurrent marking (M6). The COW-snapshot
  route benched in [experiments/results/2026-07-05-snapshot-primitives.md](experiments/results/2026-07-05-snapshot-primitives.md)
  was killed twice by measurement — its two-view substrate cost 2.2× end-to-end, and
  page protection turned out unsound on Windows because kernel-mediated writes fail
  syscalls instead of faulting (experiments/KernelWriteProbe) — so M6 shipped as
  BGC-style incremental update over the M4 card barrier with a card-based STW remark
  (docs/spec-m6-concurrent-mark.md), reusing the M5 tracer wholesale.
- **Container story** (the axis the GC team currently optimizes): aggressive decommit of
  free regions, cheap because region-granular.

## Phases

| # | Deliverable | Exit criterion |
|---|---|---|
| M0 ✅ | Baseline | Test suite: 36/56 pass, 20 feature-gated, 0 fail (2026-07-05) |
| M1 ✅ | Correctness backlog, allocator-independent part | EE brackets (2.1), collectible types (3.1), ref-counted handles (3.2), finalization-queue roots (3.3), frozen dependent handles (3.4), handle types 10/11 (4.x), SuppressFinalize (5.1), API stubs (6.x), write-barrier init (7), alloc accounting (8.1) — **suite 56/56 green (2026-07-05)** |
| M2 ✅ | **Region heap core** (replaces SegmentManager) | Allocation triggering (1.1), memory reuse (1.2), OOM-as-null (1.3), preemptive-mode dance (8.3) implemented *on the new allocator*; suite fully green; ASP.NET sample under 20k req/s load: 0 errors, flat footprint (soak-aspnet.cmd) |
| M3 | Benchmark harness + first honest comparison | GCPerfSim + custom scenarios (pinning server, LOH churn, cache churn, burst allocation) vs stock WKS/SVR/BGC; throughput, pause histogram, peak RSS, CPU. Published in experiments/results/ |
| M4 ◐ | Sticky generations via card table | Young collections; win or tie GCPerfSim steady-state. **Core landed 2026-07-06** (sticky epochs, whole-heap card barrier, Reopened regions, zero-at-carve): soh 3.20× → 2.52× vs same-day stock, STW zeroing eliminated. Win-or-tie still open — blocked on survivor density (non-moving heaps can't pack scattered survivors; see results/2026-07-06-m4-sticky-generations.md) and the card/sweep region walks (card-offset tables, M5/M7) |
| M5 ✅ | Parallel mark & sweep | Pause ∝ 1/cores. Sweep + card scan parallelized 2026-07-06 (GC-owned worker pool, CAS marking, EE calls confined to the GC thread); **full mark parallelized same day** (`344ce8b`: buffered roots, work-share queue, idle-quorum termination) plus a committed-trigger mute that killed a full-GC storm — soh total pause 0.98 → 0.55 s, **vs stock WKS 0.70–0.83× across the suite**. Remaining serial floor: EE-side `GcScanRoots` enumeration (no per-thread partitioning in the standalone API) |
| M6 ✅ | Concurrent marking (incremental update; the COW-snapshot plan died twice on measurement — see docs/spec-m6-concurrent-mark.md §2) | Root pause at young-pause scale on multi-GB heaps — **met on every scenario: pause A 0.8–4.1 ms** (young p50 13–26 ms). All stages landed 2026-07-06: side mark bitmap, two-pause cycles, concurrent mark closure on the M5 pool, **default-on via stock `gcConcurrent`** after four-scenario histograms + soak. Stage 3's apportionment found `TrimPool` decommit was 70% of pause B (46 ms on the ASP.NET soak) *and* the young-pause tail; moved outside the pause → **server full GCs: A 2.6 ms + B 19 ms** (was one 66–90 ms STW block), young p99 17.7 → 10 ms. Follow-ups (spec §9): remark-drain buffer-time mark skip (13.5 ms), M6.5 concurrent sweep (results/2026-07-06-m6-stage3-default-on.md) |
| M7 ◐ | Tuning war | Beat stock on target workloads 1–3; publish reproducible results. **Matrix rerun post-M6 (`m6s3-fairness`, 2026-07-06)**: beats WKS on all four (0.74–0.86×), even with default SVR on lohmix (1.01×) and within 9–11% elsewhere, beats SVR+BGC on pinheavy (0.88×); the tuned SVR-h8 row still leads everywhere but the gap narrowed to 1.17–1.50× (was 1.14–1.71×). **Memory exchange rate landed (`m7-exchange`, same day, `61a51e1`+`f4d5528`)**: the census found committed equilibrium ≈ survivors × linking floor — a 4 KB full-sweep floor, a free-capacity starvation trigger, and pool retention = trigger demand took uncapped peak from 6.7–8.4 GB to **2.1–2.3 GB (= tuned SVR-h8's footprint) / 4.1 GB pinheavy (below every stock config)**, still beating WKS everywhere (0.73–0.97×, ~5–10 points given back on soh/pin), and set an ASP.NET soak record: 46.5–47.5 k req/s at a flat 1.5 GB WS (was 45.2 k at 1.6–2.1 GB), pause B max 20.9 ms (results/2026-07-06-m7-memory-exchange-rate.md). **Drain-termination quantum killed (`m7-quantum`, same day)**: the 13–15 ms pause-B "drain2" was one `Thread.Sleep(1)` timer quantum in the mark-drain idle spin, not marking (15 ms to mark one object); `SpinOnce(-1)` cut it to 20–60 µs → **0.84/0.90/0.91/0.67× vs same-sitting WKS** (give-back reclaimed) at the same footprint, and the soak's full pauses fell to young-pause scale: **B p50 5.5 / p99 9.1 / max 10.6 ms** (was 19/20.9) at a record-equal 47.1 k req/s, 0 errors. The concurrent card pre-drain built for that slice (§9: pause-A plan, fenced card consume, bitmap-guided scan) proved sound under forced-full stress but net-negative on store-heavy windows — shipped default-off behind `DOTNET_GCCardPreDrain` (results/2026-07-06-m7-quantum-and-predrain.md). **M6.5 stage 1 landed (same day)**: full-cycle sweep off-pause behind `DOTNET_GCConcurrentSweep`, gated on pool float (docs/spec-m65-concurrent-sweep.md) — soak record 48.8 k req/s with **full pause B p50 3.1 ms** (concurrent-swept cycles 2–3 ms) and young max 26 → 10.6 ms at +0.5 GB WS (overshoot→holes→halved full cadence; default-off pending that exchange-rate call, results/2026-07-06-m65-concurrent-sweep.md); soh gated to byte-identical legacy behavior; `GcAwareLock`'s early Sleep(1) also killed (third quantum site). Open: the csweep default call, `DOTNET_GCFullRatio` frontier curve, young-pause p50 (cards 6.4 + sweep 6.0 ms of 12.9 on soh — mutator sweep-assist is the shared endgame), HasFinalizerRun RMW. **M6.5 stage 2 + the bitmap walks landed same day (sixth session, results/2026-07-06-m65s2-bitmap-walks-and-assist.md): STW card scan and sweep rewritten on the mark bitmap (card-offset table deleted; enumeration clamped to dirty runs), mutator sweep-assist ungated the concurrent sweep and flipped it default-on (no smear ratchet: soh committed unchanged knob-on). soh total STW 542 → ~185 ms/run; `m65s2-final` matrix: 0.65-0.70× WKS, 0.81-0.88× default SVR — beats default Server GC on all four scenarios — and 1.17/1.04/1.15/0.94× vs tuned SVR-h8 at equal ~2.1 GB peak (pinheavy is the first tuned-config win; soh/pin gap = young cards ~3.8 ms avg + the global alloc lock ≈ 25% of mutator time). Soak record 51.1 k req/s, 0 err, every pause < 7 ms (young p50 3.3, full B p50 1.9), WS 1.86 GB (=0 knob restores 1.49). Incident logged: 32 KB card-scan lookback bound corrupted Kestrel (EE fast path fills 128 KB windows past BumpMaxSize; bound = WindowSize) — GCPerfSim's ≤ 4 KB objects are a structural blind spot, soak-before-bench for geometry changes; GcStress (three modes incl. young-heavy) committed to experiments/. Open now: young cards (single-pass bitmap×card intersect), per-thread hole caches vs the alloc lock, young sweep off-pause, pause A, FullRatio curve, HasFinalizerRun RMW. **M7 mutator war landed (seventh session, 2026-07-06 evening, results/2026-07-06-m7-mutator-war.md)**: the census split win_ms into wait/carve/zero (zeroing 61%) and three rocks landed same-day — **NT-store zeroing** (`Zeroing.cs` + `IlcInstructionSet x86-64-v3` after discovering NativeAOT compile-time-folds `IsSupported`; 10.8 → 34 GB/s, pre-zeroed windows 48 → 88%), the **young-card forward-carry scan** (bitmap words read ≤ once per region scan, AVX2 run detection; soak record 52.6–54.0k rps), and the **per-thread window stash** (batched hole-only carves riding `gc_reserved_1`, epoch-forfeited at every sweep; wait_ms 384 → 90, two scars: stash extents must be plugged for interior-pointer walks, refills must be hole-only or lohmix ratchets). Same-sitting matrix: **0.56–0.61× WKS, 0.60–0.71× default SVR (DATAS), and vs tuned SVR-h8 1.06/0.95/1.10/0.94 — lohmix and pinheavy now BEAT the tuned config at its own footprint** (2.1–2.2 GB; pinheavy both axes). Whole-evening soh −13.5% at equal footprint. **Sharded supply landed (eighth session, same evening, results/2026-07-06-m7-sharded-supply.md)**: N per-thread supply shards (default min(cores,16), `DOTNET_GCAllocShards`) with private hole/class lists + own active bumps, fed by a global *reservoir* all sweeps splice into and shards pull 4-region batches from on demand — two designs died on the soak first (rotating splices decouple supply from demand: +640 MB; sealing abandoned actives at pause B was required: 16 stranded tails/full = 846 MB) — soh census wait 59 → **8 ms**, win_ms 248 → **144 ms/run** (arc: 1878 → 144), knob A/B soh −9.9% / pinheavy −13.1%, and the same-sitting matrix vs tuned SVR-h8 went **0.95/0.82/0.96/0.69 — the first full-matrix win over the tuned config**, with soh/pin peaks *below* h8's. Open: lohmix peak bimodality (+0–27% on ratcheted iterations, mechanism unknown), soak WS premium +310 MB at 60 threads (shard-count dial trades it), milestone-chart same-sitting backfill, young sweep off-pause, pause A, FullRatio curve, HasFinalizerRun RMW |

| M8 ✅ | **Adaptive nursery + web-workload war** (TechEmpower vs tuned stock-svr-h8) | Young-budget boost for tiny-live/huge-alloc heaps (fortunes 0.43→0.80×, queries 0.33→0.75×, 2026-07-07); M8.2 partitioned stack scanning (roots p50 −70%); M8.3 per-hole zeroed markers (background zeroer's work no longer forfeited by the sweep) |
| M9 ✅ | **Slow-path bump-serve** — the suspension-frequency mechanism | Always-slow-path allocations (finalizable, ~1.45/req) forfeited a ~113 KB window per ~70-byte object: 97% of handouts discarded a fitting remainder, carve+zero ~11 GB/s vs a true 552 MB/s. Serving the slow path from the live context + controller/demand recalibration (absolute-survivor-mass gates, boost retains ×1) took young GCs 1625→131/run: **fortunes 0.94×, queries 0.97× at WS below stock, updates 0.98× vs same-sitting tuned stock-svr-h8** (6b6b106, results/2026-07-07-m9-slowpath-bump-serve.md) |

Sequencing note: missing-features items 1.1/1.2/1.3/8.3 are deliberately pulled *out* of
M1 and *into* M2 — implementing memory reuse and triggering on the placeholder
SegmentManager is throwaway work. The rest of M1 doesn't depend on the allocator.

Out of scope, per the inventory's own rulings: 32-bit alignment (8.2), Android bridge
(9.2), macOS ObjC (4.4), legacy handle types for pre-.NET-10 EEs (4.3).

## Risks

- **NativeAOT tax**: UnmanagedCallersOnly transition on every GC↔EE crossing; managed
  codegen quality in the mark loop. Mitigation: fewer/larger alloc-context handouts, raw
  pointers over spans in hot loops, measure before blaming — switching languages needs
  proof of a wall, not vibes.
- **Card-table semantics with widened ephemeral range**: needs validation against every
  barrier flavor the runtime can stomp in (incl. bulk-copy `SetCardsAfterBulkCopy` paths).
- **Non-moving fragmentation**: bounded (Robson) but real; size-class design and region
  recycling policy carry the burden the compactor carries in the stock GC.
- **The suite is necessary, not sufficient**: M2's exit also needs real apps (ASP.NET
  sample, this repo's own build tooling) surviving under the GC for hours.
