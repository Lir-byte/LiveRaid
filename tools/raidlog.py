"""Digest of one LiveRaid raid, for whoever has to judge how the bots behaved.

Usage:
  python -I raidlog.py                 newest trace (BepInEx/plugins/LiveRaid/logs/*.log), else BepInEx/LogOutput.log
  python -I raidlog.py <file>          a trace file or a LogOutput.log
  python -I raidlog.py --bot <name>    also print every line about one bot (timeline included)

Works on both: the BepInEx log (log detail Normal: "LR ..." lines) and the per-raid trace (Detailed/Full: raid time, category, message).
Prints: errors (LiveRaid and other mods), LR line counts, combat stance changes per bot with flapping detection (a stance that flips back
within 10 s), gunfire reactions, tasks started/ended and why, where bot time goes (timeline: layers,
"frozen" = in SAIN combat over an unseen enemy > 60 m away without moving), stuck bots, health lines (systems' timings, error counters), raid summaries.
"""
import collections, glob, os, re, sys

SPT = r"C:\Games\SPT"
TRACE_DIR = os.path.join(SPT, "BepInEx", "plugins", "LiveRaid", "logs")
BEPINEX_LOG = os.path.join(SPT, "BepInEx", "LogOutput.log")

TRACE_LINE = re.compile(r"^(?P<t>\d\d:\d\d|--:--) \| (?P<cat>[^|]+?) \| (?P<msg>.*)$")
BEP_LINE = re.compile(r"^\[(?P<lvl>\w+)\s*:\s*(?P<src>[^\]]+?)\] (?P<msg>.*)$")
COMBAT = re.compile(r"LR Combat: (?P<bot>.+?) \((?P<style>\w+), (?P<skill>\w+)\) (?P<old>\w+) -> (?P<new>\w+)")
GUNFIRE = re.compile(r"LR Gunfire: (?P<bot>.+?) \(.*?\) hears a fight (?P<dist>\d+) m away .*?-> (?P<reaction>\w+)")
TASK_START = re.compile(r"^(?P<bot>.+?) starts (?P<kind>\w+) for")
TASK_END = re.compile(r"^(?P<bot>.+?) ends (?P<kind>\w+) after (?P<sec>\d+) s: (?P<why>.+)$")
LR_TAG = re.compile(r"\b(LR [A-Z][A-Za-z]+(?: on)?)\b")
TIMELINE = re.compile(r"^(?P<bot>.+?) \[(?P<kind>\w+)/[^\]]*\] layer=(?P<layer>.*?) (?=mind=|stance=|task=|enemy=|quest=|hp=)(?:mind=(?P<mind>\w+) )?(?:stance=\S+ morale=\S+ )?(?:task=.*?left\) )?(?:enemy=.+?@(?P<dist>\d+)m(?P<vis> visible)?(?: sain=(?P<sain>\S+))? )?.*?moved=(?P<moved>\d+)m")


def secs(stamp):
    if not stamp or stamp == "--:--":
        return None
    m, s = stamp.split(":")
    return int(m) * 60 + int(s)


def pick_file(args):
    files = [a for a in args if not a.startswith("--")]
    if files:
        return files[0]
    traces = sorted(glob.glob(os.path.join(TRACE_DIR, "*.log")), key=os.path.getmtime)
    if traces and (not os.path.exists(BEPINEX_LOG) or os.path.getmtime(traces[-1]) >= os.path.getmtime(BEPINEX_LOG) - 600):
        return traces[-1]
    return BEPINEX_LOG


