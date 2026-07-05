# SPEC-M2 — Region heap core

Status: **specified 2026-07-05**, not yet implemented. This is the implementation spec for
roadmap phase M2 (ROADMAP.md). It is written so that a cold-start session can implement it
without re-deriving any design decision. Where a rule looks arbitrary, the rationale is next
to it — do not "improve" a rule without understanding the invariant it protects.

Exit criteria (from ROADMAP.md): missing-features items **1.1** (allocation triggering),
**1.2** (memory reuse), **1.3** (OOM-as-null) and **8.3** (preemptive-mode dance) implemented
on the new allocator; test gates `GcTriggering`, `MemoryReuse`, `HardLimitOom` flipped on and
the suite fully green; real apps run indefinitely with bounded RSS.

Non-goals (explicitly out of M2): card table / generations (M4), parallel mark (M5),
concurrent mark (M6), any form of compaction (never — non-moving is a design pillar).
M1 items (docs/missing-features.md) are independent and can land before, after, or
interleaved — nothing here conflicts with them.

---

## 1. Constants

```csharp
internal static class Region
{
    public const int Shift = 21;
    public const nint Size = 1 << Shift;                  // 2 MiB
    public const long HeapReserveSize = 2L << 40;         // 2 TiB (unchanged)
    public const int Count = (int)(HeapReserveSize >> Shift);  // 1,048,576

    public const nint BumpMaxSize = 32 * 1024;            // ≤ 32 KB objects → bump tier
    public const nint WindowSize = 128 * 1024;            // default alloc-context window
    public const nint MinLinkedHole = 4 * 1024;           // smaller holes: plugged, not carved
    public const nint GuardBytes = 16;                    // reserved tail of every bump region

    public const long MinGCBudget = 64L * 1024 * 1024;    // 64 MB floor for the trigger budget
}
```

- `SizeClassMaxSize` = 1 MiB (largest size class, §4.2). Objects with `size + 8 > 1 MiB`
  go to the span tier.
- Object alignment stays 8 (`GCHeap.Align`). Minimum object size stays 24 (`SizeOfObject`).
- The old `GCHeap.SegmentSize` / `AllocationContextSize` constants and the whole
  `Segment`/`SegmentManager`/brick-table machinery are **deleted**.

## 2. Address space and region table

- Reserve `HeapReserveSize + Region.Size` and set `_heapBase = AlignUp(reservation, Region.Size)`.
  (VirtualAlloc only guarantees 64 KB alignment; regions must be 2 MB-aligned so that
  `RegionIndex(addr) = (addr - _heapBase) >> 21` is exact.)
- `NativeAllocator` keeps the single reservation. Its `IsInRange` stays the heap-pointer test.
  Add non-throwing variants (§9): `bool TryCommit(nint addr, nint size)`,
  `bool TryCarveFrontier(nint size, out nint addr)`. The throwing `Allocate`/`Commit` must have
  **no callers reachable from `Alloc`** when M2 is done.
- The **region table** is a flat array of 32-byte entries, one per region:
  `Count × 32 B = 32 MB` reserved up front (outside the heap reservation is fine),
  committed on demand as the frontier grows (same pattern as the old
  `EnsureSegmentTableCommitted`).

```csharp
internal enum RegionKind : byte { Free = 0, Bump, SizeClass, SpanStart, SpanExtension }

[StructLayout(LayoutKind.Explicit, Size = 32)]
internal struct RegionEntry
{
    [FieldOffset(0)]  public RegionKind Kind;
    [FieldOffset(1)]  public byte SizeClass;        // SizeClass only: index into class table
    [FieldOffset(2)]  public byte IsCommitted;      // Free only: 0 after decommit
    [FieldOffset(4)]  public int LiveBytes;         // rebuilt by every mark phase, consumed by sweep

    // 8..15 — kind-specific primary word:
    [FieldOffset(8)]  public nint Cursor;           // Bump: allocation high-water mark (absolute)
    [FieldOffset(8)]  public ulong AllocatedBlocks; // SizeClass: bit i set = block i allocated
    [FieldOffset(8)]  public int SpanCount;         // SpanStart: regions in the span (incl. self)
    [FieldOffset(8)]  public int SpanStartIndex;    // SpanExtension: region index of the SpanStart

    // 16..23 — kind-specific secondary word:
    [FieldOffset(16)] public nint FirstHole;        // Bump: head of hole list (plug object ref, 0 = none)
    [FieldOffset(16)] public int NextInClassList;   // SizeClass: next region index in class alloc list (-1 = end)

    [FieldOffset(24)] public int HoleBytes;         // Bump: total carveable extent bytes (policy/stats)
    [FieldOffset(28)] public int NextRecycled;      // Bump: next region index in recycled list (-1 = end)
}
```

