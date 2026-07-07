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
# 2026-07-07 backfill sitting: all thirteen milestones (eleven carried forward plus
# the two new M8.3/M9 stages) and all four stock anchors re-benched fresh in ONE
# sitting with -Scenario mixed via git worktree + Release publish +
# `bench-gcperfsim.ps1` (HEAD copy) — see summary.md. Labels: msg3-<N>.
MILESTONES = [
    ("M2 baseline",     "msg3-1",  "407cf59", "custom"),
    ("M4 sticky gens",  "msg3-2",  "5db0ed9", "custom"),
    ("M5 parallel",     "msg3-3",  "344ce8b", "custom"),
    ("M6 concurrent",   "msg3-4",  "1aedac9", "custom"),
    ("M7 tuning",       "msg3-5",  "7689ab1", "custom"),
    # See summary.md SHA notes: M6/M7/M6.5 shas walk forward from docs-only commits
    # to the first commit that actually contains the named feature.
    ("M6.5 sweep-assist","msg3-6",  "d95dddb", "custom"),
    ("faster allocation (M7 mutator war)", "msg3-7", "e65fa79", "custom"),
    ("sharded supply (M7)", "msg3-8", "98517ee", "custom"),
    ("Adaptive nursery", "msg3-9",  "ceffbfc", "custom"),
    ("Vectorized bitmap skip", "msg3-10", "68696ff", "custom"),
    # M8.2 (2026-07-07): partitioned stack scanning across GC workers. Wall-neutral
    # by design (the win is young-pause p50 -25% on web workloads); charted for
    # continuity of the stage axis.
    ("Partitioned stack scan", "msg3-11", "8b8bcbd", "custom"),
    # M8.3 (2026-07-07): per-hole zeroed markers kill the sweep's zeroer-work forfeit.
    ("Zeroed-hole markers", "msg3-12", "8868c22", "custom"),
    # M9 (2026-07-07): slow-path bump-serve — 20x carve inflation was the
    # suspension-frequency mechanism.
    ("Slow-path bump-serve", "msg3-13", "6b6b106", "custom"),
]

STOCK_REFS = [
    ("Workstation GC",          "msg3-stock-wks",   "stock-wks"),
    ("Server GC (DATAS)",       "msg3-stock-svr",   "stock-svr-datas"),
    ("Server GC (8 heaps)",     "msg3-stock-h8",    "stock-svr-h8"),
    ("Server GC (32 heaps)",    "msg3-stock-h32",   "stock-svr-h32"),
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
    lines.append("# Milestone graph — 'mixed' scenario medians (2026-07-07, backfill sitting)\n")
    lines.append("Every row in this file — all thirteen milestones and all four stock anchors — was "
                  "benched 2026-07-07 in ONE sitting via git worktree + Release publish + "
                  "the HEAD copy of `bench-gcperfsim.ps1 -Scenario mixed`, same machine. Cross-sitting "
                  "wall times are never comparable, so nothing is reused from earlier backfills — this "
                  "sitting supersedes the 2026-07-07 afternoon sitting's numbers wholesale, including "
                  "the eleven milestones it repeats. "
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
    lines.append("- **Zeroed-hole markers** (`8868c22`, M8.3) and **Slow-path bump-serve** (`6b6b106`, "
                  "M9) — new rows added this sitting: both are themselves the code-landing commits, "
                  "pre-verified per the backfill brief, used as-is.\n")
    lines.append("- **Backfill integrity**: each milestone's publish output is timestamp-verified "
                  "before benching (an earlier attempt silently re-benched a stale dll "
                  "after a failed publish — those rows were purged from the archive). This sitting's "
                  "stale-dll check flagged identical 1.194 s wall medians at Vectorized bitmap skip and "
                  "Partitioned stack scan; the raw iteration sets differ entirely "
                  "(1.175/1.194/1.269 vs 1.150/1.280/1.194, distinct GC counts and sim_s), so it is a "
                  "genuine coincidence of overlapping medians, not a stale build.\n")

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
