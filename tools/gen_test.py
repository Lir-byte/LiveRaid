import json
import ssl
import sys
import urllib.request
import zlib

level = sys.argv[1] if len(sys.argv) > 1 else "30"
url = "https://127.0.0.1:6969/liveraid/debug/genpmc/300/" + level
req = urllib.request.Request(url)
r = urllib.request.urlopen(req, context=ssl._create_unverified_context(), timeout=300).read()
try:
    r = zlib.decompress(r)
except Exception:
    pass
d = json.loads(r)
if "error" in d:
    print(d)
    sys.exit()
bots = d.pop("bots")
print("player level", level, "->", {k: d[k] for k in ("archetypes", "weaponClasses", "avgBagValue", "valueP10", "valueMedian", "valueP90")})
shown = sorted(b["level"] for b in bots)
print("shown levels: min", shown[0], "median", shown[len(shown) // 2], "max", shown[-1])
# does kit follow the shown level? average value per shown-level bucket
buckets = {}
for b in bots:
    k = "1-14" if b["level"] < 15 else "15-35" if b["level"] < 36 else "36+"
    buckets.setdefault(k, []).append(b["value"])
print({k: (len(v), round(sum(v) / len(v))) for k, v in buckets.items()})
print("kit levels seen:", sorted(set(b["kitLevel"] for b in bots))[:6], "...")
