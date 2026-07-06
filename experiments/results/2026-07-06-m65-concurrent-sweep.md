# M6.5 stage 1: the gated concurrent sweep (2026-07-06)

**TL;DR.** The full-cycle sweep — the top pause-B slice after the quantum fix — now
runs after `RestartEE` on gated-in cycles, behind `DOTNET_GCConcurrentSweep`
(default-off, staging). On the ASP.NET soak it set a new throughput record
(**48.8 k req/s**, was 47.1 k) with **full pause B p50 3.1 ms** (was 5.5) and young
max 10.6 ms (was 26) — at **+0.5 GB WS** (1.45 → ~1.95 GB, asymptotic), because the
sweep window's allocation overshoot becomes holes, more free capacity halves the
full cadence (666 → 309), and the heap re-equilibrates higher. On GCPerfSim soh the
window gate keeps every full in-pause and reproduces knob-off behavior exactly
(committed 2.2 GB, equal walls). Whether the soak's exchange rate justifies a
default flip is an open call — the trade is real on both sides.

## 1. Design (docs/spec-m65-concurrent-sweep.md)

Marks are final at pause B; the sweep only rebuilds allocation supply. Pause B now:
accounting from Σ mark-time `LiveBytes` (identical to the sweep's sum), supply
lists reset under STW, a pause-B region plan (the concurrent walk trusts only the
snapshot — pool pops during the sweep turn Free entries into live allocation
targets), and an O(1) wholesale pre-pass that recycles every all-dead region inside
the pause — deferring that measured **+1.1 GB** on soh (mutators outrun the walk's
publications; the dead nursery must be in the pool at RestartEE). The walk then
runs on the worker pool with the world running, publishing per-chunk supply under
the alloc lock, zeroer gated out, `_gcLock` held (young collections wait,
budget-triggered collects return immediately).

**The window gate**: young collections are blocked for the walk's duration while
allocation flows through, so the cycle overshoots by alloc-rate × walk-time — and
on smear-heavy heaps (soh: every region keeps survivors, pool ≈ 0) the overshoot is
fresh commits that *never decommit* (survivor-bearing regions never pool).
Ungated: committed 2.1 → 3.2 GB. So a cycle only sweeps concurrently when
`pool ≥ max(64 MB, budget/4)` at pause B; otherwise the byte-identical legacy
in-pause sweep runs.

Along the way the `GcAwareLock` contended path lost its early `Thread.Sleep(1)` —
the third timer-quantum site of the day (sweep workers publishing under the alloc
lock hit it against four allocating mutators). Sleep(1) is now reached only after
~1 ms of yielding, preserving cheap long waits (`_gcLock` behind a whole cycle).

## 2. Numbers

ASP.NET soak, 300 s, 32 workers, knob ON vs the same-day quantum-fix soak:

|                        | csweep off (m7-quantum) | csweep ON |
|------------------------|-------------------------|-----------|
| requests / errors      | 14.13 M / 0             | **14.64 M / 0** |
| avg req/s              | 47.1 k                  | **48.8 k** |
| server WS              | flat 1.49 GB            | ~1.95 GB, asymptotic (+0.5) |
| fulls (of collections) | 666 (18%)               | 309 (9%) — 57% gated in |
| full pause B p50/p90/max | 5.5 / 7.2 / 10.6 ms   | **3.1 / 5.9 / 11.0 ms** |
| concurrent-swept cycles' pause B | —             | **~2–3 ms** |
| csweep (off-pause walk) | —                      | p50 6.1 / p90 10.6 / max 15.8 ms |
| young p50/p99/max      | 5.7 / 9.5 / 26.1 ms     | **5.2 / 8.0 / 10.6 ms** |

The memory mechanism, from the census: +300 MB linked holes + +150 MB pool at
equilibrium. Sweep-window allocation lands in fresh regions, its survivors smear,
holes accumulate; more free capacity → the starvation trigger fires later → fulls
halve → less GC work → +3.5% throughput. The system is stable, just at a higher
committed point. (The young-tail improvement likely mixes the lock-backoff fix and
the halved full cadence; not isolated.)

GCPerfSim soh (smear-shaped, pool ≈ 0): gate keeps 100% of fulls in-pause;
committed equilibrium 2.2 GB and walls match knob-off within noise. Ungated
experiment for the record: pause B 11–24 → 4–12 ms at +1.1 GB committed — rejected.

Validation: unit 70/70, suite 56/56 (knob on), forced-full stress 90 s knob-on
clean (236 M ops, 585 fulls, gate flipping between both paths), plus the earlier
knob-on run before the gate (477 M ops / 512 fulls).

## 3. Open

- **Default flip is a policy call**: on server-shaped workloads it buys the lowest
  full pauses yet + a throughput record for +35% WS. The M7 exchange-rate work went
  the other way on a similar trade. Options if the memory matters: tighten pool
  retention when csweep is on, or gate stricter (pool ≥ budget).
- The principled ungated fix is **mutator sweep-assist** (allocation slow path
  drains sweep-plan chunks instead of fresh-committing) or young collections
  against a partially-swept heap — both real re-architectures, neither needed to
  ship the gated form.
- Young sweeps (6 ms of the 12.9 ms soh young p50) remain STW — same assist
  machinery would apply.
