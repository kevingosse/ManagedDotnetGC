# 2026-07-07 — `mixed` GCPerfSim scenario + first TechEmpower web-workload numbers

SHA `fa0f0c9` (M7 sharded supply), Release NativeAOT publish. Motivation: the milestone
matrix is 2/4 pinning scenarios — over-weighted vs the .NET team's own practice where
pinning is one niche config. Two answers: (a) a `mixed` scenario with realistic light
pinning inside one workload; (b) a real web workload (TechEmpower) where the GC is
exercised by 100s of pool threads and latency matters.

## 1. GCPerfSim `mixed` (95/5): the win does NOT depend on pin overweighting

New canonical scenario (additive): `soh` params + `-sohpi 20` → 1 in 20 survivors pinned.

| gc            | median wall | vs h8 | peak WS       |
|---------------|------------:|------:|---------------|
| stock-wks     | 2.475 s     | 1.91× | 2.2–2.4 GB    |
| stock-svr-h8  | 1.298 s     | 1.00× | 2.63–2.66 GB  |
| custom        | 1.239 s     | 0.95× | 1.9–2.6 GB    |

Same 0.95× ratio as pure `soh` — light pinning isn't carrying the result.

## 2. TechEmpower aspnetcore/Mvc on Windows: we get beaten badly

Setup (reusable):
- TFB sparse clone at `E:\git\tfb` (frameworks/CSharp/aspnetcore + toolset/databases).
- Postgres: `docker run -d --name tfb-database -p 5432:5432 -e POSTGRES_USER=benchmarkdbuser
  -e POSTGRES_PASSWORD=benchmarkdbpass -e POSTGRES_DB=hello_world
  -v E:\git\tfb\toolset\databases\postgres\create-postgres.sql:/docker-entrypoint-initdb.d/create.sql postgres:16`
- App: `dotnet publish src\Mvc -c Release -r win-x64 --self-contained false -o E:\git\tfb\publish\mvc`
  — plain net10.0 (EF Core + Npgsql + Razor), zero code changes; connection string via env.
- Driver: bombardier 256 conns, 10 s warmup, 3×15 s, `experiments/bench-techempower.ps1`,
  rows in `results/techempower-history.csv`. json/plaintext skipped (not GC-bound).

Median RPS (errors = timeouts during run):

| endpoint | stock-wks | stock-svr-h8 | custom | custom vs h8 | custom p99 vs h8 |
|----------|----------:|-------------:|-------:|-------------:|------------------|
| fortunes | 76,658    | 89,960       | 39,073 | **0.43×**    | 16.4 vs 8.7 ms   |
| queries  | 8,212     | 8,851        | 2,908  | **0.33×**    | 230 vs 46 ms     |
| updates  | 950       | 986          | 894    | 0.91×        | 376 vs 306 ms    |
| peak WS  | ~195 MB   | ~700 MB      | ~870 MB| —            |                  |

Occasional error bursts (467/1161/122 timeouts in single iterations) — custom only.

## Reading

- GCPerfSim (tc=4, throughput-oriented, 20 GB alloc sprints) and a Kestrel server
  (100s of pool threads, small async-flow allocations, latency-sensitive) are different
  planets. The M7 SCAR "GCPerfSim is tc=4; the soak is where thread-scaling costs show"
  applies with full force: this is the demand-shaped shard-engagement / wait problem,
  now with an RPS number attached.
- `queries` (0.33×) is the pathological case: 20 awaits per request → deep continuation
  churn across many threads. `updates` is DB-bound (par for everyone).
- Peak WS worse than h8 too (870 vs 700 MB) — no memory consolation here.
- These numbers are the strongest argument for next-session items 1–2 (lohmix
  bimodality, soak/thread-scaling footprint) and for profiling the GC under the web
  workload rather than more GCPerfSim tuning.

Machine state: normal desktop background; Docker Desktop (Linux engine) running the
Postgres container during all three configs, including the stock anchors.