Address→region resolution (replaces `FindSegmentContaining`, O(1), no probing):

```
index = (addr - _heapBase) >> Region.Shift
valid heap pointer ⇔ 0 ≤ index < carvedCount && table[index].Kind != Free
SpanExtension resolves to table[SpanStartIndex] for the containing object.
```

Region lifecycle: `Free (pool) → Bump | SizeClass | SpanStart/Extension → (sweep) → Free`.
The free pool is a LIFO stack of region indices (`int[]` + count, under the alloc lock).
The *frontier* is the never-yet-carved suffix of the reservation; carving from it commits
the region's pages and the covering region-table pages.

**Invariant P (pool zeroing):** every region in the free pool is either fully zero and
committed, or decommitted (`IsCommitted == 0`). Enforced at recycle time (§7.4). A region
popped from the pool is therefore always ready to serve zeroed memory after at most a
`TryCommit`.

## 3. Layout arithmetic (normative)

These formulas reproduce the conventions already proven in `GCHeap.Alloc` /
`FixAllocContext` / `Sweep`. Every object ref `R` has its 8-byte pre-header word at `R-8`
(upper 4 bytes = sync block index, lower 4 bytes = the GC word, §5). Sizes are as returned
by `ComputeSize()`, walks advance by `Align(size)`.

**Free-object plug covering extent `[X, E)`** (both 8-aligned, `E - X ≥ 24`):

```
ref = X + 8;  RawMethodTable = _freeObjectMethodTable;  Length = (uint)(E - X - 24)
⇒ walk-next from ref = Align(ref + 24 + Length) = E + 8   // lands on the next object ref
```

**Bump window over extent `[W, W+len)`** (serving a request of size `S`,
`len ≥ Align(S) + 24`):

```
returned object ref R = W + 8
acontext.alloc_ptr    = Align(R + S)
acontext.alloc_limit  = W + len - 16
```

The `-16` is not slack tuning: a plug written at any `P ∈ [alloc_ptr, alloc_limit]` with
`Length = alloc_limit - P` (this is exactly what `FixAllocContext` writes) has walk-next
`= Align(P + 24 + Length) = alloc_limit + 24 = W + len + 8`, i.e. precisely the first
object ref of the next window. Changing the 16, the plug formula, or `FixAllocContext` independently breaks heap
walkability. `FixAllocContext` itself is unchanged from today.

**Bump region geometry:** first window starts at `W = base` (so the first object ref is
`base + 8`); `Cursor` = end of the last handed-out extent; windows must satisfy
`W + len ≤ base + Region.Size - GuardBytes`. The 16-byte guard tail guarantees plug
nominal extents (`limit + 24 ≤ end + 8`) stay inside the region. Walk range of a bump
region = `[base + 8, Cursor)`; bytes in `[Cursor, DataEnd)` are virgin zero.

**Size-class block `i` of class size `C`:** object ref `= base + i*C + 8`; fits `S` iff
`S + 8 ≤ C`. Free blocks are *not* plugged — the allocation bitmap is the walkability
source for these regions (walk = iterate set bits). Block memory is fully zero while free
(Invariant P extended: enforced at sweep, §7.2).

**Span of `n` regions for size `S`:** `n = ceil((S + 8) / Region.Size)`; object ref
`= spanBase + 8`. `S + 8 ≤ n * Region.Size` holds by construction; no guard or plug needed
(the span contains exactly one object; walk = that object). This retires appendix-B bug #1
of missing-features.md (the old dedicated-segment off-by-8).

## 4. Allocation tiers

`GCHeap.Alloc(ref acontext, size, flags)` dispatches on `size`:

| Condition | Tier | Mechanism |
|---|---|---|
| `size ≤ 32 KB` | bump | refill alloc context from a window (§4.1) |
| `size + 8 ≤ 1 MB` | size class | direct allocation from segregated free bitmap (§4.2); **acontext untouched** |
| otherwise | span | direct allocation of `n` contiguous regions (§4.3); **acontext untouched** |

