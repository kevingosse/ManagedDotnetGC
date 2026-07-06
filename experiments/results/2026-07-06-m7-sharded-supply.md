# M7 sharded supply: reservoir + private shard lists (2026-07-06, eighth session, ran past midnight)

Session goal (next-session list #1): the structural lever on the remaining soh/pin gap —
split the allocation supply across per-thread shards so handouts stop convoying on the
one alloc lock. The census said 90 ms wait + 157 ms carve per soh run remained after the
window stash, all serialized, and blocks/spans/trim/zeroer shared the same lock.

All numbers same-sitting A/Bs (bench anchors re-run tonight: stock WKS + tuned SVR-h8).

## Final design (three tries — the failures are the documentation)

**Shipped shape.** N supply shards (default `min(ProcessorCount, 16)`,
`DOTNET_GCAllocShards` hex knob, `=1` ≈ old single-lock), each holding a private
recycled-hole list, per-class lists and its own active bump region behind its own
`GcAwareLock`. Thread→shard affinity rides the per-thread stash struct in
`gc_alloc_context.gc_reserved_1` (round-robin at first allocation; `[ThreadStatic]` dies
on EE threads). **All sweep output splices into one global *reservoir*** (own lock);
a dry shard **pulls a batch of 4 regions** (first-fit for the request, like the old
global walk) into its private list — demand-directed distribution by construction.
Steal from sibling privates (TryAcquire-only, bounded probes) survives as a straggler
drain. The pool/frontier stay global behind a pool lock (span run scans, trim, zeroer
checkouts need one coherent pool). Lock order: ≤1 shard lock → reservoir|pool, never
reservoir+pool nested, cross-shard = TryAcquire only. Mutator sweep-assist splices into
the assisting thread's own shard; the span tier lost its inline assist (lock-order
inversion) and retries through a standalone assist instead, frontier only after the
plan is exhausted.

**Failure 1 — rotating splices + steal-after-active-bump** (+640 MB soak, wait *worse*):
per-shard hole-first isn't whole-heap hole-first. Hot shards chained fresh 2 MB regions
off the pool while holes piled up in cold shards; supply landed uniformly (rotation)
while demand concentrated. Committed 2.50 GB vs 1.86 (shards=1), soak wait_ms 12.4 s vs 8.6.

**Failure 2 — steal-before-bump without sealing** (wait fixed: 2.4 s; memory moved, not
freed): tail_mb exploded 66 → 846 MB. `BeginConcurrentSweep` abandons every shard's
active bump at each full cycle (93/soak); an abandoned region's never-carved tail above
the cursor is invisible to sweeps until the whole region dies. One per full was noise;
sixteen per full — refilling slowly because carves prefer holes — is half a gigabyte.
→ **SealActiveBumps**: at pause B each active's tail is plugged and the cursor advanced
to the data end, so the following full sweep folds it into the trailing hole (tail_mb
back to ~15).

**Failure 3 — rotation itself**: with tails sealed the holes just came back (1.25 GB)
and wait with them — supply/demand decoupling is not fixable by consumption order.
→ the reservoir + demand pulls above. (Also tested and reverted: preferring pool members
without committed-free neighbors to preserve span pairs — lohmix's peak bimodality
survived it unchanged, so the mechanism is disproven, machinery deleted.)

## Same-sitting fairness matrix (medians of 3; wall s / peak WS MB)

| scenario | ours (shards) | ours (=1) | knob A/B | stock WKS | SVR-h8 | vs WKS | vs h8 |
|---|---|---|---|---|---|---|---|
| soh | 1.241 / 1985 | 1.377 / 2295 | **−9.9%** | 2.344 / 1069 | 1.303 / 2179 | 0.53× | **0.95×** |
| lohmix | 1.181 / 2549* | 1.245 / 2227 | −5.1% | 2.515 / 1386 | 1.438 / 2161 | 0.47× | **0.82×** |
| pin | 1.315 / 2001 | 1.375 / 2078 | −4.4% | 2.422 / 1057 | 1.364 / 2177 | 0.54× | **0.96×** |
| pinheavy | 1.177 / 4219 | 1.354 / 3956 | **−13.1%** | 2.584 / 4349 | 1.707 / 4394 | 0.46× | **0.69×** |

**Every scenario now beats tuned SVR-h8 on wall — the first full-matrix win**
(yesterday: 1.06/0.95/1.10/0.94). Footprint: soh and pin peak *below* h8 and below
shards-off; pinheavy below h8. \*lohmix peak is bimodal: 2162–2223 (h8-level) on clean
iterations, 2530–2740 on ratcheted ones — open item, mechanism unknown (pool-pair
splitting disproven). Note GCPerfSim runs `-tc 4`, so only ~5 shards engage on the
bench; the knob A/B is 4 allocation heads vs 1, not 16.

## Census (soh, per run, DOTNET_GCStatsFile on)

| | shards | shards=1 | Δ |
|---|---|---|---|
| wait_ms | **8.3** | 59.1 | −86% |
| carve_ms | 90.2 | 114.7 | −21% |
| win_ms total | **143.9** | 247.9 | −42% |
| win_n | 556 K | 836 K | −33% (fewer, bigger windows) |

(Series baseline: wait was 384 ms before the window stash, 59 after — sharding takes
the residue to 8. The full mutator-war arc: win_ms 1878 → 248 → 144 ms/run.)

## ASP.NET soak (120 s × several, 32 workers)

0 errors everywhere, server alive, young p50 ~3.2 ms / p99 ~5.6 / max < 8.5 ms —
pause profile unchanged. Throughput 5.70–6.10 M reqs/2 min across all variants
(shards on/off within each other's noise band; the soak is not lock-bound — the stash
already absorbed Kestrel's allocation cadence). **Cost: WS 2.22 GB vs 1.91 (shards=1),
a +310 MB premium** that scales with *effective allocation heads* (~60 threads on the
soak vs 4 on the bench): more concurrent active-bump/hole heads smear survivors across
more partially-filled regions, and each survivor holds its region's remainder captive.
The shard-count dial trades this directly (4 shards ≈ +200 MB; 1 ≈ baseline).

## Validation

Unit 70/70; suite 56/56 (custom GC confirmed loaded); GcStress tight-full 30 s,
young-heavy 30 s, shards=1 and shards=1+stash=0 combos — 0 checksum failures, exit 0
on all; four 2-min soaks, 0 errors. `bench-gcperfsim.ps1` gained `-AllocShards` (tags
rows `custom-shardsN`) and now clears leftover `GCWindowStash`/`GCNtZero`/
`GCCardPreDrain`/`GCAllocShards` env knobs.

## Open

1. **lohmix peak bimodality** (+0 to +27% vs h8 on ratcheted iterations) — span-tier
   interaction with multi-head supply; pool-pair preservation disproven as the fix.
2. **Soak footprint premium** (+310 MB at 16 shards / 60 threads) — candidate levers:
   demand-shaped shard count (engage shards lazily per contention), smaller active-bump
   grain for cold shards, or accepting a server-profile default of 4–8 shards.
3. Milestone chart refresh (same-sitting backfill of all stages + tonight's
   `m7-shards`/`m7-shards-anchor` rows) — deferred, anchors already archived.
4. Carried: young sweep off-pause, pause A (~21 ms), FullRatio frontier curve,
   HasFinalizerRun RMW, bitmap zero-skip in sweep/assist chunks.
