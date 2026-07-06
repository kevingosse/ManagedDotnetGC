import csv
import math
import statistics
from collections import defaultdict

CSV_PATH = r"E:\git\ManagedDotnetGC\experiments\results\perf-history.csv"
OUT_DIR = r"E:\git\ManagedDotnetGC\experiments\results\2026-07-06-milestone-graph"

SCENARIOS = ["soh", "lohmix", "pin", "pinheavy"]

# (name, label, sha, gc-value-to-match)
# 2026-07-06 evening: all six re-benched as "-r2" labels in the same sitting as the new
# M7 "mutator war" stage (label m7-stash2) so every chart point shares one sitting —
# see experiments/results/2026-07-06-milestone-graph/summary.md for the write-up.
MILESTONES = [
    ("M2 baseline",     "backfill-m2-r2", "407cf59", "custom"),
    ("M4 sticky gens",  "backfill-m4-r2", "5db0ed9", "custom"),
    ("M5 parallel",     "backfill-m5-r2", "344ce8b", "custom"),
    ("M6 concurrent",   "backfill-m6-r2", "1aedac9", "custom"),
    ("M7 tuning",       "backfill-m7-r2", "7689ab1", "custom"),
    ("M6.5 sweep-assist","m65s2-final-r2", "765f680", "custom"),
    ("faster allocation (M7 mutator war)", "m7-stash2", "e65fa79", "custom"),
    # Late-evening second sitting of 2026-07-06 (eighth session). Cross-sitting drift
    # CHECKED via re-run anchors (label m7-shards-anchor): stock WKS geomean 2.399 →
    # 2.465, SVR-h8 1.405 → 1.445 — the machine ran +2.7–2.9% SLOWER, so charting this
    # point raw against the earlier sitting's reference lines is conservative (the
    # same-sitting ratio vs h8 is 0.849).
    ("sharded supply (M7)", "m7-shards", "98517ee", "custom"),
]

# Stock anchors also re-measured tonight in the same sitting (label m7-stash2-anchor),
# replacing the older `svrgap` rows; four configs since the 2026-07-06 DATAS discovery
# (bare gcServer=1 is adaptive-heap-count DATAS, not fixed-32 — that needs -HeapCount 32).
STOCK_REFS = [
    ("Workstation GC",          "m7-stash2-anchor", "stock-wks"),
    ("Server GC (DATAS)",       "m7-stash2-anchor", "stock-svr-datas"),
    ("Server GC (8 heaps)",     "m7-stash2-anchor", "stock-svr-h8"),
    ("Server GC (32 heaps)",    "m7-stash2-anchor", "stock-svr-h32"),
]

def load_rows():
    rows = []
    with open(CSV_PATH, newline="") as f:
        r = csv.DictReader(f)
        for row in r:
            rows.append(row)
    return rows

def median_by_scenario(rows, label, gc):
    out = {}
    for scen in SCENARIOS:
        vals = [float(r["wall_s"]) for r in rows
                 if r["label"] == label and r["gc"] == gc and r["scenario"] == scen]
        if vals:
            out[scen] = statistics.median(vals)
    return out

def geomean(values):
    vals = [v for v in values if v is not None]
    if not vals:
        return None
    logs = [math.log(v) for v in vals]
    return math.exp(sum(logs) / len(logs))

