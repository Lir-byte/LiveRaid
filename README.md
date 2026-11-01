# LiveRaid — bots that play like people online (SPT 4.1.6)

**Version 1.0.0** · by **Lir** · [CC BY-NC-SA 4.0](LICENSE)

LiveRaid makes a raid against bots feel like a raid against players. It is an extension on top of **QuestingBots**, **SAIN** and **LootingBots**: they stay as they are, LiveRaid steers them with patches and keeps all its switches in one F12 menu.

Two editions from one source:

| Edition | For |
|---|---|
| **LiveRaid** | single player (SPT without Fika) |
| **LiveRaid Online** | co-op through Project Fika (see [docs/FIKA.md](docs/FIKA.md)) |

**LiveRaid Launcher** (in the archive) switches between the two editions and between the stable and the test branch of the mod in one click.

## What it does

- **A map full of people, not a desert.** An online-sized lobby of PMCs on the map's own numbers, reserves, player scavs that come in on their own schedule and replace the people who died, scavs that patrol their own area. *Smart population balance* keeps the head count right for each map and raid time — no crowd round you, no empty map.
- **Every bot is a person.** Play styles (camper, rat, rusher, looter, quester, explorer, hunter, sniper), skill from newcomer to pro, moods, squad doctrines (12) and a deck of squad tactics (18): flanks, pincers, peeks, smoke, bounds under cover, revenge for a fallen mate.
- **Moving like players.** Hot zones, key places, hot drop, people's corridors, bots spread over the map, no idling, no long stands, tempo of the squad's doctrine.
- **Real keys and doors.** Bots carry real keys, open locked doors and containers, may carry the key to the richest place of the map; helpers for doors, corners and rooms they are shut in.
- **Looting like a player.** Weigh value against danger, clean up after a fight, search a whole room, drop cheap things for a find, carry out a fallen mate's kit, share meds and loot in the squad.
- **Fighting like a player.** Morale and fight roles, return fire, the right gun for the range, forgetting a cold fight (and standoffs through a wall), flanks, grenades, smoke, night discipline.
- **Healing like a player.** Bandage at the first quiet moment, splints and painkillers for legs, surgery kits, treatment broken off under fire.
- **Leaving like a player.** Personal times, switch exits, paid and conditional exits, waiting crouched at the exit, leaving as a squad.
- **Repairs of EFT bugs.** Stuck hands, reload flags, magazines thrown away, friendly fire inside a squad, bots frozen under fire.
- **After death.** A free camera, an observer mode (watch raids without playing), taking over a bot (**raw: after the bot you play dies, location textures may stop loading and sound may break — see below**).
- **Bots go absolutely everywhere.** With LiveRaid the bots travel and loot over the WHOLE map — every district, every floor, every room that has loot — not just the usual few spots. Switch on the recording of your own route and the bots start walking the routes you walk yourself.

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

> **Warning — taking over a bot is raw and often breaks.** After the bot you play dies, the textures of the location may stop loading, sound may go wrong and other things may fail; you may have to restart the game. For a stable raid turn *Take over a bot* off in F12 (After death).

## Build

```
dotnet build LiveRaid.sln -c Release
dotnet build src/LiveRaid.Client/LiveRaid.Client.csproj -c Release -p:Edition=Online
```

`-p:Deploy=false` builds without copying into the game; `-p:SptRoot=<folder>` points to the game. .NET 10 SDK (pinned in `global.json`). Archives: `python -I tools/make_release.py <version>`. Checks without the game: `dotnet run --project tools/bindtest`. The architecture and the history of the work are in [PROJECT_STATE.md](PROJECT_STATE.md) (Russian).

## Credits and licence

By **Lir**. Licensed under [Creative Commons Attribution-NonCommercial-ShareAlike 4.0](LICENSE): share and adapt with credit, not for commerce, adaptations under the same licence. LiveRaid does not contain code of the mods it extends; see [NOTICE.md](NOTICE.md). Thanks to the authors of QuestingBots (DanW), SAIN (Solarint), LootingBots (Skwizzy), BigBrain and Waypoints (DrakiaXYZ), the SPT and Fika teams.

---

# LiveRaid — боты как в онлайне (SPT 4.1.6)

**Версия 1.0.0** · автор **Lir** · лицензия [CC BY-NC-SA 4.0](LICENSE)

