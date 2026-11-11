"""Carry the map edits of the game (BepInEx/plugins/LiveRaid/studio/edits/<map>.json: LiveRaid Studio and the auto links written from the player's
trail after each raid) into the project (data/map_edits/<map>.json), which make_release.py puts into every release archive.

Merging, never losing: links are united (same Id, or both ends within 1.5 m of an existing one), the other lists (patches, blockers, climbs, paths,
spots) are united by Id; nothing already in the project is deleted. Run: python -X utf8 -I tools/sync_map_edits.py [game_dir]
make_release.py runs it first (when the game folder is there)."""
import json
import math
import os
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
GAME = sys.argv[1] if len(sys.argv) > 1 else r"C:\Games\SPT"


def near(a, b):
    return a and b and len(a) >= 3 and len(b) >= 3 and math.dist(a[:3], b[:3]) < 1.5


def same_link(x, y):
    if x.get("Id") and x.get("Id") == y.get("Id"):
        return True
    return (near(x.get("A"), y.get("A")) and near(x.get("B"), y.get("B"))) or (near(x.get("A"), y.get("B")) and near(x.get("B"), y.get("A")))


def sync(game_dir=GAME, quiet=False):
    src_dir = os.path.join(game_dir, "BepInEx", "plugins", "LiveRaid", "studio", "edits")
    dst_dir = os.path.join(ROOT, "data", "map_edits")
    if not os.path.isdir(src_dir):
        return 0
    os.makedirs(dst_dir, exist_ok=True)
    changed = 0
    for name in sorted(os.listdir(src_dir)):
        if not name.endswith(".json"):
            continue
        with open(os.path.join(src_dir, name), encoding="utf-8") as f:
            src = json.load(f)
        dst_path = os.path.join(dst_dir, name)
        dst = json.load(open(dst_path, encoding="utf-8")) if os.path.exists(dst_path) else {"Format": "liveraid-edits/1", "Map": src.get("Map"), "Revision": 0}
        added = 0
        for key in ("Patches", "Blockers", "Links", "Routes", "Paths", "Spots"):
            have = dst.setdefault(key, [])
            for item in src.get(key, []):
                if key == "Links":
                    dup = any(same_link(item, h) for h in have)
                else:
                    dup = item.get("Id") and any(h.get("Id") == item.get("Id") for h in have)
                if not dup:
                    have.append(item)
                    added += 1
        if added or not os.path.exists(dst_path):
            dst["Revision"] = max(int(dst.get("Revision", 0)), int(src.get("Revision", 0))) + 1
            with open(dst_path, "w", encoding="utf-8") as f:
                json.dump(dst, f, ensure_ascii=False, indent=1)
            changed += 1
            if not quiet:
                print(f"{name}: {added} new item(s) taken from the game, {len(dst['Links'])} links in the project now")
        elif not quiet:
            print(f"{name}: nothing new")
    return changed


if __name__ == "__main__":
    sync()
