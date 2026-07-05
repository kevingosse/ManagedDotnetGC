# ASP.NET soak under ManagedDotnetGC — M2 exit evidence (2026-07-05)

Setup: `AspNetSample` (minimal API: JSON churn, big strings, LOH-band/span blobs, ~100 MB
retained cache with 10% turnover) served by Kestrel under ManagedDotnetGC; load driver on the
stock GC hammering weighted endpoints over HTTP/1.1 keep-alive. `soak-aspnet.cmd` reproduces.

## Main run — 70.5 minutes, 32 workers (GC build: M2 region heap, commit 407cf59-era)

- **78.66M requests, 0 errors** (78,661,483 OK)
- Sustained ~20k req/s (dipping to ~10-11k only while test-suite builds competed for CPU)
- Working set oscillated in the 660–815 MB band for the entire run — no growth trend
- 19,647 self-triggered collections (~4.6/s), no explicit GC.Collect anywhere in the server
- Run stopped deliberately at 70 min (of a planned 120) — flat-footprint evidence sufficient

## Confirmation run — 90 seconds, 16 workers (final M1-complete build, incl. EE brackets,
card/bundle tables, ref-counted handle scanning)

- 1.86M requests, 0 errors, ~21k req/s, working set 520–636 MB
- Every collection now issues GcStartWork/BeforeGcScanRoots/AfterGcScanRoots/GcDone and the
  bulk-copy card path writes real (lazily committed) card + bundle tables — no measurable
  throughput change vs the pre-bracket build

Verdict: the M2 "runs real apps" exit criterion is met at the ~1-hour scale. A true
multi-hour overnight soak on the final build remains open — run it monitored by a cheaper
session (a completion notification into a large expired-cache context wastes quota).
