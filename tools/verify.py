"""Which LiveRaid mechanics showed up in a raid, and how often - from the raid's trace (and the BepInEx log).

    python -X utf8 -I tools/verify.py [trace]          (default: the newest BepInEx/plugins/LiveRaid/logs/*.log)

Every mechanic has the log markers it writes when it acts. The table says how many times each one acted, and for those that did not,
what it needs (so "not seen" can be told from "broken"). Errors, the raid summary and its "so far" snapshots are read too.
Needs Log detail = Detailed or Full for the trace (the Info lines alone also go to LogOutput.log).
"""
import glob
import os
import re
import sys

GAME = r"C:/Games/SPT/BepInEx"

# (group, mechanic, regex of its marker, what it needs to show up)
CHECKS = [
    ("Отряды", "Доктрины отрядов", r' play "', "ЧВК в рейде"),
    ("Отряды", "Личная версия доктрины", r"own take on", "ЧВК в рейде"),
    ("Отряды", "Колода тактик (вызовы)", r' calls "', "бой отряда ЧВК из 2+"),
    ("Отряды", "Боец пошёл своим путём", r"goes his own way instead of", "вызовы тактик"),
    ("Отряды", "Выглядывание-провокация", r'calls "выглядывание-провокация"|peeks out to draw fire', "бой отряда, враг не на виду, 12-80 м"),
    ("Отряды", "Разворот на третью сторону", r'calls "разворот на третью сторону"', "попадание по бойцу отряда с нового направления"),
    ("Отряды", "Дымовая завеса", r"throws a smoke", "дымовая граната у бойца"),
    ("Отряды", "«Стоп, слышал»", r"stops and listens to the fight", "отряд услышал далёкий бой"),
    ("Отряды", "Доклад о контакте", r"calls contact:|sees .* and says nothing", "отряд увидел врага"),
    ("Отряды", "Сбор после боя", r"stops \d+ s after the fight for the squad to gather", "отряд пережил бой"),
    ("Отряды", "Месть / шок за павшего", r'calls "месть за своего"|the squad is shaken', "гибель бойца отряда"),
    ("Отряды", "Бегство последнего из отряда", r"is the last of its squad after", "погиб весь отряд, кроме одного"),
    ("Отряды", "Лидер прикрывает лутающих", r"covers .* while he loots", "лидер ждёт лутающих (Detailed)"),
    ("Отряды", "Охота на боссов", r"LR Boss:", "босс на карте и отряд-охотник"),
    ("Отряды", "Высота для длинного ствола", r"takes the high ground", "отряд с марксманкой/снайперкой в движении"),
    ("Отряды", "Тайник лута друга", r"hid \d+ of .* thing\(s\)", "гибель бойца, что-то не влезло"),
    ("Бой", "Смена угла после выстрелов", r"takes another angle on", "перестрелка (Detailed)"),
    ("Бой", "Бег в укрытие на открытом", r"is hit in the open by", "попадание на открытом месте (Detailed)"),
    ("Бой", "Тихие не трогают диких", r"leaves the scav .* alone", "лутер/крыса/тихий отряд рядом с диким (Detailed)"),
    ("Бой", "Гранаты по привычке", r"throws a grenade", "бой с невидимым врагом (Detailed)"),
    ("Бой", "Стимулятор перед штурмом", r"uses a stim before going in", "штурмовой вызов и стимулятор (Detailed)"),
    ("Бой", "Реакция на шум", r"LR Noise:", "враг шумит рядом со спокойным ботом"),
    ("Бой", "Реакция на стрельбу", r"LR Gunfire:", "далёкий бой"),
    ("Бой", "Пистолет заряжен в затишье", r"draws it in a lull to reload it", "пустой пистолет в кобуре (Detailed)"),
    ("Бой", "Пустой пистолет вблизи - перезарядка", r"reloads it instead of taking its", "пистолет опустел в ближнем бою (Detailed)"),
    ("Бой", "Нет DogFight без пути", r"no dogfight with .* \(no path\)", "у SAIN не построился путь до врага (Detailed)"),
    ("Бой", "Кусты между ботами", r"sight checks through bushes or grass [1-9]", "боты рядом друг с другом в кустах (итог рейда)"),
    ("Характер", "Кураж после серии убийств", r"is on a kill streak", "2+ убийства за 10 мин у «куражного»"),
    ("Характер", "Испуг после почти смерти", r"lived through a close call", "hp < 25 % и выжил"),
    ("Характер", "Месть одиночки", r"goes looking for .* who shot it", "попадание по «мстительному» и затишье"),
    ("Движение", "Голова на шарнире", r"glances about while watchful [1-9]", "насторожённый бот на ходу (итог рейда)"),
    ("Движение", "Осмотр перед открытым местом", r"looks over .* of open ground", "открытый участок впереди (Detailed)"),
    ("Движение", "Обход открытого места", r"keeps to cover:", "открытый участок 40+ м (Detailed)"),
    ("Движение", "Двери: закрыть за собой", r"shuts the door behind it", "вход в здание (Detailed)"),
    ("Движение", "Двери: послушать у закрытой", r"at a closed door to listen", "закрытая дверь на пути (Detailed)"),
    ("Движение", "Застрявшая дверь распахнута", r"swung open", "бот уткнулся в дверь"),
    ("Движение", "Отход при застревании на пути QB", r"\(QuestingBots walk\) backs off", "бот застрял на пути квеста/лута"),
    ("Движение", "Распределение по карте", r"LR Balance: .* bots; quarters", "любой рейд (Detailed)"),
    ("Дикие", "Патруль района", r"patrols its area", "ИИ-дикие, режим патруля"),
    ("Дикие", "Обход района без заданий", r"walks its beat to", "патрульному дикому нечего делать в районе"),
    ("Дикие", "Зов соседей", r"calls the others of its area", "дикий в бою"),
    ("Дикие", "Игроки-дикие: вход и рейд", r"LR PScav: .* comes in", "игроки-дикие в рейде"),
    ("Дикие", "Игроки-дикие: к телам ЧВК", r"runs for .* body", "тело ЧВК рядом с игроком-диким"),
    ("Ключи и лут", "Лут со своей стороны стены", r"loots it from \(|can only be reached through a wall|instead of looting through it", "шкаф/ящик у тонкой стены (Detailed)"),
    ("Ключи и лут", "Сбросить лишнее, чтобы бежать", r"to run again", "ЧВК перегружен (вес выше предела EFT) в затишье"),
    ("Ключи и лут", "Найденный ключ", r"found the key to", "ключ снят с тела/найден"),
    ("Ключи и лут", "Призовой ключ", r"prize place", "ЧВК с призовым ключом"),
    ("Выходы", "Карточка выходов отряда", r"its exits this raid", "отряды ЧВК (с 0.10.8)"),
    ("Выходы", "Почему нет карточки выходов", r"no exit card for", "у EFT нет точек выхода/стороны при активации (Detailed)"),
    ("Выходы", "Ждать у выхода присев", r"crouched, watching the way it came", "бот дошёл до точки выхода (Detailed)"),
    ("Выходы", "Платная машина уезжает", r"pays for the car at", "бот уходит на платной машине"),
    ("Живость", "Итог замера живости", r"LR Pulse on", "конец рейда (журнал встреч включён)"),
    ("Живость", "Игроки-дикие на замену", r"LR PScav: .* to replace", "«людей» меньше доли лобби, не последние 10 мин"),
    ("Живость", "Игроки-дикие со стороны боя", r"player scavs on the side of the fight", "замена после громкого боя (Detailed)"),
    ("Живость", "Коридоры людей построены", r"LR Corridors on", "карта готова (MapIntel)"),
    ("Живость", "Путь по коридору людей", r"goes by the people's corridor", "дальний путь ЧВК мимо коридоров (Detailed)"),
    ("Живость", "Уход из обысканного района", r"leaves the region at .* searched before", "ЧВК 2-3 мин без лута в обысканном районе"),
    ("Баланс", "Численность: старт под карту", r"LR Population: balance on", "начало рейда: настрой, ЧВК старт, ИИ-дикие"),
    ("Баланс", "Численность: толпа у человека", r"LR Population: \d+ bots within", "у человека/камеры слишком много ботов"),
    ("Баланс", "Численность: волны диких придержаны", r"scav waves held back", "ИИ-дикие выше потолка или толпа"),
    ("Баланс", "Численность: отчёт раз в 5 мин", r"LR Population: \S+ at \d+ min", "люди/дикие/толпа/тишина карты"),
    ("Баланс", "Тупик за стеной закончен", r"a standoff, the bot goes back", "враг рядом 30-60 с, не виден и не стреляет"),
    ("Баланс", "Быстрый сторож простоя", r"has stayed 2\.\d min within 45 m", "спокойный бот 2,5 мин в круге 45 м отправлен дальше"),
    ("Баланс", "Шаг квеста без прогресса", r"is no closer than .* to a step", "2,5 мин не ближе к шагу квеста"),
    ("Выходы", "Выход у точки (1-4 м)", r"cannot get closer - at the exit", "бот упёрся рядом с точкой выхода"),
    ("Выходы", "Платные и особые выходы", r"a paid or conditional exit", "машина/выход с условием на стороне"),
    ("Выходы", "Ранний уход", r"EARLY:", "причины уйти раньше"),
    ("Выходы", "Вышли из рейда", r"left the raid through", "время ухода"),
    ("События", "Аирдроп", r"LR Airdrop:", "сброс аирдропа"),
    ("События", "Ночь: фонарь выключен", r"turns its light off", "ночной рейд, враг/шум рядом (Detailed)"),
    ("События", "Ночь: свет в глаза вблизи", r"switches its light on up close", "ночной рейд, бой до 70 м у «слепящего» (Detailed)"),
]


