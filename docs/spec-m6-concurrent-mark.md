# SPEC-M6: Concurrent full marking via COW snapshot

Reconciles the 2026-07-05 snapshot-collector design (experiments/DESIGN.md, benchmark
verdict in experiments/results/2026-07-05-snapshot-primitives.md) with everything that
landed since it was written: sticky generations + the card remembered set (SPEC-M4), the
parallel mark/sweep worker pool (M5), zero-at-carve and the background zeroer with its
gate protocol (M7), and hole carving. Written 2026-07-06, before any M6 code.

## 1. Scope: what goes concurrent, what stays stopped

**Only the full mark goes concurrent.** A full collection splits into:

- **Pause A** (the snapshot pause): suspend, capture roots, arm the snapshot, resume.
  Target cost: root enumeration + a small re-protection residual — young-pause scale,
  independent of heap size.
- **Mark window**: mutators run; the worker pool computes the mark closure over the
  immutable snapshot.
- **Pause B** (the reclamation pause): suspend, run the mark-dependent EE protocol
  (dependent handles, finalization, weak clearing, RCW detach), sweep, resume.

**Young collections stay fully STW, unchanged.** Two reasons, one practical, one
structural:

1. Practical: a young pause is roots + dirty cards + young sweep — already short, and
   dominated by the same root scan the snapshot pause needs anyway. Snapshotting buys
   nothing.
2. Structural: *a young collection during the mark window could free nothing at all.*
   Regions carved after the snapshot are allocate-black (all-live by construction, §6.4),
   and pre-snapshot young objects are exactly what the in-flight full mark is deciding —
   sweeping them early would race the decision. There is no equivalent of "ephemeral GC
   during BGC" to build, because in this design its input set is empty by construction.
   Allocation during the window simply floats until pause B (§7.1).

**The sweep stays STW inside pause B** for v1. SATB makes mark decisions final at
termination, so a concurrent sweep is sound *later* — but today's sweep assumes STW
everywhere (lock-free pool pushes, list rebuilds racing nothing, zeroer excluded by the
gate). Splitting that assumption is its own project; pause B at current parallel-sweep
cost (~tens of ms, wholesale recycle doing most of the work) is an acceptable v1 floor.
Follow-up recorded in §10.

## 2. The benched primitive (recap)

From 2026-07-05 (Ryzen 7950X3D): steady-state VEH protection won.

- Keep the (mutator view of the) heap `PAGE_READONLY` permanently.
- A write fault costs 2.7 µs (unprotect + bookkeeping) or 3.6 µs with a 4 KB pre-image
  copy — the same as the kernel's own COW break (3.4 µs). Paid once per page per cycle.
- Each snapshot pause re-protects only pages dirtied since the last snapshot:
  **O(write set), not O(heap)**. Whole-heap protection costs ~28 ms/GB and does not
  parallelize (kernel-serialized), so the steady state is what makes the pause flat.
- PSS/VA-clone lost (~30 ms/GB resident, freezes mutators) but survives as diagnostics.

Two findings from the reconciliation sharpen the "O(write set)" claim:

- With sticky generations, full collections are *rare* (promotion-doubling trigger), so
  "pages dirtied since the last snapshot" approaches "every page the app touched in
  minutes" — for allocation-churn workloads that is nearly the whole committed heap.
  Left alone, pause A degenerates to whole-heap protect. The **background re-protector**
  (§4.4) and **carve-time unprotection** (§4.3) are therefore load-bearing, not
  optimizations.
- The mutator fault tax lands almost entirely on freshly allocated memory (bump windows
  are written once, immediately). Carve-time unprotection moves that cost from one fault
  per 4 KB (~0.9 µs/KB — 13× the memset cost it would ride on) to one `VirtualProtect`
  range call per carve (~0.6 µs per 128 KB window). Without it the design loses every
  allocation benchmark; with it the tax falls only on old-object mutation, which is the
  write set that is actually small.

## 3. Mark state: side bitmap, private memory (decided by measurement)

