"""Generates assets/curve.png — the dark, Spotify-green hero chart for the README.

The story in one picture: Spotify's stock curve is top-heavy (p>1, red), so the
bottom half of the slider does almost nothing. A power curve with p<1 (green) makes
the whole travel usable. Run:  python3 curve_compare.py
"""
import os
import numpy as np
import matplotlib.pyplot as plt
from matplotlib import font_manager

BG      = "#0d1117"   # GitHub dark
PANEL   = "#0d1117"
GRID    = "#262b33"
TEXT    = "#e6edf3"
MUTED   = "#8b949e"
GREEN   = "#1ed760"   # Spotify green — the hero
BLUE    = "#58a6ff"
RED     = "#f85149"

plt.rcParams.update({
    "figure.facecolor": BG, "axes.facecolor": PANEL,
    "text.color": TEXT, "axes.labelcolor": TEXT,
    "xtick.color": MUTED, "ytick.color": MUTED,
    "axes.edgecolor": GRID, "font.size": 12,
})

x = np.linspace(0, 1, 600)
fig, ax = plt.subplots(figsize=(10, 5.6), dpi=160)

# region the power curve unlocks vs. plain linear
ax.fill_between(x * 100, (x ** 0.35) * 100, x * 100, color=GREEN, alpha=0.07, zorder=1)

# stock-style top-heavy curve (the problem)
ax.plot(x * 100, (x ** 2.0) * 100, color=RED, ls=(0, (5, 4)), lw=2.0,
        label="p = 2.0   top-heavy  (Spotify-style — what we fix)", zorder=2)
# linear reference
ax.plot(x * 100, x * 100, color=MUTED, lw=1.6, label="p = 1.0   linear", zorder=2)
# balanced
ax.plot(x * 100, (x ** 0.5) * 100, color=BLUE, lw=2.6, label="p = 0.5   balanced", zorder=3)
# hero curve with a soft glow
for w, a in [(11, 0.05), (7, 0.08), (4.2, 0.16)]:
    ax.plot(x * 100, (x ** 0.35) * 100, color=GREEN, lw=w, alpha=a, solid_capstyle="round", zorder=3)
ax.plot(x * 100, (x ** 0.35) * 100, color=GREEN, lw=3.0, solid_capstyle="round",
        label="p = 0.35  strong  ★ recommended", zorder=4)

# markers at slider = 50%
ax.axvline(50, color=GRID, lw=1.0, zorder=1)
for p, c in [(2.0, RED), (1.0, MUTED), (0.5, BLUE), (0.35, GREEN)]:
    yv = (0.5 ** p) * 100
    ax.plot(50, yv, "o", color=c, ms=7, zorder=5,
            markeredgecolor=BG, markeredgewidth=1.5)
    ax.annotate(f"{yv:.0f}%", (50, yv), textcoords="offset points",
                xytext=(9, -3), fontsize=10.5, color=c, fontweight="bold", zorder=6)

fig.suptitle("Make the whole slider usable", fontsize=17, fontweight="bold", color=TEXT, y=0.99)
ax.set_title("actual volume  =  slider position ^ p", fontsize=11.5, color=MUTED, pad=10)
ax.set_xlabel("Slider position  (%)")
ax.set_ylabel("Actual volume sent  (%)")
ax.set_xlim(0, 100); ax.set_ylim(0, 100)
ax.grid(color=GRID, alpha=0.5, lw=0.7)
for s in ax.spines.values():
    s.set_color(GRID)
leg = ax.legend(loc="lower right", fontsize=10.5, facecolor="#161b22",
                edgecolor=GRID, framealpha=0.95)
for t in leg.get_texts():
    t.set_color(TEXT)
ax.annotate("at 50% slider →", (50, 3), fontsize=9, color=MUTED, ha="right")

fig.tight_layout()
os.makedirs("assets", exist_ok=True)
fig.savefig("assets/curve.png", facecolor=BG, bbox_inches="tight")
print("saved assets/curve.png")
