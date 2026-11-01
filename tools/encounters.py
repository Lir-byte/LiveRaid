"""Aggregate LiveRaid encounter summaries (BepInEx/plugins/LiveRaid/encounters/*.summary.json) per map.

Usage: python -I encounters.py [summary files...]
Prints, per map: raids, PMCs at start, how many hostile PMC groups you saw per raid, when the first one showed up,
how often bots spotted you, gunfire reactions, hides, exit camps. Compare with what a real online raid feels like and tune.
"""
import collections, glob, json, os, statistics, sys

SPT = r"C:\Games\SPT"


def med(values):
    values = [v for v in values if v is not None and v == v and v >= 0]
    return round(statistics.median(values)) if values else None


def mmss(seconds):
    return "never" if seconds is None else f"{int(seconds // 60)}:{int(seconds % 60):02d}"


def main():
    files = sys.argv[1:] or glob.glob(os.path.join(SPT, "BepInEx", "plugins", "LiveRaid", "encounters", "*.summary.json"))
    by_map = collections.defaultdict(list)
    for f in files:
        try:
            d = json.load(open(f, encoding="utf-8"))
            by_map[d["map"]].append(d)
        except (OSError, ValueError, KeyError):
            print("skipped", f)

    if not by_map:
        print("no summaries yet; play a raid with 'Encounter log' on")
        return

    for location, all_raids in sorted(by_map.items()):
        # Raids that ended in the first 3 minutes (a quick death, a restart) say nothing about the lobby: left out of the averages
        raids = [r for r in all_raids if (r.get("raid_seconds") or 0) >= 180]
        short = len(all_raids) - len(raids)
        if not raids:
            print(f"{location}: {len(all_raids)} raid(s), all shorter than 3 min - nothing to average")
            continue

        n = len(raids)

        def avg(key):
            # -1 = not measured in that raid (e.g. the raid did not reach minute 2)
            values = [r.get(key) for r in raids if r.get(key) is not None and r.get(key) != -1]
            return sum(values) / len(values) if values else 0
        reactions = collections.Counter()
        for r in raids:
            reactions.update(r.get("gunfire_reactions", {}))
        print(f"{location}: {n} raid(s){f' (+{short} shorter than 3 min left out)' if short else ''}, avg length {avg('raid_seconds') / 60:.0f} min")
        print(f"  PMCs: planned at start {avg('pmc_at_start_planned'):.1f}, alive at 2 min {avg('pmc_alive_at_2min'):.1f}, max population {avg('max_population'):.1f}")
        print(f"  you see: {avg('pmc_groups_seen_by_you'):.1f} PMC groups per raid, first sighting (median) {mmss(med([r.get('first_sighting_seconds') for r in raids]))}, "
              f"first bot to spot you {mmss(med([r.get('first_bot_saw_you_seconds') for r in raids]))}, spotted {avg('bots_saw_you_events'):.1f} times")
        print(f"  fights: you killed {avg('you_killed'):.1f}, died {avg('you_died'):.1f}, bots killed each other {avg('bot_vs_bot_kills'):.1f}")
        print(f"  gunfire: heard {avg('gunfire_fights_heard'):.1f} fights, {avg('gunfire_responses'):.1f} responses {dict(reactions)}; "
              f"hides {avg('hides'):.1f}, exit camps {avg('exit_camps'):.1f}")


if __name__ == "__main__":
    main()
