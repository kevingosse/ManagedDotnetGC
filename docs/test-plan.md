# Test plan for the missing-features backlog

Goal: for every item in `missing-features.md`, either a test that is **green on the stock GC and red on
ManagedDotnetGC until the feature is implemented**, or an explicit justification for why no such test is
practical. Constraints:

- Every test must run (and pass) on the stock GC — that's what validates the test itself. The only
  exception category is tests that *require* the custom GC API (like the existing sync-block tests).
- New tests are **feature-gated**: tests of a pending feature are skipped unless opted in with
  `--feature X` / `--all-features`, so the suite stays green while features land one by one. The gate
  applies identically on both GCs — a no-arg run means the same thing everywhere, and no GC detection
  is involved (pending-feature tests are exactly the ones that may fail-fast the custom GC, so a
  detection misfire would crash the suite instead of skipping). The stock-GC validation run passes
  `--all-features` explicitly.
- Scope: .NET 10, win-x64 only. Everything tied to 32-bit (`ALIGN8`), Android (`CROSSREFERENCE` bridge),
  older EEs (async-pinned / sized-ref / weak-native-COM), conservative/interpreter mode is **out of scope**.

A general note on methodology: several of these tests pin down semantics I inferred from reading the
runtime (e.g. exactly when a ComWrappers CCW counts as "rooted", what `GC.GetGeneration` returns for a
frozen literal). The workflow is: write the test, run it on the **stock** GC first, adjust the
assertions until they describe what stock actually does — at that point the test *is* the spec, which
is exactly the philosophy of this suite.

---

## 1. Infrastructure changes (prerequisite, small)

### 1.1 Skip support + feature gating in `TestRunner` — ✅ implemented

- `GcFeature` enum (`TestApp.TestFramework/GcFeature.cs`): one value per functional area. **Every**
  test declares its feature via the `TestBase` constructor — the existing 32 tests are decorated too
  (`Allocation`, `Marking`, `Stress`, `InteriorPointers`, `Handles`, `WeakReferences`,
  `DependentHandles`, `Finalization`, `FrozenSegments`, `SyncBlocks`), plus one pending value per
  missing feature (`CollectibleAssemblies`, `RefCountedHandles`, `FinalizationQueueRoots`,
  `FrozenDependentHandles`, `SuppressFinalizeDrop`, `ApiSurface`, `LatencyMode`, `NoGCRegion`,
  `EventCounters`, `AllocationAccounting`, `MemoryInfo`, `GcTriggering`, `MemoryReuse`,
  `HardLimitOom`, `GcEvents`, `GcInternals`).
- **`Program.PendingFeatures`** is the hardcoded source of truth for what is *not implemented yet*.
  Tests gated on a pending feature are reported as **Skipped** unless overridden on the command line:
  - `TestApp.exe --feature X[,Y]` — run *only* the tests of those features, pending or not;
  - `TestApp.exe --all-features` — run everything, including pending;
  - `TestApp.exe <TestName>` — a test requested by name always bypasses the pending gate.
  CI runs with no arguments → only implemented features are exercised. Starting work on a feature =
  removing its enum value from `PendingFeatures` and watching its tests fail. Validating pending
  tests against the stock GC = running with `--all-features` there.
- `TestBase.RequiresCustomGcApi` — skipped on the **stock** GC (the `GcApi.TryCreate() != null`
  probe); `SyncBlockCacheTest` is migrated to this and now reports *Skipped* on stock instead of
  failing. `run-tests.cmd` forwards `--feature`/`--all-features`.

### 1.2 Hang watchdog — ✅ implemented (with a caveat)

