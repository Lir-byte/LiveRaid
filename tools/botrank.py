"""Rank PMC/scav bots of a raid trace by how much they moved and where their time went (good bots: high m/min, low still %, much questing/looting).

Usage: python -I botrank.py [trace.log] [--pace 60-90]   (default: newest BepInEx/plugins/LiveRaid/logs/*.log; needs Log detail Detailed or Full)
At the end: calm pace by kind (mind=Calm samples only) against the online mark (--pace, PMCs out of a fight, m/min).
"""
import glob, os
import re, sys, collections
PACE = (60, 90)
if "--pace" in sys.argv:
    i = sys.argv.index("--pace")
    PACE = tuple(int(x) for x in sys.argv[i + 1].split("-"))
    del sys.argv[i:i + 2]
path = sys.argv[1] if len(sys.argv) > 1 else max(glob.glob(os.path.join("C:/Games/SPT/BepInEx/plugins/LiveRaid/logs", "*.log")), key=os.path.getmtime)
TL = re.compile(r"^(\d+):(\d+) \| timeline \| (?P<bot>.+?) \[(?P<kind>\w+)/(?P<style>\w+)/(?P<skill>\w+)\] layer=(?P<layer>.*?) mind=(?P<mind>\w+).*?(?:enemy=(?P<en>\S+)@(?P<d>\d+)m(?P<vis> visible)?)?.*?hp=(?P<hp>\d+)%.*?moved=(?P<moved>\d+)m")
calm = collections.defaultdict(lambda: [0, 0])  # kind -> [moved m, samples]
step = None
bots = collections.defaultdict(lambda: {"n": 0, "moved": 0, "still": 0, "layers": collections.Counter(), "kind": "", "style": "", "first": None, "last": None, "minds": collections.Counter()})
for line in open(path, encoding="utf-8", errors="replace"):
    m = TL.match(line)
    if not m:
        continue
    if m.group("kind") == "Other":
        continue
    b = bots[m.group("bot")]
    t = int(m.group(1)) * 60 + int(m.group(2))
    b["first"] = b["first"] if b["first"] is not None else t
    b["last"] = t
    b["n"] += 1
    mv = int(m.group("moved"))
    b["moved"] += mv
    b["still"] += 1 if mv <= 1 else 0
    L = m.group("layer")
    if "SAIN" in L:
        L = "SAIN" + (" seen" if m.group("vis") else "")
    b["layers"][L] += 1
    b["minds"][m.group("mind")] += 1
    if m.group("mind") == "Calm":
        c = calm[m.group("kind")]
        c[0] += mv
        c[1] += 1
    b["kind"] = m.group("kind") + "/" + m.group("style") + "/" + m.group("skill")
rows = []
for name, b in bots.items():
    if b["n"] < 24:
        continue
    mins = (b["last"] - b["first"]) / 60 or 1
    rows.append((b["moved"] / mins, name, b, mins))
rows.sort(reverse=True)
for rate, name, b, mins in rows:
    n = b["n"]
    top = ", ".join(f"{k} {100*v/n:.0f}%" for k, v in b["layers"].most_common(4))
    print(f"{name:28s} {b['kind']:26s} {mins:4.1f} min  {rate:5.1f} m/min  still {100*b['still']/n:3.0f}%  | {top}")

# Timeline step: 15 s at Detailed, 5 s at Full (the most common gap between one bot's lines)
gaps = collections.Counter()
for b in bots.values():
    if b["n"] > 1:
        gaps[round((b["last"] - b["first"]) / (b["n"] - 1) / 5) * 5] += 1
step = (gaps.most_common(1)[0][0] if gaps else 15) or 15
print()
print(f"calm pace (mind=Calm, timeline every ~{step} s), online mark for PMCs {PACE[0]}-{PACE[1]} m/min:")
for kind, (moved, n) in sorted(calm.items()):
    if n < 10:
        continue
    rate = moved / (n * step) * 60
    ok = "" if kind != "Pmc" else ("  ✔" if PACE[0] <= rate <= PACE[1] else "  ✔ (faster than the mark)" if rate > PACE[1] else "  ✘ (slower than online)")
    print(f"  {kind:12s} {rate:5.0f} m/min over {n * step / 60:.0f} bot-minutes{ok}")
