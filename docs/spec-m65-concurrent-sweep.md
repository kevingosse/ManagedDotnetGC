# SPEC-M6.5: Concurrent full-cycle sweep

2026-07-06. The sweep is the last major slice inside pause B (spec-m6 §5.4 step 4):
after the drain-termination quantum fix, it is 4–15 ms of a 9–22 ms pause B at
GCPerfSim density and ~3 ms of ~5.5 ms on the ASP.NET soak. Marks are final at
pause B, so the sweep computes nothing the pause needs — it only rebuilds the
allocation supply. This spec moves it off-pause for full cycles, behind
`DOTNET_GCConcurrentSweep`. Young sweeps stay in their (young) pauses.

## 1. The split

**Inside pause B**, replacing the STW sweep on gated-in cycles:

1. **Accounting from marks**: `_lastLiveBytes` = Σ region `LiveBytes` — the mark
   phase's interlocked adds produce exactly the number the sweep would have summed,
   so budgets and the sticky accounting no longer wait for the walk.
2. `ClearCards` (unchanged contract).
3. **Region plan**: the pause-B in-use snapshot (same shape as the card pre-drain's
   pause-A plan — skip / bump+size-class / span-of-k). The concurrent walk trusts
   only this: entries of regions carved *while the world runs* can be
   mid-publication when a worker reads them, and a pool pop during the sweep turns a
   Free entry into a live allocation target the walk must never touch.
4. **Supply reset** (`BeginConcurrentSweep`): hole lists, class lists and the active
   bump region are dropped under STW, so nothing the walk will rewrite is reachable
   to carves. Post-restart allocation runs on the pool, the frontier, and whatever
   the walk has already published.
5. **Wholesale pre-pass** (`RecycleWholesaleDead`): every planned region whose marks
   left it empty is recycled O(1) right here — on churn workloads that is the entire
   dead nursery, and it is the pool the gate (§2) depends on. Deferring this to the
   walk measured +1.1 GB of fresh commits on soh: mutators outrun the walk's
   publications.

**After `RestartEE`** (trigger thread + worker pool, still under `_gcLock`, zeroer
gate still held): the planned walk, chunk-dispensed exactly like the STW parallel
sweep, except each worker publishes its chunk's results — recycled/class list
splices and pool pushes — under the allocation lock, so carves see complete
per-chunk supply. Then the gate exits, `_fullCycleInFlight` drops, and the usual
post-pause tail runs (trim, starvation evaluation, stats, zeroer kick).

Why the walk is safe with the world running:

- Planned regions are unreachable to allocation until published (supply reset), so
  the walk's plug rewrites, card-offset rebuilds and flag updates race nothing.
- Planned regions' cursors cannot move: only the active bump region's cursor
  advances, and it was abandoned (its virgin tail — ≤ 2 MB — floats until the
  region recycles).
- Pool pops during the sweep produce only regions the plan skips; regions the
  sweeper frees are published under the same lock that carves hold to pop them.
- Dead spans decommit mid-walk exactly as they did under STW: they were dead at
  pause B, so no reference into them exists.
- The zeroer is gated out for the whole sweep (it memsets hole bodies the walk is
  rewriting); its stand-down poll already covers long collector holds.
- Young collections cannot run (the cycle holds `_gcLock`); budget-triggered
  collects keep returning immediately (`_fullCycleInFlight`, spec-m6 §6.3).

## 2. The window gate — why stage 1 was not unconditional (superseded by §2b)

The sweep window suspends young collections while allocate-through lets allocation
flow, so the cycle overshoots by allocation-rate × walk-time. On heaps where the
wholesale pre-pass restocks the pool (server-shaped: whole regions die together),
the pool floats the window and the overshoot is noise. On smear-heavy heaps
(GCPerfSim soh: every region keeps survivors, nothing wholesale-recycles, pool ≈ 0)
the overshoot is served by *fresh commits*, and it **ratchets permanently**: regions
that gain survivors never pool, never decommit. Measured ungated: committed
equilibrium 2.1 → 3.2 GB (+52%) for pause B 11–24 → 4–12 ms. The M7 exchange-rate
work exists precisely to hold that equilibrium, so:

