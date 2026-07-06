import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
import matplotlib.font_manager as fm

OUT_PNG = r"E:\git\ManagedDotnetGC\experiments\results\2026-07-06-milestone-graph\milestone-graph-ws.png"

# ---- data (computed from experiments/results/perf-history.csv: median peak_ws_mb per
# scenario, geomean of the 4 scenario medians, converted to GB by /1024) ----
milestones = [
    ("first working\nallocator", 1.557),
    ("generational", 6.887),
    ("parallel\nmark & sweep", 6.971),
    ("concurrent\nmarking", 6.964),
    ("memory diet\n+ tuning", 2.509),
    ("concurrent\nsweep", 2.434),
]

# (label, line y, explicit label y). DATAS discovery 2026-07-06: bare gcServer=1 is
# adaptive (that is what held 1.64 GB); classic fixed heap-per-core costs 6.18 GB.
stock_refs = [
    ("Server GC (32 heaps)", 6.177, 5.60, "#6b6a63"),
    ("Server GC (8 heaps)", 2.492, 3.10, "#3a3a37"),
    ("Server GC (DATAS)", 1.635, 1.05, "#55544e"),
    ("Workstation GC", 1.579, 0.55, "#9a988f"),
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
ymax = max(ys) * 1.24
ymin = 0
ax.set_ylim(ymin, ymax)

# ---- reference lines (stock configs), drawn first so the hero line sits on top ----
# Unlike the wall-time chart, the hero series is NOT monotonic here: it starts low
# (M2), spikes to ~7 GB across three consecutive milestones (M4/M5/M6 — generational
# through concurrent marking, before the memory diet), then drops back down near the
# stock cluster for the last two points. The three stock references (1.58/1.64/2.49)
# sit close together near the chart floor, right where the hero line starts and ends,
# so labels are staggered above/below their own line (not just "all to one side") to
# stay clear of both each other and the hero value-labels at x=4,5.
label_x = len(milestones) - 1 + 0.35
line_end_x = len(milestones) - 1 + 0.20

for (label, val, dy, color) in stock_refs:
    ax.axhline(val, color=color, linestyle=(0, (6, 4)), linewidth=1.6, alpha=0.9, zorder=2)
    ax.plot([line_end_x, label_x - 0.05], [val, dy], color=color, linewidth=1.0,
            alpha=0.7, zorder=2, solid_capstyle="round")
    ax.text(label_x, dy, f"{label}  {val:.2f} GB",
            color=color, fontsize=11.5, va="center", ha="left", fontweight="bold")

# ---- hero line ----
ax.plot(xs, ys, color=MAIN_COLOR, linewidth=3.2, zorder=4, solid_capstyle="round")
ax.scatter(xs, ys, s=110, color=MAIN_COLOR, zorder=5, edgecolors="white", linewidths=1.6)

# value labels above each point (offset scaled to axis range)
label_offset = (ymax - ymin) * 0.045
for x, y in zip(xs, ys):
    ax.annotate(f"{y:.1f} GB", (x, y), xytext=(0, 14), textcoords="offset points",
                ha="center", va="bottom", fontsize=13.5, fontweight="bold", color=INK, zorder=6)

# x tick labels
ax.set_xticks(xs)
ax.set_xticklabels(names, fontsize=12.5, color=SECONDARY_INK)
ax.tick_params(axis="x", length=0, pad=10)

# y axis
ax.set_ylabel("peak working set, GB (4-scenario geomean, lower is better)", fontsize=12,
              color=SECONDARY_INK, labelpad=10)
ax.tick_params(axis="y", labelsize=11, colors=MUTED, length=0)
for spine in ["top", "right", "left"]:
    ax.spines[spine].set_visible(False)
ax.spines["bottom"].set_color("#c3c2b7")
ax.spines["bottom"].set_linewidth(1)

# light horizontal gridlines only, recessive
ax.yaxis.grid(True, color="#e1e0d9", linewidth=1, zorder=0)
ax.set_axisbelow(True)

# ---- legend for the hero series ----
# Placed in fixed open space near the top-left rather than pinned to the first point's
# height: the first milestone (M2) sits at only 1.56 GB, right in the same band as the
# stock reference lines (1.58-2.49), so anchoring the label there (as the wall chart
# does relative to its own highest point) would run the label text through the
# Workstation/Server(32h) dashed lines, which span the full plot width.
ax.text(-0.4, ymax * 0.99, "ManagedDotnetGC (C#)",
        color=MAIN_COLOR, fontsize=13.5, fontweight="bold", ha="left", va="top")

# ---- title block ----
fig.text(0.07, 0.93, "The same GC's memory: peak working set across milestones",
          fontsize=20, fontweight="bold", color=INK, ha="left", va="top")
fig.text(0.07, 0.875,
          "GCPerfSim, 4-scenario geometric mean of peak working set: soh, lohmix, pin, pinheavy",
          fontsize=12.5, color=SECONDARY_INK, ha="left", va="top")

# ---- small annotation ----
fig.text(0.965, 0.03, "48h of work, 2026-07-05/06", fontsize=10, color=MUTED,
          ha="right", va="bottom", style="italic")

fig.savefig(OUT_PNG, dpi=dpi, facecolor="white")
print("saved", OUT_PNG)