A background watchdog thread is armed with each test's name and timeout (default 120 s,
`TestBase.TimeoutSeconds`); on expiry it writes `WATCHDOG: test X exceeded its timeout` to stderr and
fails fast, so CI shows *which* test hung. **Caveat discovered during design**: the watchdog is a
managed thread — a hang *inside the GC with the EE suspended* (e.g. the frozen-primary dependent-handle
livelock) suspends the watchdog too. Those hangs can only be caught by an external, out-of-process
timeout (CI-level, or the subprocess helper below). The in-proc watchdog still covers the other hang
class: deadlocked `WaitForPendingFinalizers`, runaway managed loops.

### 1.3 Subprocess helper

`ChildProcess.Run(arguments, extraEnvironment, timeout)` relaunches the current executable with the
given arguments and environment overrides, inheriting the rest (including `DOTNET_GCName`, so the
child runs on the same GC), and hands back the exit code and output. There is no generic `--child`
mode: each subprocess test defines its own marker argument, dispatched at the very top of
`Program.cs` before any other work so the child scenario fully controls its allocations (it may run
under a tiny heap hard limit). Currently the only one is `--oom-child`
(`HeapHardLimitOomTest.ChildArgument`). A distinctive exit-code protocol (42 = child scenario
succeeded) distinguishes a crash from a clean failure.

---

## 2. Category A — cross-GC behavioral tests (the bulk)

| Test class | Feature gate | Doc item | Status on custom GC today |
|---|---|---|---|
| `CollectibleAssemblyTest` | `CollectibleAssemblies` | 3.1 + 4.1/4.2 | crash (handle type 10 OOR) |
| `DynamicMethodTest` | `CollectibleAssemblies` | 3.1 | likely crash/UAF |
| `ComWrappersTest` | `RefCountedHandles` | 3.2 (+2.1 indirectly) | premature collection |
| `PendingFinalizerRootsTest` | `FinalizationQueueRoots` | 3.3 | short weak cleared early |
| `FrozenDependentHandleTest` | `FrozenDependentHandles` | 3.4 | hang / dropped value |
| `SuppressFinalizeSemanticsTest` | `SuppressFinalizeDrop` | 5.1 | wrong resurrection |
| `GenerationApiTest` | `GenerationApis` (implemented) | 6.1/6.2 | green — `WhichGeneration`/`GetMaxGeneration` landed |
| `LatencyModeTest` | `LatencyMode` | 6.1 | fail-fast |
| `NoGCRegionTest` | `NoGCRegion` | 6.1 | fail-fast |
| `MiscGcApiTest` | `ApiSurface` | 6.1 | fail-fast (`RefreshMemoryLimit`, …) |
| `EventCountersTest` | `EventCounters` | 6.1 (`GetGenerationBudget`) | fail-fast on listener attach |
| `AllocationAccountingTest` | `AllocationAccounting` | 8.1 | negative / zero values |
| `MemoryInfoTest` | `MemoryInfo` | 6.1/10 | all-zero values |
| `GcTriggerTest` | `GcTriggering` | 1.1 | red (no collection happens) |
| `MemoryFootprintTest` | `MemoryReuse` | 1.2 | red (unbounded growth) |
| `PohPinnedAllocTest` | *(none — should pass already)* | 8.x sanity | green (regression guard) |
| `DependentHandleResurrectionTest` | *(none — should pass already)* | 3.3/3.4 adjacent | green (regression guard) |

Details:

### `CollectibleAssemblyTest` — the control feature, fully testable in-proc
`AssemblyBuilder.DefineDynamicAssembly(..., AssemblyBuilderAccess.RunAndCollect)` emits a type with an
instance method returning a constant and a static field (`object` type).
1. **Instance liveness**: create instance, drop the builder/`Type` references, `GC.Collect()` several
   times, then call the method and `GetType().FullName` — on a broken GC the LoaderAllocator (and the
   MethodTable behind it) is gone. This is the direct test of the mark-time LoaderAllocator edge.
2. **Collectible statics**: write/read the emitted static across collections. First static access makes
   the EE create a `HNDTYPE_WEAK_INTERIOR_POINTER` handle → also covers 4.1/4.2 (today: index-out-of-range
   in `GCHandleStore`).
