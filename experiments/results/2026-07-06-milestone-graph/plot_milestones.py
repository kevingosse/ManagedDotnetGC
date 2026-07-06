import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
import matplotlib.font_manager as fm

OUT_PNG = r"E:\git\ManagedDotnetGC\experiments\results\2026-07-06-milestone-graph\milestone-graph.png"

# ---- data (computed by make_graph.py from experiments/results/perf-history.csv) ----
milestones = [
    ("M2 baseline", 7.673),
    ("M4 sticky gens", 5.379),
    ("M5 parallel", 2.104),
    ("M6 concurrent", 1.926),
    ("M7 tuning", 1.794),
    ("M6.5 sweep-assist", 1.438),
]

stock_refs = [
    ("stock WKS", 2.276, "#9a988f"),
    ("stock Server GC", 1.704, "#6b6a63"),
    ("stock Server GC (8 heaps)", 1.347, "#3a3a37"),
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
CLEARANCE = 0.27  # keeps each label clear of its own dashed line (no strikethrough)
# The three values are clustered (2.28 / 1.70 / 1.35). The top two get room above their
# own line; the bottom-most (lowest value) has open space below it down to y=0, so it
# is labeled below instead — this keeps every label comfortably clear of every line
# without needing an iterative stagger.
sorted_refs = sorted(stock_refs, key=lambda r: -r[1])  # WKS, Server GC, Server GC (8 heaps)
directions = ["above", "above", "below"]

for (label, val, color), direction in zip(sorted_refs, directions):
    dy = val + CLEARANCE if direction == "above" else val - CLEARANCE
    ax.axhline(val, color=color, linestyle=(0, (6, 4)), linewidth=1.6, alpha=0.9, zorder=2)
    ax.plot([line_end_x, label_x - 0.05], [val, dy], color=color, linewidth=1.0,
            alpha=0.7, zorder=2, solid_capstyle="round")
    ax.text(label_x, dy, f"{label}  {val:.2f}s",
            color=color, fontsize=11.5, va="center", ha="left", fontweight="bold")

# ---- hero line ----
ax.plot(xs, ys, color=MAIN_COLOR, linewidth=3.2, zorder=4, solid_capstyle="round")
ax.scatter(xs, ys, s=110, color=MAIN_COLOR, zorder=5, edgecolors="white", linewidths=1.6)

# value labels above each point (offset scaled to axis range)
label_offset = (ymax - ymin) * 0.045
for x, y in zip(xs, ys):
    ax.annotate(f"{y:.2f}s", (x, y), xytext=(0, 14), textcoords="offset points",
                ha="center", va="bottom", fontsize=13.5, fontweight="bold", color=INK, zorder=6)

# x tick labels
ax.set_xticks(xs)
ax.set_xticklabels(names, fontsize=12.5, color=SECONDARY_INK)
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
