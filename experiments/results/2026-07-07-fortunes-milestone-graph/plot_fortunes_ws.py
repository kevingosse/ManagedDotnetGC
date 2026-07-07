import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
import matplotlib.font_manager as fm

OUT_PNG = r"E:\git\ManagedDotnetGC\experiments\results\2026-07-07-fortunes-milestone-graph\fortunes-ws.png"

# ---- data (2026-07-07 backfill sitting, same runs as fortunes-rps.png: median
# peak working set (MB) of the app process across the 3x15 s bombardier runs;
# see summary.md. This chart MUST ship alongside the RPS one — a GC that never
# collects would "win" RPS on memory it never gives back.) ----
milestones = [
    ("first working\nallocator", 273),
    ("generational", 568),
    ("parallel\nmark & sweep", 585),
    ("concurrent\nmarking", 884),
    ("memory diet\n+ tuning", 721),
    ("concurrent\nsweep", 762),
    ("faster\nallocation", 745),
    ("sharded\nsupply", 836),
    # M8 adaptive nursery: buys its +79% RPS with a bigger young budget — the
    # working-set peak of the series (1.6 GB).
    ("adaptive\nnursery", 1611),
    ("vectorized\nbitmap skip", 1214),
    ("partitioned\nstack scan", 1262),
    ("zeroed-hole\nmarkers", 1198),
    # M9 slow-path bump-serve claws ~270 MB of that back while ALSO gaining RPS.
    ("slow-path\nbump-serve", 990),
]

# (label, line y, explicit label y). Unlike the wall/RPS charts the four anchors are
# NOT clustered: wks (197) and DATAS (280) hug the floor, h8 (726) runs through the
# middle of the hero band (721-836), h32 (2108) tops the chart. Labels are placed
# individually: wks below its line, DATAS above its line (both clear of the hero
# start at 273), h8 lifted above the hero band into the 1080-1150 gap, h32 above
# its own line in open space.
stock_refs = [
    ("Server GC (32 heaps)", 2108, 2320, "#6b6a63"),
    # h8's label lifted to 1550: the 1080-1200 band collides with the last hero
    # point's "990 MB" annotation, and 1550 sits in open space clear of both the
    # hero tail and the h32 label above it.
    ("Server GC (8 heaps)", 726, 1550, "#3a3a37"),
    ("Server GC (DATAS)", 280, 430, "#55544e"),
    ("Workstation GC", 197, 60, "#9a988f"),
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
ax = fig.add_axes([0.055, 0.13, 0.82, 0.66])  # room for title block + right-edge ref labels
ax.set_facecolor("white")

xs = list(range(len(milestones)))
ys = [m[1] for m in milestones]
names = [m[0] for m in milestones]

ax.set_xlim(-0.4, len(milestones) - 1 + 1.9)
ymax = 2108 * 1.24
ymin = 0
ax.set_ylim(ymin, ymax)

# ---- reference lines (stock configs), drawn first so the hero line sits on top ----
label_x = len(milestones) - 1 + 0.35
line_end_x = len(milestones) - 1 + 0.20

for label, val, dy, color in stock_refs:
    ax.axhline(val, color=color, linestyle=(0, (6, 4)), linewidth=1.6, alpha=0.9, zorder=2)
    ax.plot([line_end_x, label_x - 0.05], [val, dy], color=color, linewidth=1.0,
            alpha=0.7, zorder=2, solid_capstyle="round")
    ax.text(label_x, dy, f"{label}  {val} MB",
            color=color, fontsize=11.5, va="center", ha="left", fontweight="bold")

# ---- hero line ----
ax.plot(xs, ys, color=MAIN_COLOR, linewidth=3.2, zorder=4, solid_capstyle="round")
ax.scatter(xs, ys, s=110, color=MAIN_COLOR, zorder=5, edgecolors="white", linewidths=1.6)

# value labels above each point. White halo bbox: the middle hero points (721-836 MB)
# sit right at the Server GC (8 heaps) dashed line, so a plain label would get a
# strikethrough from the dashed line running behind it.
label_offset = (ymax - ymin) * 0.045
for x, y in zip(xs, ys):
    ax.annotate(f"{y} MB", (x, y), xytext=(0, 14), textcoords="offset points",
                ha="center", va="bottom", fontsize=13.5, fontweight="bold", color=INK, zorder=6,
                bbox=dict(facecolor="white", edgecolor="none", pad=1.5))

# x tick labels
ax.set_xticks(xs)
ax.set_xticklabels(names, fontsize=10.8, color=SECONDARY_INK)
ax.tick_params(axis="x", length=0, pad=10)

# y axis
ax.set_ylabel("peak working set, MB (lower is better)", fontsize=12,
              color=SECONDARY_INK, labelpad=10)
ax.tick_params(axis="y", labelsize=11, colors=MUTED, length=0)
for spine in ["top", "right", "left"]:
    ax.spines[spine].set_visible(False)
ax.spines["bottom"].set_color("#c3c2b7")
ax.spines["bottom"].set_linewidth(1)

ax.yaxis.grid(True, color="#e1e0d9", linewidth=1, zorder=0)
ax.set_axisbelow(True)

# ---- legend for the hero series ----
# Fixed open space near the top-left (the first hero point starts low, at 273 MB,
# in the same band as the wks/DATAS reference lines).
ax.text(-0.4, ymax * 0.99, "ManagedDotnetGC (C#)",
        color=MAIN_COLOR, fontsize=13.5, fontweight="bold", ha="left", va="top")

# ---- title block ----
fig.text(0.07, 0.93, "The same GC's memory: web-workload working set across milestones",
          fontsize=20, fontweight="bold", color=INK, ha="left", va="top")
fig.text(0.07, 0.875,
          "TechEmpower Fortunes (ASP.NET MVC + EF Core, PostgreSQL) — 256 connections, "
          "median of 3×15 s runs, all builds + stock anchors benched in one sitting",
          fontsize=12.5, color=SECONDARY_INK, ha="left", va="top")

# ---- small annotation ----
fig.text(0.965, 0.03, "72h of work, 2026-07-05/06/07", fontsize=10, color=MUTED,
          ha="right", va="bottom", style="italic")

fig.savefig(OUT_PNG, dpi=dpi, facecolor="white")
print("saved", OUT_PNG)
