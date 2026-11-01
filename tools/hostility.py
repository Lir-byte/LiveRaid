"""PMC hostility as the running SPT server holds it: pmc.json values and every map's PMC entries.
  python -I hostility.py
"""
import json, ssl, sys, urllib.request, zlib

r = urllib.request.urlopen("https://127.0.0.1:6969/liveraid/debug/hostility", context=ssl._create_unverified_context(), timeout=60).read()
try:
    r = zlib.decompress(r)
except Exception:
    pass
d = json.loads(r)
sys.stdout.reconfigure(encoding="utf-8")
for side, v in d["pmcConfig"].items():
    print(f"pmc.json {side}: vs BEAR {v['bearEnemyChance']}%, vs USEC {v['usecEnemyChance']}%, chanced {list(v['chanced'])}, friendly {v['friendly']}")
for m, s in sorted(d["maps"].items()):
    if s:
        print(f"  {m}: {s}")
