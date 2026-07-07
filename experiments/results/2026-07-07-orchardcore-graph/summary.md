# OrchardCore blog homepage — chart data (2026-07-07)

Medians, blog homepage (`/`), 128 connections, same-sitting 2026-07-07 evening (GC dll =
M9.1/`70be506`). Source rows: `experiments/results/orchard-history.csv`, labels
`oc-custom-1`/`oc-custom-2`, `oc-stockwks-1`, `oc-stockh8-1`/`oc-stockh8-2`, endpoint=`home`.

| GC | rps | p50 ms | p99 ms | peak WS MB |
|---|---:|---:|---:|---:|
| stock-wks | 1694 | 67 | 222 | 400 |
| custom (ManagedDotnetGC M9.1) | 4940 | 24 | 64 | 985 |
| stock-svr-h8 | 5236 | 18 | 172 | 950 |

Chart: `orchardcore-home.png` — grouped bar chart, two panels (throughput rps, p99 latency ms),
custom bar highlighted in the hero blue used throughout the milestone charts.

For the full analysis (reading, the excluded single-post endpoint, repro steps), see
`experiments/results/2026-07-07-orchardcore.md`.
