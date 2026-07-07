import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
import matplotlib.font_manager as fm

OUT_PNG = r"E:\git\ManagedDotnetGC\experiments\results\2026-07-06-milestone-graph\milestone-graph-nonum.png"

# ---- data (recomputed 2026-07-07 BACKFILL sitting under the mixed-only protocol:
# all thirteen milestones and all four stock anchors re-benched fresh in ONE sitting
# with `bench-gcperfsim.ps1 -Scenario mixed`; see summary.md. Charted metric is the
# single 'mixed' scenario (95% ordinary / 5% pinned allocations) — pin-dedicated
# scenarios greatly advantage a non-moving GC, so a geomean including them would
# flatter this public artifact.) ----
milestones = [
    ("first working\nallocator", 7.311),
    ("generational", 5.289),
    ("parallel\nmark & sweep", 1.801),
    ("concurrent\nmarking", 1.870),
    ("memory diet\n+ tuning", 1.801),
    ("concurrent\nsweep", 1.400),
    ("faster\nallocation", 1.421),
    ("sharded\nsupply", 1.223),
    # M8 (2026-07-07): adaptive young-generation budget (frequent-futile-cheap
    # boost) and SIMD zero-skip in the bitmap walks.
    ("adaptive\nnursery", 1.181),
    ("vectorized\nbitmap skip", 1.194),
    # M8.2 (2026-07-07): partitioned stack scanning — a young-pause milestone
    # (roots p50 −70%, pause p50 −25% on web workloads); wall-neutral here.
    # (Identical 1.194 median vs the previous stage is a checked coincidence:
    # the raw iteration sets differ entirely — see summary.md.)
    ("partitioned\nstack scan", 1.194),
    # M8.3 (2026-07-07): per-hole zeroed markers kill the sweep's zeroer-work
    # forfeit; wall-neutral on mixed (the win is on the web workload).
    ("zeroed-hole\nmarkers", 1.234),
    # M9 (2026-07-07): slow-path bump-serve — 20x carve inflation was the
    # suspension-frequency mechanism; best mixed wall of the series.
    ("slow-path\nbump-serve", 1.104),
    # M9.1 (2026-07-07, sha 70be506, label m9r-cap192-smoke): boost cap 448->192 MB
    # — the web-workload residual-gap profile found the larger boost bought no rps,
    # so it's capped; wall-neutral here as expected (smoke sitting: iters 1.158,
    # 1.164, 1.228 — median 1.164).
    ("boost cap\n192 MB", 1.164),
]

# (label, line y, explicit label y — slots picked to clear every dashed line and each
# other; DATAS discovery 2026-07-06: bare gcServer=1 adapts heap count, fixed-32 is
# its own config. 2026-07-07 backfill sitting: the four stock values are clustered
# within 1.3s of each other (2.60/1.97/1.83/1.34) — note h32 (1.97) now sits ABOVE
# DATAS (1.83), the reverse of the afternoon sitting; label stack re-ordered to keep
# real-value order. The real gaps between them are too close to a safe label height
# to stagger near their own lines without crossing a *different* line — so all four
# labels stay lifted into the empty band above the whole cluster (nothing else sits
# between ~2.6s and 5.3s here) and stacked in real-value order, each clear of every
# dashed line and of its neighbor label. The last six hero points (sharded supply
# onward) sit BELOW every stock reference line, including Server GC (8 heaps).
stock_refs = [
    ("Workstation GC", 2.595, 4.55, "#9a988f"),
    ("Server GC (32 heaps)", 1.970, 4.05, "#6b6a63"),
    ("Server GC (DATAS)", 1.827, 3.55, "#55544e"),
    ("Server GC (8 heaps)", 1.338, 3.05, "#3a3a37"),
]

MAIN_COLOR = "#2a78d6"
INK = "#0b0b0b"
SECONDARY_INK = "#52514e"
MUTED = "#898781"

# ---- font: prefer Segoe UI (Windows system sans) ----
preferred = ["Segoe UI", "Arial", "DejaVu Sans"]
available = {f.name for f in fm.fontManager.ttflist}
font_family = next((f for f in preferred if f in available), "DejaVu Sans")
plt.rcParams["font.family"] = font_family

