import csv
import math
import statistics
from collections import defaultdict

CSV_PATH = r"E:\git\ManagedDotnetGC\experiments\results\perf-history.csv"
OUT_DIR = r"E:\git\ManagedDotnetGC\experiments\results\2026-07-06-milestone-graph"

SCENARIOS = ["soh", "lohmix", "pin", "pinheavy"]

# (name, label, sha, gc-value-to-match)
# 2026-07-07 sitting: every milestone (the eight from the 2026-07-06 backfill plus the
# two new M8 stages) and all four stock anchors re-benched fresh in ONE sitting via
# git worktree + Release publish + `bench-gcperfsim.ps1` — see summary.md write-up.
MILESTONES = [
    ("M2 baseline",     "m8-milestones-m2", "407cf59", "custom"),
    ("M4 sticky gens",  "m8-milestones-m4", "5db0ed9", "custom"),
    ("M5 parallel",     "m8-milestones-m5", "344ce8b", "custom"),
    ("M6 concurrent",   "m8-milestones-m6", "1aedac9", "custom"),
    ("M7 tuning",       "m8-milestones-m7", "7689ab1", "custom"),
    # M6.5 sweep-assist: the previously-recorded sha 765f680 is a docs-only commit
    # (`git show 765f680 --stat` touches only ROADMAP.md + results/*). Its direct
    # parent 2a62c92 ("M6.5 stage 1: gated concurrent full-cycle sweep") *is* a code
    # commit, but stage 1 ships DOTNET_GCConcurrentSweep default-OFF — bench-gcperfsim.ps1
    # sets no such env var, so benching stage 1 would silently measure the OLD in-pause
    # sweep, not the feature the milestone is named for. The "mutator sweep-assist" work
    # and the default-on flip land one commit later, in d95dddb ("M6.5 stage 2: bitmap
    # walks, mutator sweep-assist, concurrent sweep default-on") — same pattern as the
    # M6/M7 corrections below (walk forward to the first commit that actually contains
    # the named feature). Rebacked against d95dddb.
    ("M6.5 sweep-assist","m8-milestones-m65", "d95dddb", "custom"),
    ("faster allocation (M7 mutator war)", "m8-milestones-fasteralloc", "e65fa79", "custom"),
    ("sharded supply (M7)", "m8-milestones-sharded", "98517ee", "custom"),
    # M8 (2026-07-07 sitting): both SHAs verified as code-bearing commits (`git show
    # --stat` shows ManagedDotnetGC/*.cs changes, not just docs/results).
    ("Adaptive nursery", "m8-milestones-adaptive", "ceffbfc", "custom"),
    ("Vectorized bitmap skip", "m8-milestones-vecbitmap", "68696ff", "custom"),
]

# Stock anchors re-measured fresh in TODAY's (2026-07-07) sitting, same machine, same
# sitting as every milestone row above — four configs since the 2026-07-06 DATAS
# discovery (bare gcServer=1 is adaptive-heap-count DATAS, not fixed-32 — that needs
# -HeapCount 32).
STOCK_REFS = [
    ("Workstation GC",          "stock-wks", "stock-wks"),
    ("Server GC (DATAS)",       "stock-svr-datas", "stock-svr-datas"),
    ("Server GC (8 heaps)",     "stock-svr-h8", "stock-svr-h8"),
    ("Server GC (32 heaps)",    "stock-svr-h32", "stock-svr-h32"),
]

METRICS = [
    ("wall_s", "wall seconds", "{:.3f}"),
    ("peak_ws_mb", "peak working-set MB", "{:.1f}"),
]

def load_rows():
    rows = []
    with open(CSV_PATH, newline="") as f:
        r = csv.DictReader(f)
        for row in r:
            rows.append(row)
    return rows

