# Where the 3× actually goes: soh-churn phase profile (2026-07-06)

The M3 question was "apportion the gap between mark, sweep/zeroing, and alloc-path overhead
before touching any knob." Answered with in-GC instrumentation (`GcStats`, opt-in via
`DOTNET_GCStatsFile=<path>`): per-collection CSV with suspend / mark subphases / sweep /
zeroing timings, marked-object counts, and cumulative alloc-path counters. Rows are written
after RestartEE so the file I/O never extends the pause it measures. Overhead with stats on
is inside run-to-run noise (8.09 s vs the 8.3–9.0 s archive band); with the env var unset,
every site is one branch.

Run: canonical `soh` scenario (`-tc 4 -tagb 20 -tlgb 0.5 -sohsi 50 -sohsr 100-4000 -tk time`),
Release GC dll @ instrumentation commit, wall 8.26 s, 53 full collections, same-day stock
reference band ≈ 2.8 s.

## Phase totals (53 GCs, 8.26 s wall)

| Pool | Time | Share of wall |
|---|---|---|
| **Total pause (suspend→restart)** | **6.80 s** | **82%** |
| — sweep | 5.17 s | 63% |
| —— of which zeroing | 1.98 s (36.9 GB @ ~19 GB/s) | 24% |
| —— of which walk + plug writes | ~3.2 s | 39% |
| — mark (roots + transitive trace) | 1.62 s (35.4 M obj, 22.7 GB) | 20% |
| — everything else (suspend, handles, weak, finalization) | < 0.01 s | ~0 |
| Alloc slow path (thread-time, 4 threads) | 1.90 s / 2,022,016 window handouts | ~0.94 µs each |

Steady state (GC ~25, live 515 MB): pause ≈ 150 ms = 36 ms mark + 113 ms sweep
(40 ms of that zeroing ~715 MB). Pause is O(heap): it grows linearly from 5 ms (GC 0,
53 MB live) to 150 ms as the live set fills.

## Findings

1. **The standing hypothesis was wrong — sweep dominates, not mark.** Marking the whole
   0.5 GB live set 53 times costs 1.6 s; *sweeping* costs 5.2 s. Every collection walks
   every object in every non-empty bump region (`ComputeSize` = one MethodTable deref per
   object, cache-miss bound, ~43 ns/object over ~75 M object-visits) to find dead extents.
   The wholesale-recycle fast path almost never fires: GCPerfSim's 2% survivors scatter
   across all regions, so ~every region needs the full walk.

2. **Zeroing re-pays for bytes that are already zero.** 36.9 GB zeroed vs ~27 GB allocated
   through windows: dead extents are re-zeroed *every* sweep even when they were already
   plugs (a persistent hole between two survivors gets memset 50× over the run). `ClosePlug`
   zeroes the whole extent unconditionally.

3. **The window handout path fragments itself.** 2.02 M handouts for ~20 GB = one every
   ~10 KB, not every 128 KB: the hole-first carve policy prefers swept holes, whose median
   extent is tens of KB, and each handout costs a FixAllocContext plug + global lock +
   UnmanagedCallersOnly round trip (~0.94 µs). 1.9 s of app-thread time, plus whatever the
   lock convoy costs beyond that.

4. **Suspension is free at this scale** (~40 µs/GC), and the whole handle/weak/finalization
   pipeline is invisible (< 10 ms total). The fight is entirely mark, sweep, zero, and the
   allocator protocol.

## What this means for the roadmap

- **M4 sticky generations attacks the two biggest pools at once**: young collections mark
  only young reachability (kills most of the 1.6 s mark) and sweep only young regions
  (kills the old-region share of the 3.2 s walk). Old, mostly-live regions stop being
  walked entirely.
- **What M4 does *not* fix**: zeroing volume (young dead ≈ same bytes) and the young-region
  walk. Queued behind M4, measured candidates:
  - *Zero-at-carve*: move the memset from sweep (STW, cache-cold) to window handout
    (mutator threads, parallel, cache-warm ahead of first use). Sweep becomes plug writes
    only. Would also stop re-zeroing persistent plugs (finding 2).
  - *Handout policy*: don't serve bump windows from holes below a floor (e.g. half a
    window); reserve small holes for the block tier or accept bounded waste. Kills the
    10 KB handout cadence (finding 3).
- Pause histogram infrastructure now exists for the M3 exit criterion (per-GC `pause_us`
  in the stats CSV).

Raw CSV archived at [2026-07-06-soh-profile.csv](2026-07-06-soh-profile.csv).
