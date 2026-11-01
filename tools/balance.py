"""How evenly the bots spread over the map in a raid (the coverage csv of the plugin folder).

    python -X utf8 -I tools/balance.py [coverage.csv ...] [--minutes 5] [--grid 3x2]

For each raid and each kind of bot (PMC, scavs) and each window of N minutes: the share of the bot-samples in every cell of a grid laid over the extent
the bots walked, and a balance number: 1 = the same in every cell, towards 0 = all in one. This is the measure the "Spread the bots over the map" function
is judged by (compare raids with the function off and on). The numbers are about the map as the bots used it, so a map with a lake in one quarter is never
1.00 - only compare the same map.
"""
import csv
import glob
import math
import os
import sys
from collections import Counter

DEFAULT_DIR = r"C:\Games\SPT\BepInEx\plugins\LiveRaid\coverage"


def load(fn):
    pts = []
    with open(fn, encoding="utf-8") as f:
        rows = list(csv.reader(f))
    for r in rows[1:]:
        try:
            pts.append((int(r[0]), r[3], float(r[5]), float(r[7])))
        except (ValueError, IndexError):
            continue
    return pts


def balance(counts, cells):
    n = sum(counts.values())
    if n == 0:
        return None
    shares = [counts.get(c, 0) / n for c in range(cells)]
    ent = -sum(s * math.log(s) for s in shares if s > 0)
    return ent / math.log(cells), shares


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    minutes = 5
    gx, gz = 3, 2
    if "--minutes" in sys.argv:
        minutes = int(sys.argv[sys.argv.index("--minutes") + 1])
        args = [a for a in args if a != str(minutes)]
    if "--grid" in sys.argv:
        g = sys.argv[sys.argv.index("--grid") + 1]
        gx, gz = (int(v) for v in g.lower().split("x"))
        args = [a for a in args if a != g]
    files = args or sorted(glob.glob(os.path.join(DEFAULT_DIR, "*.csv")))[-3:]
    cells = gx * gz
    for fn in files:
        pts = load(fn)
        pts = [p for p in pts if p[1] in ("Pmc", "PlayerScav", "Scav")]
        if not pts:
            continue
        x0, x1 = min(p[2] for p in pts), max(p[2] for p in pts)
        z0, z1 = min(p[3] for p in pts), max(p[3] for p in pts)
        tmax = max(p[0] for p in pts)
        print(f"{os.path.basename(fn)}: {tmax / 60:.0f} min, extent x {x0:.0f}..{x1:.0f}, z {z0:.0f}..{z1:.0f}, grid {gx}x{gz} (cells west->east, south->north)")
        for kind in ("Pmc", "Scav"):
            ks = ("Pmc", "PlayerScav") if kind == "Pmc" else ("Scav",)
            whole = []
            for lo in range(0, int(tmax) + 1, minutes * 60):
                hi = lo + minutes * 60
                cnt = Counter()
                for t, k, x, z in pts:
                    if k in ks and lo <= t < hi:
                        cx = min(gx - 1, int((x - x0) / max(1.0, x1 - x0) * gx))
                        cz = min(gz - 1, int((z - z0) / max(1.0, z1 - z0) * gz))
                        cnt[cz * gx + cx] += 1
                res = balance(cnt, cells)
                if res is None or sum(cnt.values()) < 30:
                    continue
                b, shares = res
                whole.append(b)
                print(f"  {kind:5s} {lo // 60:2d}-{hi // 60:2d} min  balance {b:.2f}  cells % " + " ".join(f"{s * 100:3.0f}" for s in shares))
            if whole:
                print(f"  {kind:5s} mean balance {sum(whole) / len(whole):.2f}, worst window {min(whole):.2f}")


if __name__ == "__main__":
    main()
