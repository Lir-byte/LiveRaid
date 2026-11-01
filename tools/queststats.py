"""How well bots do their quests, from a LiveRaid trace (Log detail = Detailed / Full).

python -I tools/queststats.py [trace] [--bots]

Per kind (Pmc / Scav / PlayerScav): share of time in QuestingBots' objective layer, quest steps completed per bot-minute ("completed a step"
lines of the quest ledger), how the held quests split by type (LR Explore areas, LR Hot key places, LR Locked rooms, EFT / QuestingBots
quests), how many different quests and how many bots shared one, and QuestingBots' decisions. --bots adds a line per bot.
Session 23 (2026-10-27): PMCs did 0.07-0.13 steps a minute against 0.8-1.2 for scavs - squad leaders stuck between quest, regroup and loot.
"""
import collections
import os
import re
import sys

LOGS = r"C:\Games\SPT\BepInEx\plugins\LiveRaid\logs"
TL = re.compile(r'^(\d\d):(\d\d) \| timeline \| (.+?) \[(\w+)/([^\]]*)\] layer=(\S+(?: : \S+ Layer)?)(.*)$')
QUEST = re.compile(r'quest="([^"]*)"')
QB = re.compile(r'qb=(\S+)')
STEP = re.compile(r'^(\d\d):(\d\d) \| quest \| (.+?) \((\w+), (\w+)\) completed a step of "([^"]*)" after (\d+) s')


def kind_of(quest):
    if quest.startswith('LR Explore'):
        return 'explore'
    if quest.startswith('LR Locked'):
        return 'locked'
    if quest.startswith('LR Hot'):
        return 'hot'
    return 'eft/qb'


def main():
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    args = [a for a in sys.argv[1:] if not a.startswith('--')]
    per_bot = '--bots' in sys.argv
    if args:
        path = args[0]
    else:
        files = sorted((os.path.join(LOGS, f) for f in os.listdir(LOGS) if f.endswith(".log")), key=os.path.getmtime)
        path = files[-1]
    print("file:", path)

    bots = collections.defaultdict(lambda: {'kind': '', 'samples': 0, 'obj': 0, 'quests': collections.Counter(), 'qb': collections.Counter(), 'steps': collections.Counter()})
    for line in open(path, encoding='utf-8', errors='replace'):
        m = TL.match(line)
        if m:
            b = bots[m[3]]
            b['kind'] = m[4]
            if b['kind'] == 'Other':
                continue
            b['samples'] += 1
            q = QUEST.search(m[7])
            qb = QB.search(m[7])
            if q:
                b['quests'][q[1]] += 1
            if qb:
                b['qb'][qb[1]] += 1
            if m[6] == 'BotObjectiveLayer':
                b['obj'] += 1
            continue
        m = STEP.match(line)
        if m:
            bots[m[3]]['steps'][m[6]] += 1

    for kind in ('Pmc', 'Scav', 'PlayerScav'):
        sel = {n: b for n, b in bots.items() if b['kind'] == kind and b['samples'] > 0}
        if not sel:
            continue
        print(f'\n===== {kind}: {len(sel)} bots')
        samples = sum(b['samples'] for b in sel.values())
        obj = sum(b['obj'] for b in sel.values())
        steps = sum(sum(b['steps'].values()) for b in sel.values())
        # Timeline every 5 s at Full, 15 s at Detailed: the share is what counts, minutes are approximate
        minutes = samples * 5 / 60
        held = collections.Counter()
        done = collections.Counter()
        holders = collections.Counter()
        qb = collections.Counter()
        for n, b in sorted(sel.items(), key=lambda kv: -kv[1]['samples']):
            for q, c in b['quests'].items():
                held[kind_of(q)] += c
                holders[q] += 1
            for q, c in b['steps'].items():
                done[kind_of(q)] += c
            qb.update(b['qb'])
            if per_bot:
                st = sum(b['steps'].values())
                print(f"  {n[:22]:22} {b['samples'] * 5 / 60:4.1f} min  objective {100 * b['obj'] / b['samples']:3.0f}%  steps {st:3d}  quests {len(b['quests'])}: "
                      + ', '.join(f'{q}({c})' for q, c in b['quests'].most_common(5)))
        print(f"  objective layer {100 * obj / samples:.0f}% of the time; steps done {steps} in ~{minutes:.0f} bot-min = {steps / max(minutes, 0.1):.2f} a minute")
        print('  time holding a quest, by type:', dict(held))
        print('  steps done, by type:', dict(done))
        print(f'  different quests {len(holders)}; held by several bots:', [(q, c) for q, c in holders.most_common(6) if c > 1])
        print('  QuestingBots decisions:', qb.most_common(8))


if __name__ == "__main__":
    main()
