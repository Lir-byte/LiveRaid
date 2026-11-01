# LiveRaid - bots that play like people online (SPT 4.1.6)

**Version 1.0.1** · by **Lir** · [CC BY-NC-SA 4.0](LICENSE)

LiveRaid does **not** replace QuestingBots, SAIN or LootingBots. They keep doing what they do - walk quests, shoot, pick things up - and I added the part none of them has: **who each bot is, what it carries, and what it decides to do with the raid around it.** It sits on top of them (no code of theirs inside) and keeps every switch in one F12 menu. Same goal as ORBIT, different way: ORBIT replaces those mods, LiveRaid extends them.

Two editions from one source:

| Edition | For |
|---|---|
| **LiveRaid** | single player (SPT without Fika) |
| **LiveRaid Online** | co-op through Project Fika (see [docs/FIKA.md](docs/FIKA.md)) |

**LiveRaid Launcher** (in the archive) switches between the two editions and between the stable and the test branch in one click.

## Who does what

| | What it does | What it does *not* do |
|---|---|---|
| **QuestingBots** | sends bots along quests, spawns PMCs | doesn't decide who the bot is or what it has |
| **SAIN** | aim, cover, the fight itself | doesn't decide what to do about a fight 200 m away |
| **LootingBots** | picks things up | doesn't decide *whether it is a good moment* |
| **LiveRaid** | gives each bot a kit, a character and a reason; steers the three above from one F12 menu | doesn't shoot, doesn't aim, doesn't loot by itself |

## What LiveRaid adds

- **A bot with a past and a kit.** A PMC's gear and level don't depend on yours: from a bum with a pistol to a full meta kit, like online. They start with an empty backpack and rig - loot is what they find in the raid. Every squad is hostile to every other squad and to you, same side included.
- **Everybody is someone.** Play style (camper, rat, rusher, looter, quester, explorer, hunter, sniper), skill from newcomer to pro, moods (the vengeful one looks for whoever shot him, the lucky survivor turns careful).
- **The lobby of a real online raid.** QuestingBots can spawn PMCs; LiveRaid sets it to the map's full lobby, adds reserves, sends player scavs on a schedule and to replace the dead, and a *smart population balance* keeps the head count right - no crowd round you, no empty map.
- **The whole map, floor by floor.** LiveRaid reads every loot spawn, container and locked room from SPT's own data and builds exploration goals over all of it, remembers who has been where, and walks routes over rubble the navmesh doesn't connect. With the route recorder on, bots also learn the paths **you** walk.
- **Keys that matter.** Real key items: no key, no door. A bot with a key walks to its locked room or container; some PMCs carry the key to the richest place of the map.
- **Decisions around loot.** LootingBots grabs; LiveRaid decides whether *now* is the time: value against danger, look at a body first, search a whole room, drop cheap stuff for a find, carry out a fallen mate's kit, share meds in the squad.
- **Decisions in a fight.** SAIN shoots; LiveRaid adds morale and roles, retreat from a lost fight, forgetting a cold one, and rush / flank / hold / ambush / leave when a fight is heard far away. Squads play one of 12 doctrines and a deck of 18 tactics.
- **Healing and leaving.** Bandage at the first quiet moment, splints and painkillers for legs; each squad has its own time to leave and goes out together.
- **Repairs of EFT's own AI bugs.** Stuck hands, reload flags, magazines thrown away, friendly fire inside a squad, bots frozen under fire.
- **After death.** A free camera and an observer mode (taking over a bot is raw, see the warning below).

It does **not** make bots aim better or cheat, adds no bosses and never goes above the map's own scav limit. Presets (Recommended, Weak PC, Quiet raids, Busy raids, Fixes only) set the depth; every group of features can be switched off on its own.

## Requirements

SPT **4.1.6** (EFT 0.16.9) and these mods (the versions LiveRaid is built and tested against):

