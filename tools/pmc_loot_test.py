"""Checks that PMCs start the raid without loot: only kit (ammo, magazines, meds, food, grenades) in rig, backpack and pockets.

Needs the SPT server running (with a profile in the launcher). Generates PMCs the same way the raid does.
  python -I pmc_loot_test.py [count] [player_level]
"""
import json
import ssl
import sys
import urllib.request
import zlib

count = sys.argv[1] if len(sys.argv) > 1 else "200"
level = sys.argv[2] if len(sys.argv) > 2 else "30"
url = f"https://127.0.0.1:6969/liveraid/debug/pmcloot/{count}/{level}"
r = urllib.request.urlopen(urllib.request.Request(url), context=ssl._create_unverified_context(), timeout=600).read()
try:
    r = zlib.decompress(r)
except Exception:
    pass
d = json.loads(r)
if "error" in d:
    print(d)
    sys.exit(1)

sys.stdout.reconfigure(encoding="utf-8")
print(f"{d['count']} PMCs, player level {d['level']}, pmcEmptyLoot={d['pmcEmptyLoot']}, backpacks: {d['backpacks']}")
for k, v in d.get('lootCounts', {}).items():
    print(f'  loot counts in template ({v} bots): {k}')
for slot, kinds in d["kinds"].items():
    total = sum(kinds.values())
    print(f"  {slot}: {total} pieces " + ", ".join(f"{k} {v}" for k, v in sorted(kinds.items(), key=lambda kv: -kv[1])))
loot = d["notKit"]
print(f"not kit: {sum(loot.values())} pieces" + ("" if loot else " - PMCs start without loot"))
for k, v in list(loot.items())[:40]:
    print(f"  {v:4d} x {k}")
if "spareMagazinesPerBot" in d:
    h = d["spareMagazinesPerBot"]
    print("spare magazines per PMC (rig+pockets+backpack+secure, not counting the one in the gun): " + ", ".join(f"{int(k)}: {v}" for k, v in h.items()))
