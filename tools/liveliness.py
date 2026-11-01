"""Индекс живости рейда: как часто что-то происходит рядом с вами, сравнение с целями «онлайн-Таможня».

Читает encounters/<карта>_<время>.csv + .summary.json (LiveRaid 0.12+: строки pulse и people/lobby в population)
и, если есть, coverage/<карта>_<время>.csv (темп ботов для старых рейдов).

  python -X utf8 -I tools/liveliness.py              последний рейд
  python -X utf8 -I tools/liveliness.py --last 5     пять последних рейдов, по строке на рейд
  python -X utf8 -I tools/liveliness.py <stamp|файл> конкретный рейд (bigmap_20261031_170853 или путь к .csv)
  --pace 60-90                                         ориентир темпа ЧВК вне боя, м/мин
"""
import csv
import json
import math
import os
import sys

GAME = r"C:\Games\SPT\BepInEx\plugins\LiveRaid"
ENC = os.path.join(GAME, "encounters")
COV = os.path.join(GAME, "coverage")

# «Онлайн-Таможня» (выбор пользователя, 2026-10-31)
FIRST_CONTACT = (120, 300)      # с
CONTACTS = (4, 6)               # за рейд (20-40 мин); для коротких рейдов пересчёт на 30 мин
SHOTS500_SHARE = 0.8            # доля минут, когда в 500 м была стрельба
SILENCE_MAX = 180               # с
PEOPLE_SHARE = 0.6              # доля лобби до последних 10 минут
PACE = (60, 90)                 # м/мин ЧВК вне боя


def mark(ok):
    return "✔" if ok else "✘"


def fmt_t(sec):
    if sec is None or sec < 0:
        return "никогда"
    return f"{int(sec // 60)}:{int(sec % 60):02d}"


def parse_kv(detail):
    out = {}
    for part in detail.split(";"):
        if "=" in part:
            k, v = part.split("=", 1)
            try:
                out[k] = float(v)
            except ValueError:
                out[k] = v
    return out


def raids():
    if not os.path.isdir(ENC):
        return []
    stamps = [f[:-4] for f in os.listdir(ENC) if f.endswith(".csv")]
    stamps.sort(key=lambda s: os.path.getmtime(os.path.join(ENC, s + ".csv")))
    return stamps


def pace_from_coverage(stamp):
    """Средний м/мин по видам ботов из coverage (все состояния, без фильтра «вне боя»)."""
    path = os.path.join(COV, stamp + ".csv")
    if not os.path.isfile(path):
        return {}
    last = {}
    moved, spent = {}, {}
    with open(path, encoding="utf-8") as f:
        for r in csv.DictReader(f):
            try:
                t = float(r["time"]); x = float(r["x"]); z = float(r["z"])
            except (KeyError, ValueError):
                continue
            key = r["bot"]
            kind = r.get("kind", "?")
            if key in last:
                t0, x0, z0 = last[key]
                dt = t - t0
                d = math.hypot(x - x0, z - z0)
                if 5 <= dt <= 25 and d < 9 * dt:
                    moved[kind] = moved.get(kind, 0) + d
                    spent[kind] = spent.get(kind, 0) + dt
            last[key] = (t, x, z)
    return {k: moved[k] / spent[k] * 60 for k in moved if spent.get(k, 0) > 60}