def main():
    rows = load_rows()

    milestone_results = []
    for name, label, sha, gc in MILESTONES:
        med = median_by_scenario(rows, label, gc)
        gm = geomean(med.values())
        missing = [s for s in SCENARIOS if s not in med]
        milestone_results.append((name, label, sha, med, gm, missing))

    stock_results = []
    for name, label, gc in STOCK_REFS:
        med = median_by_scenario(rows, label, gc)
        gm = geomean(med.values())
        missing = [s for s in SCENARIOS if s not in med]
        stock_results.append((name, label, gc, med, gm, missing))

    # ---- write summary.md ----
    lines = []
    lines.append("# Milestone graph — computed geomeans (2026-07-06)\n")
    lines.append("All custom-GC milestone rows backfilled TODAY (2026-07-06) via git worktree + Release ")
    lines.append("publish + `bench-gcperfsim.ps1`, same machine, same sitting, except M6.5 which reuses ")
    lines.append("today's `m65s2-final` rows (current HEAD build). Stock reference rows reuse today's ")
    lines.append("`svrgap` rows (also same sitting). Geomean = geometric mean of the 4 scenario medians ")
    lines.append("(3 iterations each).\n")

    lines.append("## SHA notes / corrections\n")
    lines.append("- M2 baseline, M4 sticky gens, M5 parallel: SHAs as given (407cf59, 5db0ed9, 344ce8b) "
                  "are themselves the code-landing commits — used as-is.\n")
    lines.append("- **M6 concurrent**: the CSV's `m6s3-fairness` label records sha `b889921`, but that "
                  "commit is a **docs-only** commit (\"ROADMAP reflects M6 stages 0-2 landed\") that "
                  "chronologically **precedes** the actual \"M6 stage 3 default-on\" code "
                  "(`1aedac9`, \"concurrent cycles default-on; free-region decommit leaves the "
                  "pauses\") by ~47 minutes. Checking out `b889921` would silently drop the milestone's "
                  "defining change. Backfilled against **`1aedac9`** instead.\n")
    lines.append("- **M7 exchange+quantum**: likewise, the CSV's `m7-quantum` label records sha "
                  "`79ce1dc` (\"docs: M7 memory exchange rate\"), which is an **ancestor** of "
                  "`7689ab1` (\"M7: kill the drain-termination Sleep(1) quantum\") — i.e. it predates "
                  "the quantum fix that gives the milestone its name (confirmed via `git diff --stat "
                  "79ce1dc 7689ab1`: `GCHeap.Mark.cs` and the new `GCHeap.Concurrent.cs` pre-drain only "
                  "land in `7689ab1`). Both `79ce1dc` and `1aedac9`/`b889921` bench UTC timestamps "
                  "predate their own commit's timestamp, consistent with this repo's habit of "
                  "benching against uncommitted local edits and committing afterward with a "
                  "convenience `-Sha`. Backfilled against **`7689ab1`** instead, which is the first "
                  "commit that actually contains the quantum fix.\n")

    lines.append("\n## Per-scenario medians (wall seconds, backfilled today unless noted)\n")
    lines.append("| Milestone | sha used | soh | lohmix | pin | pinheavy | geomean |")
    lines.append("|---|---|---|---|---|---|---|")
    for name, label, sha, med, gm, missing in milestone_results:
        cells = []
        for s in SCENARIOS:
            cells.append(f"{med[s]:.3f}" if s in med else "—")
        note = f" (missing: {', '.join(missing)})" if missing else ""
        lines.append(f"| {name} | `{sha}` | {cells[0]} | {cells[1]} | {cells[2]} | {cells[3]} | "
                      f"**{gm:.3f}**{note} |")

    lines.append("\n## Stock reference medians (today's `svrgap` rows, sha 765f680)\n")
    lines.append("| Config | soh | lohmix | pin | pinheavy | geomean |")
    lines.append("|---|---|---|---|---|---|")
    for name, label, gc, med, gm, missing in stock_results:
        cells = [f"{med[s]:.3f}" if s in med else "—" for s in SCENARIOS]
        lines.append(f"| {name} | {cells[0]} | {cells[1]} | {cells[2]} | {cells[3]} | **{gm:.3f}** |")

    lines.append("\n## Footnotes\n")
    for name, label, sha, med, gm, missing in milestone_results:
        if missing:
            lines.append(f"- **{name}** (`{sha}`): missing scenario(s) {', '.join(missing)} — "
                          f"geomean computed from the scenarios available.")
    lines.append("- M2 baseline predates the `pinheavy` scenario's existence in the archive's protocol "
                  "(added 2026-07-06), but today's backfill ran it anyway against the old GC build using "
                  "the current `bench-gcperfsim.ps1`/GCPerfSim — it completed normally, so no scenario "
                  "is actually missing in the final chart.")
    lines.append("- **sharded supply (M7)** (`98517ee`) is a *second sitting* late the same evening. "
                  "Cross-sitting drift was measured, not assumed: the stock anchors were re-run in that "
                  "sitting (label `m7-shards-anchor`) and came back +2.7–2.9% slower (WKS geomean "
                  "2.399 → 2.465, SVR-h8 1.405 → 1.445), so charting the point raw against the earlier "
                  "sitting's reference lines is conservative. Same-sitting ratio vs tuned SVR-h8: "
                  "**0.849** (per-scenario 0.95/0.82/0.96/0.69 — the first full-matrix win).")

    with open(f"{OUT_DIR}\\summary.md", "w", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")

    # ---- print data for the plotting step ----
    print("MILESTONES")
    for name, label, sha, med, gm, missing in milestone_results:
        print(name, sha, gm, missing)
    print("STOCK")
    for name, label, gc, med, gm, missing in stock_results:
        print(name, gm)

if __name__ == "__main__":
    main()
