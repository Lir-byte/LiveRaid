# Text for the mod page (sp-mod.com / SPT Forge) — LiveRaid 1.0.0

**Name:** LiveRaid
**Author:** Lir
**Version:** 1.0.0
**SPT versions:** 4.1.6 (`~4.1.0`)
**License field on the hub:** No license (the list has no Creative Commons non-commercial option; the real licence is CC BY-NC-SA 4.0 and is stated on the first line of the description)
**Source code:** https://github.com/Lir-byte/LiveRaid
**Dependencies:** QuestingBots (1.2.0), SAIN (4.5.1), LootingBots (1.8.0), BigBrain (1.5.0), Waypoints (1.9.0). LiveRaid Online also: Project Fika (2.4.x), QuestingBots Fika sync
**Incompatible with:** ORBIT
**Tags / categories:** AI, Bots, Gameplay, PvE, Quality of life
**Files:** `LiveRaid-1.0.0.zip` (single player), `LiveRaid-Online-1.0.0.zip` (Project Fika co-op)

## Short description (teaser)

Bots that travel and loot absolutely all over the map — and walk your own recorded routes. A full online-style lobby on every map, squad tactics, real keys and doors, looting, fighting and leaving like a player.

## Краткое описание (RU)

Боты путешествуют и лутают абсолютно по всей карте — и ходят по вашим записанным маршрутам. Полное онлайн-лобби на каждой карте, тактики отрядов, настоящие ключи и двери, лут, бой и уход как у игрока.

## Description

**License: CC BY-NC-SA 4.0** — credit Lir, non-commercial, adaptations under the same licence (text: https://creativecommons.org/licenses/by-nc-sa/4.0/ , also `LICENSE.txt` in the archive). QuestingBots, which LiveRaid extends, is under the same licence.

**⚠ Taking over a bot (after death) is raw and often breaks:** after the bot you play dies, the textures of the location may stop loading, sound may go wrong and other things may fail — you may have to restart the game. Leave *Take over a bot* off in F12 (After death) for a stable raid.

**LiveRaid makes a raid against bots feel like a raid against players.** It is an extension on top of QuestingBots, SAIN and LootingBots: those mods stay untouched, LiveRaid steers them with patches and keeps every switch in one F12 menu.

### What you get

- **A map full of people, not a desert.** An online-sized PMC lobby on each map's own numbers, reserves, player scavs coming in on their own schedule and replacing the people who died, scavs patrolling their area. The new **smart population balance** keeps the head count right for the map and the age of the raid — no crowd round you, no empty map — and every raid has its own mood (quiet / usual / busy).
- **Every bot is a person.** Play styles (camper, rat, rusher, looter, quester, explorer, hunter, sniper), skill from newcomer to pro, moods, 12 squad doctrines and a deck of 18 squad tactics: flanks, pincers, peeks, smoke, bounds under cover, revenge for a fallen mate.
- **Moving like players.** Hot zones, key places, hot drop, people's corridors, bots spread over the map, no idling and no long stands.
- **Real keys and doors.** Bots carry real keys, open locked doors and containers, may carry the key to the richest place of the map; helpers for doors, corners and rooms they are shut in.
- **Looting like a player.** Value against danger, clean-up after a fight, a whole room searched, cheap things dropped for a find, a fallen mate's kit carried out, meds and loot shared in the squad.
- **Fighting like a player.** Morale and fight roles, return fire, the right gun for the range, cold fights forgotten (and standoffs through a wall), flanks, grenades, smoke, night discipline.
- **Healing and leaving like a player.** Bandage at the first quiet moment, splints, surgery; personal leave times, switch exits, paid exits, waiting crouched at the exit, leaving as a squad.
- **Repairs of EFT bugs.** Stuck hands, reload flags, magazines thrown away, friendly fire inside a squad, bots frozen under fire.
- **After death.** A free camera, an observer mode (watch raids without playing), taking over a bot (**experimental, see the warning below**).
- **Bots go absolutely everywhere.** With LiveRaid the bots travel and loot over the WHOLE map — every district, every floor, every room that has loot — not just the usual few spots. Switch on the recording of your own route and the bots start walking the routes you walk yourself.
- **LiveRaid Launcher** in the archive: switch single player ↔ Online and the stable ↔ test branch in one click.

### Two editions

| File | For |
|---|---|
| `LiveRaid-1.0.0.zip` | single player (SPT without Fika) |
| `LiveRaid-Online-1.0.0.zip` | co-op through Project Fika (the host's settings apply to everyone) |

### Install

Close the game and the server, unpack the archive into the game's root folder (where `EscapeFromTarkov.exe` is), start the server and the game. Details: `INSTALL.md` in the archive. Check `BepInEx/LogOutput.log` for `LiveRaid 1.0.0 loaded`. Open F12 → LiveRaid and pick a preset (or leave *Recommended*).

### Notes

- **Taking over a bot is raw and often breaks.** After the bot you play dies, the textures of the location may stop loading, sound may go wrong and other things may fail (you may have to restart the game). It is off the beaten path of the mod: leave *Take over a bot* off in F12 (After death) if you want a stable raid.
- Built and tested on SPT 4.1.6 (EFT 0.16.9). Remove ORBIT — it conflicts.
- Part of the newest behaviour has not yet been through a long raid; report problems with `BepInEx/LogOutput.log` and `BepInEx/plugins/LiveRaid/logs`.
- Licence: CC BY-NC-SA 4.0 (QuestingBots, which LiveRaid extends, is under the same licence). LiveRaid contains no code of the mods it extends.

## Описание (RU)

**Лицензия: CC BY-NC-SA 4.0** — с указанием автора (Lir), не для коммерции, производные работы на тех же условиях (текст: https://creativecommons.org/licenses/by-nc-sa/4.0/ , а также `LICENSE.txt` в архиве).

**LiveRaid делает рейд против ботов похожим на рейд против игроков.** Надстройка над QuestingBots, SAIN и LootingBots: сами эти моды не меняются, LiveRaid управляет ими патчами, все выключатели — в одном меню F12.

Полное онлайн-лобби на каждой карте и «умный баланс численности», игроки-дикие на замену погибшим; характеры, 12 доктрин и 18 тактик отрядов; ходьба как у игроков (горячие зоны, ключевые места, хот-дроп, без простоя); настоящие ключи и двери; лут, бой, лечение и уход как у игрока; исправления ошибок EFT; свободная камера, режим наблюдателя и вселение в бота после смерти (**сырое: после гибели бота могут перестать грузиться текстуры, бывают проблемы со звуком — подробности в примечаниях**). Боты ходят и лутают по всей карте, а при включённой записи вашего маршрута — по вашим путям. LiveRaid Launcher переключает одиночный ↔ Online и стабильную ↔ тестовую ветку.

Две редакции: **LiveRaid** (одиночная игра) и **LiveRaid Online** (кооператив через Project Fika). Установка: закройте игру и сервер, распакуйте архив в корень игры. Нужны SPT 4.1.6, QuestingBots 1.2.0, SAIN 4.5.1, LootingBots 1.8.0, BigBrain 1.5.0, Waypoints 1.9.0. С ORBIT не совместим. Лицензия CC BY-NC-SA 4.0.

**Внимание:** вселение в бота сырое и часто ломается: после гибели бота, за которого вы играли, могут перестать подгружаться текстуры локаций, бывают проблемы со звуком и другие сбои. Для стабильного рейда выключите «Вселяться в бота» в F12 (после смерти).
