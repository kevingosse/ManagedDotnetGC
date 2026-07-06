import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
import matplotlib.font_manager as fm

OUT_PNG = r"E:\git\ManagedDotnetGC\experiments\results\2026-07-06-milestone-graph\milestone-graph.png"

# ---- data (recomputed 2026-07-06 evening sitting: six milestones re-benched as -r2
# labels alongside tonight's new M7 "mutator war" stage (label m7-stash2) so every
# point on the chart shares one sitting; see experiments/results/2026-07-06-milestone-graph/summary.md) ----
milestones = [
    ("M2\nbaseline", 8.054),
    ("M4\nsticky gens", 5.350),
    ("M5 parallel", 2.029),
    ("M6 concurrent", 1.928),
    ("M7 tuning", 1.851),
    ("M6.5\nsweep-assist", 1.816),
    ("faster\nallocation", 1.418),
    # Second sitting of 2026-07-06 (late evening). Drift-checked via re-run anchors:
    # WKS/h8 ran +2.7-2.9% SLOWER than the earlier sitting, so this point is
    # conservative against the reference lines below (same-sitting ratio vs h8: 0.849).
    ("sharded\nsupply", 1.227),
]

stock_refs = [
    ("stock WKS", 2.399, "#9a988f"),
    ("stock Server GC (DATAS)", 2.119, "#55544e"),
    ("stock Server GC (32 heaps)", 1.759, "#6b6a63"),
    ("stock Server GC (8 heaps)", 1.405, "#3a3a37"),
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

fig_w, fig_h, dpi = 12.0, 6.75, 100
fig = plt.figure(figsize=(fig_w, fig_h), dpi=dpi, facecolor="white")
ax = fig.add_axes([0.07, 0.13, 0.78, 0.66])  # leave room for title block + right-edge ref labels
ax.set_facecolor("white")

xs = list(range(len(milestones)))
ys = [m[1] for m in milestones]
names = [m[0] for m in milestones]

# extend xlim so reference-line labels have room on the right without colliding with the
# last point (7th stage, "faster allocation," added 2026-07-06 evening pushed the last
# point one slot right and the 4th stock ref crowds the same 1.4-2.4s band the last few
# hero points sit in, so this needs more right-margin than the old 6-stage/3-ref chart)
ax.set_xlim(-0.4, len(milestones) - 1 + 2.2)
ymax = max(ys) * 1.14
ymin = 0
ax.set_ylim(ymin, ymax)

# ---- reference lines (stock configs), drawn first so the hero line sits on top ----
# Four reference values, clustered within 1.0s of each other (2.40 / 2.12 / 1.76 / 1.41)
# — any near-own-line placement collides with a *different* reference's dashed line,
# since the real gaps between them (0.28 / 0.36 / 0.35) are themselves close to the
# clearance a label needs. So all four labels are lifted into the empty band above the
# whole cluster (nothing else sits between ~2.4s and 5.3s at this x position) and
# stacked in the same order as their real values, each >=0.35s clear of every dashed
# line and >=0.5s clear of its neighbor label.
label_x = len(milestones) - 1 + 0.65
line_end_x = len(milestones) - 1 + 0.45
stock_label_y = {
    "stock WKS": 4.25,
    "stock Server GC (DATAS)": 3.75,
    "stock Server GC (32 heaps)": 3.25,
    "stock Server GC (8 heaps)": 2.75,
}

for label, val, color in stock_refs:
    dy = stock_label_y[label]
    ax.axhline(val, color=color, linestyle=(0, (6, 4)), linewidth=1.6, alpha=0.9, zorder=2)
    ax.plot([line_end_x, label_x - 0.05], [val, dy], color=color, linewidth=1.0,
            alpha=0.7, zorder=2, solid_capstyle="round")
    ax.text(label_x, dy, f"{label}  {val:.2f}s",
            color=color, fontsize=11.5, va="center", ha="left", fontweight="bold")

# ---- hero line ----
ax.plot(xs, ys, color=MAIN_COLOR, linewidth=3.2, zorder=4, solid_capstyle="round")
ax.scatter(xs, ys, s=110, color=MAIN_COLOR, zorder=5, edgecolors="white", linewidths=1.6)

# value labels above each point (offset scaled to axis range). White halo bbox: the
# last two points (M6.5 sweep-assist, faster allocation) sit right where the stock
# Server GC (32 heaps)/(8 heaps) dashed lines cross the plot, so a plain text label
# would get a strikethrough from the dashed line running behind it.
label_offset = (ymax - ymin) * 0.045
for x, y in zip(xs, ys):
    ax.annotate(f"{y:.2f}s", (x, y), xytext=(0, 14), textcoords="offset points",
                ha="center", va="bottom", fontsize=13.5, fontweight="bold", color=INK, zorder=6,
                bbox=dict(facecolor="white", edgecolor="none", pad=1.5))

# x tick labels (fontsize trimmed slightly from 12.5: 7 categories now share the same
# width 6 used to, and two labels wrap to two lines to avoid crowding their neighbors)
ax.set_xticks(xs)
ax.set_xticklabels(names, fontsize=11.3, color=SECONDARY_INK)
ax.tick_params(axis="x", length=0, pad=10)

# y axis
ax.set_ylabel("wall seconds (4-scenario geomean, lower is better)", fontsize=12, color=SECONDARY_INK, labelpad=10)
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
          "GCPerfSim, 4-scenario geometric mean — all builds re-benchmarked same day, same machine, "
          "vs same-day stock anchors",
          fontsize=12.5, color=SECONDARY_INK, ha="left", va="top")

# ---- small annotation ----
fig.text(0.965, 0.03, "48h of work, 2026-07-05/06", fontsize=10, color=MUTED,
          ha="right", va="bottom", style="italic")

fig.savefig(OUT_PNG, dpi=dpi, facecolor="white")
print("saved", OUT_PNG)