| Mod | Version | Needed for |
|---|---|---|
| QuestingBots | 1.2.0 | bot quests and spawning |
| SAIN | 4.5.1 | combat AI |
| LootingBots | 1.8.0 | looting |
| BigBrain | 1.5.0 | AI layers (needed by the three above) |
| Waypoints | 1.9.0 | bot navigation |
| Project Fika + QuestingBots Fika sync | 2.4.x | **LiveRaid Online** only |

Not compatible with **ORBIT** (remove it). Install guide: [INSTALL.md](INSTALL.md).

> **Warning - taking over a bot is raw and often breaks (it is off by default).** After the bot you play dies, the textures of the location may stop loading, sound may go wrong and other things may fail; you may have to restart the game. For a stable raid turn *Take over a bot* off in F12 (After death).

## Build

```
dotnet build LiveRaid.sln -c Release
dotnet build src/LiveRaid.Client/LiveRaid.Client.csproj -c Release -p:Edition=Online
```

`-p:Deploy=false` builds without copying into the game; `-p:SptRoot=<folder>` points to the game. .NET 10 SDK (pinned in `global.json`). Archives: `python -I tools/make_release.py <version>`. Checks without the game: `dotnet run --project tools/bindtest`. The architecture and the history of the work are in [PROJECT_STATE.md](PROJECT_STATE.md) (Russian).

## Reporting a problem

Say what you did, which map and which minute, and attach `BepInEx/LogOutput.log` (and the newest file from `BepInEx/plugins/LiveRaid/logs` if you can). Send the logs to me on Discord: **n1x7192**, or open an issue here on GitHub.

## Credits and licence

By **Lir**. Licensed under [Creative Commons Attribution-NonCommercial-ShareAlike 4.0](LICENSE): share and adapt with credit, not for commerce, adaptations under the same licence. LiveRaid does not contain code of the mods it extends; see [NOTICE.md](NOTICE.md). Thanks to the authors of QuestingBots (DanW), SAIN (Solarint), LootingBots (Skwizzy), BigBrain and Waypoints (DrakiaXYZ), the SPT and Fika teams.

---

# LiveRaid - боты как в онлайне (SPT 4.1.6)

**Версия 1.0.1** · автор **Lir** · лицензия [CC BY-NC-SA 4.0](LICENSE)

LiveRaid **не заменяет** QuestingBots, SAIN и LootingBots. Они делают то же, что и делали - ходят по квестам, стреляют, подбирают вещи, - а я добавил то, чего нет ни в одном из них: **кто такой каждый бот, что у него с собой и что он решает делать с рейдом вокруг.** Мод стоит поверх них (их кода в нём нет), а все выключатели собраны в одном меню F12. Цель та же, что у ORBIT, путь другой: ORBIT заменяет эти моды, LiveRaid их расширяет.

Две редакции из одного исходника: **LiveRaid** (одиночная игра, SPT без Fika) и **LiveRaid Online** (кооператив через Project Fika, [docs/FIKA.md](docs/FIKA.md)). **LiveRaid Launcher** (лежит в архиве) переключает редакции и ветку мода (стабильная / тестовая) одним нажатием.

## Кто за что отвечает

| | Что делает | Чего не делает |
|---|---|---|
| **QuestingBots** | водит ботов по квестам, спавнит ЧВК | не решает, кто этот бот и что у него есть |
| **SAIN** | прицел, укрытия, сам бой | не решает, что делать с боем за 200 м |
| **LootingBots** | подбирает вещи | не решает, *подходящий ли сейчас момент* |
| **LiveRaid** | даёт каждому боту снаряжение, характер и причину; управляет тремя модами выше из одного меню F12 | не стреляет, не целится, сам не лутает |

## Что добавляет LiveRaid