3. **Unloadability**: drop everything, take a `WeakReference` to the emitted `Type`, collect in a retry
   loop (unload is multi-GC on stock) → weak ref must die, proving we don't *leak* collectible
   assemblies either (the long-weak handle on the LoaderAllocator must clear).

### `DynamicMethodTest`
Same feature area, different mechanism: build a `DynamicMethod`, get its delegate, drop the
`DynamicMethod`, collect, invoke the delegate repeatedly. Then drop the delegate and verify
collectability via weak ref + retries.

### `ComWrappersTest`
Minimal `ComWrappers` subclass (empty vtable is fine for lifetime purposes).
1. `GetOrCreateComInterfaceForObject(obj)` → native `IntPtr` holds a ref. Drop all managed refs to
   `obj`, keep a short `WeakReference`. Collect → **must stay alive** (refcounted handle answers "rooted"
   via `RefCountedHandleCallbacks`).
2. `Marshal.Release` the pointer → collect → weak ref must die (refcounted handle behaves long-weak and
   is cleared).
3. Roundtrip sanity: `GetOrCreateObjectForComInstance` returns the same instance while rooted.
The exact rooted-vs-refcount semantics get pinned empirically on stock (see methodology note).

### `PendingFinalizerRootsTest`
1. Block the finalizer thread with a "gate" object whose finalizer waits on a `ManualResetEventSlim`.
2. Make finalizable object `F` unreachable, `GC.Collect()` → `F` is queued behind the gate.
3. Resurrect a reference via a pre-created `WeakReference(F, trackResurrection: true).Target`, wrap it
   in a **new short** `WeakReference`, drop the strong ref, `GC.Collect()` again.
4. Assert the short weak is **alive** (stock: f-reachable queue is a strong root from the top of the
   mark phase). 5. Release the gate, `WaitForPendingFinalizers`, collect → everything dead.
Queue order between gate and `F` isn't guaranteed → verify `F`'s finalizer hasn't run at step 3 and
retry the whole scenario otherwise (bounded retries).

### `FrozenDependentHandleTest`
1. `DependentHandle(primary: "literal string", secondary: new object())` → collect → secondary alive
   (weak-ref probe). Covers the dropped-value defect.
2. `DependentHandle(primary: live normal object, secondary: "literal string")` → `GC.Collect()`
   **returns** (covers the livelock — this is the test the watchdog exists for).
3. `ConditionalWeakTable<string, object>` with a literal key → value survives collections.
Sanity probe at start: record `GC.GetGeneration("literal")` so the test log shows whether the literal
was actually frozen on this runtime (assertions hold either way on stock — interning roots it forever).

### `SuppressFinalizeSemanticsTest`
1. **No spurious resurrection**: finalizable object, `GC.SuppressFinalize`, track:true weak, drop,
   single `GC.Collect()` → long weak must be **dead immediately** (stock drops suppressed objects at the
   finalization scan; yours currently resurrects them for one extra GC).
2. **Suppress + ReRegister while alive**: suppress, `ReRegisterForFinalize`, drop, collect + WFPF →
   finalizer ran exactly once.
3. **Suppress → never finalized**: suppress, drop, collect + WFPF → finalizer never ran (may already be
   covered by `FinalizerTest`; keep here for completeness of the semantic group).

### `GenerationApiTest`
`GC.GetGeneration(new object())` ∈ [0, 2]; `GC.MaxGeneration == 2`; `GC.GetGeneration` on a frozen
literal == whatever stock says (expected `int.MaxValue`, verify at impl time); `GC.CollectionCount(g)`
non-negative, monotonic, increases after `GC.Collect()` for all g ∈ 0..MaxGeneration.

### `LatencyModeTest`
Read `GCSettings.LatencyMode`; set `Interactive`, `SustainedLowLatency`, `Batch` and read each back;
restore. (Today: fail-fast on the getter.)

