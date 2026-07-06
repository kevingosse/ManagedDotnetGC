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
| M6 ◐ | Concurrent marking (incremental update; the COW-snapshot plan died twice on measurement — see docs/spec-m6-concurrent-mark.md §2) | Root pause at young-pause scale on multi-GB heaps. **Stages 0–2 landed 2026-07-06**: side mark bitmap (benched 0.90× of epochs), two-pause full cycles behind `DOTNET_GCConcurrentCycles`, mark closure concurrent on the M5 pool with card-based STW remark. soh full GCs: pause A 1–3 ms + pause B 32–41 ms (vs 58–90 ms STW), wall −4–9% on 3 of 4 scenarios. Open: histograms on all scenarios, default-on decision, fairness rerun; M6.5 = concurrent sweep (now the pause-B floor) |
| M7 ◐ | Tuning war | Beat stock on target workloads 1–3; publish reproducible results. **Fairness matrix recorded 2026-07-06** (results/perf-history.md): beats WKS everywhere and default SVR-32 + SVR+BGC on 3 of 4 scenarios; the tuned SVR-h8 row (half our memory) still leads — except **pinheavy, won 0.92× after span zero-at-carve** (`9251e10`, which also took lohmix to 1.05× of default SVR and the ASP.NET soak +47% to 45.5 k req/s). At enforced memory parity we're 1.1× (pinheavy) to 2.0× (lohmix). Open: lohmix run-scan + zeroing cost, the memory exchange rate, young-pause p50 |

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