fig_w, fig_h, dpi = 15.0, 6.75, 100
fig = plt.figure(figsize=(fig_w, fig_h), dpi=dpi, facecolor="white")
ax = fig.add_axes([0.055, 0.13, 0.82, 0.66])  # leave room for title block + right-edge ref labels
ax.set_facecolor("white")

xs = list(range(len(milestones)))
ys = [m[1] for m in milestones]
names = [m[0] for m in milestones]

# extend xlim so reference-line labels have room on the right without colliding with the last point
ax.set_xlim(-0.4, len(milestones) - 1 + 1.9)
ymax = max(ys) * 1.14
ymin = 0
ax.set_ylim(ymin, ymax)

# ---- reference lines (stock configs), drawn first so the hero line sits on top ----
# The three reference values are clustered (2.28 / 1.70 / 1.35) — placing each label at
# its exact y would overlap. Stagger label y-positions with a minimum gap and connect
# each to its true line with a short leader.
label_x = len(milestones) - 1 + 0.35
line_end_x = len(milestones) - 1 + 0.20

for label, val, label_y, color in stock_refs:
    ax.axhline(val, color=color, linestyle=(0, (6, 4)), linewidth=1.6, alpha=0.9, zorder=2)
    ax.plot([line_end_x, label_x - 0.05], [val, label_y], color=color, linewidth=1.0,
            alpha=0.7, zorder=2, solid_capstyle="round")
    ax.text(label_x, label_y, f"{label}  {val:.2f}s",
            color=color, fontsize=11.5, va="center", ha="left", fontweight="bold")

# ---- hero line ----
ax.plot(xs, ys, color=MAIN_COLOR, linewidth=3.2, zorder=4, solid_capstyle="round")
ax.scatter(xs, ys, s=110, color=MAIN_COLOR, zorder=5, edgecolors="white", linewidths=1.6)

# value labels above each point (offset scaled to axis range). White halo bbox: the
# last two points (concurrent sweep, faster allocation) sit right where the Server GC
# (32 heaps)/(8 heaps) dashed lines cross the plot, so a plain text label would get a
# strikethrough from the dashed line running behind it.
label_offset = (ymax - ymin) * 0.045
for x, y in zip(xs, ys):
    ax.annotate(f"{y:.2f}s", (x, y), xytext=(0, 14), textcoords="offset points",
                ha="center", va="bottom", fontsize=13.5, fontweight="bold", color=INK, zorder=6,
                bbox=dict(facecolor="white", edgecolor="none", pad=1.5))

# x tick labels
ax.set_xticks(xs)
ax.set_xticklabels(names, fontsize=10.8, color=SECONDARY_INK)
ax.tick_params(axis="x", length=0, pad=10)

# y axis
ax.set_ylabel("wall seconds (mixed scenario, lower is better)", fontsize=12, color=SECONDARY_INK, labelpad=10)
ax.tick_params(axis="y", labelsize=11, colors=MUTED, length=0)
for spine in ["top", "right", "left"]:
    ax.spines[spine].set_visible(False)
ax.spines["bottom"].set_color("#c3c2b7")
ax.spines["bottom"].set_linewidth(1)

# light horizontal gridlines only, recessive
ax.yaxis.grid(True, color="#e1e0d9", linewidth=1, zorder=0)
ax.set_axisbelow(True)

# ---- legend for the hero series (single-series: name it via a small inline label instead of a box) ----
ax.text(-0.4, ys[0] + label_offset + (ymax - ymin) * 0.05, "ManagedDotnetGC (C#)",
        color=MAIN_COLOR, fontsize=13.5, fontweight="bold", ha="left", va="bottom")

# ---- title block ----
fig.text(0.07, 0.93, "A .NET GC written in C#: wall time across milestones",
          fontsize=20, fontweight="bold", color=INK, ha="left", va="top")
fig.text(0.07, 0.875,
          "GCPerfSim, mixed workload: 95% ordinary / 5% pinned allocations — "
          "all builds re-benchmarked same sitting, same machine, vs same-sitting stock anchors",
          fontsize=12.5, color=SECONDARY_INK, ha="left", va="top")

# ---- small annotation ----
fig.text(0.965, 0.03, "72h of work, 2026-07-05/06/07", fontsize=10, color=MUTED,
          ha="right", va="bottom", style="italic")

fig.savefig(OUT_PNG, dpi=dpi, facecolor="white")
print("saved", OUT_PNG)