Direct-tier allocations leave the caller's alloc context alone (it may still have room for
small objects; the stock GC's UOH path behaves the same way). The old
"zero the context for huge objects" behavior is dropped.

`GC_ALLOC_FINALIZE`: register after a successful allocation, as today — but if
`RegisterForFinalization` fails to grow the queue, **return null** (treat as OOM, §9).
`GC_ALLOC_ZEROING_OPTIONAL` is ignored; we always hand out zeroed memory.

### 4.1 Bump tier

Window refill order (all under the alloc lock, §8.1):

1. **Holes first**: walk `_recycledRegions` (intrusive list via `NextRecycled`); carve from
   the first hole with extent ≥ `Align(S) + 24` (§4.4). Hole-first is a deliberate M2
   policy: it exercises reuse constantly (the `MemoryReuse` gate is the point of M2);
   locality tuning is M3's job.
2. **Active fresh region** bump: extent `[Cursor, Cursor + len)`,
   `len = min(max(WindowSize, Align(S) + 24), DataEnd - Cursor)`, where
   `DataEnd = base + Region.Size - GuardBytes`. If remaining `< Align(S) + 24`, the region
   is abandoned as-is (its virgin tail is zero and unreachable by walks — no plug needed;
   it is reclaimed when the region eventually dies or is swept-recycled).
3. **Free pool** pop → becomes the active fresh region (commit if decommitted).
4. **Frontier** carve → becomes the active fresh region.
5. Fail → caller runs the collect-and-retry protocol (§9.1).

On every successful window handout: `_allocatedSinceGC += len` and
`acontext.alloc_bytes += len` (the EE computes per-thread numbers as
`alloc_bytes - (alloc_limit - alloc_ptr)`, so full-extent-at-handout is exact;
`FixAllocContext` additionally does `alloc_bytes -= (alloc_limit - alloc_ptr)` before
invalidating, keeping totals equal to consumed bytes — that closes item 8.1's biggest hole
for free).

### 4.2 Size-class tier

20 classes, 4 linear steps per power of two (max internal fragmentation ≈ 20%):

```
KB: 40 48 56 64 | 80 96 112 128 | 160 192 224 256 | 320 384 448 512 | 640 768 896 1024
blocks/region (floor(2048/C)):
    51 42 36 32 | 25 21 18 16   | 12  10  9   8   | 6   5   4   4   | 3   2   2   2
```

Class selection: smallest `C` with `C ≥ size + 8`. Note coverage is contiguous with the
bump tier: `32 KB < size + 8 ≤ 40 KB` lands in class 0. The tail waste of non-dividing
classes (worst: 768 KB class wastes 512 KB/region) is accepted for M2 and listed as an
M3/M7 tuning knob — do not redesign it now.

Per class: an intrusive list (via `NextInClassList`) of regions with at least one free
block, plus nothing else — no per-class locks in M2 (the alloc lock covers all of it;
mimalloc-style sharding is the M5-era plan).

Allocate: head region of the class list → `i = Tzcnt(~AllocatedBlocks & fullMask)` where
`fullMask = (blockCount == 64 ? ~0ul : (1ul << blockCount) - 1)` → set bit; if the region
becomes full, unlink it. No list head → take a region (pool/frontier), stamp
`Kind = SizeClass, SizeClass = c`, link it. Memory is already zero (Invariant P), so
allocation writes nothing but the region-table bit. `_allocatedSinceGC += C`;
`acontext.alloc_bytes_uoh += C`.

### 4.3 Span tier

Need `n` **contiguous** free regions. M2 policy: first, scan the free pool for a contiguous
run (the pool is small; O(pool²) worst case is acceptable — sort-on-demand if it ever shows
up in profiles, M3); else carve `n` regions from the frontier (always contiguous). Stamp
region 0 `SpanStart { SpanCount = n }`, regions 1..n-1 `SpanExtension { SpanStartIndex }`.
`_allocatedSinceGC += n * Region.Size`; `acontext.alloc_bytes_uoh += n * Region.Size`.

### 4.4 Hole carving (the reuse mechanism, item 1.2)

A **hole** is a free-object plug in a swept bump region, linked into the region's hole list:
`FirstHole` → plug ref; each linked plug stores the next plug's ref as an `nint` at
`ref + 16` (inside the plug's dead body; plugs with extent < 32 can't hold a link and are
never linked). List is built by sweep (§7.1), consumed here, singly linked, LIFO.

