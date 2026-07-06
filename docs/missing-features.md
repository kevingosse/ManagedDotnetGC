# Missing features: what ManagedDotnetGC needs to run any .NET app reliably, forever

Scope: the GC stays a **non-generational, non-compacting, stop-the-world mark & sweep** by design.
This document only lists *correctness and liveness* gaps against the contract the CoreCLR EE
(`E:\git\runtime`, .NET 10) actually imposes on a standalone GC. Performance is explicitly out of scope.

Legend:
- 🟥 **Blocker** — crashes, corrupts memory, or hangs on workloads that plain, common .NET apps hit.
- 🟧 **Correctness** — wrong observable semantics or unbounded leak over time; an app can run into it and misbehave.
- 🟨 **Robustness** — only hit under specific-but-legitimate configurations (older runtimes, 32-bit, interpreter, Android, monitoring tools).
- ⬜ **Diagnostic** — no app-level breakage; tooling/observability only. Included because "reliably in production" usually implies "survives having `dotnet-counters` attached".

References are `runtime-file:line` for `E:\git\runtime\src\coreclr\...` and `gc-file:line` for this repo.

---

## 0. TL;DR — the list

| # | Item | Severity | Test gate (`GcFeature`) |
|---|------|----------|-------------------------|
| 1.1 | No GC trigger policy — collections happen only on explicit `GC.Collect()` | ✅ done (M2 region heap) | `GcTriggering` |
| 1.2 | Memory is never reused (known) — plus the correctness work reuse drags in (zeroing, brick-table reset, decommit) | ✅ done (M2 region heap) | `MemoryReuse` |
| 1.3 | OOM handling: `Alloc` must return `null`, never throw; today OOM = fail-fast | ✅ done (M2 region heap) | `HardLimitOom` |
| 2.1 | `GcStartWork` / `BeforeGcScanRoots` / `AfterGcScanRoots` / `GcDone` never called | ✅ done | `GcInternals` |
| 3.1 | Collectible types: LoaderAllocator objects are not kept alive during marking | ✅ done | `CollectibleAssemblies` |
| 3.2 | Ref-counted handles (COM / ComWrappers) never scanned, never cleared | ✅ done | `RefCountedHandles` |
| 3.3 | Objects awaiting finalization are marked too late in the cycle | ✅ done | `FinalizationQueueRoots` |
| 3.4 | Dependent handles: frozen-segment primaries break the fixpoint (hang + dropped values) | ✅ done | `FrozenDependentHandles` |
| 4.1 | Handle table can't store handle types 10/11 (`WEAK_INTERIOR_POINTER`, `CROSSREFERENCE`) → index-out-of-range | ✅ done | `NewHandleTypes` |
| 4.2 | `HNDTYPE_WEAK_INTERIOR_POINTER` semantics (collectible statics) | ✅ done (cleared with long-weak) | `NewHandleTypes` + `CollectibleAssemblies` |
| 4.3 | Legacy handle types for older EEs (async-pinned, sized-ref, weak-native-COM) | 🟨 | — (out of scope: .NET 10 only) |
| 4.4 | `TraceRefCountedHandles` stub | 🟨 | — (out of scope: macOS) |
| 5.1 | SuppressFinalize: suppressed objects are resurrected instead of dropped; skip path doesn't clear the bit | ✅ done | `SuppressFinalizeDrop` |
| 6.1 | `NotImplementedException` stubs reachable from public APIs (≈15 methods, each = fail-fast) | ✅ done | `ApiSurface`, `LatencyMode`, `NoGCRegion`, `EventCounters` |
| 6.2 | Generation numbering consistency (`MaxGeneration`, `WhichGeneration`, frozen = `INT32_MAX`) | ✅ done | `GenerationApis` (implemented) |
| 6.3 | `IsPromoted` / `GetContainingObject` / `IsHeapPointer` needed by the EE outside your own scan | ✅ done (with 6.1) | `GcInternals` |
| 7.1 | Write-barrier initialization passes null card table and zero heap bounds | ✅ done (lazy cards + eager bundles) | `GcInternals` |
| 8.1 | `alloc_bytes` accounting missing → negative `GC.GetAllocatedBytesForCurrentThread()` | ✅ done (M2 + totals) | `AllocationAccounting` |
| 8.2 | `GC_ALLOC_ALIGN8` / `ALIGN8_BIAS` (32-bit only) | 🟨 | — (out of scope: 32-bit) |
| 8.3 | GC-from-allocation must toggle preemptive mode before `SuspendEE` | ✅ done (M2 region heap) | `GcTriggering` |
| 9.1 | Conservative-GC / interpreter mode robustness | 🟨 | — (no test yet) |
| 9.2 | Android / `FEATURE_JAVAMARSHAL` GC bridge | 🟨 | — (out of scope: Android) |
| 10.x | Diagnostics: `Diag*` throwing stubs, DAC vars, event sink, memory-info numbers | ⬜ Diag no-ops + memory-info numbers done; event sink and DAC vars remain | `GcEvents`, `MemoryInfo`, `EventCounters` |