- **У бота есть прошлое и набор.** Снаряжение и уровень ЧВК не зависят от вашего: от бомжа с пистолетом до полной меты, как в онлайне. Стартуют с пустым рюкзаком и разгрузкой - лут это то, что нашли в рейде. Каждый отряд враг любому другому отряду и вам, своя сторона тоже.
- **Каждый - кто-то.** Стиль игры (кемпер, крыса, рашер, лутер, квестер, исследователь, охотник, снайпер), навык от новичка до профи, настроение (мстительный ищет того, кто в него стрелял, чудом выживший становится осторожным).
- **Лобби настоящего онлайн-рейда.** QuestingBots умеет спавнить ЧВК; LiveRaid выставляет ему полное лобби карты, добавляет запасных, отправляет игроков-диких по расписанию и на замену погибшим, а *умный баланс численности* держит число ботов в норме: не толпа вокруг вас и не пустыня.
- **Вся карта, этаж за этажом.** LiveRaid берёт из данных самого SPT каждую точку лута, контейнер и запертую комнату и строит по ним цели исследования, помнит, кто где был, и водит ботов по тропам через завалы, которые навмеш не соединяет. Включите запись маршрута - боты начнут ходить и по тем путям, которыми ходите **вы**.
- **Ключи, которые что-то значат.** Настоящие ключи: нет ключа - нет двери. Бот с ключом идёт к своей запертой комнате или контейнеру; у некоторых ЧВК ключ от самого богатого места карты.
- **Решения вокруг лута.** LootingBots хватает; LiveRaid решает, *сейчас ли* время: ценность против опасности, сперва осмотреть тело, обыскать всю комнату, выбросить дешёвое ради находки, вынести снаряжение павшего товарища, поделиться аптечками в отряде.
- **Решения в бою.** Стреляет SAIN; LiveRaid добавляет мораль и роли, отступление из проигранного боя, забывание остывшего и бросок / фланг / ожидание / засаду / уход, когда бой слышен далеко. Отряды играют по одной из 12 доктрин и колоде из 18 тактик.
- **Лечение и уход.** Перевязка в первую тихую минуту, шины и обезболивающие для ног; у каждого отряда своё время ухода, уходят вместе.
- **Исправления ошибок ИИ самой EFT.** Зависшие руки, флаги перезарядки, выброшенные магазины, огонь по своим внутри отряда, замирание под огнём.
- **После смерти.** Свободная камера и режим наблюдателя (вселение в бота сырое, см. предупреждение ниже).

Он **не** улучшает прицеливание и не читерит, не добавляет боссов и не поднимает число диких выше лимита карты. Глубину задают пресеты (Рекомендуемый, Слабый ПК, Тихие рейды, Людные рейды, Только исправления); каждую группу функций можно отключить отдельно.

## Требования

SPT **4.1.6** (EFT 0.16.9) и моды (версии, под которые собран и проверен LiveRaid): QuestingBots 1.2.0, SAIN 4.5.1, LootingBots 1.8.0, BigBrain 1.5.0, Waypoints 1.9.0; для **LiveRaid Online** ещё Project Fika 2.4.x и QuestingBots Fika sync. С **ORBIT** не совместим (удалите). Установка - [INSTALL.md](INSTALL.md).

> **Внимание - вселение в бота сырое и часто ломается (по умолчанию выключено).** После гибели бота, за которого вы играли, могут перестать подгружаться текстуры локаций, бывают проблемы со звуком и другие сбои; возможно, придётся перезапустить игру. Для стабильного рейда выключите «Вселяться в бота» в F12 (после смерти).

Сборка и устройство мода - выше и в [PROJECT_STATE.md](PROJECT_STATE.md).

## Сообщить о проблеме

Опишите, что делали, на какой карте и в какую минуту, и приложите `BepInEx/LogOutput.log` (и самый свежий файл из `BepInEx/plugins/LiveRaid/logs`, если можете). Логи можно скинуть мне в Discord: **n1x7192**, или открыть issue здесь, на GitHub.

## Автор и лицензия

Автор - **Lir**. Лицензия [CC BY-NC-SA 4.0](LICENSE): можно делиться и изменять с указанием автора, не для коммерции, производные работы - на тех же условиях. Кода модов, которые LiveRaid расширяет, в нём нет ([NOTICE.md](NOTICE.md)). Спасибо авторам QuestingBots (DanW), SAIN (Solarint), LootingBots (Skwizzy), BigBrain и Waypoints (DrakiaXYZ), командам SPT и Fika.
