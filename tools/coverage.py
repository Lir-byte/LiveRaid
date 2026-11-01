"""Coverage report for LiveRaid: how much of a map's playable area bots actually visited, floors and rooms included,
and whether bosses stay home.

Usage: python -I coverage.py [csv files...]   (default: all CSVs in BepInEx/plugins/LiveRaid/coverage)

Playable area = every loose-loot spawn point of the map in the SPT database (3D: every floor, every room).
A point counts as visited when a bot passed within R metres of it ON THE SAME FLOOR (height difference < 2.5 m).
Bot positions are logged every 10 s; between two samples of one bot on one floor the path is interpolated.
Bots tuned by LiveRaid (PMC/scav) count for coverage; "Other" bots (bosses, followers, raiders, rogues, cultists...) are checked
separately: they should stay near where they spawned, like in vanilla.
"""
import collections, csv, glob, json, math, os, sys

SPT = r"C:\Games\SPT"
DB = os.path.join(SPT, "SPT_Runtime", "SPT_Data", "database", "locations")
PLUGIN = os.path.join(SPT, "BepInEx", "plugins", "LiveRaid")
FLOOR = 2.5             # same floor if the height difference is below this
RADII = (5, 10, 20)     # "visited" radius, metres; 10 m is the headline number
POI_RADIUS = 15         # a loot point "has a route" when a LiveRaid POI is this close on its floor
BOSS_ROAM_LIMIT = 250.0  # metres from the first seen position


