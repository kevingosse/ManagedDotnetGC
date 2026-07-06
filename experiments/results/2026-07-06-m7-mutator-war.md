# M7 mutator war: census, NT-store zeroing, card-scan carry, window stash (2026-07-06, seventh session)

Session goal (settled in the previous evening's discussion): attack the mutator-side
half of the soh/pin gap to tuned SVR-h8 — census first, then the biggest rock the
census names. All numbers below are same-sitting A/Bs; the machine drifted ~9% slower
across the evening (stock WKS soh 2.205 → 2.405 between sittings), so cross-sitting
walls are not comparable but ratios and same-build knob A/Bs are.

## 1. Census: win_ms split (wait / carve / zero)

Three new cumulative CSV columns (`wait_ms`, `carve_ms`, `wzero_ms`) split the
mutator-felt window handout. soh, per run, at session start:

| component | ms/run | share |
|---|---|---|
| zero-at-carve (wzero) | 1140 | 61% |
| lock wait | 457 | 24% |
| carve work under lock | 256 | 14% |
| **win_ms total** | **1878** | |

Zero throughput measured 10.8 GB/s aggregate over 19.9 GB/run. **Verdict: zeroing
first, lock second** — as the audit predicted from `zero_us`.

## 2. NT-store zeroing (`Zeroing.cs`, DOTNET_GCNtZero=0 opts out)

Machine truth (scratch microbench, 512 MB working set): temporal `Span.Clear` walls
at **~33 GB/s aggregate** no matter the thread count (RFO-bound); AVX2/AVX512 NT
stores hit **54–69 GB/s** at ≥128 KB shapes and win at 4 threads even for 20 KB
chunks. AVX512 buys nothing over AVX2 256-bit NT stores on this machine.

**The trap that ate the first attempt**: NativeAOT resolves `Avx.IsSupported` at
*compile time* against the default x86-64-v2 baseline — no AVX — so the NT path
compiled to dead code and zero_us moved 3%. `<IlcInstructionSet>x86-64-v3</IlcInstructionSet>`
(Haswell 2013+ floor) engaged it. Sites: `ZeroMemory` (all zero-at-carve),
`ClearMarks`, `ClearRegionMarks`, `ClearCards`; temporal below 4 KB; `sfence` before
returning keeps NT zeros ordered ahead of header/publication stores.

Census after: zero throughput 10.8 → **34 GB/s**, wzero 1140 → **95 ms/run**, and the
pre-zeroed window share jumped 48% → 88% — the background zeroer, 3× faster, now
outruns demand, so the biggest effect is windows arriving clean rather than faster
inline memsets.

Same-build knob A/B (medians of 3): **soh −3.7%, pin −9.1%, lohmix −4.3%,
pinheavy −5.7%**. Same-sitting soh ratio vs stock WKS: 0.663 → **0.639×**.

## 3. Young cards: forward-carry head + AVX2 run detection

`ScanRegionCardRuns` now carries a highest-set-bit watermark across a region's dirty
runs: each head search backward-scans only its own unseen gap, so bitmap words are
read at most once per region scan (the old per-run search re-walked up to the full
128 KB lookback on smear regions — runs ~2 KB apart, survivors ~100 KB apart). The
body enumerator masks its first word, killing the old head+body double enumeration.
Detection vectorized: `RegionHasDirtyCard` = 8 OR+VPTEST strides per 1 KB slice;
run boundaries via VPCMPEQB+VPMOVMSKB over 32 cards at a time (bump/class/span).

Note for interpretation: `cards_us` wraps the scan *and the tracing* of everything
the cards reach, and trace volume dominates and varies per run — the honest readout
was pause totals and walls. Bench vs the NT build: **soh 1.536 → 1.394** (matches the
−149 ms/run pause-total drop on the census), lohmix −3.7%, pin/pinheavy flat.
Soak (card-geometry law: soak before bench): 6.30M reqs, 0 errors, record window rps
52.6–54.0k, young p50 3.2/max 7.9 ms, full B p50 2.0 ms.

## 4. Per-thread window stash (DOTNET_GCWindowStash=0 opts out)

Post-NT census: the global alloc lock was 82% of what remained (wait 384 + carve 156
of win 659 ms/run, ~1M acquisitions at ~20 KB avg — the hole granularity). Carves now
batch up to 3 extra hole windows out under the same acquisition into a per-thread
stash riding in `gc_alloc_context.gc_reserved_1`; pops take no lock. An epoch bumped
under STW at both sweep entries forfeits all stashes whenever the supply rebuilds;
sweeps then reclaim the extents as ordinary bitmap-gap holes.

Census: **wait 384 → 90 ms/run**; win_ms 659 → 475. Total mutator handout cost for
the session: **1878 → 475 ms/run (−75%)**.

Three findings, two of them scars:

1. **`[ThreadStatic]` in the GC dll dies on EE threads** (fatal on first touch from a
   reverse-P/Invoke thread). The context's GC-reserved slot is the right per-thread
   hook — it's what the stock GC uses for heap affinity, and it's faster anyway.
2. **Stashed extents must be plugged free objects.** Interior-pointer resolution
   walks bump regions object-by-object; the primary window is walkable only because
   it becomes the alloc context and `FixAllocContext` plugs it at every suspension.
   The unplugged first cut AVed in `ResolveInteriorPointer` under GcStress's pinned
   roots — surfacing as a misleading "UnmanagedCallersOnly called from managed"
   fatal. cdb on the live crash (not the fatal message) found it in minutes.
3. **Refills must be hole-only** (`TryGetStashWindow`: no active-bump/assist/pool/
   frontier fallthrough). The full-refill cut let 4 fresh 128 KB carves per
   acquisition outrun the concurrent sweep's publications — 2 of 3 lohmix runs
   ratcheted +0.5 GB peak, the pre-assist M6.5 gate story again. Hole-only refills
   redistribute committed supply and cannot deepen a drought; lohmix peaks returned
   to family (2193/2372/2196 vs baseline 2212/2212/2246) and pin's peak fell
   3.0 → 2.1 GB.

## Where the evening landed (m7-stash2, medians of 3)

| scenario | wall_s | peak_ws | vs m7-cards wall | vs m7-cards peak |
|---|---|---|---|---|
| soh | 1.379 | 2121 | −1.1% | −2.0% |
| lohmix | 1.366 | 2197 | −0.4% | −0.7% |
| pin | 1.400 | 2120 | −1.5% | **−29%** |
| pinheavy | 1.534 | 3994 | +3.2% (noise band) | −0.7% |

Cumulative same-sitting soh: 1.595 (knob-off start) → 1.379 — **−13.5% wall in one
evening**, at equal-or-better footprint.

## The fairness matrix, end-of-sitting anchors (`m7-stash2-anchor`, medians of 3)

| scenario | ours wall/peak | WKS | DATAS | h8 | h32 | vs WKS | vs DATAS | vs h8 |
|---|---|---|---|---|---|---|---|---|
| soh | 1.379 / 2121 | 2.307 / 1062 | 2.041 / 1200 | 1.306 / 2176 | 1.768 / 7124 | 0.60× | 0.68× | 1.06× |
| lohmix | 1.366 / 2197 | 2.422 / 1383 | 1.970 / 1389 | 1.431 / 2162 | 1.625 / 4004 | 0.56× | 0.69× | **0.95×** |
| pin | 1.400 / 2120 | 2.312 / 1069 | 1.962 / 1200 | 1.274 / 2177 | 1.583 / 7121 | 0.61× | 0.71× | 1.10× |
| pinheavy | 1.534 / 3994 | 2.565 / 4392 | 2.555 / 4381 | 1.638 / 4295 | 2.106 / 7639 | 0.60× | 0.60× | **0.94×** |

**The evening moved the tuned-SVR-h8 row from 1.17/1.04/1.15/0.94 (m65s2) to
1.06/0.95/1.10/0.94 — lohmix flipped to a win**, at h8's own footprint (2.1–2.2 GB;
pinheavy 3.99 vs h8's 4.29 GB — both axes). Against default Server GC (DATAS) the
sweep is 0.60–0.71× everywhere; against Workstation 0.56–0.61×. The remaining h8
gaps: soh 6%, pin 10% — the mutator front (sharded supply) and the young-pause floor
are the two levers left.

Soaks (2 min, 32 workers) stayed clean throughout: 0 errors on every build; the
card-rewrite soak set the throughput record (52.6–54.0k rps windows); every pause on
every soak < 10.6 ms.

## Validation per landing

Unit 70/70, suite 56/56, GcStress young-heavy + tight + knob-off clean, 2-min soak —
run for each of the three code landings separately. Both new knobs (`GCNtZero`,
`GCWindowStash`) verified as clean opt-outs under stress.

## Open after this session

- **Sharded supply (mimalloc-style)** remains the structural answer the stash
  approximates: the census still shows 90 ms wait + 157 ms carve, and the stash only
  covers the window tier. Next big rock if the mutator front is continued.
- Young sweep off-pause (~19 ms/run), pause A (~21 ms), FullRatio frontier curve,
  HasFinalizerRun RMW, stock-SVR-DATAS soak anchor — unchanged from the last handoff.
- SIMD audit item still unclaimed: bitmap zero-skip in the sweep walk / assist chunks
  (Vector256 TestZ over mostly-zero words).
- Milestone evolution charts need regenerating with tonight's `m7-stash2` +
  `m7-stash2-anchor` rows (graph upkeep rule).
