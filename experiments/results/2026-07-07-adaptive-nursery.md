# 2026-07-07 — Adaptive nursery boost: closing the TechEmpower gap

Baseline SHA `f176cff` (M7 sharded supply). Mission: close fortunes 0.43× / queries 0.33×
vs tuned stock-svr-h8 (see 2026-07-07-techempower-and-mixed.md).

## 1. Diagnosis: the world was stopped half the time

First instrumented runs (`DOTNET_GCStatsFile` via new `-StatsDir` bench flag), ~60 s of
load per endpoint:

| run      | young GCs | rate     | young p50 | total STW | live max | committed max |
|----------|----------:|---------:|----------:|----------:|---------:|--------------:|
| fortunes | 5,817     | ~97/s    | 5.02 ms   | 34.7 s (~55%) | 86 MB | 692 MB |
| queries  | 8,972     | ~150/s   | 3.44 ms   | 34.3 s (~55%) | 78 MB | 844 MB |

Root cause: `budget = max(64 MB, live)` with live ≈ 20–80 MB pins the young budget at
64 MB against a multi-GB/s web allocation rate. Each young pause carries a fixed cost
dominated by root scanning (mean 3.47 ms on fortunes — all Kestrel thread stacks, serial,
per collection) while marking <1 MB of survivors. Pure fixed cost × 100–150 Hz.

## 2. Confirmation: DOTNET_GCgen0size as exact override (knob sweep)

Made the knob override the budget in both directions (was: cap only). Same bench:

| young budget | fortunes rps | vs h8 | queries rps | vs h8 | peak WS (f/q)    |
|-------------:|-------------:|------:|------------:|------:|------------------|
| 64 MB        | 39.3k        | 0.43× | 3.0k        | 0.33× | 858 / 1,000 MB   |
| 256 MB       | 66.3k        | 0.73× | 6.1k        | 0.69× | 1,173 / 1,342 MB |
| 512 MB       | 72.1k        | 0.80× | 6.7k        | 0.76× | 1,644 / 1,464 MB |
| stock-svr-h8 | 90.0k        | 1.00× | 8.85k       | 1.00× | ~700 MB          |

- Timeout bursts (the 0.43×-era error spikes) vanished at ≥256 MB.
- p99: fortunes 17 → 11 ms, queries 230 → 60 ms.
- SCAR found: the knob applied only on young `ApplyBudget`, so every full collection
  reset `_budget` to max(64 MB, live) and `TrimOutsidePause` (demand = 2×budget) trimmed
  the retention target down to ~2×live, starving the next 256–512 MB young runway →
  starved-full storms (queries g256: 113/114 fulls starved; g512: 282/282, 4.7 fulls/s,
  2.9 s extra STW). Budget and trim demand must move together.

## 3. The policy: frequent-futile-cheap nursery boost (no knob)

`_youngBudgetBoost` rides on top of `ComputeBudget` for young AND full paths (one number
for budget and trim demand — see the SCAR above); grows ×2 (64 MB floor → 448 MB cap)
when a young collection was frequent (<250 ms since the previous one), futile (survivors
< budget/100) AND cheap (<10 ms of pause so far — measured with Stopwatch directly, NOT
GcStats.Timestamp, which returns 0 when stats are off); halves on real survival
(> budget/50) or sparse cadence (>1 s). `DOTNET_GCgen0size` remains as exact override.

Why survival+pause-cost, not pause-ratio: GCPerfSim shapes are pause-heavy by *useful
work* — a pause-ratio controller would inflate them and regress the matrix WS. The
cadence gate keeps lightly-loaded services from paying the cap for pauses they don't take.

- **SCAR (v1)**: futility alone at budget/50 was NOT enough — soh's survival (`sohsi 50`
  ≈ 2% of allocated bytes) sits exactly on a 2% threshold. v1 (grow at <2%, no pause
  gate) boosted soh WS to 2.9–3.8 GB (anchor: ~2.0, h8 2.18) and mixed to 3.8–4.4 GB
  while making wall FASTER (soh 1.24 → 1.11 s) — a memory-for-speed trade the matrix
  story can't afford. The cheap-pause gate is the robust discriminator: GCPerfSim young
  pauses mark real survivors (25–73 ms), web young pauses are fixed cost (2–5 ms).

## 4. Adaptive results