> **Gate**: a full cycle sweeps concurrently only when
> `PooledCommittedBytes ≥ max(MinGCBudget, budget/4)` at pause B (pool only —
> linked holes are about to be invalidated). Otherwise the unchanged in-pause sweep
> runs (`SweepAndAccount`), byte-for-byte the legacy path.

soh under the gate reproduces knob-off behavior exactly (committed 2.2 GB, walls
equal); the ASP.NET soak passes the gate on every full.

The unconditional version stays interesting for a future stage: the principled fix
is mutator sweep-assist (allocation slow path helps drain the sweep plan instead of
fresh-committing) or young collections running against a partially-swept heap.
Both are real re-architectures; the gate ships value without them.

## 2b. Stage 2 (2026-07-06, same day): mutator sweep-assist — the gate is gone

The assist landed hours after stage 1
(results/2026-07-06-m65s2-bitmap-walks-and-assist.md). `BeginConcurrentSweep(plan,
planCount)` now *arms* the plan under STW — fields on the RegionAllocator, cursor
shared between the worker pool and allocating threads — because the first
post-restart carve can run dry before the workers publish anything. Any carve slow
path that would otherwise fresh-commit (bump window, class block, span single or
run) first calls `TrySweepAssist`: claim a 2-region chunk through the shared cursor,
sweep it inline (the caller already holds the publication lock), retry supply.
Overshoot drains the plan instead of committing, which is exactly the ratchet the §2
gate existed to contain — soh committed equilibrium measured *equal* to knob-off
(2.12 vs 2.24 GB) with the gate deleted.

Assist chunk size is a real knob: at 8 regions the mutators competed with the
workers for the plan (+273 ms cumulative alloc-path time on soh); at 2 they only
bridge to the workers' publications (+126 ms) and serve dozens of carves per chunk.

Safety recap: a claim runs entirely inside one allocation call under the alloc lock
in cooperative mode, so a starting suspension waits it out — the next cycle's plan
cannot be rebuilt while any claim is in flight, and the zeroer's lock-free escape
(`CollectorWaitingForGate`) only fires once the world is fully suspended, which
implies no assist is mid-chunk. `SweepConcurrentFull` disarms on completion; claims
raced past the flag find the cursor exhausted and fall through to the frontier.

**Default flipped on** with stage 2 (`DOTNET_GCConcurrentSweep=0` now *forces the
in-pause sweep*): soak evidence 51.1 k req/s (+8.5% over knob-off), all pauses < 7 ms,
WS 1.86 vs 1.49 GB knob-off — the exchange-rate call §3 deferred, resolved in favor
of throughput with the knob as the opt-out.

## 3. Measured (2026-07-06, results/2026-07-06-m65-concurrent-sweep.md)

ASP.NET soak (knob on): throughput record 48.8 k req/s (was 47.1), full pause B
p50 3.1 ms (was 5.5; concurrent-swept cycles ~2–3 ms), young max 26 → 10.6 ms,
at +0.5 GB WS — sweep-window overshoot becomes holes, free capacity rises, the
full cadence halves (666 → 309), and the heap re-equilibrates at ~1.95 GB. soh:
gate keeps 100% of fulls in-pause, knob-off behavior reproduced exactly. Unit
70/70, suite 56/56, forced-full stress clean on both gate paths. Default stays
off pending the exchange-rate call (results doc §3).

## 4. Instrumentation

`csweep_us` (GCStats CSV): concurrent walk wall time; `sweep_us` reads ~0 on
concurrent-swept cycles and keeps the in-pause cost on gated-out ones. The window
gate's decision is visible as which of the two columns is nonzero.