### `NoGCRegionTest`
- `GC.TryStartNoGCRegion(16MB)`: if `false` → pass (declining is a documented outcome — this keeps the
  test green if you choose the "always decline" implementation). If `true`: snapshot
  `CollectionCount`, allocate ~1 MB, assert no collection happened, `EndNoGCRegion()`, assert a
  subsequent `GC.Collect()` works.
- `GC.TryStartNoGCRegion(ridiculously large)` → expect the documented exception (verify exact type on
  stock; expected `ArgumentOutOfRangeException` from `start_no_gc_too_large`).
- `GC.EndNoGCRegion()` outside a region → `InvalidOperationException`.

### `MiscGcApiTest`
All no-crash + documented-outcome checks: `GC.Collect(gen, mode, blocking, compacting)` across modes
(incl. `Aggressive`); `GC.AddMemoryPressure(100MB)` + `RemoveMemoryPressure`; `GC.RefreshMemoryLimit()`;
`GC.RegisterNoGCRegionCallback(size, cb)` → documented exception outside a region;
`GC.RegisterForFullGCNotification` → either succeeds (then `CancelFullGCNotification`) or throws
`InvalidOperationException` — both accepted, because "decline" is legal for a non-concurrent GC;
`GCSettings.LargeObjectHeapCompactionMode` set/reset.

### `EventCountersTest`
In-proc `EventListener` that enables the `System.Runtime` EventSource with
`EventCounterIntervalSec=1`, waits (≤10 s) for counter payloads, asserts at least one interval arrived
and specifically that the `gen-0-gc-budget` counter was delivered — that counter polls
`GC.GetGenerationBudget(0)`, the exact call that fail-fasts your GC the moment `dotnet-counters`
attaches. This is the "survives monitoring" test.

### `AllocationAccountingTest`
`GC.GetAllocatedBytesForCurrentThread()` before/after allocating ~10 MB of dropped arrays: delta ≥
10 MB **and both samples ≥ 0** (catches the current negative-value bug); `GC.GetTotalAllocatedBytes()`
monotonic and ≥ the per-thread delta; `GC.GetTotalMemory(false) > 0` while holding live data.

### `MemoryInfoTest`
After allocating live data + one collect: `GCMemoryInfo.HeapSizeBytes > 0`,
`TotalCommittedBytes > 0`, `Index` increases across collections; with the finalizer gate from
`PendingFinalizerRootsTest`, `FinalizationPendingCount ≥ 1` (tolerance settled on stock first).

### `GcTriggerTest` — yes, the trigger policy is testable
Snapshot `GC.CollectionCount(0)`; allocate ~256 MB of immediately-dropped 1 MB arrays **without any
explicit `GC.Collect()`**; assert `CollectionCount(0)` increased. Stock's gen0 budget makes this
deterministic; your GC stays red until allocation-triggered collection exists. (If the trigger
deadlocks on the preemptive-mode issue — 8.3 — this test hangs and the watchdog names it.)

### `MemoryFootprintTest` — memory reuse
Churn ~2 GB total (1 MB arrays, dropped immediately) with an **explicit** `GC.Collect()` every 256 MB
(deliberately independent of `GcTriggering`), then assert `Process.GetCurrentProcess().PrivateMemorySize64`
(refreshed) stays under a generous threshold (~600 MB; stock lands far below). Red until sweep actually
feeds a free list / decommits.

### Ungated regression guards (expected green on both today)
- `PohPinnedAllocTest`: `GC.AllocateArray<byte>(n, pinned: true)` + `GC.AllocateUninitializedArray`;
  data pointer stable across collections; contents intact.
- `DependentHandleResurrectionTest`: dependent handle whose primary is a *finalizable* object —
  secondary must stay alive while the primary awaits finalization and die after it's finalized
  (exercises the second dependent-handle pass, which you already have; cheap insurance while touching
  that code for 3.4).

---