def newest_trace():
    files = sorted(glob.glob(os.path.join(GAME, "plugins", "LiveRaid", "logs", "*.log")), key=os.path.getmtime)
    return files[-1] if files else None


def main():
    trace = sys.argv[1] if len(sys.argv) > 1 else newest_trace()
    if not trace or not os.path.exists(trace):
        print("no trace found (F12 -> Log detail = Detailed)")
        return 1

    text = open(trace, encoding="utf-8", errors="replace").read()
    lines = text.splitlines()
    head = lines[0] if lines else ""
    last = next((ln.split(" | ")[0] for ln in reversed(lines) if re.match(r"^\d+:\d\d \|", ln)), "?")
    print(f"trace: {trace}")
    print(f"{head}\nraid time in the trace: up to {last}\n")

    summary = [ln for ln in lines if "LR Health on" in ln]
    snapshots = [ln for ln in lines if "LR Health so far" in ln]
    log_path = os.path.join(GAME, "LogOutput.log")
    if not summary and os.path.exists(log_path):
        log = open(log_path, encoding="utf-8", errors="replace").read()
        summary = [ln for ln in log.splitlines() if "LR Health on" in ln][-1:]
    errors = [ln for ln in lines if "| error |" in ln or "| ERROR |" in ln]

    print("== итог рейда ==")
    if summary:
        cut = "закрыта игра" if "the game was closed" in summary[-1] else "конец рейда"
        print(f"  есть ({cut})")
    elif snapshots:
        print(f"  нет итога; последний промежуточный итог: {len(snapshots)} шт.")
    else:
        print("  нет (рейд не закончен, и промежуточных итогов нет)")
    print(f"  ошибок LiveRaid в трассе: {len(errors)}")
    for e in errors[:5]:
        print("   ", e[:200])
    print()

    group = None
    seen = missing = 0
    haystack = text + ("\n" + summary[-1] if summary else "") + ("\n" + snapshots[-1] if snapshots else "")
    for g, name, rx, needs in CHECKS:
        if g != group:
            group = g
            print(f"== {g} ==")
        n = len(re.findall(rx, haystack))
        if n:
            seen += 1
            print(f"  ✔ {name}: {n}")
        else:
            missing += 1
            print(f"  — {name}: не встретилось (нужно: {needs})")
    print(f"\nсработало {seen} из {seen + missing} механик")
    return 0


if __name__ == "__main__":
    sys.exit(main())
