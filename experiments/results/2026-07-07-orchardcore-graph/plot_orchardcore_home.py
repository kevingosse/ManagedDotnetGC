import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
import matplotlib.font_manager as fm

OUT_PNG = r"E:\git\ManagedDotnetGC\experiments\results\2026-07-07-orchardcore-graph\orchardcore-home.png"

# ---- data (medians, blog homepage, 128 connections, same-sitting 2026-07-07 late
# evening; GC dll = M9.2/edd5b82 (probe controller); source rows in
# experiments/results/orchard-history.csv, labels m9.2-probe, m9.2-probe-b2,
# m9.1-old-ab, m9.2-anchor-h8, m9.2-anchor-datas, endpoint=home. See
# experiments/results/2026-07-07-m9.2-probe-controller.md for the full analysis.
# Style matches plot_fortunes_rps.py / plot_fortunes_ws.py in
# ../2026-07-07-fortunes-milestone-graph/.) ----
configs = [
    ("Workstation GC\n(stock)", 1694, 222, 400, "#9a988f"),
    ("ManagedDotnetGC\n(custom, M9.2)", 5266, 71, 1250, "#2a78d6"),
    ("Server GC\n(8 heaps, stock)", 4992, 172, 930, "#3a3a37"),
    ("Server GC\n(DATAS, stock)", 5482, 62, 460, "#55544e"),
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

fig_w, fig_h, dpi = 13.5, 6.75, 100
fig = plt.figure(figsize=(fig_w, fig_h), dpi=dpi, facecolor="white")

ax_rps = fig.add_axes([0.065, 0.15, 0.40, 0.62])
ax_p99 = fig.add_axes([0.575, 0.15, 0.40, 0.62])
for ax in (ax_rps, ax_p99):
    ax.set_facecolor("white")

names = [c[0] for c in configs]
rps = [c[1] for c in configs]
p99 = [c[2] for c in configs]
colors = [c[4] for c in configs]
xs = list(range(len(configs)))

# ---- left panel: throughput (higher is better) ----
bars_rps = ax_rps.bar(xs, rps, width=0.58, color=colors, zorder=3,
                       edgecolor="white", linewidth=1.2)
ax_rps.set_ylim(0, max(rps) * 1.22)
for x, y in zip(xs, rps):
    ax_rps.annotate(f"{y:,}", (x, y), xytext=(0, 8), textcoords="offset points",
                     ha="center", va="bottom", fontsize=15, fontweight="bold", color=INK, zorder=6)
ax_rps.set_title("Throughput (rps, higher is better)", fontsize=13.5, color=INK,
                  fontweight="bold", pad=14)
ax_rps.set_ylabel("requests per second", fontsize=11.5, color=SECONDARY_INK, labelpad=8)

# ---- right panel: p99 latency (lower is better) ----
bars_p99 = ax_p99.bar(xs, p99, width=0.58, color=colors, zorder=3,
                       edgecolor="white", linewidth=1.2)
ax_p99.set_ylim(0, max(p99) * 1.22)
for x, y in zip(xs, p99):
    ax_p99.annotate(f"{y} ms", (x, y), xytext=(0, 8), textcoords="offset points",
                     ha="center", va="bottom", fontsize=15, fontweight="bold", color=INK, zorder=6)
ax_p99.set_title("p99 latency (ms, lower is better)", fontsize=13.5, color=INK,
                  fontweight="bold", pad=14)
ax_p99.set_ylabel("milliseconds", fontsize=11.5, color=SECONDARY_INK, labelpad=8)

# shared x styling
for ax in (ax_rps, ax_p99):
    ax.set_xticks(xs)
    ax.set_xticklabels(names, fontsize=11.5, color=SECONDARY_INK)
    ax.tick_params(axis="x", length=0, pad=10)
    ax.tick_params(axis="y", labelsize=10.5, colors=MUTED, length=0)
    for spine in ["top", "right", "left"]:
        ax.spines[spine].set_visible(False)
    ax.spines["bottom"].set_color("#c3c2b7")
    ax.spines["bottom"].set_linewidth(1)
    ax.yaxis.grid(True, color="#e1e0d9", linewidth=1, zorder=0)
    ax.set_axisbelow(True)

# highlight the custom bar's tick label to match the fortunes charts' hero-series color
for ax in (ax_rps, ax_p99):
    ax.get_xticklabels()[1].set_color(MAIN_COLOR)
    ax.get_xticklabels()[1].set_fontweight("bold")

# ---- title block ----
fig.text(0.065, 0.94, "OrchardCore 3.0 blog homepage — custom GC vs stock (2026-07-07)",
          fontsize=20, fontweight="bold", color=INK, ha="left", va="top")
fig.text(0.065, 0.885,
          "OrchardCore CMS 3.0 (net10), Blog recipe on SQLite, full CMS middleware + Razor + "
          "YesSql per request — 128 connections, blog homepage (/)",
          fontsize=12, color=SECONDARY_INK, ha="left", va="top")

# ---- footnote: peak working set + methodology (two lines to fit the fig width) ----
fig.text(0.065, 0.02,
          "Peak working set: custom ~1250 MB, Server GC (8 heaps) 930 MB, Workstation GC 400 MB, "
          "Server GC (DATAS) 460 MB.\n"
          "Medians of same-sitting iterations, 128 conns. Custom is M9.2/edd5b82 (probe "
          "controller); first real-app result above pinned stock-h8 (1.055x). Workstation GC "
          "bar carried over from the morning sitting.",
          fontsize=10, color=MUTED, ha="left", va="bottom", style="italic")

fig.savefig(OUT_PNG, dpi=dpi, facecolor="white")
print("saved", OUT_PNG)
