# The snapshot collector ("single-pause" design)

Design notes from the 2026-07-05 discussion. Target: ManagedDotnetGC — non-moving,
non-compacting mark & sweep, x64 only, standalone GC API only (no runtime fork).

## Theory summary

**Why not zero pauses:** The CLR write barrier is *lazy* (card marking: records the
written *location*, discovers the value only at rescan time) and stacks/registers are
unbarriered. With a lazy barrier, two mutators can keep the sole reference to an object
perpetually inside the collector's staleness window (register ↔ heap ping-pong), so a
sound "marking is complete" decision requires one instant where all roots are captured
atomically. Every on-the-fly collector in the literature (Dijkstra, DLG,
Domani–Kolodner–Petrank, SGCL) buys its way out with an *eager* mutator-executed
barrier, which we don't have. Incremental-update collectors with lazy dirty tracking
(CMS, .NET background GC, Boehm incremental) all end with a STW remark.

**The escape hatch:** SATB doesn't intrinsically need a per-store barrier — it needs
*the heap as it existed at mark start*. A copy-on-write snapshot of the heap plus an
atomic root capture gives exact SATB semantics with **zero** reliance on the write
barrier:

1. **Pause** (the only one): capture roots (stacks + registers), arm the heap snapshot.
2. Mutators run; first write to each page preserves that page's pre-image (COW).
3. Marker computes the closure over the immutable snapshot. Termination is
   deterministic and bounded at snapshot time — the mutator *cannot* extend mark work
   (contrast: dirty-card recirculation). No remark. Allocate-black via alloc-context ranges.
4. Drop the snapshot, sweep concurrently (mark decisions are final; freelist writes to
   dead objects are safe).

SATB invariant: any reference a mutator holds post-snapshot is either reachable in the
snapshot (marked) or newly allocated (black). A page pre-image is strictly stronger than
a Yuasa deletion log for that page — only the *first* write per page matters. Floating
garbage ≤ one cycle of allocation + mid-cycle deaths → memory stays bounded.

Root sources: stacks/registers need the atomic capture; handles + finalization queue are
GC-owned; statics are heap-backed (covered by the snapshot).

## Two candidate snapshot mechanisms

### A. Manual COW: VirtualProtect + vectored exception handler
- Suspend → root scan → `PAGE_READONLY` the heap → resume.
- VEH on write fault: copy 4KB pre-image to a preallocated buffer, unprotect page, resume.
- Marker reads pre-images through a translation layer; untouched pages read in place.
- Variant: allocate the heap as a pagefile-backed section with **two views** — protected
  mutator view + always-writable GC alias. The handler copies pre-images via the alias;
  the marker can write in-heap GC metadata (see padding word below) without faulting.
- Risks: VEH handler correctness (must be allocation-free), user-mode fault cost per
  written page, `VirtualProtect` cost over a multi-GB range inside the pause.

### B. Kernel COW: PSS / process reflection
- `PssCaptureSnapshot(PSS_CAPTURE_VA_CLONE | PSS_CAPTURE_THREADS | PSS_CAPTURE_THREAD_CONTEXT)`
  = Windows' hidden fork (RtlCreateProcessReflection). Clone process shares COW pages.
- Kernel handles COW breaks transparently (no VEH at all, covers runtime-native and bulk
  writes). Marker reads the frozen clone via batched `ReadProcessMemory`.
- If thread contexts are captured atomically with the VA clone, the *entire* root
  capture (registers + stacks-in-clone) rides the kernel freeze → pause independent of
  thread count; roots are then necessarily conservative (fine for non-moving; but
  adversarially unbounded retention — precise mode = SuspendEE + GcScanRoots + capture).
- Risks: clone freeze scales with VA/page-table size (Criteo datapoint: ProcDump -r on
  10–20GB heaps still froze ~1min — was that the primitive or ProcDump's dump-writing?),
  process spawn per cycle, RPM read throughput, EDR noise, semi-documented internals.
- Constraint: marker must not write into the parent heap while the clone lives (every
  write forces a COW copy of a live page) → side mark bitmap during mark; defer any
  in-object stamping until clone teardown.

## The x64 padding word (object − 8, `m_alignpad`)

4 unused bytes per object on x64 (header is 8 bytes, only the syncblock DWORD at −4 is
used; release runtimes never touch the pad; nothing moves, so nothing clobbers it).
Candidate uses:
- **Epoch marking**: 32-bit "last marked in cycle E" → no mark-clear phase ever,
  multi-cycle lazy sweep is trivially correct, free per-object age for diagnostics
  (2³² cycles ≈ 136 years @ 1Hz). Requires the two-view aliasing trick under design A;
  under design B must be deferred to post-teardown stamping.
- **Intrusive grey queue**: 32-bit compressed next-link → O(1) marking memory, no
  mark-stack overflow handling.
- **Size cache**: heap walks / interior-pointer resolution without MethodTable derefs.

## Open questions → SnapshotBench

1. `PssCaptureSnapshot` self-clone latency vs VA size — and does the observed mutator
   freeze match the API duration? (heartbeat threads measure actual gaps)
2. Are thread contexts captured atomically with the VA clone? (register-resident counter
   vs cloned-memory counter delta)
3. `ReadProcessMemory` throughput from the clone: sequential + random-4KB (marking is
   pointer chasing).
4. Kernel COW break cost per page (mutator tax of design B) vs VEH fault + 4KB copy +
   unprotect (mutator tax of design A).
5. `VirtualProtect` over 1–8GB ranges (pause contribution of design A).
6. Clone teardown cost, and whether teardown also stalls mutators.

Kill criteria: if the raw clone freeze at 8–16GB is ≥ hundreds of ms, design B dies and
A wins by default. If VEH faults cost ≫ kernel COW breaks and `VirtualProtect` of the
whole heap is cheap, B still needs to beat A on marker read throughput (in-process reads
vs RPM) to justify the process machinery.