The *Test gate* column is the `GcFeature` value gating the item's tests. To start working on an
item: remove that value from `pendingFeatures` in `TestApp/Program.cs` and run the suite — its
tests flip from skipped to failing. `—` means no coverage (out of scope on win-x64 / .NET 10, or
not testable from managed code).

The stock GC's own ordering, used as the reference throughout: `runtime-gc/mark_phase.cpp:3033-3498`
(the mark-phase driver) and `runtime-gc/interface.cpp:1827` (`GarbageCollectGeneration`).

---

## 1. Heap lifecycle

### 1.1 🟥 No GC trigger policy

`GCHeap.Alloc` (gc-GCHeap.cs:129) never initiates a collection: when a segment fills up it just
allocates a new one. The only way a collection ever runs is an explicit `GC.Collect()`. In CoreCLR,
**the GC owns the decision to collect** — the EE never calls `GarbageCollect` on the app's behalf;
allocation is expected to trigger collection internally when a budget is exhausted
(`runtime-vm/gchelpers.cpp:420-423`, `runtime-gc/interface.cpp:1827`).

Consequence: any app that doesn't call `GC.Collect()` itself (i.e., almost all of them) grows
monotonically until the 2 TB reservation or physical memory runs out. This and 1.2 are jointly the
core of "run forever".

What's needed: an allocation budget (bytes allocated since last GC) checked in the slow path of
`Alloc`, plus optionally a memory-load trigger. Precision is irrelevant; existence is not.
See 8.3 for a mandatory detail (preemptive-mode toggle) when you do this.

### 1.2 🟥 Memory reuse (the one you named) — and what it drags in

