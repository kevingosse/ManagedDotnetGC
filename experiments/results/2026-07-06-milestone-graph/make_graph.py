import csv
import statistics

CSV_PATH = r"E:\git\ManagedDotnetGC\experiments\results\perf-history.csv"
OUT_DIR = r"E:\git\ManagedDotnetGC\experiments\results\2026-07-06-milestone-graph"

# PROTOCOL (2026-07-07, milestone-graph-upkeep): the charts plot the single 'mixed'
# scenario (95% ordinary / 5% pinned allocations, sohpi 20), NOT the 4-scenario
# geomean — pin/pinheavy greatly advantage a non-moving GC and would flatter the
# public artifact. The 5-scenario matrix keeps its separate role as the per-change
# regression control.
SCENARIO = "mixed"

# (name, label, sha, gc-value-to-match)
# 2026-07-07 afternoon sitting: every milestone (ten from the morning backfill plus
# M8.2 partitioned stack scan) and all four stock anchors re-benched fresh in ONE
# sitting with -Scenario mixed via git worktree + Release publish +
# `bench-gcperfsim.ps1` (HEAD copy) — see summary.md.
MILESTONES = [
    ("M2 baseline",     "mixed-m2",        "407cf59", "custom"),
    ("M4 sticky gens",  "mixed-m4",        "5db0ed9", "custom"),
    ("M5 parallel",     "mixed-m5",        "344ce8b", "custom"),
    ("M6 concurrent",   "mixed-m6",        "1aedac9", "custom"),
    ("M7 tuning",       "mixed-m7",        "7689ab1", "custom"),
    # See summary.md SHA notes: M6/M7/M6.5 shas walk forward from docs-only commits
    # to the first commit that actually contains the named feature.
    ("M6.5 sweep-assist","mixed-m65",      "d95dddb", "custom"),
    ("faster allocation (M7 mutator war)", "mixed-fastalloc", "e65fa79", "custom"),
    ("sharded supply (M7)", "mixed-sharded", "98517ee", "custom"),
    ("Adaptive nursery", "mixed-adaptive",  "ceffbfc", "custom"),
    ("Vectorized bitmap skip", "mixed-vecbitmap", "68696ff", "custom"),
    # M8.2 (2026-07-07): partitioned stack scanning across GC workers. Wall-neutral
    # by design (the win is young-pause p50 -25% on web workloads); charted for
    # continuity of the stage axis.
    ("Partitioned stack scan", "mixed-partscan", "8b8bcbd", "custom"),
]

STOCK_REFS = [
    ("Workstation GC",          "mixed-stock-wks",   "stock-wks"),
    ("Server GC (DATAS)",       "mixed-stock-datas", "stock-svr-datas"),
    ("Server GC (8 heaps)",     "mixed-stock-h8",    "stock-svr-h8"),
    ("Server GC (32 heaps)",    "mixed-stock-h32",   "stock-svr-h32"),
]

METRICS = [
    ("wall_s", "wall seconds", "{:.3f}"),
    ("peak_ws_mb", "peak working-set MB", "{:.1f}"),
]

def load_rows():
    with open(CSV_PATH, newline="") as f:
        return list(csv.DictReader(f))

def median_metric(rows, label, gc, metric):
    vals = [float(r[metric]) for r in rows
            if r["label"] == label and r["gc"] == gc and r["scenario"] == SCENARIO]
    return statistics.median(vals) if vals else None

def compute(rows, entries, is_stock, metric):
    results = []
    for entry in entries:
        if is_stock:
            name, label, gc = entry
            key = gc
        else:
            name, label, sha, gc = entry
            key = sha
        med = median_metric(rows, label, gc, metric)
        results.append((name, label, key, med))
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
    lines.append("# Milestone graph — 'mixed' scenario medians (2026-07-07, afternoon sitting)\n")
    lines.append("Every row in this file — all eleven milestones and all four stock anchors — was "
                  "benched 2026-07-07 afternoon in ONE sitting via git worktree + Release publish + "
                  "the HEAD copy of `bench-gcperfsim.ps1 -Scenario mixed`, same machine. Cross-sitting "
                  "wall times are never comparable, so nothing is reused from earlier backfills. "
                  "**Charted metric = median of 3 iterations of the single 'mixed' scenario** (95% "
                  "ordinary / 5% pinned allocations): pin-dedicated scenarios greatly advantage this "
                  "non-moving GC, so a geomean including them would flatter the public artifact "
                  "(protocol change 2026-07-07). Two metrics are tracked: wall_s (lower is better) "
                  "and peak_ws_mb (lower is better; the memory companion — a wall-only view flatters "
                  "memory-hungry collectors).\n")

    lines.append("## SHA notes / corrections (carried from the 2026-07-07 morning backfill)\n")
    lines.append("- M2 baseline, M4 sticky gens, M5 parallel: SHAs as given (407cf59, 5db0ed9, 344ce8b) "
                  "are themselves the code-landing commits — used as-is.\n")
    lines.append("- **M6 concurrent**: the CSV's original `m6s3-fairness` label recorded sha `b889921`, "
                  "a **docs-only** commit that precedes the actual \"M6 stage 3 default-on\" code. "
                  "Backfilled against **`1aedac9`** instead.\n")
    lines.append("- **M7 exchange+quantum**: the original recorded sha `79ce1dc` (docs) predates the "
                  "quantum fix that names the milestone. Backfilled against **`7689ab1`**.\n")
    lines.append("- **M6.5 sweep-assist**: original sha `765f680` is docs-only and its parent ships the "
                  "feature default-OFF; the mutator sweep-assist + default-on land in **`d95dddb`** — "
                  "used instead.\n")
    lines.append("- **Adaptive nursery** (`ceffbfc`), **Vectorized bitmap skip** (`68696ff`), "
                  "**Partitioned stack scan** (`8b8bcbd`): all verified code-bearing commits.\n")
    lines.append("- **Backfill integrity**: each milestone's publish output is timestamp-verified "
                  "before benching (an earlier attempt this sitting silently re-benched a stale dll "
                  "after a failed publish — those rows were purged from the archive).\n")

    for metric, metric_label, fmt in METRICS:
        lines.append(f"\n## 'mixed' medians ({metric_label}) — milestones\n")
        lines.append("| Milestone | sha used | mixed |")
        lines.append("|---|---|---|")
        for name, label, sha, med in by_metric[metric]["milestones"]:
            med_str = fmt.format(med) if med is not None else "—"
            lines.append(f"| {name} | `{sha}` | **{med_str}** |")

        lines.append(f"\n## 'mixed' medians ({metric_label}) — stock anchors (same sitting)\n")
        lines.append("| Config | mixed |")
        lines.append("|---|---|")
        for name, label, gc, med in by_metric[metric]["stock"]:
            med_str = fmt.format(med) if med is not None else "—"
            lines.append(f"| {name} | **{med_str}** |")

    with open(f"{OUT_DIR}\\summary.md", "w", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")

    # ---- print data for the plotting step ----
    for metric, metric_label, fmt in METRICS:
        print(f"=== {metric} ===")
        print("MILESTONES")
        for name, label, sha, med in by_metric[metric]["milestones"]:
            print(f"  {name!r}: {med}")
        print("STOCK")
        for name, label, gc, med in by_metric[metric]["stock"]:
            print(f"  {name!r}: {med}")

if __name__ == "__main__":
    main()