def analyse(stamp, pace_target):
    csv_path = os.path.join(ENC, stamp + ".csv")
    summary = {}
    sp = os.path.join(ENC, stamp + ".summary.json")
    if os.path.isfile(sp):
        with open(sp, encoding="utf-8") as f:
            summary = json.load(f)

    pop, pulse, contacts_t, first_contact = [], [], [], None
    with open(csv_path, encoding="utf-8") as f:
        for r in csv.DictReader(f):
            try:
                t = float(r["time"])
            except (KeyError, ValueError):
                continue
            ev = r["event"]
            det = r.get("detail", "")
            if ev == "population":
                pop.append((t, parse_kv(det)))
            elif ev == "pulse":
                pulse.append((t, parse_kv(det)))
            elif ev in ("sighting", "bot_sees_you") and (det.startswith("Pmc") or det.startswith("PlayerScav")):
                contacts_t.append(t)
                if first_contact is None:
                    first_contact = t

    raid_s = summary.get("raid_seconds") or (pop[-1][0] if pop else 0)
    res = {"stamp": stamp, "map": stamp.split("_")[0], "minutes": raid_s / 60.0, "new_format": bool(pulse)}

    # Первый контакт и контакты
    fc = summary.get("first_people_contact_seconds", first_contact if first_contact is not None else -1)
    res["first_contact"] = fc
    n_contacts = summary.get("contacts", len(contacts_t))
    res["contacts"] = n_contacts
    per30 = n_contacts * 30 / max(res["minutes"], 1)
    res["contacts_per30"] = per30
    buckets = summary.get("contacts_per_10min")
    if buckets is None:
        buckets = [0] * (int(raid_s // 600) + 1)
        for t in contacts_t:
            buckets[min(int(t // 600), len(buckets) - 1)] += 1
    res["contacts_10"] = buckets

    # Стрельба и тишина (только новый формат)
    if pulse:
        res["shots500_share"] = summary.get("minutes_with_shots_500_share",
                                            sum(1 for _, d in pulse if d.get("shots500", 0) > 0) / len(pulse))
        res["shots"] = (summary.get("shots_per_min_150"), summary.get("shots_per_min_300"), summary.get("shots_per_min_500"))
        res["silence"] = summary.get("longest_silence_seconds", max((d.get("silence", 0) for _, d in pulse), default=0))
        res["silence_at"] = summary.get("longest_silence_ended_at", -1)
    else:
        res["shots500_share"] = None
        res["silence"] = None

    # Люди: ЧВК + игроки-дикие (+ человек) против лобби, с 3-й минуты до последних 10 минут
    lobby = summary.get("lobby_people") or 0
    if not lobby and summary.get("pmc_at_start_planned"):
        lobby = summary["pmc_at_start_planned"] + 1
    curve = []
    for t, d in pop:
        people = d.get("people")
        if people is None:
            people = d.get("pmc", 0) + d.get("pscav", 0) + 1
        lb = d.get("lobby") or lobby
        if lb:
            curve.append((t, people, lb))
    window = [(t, p, lb) for t, p, lb in curve if t > 180 and raid_s - t > 600]
    res["people_curve"] = [(int(t // 60), int(p), int(lb)) for t, p, lb in curve]
    if window:
        res["people_min"] = min(p / lb for _, p, lb in window)
        res["people_under"] = sum(1 for _, p, lb in window if p / lb < PEOPLE_SHARE - 0.005)
        res["people_minutes"] = len(window)
    else:
        res["people_min"] = None

    # Темп
    pmc_pace = summary.get("pmc_m_per_min_calm")
    res["pace_calm"] = pmc_pace if pmc_pace is not None and pmc_pace >= 0 else None
    res["pace_cov"] = pace_from_coverage(stamp)
    res["pace_target"] = pace_target
    res["replacements"] = summary.get("pscav_replacements")
    return res


def report(r):
    print(f"== {r['stamp']}  ({r['minutes']:.0f} мин){'' if r['new_format'] else '  [старый формат: стрельбы и тишины нет]'}")
    fc = r["first_contact"]
    print(f"  первый контакт с людьми   {fmt_t(fc):>8}   цель {fmt_t(FIRST_CONTACT[0])}-{fmt_t(FIRST_CONTACT[1])}  "
          f"{mark(fc is not None and 0 <= fc <= FIRST_CONTACT[1])}")
    print(f"  контактов                 {r['contacts']:>8}   на 30 мин {r['contacts_per30']:.1f}, цель {CONTACTS[0]}-{CONTACTS[1]}  "
          f"{mark(r['contacts_per30'] >= CONTACTS[0])}   по 10 мин: {'/'.join(str(x) for x in r['contacts_10'])}")
    if r["shots500_share"] is not None:
        s150, s300, s500 = r["shots"]
        print(f"  минут со стрельбой в 500 м {r['shots500_share'] * 100:>6.0f} %   цель ≥ {SHOTS500_SHARE * 100:.0f} %  {mark(r['shots500_share'] >= SHOTS500_SHARE)}"
              f"   выстрелов/мин 150/300/500 м: {s150}/{s300}/{s500}")
        print(f"  самая долгая тишина       {fmt_t(r['silence']):>8}   цель ≤ {fmt_t(SILENCE_MAX)}  {mark(r['silence'] <= SILENCE_MAX)}"
              f"   (кончилась на {fmt_t(r['silence_at'])})")
    if r["people_min"] is not None:
        print(f"  людей минимум             {r['people_min'] * 100:>6.0f} %   цель ≥ {PEOPLE_SHARE * 100:.0f} % лобби до последних 10 мин  "
              f"{mark(r['people_min'] >= PEOPLE_SHARE - 0.005)}   ниже цели {r['people_under']} из {r['people_minutes']} мин")
        print("  люди/лобби по минутам:    " + " ".join(f"{m}:{p}/{lb}" for m, p, lb in r["people_curve"][::3]))
    lo, hi = r["pace_target"]
    if r["pace_calm"] is not None:
        print(f"  темп ЧВК вне боя          {r['pace_calm']:>6.0f} м/мин  цель {lo}-{hi}  {mark(r['pace_calm'] >= lo)}")
    if r["pace_cov"]:
        print("  темп по coverage (все состояния): " + ", ".join(f"{k} {v:.0f}" for k, v in sorted(r["pace_cov"].items())) + " м/мин")
    if r["replacements"] is not None:
        print(f"  игроков-диких на замену   {r['replacements']:>8}")


def main(argv):
    pace = PACE
    if "--pace" in argv:
        i = argv.index("--pace")
        lo, hi = argv[i + 1].split("-")
        pace = (int(lo), int(hi))
        del argv[i:i + 2]
    all_raids = raids()
    if not all_raids:
        print("нет encounters/*.csv в", ENC)
        return
    if "--last" in argv:
        n = int(argv[argv.index("--last") + 1])
        pick = all_raids[-n:]
    elif len(argv) > 1:
        arg = os.path.basename(argv[1])
        for suffix in (".summary.json", ".csv"):
            if arg.endswith(suffix):
                arg = arg[: -len(suffix)]
        pick = [arg]
    else:
        pick = all_raids[-1:]
    for stamp in pick:
        if not os.path.isfile(os.path.join(ENC, stamp + ".csv")):
            print("нет рейда", stamp)
            continue
        report(analyse(stamp, pace))


if __name__ == "__main__":
    main(sys.argv)
