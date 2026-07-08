import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
import matplotlib.font_manager as fm

OUT_PNG = r"E:\git\ManagedDotnetGC\experiments\results\2026-07-07-fortunes-milestone-graph\fortunes-rps.png"

# ---- data (2026-07-07 backfill sitting: all thirteen milestones and all four stock
# anchors benched in ONE sitting with `bench-techempower.ps1 -Endpoint fortunes` —
# median rps of 3x15 s bombardier runs at 256 connections; see summary.md.
# Style matches plot_milestones_nonum.py; axis direction adapted: RPS is
# higher-is-better, so the hero line CLIMBS toward the stock band instead of
# dropping under it.) ----
milestones = [
    ("first working\nallocator", 25972),
    # sticky-generations build times out under Kestrel load (thousands of bombardier
    # timeouts per run, confirmed by a retry) — the 13.8k median is its real behavior,
    # not a bad run; see summary.md.
    ("generational", 13774),
    ("parallel\nmark & sweep", 31016),
    ("concurrent\nmarking", 29143),
    ("memory diet\n+ tuning", 30809),
    ("concurrent\nsweep", 37840),
    ("faster\nallocation", 40248),
    ("sharded\nsupply", 39315),
    # M8 adaptive nursery: the single biggest web-workload jump of the series
    # (+79% — the frequent-futile-cheap boost was built FOR this shape).
    ("adaptive\nnursery", 70199),
    ("vectorized\nbitmap skip", 70007),
    ("partitioned\nstack scan", 72137),
    ("zeroed-hole\nmarkers", 71749),
    # M9 slow-path bump-serve: kills the 20x carve inflation that drove
    # suspension frequency — first stage past Workstation GC and Server GC (32 heaps).
    ("slow-path\nbump-serve", 80595),
    # M9.1 boost cap 192 MB (2026-07-07, separate same-day sitting, sha 70be506,
    # label m9r-cap192 in techempower-history.csv): residual-gap profile found the
    # 448 MB boost bought no RPS, so it's capped. This point and its own stock-h8
    # anchor (83,798 — NOT the 88,057 cluster above, a different sitting) come from
    # that later sitting; rps is plotted on the hero line as reported (80,408) rather
    # than re-normalized against the earlier cluster.
    ("tuning", 80408),
]

# (label, line y, explicit label y). The four stock anchors are clustered within
# 11.3k of each other (88.1/86.7/80.3/76.8) at the very top of the hero line's
# range — the same too-close-to-stagger situation as the wall chart, so all four
# labels are lifted into the empty band ABOVE the whole cluster (nothing sits above
# 88.1k) and stacked in real-value order, each clear of every dashed line and of
# its neighbor label.
stock_refs = [
    ("Server GC (8 heaps)", 88057, 106000, "#3a3a37"),
    #("Server GC (DATAS)", 86736, 101000, "#55544e"),
    ("Server GC (32 heaps)", 80309, 96000, "#6b6a63"),
    ("Workstation GC", 76822, 91000, "#9a988f"),
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
ax = fig.add_axes([0.055, 0.13, 0.89, 0.66])  # room for title block + right-edge ref labels
ax.set_facecolor("white")

xs = list(range(len(milestones)))
ys = [m[1] for m in milestones]
names = [m[0] for m in milestones]

ax.set_xlim(-0.4, len(milestones) - 1 + 1.9)
ymax = 112000  # headroom above the stock cluster for the four stacked labels
ymin = 0
ax.set_ylim(ymin, ymax)

# ---- reference lines (stock configs), drawn first so the hero line sits on top ----
label_x = len(milestones) - 1 + 0.35
line_end_x = len(milestones) - 1 + 0.20

for label, val, label_y, color in stock_refs:
    ax.axhline(val, color=color, linestyle=(0, (6, 4)), linewidth=1.6, alpha=0.9, zorder=2)
    ax.plot([line_end_x, label_x - 0.05], [val, label_y], color=color, linewidth=1.0,
            alpha=0.7, zorder=2, solid_capstyle="round")
    ax.text(label_x, label_y, f"{label}  {val/1000:.1f}k",
            color=color, fontsize=11.5, va="center", ha="left", fontweight="bold")

# ---- hero line ----
ax.plot(xs, ys, color=MAIN_COLOR, linewidth=3.2, zorder=4, solid_capstyle="round")
ax.scatter(xs, ys, s=110, color=MAIN_COLOR, zorder=5, edgecolors="white", linewidths=1.6)

# value labels above each point. White halo bbox: several hero points (80.6k, 80.4k)
# sit right at the Server GC (32 heaps) dashed line, so a plain label would get a
# strikethrough from the dashed line running behind it. The last two points (M9
# 80.6k, M9.1 80.4k) are one x-unit apart at nearly the same height, so an "above"
# label on both collides — M9.1's label drops BELOW its point instead, into the
# wide-open band between the hero tail and the M8-stage points.
label_offset = (ymax - ymin) * 0.045
last_x = xs[-1]
for x, y in zip(xs, ys):
    if x == last_x:
        ax.annotate(f"{y/1000:.1f}k", (x, y), xytext=(0, -18), textcoords="offset points",
                    ha="center", va="top", fontsize=13.5, fontweight="bold", color=INK, zorder=6,
                    bbox=dict(facecolor="white", edgecolor="none", pad=1.5))
    else:
        ax.annotate(f"{y/1000:.1f}k", (x, y), xytext=(0, 14), textcoords="offset points",
                    ha="center", va="bottom", fontsize=13.5, fontweight="bold", color=INK, zorder=6,
                    bbox=dict(facecolor="white", edgecolor="none", pad=1.5))

# x tick labels
ax.set_xticks(xs)
ax.set_xticklabels(names, fontsize=10.8, color=SECONDARY_INK)
ax.tick_params(axis="x", length=0, pad=10)

# y axis
ax.set_ylabel("requests per second (higher is better)", fontsize=12, color=SECONDARY_INK, labelpad=10)
ax.tick_params(axis="y", labelsize=11, colors=MUTED, length=0)
for spine in ["top", "right", "left"]:
    ax.spines[spine].set_visible(False)
ax.spines["bottom"].set_color("#c3c2b7")
ax.spines["bottom"].set_linewidth(1)

ax.yaxis.grid(True, color="#e1e0d9", linewidth=1, zorder=0)
ax.set_axisbelow(True)

# ---- legend for the hero series ----
# Fixed open space near the top-left rather than pinned to the first point's height:
# the hero line starts at 26k with its own value label right there, and anchoring the
# series name to it (as the wall chart does) would collide with the stage-3 "31.0k"
# label. Everything above the stock cluster (>88k) is empty at the left edge.
ax.text(-0.4, ymax * 0.99, "ManagedDotnetGC (C#)",
        color=MAIN_COLOR, fontsize=13.5, fontweight="bold", ha="left", va="top")

# ---- title block ----
fig.text(0.07, 0.93, "A .NET GC written in C# - Throughput",
          fontsize=20, fontweight="bold", color=INK, ha="left", va="top")
fig.text(0.07, 0.875,
          "TechEmpower Fortunes (ASP.NET MVC + EF Core, PostgreSQL) — 256 connections, "
          "median of 3×15 s runs",
          fontsize=12.5, color=SECONDARY_INK, ha="left", va="top")

fig.savefig(OUT_PNG, dpi=dpi, facecolor="white")
print("saved", OUT_PNG)
