**License: CC BY-NC-SA 4.0** — credit Lir, non-commercial, adaptations under the same licence ([text](https://creativecommons.org/licenses/by-nc-sa/4.0/), also `LICENSE.txt` in the archive). QuestingBots, which LiveRaid extends, is under the same licence.  
**🇷🇺 Описание на русском — ниже на этой же странице** ([перейти](#русский)).

> ⚠ **Taking over a bot after death is raw and often breaks.** After the bot you play dies, the textures of the location may stop loading, sound may go wrong and other things may fail — you may have to restart the game. For a stable raid leave *Take over a bot* off in F12 → *After death*. Everything else in the mod works without it.

# English

## What is LiveRaid?

A raid against bots that feels like a raid against players.

In plain SPT the bots are fine at shooting but odd at *living*: squads spawn far apart and march to the same few spots, stand around doing nothing, ignore doors and keys, loot like a vacuum cleaner and leave the raid whenever they feel like it. LiveRaid teaches them to behave the way people in an online raid do — and it does it on top of **QuestingBots, SAIN and LootingBots**, which stay exactly as they are. LiveRaid steers them with patches and puts every switch in one F12 menu.

## Why you might want it

- **The map is full of people, not empty.** Every map gets its own online-sized lobby of PMCs. When people die, player scavs come in to replace them. A *smart population balance* keeps the head count right for the map and the age of the raid — no crowd round you, no empty map — and every raid has its own mood (quiet, usual or busy), so no two raids feel the same.
- **Every bot is someone.** A style (camper, rat, rusher, looter, quester, explorer, hunter, sniper), a skill from newcomer to pro, moods, and squads that play a doctrine (12 of them) with a deck of 18 tactics: flanks, pincers, peeking to draw fire, smoke, bounds under cover, revenge for a fallen mate. Some squads are smart, some forget about cover — like online.
- **They move like players.** Hot zones, key places, a hot drop to the richest spot, "people's corridors", bots spread over the whole map, no idling, no ten-minute stands in one corner.
- **Real keys and doors.** Bots carry real keys, open locked doors and containers, close doors behind them, and some carry the key to the richest place of the map.
- **They loot, fight, heal and leave like a player.** They weigh value against danger, search a whole room, clean up after a fight, carry out a fallen mate's kit; they have morale and fight roles, choose a gun for the range, forget a cold fight; they bandage at the first quiet moment, put on splints, break treatment off under fire; they leave at their own time, wait crouched at the exit, leave as a squad.
- **It repairs EFT's own AI bugs.** Stuck hands, reload flags, magazines thrown away, friendly fire inside a squad, bots frozen under fire.
- **Free camera after death and an observer mode** — watch the raid play out without being in it.
- **Bots go absolutely everywhere.** With LiveRaid the bots travel and loot over the WHOLE map — every district, every floor, every room that has loot — not just the usual few spots. Switch on the recording of your own route and the bots start walking the routes you walk yourself.

## How much does it change the bots — and why?

**A lot — it is not a tweak.** LiveRaid changes how bots spawn, where they go, what they do on the way, how squads think in a fight, how they loot, heal and leave. The aim is simple: a raid should feel like online, where you can meet anyone anywhere, nobody waits for you in a corner and the map is alive for the whole 40 minutes.

What it does **not** do: it does not make bots aim better, see through walls or cheat. Shooting is still SAIN's job. It does not add bosses (they stay as they were) and it never pushes the number of scavs above the map's own limit.

You choose how deep it goes:

| Preset | In short |
|---|---|
| **Recommended** | everything on, as the author plays it |
| **Weak PC** | far bots simplified and put to sleep, fewer bots, no detailed logs |
| **Quiet raids** | fewer bots and PMCs, calmer map |
| **Busy raids** | more bots and PMCs, more reserves, more fights |
| **Fixes only** | all of LiveRaid's behaviour off — only the repairs of EFT's AI bugs and healing stay |

Each pack (keys and doors, map life, characters, extraction, spawns, loot, squads, fight, healing, fixes, quests, routes) can also be switched off on its own.

## The F12 menu, section by section

Open **F12 → LiveRaid**. At the top: *Language* (Auto follows the game), *Preset*, and a switch that shows every single setting (*Developer mode*). Below, the sections:

1. **Main** — the master switch, how detailed the logs are, the raid summary; tools for bug reports. Leave as it is.
2. **Keys and doors** — how many keys bots carry (few / like online / many), prize keys (the key to the richest place), help at doors and corners, opening locked containers, closing doors behind them.
3. **Life on the map** — how bots move: hot zones, key places, hot drop, people's corridors, spreading over the map, tempo, the "evict those who sit too long" watchdog, patrolling scavs, player scavs' short raids, airdrop fights.
4. **Characters and extraction** — play styles and skill spread, moods; when bots leave, switch exits, paid exits (cars), waiting at the exit, leaving as a squad.
5. **Spawns and lobby** — the online-sized lobby, extra PMCs, reserves, player scavs on a schedule and as replacements, the **smart population balance** (density, how closely it follows the map's lobby, the crowd cap round you), spawn distance, spreading squads over the sides of the map.
6. **Loot** — thinking before looting, no looting in danger, clean-up after a fight, searching a room, dropping cheap things for a find, shedding weight to run, loot speed, looting lying down (a test feature).
7. **Squads** — roles, staying together, helping a mate, recovering a fallen mate's kit, sharing meds and loot, formations, voice calls, **doctrines** and the **tactics deck**, flanks, grenades, smoke, night discipline.
8. **Combat** — morale, leaving a lost fight, forgetting a cold fight, responding to gunfire and noise, return fire, fighting at the gun's range, the "fight like a player" habits, hearing range.
9. **Healing, weapons and ammo** — healing at the first quiet moment, broken legs, surgery, weapon swaps, magazines, ammo from nowhere (off in plain SPT), and the repairs of EFT bugs.
10. **Quests and routes** — EFT quests for PMCs, checks and fixes of quest points, recorded routes over obstacles, the route recorder.
11. **Performance** — simplify distant bots, QuestingBots' sleeping of far bots, AI-versus-AI at a distance. *Weak PC* sets these for you.
12. **After death** — the free camera, observer mode, following and taking over bots (**raw, see the warning at the top**), camera keys.

Every setting has a tooltip. Anything that needs a game restart says so.

## Install

1. **Install the requirements first** (below) and make sure SPT 4.1.6 runs.
2. **Close the game and stop the SPT server.**
3. Download the archive: **`LiveRaid-<version>.zip`** for single player, or **`LiveRaid-Online-<version>.zip`** for co-op through Project Fika.
4. Unpack it **into the game's root folder** (where `EscapeFromTarkov.exe` and `BepInEx` are), agreeing to replace files. It contains the client plugin (`BepInEx/plugins/LiveRaid`), the server mod (`SPT_Runtime/user/mods/LiveRaid`, or `SPT/user/mods/LiveRaid` in the older layout), and `LiveRaid Launcher.exe` with its `LiveRaid Launcher` folder.
5. Start the server and the game. Open **F12 → LiveRaid**, pick a preset (or leave *Recommended*).
6. **Check:** `BepInEx/LogOutput.log` has `LiveRaid <version> loaded` with no `disabled: …` after it.

Updating: the same way over the old files; your settings, map caches and recorded routes are kept. Removing: delete `BepInEx/plugins/LiveRaid`, `SPT_Runtime/user/mods/LiveRaid`, `LiveRaid Launcher.exe` and the `LiveRaid Launcher` folder.

## Requirements and compatibility

SPT **4.1.6** (EFT 0.16.9) and these mods (the versions LiveRaid is built and tested with): **QuestingBots 1.2.0**, **SAIN 4.5.1**, **LootingBots 1.8.0**, **BigBrain 1.5.0**, **Waypoints 1.9.0**. **LiveRaid Online** also needs **Project Fika 2.4.x** with **QuestingBots Fika sync**.

Not compatible with **ORBIT** — remove it. Other bot mods that change the same things (spawns, quests, looting AI) will fight with LiveRaid; use one of them.

## Two editions and the launcher

**LiveRaid** is for single player. **LiveRaid Online** is for co-op through Fika: the bots live on the host, and the host's settings apply to everybody (F12 is locked for the others). **LiveRaid Launcher** switches between the two editions and between the stable and the test branch of the mod in one click; it also starts the server and the game folder.

## Known problems

- **Taking over a bot is raw** — see the warning at the top.
- Part of the newest behaviour (population balance, standoff breaker, quick idle watchdog) was written from the logs of a single raid and has not yet been through many long ones. If bots do something silly, please report it.

## Reporting a problem

Say what you did, which map and which minute, and attach `BepInEx/LogOutput.log` (and, if you can, the newest file from `BepInEx/plugins/LiveRaid/logs`). Source code and issues: https://github.com/Lir-byte/LiveRaid

## Licence and credits

By **Lir**. Licensed CC BY-NC-SA 4.0 (credit, non-commercial, share alike). LiveRaid contains no code of the mods it extends. Thanks to the authors of QuestingBots (DanW), SAIN (Solarint), LootingBots (Skwizzy), BigBrain and Waypoints (DrakiaXYZ), and to the SPT and Fika teams.

---

# Русский

## Что такое LiveRaid?

Рейд против ботов, который ощущается как рейд против игроков.

В обычном SPT боты неплохо стреляют, но странно *живут*: отряды появляются далеко друг от друга и идут в одни и те же места, стоят без дела, не замечают дверей и ключей, лутают как пылесос и уходят с карты когда вздумается. LiveRaid учит их вести себя как люди в онлайн-рейде — поверх **QuestingBots, SAIN и LootingBots**, которые остаются как есть. LiveRaid управляет ими патчами, а все выключатели собраны в одном меню F12.

## Чем он хорош

- **Карта полна людей, а не пуста.** На каждой карте своё онлайн-лобби ЧВК. Когда люди гибнут, им на замену приходят игроки-дикие. «Умный баланс численности» держит число ботов под карту и время рейда: не толпа вокруг вас и не пустыня; к тому же у каждого рейда свой настрой (тихий, обычный, людный) — два рейда не похожи.
- **У каждого бота своё лицо.** Стиль (кемпер, крыса, рашер, лутер, квестер, исследователь, охотник, снайпер), навык от новичка до профи, настроение; отряды играют по доктрине (их 12) и по колоде из 18 тактик: фланги, клещи, выглядывание, дым, перебежки с прикрытием, месть за павшего. Одни отряды умные, другие забывают про укрытие — как в онлайне.
- **Ходят как игроки.** Горячие зоны, ключевые места, хот-дроп в самое богатое место, «коридоры людей», боты распределены по всей карте, без простоя и десятиминутных стояний в углу.
- **Настоящие ключи и двери.** У ботов настоящие ключи, они открывают запертые двери и контейнеры, закрывают двери за собой; у некоторых — ключ от самого богатого места карты.
- **Лутают, дерутся, лечатся и уходят как игрок.** Ценность против опасности, обыск всей комнаты, зачистка после боя, вынос снаряжения павшего товарища; мораль и роли в бою, оружие под дистанцию, остывший бой забывается; перевязка в первую тихую минуту, шины, лечение прерывается под огнём; уход в своё время, ожидание у выхода присев, уход отрядом.
- **Чинит ошибки ИИ самой EFT.** Зависшие руки, флаги перезарядки, выброшенные магазины, огонь по своим, замирание под огнём.
- **Свободная камера после смерти и режим наблюдателя** — смотреть рейд, не участвуя в нём.
- **Боты ходят и лутают абсолютно по всей карте.** С LiveRaid боты путешествуют и лутают по ВСЕЙ карте — каждый район, каждый этаж, каждая комната с лутом, а не пара привычных мест. Включите запись своего маршрута — и боты начнут ходить по тем путям, которыми ходите вы.

## Насколько сильно он меняет ботов — и зачем

**Сильно, это не мелкая правка.** LiveRaid меняет, как боты появляются, куда идут, что делают по пути, как отряды думают в бою, как лутают, лечатся и уходят. Цель простая: рейд должен ощущаться как онлайн, где можно встретить кого угодно где угодно, никто не ждёт вас в углу, а карта жива все 40 минут.

Чего он **не** делает: не улучшает прицеливание, не даёт видеть сквозь стены, не читерит. Стрельба — по-прежнему дело SAIN. Он не добавляет боссов (они остаются как были) и не поднимает число диких выше собственного лимита карты.

Глубину выбираете вы:

| Пресет | Коротко |
|---|---|
| **Рекомендуемый** | всё включено, как играет автор |
| **Слабый ПК** | дальние боты упрощены и усыплены, ботов меньше, без подробных журналов |
| **Тихие рейды** | меньше ботов и ЧВК, спокойнее |
| **Людные рейды** | больше ботов и ЧВК, больше запасных, чаще бои |
| **Только исправления** | всё поведение LiveRaid выключено, остаются исправления ошибок ИИ EFT и лечение |

Каждый пакет (ключи и двери, жизнь на карте, характеры, эвакуация, спавн, лут, отряды, бой, лечение, исправления, квесты, маршруты) можно отключить отдельно.

## Меню F12 по разделам

Откройте **F12 → LiveRaid**. Вверху: *Язык* (Авто — как в игре), *Готовые наборы настроек* (пресеты) и переключатель, который показывает каждую настройку отдельно («Режим разработчика»). Ниже — разделы:

1. **Основное** — главный выключатель, подробность журналов, итог рейда, инструменты для отчётов об ошибках. Обычно не трогать.
2. **Ключи и двери** — сколько ключей у ботов (мало / как онлайн / много), призовые ключи (от самого богатого места), помощь у дверей и углов, отпирание контейнеров, закрывание дверей за собой.
3. **Жизнь на карте** — как боты ходят: горячие зоны, ключевые места, хот-дроп, коридоры людей, распределение по карте, темп, «выгонять засидевшихся», патруль диких, короткие рейды игроков-диких, схватки у аирдропа.
4. **Характеры и эвакуация** — стили и разброс навыка, настроение; когда боты уходят, выходы с рубильником, платные выходы (машины), ожидание у выхода, уход отрядом.
5. **Спавн и лобби** — онлайн-лобби, добавочные ЧВК, запасные, игроки-дикие по расписанию и на замену, **умный баланс численности** (густота, насколько следовать лобби карты, потолок толпы рядом с вами), расстояние при появлении, разнос отрядов по сторонам карты.
6. **Лут** — подумать перед лутом, не лутать в опасности, зачистка после боя, обыск комнаты, выброс дешёвого ради находки, сброс веса, скорость лута, лут лёжа (тестовая функция).
7. **Отряды** — роли, держаться вместе, помощь товарищу, вынос снаряжения павшего, обмен аптечками и лутом, строй, голосовые команды, **доктрины** и **колода тактик**, фланги, гранаты, дым, ночная дисциплина.
8. **Бой** — мораль, уход из проигранного боя, забывание остывшего, реакция на стрельбу и шум, ответный огонь, бой на дистанции оружия, привычки «как у игрока», слышимость.
9. **Лечение, оружие и патроны** — лечение в первую тихую минуту, сломанные ноги, хирургия, смена оружия, магазины, патроны из воздуха (в SPT выключены), исправления ошибок EFT.
10. **Квесты и маршруты** — квесты EFT для ЧВК, проверка и исправление точек квестов, записанные маршруты через завалы, запись маршрутов.
11. **Производительность** — упрощать дальних ботов, усыпление дальних ботов QuestingBots, «ИИ против ИИ» вдали. Пресет *Слабый ПК* ставит их сам.
12. **После смерти** — свободная камера, режим наблюдателя, слежение и вселение в ботов (**сырое, см. предупреждение выше**), клавиши камеры.

У каждой настройки есть подсказка; всё, что требует перезапуска игры, помечено.

## Установка

1. **Сначала поставьте нужные моды** (ниже) и убедитесь, что SPT 4.1.6 запускается.
2. **Закройте игру и остановите сервер SPT.**
3. Скачайте архив: **`LiveRaid-<версия>.zip`** для одиночной игры или **`LiveRaid-Online-<версия>.zip`** для кооператива через Project Fika.
4. Распакуйте **в корень папки игры** (где `EscapeFromTarkov.exe` и `BepInEx`), согласившись на замену файлов. В архиве клиентский плагин (`BepInEx/plugins/LiveRaid`), серверный мод (`SPT_Runtime/user/mods/LiveRaid`, а в старой раскладке — `SPT/user/mods/LiveRaid`) и `LiveRaid Launcher.exe` с папкой `LiveRaid Launcher`.
5. Запустите сервер и игру. Откройте **F12 → LiveRaid**, выберите пресет (или оставьте «Рекомендуемый»).
6. **Проверка:** в `BepInEx/LogOutput.log` есть `LiveRaid <версия> loaded` без `disabled: …` после неё.

Обновление: так же, поверх старых файлов; настройки, кэш карт и записанные маршруты сохраняются. Удаление: удалите `BepInEx/plugins/LiveRaid`, `SPT_Runtime/user/mods/LiveRaid`, `LiveRaid Launcher.exe` и папку `LiveRaid Launcher`.

## Требования и совместимость

SPT **4.1.6** (EFT 0.16.9) и моды (версии, с которыми LiveRaid собран и проверен): **QuestingBots 1.2.0**, **SAIN 4.5.1**, **LootingBots 1.8.0**, **BigBrain 1.5.0**, **Waypoints 1.9.0**. Для **LiveRaid Online** ещё **Project Fika 2.4.x** и **QuestingBots Fika sync**.

С **ORBIT** не совместим — удалите. Другие моды, меняющие то же самое (спавн, квесты, ИИ лута), будут конфликтовать с LiveRaid; оставьте один.

## Две редакции и лаунчер

**LiveRaid** — для одиночной игры. **LiveRaid Online** — для кооператива через Fika: боты живут у хоста, настройки задаёт хост (у остальных F12 заблокирован). **LiveRaid Launcher** переключает редакции и ветку мода (стабильная / тестовая) одним нажатием, а ещё запускает сервер и открывает папку игры.

## Известные проблемы

- **Вселение в бота сырое** — см. предупреждение вверху страницы.
- Часть новейшего поведения (баланс численности, выход из тупика, быстрый сторож простоя) написана по логам одного рейда и ещё не прошла много длинных. Если бот делает глупость, сообщите.

## Сообщить о проблеме

Опишите, что делали, на какой карте и в какую минуту, и приложите `BepInEx/LogOutput.log` (и, если можете, самый свежий файл из `BepInEx/plugins/LiveRaid/logs`). Исходный код и обращения: https://github.com/Lir-byte/LiveRaid

## Лицензия и благодарности

Автор — **Lir**. Лицензия CC BY-NC-SA 4.0 (с указанием автора, не для коммерции, на тех же условиях). Кода модов, которые LiveRaid расширяет, в нём нет. Спасибо авторам QuestingBots (DanW), SAIN (Solarint), LootingBots (Skwizzy), BigBrain и Waypoints (DrakiaXYZ), командам SPT и Fika.