To carve a window of `len` from a hole plug at ref `R` covering extent `[X = R - 8, E)`
(recover `E = X + 24 + Length = R + 16 + Length` from the plug):

1. Unlink the hole from the list (read next-link at `R + 16` first).
2. If `E - X - len < 48`: take the whole hole, `len = E - X`. (48 = min plug 24 + link room
   + margin; a sliver too small to re-plug-and-link must not be left behind.)
3. Zero `[X, X + 32)` — this erases the plug header and the link word; the rest of the hole
   body is already zero (sweep zeroed it, §7.1; Invariant P analog for holes).
4. Hand out window `[X, X + len)` per §3.
5. If a remainder `[X + len, E)` exists: zero-write is not needed (still zero except
   nothing — the plug for it is fresh); write plug over `[X + len, E)` and push it on the
   hole list (it is ≥ 48 ≥ MinLinkedHole… link it regardless of MinLinkedHole, it already
   qualified as carveable).
6. Update `HoleBytes`; if the region has no linked holes left, unlink it from
   `_recycledRegions`.

**Staleness note:** a window plugged back by `FixAllocContext` at GC time becomes a dead
extent like any other; it is *not* re-linked until the next sweep rebuilds the region's
hole list. That is correct and accepted (holes are only ever consumed between two sweeps
of their region).

## 5. Epoch marking (replaces the MethodTable mark bit)

The 4 bytes at `ref - 8` (the x64 padding half of the pre-header word; the sync block index
occupies `ref - 4`) become the **GC word**, holding an epoch stamp:

```csharp
// on GCObject:
public uint Epoch
{
    get => *(uint*)((byte*)Unsafe.AsPointer(ref this) - 8);
    set => *(uint*)((byte*)Unsafe.AsPointer(ref this) - 8) = value;
}
```

- Global `_currentEpoch` (uint), starts at 1, incremented at the start of every collection.
- **Marked in this GC** ⇔ `obj->Epoch == _currentEpoch`. `Mark()` = stamp. There is **no
  unmark pass** — that is the point (sweep stops writing to live objects entirely).
- `IsMarked/Mark/Unmark` on `GCObject` and every caller (weak scans, `ClearHandles`,
  dependent-handle fixpoint, future `IsPromoted`) switch to the epoch test. The MT low-bit
  scheme is deleted. All those tests only run during STW, so 4-byte non-atomic accesses
  are fine.
- Allocation-time zeroing rules already guarantee the GC word is 0 on fresh objects
  (the EE requires the *whole* pre-header word zero at allocation; stamps are only written
  to live objects during GC, and dead objects' memory is re-zeroed before reuse — so the
  contract holds).
- Wrap guard: when `_currentEpoch` would overflow to 0, walk every object and zero its GC
  word, then set `_currentEpoch = 1`. (One extra full pass per 2³² collections.)
- Frozen-segment objects are never stamped (they fail `IsInRange` before any mark), same
  as today. Their GC word belongs to the EE — never touch it.
- **M4 forward hook (do not build now):** the stamp doubles as an age — "marked at epoch E"
  survives as "old, implicitly live" for young collections; region entries will grow
  youngest-epoch tracking. This is why the scheme is per-object state and not a side bitmap.

## 6. Mark phase changes

- `FindSegmentContaining` → region-table lookup (§2). `Segment.MarkObject` and the brick
  table die.
- When an object is marked, also `table[index].LiveBytes += (int)Align(size)` on its
  containing region (spans: on the SpanStart entry; saturate at `int.MaxValue` — for spans
  the value is only a zero/nonzero liveness flag).