Sweep (gc-GCHeap.Sweep.cs:10) creates free objects but nothing ever consumes them; `SegmentManager.FreeSegment`
(gc-SegmentManager.cs:43) has no caller. When you implement reuse, these become correctness items
(they're all free today only because memory is virgin):

- **Zeroing**: the JIT and EE assume newly allocated objects are zeroed unless
  `GC_ALLOC_ZEROING_OPTIONAL` (`runtime-gc/gcinterface.h:1090`), and the pre-object sync-block word
  **must** be zero even in the optional case (`runtime-gc/interface.cpp:1456-1460`). Your sweep already
  zeroes reclaimed ranges (gc-GCHeap.Sweep.cs:27), which is sufficient — keep that invariant when free
  lists appear, including for ranges handed out as fresh allocation contexts.
- **Brick-table staleness** (gc-Segment.cs:39): entries are only ever raised, never reset when objects die
  and memory is re-carved. After reuse, `FindClosestObjectBelow` can return a position that is no longer
  an object start → interior-pointer resolution walks garbage → marking corruption. The brick table must
  be rebuilt/adjusted on sweep or on re-allocation of a range.
- **Stale sync-block indices**: cleaning dead objects' memory (you do) plus the sync-block weak scan
  (you do) covers this; just preserve both when the allocator changes.
- **Decommit** (optional for correctness, mandatory for "forever" on real machines): dead dedicated
  segments should be returned via `VirtualFree`/`MEM_DECOMMIT`; the plumbing exists in
  gc-NativeAllocator.cs:128 but is unused.
- The 2 TB address-space reservation is a hard wall (gc-GCHeap.cs:14). With reuse it stops being a
  practical limit, but exhaustion must still surface as managed OOM (1.3), not a fail-fast.

### 1.3 🟥 OOM must surface as `null` from `Alloc`

The EE's contract: `IGCHeap::Alloc` returns `nullptr` on failure and the EE throws
`OutOfMemoryException` (`runtime-vm/gchelpers.cpp:498-504`; contract comment `runtime-gc/gcinterface.h:917-930`).
Today `NativeAllocator.Allocate` throws a managed `OutOfMemoryException`
(gc-NativeAllocator.cs:63,76) that propagates out of an `UnmanagedCallersOnly` frame → NativeAOT
fail-fast. Additionally:

- `Alloc` should attempt a collection before giving up (once 1.1 exists).
- Oversized/overflowing requests are already rejected by the EE before `Alloc` is called
  (`runtime-vm/gchelpers.cpp:302-330, 610-647`) — you don't need to validate sizes, only fail cleanly.
- `RegisterForFinalization` may return `false` to signal "couldn't grow the finalization queue"; the EE
  converts that to a managed OOM (`runtime-vm/comutilnative.cpp:1039-1042`). Your version grows a managed
  array (gc-GCHeap.Finalization.cs:59) — wrap the growth so failure returns `false` instead of throwing.

---

## 2. The GC↔EE brackets around a collection

### 2.1 🟥 `GcStartWork`, `BeforeGcScanRoots`, `AfterGcScanRoots`, `GcDone` are never called

`GarbageCollect` (gc-GCHeap.cs:79) goes straight `SuspendEE → mark → sweep → RestartEE`. The stock GC
interleaves four EE callbacks, and each one does real work (`runtime-vm/gcenv.ee.cpp`):

| Callback | When (stock) | What the EE does | If skipped |
|---|---|---|---|
| `GcStartWork(condemned, max_gen)` | GC start, after suspend (`runtime-gc/collect.cpp:982`) | `ExecutionManager::CleanupCodeHeaps()` (reclaims JIT code of unloaded methods), type-system log cleanup, `Interop::OnGCStarted` → ComWrappers `BeginExternalObjectReferenceTracking` (`gcenv.ee.cpp:339-360`) | JIT code heaps leak forever; ComWrappers/WinRT reference tracking never engages |
| `BeforeGcScanRoots(condemned, false, false)` | just before root scan (`runtime-gc/mark_phase.cpp:3033`) | ObjC interop begin, IL-stub byref validation (`gcenv.ee.cpp:78-96`) | ObjC interop breaks (macOS); harmless on Windows but free to call |
| `AfterGcScanRoots(condemned, max_gen, sc)` | after all strong marking (stack+handles+dependent), **before** weak clearing (`runtime-gc/mark_phase.cpp:3385`) | `DetachRCWs()` — detaches **unmarked** RCWs so the RCW cache can't hand out dead wrappers; ComWrappers `DetachNonPromotedObjects` (`gcenv.ee.cpp:100-117`) | COM/`ComWrappers` objects resurrect or leak; use-after-free in interop-heavy apps (WinForms/WPF/WinUI) |
| `GcDone(condemned)` | GC end (`runtime-gc/collect.cpp:1493`) | `Interop::OnGCFinished` → `EndExternalObjectReferenceTracking` (`gcenv.ee.cpp:367-377`) | pairs with `GcStartWork`; tracker never completes |

All four are already bound in gc-Interfaces/IGCToCLR.cs:27-44 — they just need call sites. Note:
- Report `condemned = 2` so the "full GC" paths engage (the ComWrappers tracker only runs when
  `condemned >= 2`, `runtime-vm/interoplibinterface_shared.cpp:82-98`). You already use 2 in `GcScanRoots`.
- `AfterGcScanRoots` internally calls back into **your** `IsPromoted` (see 6.3) — implement that first
  or the callback itself will fail-fast.

### 2.2 ✅ For the record: things you already do correctly here

Suspension bracket, `FixAllocContexts` before marking (including the zero-both-fields rule,
`runtime-gc/gcinterface.ee.h:283-288`), sync-block weak scan positioned after long-weak clearing
(matches `runtime-gc/mark_phase.cpp:3498`), `EnableFinalization` after restart, short-weak before /
long-weak+dependent after the finalization scan (matches `mark_phase.cpp:3424/3489`),
`EagerFinalized` consultation, normal-before-critical finalizer dequeue order
(matches `runtime-gc/finalization.cpp:224-247`), free-object plugging for walkability, and the
`SetGCInProgress`/`Set/ResetWaitForGCEvent` surface — which it turns out the **EE drives itself** from
`SuspendEE`/`RestartEE` (`runtime-vm/threadsuspend.cpp:3252, 3577, 5363, 5505`). Only nit there: create
`_gcEvent` initially **set** (stock does) so a racing `WaitUntilGCComplete` before the first GC can't hang.

---

## 3. Marking-protocol gaps

### 3.1 🟥 Collectible types: the LoaderAllocator edge

The rule (`runtime-gc/gc.cpp:7470`, `go_through_object_cl`): when marking an object whose MethodTable is
**collectible**, the GC must *also mark that type's managed `LoaderAllocator` object*, obtained via
`IGCToCLR.GetLoaderAllocatorObjectForGC(obj)` (`runtime-vm/gcenv.ee.cpp:514` →
`runtime-vm/methodtable.cpp:8737`). The LoaderAllocator object is only referenced from native code via a
*long weak* handle (`runtime-vm/loaderallocator.cpp:1069`) — the GC-side edge is exactly what keeps a
collectible assembly alive while instances of its types exist.

Your `EnumerateObjectReferences` (gc-GCObject.cs:68) never adds this edge. Consequence: with any
collectible `AssemblyLoadContext` (plugin systems, test runners, hot reload) the LoaderAllocator dies
while instances live → the native loader heaps (MethodTables, JIT code) are freed → the next virtual
call or GC touching a surviving instance reads freed memory. Hard crash, hard to debug.

Implementation notes:
- The `Collectible` flag is `0x00200000` in the MT flags (`runtime-vm/methodtable.h:3812`; the GC-side
  mirror with the .NET 8 compat shim is `runtime-gc/env/gcenv.object.h:54,94`). Add it to
  gc-MethodTable.cs and, in the mark loop, for collectible MTs push
  `GetLoaderAllocatorObjectForGC(obj)` (null-checked) alongside the field references.
- The EE additionally reports LoaderAllocator objects of *executing* code as stack roots by itself
  (`GcReportLoaderAllocator`, `runtime-vm/gcenv.ee.common.cpp:222`) — that part arrives through your
  existing `ScanRoots` callback and needs nothing.

### 3.2 🟥 Ref-counted handles (`HNDTYPE_REFCOUNTED`) are invisible to your GC

`MarkPhase` scans only `STRONG`+`PINNED` (gc-GCHeap.Mark.cs:77) and clears only
`WEAK_SHORT`/`WEAK_LONG`/`DEPENDENT` (gc-GCHeap.Mark.cs:21-24). Ref-counted handles — created for every
classic COM CCW and every `ComWrappers`-managed object (`runtime-vm/appdomain.hpp:792`) — need both:

- **Scan** (with the strong handles): for each refcounted handle whose target isn't already marked, call
  `IGCToCLR.RefCountedHandleCallbacks(obj)` (bound at gc-Interfaces/IGCToCLR.cs:50; EE impl
  `runtime-vm/gcenv.ee.cpp:379-409`) and promote the target iff it returns true (stock:
  `PromoteRefCounted`, `runtime-gc/objecthandle.cpp:80-108, 1150-1177`).
- **Clear** (with the long-weak handles, i.e. *after* the finalization scan): null the handle if the
  target is still unmarked (stock includes `REFCOUNTED` in `Ref_CheckReachable`'s list,
  `runtime-gc/objecthandle.cpp:1222`).

Failure mode without it: a `[ComVisible]` object handed to native code with a positive external ref
count is collected → native calls into a freed CCW; or (if you conservatively treated them as strong)
CCWs leak forever. Affects COM interop, drag-and-drop, WinRT/CsWinRT, WinUI 3.

### 3.3 🟧 Objects awaiting finalization must be roots *early* in the mark phase

Stock order (`runtime-gc/mark_phase.cpp:3176`): the f-reachable objects (queued for the finalizer thread
in a previous GC, not yet finalized) are promoted as roots right after stack scanning — **before**
handle scanning, dependent-handle fixpoint, `AfterGcScanRoots` (RCW detach) and short-weak clearing
(`runtime-gc/finalization.cpp:289-310`).

Your equivalent objects (`_freachableQueue`/`_criticalFreachableQueue` leftovers) only get marked inside
`ScanForFinalization` (gc-GCHeap.Mark.cs:56), which runs *after* short-weak clearing. Consequences:

- To be precise about the weak-handle impact: a short weak that exists at the GC that first *discovers*
  the object dead is cleared in that GC, before the finalization scan — identical in both GCs, and the
  documented semantic. The divergence is only for short weaks **created during the pending-finalization
  window** (obtainable legitimately: `WeakReference(trackResurrection: true).Target`, another object's
  finalizer, resurrection). At the *next* GC, stock keeps them alive because the f-reachable queue is a
  strong root from step one; yours clears them because the queue is only marked after short-weak clearing.
  Narrow, but observably different from C#.
- The stronger reason to fix the ordering: once 3.2/2.1 exist, the EE consults `IsPromoted` mid-GC
  (RCW detach, refcounted-handle decisions, ComWrappers `DetachNonPromotedObjects`) and would treat
  objects whose finalizers haven't run yet as dead — detaching interop state a finalizer still needs.

Fix: at the top of `MarkPhase`, mark everything in both f-reachable queues (transitively), then keep
`ScanForFinalization` for the newly dead only.

### 3.4 🟥 Dependent handles vs frozen-segment (and other non-heap) primaries

`ScanDependentHandles` (gc-GCHeap.Mark.cs:87) tests `primary->IsMarked()`. Frozen-segment objects are
never marked by your GC (by design). Two bugs follow:

1. **Dropped values**: primary = interned string literal (allocated on the frozen object heap since
   .NET 8, `runtime-vm/gchelpers.cpp:1156`) → `primary->IsMarked()` is always false → the secondary is
   never promoted → a `ConditionalWeakTable` keyed on a literal string (or any frozen object) has its
   value collected while the key is eternally alive → `GetValue` returns a dangling/collected object.
2. **Infinite loop**: primary marked, secondary frozen → `!secondary->IsMarked()` is permanently true →
   `markedObjects = true` every pass → `MarkPhase` never terminates. A `CWT<object, string>` whose value
   is a literal is enough.

Rule to implement: "is alive" for dependent-handle purposes = `IsMarked() || !_nativeAllocator.IsInRange(obj)`
(same treatment you already give weak handles in `ClearHandles`, gc-GCHeap.Sweep.cs:40), and "needs
promotion" must be false for out-of-range secondaries. Stock semantics reference:
`runtime-gc/objecthandle.cpp:230-292` (promotion + pair clearing — note stock clears **both** slots,
which your `handle->Clear()` already matches).

How the stock GC avoids both defects: (a) `mark_ro_segments` artificially sets the mark bit on **every**
object in registered frozen segments at the start of each mark phase (`runtime-gc/mark_phase.cpp:3652-3665` —
"all objects on ro segs are live … artificially mark all of them"), and (b) every liveness question in
handle scanning funnels through `GCHeap::IsPromoted`, which treats **any address outside the GC heap
bounds as promoted** (`runtime-gc/interface.cpp:783-784`; regions variant `:790`), applied to primaries
and secondaries alike (`runtime-gc/objecthandle.cpp:244-246`). So "outside the managed heap ⇒ always
alive" is exactly the stock behavior.

### 3.5 🟩 Frozen segments' outgoing references are never scanned — safe by construction (verified 2026-07-06)

Neither the full mark nor the M4 card scan ever enumerates the fields of frozen-segment objects
(they are outside the reservation; stores into them don't even reach our card table, since the
checked barrier tests the destination against the heap bounds first). If a frozen object could
reference a GC-heap object, that edge would be invisible and the target collectable while
reachable — even before M4.

Verified against the runtime sources that this cannot happen today: everything the EE puts on the
frozen object heap is reference-free or reference-inert —

- string literals (`runtime/vm/gchelpers.cpp:1158`) — no ref fields;
- constant primitive arrays (`gchelpers.cpp:759`) — no ref elements;
- boxed statics, gated by `_ASSERT(!pFieldMT->ContainsGCPointers())` (`runtime/vm/methodtable.cpp:3487`);
- `RuntimeType` instances for **non-collectible** types only (`runtime/vm/typehandle.cpp:349-365`,
  under `!allocator->CanUnload()`): its single ref field `m_keepalive` is only used for collectible
  types and stays null; the member cache hangs off an `IntPtr` GCHandle, not a direct ref.

So "frozen objects never point into the GC heap" is a runtime invariant we inherit. **Re-verify it
when bumping the target runtime** — if a future runtime relaxes it, frozen segments need the stock
treatment (artificially live + card-scanned for outgoing refs, `runtime-gc/mark_phase.cpp:3652`).

---

## 4. Handle-table structural gaps

### 4.1 🟥 The handle-type enum stops at 9; .NET 10 goes to 11

gc-Types.cs:193 declares `Max = HNDTYPE_WEAK_NATIVE_COM (9)`, and `GCHandleStore` sizes `_lists` as
`Max + 1` (gc-GCHandleStore.cs:9,18). The current runtime defines
(`runtime-gc/gcinterface.h:417-557`):

- `HNDTYPE_WEAK_INTERIOR_POINTER = 10` — **actively created** by the EE for collectible statics
  (`runtime-vm/loaderallocator.cpp:2385, 2465, 2518` via `AppDomain::CreateWeakInteriorHandle`,
  `runtime-vm/appdomain.hpp:779`).
- `HNDTYPE_CROSSREFERENCE = 11` — Java GC bridge (`FEATURE_JAVAMARSHAL`; Android, plus Debug/Checked
  builds of the runtime).

First collectible assembly → `CreateHandleWithExtraInfo` indexes `_lists[10]` → `IndexOutOfRangeException`
→ fail-fast. Extend the enum and the per-type lists (stock reserves 13 slots,
`runtime-gc/handletableconstants.h:17`).

Isolated tests (gate `NewHandleTypes`): `WeakInteriorHandleTest` — touching a collectible type's
statics triggers type-10 creation without needing 3.1 or unload; `CrossReferenceHandleTest` —
type 11 has no public API on win-x64, but `GCHandle.InternalAlloc` via reflection reaches
`CreateHandleOfType` with an arbitrary type (the FCall's range check is a compiled-out assert on
release runtimes, `runtime-vm/marshalnative.cpp:342`).

### 4.2 🟥 `HNDTYPE_WEAK_INTERIOR_POINTER` semantics

Cheap for a non-moving GC. Contract (`runtime-gc/objecthandle.cpp:150-189, 1224, 1394-1426`):

- **Never a root** (weak on the primary).
- **Cleared with the long-weak handles** (it's in `Ref_CheckReachable`'s type list), i.e. after the
  finalization scan.
- Extra-info word = address of a location holding an interior pointer into the primary; only compacting
  GCs must update it — you just need to null the handle when the primary dies.

Add it to the `ClearHandles([WEAK_LONG, DEPENDENT, …])` list.

### 4.3 🟨 Legacy handle types (only if you want to support .NET 6/7 hosts)

On .NET 8+ the VM never creates these (`runtime-gc/gcinterface.h:504, 517, 534` — "no longer used in the
VM"); a standalone GC that only targets .NET 10 can ignore their scanning semantics. For completeness,
the semantics older EEs expect:
`ASYNCPINNED (7)` = pinned root + `WalkAsyncPinnedForPromotion` for the Overlapped's buffer array
(gc-Interfaces/IGCToCLR.cs:259); `SIZEDREF (8)` = strong root (size bookkeeping optional);
`WEAK_NATIVE_COM (9)` = short-weak clearing. `HNDTYPE_VARIABLE (4)` is dead code in all versions —
keep the slot, never scan it.

### 4.4 🟨 `TraceRefCountedHandles` no-op

gc-GCHandleManager.cs:115 stubs it. It's used by ObjC interop (`FEATURE_OBJCMARSHAL`) to enumerate
ref-counted handles — irrelevant on Windows, required on macOS with `ObjectiveCMarshal`.

---

## 5. Finalization refinements

The big pieces (registration at alloc, resurrection with full closure, critical/normal split via the MT
flag, eager finalization of WeakReference, ReRegister-via-header-bit, normal-drains-before-critical) are
all present and match `runtime-gc/finalization.cpp`. Two divergences remain:

### 5.1 🟧 SuppressFinalize handling at scan and dequeue

Stock `ScanForFinalization` (`runtime-gc/finalization.cpp:367-377`): a **dead + suppressed** finalizable
object is *dropped* from the queue (and the bit cleared) — it is **not resurrected**. Your
`PrepareForFinalization` (gc-GCHeap.Finalization.cs:72) doesn't check the bit, so suppressed dead objects
are moved to the f-reachable queue, *resurrected* (marked with their whole graph), and only skipped at
dequeue time — surviving at least one extra GC for no reason.

Worse, the dequeue skip (gc-GCHeap.Finalization.cs:33-36) doesn't **clear** the bit, while stock does
(`runtime-vm/finalizerthread.cpp:253-259`). Sequence that goes wrong: suppress → object dies → skipped
at dequeue (bit still set, object now out of the queue) → object is resurrected by other means and
`GC.ReRegisterForFinalize` is called → your `RegisterForFinalization` sees the bit, clears it and
returns **without enqueuing** (gc-GCHeap.Finalization.cs:51-55) — that early-return is only valid while
the object is still in the queue (`runtime-gc/interface.cpp:2535-2539`). Result: finalizer never runs.

Fix: in `PrepareForFinalization`, treat `HasFinalizerRun` like `EagerFinalized` (drop + clear bit,
don't enqueue); clear the bit when skipping in `GetNextFinalizable`.

### 5.2 ⬜ Signal semantics

You call `EnableFinalization(count > 0)`; stock calls it (with `true`) only when there is work
(`runtime-gc/interface.cpp:1960`). Calling with `false` is harmless today but pointless — minor.

---

## 6. The `IGCHeap` surface: every throwing stub is a process crash

Under NativeAOT, a `NotImplementedException` escaping an `UnmanagedCallersOnly` boundary is a fail-fast.
So every stub in gc-GCHeap.NotImplemented.cs that the EE or BCL can reach is a latent crash, not a TODO.
Reachable ones, with the concrete trigger and the correct cheap implementation:

| Method (gc-GCHeap.NotImplemented.cs) | Reached from | Correct stub for this GC |
|---|---|---|
| `WhichGeneration` (:89) | `GC.GetGeneration(obj)` (`runtime-vm/comutilnative.cpp:630`), sync-block bookkeeping (`runtime-vm/syncblk.cpp:1156`) | ✅ implemented: `0` for heap objects, **`INT32_MAX` for frozen-segment / non-heap** (`runtime-gc/gcinterface.h:804`) — see 6.2 |
| `GetGenerationBudget` (:270) | **`RuntimeEventSource` EventCounter `gen-0-gc-budget`** — polled the moment `dotnet-counters`/any EventListener attaches (`runtime-libs/RuntimeEventSource.cs:85` → QCall `runtime-vm/comutilnative.cpp:1174-1197`) | any constant (stock semantics: gen budget in bytes) |
| `GetGcLatencyMode` / `SetGcLatencyMode` (:61/:67) | `GCSettings.LatencyMode` get/set — used by real libraries (SustainedLowLatency toggles) | store + return the value; return success (0) from the setter |
| `StartNoGCRegion` / `EndNoGCRegion` (:95/:101) | `GC.TryStartNoGCRegion` / `EndNoGCRegion` | **declining is legal**: return `start_no_gc_no_memory` → managed `false`; `end_no_gc_not_in_progress` (`runtime-gc/gcinterface.h:351-386`, mapping `GC.CoreCLR.cs:507-578`). Or implement for real: "success" promises **no GC until End** (budget pre-commit) |
| `EnableNoGCRegionCallback` (:264) | `GC.RegisterNoGCRegionCallback` | `not_started` |
| `RefreshMemoryLimit` (:258) | `GC.RefreshMemoryLimit()` | `0` (`refresh_success`) |
| `IsPromoted` (:107) | ComWrappers `DetachNonPromotedObjects` + RCW code during **your own** `AfterGcScanRoots` call (`runtime-vm/interoplibinterface_comwrappers.cpp:275`, `runtime-vm/runtimecallablewrapper.cpp:828`); also the pattern for weak scans (`runtime-gc/gcscan.cpp:116`) | during GC: `obj->IsMarked() \|\| !IsInRange(obj)`; outside GC: `true` |
| `GetContainingObject` (:166) | profiler root scanning (`runtime-vm/gcenv.ee.cpp:606`), byref validation (`runtime-vm/stubhelpers.cpp:140`) | you already have the machinery — brick table + walk from `ScanRoots`'s interior path; factor it out and return null for non-heap |
| `IsHeapPointer` (:113) | object validation, marshaling validation, GCStress (`runtime-vm/object.cpp:558`, `runtime-vm/stubhelpers.cpp:620`) | `_nativeAllocator.IsInRange(ptr)` (plus frozen segments if `small_heap_only == false`… stock excludes frozen: keep it heap-only) |
| `IsEphemeral` (:121) | sync-block ephemeral path (`runtime-vm/syncblk.cpp:929`) — unreachable today only because `condemned(2) < maxgen(0)` is false | `false` |
| `IsLargeObject` (:148) | profiler / ETW paths | `false` (or size ≥ 85000) |
| `RuntimeStructuresValid` (:127) | debugger/DAC-adjacent checks | `true` |
| `GetGenerationWithRange` (:252) | profiler generation-bounds walks | fill with one pseudo-generation spanning the heap |
| `IsValidSegmentSize`/`IsValidGen0MaxSize`/`GetValidSegmentSize`/`SetReservedVMLimit` (:13-35) | hosting/config paths | `true`/`true`/a constant/no-op |
| `WaitUntilConcurrentGCComplete(Async)`, `Temporary(En/Dis)ableConcurrentGC` (:37-59) | hosting APIs, profiler `ForceGC` prep | no-op / `S_OK` — "no concurrent GC" means always complete |
| `SetYieldProcessorScalingFactor` (:138) | EE startup measurement on some paths | no-op |
| `Destructor`, `Uproot`, handle-store `Destructor` | shutdown | no-op |

Also in this category though not throwing: `WaitForFullGCApproach/Complete` correctly return
`wait_full_gc_na = 4`, and `RegisterForFullGCNotification` returning `false` maps to a documented
managed `InvalidOperationException` — both fine.

### 6.2 ✅ Generation-numbering consistency — implemented (the single-generation story)

The chosen story: a single generation, 0, everywhere. `GetMaxGeneration() = 0` (gc-GCHeap.Stats.cs:34),
`WhichGeneration = 0` for heap objects / `INT32_MAX` for frozen and other non-heap objects
(gc-GCHeap.NotImplemented.cs:89, matching stock's out-of-range answer, `runtime-gc/interface.cpp:848-854`),
`CollectionCount(g) = _gcCount` for all g. The hard constraint that made the *pair* matter:
`WhichGeneration(obj) <= GetMaxGeneration()` — the EE indexes arrays sized `GetMaxGeneration() + 1`
with per-object generations (dead-thread GC trigger heuristic, `runtime-vm/threads.cpp:4154/4178`).
No VM consumer requires `MaxGeneration = 2`; the sync-block ephemeral branch stays off
(`0 < 0` is false, `runtime-vm/syncblk.cpp:885`) and `FinalizerThreadWait`'s
`CollectionCount(GetMaxGeneration())` (`runtime-vm/finalizerthread.cpp:740-778`) stays monotonic.
Loose end (cosmetic): `GetCondemnedGeneration()` still returns 2 (gc-GCHeap.NotImplemented.cs:122);
0 would be more coherent, both behave correctly at the existing gates. Covered by `GenerationApiTest`.

### 6.3 (folded into the table above — `IsPromoted`, `GetContainingObject`, `IsHeapPointer` are prerequisites for 2.1/3.2.)

---

## 7. Write barrier initialization

🟨 `Initialize` passes only `ephemeral_low = -1` (gc-GCHeap.cs:65-72); `card_table`, `lowest_address`,
`highest_address` stay null/zero. The ephemeral trick is sound for the release x64 barriers
(`runtime-vm/amd64/JitHelpers_Fast.asm:152-155` — value below `g_ephemeral_low` exits before any card
write), but the documented `WriteBarrierOp::Initialize` contract wants a non-null card table and real
bounds, and debug-flavor runtimes assert on it (`runtime-vm/gcenv.ee.cpp:1105-1116`). Cheap fix: pass
`lowest_address`/`highest_address` = your 2 TB reservation range and a small dummy card-table allocation
sized for it (it will never be dirtied given the ephemeral trick). That also makes
`g_lowest/g_highest_address`-based VM fast checks truthful, and covers non-x64 barrier flavors whose
instruction order differs. Bonus: with fixed bounds you never need `StompResize`.

---

## 8. Allocation details

### 8.1 ⬜ `alloc_bytes` accounting

You never touch `gc_alloc_context.alloc_bytes`/`alloc_bytes_uoh`.
`GC.GetAllocatedBytesForCurrentThread()` computes `alloc_bytes + alloc_bytes_uoh − (alloc_limit − alloc_ptr)`
(`runtime-vm/comutilnative.cpp:847-858`) → **negative numbers** with a live context. Increment
`alloc_bytes` by the window size whenever you hand out/refresh a context. Same family:
`GetTotalAllocatedBytes() = 0`, `GetTotalBytesInUse() = 0` (→ `GC.GetTotalMemory` returns 0),
`GetMemoryInfo` zeros — all wrong-number-only, but trivially computable from `SegmentManager`.

### 8.2 🟨 `GC_ALLOC_ALIGN8` / `GC_ALLOC_ALIGN8_BIAS`

Only emitted on 32-bit (x86/ARM32) for `double`/`long` alignment (`runtime-vm/gchelpers.cpp:678-690,
1271-1285`). Irrelevant while you target win-x64 exclusively; a hard requirement the day you don't.

### 8.3 🟥 (with 1.1) Triggering GC from `Alloc` requires the preemptive-mode dance

`GC.Collect` reaches you on a thread already in preemptive mode, but an allocation-triggered GC starts
on a thread in **cooperative** mode; calling `SuspendEE` from cooperative mode self-deadlocks. Stock
switches first (`runtime-gc/interface.cpp:1890-1895`). Use the already-bound
`EnablePreemptiveGC`/`DisablePreemptiveGC` (gc-Interfaces/IGCToCLR.cs:79-84) around the collection, and
add a real GC lock so two racing triggers don't run two back-to-back collections.

### 8.4 ⬜ `GetLOHThreshold() = nint.MaxValue`

Means the EE never sets `GC_ALLOC_LARGE_OBJECT_HEAP` (`runtime-vm/gchelpers.cpp:664` requires
`size >= 85000 && size >= GetLOHThreshold()`). Harmless with a unified heap; return 85000 if you ever
want the flag for bookkeeping.

---

## 9. Conditional environments

### 9.1 🟨 Conservative GC / interpreter mode

With `DOTNET_GCConservative=1` — and **unconditionally under the interpreter**
(`runtime-vm/gcenv.ee.cpp:1236-1242`) — every plausible stack word is reported as
`GC_CALL_INTERIOR | GC_CALL_PINNED` (`runtime-vm/gcenv.ee.cpp:156-189`). Your interior resolution
already rejects out-of-range pointers; two hardening points: don't treat a resolved **free object** as
live (stock skips them, `runtime-gc/interface.cpp:1100`), and make interior lookup robust against
pointers into segment metadata (brick table region / headers).

### 9.2 🟨 Android / `FEATURE_JAVAMARSHAL`

`HNDTYPE_CROSSREFERENCE` handles + `GcProcessBridgeObjects` + `TriggerClientBridgeProcessing`
(`runtime-gc/mark_phase.cpp:3334`, `runtime-vm/gcenv.ee.cpp:411`) form the .NET↔Java bridge. Not needed
on Windows; allocate the handle slot (4.1) so its mere existence can't crash you, and ignore the rest
unless you target Android.

---

## 10. Diagnostics & tooling (explicitly outside "app correctness", included for honesty)

- 🟥-under-tooling: every `Diag*` method throws (gc-GCHeap.NotImplemented.cs:172-236). A profiler attach,
  `dotnet-gcdump`, or an EventPipe session requesting heap walks calls into them → fail-fast **of the
  app**. Minimum bar: make them all silent no-ops. (Same for `StressHeap` if anyone sets GCStress.)
- ⬜ `DiagGCStart`/`DiagGCEnd`/`DiagUpdateGenerationBounds`/`DiagWalkFReachableObjects` (IGCToCLR side)
  are never invoked → profilers see no GC events; ETW/EventPipe GC events absent (`EventSink` unused).
- ⬜ `GcDacVars` is never populated → SOS/`dotnet-dump`/WinDbg can't inspect the heap (you compensate
  with your own DAC trick for names, gc-Dac/DacManager.cs).
- ⬜ EventCounters coherence: `gc-heap-size` (→ `GetTotalMemory`), `gen-x-gc-count`
  (→ `CollectionCount`), `GetMemoryLoad`, `GetLastGCPercentTimeInGC` — all zeros/synthetic today.

---

## Appendix A — target collection cycle (stock-equivalent order for this design)

```text
lock(gc)                                    // one collection at a time (8.3)
if cooperative: EnablePreemptiveGC()        // only for alloc-triggered GCs (8.3)
SuspendEE(SUSPEND_FOR_GC)
GcStartWork(2, 2)                                            // 2.1
FixAllocContexts()                                           // ✅ have
BeforeGcScanRoots(2, false, false)                           // 2.1
mark f-reachable queues (prior GCs' pending finalizables)    // 3.3
GcScanRoots(promote, 2, 2, sc)                               // ✅ have (stack, statics, LA-of-running-code)
    └── each mark: collectible MT ⇒ also mark LoaderAllocator object   // 3.1
scan STRONG + PINNED handles                                 // ✅ have
scan REFCOUNTED handles via RefCountedHandleCallbacks        // 3.2
dependent-handle fixpoint (frozen/non-heap primary = alive)  // ✅ have + 3.4
AfterGcScanRoots(2, 2, sc)                                   // 2.1 (needs IsPromoted)
clear WEAK_SHORT (+ WEAK_NATIVE_COM if supporting old EEs)   // ✅ have
ScanForFinalization: EagerFinalized → suppressed = drop+clearbit → resurrect rest  // ✅ have + 5.1
dependent-handle fixpoint again                              // ✅ have
clear WEAK_LONG + DEPENDENT + REFCOUNTED + WEAK_INTERIOR_POINTER      // ✅ have + 3.2 + 4.2
SyncBlockCacheWeakPtrScan(...)                               // ✅ have
sweep (rebuild brick tables, feed free lists, decommit)      // 1.2
GcDone(2)                                                    // 2.1
RestartEE(true)
EnableFinalization(pending > 0)                              // ✅ have
```

## Appendix B — implementation bugs noticed in passing (not "missing features")

1. **Dedicated large-segment sizing** (gc-GCHeap.cs:160-180): the segment is allocated with exactly
   `size` bytes of object space but the object is placed at `ObjectStart + IntPtr.Size`, so its tail
   extends `IntPtr.Size` bytes past `End`. Page-rounding in `NativeAllocator.Allocate` usually hides it;
   an allocation whose header+brick+size lands exactly on a page boundary AVs. Allocate
   `size + SizeOfObject` like the normal path does.
2. **`_gcEvent` initial state** — see 2.2; create it set.
3. **`GetNextFinalizable` doesn't clear `HasFinalizerRun` when skipping** — covered in 5.1 but it's a
   one-line fix independent of the rest.
