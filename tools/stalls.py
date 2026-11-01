"""Where bots hang: long runs of a state per bot from a LiveRaid trace timeline.

python -I tools/stalls.py [trace] [--bot NAME] [--min SECONDS]

Runs looked for (timeline samples every 5 s at Full, 15 s at Detailed):
  empty     ammo=empty in a fight or near an enemy, and out of it
  reload    ammo=reloading for longer than a reload takes
  meds      meds=using / hands=meds
  still     moved <= 1 m while not looting and not in a LiveRaid task that stands on purpose
  calmhit   hp dropping while mind=Calm (shot without noticing)
  exfil     layer=Exfiltration with hp < 40 %, bleeding or an empty gun
  layers    the bot's layer changing back and forth (A -> B -> A) more than 6 times a minute
"""
import os
import re
import sys
from collections import defaultdict

LOGS = r"C:\Games\SPT\BepInEx\plugins\LiveRaid\logs"
TL = re.compile(r"^(\d\d):(\d\d) \| timeline \| (.+?) \[(\w+)/(\w+)/(\w+)\] (.*)$")
FIELD = re.compile(r"(\w+)=((?:\"[^\"]*\")|\S+)")


def secs(m, s):
    return int(m) * 60 + int(s)


def fmt(t):
    return f"{t // 60:02d}:{t % 60:02d}"


def parse(path, only=None):
    bots = defaultdict(list)
    with open(path, encoding="utf-8", errors="replace") as f:
        for line in f:
            m = TL.match(line.rstrip("\n"))
            if not m:
                continue
            name = m.group(3)
            if only and name != only:
                continue
            d = {k: v.strip('"') for k, v in FIELD.findall(m.group(7))}
            d["t"] = secs(m.group(1), m.group(2))
            d["kind"] = m.group(4)
            d["style"] = m.group(5)
            mv = d.get("moved", "0m").rstrip("m")
            try:
                d["mv"] = float(mv)
            except ValueError:
                d["mv"] = 0.0
            hp = d.get("hp", "100%").rstrip("%")
            try:
                d["hpv"] = float(hp)
            except ValueError:
                d["hpv"] = 100.0
            bots[name].append(d)
    return bots


def runs(samples, pred, gap=16):
    """Consecutive samples where pred holds -> (start, end, samples)."""
    out = []
    cur = []
    for s in samples:
        if pred(s) and (not cur or s["t"] - cur[-1]["t"] <= gap):
            cur.append(s)
        else:
            if len(cur) > 1:
                out.append(cur)
            cur = [s] if pred(s) else []
    if len(cur) > 1:
        out.append(cur)
    return out


STAND_OK = ("Looting", "LiveRaid Heal", "LiveRaid Quest", "Exfiltration")


def main():
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    args = sys.argv[1:]
    only = None
    minimum = 30
    path = None
    i = 0
    while i < len(args):
        if args[i] == "--bot":
            only = args[i + 1]
            i += 2
        elif args[i] == "--min":
            minimum = int(args[i + 1])
            i += 2
        else:
            path = args[i]
            i += 1
    if path is None:
        files = sorted((os.path.join(LOGS, f) for f in os.listdir(LOGS) if f.endswith(".log")), key=os.path.getmtime)
        path = files[-1]
    print("file:", path)
    bots = parse(path, only)

    checks = {
        "empty gun": lambda s: s.get("ammo") == "empty",
        "reloading": lambda s: s.get("ammo", "").startswith("reloading"),
        "meds in use": lambda s: s.get("meds") == "using" or s.get("hands") == "meds",
        "standing still": lambda s: s["mv"] <= 1.0 and not any(s.get("layer", "").startswith(x) for x in STAND_OK)
        and not (s.get("layer") == "LiveRaid Tactics" and s.get("task", "").startswith(("Listen", "Handover"))),
        "exfil hurt/dry": lambda s: s.get("layer") == "Exfiltration" and (s["hpv"] < 40 or s.get("ammo") == "empty"),
        "knife in hands": lambda s: s.get("hands") == "a_knife",
    }
    for title, pred in checks.items():
        found = []
        for name, ss in bots.items():
            for r in runs(ss, pred):
                dur = r[-1]["t"] - r[0]["t"]
                if dur >= minimum:
                    layers = defaultdict(int)
                    for s in r:
                        layers[s.get("layer", "?")] += 1
                    top = ", ".join(f"{k} {v}" for k, v in sorted(layers.items(), key=lambda kv: -kv[1])[:3])
                    enemy = sum(1 for s in r if "enemy" in s)
                    found.append((dur, name, r[0]["t"], r[-1]["t"], r[0]["kind"], top, enemy, len(r)))
        found.sort(reverse=True)
        print(f"\n== {title} (runs >= {minimum} s): {len(found)} ==")
        for dur, name, a, b, kind, top, enemy, n in found[:25]:
            print(f"  {dur:4d} s  {name} [{kind}] {fmt(a)}-{fmt(b)}  layers: {top}; enemy known in {enemy}/{n}")

    # hp falling while Calm: shot without noticing
    print("\n== hp falling while mind=Calm (hit, did not notice) ==")
    for name, ss in bots.items():
        for a, b in zip(ss, ss[1:]):
            if b["t"] - a["t"] <= 16 and a.get("mind") == "Calm" and b.get("mind") == "Calm" and a["hpv"] - b["hpv"] >= 8:
                print(f"  {name} {fmt(a['t'])}-{fmt(b['t'])} hp {a['hpv']:.0f} -> {b['hpv']:.0f} in {b.get('layer')}")

    # layer flapping per bot per minute
    print("\n== layer back-and-forth (A->B->A within 15 s), worst bots ==")
    flaps = []
    for name, ss in bots.items():
        n = 0
        pairs = defaultdict(int)
        for a, b, c in zip(ss, ss[1:], ss[2:]):
            if c["t"] - a["t"] <= 15 and a.get("layer") == c.get("layer") and a.get("layer") != b.get("layer"):
                n += 1
                pairs[f"{a.get('layer')} <-> {b.get('layer')}"] += 1
        if n:
            mins = max(1, (ss[-1]["t"] - ss[0]["t"]) / 60)
            flaps.append((n / mins, n, name, sorted(pairs.items(), key=lambda kv: -kv[1])[:2]))
    flaps.sort(reverse=True)
    for rate, n, name, top in flaps[:15]:
        print(f"  {name}: {n} ({rate:.1f}/min)  " + "; ".join(f"{k} {v}" for k, v in top))


if __name__ == "__main__":
    main()