- `LiveBytes` is reset by sweep after consumption (§7), so mark phases always start from 0.
- **Interior pointers** (`GC_CALL_INTERIOR`): resolve via region kind —
  `SizeClass`: block index arithmetic + allocated-bit check → O(1).
  `SpanStart/Extension`: the span's single object, containment check → O(1).
  `Bump`: linear walk from `base + 8` bounded by `Cursor` (regions are 2 MB, so worst case
  is short; the old walk-from-segment-start had the same shape). No brick table: sweep-built
  bricks go stale the moment a hole is carved (the plug header a brick points at gets
  overwritten by a new object's pre-header) — that is missing-features 1.2's
  "brick-table staleness" trap; we eliminate it by not having bricks. If conservative-mode
  benchmarks (9.1) ever hurt, the M3 fix is a side object-start bitmap maintained at
  fix/sweep time, not bricks.
  A resolved "object" whose MT is the free-object MT is **not** promoted (conservative-mode
  hardening, missing-features 9.1).
- **Mark stack**: replace `Stack<IntPtr>` + per-object `Action<IntPtr>` delegate (which
  allocates on our own NativeAOT heap mid-collection) with a native stack: reserve 256 MB
  of `nint` slots up front, commit on demand, `ref struct` push/pop. Commit failure while
  marking is unrecoverable by design → `Environment.FailFast` with a clear message.
  `EnumerateObjectReferences` gains an overload taking the mark stack (or a
  `delegate*<nint, void>` + context) instead of `Action<IntPtr>`.

## 7. Sweep

Iterate carved region-table entries once. Per kind:

### 7.1 Bump regions

- `LiveBytes == 0` → **wholesale recycle** (§7.4). No walk, no per-object work — this is
  the gen0-churn answer from the roadmap and must stay O(1).
- Else walk `[base + 8, Cursor)`. Maintain a pending-dead-extent accumulator; for each
  object: live (epoch == current) → close any pending extent; dead (including existing
  free plugs — they coalesce for free) → extend it. Closing an extent `[X, E)`:
  zero `[X, E)`, write the plug (§3), and if `E - X ≥ MinLinkedHole` link it into
  `FirstHole`/`HoleBytes`. After the walk: if any linked hole exists, put the region on
  `_recycledRegions` (if not already); reset `LiveBytes = 0`.
- Note the virgin tail `[Cursor, DataEnd)` is untouched — it is *not* a hole (it's not
  walkable space); only the active region's tail is ever bump-carved, and only via `Cursor`.

### 7.2 Size-class regions

For each set bit `i`: read the block object's epoch; dead → zero `[blockStart, blockEnd)`
(re-establishes Invariant P for the block) and clear the bit. Afterwards:
bitmap == 0 → recycle (§7.4; the region leaves its class list — sweep must unlink it);
bitmap gained free bits and the region was full → relink into its class list.
Reset `LiveBytes = 0`.

### 7.3 Spans

`LiveBytes == 0` → recycle all `n` regions (§7.4). Alive → reset `LiveBytes = 0`. Nothing
else to do (single object, non-moving).

### 7.4 Recycle + decommit policy

Recycling a region: reset entry to `Free`, push on the pool, and restore Invariant P by
either `memset(base, 0, Region.Size)` **or** `MEM_DECOMMIT` (then `IsCommitted = 0`).
Rule: decommit when the region is part of a span ≥ 4 regions or when the pool's committed
bytes already exceed the retention target; else memset (cheaper than a future page fault
storm for the common churn case).

After sweeping everything: compute `liveTotal = Σ LiveBytes` (grab it before resets — in
practice: accumulate during the sweep walk), then shrink the pool's *committed* slack to
`max(64 MB, liveTotal / 4)` by decommitting pool regions (LIFO tail first). Update
`_lastLiveBytes = liveTotal` for the budget (§8.3) and stats
(`GetTotalBytesInUse`-family wiring is M1's `MemoryInfo` item; the allocator just exposes
`LiveBytes`, `CommittedBytes`, `FrontierBytes`).

Sweep runs entirely under STW with the world stopped; it takes **no locks** (§8.2 explains
why that is safe even though a mutator may technically hold the alloc lock while parked).

## 8. Locking, triggering, and the preemptive dance (items 1.1 + 8.3)

### 8.1 `GcAwareLock`

One small primitive used for both locks. Semantics:

```
Acquire():
    spin-try briefly (≈ 30 YieldProcessor iterations) in the current mode
    if failed:
        wasCoop = gcToClr.EnablePreemptiveGC()      // false if already preemptive / non-EE thread
        blocking acquire (CAS + SwitchToThread/Sleep(0) loop)
        if wasCoop: gcToClr.DisablePreemptiveGC()   // may PARK here until a running GC finishes
Release(): store 0
```

**Why the mode juggling:** a cooperative-mode thread that blocks indefinitely inside the GC
DLL never reaches a safe point, so `SuspendEE` would deadlock on it. Therefore: *waiting*
always happens in preemptive mode. And because `DisablePreemptiveGC` happens **after**
acquiring, a thread that wins the lock while a GC is in flight parks *before touching any
allocator state* — giving the collector exclusive access without ever taking the alloc
lock itself (§8.2).

Critical-section rules (invariant set L):
- **L1**: alloc-lock sections run in cooperative mode (or on non-EE threads), do bounded
  work (bit ops, memset ≤ 2 MB, one VirtualAlloc), and never block on anything.
- **L2**: no thread ever calls `SuspendEE` or acquires the GC lock while holding the alloc
  lock. The trigger path *releases* the alloc lock first.
- **L3**: after any acquire, treat all previously-read allocator state as stale
  (a GC may have run while parked in `DisablePreemptiveGC`).

Consequence: while the EE is fully suspended, every mutator is (a) suspended in managed
code, (b) parked in `DisablePreemptiveGC` before its critical section (L-holder or not), or
(c) briefly finishing a bounded coop section after which it suspends — so sweep may mutate
allocator structures lock-free (the alloc-lock *owner*, if any, is case (b) and cannot be
inside the section).

Two instances: `_allocLock` (hole lists, pool, frontier, class lists, region table,
`_allocatedSinceGC`) and `_gcLock` (whole collections).

### 8.2 Collection entry point

All collections funnel through one helper; `GarbageCollect(gen, lowMem, mode)` (the
`GC.Collect` path, already preemptive on arrival) and the allocation trigger both call it:

```
bool Collect(uint gcCountSnapshot):
    wasCoop = EnablePreemptiveGC()            // no-op false when already preemptive
    _gcLock.Acquire()
    if (_gcCount == gcCountSnapshot)          // else: someone collected while we waited — done
        _currentEpoch++ (with wrap guard §5)
        SuspendEE(SUSPEND_FOR_GC)
        FixAllocContexts()
        MarkPhase()
        SweepPhase()
        _allocatedSinceGC = 0
        _budget = max(MinGCBudget, _lastLiveBytes)
        _gcCount++
        RestartEE(true)
        EnableFinalization(pending > 0)
    _gcLock.Release()
    if (wasCoop) DisablePreemptiveGC()
    return true
```

The snapshot dance (capture `_gcCount` before deciding to collect, re-check under the lock)
is what stops N racing allocators from running N back-to-back collections.

### 8.3 Trigger policy (item 1.1)

Budget: `_budget = max(64 MB, liveBytesOfLastGC)` — heap converges to ≈ 2× live, the
classic ratio. `_allocatedSinceGC` counts handed-out extents (windows, blocks, spans);
over-approximation by unused window tails is bounded by threads × 128 KB and irrelevant.

In the slow path, *before* satisfying a request:
`if (_allocatedSinceGC > _budget)` → release alloc lock, `Collect(snapshot)`, retry
(at most one budget-triggered collection per `Alloc` call — the snapshot makes redundant
triggers cheap no-ops). This is precisely the missing-features 8.3 scenario: the thread is
in cooperative mode here, and `Collect`'s `EnablePreemptiveGC` is what makes `SuspendEE`
legal. Do not add a memory-load-based trigger in M2 (listed for M3/M7).

## 9. OOM handling (item 1.3)

Blanket rule: **no exception may escape any `UnmanagedCallersOnly` frame**. Under NativeAOT
that is a fail-fast, which is exactly what item 1.3 forbids. Audit list of throw sites to
eliminate on the `Alloc` path: `NativeAllocator.Allocate/Commit` (→ `Try*` variants),
region-table commit growth, finalization-queue growth (`RegisterForFinalization` returns
`false`; the EE turns that into a managed OOM), and any `List<T>`/LINQ growth in the
allocator (there should be none left — the pool array is preallocated for `Count` entries:
4 MB, committed lazily… simpler: allocate it eagerly; 4 MB once is nothing).

### 9.1 Collect-and-retry protocol in `Alloc`

```
snapshot = _gcCount
attempt = 0
loop:
    under _allocLock: budget check (§8.3), then try to satisfy (tier §4)
    success → return obj
    // genuine failure: frontier exhausted or commit failed
    if attempt == 1: return null                  // EE throws managed OutOfMemoryException
    Collect(snapshot); snapshot = _gcCount; attempt = 1
```

One forced full collection between first failure and surrender. `null` is the contract
(`gcinterface.h`: the EE throws; we never do). The `GC_ALLOC_FINALIZE` registration failing
after a successful carve also returns `null` (the carved object stays a dead plug for the
next sweep — write a free plug over it before returning null so the heap stays walkable).

## 10. Code plan

New files: `Region.cs` (constants, `RegionKind`, `RegionEntry`, index math),
`RegionAllocator.cs` (table, pool, frontier, windows, holes, classes, spans — everything in
§2–§4, exposing `TryCarveWindow`, `TryAllocBlock`, `TryAllocSpan`, `SweepAll`),
`GcAwareLock.cs`, `MarkStack.cs`.

Modified: `GCHeap.cs` (`Alloc` dispatch + retry protocol, `Initialize` carves the first
region + eagerly commits the pool array, `GarbageCollect` → `Collect`), `GCHeap.Mark.cs`
(region lookup, epoch stamping, live accounting, native mark stack, interior-pointer
resolution per kind), `GCHeap.Sweep.cs` (rewritten per §7), `GCObject.cs` (epoch accessors,
delegate-free reference enumeration), `NativeAllocator.cs` (`Try*`, 2 MB alignment),
`GCHeap.Stats.cs` (expose live/committed).

Deleted: `Segment.cs`, `SegmentManager.cs`, the brick table, `_activeSegment`,
`_allocationLock` (object), `SegmentSize`/`AllocationContextSize`/`HeapReserveSize` consts
(moved to `Region`).

Unchanged and load-bearing: `FixAllocContext` (§3 depends on its exact plug),
`WalkHeapObjects` callers get a region-aware enumerator (bump: cursor walk; size-class:
bitmap; span: one object), frozen-segment handling, handle scanning, finalization.

## 11. Implementation order (each step keeps the suite green)

1. `GcAwareLock` + `Collect` funnel + snapshot dance (8.3 core, no behavior change yet).
2. Region table + frontier + bump windows replacing `SegmentManager` (no reuse yet;
   size > 32 KB temporarily via spans for everything — delete brick table here, interior
   pointers via walk-from-base).
3. Epoch marking + native mark stack (delete unmark pass).
4. Size-class tier (fixes the dedicated-segment off-by-8 by removal).
5. Sweep rewrite: live accounting, wholesale recycle, hole building, bitmap sweep,
   Invariant P, decommit policy.
6. Hole carving in the window path → flip `MemoryReuse` gate.
7. Budget trigger → flip `GcTriggering` gate.
8. `Try*` everywhere + retry protocol → flip `HardLimitOom` gate.
9. Soak: TestApp soak mode + a real app (hours, RSS bounded).

Steps 1, 4, 8 and the unit tests below are well-specced mechanical work, delegable to a
separate session per the working-mode memory; steps 2, 3, 5, 6 are the kernel and belong
in one head.

## 12. Tests

Unit (ManagedDotnetGC.Tests, pure logic, no EE): size-class selection table (boundaries:
32 KB±8, 40 KB, 1 MB−8, 1 MB−7), span count math, window/plug arithmetic invariants of §3
(property test: random carve sequences keep a synthetic region walkable end-to-end), hole
coalescing + relink on a synthetic region, bitmap alloc/free/full-unlink, budget formula,
epoch wrap guard, pool zero invariant after recycle.

Gates flipped in `TestApp/Program.cs` `pendingFeatures`: `GcTriggering`, `MemoryReuse`,
`HardLimitOom`. Suite target: everything green except gates owned by M1 items still open
at that point.

Soak (exit criterion "runs real apps indefinitely"): TestApp `--soak` mode — mixed-size
allocation churn (weighted: 90% ≤ 1 KB, 9% 1–32 KB, 0.9% 32 KB–1 MB, 0.1% spans) with a
retained working set, N minutes, assert RSS plateau and no failures; plus one real
application (ASP.NET sample under load) surviving hours.

## 13. Deferred decisions (recorded so nobody re-litigates them silently)

- Hole-first window policy (§4.1) — correctness-first; revisit for locality in M3 with data.
- Size-class table waste (§4.2), pool LIFO vs address-ordered, span first-fit (§4.3),
  decommit thresholds (§7.4) — M3/M7 tuning knobs, all behind one constants block.
- Interior-pointer acceleration for conservative mode — only if M3 measures it (§6).
- Card table, ephemeral write-barrier bounds, epoch-as-age — M4. Write-barrier init fix
  (missing-features 7.1) stays an M1 item and is unaffected by this spec.