**Two-view aliasing was built first and killed by its own stage-0 parity gate**
(2026-07-06, rows `m6s0-*` in results/perf-history.csv). Per-region 2 MB pagefile
sections mapped twice — protected front view for mutators plus an always-writable GC
alias, via `VirtualAlloc2` placeholders + `MapViewOfFile3` — ran **2.2–2.6× slower**
than the private-memory baseline (soh median 2.205 → 4.849 s, pinheavy 2.189 → 5.774 s)
with the **working set doubled** (peak 6.8 → 13.2 GB: every heap page resident through
both mappings' PTEs). A control build with bulk zeroing routed back through the front
view isolated the attribution: the WS inflation mostly disappeared (→ 8.6 GB) but wall
stayed ~2× — **section-backed pages make the (re)commit-churn soft faults several times
pricier than private demand-zero faults**, and this GC's recycle/trim churn lives on
that fault path. Intrinsic to the substrate, not fixable by write routing; the WS
doubling would also have poisoned the memory column of every future comparison.
Reverted wholesale.

**Adopted: the heap stays plain private `VirtualAlloc` memory; mark state moves out of
the heap into a side bitmap.**

- 1 bit per 8 heap bytes → 32 KB per region, reserved flat (32 GB VA over the 2 TB
  heap), committed alongside the frontier like the card-offset table (+1.6% committed).
- `IsMarked`/`Mark`/`TryMark` keep their call sites and become bit tests / interlocked
  bit sets; M4 sticky semantics are unchanged. A full collection clears the bitmap
  wholesale at mark start (~1.5 ms per committed GB, in-pause — if stage-3 pause budgets
  object, switch to ping-pong bitmaps: O(1) swap at pause A, background clear after).
  A recycled region's 32 KB slice is cleared at carve, so a previous life's sticky bits
  cannot resurrect dead objects between fulls (the exact analogue of zero-at-carve).
- Epoch machinery (CurrentEpoch, NextEpoch, the wrap-clear walk) is deleted; the obj−8
  padding word is freed for future use (grey links, size cache).
- Mark consumers must range-check before consulting bits — the bitmap only covers the
  heap, so the old "IsMarked first, range-check later" orderings in the mark loop and
  the dependent-handle scan are inverted.

**Parity gate result: passed with margin** — the bitmap runs 7–10% *faster* than the
epoch baseline on all four scenarios (soh 1.991 vs 2.205, lohmix 1.906 vs 2.045, pin
1.906 vs 2.124, pinheavy 1.967 vs 2.189; identical GC counts, WS within ~2%). Mark-state
traffic on dense bitmap words beats scattered obj−8 stamps: the duplicate-pop check
never touches the object, and sweep/card liveness tests read sequentially.

The failed experiment also exposed why the bitmap is **required regardless of
substrate**: under an armed snapshot, every marker write into a protected heap page
faults and must save that page's pre-image, making the pre-image slab O(live set) —
a 16 GB live heap could take ~64 GB of pre-images from marking alone. With the side
bitmap the marker performs *zero heap writes* — it reads the snapshot and writes
GC-private memory — so pre-images stay bounded by the mutator write rate, which is the
point of the whole design.

**Remaining GC heap writers under protection** (stages 1+), with no alias to hide in:

- **Bulk writers** — the zeroer's region memsets, the sweep's plug writes — explicitly
  `VirtualProtect` the target region writable first (one call, ~0.6 µs) and record its
  pages in the dirty set. Sound and cheap: those extents are dead and about to be
  carved, i.e. unprotected-by-design anyway (§4.3).
- **Scattered rare writers** — the finalizer-run header bit, hole-carve plug/link
  writes on the mutator's allocation path between cycles — simply take the write
  fault. First-fault-saves is sound no matter who faults (§4.1); the volumes are
  trivial.

## 4. Protection domain and dirty-page bookkeeping

### 4.1 Steady state

Front-view pages of live regions are protected. A write fault:

1. Checks the address is in the heap range (else pass to the next handler).
2. Sets the page's bit in the **dirty-page bitmap** (flat, 1 bit per 4 KB page,
   committed alongside the frontier; interlocked or).
