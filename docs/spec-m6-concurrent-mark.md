# SPEC-M6 v2: Concurrent full marking (incremental update, card-based remark)

Design for taking the full-collection mark off the pause path. **v2, 2026-07-06
afternoon**: v1 specified the 2026-07-05 COW-snapshot/SATB design (VEH + steady-state
page protection); building its stage 0 produced two measured kills, recorded in §2 —
read that section before re-proposing anything protection-based. v2 reaches the same
pause goals with mechanisms this GC already ships: the M4 card remembered set, the M5
parallel tracer, and the side mark bitmap that stage 0 landed.

## 1. Scope: what goes concurrent, what stays stopped

**Only the full mark goes concurrent.** A full collection becomes:

- **Pause A** (root pause): suspend, fix alloc contexts, clear the mark bitmap, clear
  cards, buffer all roots (M5's `_bufferMarkRoots` mechanism: stacks/registers/statics
  via `GcScanRoots`, strong + pinned handles, ref-counted callbacks, f-reachable
  queues), resume. No arming, no protection — this is a young-pause-sized suspension.
- **Mark window**: the worker pool traces the buffered roots over the **live heap**
  while mutators run. Mark bits go to the side bitmap; the marker writes no heap page.
  Mutator ref stores during the window dirty cards through the existing barrier —
  that is the entire correctness mechanism.
- **Pause B** (remark + reclaim): suspend, **remark** (re-scan roots + scan dirty
  cards, §5.4), then the mark-dependent EE protocol (dependent handles, finalization,
  weak clearing, RCW detach), sweep, `ClearCards`, budgets, resume.

**Young collections stay fully STW, unchanged** — short by construction, and a young
collection during the window would race the in-flight mark's partial bitmap, so
allocation during the window flows through (§6.3).

**The sweep stays STW inside pause B** for v2.0; concurrent sweep is the natural
follow-up once marks-final-at-pause-B is proven in production (§9).

## 2. Why not the snapshot design (measured, twice)

The 2026-07-05 benchmarks (experiments/results/2026-07-05-snapshot-primitives.md)
picked VEH manual COW with steady-state protection: pause O(write set), mutator tax
3.6 µs/page/cycle. Two later results killed it:

1. **The substrate tax** (rows `m6s0-*`, perf-history.csv): the two-view alias
   substrate the design needed (per-region pagefile sections mapped twice, so GC
   writes never fault) ran **2.2–2.6× slower** end-to-end with the working set
   doubled; a control isolated the cause to section-page soft faults being several
   times pricier than private demand-zero faults — intrinsic, unfixable by routing
   (spec history in git; summary in perf-history.md). The side-bitmap replacement it
   forced (mark state out of the heap) benched 0.90–0.93× of baseline and stays.
2. **The kernel-write wall** (experiments/KernelWriteProbe, 2026-07-06): a
   kernel-mediated write into a `PAGE_READONLY` user page **fails the syscall without
   ever reaching a user-mode VEH** — sync `ReadFile` into a protected page returns
   `ERROR_NOACCESS` (998) while the handler that successfully fixes up native
   user-mode writes (and even `GetComputerNameW`, which writes from user mode) never
   fires. Managed apps hand heap `byte[]`s to kernel-writing APIs constantly
   (`FileStream.Read` pins with `fixed` and calls ReadFile; sockets likewise), buffers
   from alloc contexts share pages with arbitrary objects, and no allocation-side
   segregation can cover `fixed`-pinned sync I/O. Boehm's incremental collector
   documents the same limitation and requires syscall wrappers — not available to a
   standalone GC hosting arbitrary P/Invokes. **Page protection over general heap
   pages is therefore unsound for arbitrary .NET apps on Windows, at any tuning.**

What survives from that line of work: the side mark bitmap (§3), the PSS snapshot as a
diagnostics tool, the measured fault-economics table, and the probe itself for the
eventual write-up. The COW design would have been sound for I/O-free workloads only —
it would have benched beautifully and broken real servers.

The v1 rejection of incremental update ("ends with a STW remark") valued
adversary-proof termination over remark cost. That trade reverses under the evidence:
remark work is bounded by window-era dirty cards — the same order as one young
collection's card scan, on infrastructure M4/M5 already hardened — while the
"no-remark" design costs an unshippable substrate. Stock BGC has run this trade in
production for 15 years.

## 3. Mark state: the side bitmap (landed as stage 0)

1 bit per 8 heap bytes, 32 KB per region, committed alongside the frontier; cleared
wholesale at full-mark start (~1.5 ms/GB, in pause A) and per-region at carve.
`IsMarked`/`Mark`/`TryMark` are bit tests / interlocked bit sets; sticky-generation
semantics are unchanged from M4 (young marks accumulate; fulls restart the bitmap).
Epoch machinery is deleted; obj−8 is free again.

Why it stays load-bearing in v2: the concurrent marker shares no heap cache lines with
mutators for mark traffic, `TryMark`'s interlocked bit set is the multi-worker claim,
and marking touches nothing a mutator write could tear. It also benched faster than
epochs outright (dense words beat scattered obj−8 stamps: `m6s0-bitmap` 0.90–0.93× of
`m6s0-base` on all four scenarios).