def median_by_scenario(rows, label, gc, metric):
    out = {}
    for scen in SCENARIOS:
        vals = [float(r[metric]) for r in rows
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

def compute(rows, entries, is_stock, metric):
    results = []
    for entry in entries:
        if is_stock:
            name, label, gc = entry
        else:
            name, label, sha, gc = entry
        med = median_by_scenario(rows, label, gc, metric)
        gm = geomean(med.values())
        missing = [s for s in SCENARIOS if s not in med]
        if is_stock:
            results.append((name, label, gc, med, gm, missing))
        else:
            results.append((name, label, sha, med, gm, missing))
    return results

def main():
    rows = load_rows()

    by_metric = {}
    for metric, _, _ in METRICS:
        by_metric[metric] = {
            "milestones": compute(rows, MILESTONES, False, metric),
            "stock": compute(rows, STOCK_REFS, True, metric),
        }

    # ---- write summary.md ----
    lines = []
    lines.append("# Milestone graph — computed geomeans (2026-07-07)\n")
    lines.append("Every row in this file — all ten milestones (the eight from the 2026-07-06 backfill "
                  "plus the two new M8 stages) and all four stock anchors — was benched TODAY "
                  "(2026-07-07) in ONE sitting via git worktree + Release publish + "
                  "`bench-gcperfsim.ps1`, same machine. Cross-sitting wall times are never comparable, "
                  "so nothing here is reused from the 2026-07-06 backfill's numbers even where the "
                  "milestone and sha are unchanged. Geomean = geometric mean of the 4 scenario medians "
                  "(3 iterations each). Two metrics are tracked: wall_s (lower is better) and "
                  "peak_ws_mb (lower is better; the memory companion — a wall-only view flatters "
                  "memory-hungry collectors).\n")

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
    lines.append("- **M6.5 sweep-assist** (found during this 2026-07-07 sitting's SHA verification pass): "
                  "the CSV's `m65s2-final` label records sha `765f680` (\"docs: M6.5 stage 1 results\"), "
                  "which is itself docs-only. Its direct parent `2a62c92` (\"M6.5 stage 1: gated "
                  "concurrent full-cycle sweep\") is a real code commit, but stage 1 ships "
                  "`DOTNET_GCConcurrentSweep` **default-off** — `bench-gcperfsim.ps1` sets no such env "
                  "var, so benching either `765f680` or `2a62c92` would silently measure the old "
                  "in-pause sweep, not the feature the milestone is named for. The \"mutator "
                  "sweep-assist\" work and the default-on flip land one commit later, in `d95dddb` "
                  "(\"M6.5 stage 2: bitmap walks, mutator sweep-assist, concurrent sweep default-on\") "
                  "— same pattern as the M6/M7 corrections above. Backfilled against **`d95dddb`** "
                  "instead.\n")
    lines.append("- **Adaptive nursery** (`ceffbfc`) and **Vectorized bitmap skip** (`68696ff`): both "
                  "verified as code-bearing commits — `git show <sha> --stat` shows "
                  "`ManagedDotnetGC/GCHeap*.cs` / `GCObject.cs` changes, not just docs/results.\n")

    for metric, metric_label, fmt in METRICS:
        milestone_results = by_metric[metric]["milestones"]
        stock_results = by_metric[metric]["stock"]

        lines.append(f"\n## Per-scenario medians ({metric_label}) — milestones\n")
        lines.append("| Milestone | sha used | soh | lohmix | pin | pinheavy | geomean |")
        lines.append("|---|---|---|---|---|---|---|")
        for name, label, sha, med, gm, missing in milestone_results:
            cells = []
            for s in SCENARIOS:
                cells.append(fmt.format(med[s]) if s in med else "—")
            note = f" (missing: {', '.join(missing)})" if missing else ""
            gm_str = fmt.format(gm) if gm is not None else "—"
            lines.append(f"| {name} | `{sha}` | {cells[0]} | {cells[1]} | {cells[2]} | {cells[3]} | "
                          f"**{gm_str}**{note} |")

        lines.append(f"\n## Per-scenario medians ({metric_label}) — stock anchors (today, fresh)\n")
        lines.append("| Config | soh | lohmix | pin | pinheavy | geomean |")
        lines.append("|---|---|---|---|---|---|")
        for name, label, gc, med, gm, missing in stock_results:
            cells = [fmt.format(med[s]) if s in med else "—" for s in SCENARIOS]
            gm_str = fmt.format(gm) if gm is not None else "—"
            lines.append(f"| {name} | {cells[0]} | {cells[1]} | {cells[2]} | {cells[3]} | **{gm_str}** |")

    lines.append("\n## Footnotes\n")
    for name, label, sha, med, gm, missing in by_metric["wall_s"]["milestones"]:
        if missing:
            lines.append(f"- **{name}** (`{sha}`): missing scenario(s) {', '.join(missing)} — "
                          f"geomean computed from the scenarios available.")
    lines.append("- M2 baseline predates the `pinheavy` scenario's existence in the archive's protocol "
                  "(added 2026-07-06), but it ran anyway against the old GC build using the current "
                  "`bench-gcperfsim.ps1`/GCPerfSim — it completed normally, so no scenario is actually "
                  "missing in the final chart.")

    with open(f"{OUT_DIR}\\summary.md", "w", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")

    # ---- print data for the plotting step ----
    for metric, metric_label, fmt in METRICS:
        print(f"=== {metric} ===")
        print("MILESTONES")
        for name, label, sha, med, gm, missing in by_metric[metric]["milestones"]:
            print(name, sha, gm, missing)
        print("STOCK")
        for name, label, gc, med, gm, missing in by_metric[metric]["stock"]:
            print(name, gm)

if __name__ == "__main__":
    main()
