# M9 residual-gap profile: GC CPU is at parity; the WS overshoot was free to reclaim

Same-sitting ETW CPU-sampling diff on fortunes (profile-techempower.ps1, 25 s windows,
EtlCpu rollups), post-bump-serve (6b6b106). Profiled RPS 70.7k (ours) vs 76.1k (stock-h8)
= 0.93x, matching the clean-bench gap, so the gap is per-request CPU, not stalls:
**207.5 us/req vs 190.8 us/req = +16.7 us/req.**

## Decomposition (exclusive samples, per request)

| bucket | delta | note |
|---|---|---|
| GC proper | **~0** | our module +3.45, stock's GC lives in coreclr/ntdll which cost −3.47 vs ours |
| kernel (real) | +6.5 | SwapContext +0.7, KeYieldExecution +0.8, syscall dispatch +0.7, rest diffuse |
| CoreLib threadpool | +2.8 | LowLevelLifoSemaphore.Wait spin 4.5 vs 2.5 us/req — burstier work arrival |
| app modules | +2.4 | uniform +7% across mvc/npgsql/EF/kestrel |
| network drivers | +1.9 | uniform +10% |
| unresolved + sampler artifact | +2.8 | |

The headline: **there is no GC hotspot left.** Our GC's direct CPU (write barrier, alloc
path, collections, zeroing) nets to zero against stock's. The residual is a diffuse
per-request tax (scheduling churn + uniform module inflation) plus longer pauses
(p99 10.2 vs 9.2 ms in every run).

Confound noted: bztransmit64 (Backblaze, 25 s CPU) was active during our trace only;
the uniform-inflation attribution carries that asterisk, the RPS gap itself does not
(reproduced in clean interleaved benches below).

## Three clean negatives (fortunes, interleaved A/B/A, 3x15 s each)

1. **Nursery size does not buy RPS.** Fixed budgets via -Gen0MB: adaptive-512 79.4k /
   983 MB; fixed-384 79.3k / 788 MB; fixed-256 79.6k / 660 MB. Flat RPS, −330 MB WS.
   Also kills the pause-frequency story: fixed-256 doubles young GC rate, costs nothing.
2. **Temporal zeroing does not buy RPS.** DOTNET_GCNtZero=0 (Span.Clear everywhere,
   cache-warming): 79.5k vs 80.1k adaptive — noise. The 13th-session NT-at-carve verdict
   survives bump-serve's 20x carve-volume drop.
3. **Queries tolerates the small nursery too:** fixed-256 8.0k vs adaptive 7.8k.
   (Fixed budgets RAISE queries WS — 609 vs 520 — because a fixed budget retains
   2xbudget while the boost path retains 2xbase + boost. The lever must be the boost
   cap, not gen0size.)

## The change: BoostCapBytes 448 -> 192 MB

Boosted budget lands at 256 MB (floor base 64 + 192), trim demand 320 MB. Same-sitting
results (stock-h8 anchor same sitting: fortunes 83.8k / 731 MB):

| endpoint | before (cap 448) | after (cap 192) | stock-h8 |
|---|---|---|---|
| fortunes | 80.1k / 983 MB | 80.4k / **683 MB** | 83.8k / 731 MB |
| queries  | 7.8k / 520 MB  | 8.0k / **448 MB** | (686 MB hist) |
| updates  | ~0.98x / —     | 966 / **429 MB**  | — |

Fortunes WS is now BELOW stock's. RPS unchanged (0.95-0.96x same-sitting).

Gates: 56/56, GcStress x3 clean, GCPerfSim mixed smoke 1.158 s / 2131 MB (stock-h8
1.28-1.36 s / ~2600 MB). GCPerfSim full matrix and soak excluded by construction:
the diff only changes the boost magnitude cap, and boost is provably 0 on those
workloads (detector's survivor/cheap-pause/floor gates, verified at M8/M9).

## What remains for 1x (next session)

The ~4-5% residual has no cheap lever left. Candidates, in order:
1. Pause duration (p99 gap): our young pause is longer than stock h8's per-heap-parallel
   pause; each pause parks 256 connections and forces a threadpool park/unpark cycle
   (the LifoSemaphore/SwapContext signature). Attack: shave the young pause itself
   (remaining floor = fattest-single-stack scan, 385 us) or fewer stacks to scan.
2. CSWITCH-level trace to count context switches per request and locate the burst.
3. Accept 0.95x/0.97x/0.98x at below-stock WS as the plateau for this architecture.