## 4. Correctness argument (incremental update)

The invariant: at the end of pause B's remark, every reachable object is marked.

- A ref that existed at pause A and never moved: found by the concurrent trace from
  the buffered roots (the marker reads current field values; an unmutated field holds
  the pause-A value).
- A ref stored into a heap slot during the window: the store dirtied the slot's card
  (the M4 barrier covers the whole heap); remark scans dirty cards and traces the
  current values of every marked object's fields overlapping them (§5.4).
- A ref held only in a register/stack slot at remark time (loaded during the window,
  heap copy overwritten): pause B re-runs the root scan under STW — it is found there.
  This is the case that makes root re-scan mandatory, and it is also why the v1 doc's
  "register ping-pong adversary" only inflates remark *work*, never soundness: at
  suspension the ref is either in a root (root scan) or in the heap (card or already
  marked).
- Objects allocated during the window: the marker can reach them (it reads the live
  heap), and whatever refs them is a root or a carded store at pause B. Dead
  window-born objects are collected in the same cycle — better than SATB's mandatory
  floating garbage. No allocate-black machinery, no `CarvedDuringMark` flag, and
  **hole carving stays enabled during the window** (an unmarked window-born object in
  a swept region is genuinely dead; plugging it is correct, unlike under a snapshot).
- Torn reads: x64 aligned pointer loads are atomic; a mid-store read sees old or new
  value, and the new value's store dirtied a card either way. MethodTable/Length are
  written before an object is published (x64 store order), so a traced ref always
  reaches a well-formed object.

Termination: the remark is one STW pass — roots, then a single sweep over dirty cards
with inline transitive drains. The single pass is complete for the same reason M4's
young card scan is: any object marked *during* the pass has its references traced at
marking time from the (now frozen) heap, so a card skipped because its object was
unmarked at visit time is covered by that object's own trace when it gets marked.

## 5. The cycle

### 5.1 Trigger and orchestration