LiveRaid делает рейд против ботов похожим на рейд против игроков. Это надстройка над **QuestingBots**, **SAIN** и **LootingBots**: сами эти моды не меняются, LiveRaid управляет ими патчами, а все выключатели собраны в одном меню F12.

Две редакции из одного исходника: **LiveRaid** (одиночная игра, SPT без Fika) и **LiveRaid Online** (кооператив через Project Fika, [docs/FIKA.md](docs/FIKA.md)). **LiveRaid Launcher** (лежит в архиве) переключает редакции и ветку мода (стабильная / тестовая) одним нажатием.

## Что делает мод

- **Карта с людьми, а не пустыня.** Онлайн-лобби ЧВК по числам именно этой карты, запасные, игроки-дикие по своему расписанию и на замену погибшим, дикие патрулируют свой район. «Умный баланс численности» держит число ботов под карту и время рейда: не толпа вокруг вас и не пустая карта.
- **У каждого бота своё лицо.** Стили (кемпер, крыса, рашер, лутер, квестер, исследователь, охотник, снайпер), навык от новичка до профи, настроение, 12 доктрин отрядов и колода из 18 тактик: фланги, клещи, выглядывание, дым, перебежки с прикрытием, месть за павшего товарища.
- **Ходят как игроки.** Горячие зоны, ключевые места, хот-дроп, «коридоры людей», боты распределены по карте, без простоя и долгих стояний, темп по доктрине отряда.
- **Настоящие ключи и двери.** Ключи у ботов, запертые двери и контейнеры, призовой ключ от самого богатого места карты; помощь у дверей, углов и в запертых комнатах.
- **Лут как у игрока.** Ценность против опасности, зачистка после боя, обыск всей комнаты, выброс дешёвого ради находки, разлут павшего товарища, обмен аптечками и лутом в отряде.
- **Бой как у игрока.** Мораль и роли, ответный огонь, оружие под дистанцию, остывший бой забывается (и тупик за стеной), фланги, гранаты, дым, ночная дисциплина.
- **Лечение как у игрока.** Перевязка в первую тихую минуту, шины и обезболивающие, хирургия, прерывание лечения под огнём.
- **Уход как у игрока.** Своё время, выходы с рубильником, платные и особые выходы, ожидание у выхода присев, уход отрядом.
- **Исправления ошибок EFT.** Зависшие руки, флаги перезарядки, выброшенные магазины, огонь по своим, замирание под огнём.
- **После смерти.** Свободная камера, режим наблюдателя (смотреть рейды, не играя), вселение в бота (**сырое: после гибели бота могут перестать грузиться текстуры и сломаться звук — см. ниже**).
- **Боты ходят и лутают абсолютно по всей карте.** С LiveRaid боты путешествуют и лутают по ВСЕЙ карте — каждый район, каждый этаж, каждая комната с лутом, а не пара привычных мест. Включите запись своего маршрута — и боты начнут ходить по тем путям, которыми ходите вы.

## Требования

SPT **4.1.6** (EFT 0.16.9) и моды (версии, под которые собран и проверен LiveRaid): QuestingBots 1.2.0, SAIN 4.5.1, LootingBots 1.8.0, BigBrain 1.5.0, Waypoints 1.9.0; для **LiveRaid Online** ещё Project Fika 2.4.x и QuestingBots Fika sync. С **ORBIT** не совместим (удалите). Установка — [INSTALL.md](INSTALL.md).

> **Внимание — вселение в бота сырое и часто ломается.** После гибели бота, за которого вы играли, могут перестать подгружаться текстуры локаций, бывают проблемы со звуком и другие сбои; возможно, придётся перезапустить игру. Для стабильного рейда выключите «Вселяться в бота» в F12 (после смерти).

Сборка и устройство мода — выше и в [PROJECT_STATE.md](PROJECT_STATE.md).

## Автор и лицензия

Автор — **Lir**. Лицензия [CC BY-NC-SA 4.0](LICENSE): можно делиться и изменять с указанием автора, не для коммерции, производные работы — на тех же условиях. Кода модов, которые LiveRaid расширяет, в нём нет ([NOTICE.md](NOTICE.md)). Спасибо авторам QuestingBots (DanW), SAIN (Solarint), LootingBots (Skwizzy), BigBrain и Waypoints (DrakiaXYZ), командам SPT и Fika.