class Grid:
    """Points bucketed by 20 m columns for same-floor nearest-distance queries."""

    def __init__(self, pts, cell=20.0):
        self.c = cell
        self.g = collections.defaultdict(list)
        for p in pts:
            self.g[(int(p[0] // cell), int(p[2] // cell))].append(p)

    def nearest(self, q, r):
        best = None
        ring = int(math.ceil(r / self.c))
        cx, cz = int(q[0] // self.c), int(q[2] // self.c)
        for dx in range(-ring, ring + 1):
            for dz in range(-ring, ring + 1):
                for p in self.g.get((cx + dx, cz + dz), ()):
                    if abs(p[1] - q[1]) < FLOOR:
                        d = math.hypot(p[0] - q[0], p[2] - q[2])
                        if d <= r and (best is None or d < best):
                            best = d
        return best


def loot_points(location):
    """Every loose-loot spawn point (and group position) of the map."""
    pts = []
    try:
        loose = json.load(open(os.path.join(DB, location.lower(), "looseLoot.json"), encoding="utf-8"))
    except (OSError, ValueError):
        return pts
    for sp in loose.get("spawnpoints", []) + loose.get("spawnpointsForced", []):
        t = sp.get("template", {})
        for p in [t.get("Position")] + [g.get("Position") for g in t.get("GroupPositions") or []]:
            if p:
                pts.append((p["x"], p["y"], p["z"]))
    return pts


def upper_floor_flags(pts):
    """True for points at least one floor above the lowest loot point of their 10 m column."""
    low = {}
    for p in pts:
        k = (int(p[0] // 10), int(p[2] // 10))
        low[k] = min(low.get(k, p[1]), p[1])
    return [p[1] - low[(int(p[0] // 10), int(p[2] // 10))] >= FLOOR for p in pts], low


def track_points(tracks):
    """Logged positions plus the interpolated path between two samples of one bot on one floor."""
    out = []
    for tr in tracks:
        tr.sort()
        for a, b in zip(tr, tr[1:]):
            out.append(a[1:])
            seg = math.dist(a[1:], b[1:])
            if b[0] - a[0] <= 12 and abs(a[2] - b[2]) < FLOOR and seg <= 70:
                n = int(seg // 3)
                for i in range(1, n):
                    out.append(tuple(a[j] + (b[j] - a[j]) * i / n for j in (1, 2, 3)))
        if tr:
            out.append(tr[-1][1:])
    return out


def pct(n, d):
    return f"{n}/{d} ({100 * n / d:.0f}%)" if d else "0/0"


def main():
    files = sys.argv[1:] or glob.glob(os.path.join(PLUGIN, "coverage", "*.csv"))
    by_map = collections.defaultdict(list)
    for f in files:
        by_map[os.path.basename(f).split("_2")[0]].append(f)

    for location, flist in sorted(by_map.items()):
        flist.sort()
        tracks_by_file = collections.defaultdict(lambda: collections.defaultdict(list))
        styles = collections.Counter()
        others = {}  # (file, bot) -> [role, first_x, first_z, max_disp]
        minutes = []
        for f in flist:
            times = []
            for row in csv.DictReader(open(f, encoding="utf-8")):
                x, y, z, t = float(row["x"]), float(row["y"]), float(row["z"]), int(row["time"])
                times.append(t)
                if row.get("kind") in ("Other", "?"):
                    key = (f, row["bot"])
                    rec = others.setdefault(key, [row.get("role", "?"), x, z, 0.0])
                    rec[3] = max(rec[3], math.hypot(x - rec[1], z - rec[2]))
                    continue
                tracks_by_file[f][row["bot"]].append((t, x, y, z))
                styles[row["style"]] += 1
            minutes.append((max(times) - min(times)) / 60 if times else 0)

        truth = loot_points(location)
        n_bots = sum(len(v) for v in tracks_by_file.values())
        print(f"{location}: {len(flist)} raid(s) ({', '.join(f'{m:.0f}' for m in minutes)} min logged), {n_bots} PMC/scav bots; samples by style: {dict(styles)}")
        if not truth:
            print("  no loot spawn data in the SPT database for this map")
        else:
            upper, low = upper_floor_flags(truth)
            n_upper = sum(upper)
            walked = track_points([tr for v in tracks_by_file.values() for tr in v.values()])
            g = Grid(walked)
            near = [g.nearest(p, max(RADII)) for p in truth]
            parts = [f"{r} m {pct(sum(1 for d in near if d is not None and d <= r), len(truth))}" for r in RADII]
            print(f"  loot points a bot passed on the same floor: " + ", ".join(parts))
            up_hit = sum(1 for d, u in zip(near, upper) if u and d is not None and d <= 10)
            print(f"  upper floors (10 m): {pct(up_hit, n_upper)}; ground level: {pct(sum(1 for d, u in zip(near, upper) if not u and d is not None and d <= 10), len(truth) - n_upper)}")

            levels = collections.Counter()
            for p in walked:
                k = (int(p[0] // 10), int(p[2] // 10))
                if k in low:
                    levels["upper" if p[1] - low[k] >= FLOOR else "ground"] += 1
            on = sum(levels.values())
            print(f"  time bots spend on upper floors (where there is loot): {100 * levels['upper'] / on:.0f}% vs {100 * n_upper / len(truth):.0f}% of loot points" if on else "")

            # does coverage still grow raid by raid (more raids = more) or has it plateaued (a structural gap)?
            acc, steps = [], []
            for f in flist:
                acc += track_points(list(tracks_by_file[f].values()))
                gg = Grid(acc)
                steps.append(100 * sum(1 for p in truth if (lambda d: d is not None and d <= 10)(gg.nearest(p, 10))) / len(truth))
            print(f"  cumulative 10 m coverage by raid: {' -> '.join(f'{s:.0f}%' for s in steps)}")

            # LiveRaid's own route points (map cache)
            try:
                cache = json.load(open(os.path.join(PLUGIN, "cache", location + ".json"), encoding="utf-8"))
                pois = [(p["X"], p["Y"], p["Z"]) for p in cache.get("Pois", [])]
                pg = Grid(pois)
                pn = [pg.nearest(p, POI_RADIUS) for p in truth]
                has = [d is not None for d in pn]
                kinds = collections.Counter(str(p.get("Kind")) for p in cache.get("Pois", []))
                print(f"  route points (cache v{cache.get('Version')}): {len(pois)} {dict(kinds)}; loot points with one within {POI_RADIUS} m on their floor: "
                      f"{pct(sum(has), len(truth))}, upper floors {pct(sum(1 for h, u in zip(has, upper) if h and u), n_upper)}")
            except (OSError, ValueError):
                has = None
                print("  no map cache yet (built on the first raid on this map)")

            miss = collections.Counter()
            for i, (p, d) in enumerate(zip(truth, near)):
                if d is None or d > 10:
                    miss[(int(p[0] // 25) * 25, int(p[1] // 3.5) * 3.5, int(p[2] // 25) * 25, None if has is None else has[i])] += 1
            if miss:
                print("  biggest areas no bot came within 10 m of (x, height, z):")
                for (x, y, z, h), n in miss.most_common(6):
                    route = "" if h is None else ("  route point near" if h else "  NO route point")
                    print(f"    {n:>3} loot points at x={x}..{x + 25} y~{y:.0f} z={z}..{z + 25}{route}")

        # places learned from the player's routes (PlayerTrail): did bots get there?
        try:
            learned = json.load(open(os.path.join(PLUGIN, "learned", location + ".json"), encoding="utf-8")).get("Places", [])
        except (OSError, ValueError):
            learned = []
        if learned:
            lg = Grid(track_points([tr for v in tracks_by_file.values() for tr in v.values()]))
            stops = [l for l in learned if l.get("Stops", 0) > 0]
            visited = [l for l in learned if lg.nearest((l["X"], l["Y"], l["Z"]), 10) is not None]
            print(f"  places learned from your routes: {len(learned)} ({len(stops)} where you stopped); a bot came within 10 m of "
                  f"{pct(len(visited), len(learned))}, of your stops {pct(sum(1 for l in visited if l.get('Stops', 0) > 0), len(stops))}")

        # the old 2D 50 m cell metric, for comparison with earlier reports
        cells = {(int(p[0] // 50), int(p[2] // 50)) for p in truth}
        vis = {(int(s[1] // 50), int(s[3] // 50)) for v in tracks_by_file.values() for tr in v.values() for s in tr}
        if cells:
            print(f"  (old 2D metric: {pct(len(cells & vis), len(cells))} 50 m cells with loot visited - ignores floors, overstates)")

        if others:
            roamers = [(name, rec) for (f, name), rec in others.items() if rec[3] > BOSS_ROAM_LIMIT]
            roles = collections.Counter(rec[0] for rec in others.values())
            worst = max(rec[3] for rec in others.values())
            print(f"  bosses/followers/raiders/others: {len(others)} tracked {dict(roles)}; farthest from its first position: {worst:.0f} m; "
                  f"{len(roamers)} roamed further than {BOSS_ROAM_LIMIT:.0f} m")
            for name, rec in sorted(roamers, key=lambda kv: -kv[1][3])[:8]:
                print(f"    ! {name} ({rec[0]}) moved {rec[3]:.0f} m")


if __name__ == "__main__":
    main()