Full-collection triggers are unchanged, and **the triggering thread orchestrates the
whole cycle itself** — it is a normal EE thread in preemptive mode, i.e. the standard
GC-induction caller of `SuspendEE`, so it may suspend/restart twice and participate in
the window's drain in between (it is doing GC work, not waiting). It holds `_gcLock`
for the whole cycle, which serializes against young collections and other full
triggers; §6.3's allocate-through keeps other allocators from queueing behind it.
This deletes the v1 coordinator thread and its `IGCToCLR.CreateThread` verification
item. A dedicated coordinator remains a follow-up option if dedicating the trigger
thread for the window ever measures as unfair (stock BGC's shape); nothing in the
design depends on the choice. Young collections keep running inline as today.

### 5.2 Pause A

Under `SuspendEE` + `EnterGateForCollection`: notifications (`GcStartWork`,
`BeforeGcScanRoots` — flag semantics to verify, §8), `FixAllocContexts`, `ClearMarks`
+ `ResetLiveBytes`, **clear cards** (pre-window cards are subsumed by the full trace;
clearing here makes the remark set exactly the window's writes), buffer the roots
(§1), resume. The ref-counted handle callbacks run here (EE calls need STW; their
results are buffered like everything else).

### 5.3 The mark window

The pool (plus coordinator) runs the existing buffered-root drain — share queue,
donation, idle-quorum termination — with mark state in the bitmap and `LiveBytes`
interlocked into the region table, both GC-private. Collectible-assembly edges
(`GetLoaderAllocatorObjectForGC`) are deferred by workers as today; whether the
coordinator may take them mid-window off-STW is a verification item (§8) with
pause-B deferral as the fallback.

The zeroer keeps running through the window (it writes only dead memory: pooled
regions and hole bodies, which the marker never traces into); the gate excludes it
from both pauses exactly as it excludes it from today's single pause.
`CollectorWaitingForGate` trips twice per cycle; the stand-down/kick dance already
handles that.

### 5.4 Pause B

Under `SuspendEE` + `EnterGateForCollection`, in order:

1. `FixAllocContexts` (windows carved during the window get plugged as usual).
2. **Remark**: re-run the buffered root scan (stacks/registers/statics/handles) and
   drain; then `ScanCards` in **remark mode** — same code as the young card scan with
   one flag: *Fresh regions are not skipped*. (Young scans may skip them because young
   marking is entirely STW — an object traced early in the pause cannot be mutated
   later. A concurrently-traced object can be mutated after its trace, so remark must
   rescan dirty cards over every region age, including window-born regions.)
3. The existing pause tail, verbatim: dependent-handle fixpoint, finalization scan +
   resurrection trace, `NotifyAfterGcScanRoots` (RCW detach), weak clears, sync-block
   weak scan.
4. Sweep (parallel, unchanged — no skips, no new flags), `ClearCards`, `TrimPool`,
   budget/trigger accounting, `_gcCount++`, notifications, resume, kick the zeroer.

Everything after the remark is today's code operating on final marks. Cards cleared
at pause B end restores exactly today's young-collection contract.

## 6. Policies

### 6.1 Kill switch

Staging knob: `DOTNET_GCConcurrentCycles` (a private name — the EE reports stock
`gcConcurrent`'s *default* as true, so honoring it would open the path everywhere
before it earns that). Off = the current inline STW path. Once §7 stage-2 exit
criteria hold, the gate flips to honoring stock `DOTNET_GCConcurrent`.

### 6.2 OOM and forced collections

`GC.Collect` and OOM-path forced fulls block preemptively on cycle completion if one
is in flight, then run their own (the forced-full semantics of `GarbageCollect` — the
caller is entitled to a collection that starts after its call — are preserved by
running a fresh cycle, which may itself be concurrent for non-OOM callers and inline
STW for the OOM last-stand).

### 6.3 Allocation during the window (allocate-through)

Budget-triggered `Collect` calls return immediately while a cycle is in flight (the
coordinator holds `_gcLock`; a young collection could not run anyway and its input —
what's reclaimable — is being computed). The overshoot is bounded by alloc-rate ×
window length; pause B's budget reset absorbs it. Under a hard limit the window can
delay an OOM verdict by one window length (§6.2 path).

### 6.4 Window length

Remark cost and memory overshoot both scale with window length, so the window is the
tuning surface: worker count for the concurrent drain, and (follow-up) bounded
concurrent card pre-drain passes before suspending — stock BGC's revisit trick — if
remark measures long on store-heavy workloads.

## 7. Implementation stages (each ends suite-green + soak clean)

0. **Side mark bitmap** ✅ (2026-07-06): `m6s0-bitmap` at 0.90–0.93× of baseline,
   suite 56/56, unit 70/70. Also proved out: range-check-before-marks orderings.
1. **Two-pause STW cycle, empty window**: the triggering thread runs pause A (root
   buffering) / gap / pause B (drain + full remark + protocol + sweep) — the "window"
   is a no-op resume/re-suspend, so the remark machinery is exercised against real
   gap mutations with zero concurrency risk. Allocate-through flag lands here too.
   Validates the EE choreography (double suspend/restart, notification flags,
   `WaitUntilGCComplete`, §8 items).
   *Exit: suite/soak green with the split active; pause histograms recorded.*
2. **Concurrent drain**: move the trace into the window (workers off-STW), remark =
   root re-scan + all-ages card scan. Kill switch honored.
   *Exit: suite/soak green with DOTNET_GCConcurrent=1; forced-full stress run (tight
   GC.Collect loop under load) green; pause histograms show pause A ≈ young-pause
   scale and pause B ≈ remark+sweep on all four scenarios + the ASP.NET soak.*
3. **Tune + publish**: window/remark instrumentation (§10), card pre-drain if needed,
   rerun the fairness matrix (pause profile changed — then re-evaluate the frozen M7
   pause-tuning list per the 2026-07-06 course-check).

## 8. Verification items against a real EE (tracked, not assumed)

- `BeforeGcScanRoots(is_bgc, is_concurrent)` / `GcStartWork` semantics with a real
  BGC-shaped cycle; `RestartEE(finishedGC: false)` at pause A.
- Back-to-back `SuspendEE`/`RestartEE` pairs from the same EE thread within one
  logical GC (pause A, pause B) — believed identical to two consecutive GCs from the
  EE's perspective; verify notifications and the wait-event protocol around it.
- `GetLoaderAllocatorObjectForGC` legality off-STW (fallback: defer to pause B).
- `WaitUntilGCComplete`/`_gcEvent` semantics across a two-pause cycle.
- Finalizer thread interaction: `GetNextFinalizable` while a window is open (it only
  reads GC-private queues + the header bit — believed fine; verify under stress).

## 9. Deliberately deferred

- **Concurrent sweep** (pause B → remark only): sound once marks are final at pause
  B; requires re-architecting sweep/allocator/zeroer interleaving. M6.5.
- **Concurrent card pre-drain** (shrinks remark on store-heavy workloads).
- **Young collections during the window** (needs bitmap generation separation; only
  worth it if windows measure long on huge heaps).
- Ping-pong mark bitmaps (pause A's wholesale clear → O(1) swap + background clear)
  if the in-pause memset ever shows up at scale.
- **COW-SATB on Linux**: the kernel-write wall (§2) is Windows-specific. Linux's
  `userfaultfd` write-protect mode makes kernel-originated writes to monitored pages
  block and resolve through the fault handler instead of failing the syscall, so the
  2026-07-05 snapshot design is soundly implementable there. Only relevant if the
  "Linux is a bonus" clause ever activates *and* remark tails prove real; recorded so
  the door stays marked. PSS also survives on Windows as diagnostics — an occasional
  offline shadow-mark verification pass against a PSS clone is a cheap way to audit
  the concurrent marker in debug builds.

## 10. Instrumentation

Per full cycle: pause A ms (suspend / fix / clear / roots breakdown), window ms,
pause B ms (root-rescan / card-remark / protocol / sweep breakdown), cards dirty at
remark, objects marked concurrently vs at remark, allocation during window. GCStats
CSV columns extend accordingly; perf-history.md records each stage per the archive
protocol.