## 3. Category B — custom-GC-API-only (skipped on stock, like the sync-block tests)

Several items that have no managed-code trigger on the stock GC *are* testable on the custom GC by
extending `ManagedDotnetGC.Api` (`IGc`). These tests carry `RequiresCustomGcApi = true` + feature
`GcInternals`, and are the sanctioned exception to the "green on both" philosophy:

- **`GcInternalsProbeTest`** — `IGc` methods that route test-provided addresses through the GC's own
  query implementations (doc 6.3), turning them into unit tests of load-bearing machinery:
  - `GetContainingObject(interior)`: take a real object address (`Utils.GetAddress`), probe interior
    pointers into its middle, its first byte, one-past-the-end, a stack address, null — assert the
    resolved object start (validates the brick-table lookup, which becomes correctness-critical the
    moment memory reuse lands and bricks can go stale).
  - `IsHeapPointer`: heap object → true; stack/native/frozen addresses → per stock semantics.
  - `IsPromoted` (outside GC): asserts the safe answer (true — the EE treats false as "dead, safe
    to detach" from the `AfterGcScanRoots` callback). `WhichGeneration` needs no probe:
    `GC.GetGeneration` maps directly to it (implemented — `GenerationApiTest` covers it), and
    `IsEphemeral` has no reachable release-mode caller for this GC (its only VM call site is behind
    `GetCondemnedGeneration() < GetMaxGeneration()`), so its stub deliberately keeps throwing to
    flag any unexpected call.
- **`GcCallbackBracketTest`** — counters for `GcStartWork`/`BeforeGcScanRoots`/`AfterGcScanRoots`/
  `GcDone` recorded by the GC itself, asserted to increment exactly once per induced collection and
  in the right order (doc 2.1). Self-instrumentation — it proves the calls happen, not that the EE
  liked them — but it catches regressions like "sweep refactor dropped the GcDone call". The
  EE-observable side of 2.1 is covered by `ComWrappersTest` (Category A).
- **`WriteBarrierParamsTest`** — expose the `WriteBarrierParameters` passed at `Initialize` (doc 7.1):
  assert non-null card table and bounds covering the reservation once that fix lands.

These require small `IGc` additions (counters + pass-through wrappers), mirroring how
`GetSyncBlockCacheCount` already works.

**Landed.** The `IGc` surface (methods in declaration order after `GetSyncBlockCacheCount` — the
vtable is positional, so the Api and GC projects must ship from the same build):

- `GetContainingObject` / `IsHeapPointer` / `IsPromoted` — pass-throughs to the `IGCHeap`
  implementations, with `NotImplementedException` (and any other exception) mapped to the `GcProbe`
  sentinels instead of a fail-fast, so the tests fail cleanly while the stubs remain.
- `GetGcCallbackCount(GcCallbackKind)` — bracket counters. `GCHeap.ManagedApi.cs` contains
  `NotifyGcStartWork`/`NotifyBeforeGcScanRoots`/`NotifyAfterGcScanRoots`/`NotifyGcDone` wrappers
  (counter + order-violation state machine + the actual `_gcToClr` call): **when implementing 2.1,
  call these wrappers from the collection cycle** — they are the sanctioned call sites, and using
  them is what turns `GcCallbackBracketTest` green.
- `GetWriteBarrierParameter(WriteBarrierParameterKind)` — reads the parameters captured by the
  `StompWriteBarrier` wrapper (all write-barrier updates must go through it).

One probe encodes an edge the machinery originally got wrong: `GetContainingObject(first byte of
the object)` must resolve to that object (stock `find_object`: `obj <= ptr < obj + size`). The
interior path in `ScanRoots` used to walk `WalkHeapObjects(closestBelow, root)` with an *exclusive*
upper bound, so a pointer exactly at an object start resolved to nothing and a `GC_CALL_INTERIOR`
root at an object's first byte didn't mark it. The bound is fixed (`root + 1`); implementing 6.3 by
factoring out that path keeps the probe green.

That marking bug also has a direct end-to-end test, `InteriorPointerObjectStartTest` (no custom API
needed): a byref synthesized at the very first byte of a `byte[]` (via `Unsafe.SubtractByteOffset`
from the array data reference) is the object's only root across a `GC.Collect()`, observed through a
`WeakReference`; a control scenario does the same with a byref to the first data byte. Validated
green on stock (the byref at the method-table word does keep the object alive there); it initially
failed on the custom GC exactly as predicted (control passes, object-start scenario fails) and went
green with the `ScanRoots` bound fix. It sits under `GcFeature.InteriorPointers` — the implemented
feature it belongs to — as the permanent regression guard.

## 4. Category C — subprocess tests

### `HeapHardLimitOOMTest` — feature `HardLimitOom` (doc 1.3)
Parent spawns `TestApp.exe --oom-child` with `DOTNET_GCHeapHardLimit=0x8000000` (128 MB,
env var must precede runtime start — hence subprocess). Child: allocate 1 MB arrays into a list until
`OutOfMemoryException` is **caught**; verify the heap still works afterwards (drop list, collect,
allocate); exit 42. Parent asserts exit code 42. Green on stock. For your GC this implies two small
features: honoring `GCHeapHardLimit` (read via `GetIntConfigValue` — you already have the plumbing) and
returning `null` from `Alloc` instead of throwing. This is the only sane way to test OOM without
grinding the whole machine into the pagefile.

### (Optional, tier 3) `GcEventsTest` — feature `GcEvents`
In-proc `EventListener` on `Microsoft-Windows-DotNETRuntime` with the GC keyword (0x1): after
`GC.Collect()`, expect `GCStart`/`GCEnd` events. Green on stock; red on yours until the `EventSink` is
wired. Only worth it if/when you decide diagnostics are in scope. A gcdump-style test (heap walk via
`Microsoft.Diagnostics.NETCore.Client` against self) is also possible for the `Diag*` no-op work, same
tier.

## 5. Category D — not realistically testable (and why)

| Doc item | Why not |
|---|---|
| 7.1 write-barrier init params (stock side) | Only observable as asserts under a Debug/Checked runtime build. On the custom GC: covered by `WriteBarrierParamsTest` (Category B). |
| 6.3 query methods (stock side) | No managed trigger on a release runtime (profiler/debugger only). On the custom GC: covered by `GcInternalsProbeTest` (Category B); they also become load-bearing via `ComWrappersTest`/2.1. |
| 2.1 bracket calls, EE side | The EE-observable effects are covered by `ComWrappersTest`; the calls themselves by `GcCallbackBracketTest` (Category B). `GcStartWork`'s code-heap cleanup is a native leak with no managed observable on either GC. |
| 4.3 legacy handle types | Requires hosting the GC in .NET 6/7 — out of scope by your constraint. |
| 9.1 conservative/interpreter, 8.2 ALIGN8, 9.2 Android bridge | Out of scope (platform/config). |
| 8.3 preemptive-mode dance | No direct test; failure mode (deadlock) is surfaced by `GcTriggerTest` + an external timeout (in-proc watchdog is blind to EE-suspended hangs). |
| 5.2 `EnableFinalization(false)` nuance | Not observable from managed code. |
| 10 DAC/SOS | Manual/debugger-driven by nature. |

### Empirically validated semantics (from Phase 4)

Blocking the finalizer thread with "gate" finalizers is **only deterministic if you block first**:
queue the gate alone, spin until a flag set at the *entry* of its finalizer confirms the thread is
stuck, and only then queue the payload objects. Bracketing the payload between two gates in a single
batch is not enough — in a full-suite run (with other tests' finalizables interleaved in the queue)
the payload can be dequeued before either gate is reached, which made `PendingFinalizerRootsTest`
flaky until it switched to block-first. `MemoryInfoTest` uses the same pattern.

### Empirically validated semantics (from Phase 1)

`DependentHandleResurrectionTest`, written per this plan, initially asserted that a *short* weak
reference to the dependent secondary survives while the primary awaits finalization — **the stock GC
failed that assertion**. The real semantic: the secondary is promoted as part of the resurrection
wave (second dependent-handle pass, *after* short-weak severing), so short weak references to both
primary **and** secondary are cleared in the GC that queues the primary; only
`trackResurrection: true` references observe either object while the primary sits in the finalizer
queue. The test now encodes that. Keep this in mind when writing `PendingFinalizerRootsTest` — its
scenario (short weak created while the object is *already* f-reachable, surviving the *next* GC) is
unaffected, but assertions about objects promoted during the resurrection wave must use long weaks.

## 6. Suggested order — status

1. ✅ **Phase 0**: runner infra (skip state, feature gate, watchdog, `ChildProcess` helper;
   `SyncBlockCacheTest` migrated to `RequiresCustomGcApi`).
2. ✅ **Phase 1**: ungated guards (`PohPinnedAllocTest`, `DependentHandleResurrectionTest`).
3. ✅ **Phase 2**: `GcTriggerTest`, `MemoryFootprintTest`, `HeapHardLimitOomTest` (`--oom-child`).
4. ✅ **Phase 3**: `GenerationApiTest`, `LatencyModeTest`, `NoGCRegionTest`, `MiscGcApiTest`,
   `EventCountersTest`.
5. ✅ **Phase 4 (Category A)**: `CollectibleAssemblyTest`, `DynamicMethodTest`, `ComWrappersTest`,
   `PendingFinalizerRootsTest`, `FrozenDependentHandleTest`, `SuppressFinalizeSemanticsTest`,
   `AllocationAccountingTest`, `MemoryInfoTest`.
6. ✅ **Category B probes** (`GcInternalsProbeTest`, `GcCallbackBracketTest`,
   `WriteBarrierParamsTest`) — `IGc` extended (probe pass-throughs with `GcProbe` sentinels,
   bracket counters, write-barrier parameter capture), GC republished and validated. On the custom
   GC with `--feature GcInternals` all three fail cleanly today (stub → sentinel → readable failure,
   no fail-fast), which is the designed red state until items 6.3 / 2.1 / 7.1 land.

7. ✅ **`InteriorPointerObjectStartTest`** — end-to-end test for the object-start interior-pointer
   marking bug (see Category B section above). Confirmed the bug red-first, then went green with
   the `ScanRoots` walk-bound fix; stays under the implemented `InteriorPointers` feature.
8. ✅ **First implemented item**: `WhichGeneration` — the single-generation story: heap object → 0
   (= `GetMaxGeneration`), non-heap (frozen) → `int.MaxValue` for stock API parity. The only hard
   EE constraint is consistency of the pair: the EE indexes `GetMaxGeneration + 1`-sized arrays
   with `WhichGeneration` results (dead-thread GC trigger heuristic, threads.cpp). Nothing in the
   VM requires MaxGeneration = 2: the remaining consumers are `CollectionCount(maxGen)` (the GC
   ignores the argument), ETW full-GC tagging, profiler walks, and syncblock scan gates that stay
   on the full-scan path with `0/0`. `GenerationApiTest` is green on the custom GC and moved to
   its own implemented feature `GenerationApis`; `ApiSurface` stays pending for `MiscGcApiTest`
   (`RefreshMemoryLimit`, …).

Validation state: stock GC `--all-features` = 50 passed / 4 skipped (sync-block + the 3 Category B
tests, which need the custom GC API) / 0 failed; custom GC default = 36 passed / 18 skipped (pending
features) / 0 failed, exit 0.

Every test landed green-on-stock; each feature flag flips from "skipped" to "running" on the custom
GC as the corresponding item in `missing-features.md` is implemented.
