"""Builds learned/<map>.atlas.json (the map's memory of cold places, see src/LiveRaid.Client/Raid/Atlas.cs) from the coverage CSVs of the raids already played.

Usage: python -X utf8 -I atlas_bootstrap.py [map ...] [--keep N] [--dry]
  map     bigmap, Sandbox ... (default: every map that has a cache and CSVs)
  --keep  how many of the newest raids count (default 6); a raid shorter than 8 minutes of logged bot movement does not count
  --dry   only print, write nothing

A spot is "visited" in a raid when a bot came within 12 m of it on the same floor (height difference <= 3.5 m), the rule of CoverageLedger; the path between two
samples of one bot (10 s apart) is interpolated. Missed = how many of the counted raids IN A ROW, from the newest, nobody came; Seen = raids with a visit.
Never overwrites an atlas the mod has already written unless --force is given.
"""
import collections, csv, glob, json, math, os, sys

SPT = r"C:\Games\SPT"
PLUGIN = os.path.join(SPT, "BepInEx", "plugins", "LiveRaid")
RADIUS, HEIGHT, MIN_SECONDS = 12.0, 3.5, 480


def load_pois(map_id):
    path = os.path.join(PLUGIN, "cache", map_id + ".json")
    with open(path, encoding="utf-8") as f:
        return [(p["X"], p["Y"], p["Z"]) for p in json.load(f)["Pois"]]


def raid_visits(path, pois):
    tracks = collections.defaultdict(list)
    tmin, tmax = 10 ** 9, 0
    with open(path, encoding="utf-8") as f:
        for r in csv.DictReader(f):
            if r["kind"] == "Other":
                continue
            t = int(r["time"])
            tmin, tmax = min(tmin, t), max(tmax, t)
            tracks[r["bot"]].append((t, float(r["x"]), float(r["y"]), float(r["z"])))
    if tmax - tmin < MIN_SECONDS:
        return None
    grid = collections.defaultdict(list)
    for tr in tracks.values():
        tr.sort()
        pts = []
        for a, b in zip(tr, tr[1:]):
            if b[0] - a[0] > 30:
                pts.append(a[1:])
                continue
            n = max(1, int(math.hypot(b[1] - a[1], b[3] - a[3]) // 4))
            for i in range(n + 1):
                k = i / n
                pts.append((a[1] + (b[1] - a[1]) * k, a[2] + (b[2] - a[2]) * k, a[3] + (b[3] - a[3]) * k))
        if tr:
            pts.append(tr[-1][1:])
        for p in pts:
            grid[(int(p[0] // 12), int(p[2] // 12))].append(p)
    seen = []
    for (x, y, z) in pois:
        cx, cz = int(x // 12), int(z // 12)
        hit = False
        for dx in (-1, 0, 1):
            for dz in (-1, 0, 1):
                for p in grid.get((cx + dx, cz + dz), ()):
                    if abs(p[1] - y) <= HEIGHT and math.hypot(p[0] - x, p[2] - z) <= RADIUS:
                        hit = True
                        break
                if hit:
                    break
            if hit:
                break
        seen.append(hit)
    return seen


def main(argv):
    keep = 6
    maps = []
    dry = "--dry" in argv
    force = "--force" in argv
    i = 0
    while i < len(argv):
        if argv[i] == "--keep":
            keep = int(argv[i + 1])
            i += 1
        elif not argv[i].startswith("--"):
            maps.append(argv[i])
        i += 1
    if not maps:
        maps = [os.path.basename(p)[:-5] for p in glob.glob(os.path.join(PLUGIN, "cache", "*.json"))]
    for m in maps:
        out = os.path.join(PLUGIN, "learned", m + ".atlas.json")
        if os.path.exists(out) and not force:
            print(m, "- atlas already exists, left alone (use --force)")
            continue
        try:
            pois = load_pois(m)
        except OSError:
            print(m, "- no cache, skipped")
            continue
        files = sorted(glob.glob(os.path.join(PLUGIN, "coverage", m + "_*.csv")))
        raids = []
        for fpath in files:
            v = raid_visits(fpath, pois)
            if v is not None:
                raids.append((os.path.basename(fpath), v))
        raids = raids[-keep:]
        print(m, "- counted raids:", [r[0] for r in raids])
        if not raids:
            continue
        spots = []
        for idx, (x, y, z) in enumerate(pois):
            flags = [r[1][idx] for r in raids]
            missed = 0
            for hit in reversed(flags):
                if hit:
                    break
                missed += 1
            spots.append({"X": round(x, 3), "Y": round(y, 3), "Z": round(z, 3), "Missed": missed, "Seen": sum(flags)})
        cold = [s for s in spots if s["Missed"] >= 2]
        print("   spots", len(spots), "cold (missed 2+ raids in a row)", len(cold), "never seen at all", sum(1 for s in spots if s["Seen"] == 0))
        if not dry:
            os.makedirs(os.path.dirname(out), exist_ok=True)
            with open(out, "w", encoding="utf-8") as f:
                json.dump({"Version": 1, "LocationId": m, "Raids": len(raids), "Spots": spots}, f, separators=(",", ":"))
            print("   written", out)


if __name__ == "__main__":
    main(sys.argv[1:])