def read(path):
    """Yields (raid_seconds or None, category, source, level, message)."""
    with open(path, "rb") as f:
        for raw in f:
            # The BepInEx log mixes UTF-8 lines with a few cp1251 ones (some bot names): decode line by line
            try:
                line = raw.decode("utf-8")
            except UnicodeDecodeError:
                line = raw.decode("cp1251", errors="replace")
            line = line.rstrip("\r\n")
            m = TRACE_LINE.match(line)
            if m:
                cat = m.group("cat").strip()
                lvl = "Error" if cat == "ERROR" else "Warning" if cat == "WARN" else "Info"
                yield secs(m.group("t")), cat, "LiveRaid", lvl, m.group("msg")
                continue
            m = BEP_LINE.match(line)
            if m:
                yield None, "bepinex", m.group("src").strip(), m.group("lvl"), m.group("msg")


BRAIN_PING = re.compile(r"^.+? (?P<what>layer|SAIN decision) ping-pong (?P<a>.+?) -> (?P<b>.+?) \([\d.]+ s\) -> ")
MIND = re.compile(r"^.+? (?P<old>Calm|Alert|Engaged|Settling) -> (?P<new>Calm|Alert|Engaged|Settling) after")
IDLE_QB = re.compile(r"layer=BotRecoveryLayer .*?qb=(\w+)")


