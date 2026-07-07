# OrchardCore blog homepage — chart data (2026-07-07)

Medians, blog homepage (`/`), 128 connections, same-sitting 2026-07-07 late evening (GC dll =
M9.2/`edd5b82`, probe controller). Source rows: `experiments/results/orchard-history.csv`,
labels `m9.2-probe`, `m9.2-probe-b2`, `m9.1-old-ab`, `m9.2-anchor-h8`, `m9.2-anchor-datas`,
endpoint=`home`.

| GC | rps | p50 ms | p99 ms | peak WS MB |
|---|---:|---:|---:|---:|
| stock-wks | 1694 | 67 | 222 | 400 |
| custom (ManagedDotnetGC M9.2, probe controller) | 5266 | 19.6 | 71 | ~1250 |
| stock-svr-h8 | 4992 | 19.6 | 172 | 930 |
| stock-svr-datas | 5482 | 19.0 | 62 | 460 |

p99 values are the median of same-sitting iterations. stock-wks is carried over unchanged
from the earlier (morning) sitting — it was not re-run in this session.

Headline: this is the first real-app result where the custom GC's throughput lands above
pinned stock Server GC at 8 heaps — 5266 vs 4992 rps, 1.055x. The old-controller (M9.1)
comparison anchor is row `m9.1-old-ab`.

Chart: `orchardcore-home.png` — grouped bar chart, two panels (throughput rps, p99 latency ms),
custom bar highlighted in the hero blue used throughout the milestone charts.

For the full analysis and repro steps, see
`experiments/results/2026-07-07-m9.2-probe-controller.md`.
