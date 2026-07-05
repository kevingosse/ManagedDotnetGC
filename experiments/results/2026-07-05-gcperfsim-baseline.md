# GCPerfSim baseline: first honest comparison (2026-07-05, informal)

Single runs, no variance control, light background activity — a starting line, not a
verdict. GCPerfSim vendored at `experiments/GCPerfSim` (dotnet/performance @ 17f0d0f8);
runner: `-tc 4 -tagb 20 -tlgb 0.5 -sohsi 50 -sohsr 100-4000 -tk time` (+ scenario deltas),
workstation, non-concurrent, win-x64, GC dll = **Release** NativeAOT publish (M1-complete
build, same night). Wall/peak-WS measured outside the process.

| Scenario | Stock WKS | ManagedDotnetGC | Ratio |
|---|---|---|---|
| SOH churn | 2.50 s · 467/105/3 GCs · 1046 MB peak | 8.33 s · 51 full GCs · 1368 MB peak | 3.3× |
| + LOH mix (`-lohar 50 -lohsr 100000-2000000 -lohsi 50`) | 2.57 s · 450/148/7 · 1378 MB | 7.58 s · 47 full · 1393 MB | 3.0× |
| + pinning (`-sohpi 100`) | 2.48 s · 466/104/3 · 1068 MB | 8.35 s · 52 full · 1349 MB | 3.4× |

## Reading

- **The gap is generational, as predicted.** Stock does ~470 cheap gen0 collections that
  touch only nursery survivors; we do ~50 *full* collections, each marking the entire
  ~0.5 GB live set. ROADMAP confidence-ordering said pure gen0 churn is the hardest fight;
  the fix on the board is M4 (sticky generations via the card table), plus profiling to
  split the blame between mark cost and the allocation slow path (window handout = global
  lock + UnmanagedCallersOnly round trip every ~128 KB per thread).
- **Debug vs Release GC matters ~2.5×**: the same SOH scenario ran 21 s with the Debug dll
  before switching to Release (8.3 s). Benchmarks must always use the Release publish;
  run-tests.cmd publishes Debug.
- **This pinning scenario doesn't capture the pinning story.** `-sohpi 100` barely dents
  stock here (uniform small objects, small live set, short run — nothing fragments). The
  structural-win claim needs the long-running fragmentation shape (pinned socket buffers
  amid churn); design that scenario deliberately in M3 proper.
- Our final heap size after the run is ~7× smaller than stock's (136 MB vs 1 GB) — the
  budget converges the heap to ≈ 2× live while stock lets gen0 balloon for throughput.
  That is the footprint-vs-throughput trade M3/M7 tuning gets to play with (budget knob,
  SPEC-M2 §13).

## Next (M3 proper)

1. Real harness: N iterations, medians + variance, pause histograms (needs GC event sink or
   in-GC pause log), CPU time, more scenarios (burst, cache-churn, fragmentation-pinning).
2. Profile one SOH-churn run under our GC: apportion the 3× between mark, sweep/zeroing,
   and alloc-path overhead before touching any knob.