3. If a snapshot is armed *and* the page's region is snapshot-visible (§5.2) *and* no
   pre-image exists yet: claims the page's slot (CAS in the pre-image index), copies the
   4 KB pre-image into the preallocated slab, publishes the index (release), then
   unprotects. Losers of the claim return `EXCEPTION_CONTINUE_EXECUTION` and re-fault
   until the winner unprotects.
4. Otherwise just unprotects (single-page `VirtualProtect`, 0.59 µs).

Handler discipline: allocation-free, lock-free (interlocked bitmap + slab cursor),
touches only GC-private unprotected memory, registered first
(`AddVectoredExceptionHandler(1, …)`), claims only heap-range write AVs. Foreign-thread
attach to the GC's NativeAOT runtime is the same mechanism as every GC callback — the
2026-07-05 bench note confirms coop-mode CoreCLR threads are fine here.

Invariant: for every page of a committed region, *protected* XOR *dirty-bit set*. The
re-protector and pause A restore protection and clear bits together.

### 4.2 What is never protected

- Metadata (region table, card table, card offsets, pool, handle segments — handle
  segments are `NativeMemory` outside the reservation, see §6.2).
- Decommitted regions (nothing mapped).
- **Pinned pages** during a mark window — see §6.5. Excluded for correctness (buffered
  I/O completions must not fail), made sound by pause-A eager capture.

### 4.3 Carve-time unprotection

Every allocation handout (`TryGetWindow`, hole carve, `TryAllocBlock`, `TryAllocSpan`)
unprotects the handed-out extent (one range `VirtualProtect` outside the alloc lock,
alongside zero-at-carve) and sets its dirty bits. Sound in all states: carved memory is
either in a region born after the snapshot (allocate-black, marker never reads it) or is
a hole — and hole carving is disabled during the mark window (§6.4), so during a window
carves only touch post-snapshot regions.

### 4.4 The background re-protector

A GC-runtime thread (sibling of the zeroer; likely the same thread with a second duty)
that walks the dirty bitmap and re-protects cold pages *before* pause A needs to, so the
pause only handles the recent residual. Soundness is trivial: early re-protection just
means the page faults again if re-written — pre-images are only taken while a snapshot
is armed, so nothing stale is ever captured (the "early re-protection is unsound" note
in the 2026-07-05 results referred to taking pre-images early; re-protecting early is
fine).

Policy (tuning knobs, not correctness):

- Ramp with full-trigger proximity: idle until `_promotedSinceFull` crosses ~60% of the
  full trigger, then work the backlog down so the pause-A residual is minutes of writes,
  not the whole inter-full write set.
- Skip pages in pooled/free regions (not snapshot-visible; their bits stay set and they
  are carve-unprotected anyway) and pages of the active bump region / recently carved
  regions (still being written once — re-protecting mid-fill causes a fault storm on
  memory that faults exactly once otherwise). "Recently" = region carved within the last
  N seconds; start with N = 2.
- Zeroer-style gate discipline: stands down when `CollectorWaitingForGate`.

## 5. The concurrent cycle

### 5.1 Trigger and the coordinator thread

Full-collection triggers are unchanged (promotion-doubling, committed>8×live with the
mute, forced/OOM, memory pressure). What changes is *who runs the cycle*: a
**coordinator thread** created at Initialize via `IGCToCLR.CreateThread(…,
is_suspendable: false, …)` — the standalone-API equivalent of the stock BGC thread, and
the legitimacy for calling `SuspendEE`/`RestartEE` off a mutator thread. The triggering
mutator signals the coordinator and returns to allocating (young collections still run
inline on the triggering thread as today).

The coordinator holds `_gcLock` for the whole cycle (pause A → window → pause B), which
is what serializes it against young collections and other full triggers.

### 5.2 Pause A — the snapshot pause

Under `SuspendEE` + `EnterGateForCollection`:

1. `NotifyGcStartWork` / `BeforeGcScanRoots(condemned: 2, is_bgc: true, is_concurrent:
   true)` — flag semantics against a real EE are a stage-3 verification item (§9).
2. `FixAllocContexts` (plug windows — writes precede arming, so plugs are simply part of
   the snapshot).
3. `ClearMarks` + `ResetLiveBytes` (as today for full collections; the bitmap clear is
   the in-pause memset §3 budgets, or the ping-pong swap once that lands).
4. **Clear all cards.** Pre-pause-A old→young edges are subsumed by the full mark;
   cards dirtied *during* the window must survive for the next young collection, so the
   clear moves from end-of-collection to here (§6.6).
5. **Buffer the roots** (M5's `_bufferMarkRoots` mechanism, now feeding the window):
   `GcScanRoots` (stacks/registers/statics; interior pointers resolved here under STW,
   where snapshot ≡ live), strong + pinned handles, ref-counted handles (EE callback runs
   here, on the suspended world), f-reachable queues. This buffered set *is* the atomic
   root capture — including a snapshot of every handle slot, which is what makes
   unprotected handle memory sound (§6.2).
6. **Stamp snapshot visibility**: one pass over the region table recording `Kind != Free`
   into a per-region snapshot flag, and clear the per-cycle `CarvedDuringMark` flags.
   (~O(carved regions), trivially cheap.)
7. **Eager-capture excluded pages** (§6.5): for every page overlapping a pinned object,
   leave it unprotected and enumerate the references of all objects overlapping that
   page into the root buffer (worker-pool parallel; byte[] buffers have no refs and cost
   nothing).
8. **Arm**: re-protect every dirty-bitmap page belonging to a snapshot-visible region
   (coalesced ranges; the re-protector has already done the bulk), clear those bits,
   reset the pre-image slab/index, set `_fullCycleInFlight`.
9. `RestartEE`.

### 5.3 The mark window

The worker pool (plus the coordinator as participant) runs the existing
buffered-roots/share-queue/idle-quorum drain — same code shape as `ParallelDrainMark` —
with two changes:

- **Reads go through snapshot translation.** Per object: read the pre-image index for
  the page (0 → read live memory, then *re-check the index*; changed → retry via
  pre-image — the release-publish before unprotect makes this race-free); nonzero → read
  from the slab. MethodTable pointers, Length, and GCDescs are immutable after publish
  and may be read from either view; *reference slots must come from the snapshot*.
  Objects straddling pages check per page. Mark throughput through translation will be
  slower than STW marking — irrelevant to pauses, bounded interest for window length.
- **Writes go to GC-private memory only** — mark bits to the side bitmap, LiveBytes to
  the region table. The marker never writes a heap page, so it never faults and never
  forces a pre-image (§3).

The collectible-assembly edge (`GetLoaderAllocatorObjectForGC`) is deferred by workers
as today; the coordinator takes deferred entries *during* the window. Whether that EE
call is legal off-STW from the coordinator is a stage-3 verification item; the fallback
is deferring them to pause B (bounded extra pause work, collectible assemblies are
rare).

Termination is the existing idle-quorum protocol — SATB's gift is that the closure is
bounded at snapshot time, so there is no remark and no recirculation.

### 5.4 Pause B — the reclamation pause

Under `SuspendEE` + `EnterGateForCollection`, with the snapshot still armed (pause-B
tracing must read snapshot state):

1. `FixAllocContexts` (windows carved during the window live in all-live regions; plugged
   anyway for heap walkability).
2. Dependent-handle fixpoint (unchanged code; any new tracing reads via translation).
3. `ScanForFinalization` + resurrection trace, `NotifyAfterGcScanRoots` (RCW detach),
   weak-handle clearing, sync-block weak scan — the existing pause-tail protocol,
   verbatim, just relocated to pause B.
4. **Sweep** (parallel, existing code) with one new rule: **skip regions flagged
   `CarvedDuringMark`** — leave them `Fresh`, objects unmarked; they are this cycle's
   floating garbage and the next collection's normal work. Sweep plug writes unprotect
   their target region first (§3); the snapshot is disarmed before the sweep starts, so
   none of those writes takes a pre-image.
5. `TrimPool`, budget/trigger accounting, `_gcCount++`, `NotifyGcDone` — as today.
   **Do not clear cards** (window-era cards feed the next young collection).
6. **Drop the snapshot**: walk the handler's published-page log, clear those pre-image
   index entries, reset the slab cursor, clear `_fullCycleInFlight`.
7. `RestartEE(finishedGC: true)`, `EnableFinalization`, kick the zeroer and re-protector.

## 6. SATB soundness inventory

The invariant to protect: *every reference a mutator can hold after pause A is either
reachable in the snapshot (the marker will find it) or points into post-snapshot
allocation (allocate-black)*. Deletion is the only enemy — each way a reference can be
destroyed must leave its pre-deletion value discoverable.

| Deletion channel | Covered by |
|---|---|
| Overwrite of a heap reference slot | COW pre-image (first write faults before the old value is lost) |
| Overwrite of a stack slot / register | Pause-A `GcScanRoots` capture (STW, atomic) |
| Overwrite/free of a handle slot (unprotected native memory) | Pause-A buffered handle enumeration = snapshot of all slots (§6.2) |
| Writes to excluded (pinned) pages | Pause-A eager capture of every object overlapping those pages (§6.5) |
| Alloc-context churn | `FixAllocContexts` before arming; new contexts land in post-snapshot regions |
| GC's own writes (plugs, zeroing) | Only touch dead-at-snapshot memory or non-reference words; bulk writers unprotect explicitly, rare ones fault with first-fault-saves (§3, §6.3). Marks live outside the heap entirely. |

### 6.1 Newly allocated objects (allocate-black)

The GC never sees individual object allocations (the EE bump-allocates inside handed-out
windows), so per-object black allocation is impossible — the unit is the **region**.
Regions carved during the window get `CarvedDuringMark`; pause-B sweep skips them
wholesale. Windows/blocks/spans carved during the window all come from such regions
because **hole carving is disabled during the window** (§6.4). Objects born in them are
unmarked-but-alive floating garbage until the next collection — the standard SATB bound
(≤ one window of allocation + mid-window deaths).

Stale-mark hygiene: recycled regions clear their bitmap slice at carve (§3), so no
sticky bit from a previous life can make an object in a skipped region look marked.

### 6.2 Handles

Handle segments are `NativeMemory` outside the heap — never protected, never
pre-imaged. Sound because pause A buffers every strong/pinned/ref-counted target (a
point-in-time copy of the slots): a handle retargeted or freed during the window cannot
delete its pause-A value from the marker's input. New handles created during the window
hold values the mutator already held — snapshot-reachable or new, both safe. Weak
handles are read at pause B against final marks, as today.

### 6.3 The zeroer and the sweep as heap writers

Both write only memory that was *dead at snapshot time* (pool regions were `Free`;
hole bodies were plug interiors — plugs are unreachable by definition and the marker
never traces into them), and both unprotect their target region explicitly before
writing (§3: one `VirtualProtect` call, pages recorded dirty — sound because those
extents are about to be carved unprotected anyway, and no pre-image is needed for
dead-at-snapshot memory). The zeroer therefore **keeps running through the whole cycle**,
gate protocol unchanged: `EnterGateForCollection` at both pauses excludes it exactly as
the single pause does today; its hole-region checkout cannot straddle a *pause* (gate),
while straddling the mark window is harmless (the sweep it must not race sits inside
pause B, behind the gate). `CollectorWaitingForGate` now trips twice per full cycle —
the stand-down/kick dance already handles repeated trips.

### 6.4 Hole carving disabled during the window

If a hole in an `Old`/`Reopened` region were carved during the window, pause-B's sweep
walk of that region (it has dead-at-snapshot objects to plug) would see the new objects
— unmarked, walk-visible — and plug them as dead. Rather than teach the sweep about
per-extent birth epochs, **`TryCarveFromHoles` returns nothing while
`_fullCycleInFlight`**: all window-era demand goes to fresh regions (allocate-black,
uniform). Costs a window of hole-reuse (bounded committed growth, same floating-garbage
bound as §6.1); the recycled list is intact and resumes after pause B. This also
removes every mutator-side plug/link write to snapshot-visible pages, simplifying §6.3.

### 6.5 Pinned pages: exclusion + eager capture

Protected pages break kernel-mediated writes into pinned buffers: buffered-I/O
completions (`ReadFile` into a pinned byte[]) copy from kernel mode and would *fail the
I/O* on a read-only user page, and direct-I/O DMA via locked MDLs bypasses protection
entirely (invisible writes). Pinning is our headline workload — this cannot be an
accepted regression.

Resolution: at pause A, every page overlapping a pinned object (pinned handles + pinned
stack roots, both enumerated in the pause) is **left unprotected**, and — because a 4 KB
page in a 2 MB mixed region can hold non-pinned neighbors — *all* objects overlapping
those pages get their references enumerated into the root buffer (eager capture: the
page's snapshot state is captured once, by value, instead of lazily by COW). Unlogged
window-era writes to those pages can then never delete anything the marker hasn't
already seen. Typical pinned objects are ref-free buffers (`ContainsGCPointers ==
false`), so eager capture costs a region-table lookup and an MT check per page —
pinheavy stays cheap. DMA data writes remain invisible to the snapshot, which is fine:
they are data, not references, and marking only consumes references.

### 6.6 The card table across the cycle

- Cards cleared at pause A (old edges subsumed by the full mark).
- Cards dirtied during the window (the barrier keeps running — it writes the card
  table, which is unprotected GC memory) record every old→young store made while
  marking; they survive pause B untouched and feed the next young collection's scan.
- Stale dirty cards over regions the pause-B sweep recycles are waste, not bugs (the
  young scan gates on region Kind/Age), and the next young collection's end-of-pause
  `ClearCards` retires them.

### 6.7 Accepted parity risks

Raw memory writes that bypass both the barrier and page protection —
`WriteProcessMemory` from a debugger (restores protection around its write), func-eval
side effects — can hide references from an in-flight concurrent mark. The stock BGC has
the same exposure for the same reason (no barrier executes); debuggers own the
synchronization. Documented, not defended.

## 7. Policies

### 7.1 Allocation during the window: allocate-through

Budget-triggered `Collect` calls while `_fullCycleInFlight` return immediately (no
young collection can reclaim anything — §1 — and the coordinator holds `_gcLock`
anyway). Allocation proceeds past the budget; the overshoot is bounded by alloc-rate ×
window length and is reclaimed by pause B + the next young collection. The post-full
budget reset absorbs the bookkeeping.

OOM-path calls (`force: true`) during the window block preemptively on a
cycle-completion event, then re-evaluate — if memory is still short after pause B, the
normal forced-full path runs. Under a hard limit the window can therefore delay an OOM
verdict by one window length; acceptable.

### 7.2 Pre-image slab overflow

The slab is fixed (default 512 MB, `DOTNET_GCConcurrentPreimageMB` override). If the
window's write set exhausts it, the handler can no longer preserve pre-images — the
snapshot is dying. Recovery: the handler stops taking pre-images and unprotects on
fault (dirty bits still recorded); the coordinator observes the overflow flag,
suspends the world, and **finishes the collection as a full STW mark from scratch**
(existing M5 path, same epoch). Keeping the partial concurrent marks is sound: an
object marked from the snapshot may have died mid-window, but an over-mark is just
floating garbage; the STW retrace adds everything currently reachable on top. Rare,
bounded, guaranteed to terminate. Stats count overflows; if they ever show up in
benchmarks, the slab or the policy is wrong.

### 7.3 Kill switch

`DOTNET_GCConcurrent=0` (the stock knob) disables the coordinator: full collections run
the current STW path. Stage gating (§8) keeps this the default until stage 3 lands.

## 8. Implementation stages (each ends suite-green: 56/56 + 72/72, soak clean)

0. **Side mark bitmap** ✅ (2026-07-06): epoch marking replaced by the GC-private
   bitmap (§3), carve-time slice clears, range-check-before-marks orderings. The
   two-view-alias variant of this stage was built first and **failed its parity gate
   2.2–2.6×** (rows `m6s0-substrate`/`m6s0-frontzero`); the bitmap variant **passed at
   0.90–0.93× of baseline** (`m6s0-bitmap` vs `m6s0-base`) with GC counts and WS
   unchanged. Suite 56/56, unit 70/70.
1. **Protection machinery, still fully STW**: VEH handler, dirty-page bitmap,
   steady-state protection, carve-time unprotection, unprotect-before-write for the
   zeroer and sweep-plug paths, re-protector thread; full collections re-protect
   dirty∩live inside their (single) pause. No snapshot, no pre-images.
   *Exit: suite/soak green under permanent protection; measured mutator fault tax and
   pause re-protect cost recorded in experiments/results/.*
2. **Snapshot shadow-test**: pre-image slab + index + translation reader. Full marks
   stay STW but trace *through the translation layer*, and a debug mode runs both the
   translated and direct mark and asserts identical mark sets.
   *Exit: shadow assertion holds across the suite + soak with forced periodic fulls.*
3. **Go concurrent**: coordinator thread, pause A/B split, buffered-root capture at
   pause A, eager pinned capture, hole-carve gating, CarvedDuringMark sweep skip, card
   timing move, allocate-through, overflow fallback.
   *Exit: suite/soak green with DOTNET_GCConcurrent=1; pause histograms show pause A ≈
   young-pause scale and pause B ≈ sweep-only on soh/lohmix/pin/pinheavy.*
4. **Tune + publish**: re-protector policy, slab sizing, window-length accounting; rerun
   the full fairness matrix (pause profile changed — the frozen M7 pause-tuning list
   gets re-evaluated after this per the 2026-07-06 course-check).

## 9. Verification items against a real EE (tracked, not assumed)

- `BeforeGcScanRoots(is_bgc, is_concurrent)` / `GcStartWork` semantics — what the EE
  actually does differently (handle-scan flags, stack-scan modes).
- `RestartEE(finishedGC: false)` at pause A — confirm the bool's contract.
- `GetLoaderAllocatorObjectForGC` legality off-STW from the coordinator (fallback:
  defer collectible edges to pause B).
- `WaitUntilGCComplete`/`_gcEvent` semantics across a two-pause cycle.
- EE 8-byte accesses to `[obj−8, obj)`: epoch CAS already coexists with syncblock ops
  under STW (M4/M5); confirm no wide interlocked EE access spans the pad during the
  window (believed none on x64 — the pad exists *because* the header is 4 bytes).
- Our own VEH vs the CLR's handlers: registration order, and that we pass on every
  non-heap AV untouched.

## 10. Follow-ups this design deliberately defers

- **Concurrent sweep** (pause B → epsilon): sound once marks are final; requires
  re-architecting sweep/allocator/zeroer interleaving. The natural M6.5.
- **Young collections during the window**: pointless in this design (§1) *unless*
  windows grow long on huge heaps; revisit with evidence.
- **Sequential-fault prefetch** in the handler (unprotect K pages ahead on sequential
  patterns) — only if stage-1 numbers show array-write-heavy workloads paying.
- Linux port of the two-view substrate (memfd + double mmap).

## 11. Instrumentation (added at each stage, feeds experiments/results/)

Per full cycle: pause A ms (suspend / roots / eager-capture / arm breakdown), window ms,
pause B ms (EE-protocol / sweep breakdown), faults taken (with/without pre-image),
pre-image pages + slab high-water, pages re-protected (pause vs background), re-protector
backlog at arm, CarvedDuringMark region count, overflow events. Steady state: fault rate
outside windows, carve-unprotect counts. GCStats CSV columns extend accordingly.