def main():
    args = sys.argv[1:]
    bot_filter = args[args.index("--bot") + 1] if "--bot" in args and args.index("--bot") + 1 < len(args) else None
    if bot_filter:
        args = [a for a in args if a not in ("--bot", bot_filter)]
    path = pick_file(args)
    print(f"file: {path}")

    errors = collections.Counter()
    error_samples = {}
    tags = collections.Counter()
    categories = collections.Counter()
    stances = collections.defaultdict(list)  # bot -> [(t, old, new)]
    reactions = collections.Counter()
    task_starts = collections.Counter()
    task_ends = collections.Counter()
    stuck = []
    health = []
    summaries = []
    bot_lines = []
    time_use = collections.Counter()
    frozen_by = collections.Counter()
    sain_decisions = collections.Counter()
    idle_qb = collections.Counter()
    minds = collections.Counter()
    brain_pings = collections.Counter()
    mind_changes = collections.Counter()
    brain_summary = []
    hunt_runs = collections.defaultdict(list)      # bot -> [[start, last, samples]] runs of SAIN combat over an unseen enemy
    spin_loot = collections.Counter()               # bot -> timeline lines where it turned >= 150 deg while looting
    spin_other = collections.Counter()
    lapses = []
    flees = []
    stuck_places = collections.Counter()
    v02 = collections.defaultdict(list)  # 0.2/0.3 systems: weapon, loot danger, squad recovery, exits, key places, body jobs, shut-in bots
    v02_keys = [
        ("weapon back-and-forth stopped", "LR Weapon:"),
        ("gun taken back from the knife", "puts the knife away"),
        ("knife refused beyond arm's reach", "keeps its gun: the enemy is"),
        ("returns fire instead of moving", "returns fire at"),
        ("body jobs added", "will go through"),
        ("body jobs asked for", "'s body (", "goes for"),
        ("body jobs done", "is done with"),
        ("body jobs given up", "gives up on", "tries)"),
        ("shut-in bot freed or not", "is shut in at"),
        ("stepped out of a shooter's sight", "steps out of it"),
        ("empty gun reloaded instead of swapped", "instead of swapping"),
        ("gun empty in a fight, no reload", "its gun is empty and no reload"),
        ("fallen squadmate's gun/armour into the bag", "takes its fallen squadmate's"),
        ("loot break skipped (LootingBots would not take it)", "QuestingBots stops it to loot, but"),
        ("no jump on a broken leg", "does not jump: a leg is broken"),
        ("idle bot told to go on / sent elsewhere", "LR Idle:"),
        ("swap to an empty gun refused / reloads instead", "reloads its empty"),
        ("bot came in short of rounds (given at spawn)", "came in short of rounds"),
        ("looting broken off by danger", "stops looting -"),
        ("squad recovery of a fallen mate", "to recover"),
        ("goes to a fallen mate's body to carry out the loot", "to carry out the loot"),
        ("stash made for a fallen mate's loot", "in a hidden spot at"),
        ("fallen mate's body looted", "s body (~"),
        ("waits at the exit for the squad", "waits at the exit"),
        ("squad together at the exit", "together at the exit"),
        ("heads for a key place", "heads for key place"),
        ("got into a key place", "got into key place"),
        ("new squad leader", "takes the lead"),
        ("spawn sides", "split into"),
        # 0.6.0
        ("room: on to the next thing of the same room", "in the same room ("),
        ("room done: on to the next room", "goes on to the next one"),
        ("left a building (and why)", "leaves the building after"),
        ("leader waits for squadmates who loot", "to finish looting (up to"),
        ("leader walks back to a looter", "who is still looting"),
        ("leader calls the looters on", "calls", " on"),
        ("PMC kept in the raid: too early to leave", "too early"),
        ("one of the few early leavers", "one of the few who leave this early"),
        ("flipping bot calmed", "LR Calm:"),
        ("loot break skipped (nothing near)", "nothing worth taking within"),
        ("loot break: straight to the nearest thing", "loot break - goes for"),
    ]
    order = 0

    for t, cat, src, lvl, msg in read(path):
        order += 1
        for label, key, *also in v02_keys:
            # LiveRaid lines: "LR ..." in the BepInEx log, any category of the trace file (detail lines have no tag)
            if key in msg and all(a in msg for a in also) and ("LR " in msg or t is not None):
                v02[label].append(msg)
        when = t if t is not None else order  # BepInEx log has no raid time: keep the order
        categories[cat] += 1
        if lvl in ("Error", "Fatal") or (lvl == "Warning" and "Exception" in msg):
            key = f"{src}: {msg[:120]}"
            errors[key] += 1
            error_samples.setdefault(key, msg[:400])
        for tag in LR_TAG.findall(msg):
            tags[tag] += 1
        m = IDLE_QB.search(msg)
        if m:
            idle_qb[m.group(1)] += 1
        m = COMBAT.search(msg)
        if m:
            stances[m.group("bot")].append((when, m.group("old"), m.group("new")))
        m = GUNFIRE.search(msg)
        if m:
            reactions[m.group("reaction")] += 1
        if cat == "task":
            m = TASK_START.match(msg)
            if m:
                task_starts[m.group("kind")] += 1
            m = TASK_END.match(msg)
            if m:
                task_ends[(m.group("kind"), re.sub(r"\d+", "#", m.group("why")))] += 1
        if cat == "brain":
            m = BRAIN_PING.search(msg)
            if m:
                brain_pings[f"{m.group('what')}: {m.group('a')} <-> {m.group('b')}"] += 1
        if cat == "mind":
            m = MIND.search(msg)
            if m:
                mind_changes[f"{m.group('old')} -> {m.group('new')}"] += 1
        if msg.startswith("LR Brain on"):
            brain_summary.append(msg)
        if "loses interest in" in msg or "forgets about him for a while" in msg:
            lapses.append(msg)
        if "loses its nerve and leaves the fight" in msg:
            flees.append(msg)
        if cat == "timeline":
            tm = re.search(r"turned=(\d+)d", msg)
            if tm:
                (spin_loot if "layer=Looting" in msg else spin_other)[msg.split(" [")[0]] += 1
            m = TIMELINE.match(msg)
            if m and m.group("kind") != "Other" and t is not None:
                # a run = consecutive timeline samples of the same bot in SAIN combat with an enemy known but not in sight
                hunting = "SAIN" in m.group("layer") and bool(m.group("dist")) and not m.group("vis")
                runs = hunt_runs[m.group("bot")]
                if hunting:
                    if runs and t - runs[-1][1] <= 12 and runs[-1][3]:
                        runs[-1][1] = t
                        runs[-1][2] += 1
                    else:
                        runs.append([t, t, 1, True])
                elif runs:
                    runs[-1][3] = False
            if m and m.group("kind") != "Other" and m.group("mind"):
                minds[m.group("mind")] += 1
            if m and m.group("kind") != "Other":
                layer = m.group("layer")
                if m.group("dist") and "SAIN" in layer:
                    if m.group("sain"):
                        sain_decisions[m.group("sain")] += 1
                    if m.group("vis"):
                        time_use["SAIN combat, enemy visible"] += 1
                    elif int(m.group("dist")) > 60 and int(m.group("moved")) <= 1:
                        time_use["SAIN combat FROZEN (unseen enemy > 60 m, not moving)"] += 1
                        frozen_by[m.group("kind")] += 1
                    elif int(m.group("dist")) > 60:
                        time_use["SAIN combat, unseen enemy > 60 m, moving"] += 1
                    else:
                        time_use["SAIN combat, unseen enemy <= 60 m"] += 1
                else:
                    time_use[layer] += 1
        if "LR Stuck:" in msg:
            stuck.append(msg)
            pm = re.search(r"at \((-?\d+)(?:\.\d)?, -?\d+(?:\.\d)?, (-?\d+)(?:\.\d)?\)", msg)
            if pm:
                stuck_places[(round(int(pm.group(1)) / 10) * 10, round(int(pm.group(2)) / 10) * 10)] += 1
        if msg.startswith("LR Health"):
            health.append(msg)
        if msg.startswith("LR Encounters on") or msg.startswith("LR Quests on") or msg.startswith("map intel") or msg.startswith("LR Population"):
            summaries.append(msg)
        if bot_filter and bot_filter in msg:
            bot_lines.append(f"{'--:--' if t is None else f'{t // 60:02d}:{t % 60:02d}'} [{cat}] {msg}")

    print("\n== errors ==")
    if not errors:
        print("none")
    for key, n in errors.most_common(15):
        print(f"{n:5d} x {error_samples[key][:300]}")

    print("\n== LiveRaid lines ==")
    print(", ".join(f"{k} {v}" for k, v in tags.most_common()))
    if len(categories) > 2:
        print("trace categories: " + ", ".join(f"{k} {v}" for k, v in categories.most_common()))

    print("\n== combat stances ==")
    timed = categories.get("bepinex", 0) < sum(categories.values()) / 2  # trace lines carry raid time, BepInEx lines only order
    flappers = []
    for bot, changes in stances.items():
        flips = 0
        for (t1, o1, n1), (t2, o2, n2) in zip(changes, changes[1:]):
            if n2 == o1 and o2 == n1 and t2 - t1 <= (10 if timed else 8):
                flips += 1
        flappers.append((flips, len(changes), bot))
    total_changes = sum(len(c) for c in stances.values())
    print(f"{total_changes} changes over {len(stances)} bots; transitions: " +
          ", ".join(f"{k} {v}" for k, v in collections.Counter(f"{o}->{n}" for c in stances.values() for _, o, n in c).most_common(8)))
    for flips, n, bot in sorted(flappers, reverse=True)[:8]:
        if n >= 4:
            print(f"  {bot}: {n} changes{f', {flips} quick flip-backs (' + ('<=10 s' if timed else 'within a few log lines') + ')' if flips else ''}")

    if reactions:
        print("\n== gunfire reactions ==")
        print(", ".join(f"{k} {v}" for k, v in reactions.most_common()))

    if task_starts:
        print("\n== tactical tasks ==")
        print("started: " + ", ".join(f"{k} {v}" for k, v in task_starts.most_common()))
        print("ended:   " + ", ".join(f"{k}/{why} {v}" for (k, why), v in task_ends.most_common(12)))

    if time_use:
        total = sum(time_use.values())
        print(f"\n== where bot time goes ({total} timeline samples, PMC/scav) ==")
        for k, v in time_use.most_common(12):
            print(f"  {v / total:6.1%}  {k}")
        if frozen_by:
            print("  frozen by kind: " + ", ".join(f"{k} {v}" for k, v in frozen_by.most_common()))
        if sain_decisions:
            print("  SAIN combat/squad decisions: " + ", ".join(f"{k} {v}" for k, v in sain_decisions.most_common(8)))
        if idle_qb:
            print("  QuestingBots decision while idle (BotRecoveryLayer): " + ", ".join(f"{k} {v}" for k, v in idle_qb.most_common(8)))

    print("\n== brain stability ==")
    if brain_summary:
        for b in brain_summary[-2:]:
            print("  " + b[:900])
    else:
        print("  no 'LR Brain on' line (older build, or the raid ended before a summary)")
    if brain_pings:
        print("  most frequent ping-pongs in the trace (A -> B -> A, B shorter than 3 s / 2 s):")
        for k, v in brain_pings.most_common(10):
            print(f"    {v:4d} x {k}")
    if minds:
        total_m = sum(minds.values())
        print("  time by mind phase: " + ", ".join(f"{k} {v / total_m:.0%}" for k, v in minds.most_common()))
    if mind_changes:
        print("  mind phase changes: " + ", ".join(f"{k} {v}" for k, v in mind_changes.most_common(8)))

    print(f"\n== stuck bots: {len(stuck)} ==")
    kinds = collections.Counter()
    per_bot = collections.Counter()
    for sline in stuck:
        km = re.search(r"LR Stuck: (?P<b>.+?) (?:\((?P<i>[^)]*)\) )?(?P<what>cannot get on|pressed into|at shut door|still cannot|cannot get through|snagged|squad request)", sline)
        if km:
            kinds[km.group("what")] += 1
            per_bot[km.group("b")] += 1
    if kinds:
        print("  by kind: " + ", ".join(f"{k} {v}" for k, v in kinds.most_common()))
        print("  most stuck bots: " + ", ".join(f"{k} {v}" for k, v in per_bot.most_common(5)))
    if stuck_places:
        print("  worst places (10 m cells x,z): " + ", ".join(f"{k} {v}" for k, v in stuck_places.most_common(6)))
    for sline in stuck[:6]:
        print("  " + sline)

    long_runs = sorted(((r[1] - r[0], b, r[0]) for b, rs in hunt_runs.items() for r in rs if r[2] >= 2), reverse=True)
    if long_runs:
        print("\n== longest hunts for an enemy not in sight (SAIN combat, unseen) ==")
        for dur, b, start in long_runs[:6]:
            print(f"  {dur:4d} s  {b}  from {start // 60:02d}:{start % 60:02d}")
        over60 = sum(1 for d, _, _ in long_runs if d > 60)
        print(f"  runs over 60 s: {over60} of {len(long_runs)}   (the cold-fight rule should keep these rare; before it bots hunted for 5-20 minutes)")
    if lapses:
        print(f"  cold fights given up: {len(lapses)}")
    if flees:
        print(f"  bots that lost their nerve and left a fight: {len(flees)}")
        for f in flees[:5]:
            print("    " + f[:260])
    if spin_loot or spin_other:
        print("\n== turning on the spot (timeline lines with turned >= 150 deg) ==")
        if spin_loot:
            print("  while looting: " + ", ".join(f"{k} {v}" for k, v in spin_loot.most_common(6)))
        if spin_other:
            print("  other layers: " + ", ".join(f"{k} {v}" for k, v in spin_other.most_common(6)))

    if v02:
        print("\n== LiveRaid 0.2-0.4 systems ==")
        for label, *_ in v02_keys:
            lines = v02.get(label)
            if lines:
                print(f"  {label}: {len(lines)}")
                for line in lines[:3]:
                    print("    " + line[:240])

    if health:
        print(f"\n== health ({len(health)} lines, last 3) ==")
        for h in health[-3:]:
            print("  " + h)

    if summaries:
        print("\n== summaries ==")
        for s in summaries[-8:]:
            print("  " + s[:400])

    if bot_filter:
        print(f"\n== {bot_filter} ({len(bot_lines)} lines) ==")
        for line in bot_lines:
            print(line[:400])


if __name__ == "__main__":
    main()