v1 (before the gate fix), TechEmpower: fortunes 71.5k rps / WS 1.46 GB (matches the
hand-tuned 512 MB knob, zero config: ramp 64→512 in 6 collections, then pinned at cap);
queries 6.3k / WS 1.77 GB. Starved fulls 282 → 64 on queries (the flap fix works).
Queries note: g512's starved-full storm kept committed at 1.2 GB vs adaptive 1.58 GB —
the accidental leanness was worth ~6% RPS (6.7k vs 6.3k); retention policy is the lever
(committed scales with demand = 2×budget; young sweep total doubled 1.67 → 3.14 s).

v2 (gated, the committed version) — full validation set, same sitting:

| endpoint | custom before | custom adaptive | stock-svr-h8 | vs h8        | peak WS       |
|----------|--------------:|----------------:|-------------:|--------------|---------------|
| fortunes | 39.1k         | **72.1k**       | 90.0k        | 0.43→**0.80×** | 1,556 MB    |
| queries  | 2.9k          | **6.6k**        | 8.85k        | 0.33→**0.75×** | 1,608 MB    |

p99: fortunes 16.4 → 11.6 ms (h8: 8.7), queries 230 → 62 ms (h8: 46). Zero errors (the
timeout bursts are gone). GCPerfSim same build: soh 1.239 s clean rerun (the 1.297
first pass was TechEmpower bench interference; anchor 1.241), WS 1950–2112 MB; mixed
1.244 s, WS 1889–2111 MB — walls at anchor, WS back below h8 (v1 blowup cured).

## Remaining gap to h8 (~20-25%), measured

From the g512/adaptive stats: young STW is now roots ~1.0–1.5 ms + cards ~1.1–1.6 ms
(583–596 regions/scan, scales with committed) + sweep ~0.6–1.25 ms at 25–45 young/s
(~14–15% of wall), plus fulls. Mutator side: win_ms 16–18 s cumulative per run, of which
wzero (inline zeroing at carve) is ~10–12 s. Candidates, in expected-value order:

1. **Retention** (queries evidence: leaner committed was worth +6% RPS, and cards/sweep
   costs track committed). demand = 2×budget is over-provisioned for young-only regimes —
   the "window allocations" half only exists during concurrent fulls.
2. **Per-root callback overhead**: ScanRootsCallback does GCHandle.FromIntPtr(...).Target
   per reported stack slot, plus an inline DrainMarkStack call per root on young paths.
   Cache the singleton in a static; consider buffering young roots like full marks.
3. **Young root scan is serial** on the GC thread (buffered = !young): stock server GC
   partitions stack scanning across GC threads via ScanContext.thread_number.
4. Card scan at 550–600 regions per young GC; zero-skip bitmap (Vector256) still undone.

## Validation (all same sitting)

- unit 70/70 ✔
- GCPerfSim full matrix at anchor: soh 1.239 clean (anchor 1.241), mixed 1.244 (1.239),
  lohmix 1.178 (1.181, WS better — no ratcheted run), pin 1.223 (1.315, faster; two
  iters +~400 MB WS, within pin-family spread), pinheavy 1.187 (1.177, WS lower) ✔
- TechEmpower updates: 955–970 rps ≈ 0.97× h8 (was 0.91×), p99 at parity ✔
- 2-min soak, 32 workers: 6.32M requests, 0 errors, young p50 3.22 ms / p99 5.48 —
  identical to the M7 soak record. **Boost never engaged** (budget_mb ≈ live_mb all
  run): the soak app's live set grows 268 → 641 MB, so the futility gate blocks growth —
  its WS ramp (1.85 → 2.45 GB) is live-growth × 2×-live convergence, pre-existing ✔
- Soak side-finding: 88/97 soak fulls are "starved" with boost=0 (pre-existing M7
  exchange-rate behavior, full p99 42 ms) — same lever as the retention work below.

## Next

1. Retention/trim: committed scales with demand = 2×budget; queries evidence says leaner
   committed is worth RPS (cards+sweep track committed — Sweep walks 0..frontier even
   youngOnly) and web WS 1.5–1.8 GB vs h8 700 MB is the remaining bad axis.
2. Young pause fixed cost: per-root GCHandle.FromIntPtr in ScanRootsCallback, young root
   scan serial on the GC thread (stock partitions across GC threads), card scan
   550–600 regions/young.
