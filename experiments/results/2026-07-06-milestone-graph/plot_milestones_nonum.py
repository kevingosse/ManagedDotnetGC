import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
import matplotlib.font_manager as fm

OUT_PNG = r"E:\git\ManagedDotnetGC\experiments\results\2026-07-06-milestone-graph\milestone-graph-nonum.png"

# ---- data (recomputed 2026-07-07 sitting: all ten milestones — the eight from the
# 2026-07-06 backfill plus the two new M8 stages — and all four stock anchors
# re-benched fresh in ONE sitting; see
# experiments/results/2026-07-06-milestone-graph/summary.md) ----
milestones = [
    ("first working\nallocator", 7.037),
    ("generational", 5.089),
    ("parallel\nmark & sweep", 1.887),
    ("concurrent\nmarking", 1.826),
    ("memory diet\n+ tuning", 1.743),
    ("concurrent\nsweep", 1.467),
    ("faster\nallocation", 1.366),
    ("sharded\nsupply", 1.162),
    # M8 (2026-07-07 sitting): adaptive young-generation budget (frequent-futile-cheap
    # boost) and SIMD zero-skip in the bitmap walks.
    ("adaptive\nnursery", 1.176),
    ("vectorized\nbitmap skip", 1.167),
]

# (label, line y, explicit label y — slots picked to clear every dashed line and each
# other; DATAS discovery 2026-07-06: bare gcServer=1 adapts heap count, fixed-32 is
# its own config. 2026-07-07 refresh: the four stock values are clustered within 0.9s
# of each other (2.25/1.82/1.75/1.37), and the real gaps between them are too close to
# a safe label height to stagger near their own lines without crossing a *different*
# line — so all four labels stay lifted into the empty band above the whole cluster
# (nothing else sits between ~2.3s and 5.3s here) and stacked in real-value order, each
# clear of every dashed line and of its neighbor label. The last three hero points
# (sharded supply, adaptive nursery, vectorized bitmap skip) now sit BELOW every stock
# reference line, including Server GC (8 heaps) — the tuned custom GC's cluster has
# moved under the whole stock band.
stock_refs = [
    ("Workstation GC", 2.245, 4.25, "#9a988f"),
    ("Server GC (32 heaps)", 1.752, 3.25, "#6b6a63"),
    ("Server GC (DATAS)", 1.818, 3.75, "#55544e"),
    ("Server GC (8 heaps)", 1.365, 2.75, "#3a3a37"),
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
          "GCPerfSim, 4-scenario geometric mean: soh, lohmix, pin, pinheavy",
          fontsize=12.5, color=SECONDARY_INK, ha="left", va="top")

# ---- small annotation ----
fig.text(0.965, 0.03, "72h of work, 2026-07-05/06/07", fontsize=10, color=MUTED,
          ha="right", va="bottom", style="italic")

fig.savefig(OUT_PNG, dpi=dpi, facecolor="white")
print("saved", OUT_PNG)

