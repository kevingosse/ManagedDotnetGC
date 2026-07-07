# OrchardCore: first real-application benchmark — 0.94× server GC throughput, 2.7× better p99

First measurement against a real product instead of a synthetic or benchmark-derived
workload: OrchardCore CMS 3.0 (net10), Blog recipe on SQLite, full CMS middleware +
Razor + YesSql per request. Setup: `E:\git\oc-bench\publish` (OcBench.dll, auto-setup,
App_Data must be preserved and CWD must be the publish dir); harness =
`experiments/bench-orchard.ps1` (bench-techempower conventions: dll-deploy trap fix,
inline WS sampler, interleaved A/B/A). 128 connections, blog homepage (`/`).

## Results (same-sitting 2026-07-07 evening, GC dll = M9.1/70be506, medians of 6 iters across 2 brackets)

| GC | rps | p50 | p99 | peak WS |
|---|---|---|---|---|
| stock-wks | 1694 | 67 ms | 222 ms | ~400 MB |
| **custom** | **4940** | **24 ms** | **64 ms** | ~985 MB |
| stock-svr-h8 | 5236 | 18 ms | **172 ms** | ~950 MB |

60 s sustained (1 iter each, interleaved): custom 4823 rps / p99 91 ms / 1013 MB;
stock-h8 5009 rps / p99 185 ms / 933 MB → 0.96×, p99 2×.

## Reading

- **Throughput ratio replicates TechEmpower exactly (0.94×)** — the M9.1 profile's
  diffuse-scheduler-tax diagnosis transfers unchanged to a real app. Nothing about a
  10× heavier request (Razor + YesSql + CMS pipeline vs raw fortunes) changed the gap.
- **The tail flips in our favor on a real app: p99 64 ms vs 172 ms.** Stock server GC
  spikes p99 ≥170 ms in 4 of 6 iterations (and 185 ms over the 60 s run) — the
  episodic-gen2 signature on a heap holding real mid-life state (content items, YesSql
  session caches). Our small frequent pauses keep p99 flat at 60–67 ms. This was
  invisible on TechEmpower fortunes (trivial live set, ~10 ms p99 both sides): it is
  exactly the [[snapshot-collector-design]] / M6 win-axis showing up unprompted, and
  the first workload where the no-compaction architecture WINS on a user-visible
  metric rather than tying.
- **Workstation GC (1-heap default) collapses: 2.9× slower than us** at the same task.
  Only fair to note ASP.NET defaults to server GC.
- WS: parity with server-h8 (~1 GB both). The boost controller behaves on a real app
  (fires on the web shape as designed; no runaway).

## Excluded: the single-post endpoint (`/blog/post-1`)

Pathological under ALL THREE GCs identically (~200 rps, p99 3–6 s, degrading across
iterations, e.g. custom 247→259→129, stock-h8 221→243→186). App-level bottleneck —
smells like SQLite write contention or a lock convoy on the content-item path, not GC
signal. Future: rerun the post endpoint with Postgres or diagnose the write; until
then only the homepage differentiates GCs.

## Repro

```
# app: E:\git\oc-bench\publish (OcBench.dll, ASPNETCORE_URLS=http://127.0.0.1:9080,
#      CWD = publish dir or AutoSetup re-triggers; wwwroot/ must exist after republish)
experiments\bench-orchard.ps1 -Label <x> -GcDll <Release publish dll>
experiments\bench-orchard.ps1 -Label <x> -ServerGC -HeapCount 8
```
