https://www.youtube.com/watch?v=I84dgrjb89g

**License: CC BY-NC-SA 4.0** - credit Lir, non-commercial, adaptations under the same licence ([text](https://creativecommons.org/licenses/by-nc-sa/4.0/), also `LICENSE.txt` in the archive). QuestingBots, which LiveRaid extends, is under the same licence.  
**Описание на русском - ниже на этой же странице** (раздел «Русский»).

# English

## What it is

LiveRaid makes SPT bots **much smarter, more tactical and capable of what online players do.** A bot here is not a target that runs along a quest: it has a character and a kit, plays in a squad with its own doctrine and tactics (flanks, pincers, ambushes, bounds under cover, revenge for a fallen mate), weighs a fight and breaks off a lost one, opens locked rooms with real keys and searches them, loots with a head on its shoulders, explores the whole map and leaves through any exit - even the ones behind a lever and the armoured train.

How: QuestingBots, SAIN and LootingBots keep doing what they do - walk quests, shoot, pick things up - and LiveRaid adds the part none of them has: **who each bot is, what it carries, and what it decides to do with the raid around it.** Is it a copy of ORBIT? Same goal, different way: ORBIT replaces those mods, LiveRaid sits on top of them and does not contain their code.

## What's new since 1.0.5 (versions 1.2.4 - 1.4.16)

- **Weapons and clothes of WTT-ContentBackport on the bots** - the mod's guns, knives, ammo and clothes now appear on PMCs and scavs (when that mod is installed; a switch in F12).
- **Every exit is open to the bots.** The airtight door of the bunker, D-2, the Interchange safe room, gates and lifts, the sewer manhole and the armoured train on Reserve: the bot finds the lever, pulls it, stands out the timer and leaves, like a player. An exit that opens in the middle of a raid is a chance to slip out "under cover of noise", when everybody is heading for the exit.
- **The map is explored evenly, floor by floor.** An atlas remembers every place the bots missed and why; each bot gets 2-4 aims for the raid; upper floors and "cold corners" get their share of the time.
- **The mod learns the map from your walk.** Where you walked off the navmesh, it writes a link or a patch itself (with a wall check) and uses it from the next raid.
- **Keys got a purpose.** A key lies in the secured pouch with a chance; a bot with a door key goes to its door, enters the room it unlocked and searches all of it; keys are dealt near the doors they open.
- **The packrat** - a lone PMC that comes only for loot, with the hitboxes of Kaban; it loots room by room and runs from a fight away from the crowd. Its chance is in F12.
- **Careful choice.** A lone bot or a squad with a wounded mate weighs a fight before it goes in; places where bots died are remembered; a cornered bot fights where it stands; bots lie in ambush for an enemy they hear.
- **16 squad doctrines** (new: scouts, ambushers, wolf pack, support group), a formation that breathes, a fallen mate's kit looted whole.
- **Fixes of EFT bugs:** the eternal reload, spare magazines for every PMC (a third of them had none), "the rounds did not go in" the magazine, a gun without a magazine breaking SAIN, grenade suicides.
- **Spawns:** squads placed apart at the start, reserves not before N minutes, optional late waves of PMCs.

## Who does what

| | What it does | What it does *not* do |
|---|---|---|
| **QuestingBots** | sends bots along quests, spawns PMCs | doesn't decide who the bot is or what it has |
| **SAIN** | aim, cover, the fight itself | doesn't decide what to do about a fight 200 m away |
| **LootingBots** | picks things up | doesn't decide *whether it is a good moment* |
| **LiveRaid** | gives each bot a kit, a character and a reason; steers the three above from one F12 menu | doesn't shoot, doesn't aim, doesn't loot by itself |

## What LiveRaid adds (and the others don't have)

- **A bot with a past and a kit.** A PMC's gear and level don't depend on yours: from a bum with a pistol and no armour to a full meta kit, like online. They start with an empty backpack and rig (just the kit: gun, mags, a few meds) - loot is what they find in the raid. Every squad is hostile to every other squad and to you, same side included.
- **Everybody is someone.** A play style (camper, rat, rusher, looter, quester, explorer, hunter, sniper), a skill from newcomer to pro, moods (the vengeful one looks for whoever shot him, the lucky survivor turns careful, the one with an expensive find hurries to the exit).
- **The lobby of a real online raid.** QuestingBots can spawn PMCs; LiveRaid sets it to the full lobby of that map, adds reserves, sends player scavs on a schedule and to replace the dead, and a **smart population balance** keeps the head count right for the map and the age of the raid - no crowd round you, no empty map.
- **The whole map, floor by floor.** QuestingBots goes to quest points. LiveRaid reads every loot spawn, container and locked room from SPT's own data and builds exploration goals over all of it, remembers who has been where so places "cool down", and walks routes over rubble the navmesh doesn't connect. Places the bots missed are remembered in an **atlas** and swept floor by floor in the next raids. Switch on the route recorder and the bots also learn the paths **you** walk - links and navmesh patches are written automatically.
- **Keys that matter.** Real key items, often in the secured pouch: no key, no door. A bot with a key walks to its locked room or container, goes in and searches **all** of it, and the squad does not repeat it; some PMCs carry the key to the richest place of the map (you can take it off the body). A helper opens doors in the way when a bot gets stuck.
- **Decisions around loot.** LootingBots grabs; LiveRaid decides whether *now* is the time: value against danger, look at a body before walking up to it, search a whole room, drop cheap stuff for a find, carry out a fallen mate's kit, share meds in the squad.
- **Decisions in a fight.** SAIN does the shooting; LiveRaid decides the rest: morale and roles, retreating from a lost fight, forgetting a cold one, rush / flank / hold / ambush / leave when a fight is heard far away, by character. Squads play one of 16 doctrines and a deck of 21 tactics (flanks, pincers, smoke, bounds under cover, revenge for a fallen mate).
- **Healing and leaving.** Bandage at the first quiet moment, splints and painkillers for broken legs; each squad has its own time to leave, waits crouched at the exit and goes out together. Bots use **every exit**, including the ones behind a lever (the airtight door, D-2, the manhole) and the armoured train.
- **Repairs of EFT's own AI bugs.** Stuck hands after a reload, magazines thrown on the floor, friendly fire inside a squad, bots frozen under fire, bots walking to the exit backwards.
- **WTT-ContentBackport on the bots.** Guns, knives, ammo and clothes of that mod appear on PMCs and scavs (only when the mod is installed).
- **The packrat.** A lone PMC that comes only for loot and keeps out of fights.
- **After death - a second life.** **Offline (single player) edition only - not in LiveRaid Online (Fika).** A free camera, an observer mode, and taking over any other bot: play on as that PMC or scav with its own gear and inventory (see *Life after death* below).
- **One menu instead of four config files.** The other mods' files stay original; LiveRaid sets the values it needs in memory from F12 and gives them back when you switch a feature off. Presets, English and Russian.

What it does **not** do: it doesn't make bots aim better, see through walls or cheat, it adds no bosses and never goes above the map's own scav limit.

## In detail: the mod's mechanics

### Squad doctrines: every squad plays its own game

A PMC squad doesn't just follow its leader. When it comes in, it draws one of **16 doctrines**: storm troopers, PvP hunters, looters, questers, carry (the strong one leads the weak), randoms, a tight clan, boss hunters, the quiet ones, marksmen, speedrunners, armoured fist, scouts, ambushers, the wolf pack and a support group. Who is in the squad matters: a squad of rushers storms more often, rats keep quiet, a squad of long guns plays marksmen.

A doctrine is a set of eleven numbers (aggression, cohesion, pace, talk, precision, grit, preferred range, greed for loot, appetite for third-partying, thirst for revenge, readiness to leave early). On top of that every squad gets **two quirks** out of fourteen: chatty or silent, hasty or pedantic, careful or bold, scattering or sticking together, long fights or close ones, vultures or loners, vengeful, greedy. A doctrine comes up at most twice per raid and no two squads are alike - a draw that lands too close to a squad already on the map is drawn again.

Inside the squad every member plays the doctrine his own way: a rusher pushes harder than his mates, a novice keeps formation worse and catches on to a call a second or two late, a sniper wants distance.

### Tactic cards

In a fight the squad leader plays a card from a deck of **21 tactics**. The pick is weighed by the doctrine, by the fight (numbers, range, health, a wounded enemy) and by what the squad has already played this raid: a card used before is less likely to come up again, so a squad tries different things. The cards:

- **Straight in:** push together, pair push (the two boldest go), suppress and rush (one runs in while the rest shoot).
- **Round him:** pin and flank (one goes round), pincer (two, one each side), wide flank (far round to the enemy's side), high ground (round by a floor, roof or hill above him).
- **Tricks:** bait (one shows himself while another goes round), peek-bait (peek to draw fire and pull back), spread then storm (everyone takes a spot for 10-20 s, then all go in at once), smoke screen.
- **Under cover:** hold, bounding (two teams take turns every 6-9 s, one moves up while the other covers; a drilled squad keeps time, randoms don't).
- **Buildings:** storm the building (two bounds up to it, then in), hold the building (hold the ways in, then go at him or leave by another way).
- **Reading the fight:** push the wounded (a mate hit the enemy, everyone presses him before he patches up), run him down (he went out of sight, the two boldest chase), turn on a third party (new shots from another side).
- **When it goes wrong or personal:** fall back and regroup (the whole squad pulls back 45-90 m and comes back, likely round him), scatter (the fight is lost, everyone breaks off and the squad soon leaves the raid), revenge (a mate fell and the squad is vengeful: all go for the killer).
- And **every man for himself** - no call at all, each by his own character.

A card hands out fight roles (hold, assault, flank) and the members play them their way. A member may go his own way (randoms often, a clan hardly ever). Morale still wins over any card: a bot that loses its nerve falls back whatever it was told.

### Morale: when a bot leaves a lost fight

SAIN fights well from moment to moment, but on its own it only falls back when it runs out of ammo. LiveRaid keeps weighing the fight the way a player does. Pressure to leave builds up from: enemies outnumbering friends, being left alone after a squad, how much of the squad is lost, health, being pinned down, and the enemy's better kit. Every term is small, so one thing alone (a lone man against two) rarely breaks anybody, while several together (alone, hurt, three against one, a better kit against you) often do.

Then comes character: nerve multiplies the pressure by 1.5 for the most timid and by 0.45 for the boldest (aggression, skill, style, personality). The bot checks every 1.5 seconds; steady heavy pressure turns into a run within some twenty seconds, light pressure never does, and nobody runs from an enemy closer than 12 m. When it breaks, the bot runs 45-90 m away, SAIN stops treating its enemies as known for a minute or two, and a bot that lost a fight heads for the exit sooner.

**Moods.** By chance each PMC and player scav has some of four natures:
- **vengeful** - remembers who shot it and, once it is quiet, goes looking for him (twice at most);
- **shaken** - dropped under 25% health and lived, so it is careful for the rest of the raid: fewer fights, less nerve, leaves sooner;
- **streaky** - after two kills in ten minutes it is bolder but sloppier;
- **a hoarder** - with a rich bag it guards it, avoids fights and heads out sooner.

A bum with nothing to lose is bolder than a man with a full kit and a bag of loot.

### Squad talk

The first member to see an enemy calls the contact. Mates who QuestingBots split into a group of their own learn of it after a delay: half a second in a talkative clan, three in a quiet one, and a squad that hardly talks sometimes doesn't pass it on at all. When a mate falls, a vengeful squad hunts the killer, a shaken one falls back, and a squad that lost its leader without the stomach for revenge soon heads for an exit. After a fight, once it has been quiet for a while, the leader stops for a few seconds to let the squad gather; patching up and handing over meds happen meanwhile.

### Fighting like a player

- **Changing the angle.** SAIN peeks again and again from the same cover. Here, after a few shots from one spot (a pro after 4-9 rounds, a novice after 18-30, a long gun after 2-4) and once the enemy has ducked out of sight, the bot goes to another spot instead of peeking where he waits.
- **Hit in the open** by an enemy 25 m or more away: run for cover, don't stand and trade.
- **The right gun for the range.** A shotgun against an enemy far out of reach, or a bolt-action rifle at arm's length, is no answer, and SAIN doesn't see that. The bot takes the other gun, or runs in closer, or goes round, or leaves him be.
- **Leaving a scav be.** A looter, a rat or a quiet squad doesn't start a gunfight with an AI scav 30 m or more away that isn't shooting at them: the noise would bring the PMCs.
- **Grenades with a habit.** A storm squad throws before it goes in, a rat keeps its grenade (the throw gives it away), a novice forgets it has one.
- **Night discipline.** The disciplined keep the light off while someone is about, and at close contact switch every light on to blind the enemy; a squad waiting in ambush stays dark until the first shot.
- **Weighing the stakes.** A full kit with a bag of loot makes a bot careful; nothing to lose makes it bold.

### Hearing and looking

Bots hear more than gunfire: someone running (38 m), a door (30 m), a body being looted (14 m), a reload (16 m). A wall halves the range, another floor cuts it to 40%. What a bot does depends on who it is: go and look, go round, stop and listen, slip away, or let it be. The human player makes noise like anyone and gets looked for.

Doors are not left open either: rats and drilled squads shut a door behind them, the cautious stop and listen at a closed one. A bot that knows trouble is about glances into doorways as it walks. And a careful bot doesn't cross an open field the way QuestingBots' shortest path would: it runs along the tree line, the wall, the cars.

### Boss hunts

Online some squads come for Reshala, Glukhar, Sanitar or Tagilla, for the boss's gear. Boss hunter squads always, about a third of the storm troopers and some PvP hunters make the map's boss their goal 2-8 minutes into the raid. The leader takes the squad there in stages, and the hunt is called off if the squad is badly hurt, after 14 minutes, or when the boss is dead; then the hunters nearby go through his body and his guards'. The bosses themselves are not touched.

### A fallen mate's kit

Online a teammate comes for his friend's loot once it is quiet. The same here, and then some: at the body the bot swaps its own finds for the friend's guns, armour, helmet and backpack; what still doesn't fit it carries off to a hidden spot 6-22 m away (a bush, a corner) or heads out with what it has. Nobody goes while the killer is still within 120 m of the body.

### Leaving the raid: every exit, even behind a lever

Until now bots left only through exits without conditions. Now they leave **like players**:

- **Switch exits.** The airtight door of the bunker and D-2 (Reserve), the Interchange safe room, gates and lifts. The bot finds the first lever of the exit's chain that is still off, runs to it, pulls it and, when the exit opens, runs there, stands out the timer and leaves. A squad does it together. Levers that sit off the navmesh are reached by the end of the path and a wait.
- **The sewer manhole** takes nobody with a backpack: the bot throws the backpack off at the hatch and leaves.
- **The armoured train** (Reserve): bots know its rules (it comes in the second part of the raid, is open while it stands, leaves 5 s after the countdown), run to its zone, **step into the carriage, spread over its length** and leave when it pulls out. *The carriage boarding is new and has not been seen in many raids yet.*
- **"Under cover of noise".** An exit that opens after the start (a lever, power, the train, a timer) is an event: each free PMC or player scav that is not in a fight rolls a die (a rat or a looter more likely, a quester less), and the winner slips out while it is open. The chance is in F12.

### The whole map: sweep, atlas, learned paths

- **Even sweep.** A region is split into parts of 12-20 places and every place gets its quest; "cold corners" that nobody has visited pull harder from the 4th to the 12th minute.
- **Raid aims.** Each PMC and player scav gets 2-4 aims by character (an area to loot, a quest of its own, a height, a cold corner); the upper floors get their share of the time.
- **The atlas.** Every place the bots missed is remembered with the reason; sweep quests go floor by floor over the unreached places, and the weight grows with every raid that missed one.
- **Your walk teaches the map.** Where you walked off the navmesh, the mod learns a link or a navmesh patch (with a wall check) and uses it from the next raid, with nothing for you to do.

### The packrat

A lone PMC comes into the raid with a pistol and one of the biggest backpacks only for the loot, keeps out of fights and has the hitboxes of Kaban (chest 355, stomach 260). It loots room by room, flees a fight away from the crowd, ignores enemies farther than the alert distance (F12, 70 m) and goes home only with loot. Its spawn chance is set in F12 (Spawns).

### WTT-ContentBackport: weapons and clothes for the bots

If the WTT-ContentBackport mod is installed, its guns, knives and ammo go into the PMC pools (cheap ones into the scavs'), and its clothes into the PMC appearance by side. A switch is in F12 and shows only when the client part of that mod is installed.

### Life after death: play on as any bot

> **Offline (single player) edition only - not available in LiveRaid Online (Fika).**

Your death does not have to end the raid. A free camera comes loose over the map; aim at any living PMC or scav and press **F** - you are that bot now, with **its own gun, armour, rig, backpack and everything in them**: shoot, loot, heal, open its inventory, walk to its exit. When it dies, you are back in the free camera and can take over the next one; **Delete** hands it back to its AI. Switch it on in F12 → *After death* → *Take over a bot*. What the bot does - kills, loot, its exit - does not go to your profile: the raid still ends as your death. If the hands of the bot you took do not work (the gun does not fire or reload), leave it with Delete and take it over again.

## Depth: presets

| Preset | In short |
|---|---|
| **Recommended** | everything on, as I play it |
| **Weak PC** | far bots simplified and put to sleep, fewer bots, no detailed logs |
| **Quiet raids** | fewer bots and PMCs, calmer map |
| **Busy raids** | more bots and PMCs, more reserves |
| **Fixes only** | all of LiveRaid's behaviour off, only the repairs of EFT bugs and healing stay |

Every group of features (keys, map life, characters, spawns, loot, squads, fight, healing, routes) can also be switched off separately. Every setting has a tooltip.

## Install

1. Install the requirements below and make sure SPT 4.1.6 runs. Close the game and the server.
2. Download **`LiveRaid-<version>.zip`**: one archive with two equal folders, `Single` (single player) and `Online` (co-op through Project Fika).
3. Unpack the folder you need into the game's root folder (where `EscapeFromTarkov.exe` is), agreeing to replace files. **LiveRaid Launcher** (in the archive) switches the edition later.
4. Start the server and the game. F12 → LiveRaid, pick a preset or leave *Recommended*.
5. Check `BepInEx/LogOutput.log` for `LiveRaid <version> loaded` with no `disabled: …` after it.

Updating: the same way over the old files; your settings and recorded routes are kept. Removing: delete `BepInEx/plugins/LiveRaid`, `SPT_Runtime/user/mods/LiveRaid`, `LiveRaid Launcher.exe` and the `LiveRaid Launcher` folder.

## Requirements and compatibility

SPT **4.1.6** and: **QuestingBots 1.2.0**, **SAIN 4.5.1**, **LootingBots 1.8.0**, **BigBrain 1.5.0**, **Waypoints 1.9.0**. *SAIN 4.5.1 is the latest version on the Forge; older 4.4.x may work worse or not at all.* **Strongly recommended: Smoothie 1.1.0** (by morphox, https://sp-mod.com/mod/3132/smoothie) - it renders only what you actually see; a big performance gain with no downside for LiveRaid (with 20+ bots alive it helps most). Many thanks to its author. **Compatible with WTT-ContentBackport** (optional): with it installed the bots also carry its guns and clothes. **LiveRaid Online** also needs **Project Fika 2.4.x** and **QuestingBots Fika sync**: the bots live on the host, the host's settings apply to everybody. If the SPT server runs on another machine (dedicated or headless), put the text of the server's `user/mods/LiveRaid/host.token` into the **Host key** field of `BepInEx/config/com.lir.liveraid.cfg` (section `0. Online`) on the machine of the player who should change the settings, and restart the game.

Not compatible with **ORBIT** - remove it. Other mods that change the same things (spawns, quests, looting) will fight with LiveRaid; keep one of them. **LiveRaid Launcher** (in the archive) switches single player ↔ Online and the stable ↔ test branch in one click.

## Known problems

- **Taking over a bot** (offline edition only): if the hands of the bot you took do not work (the gun does not fire or reload), leave it (Delete) and take it over again.

## Reporting a problem

Say what you did, which map and which minute, and attach `BepInEx/LogOutput.log` (and the newest file from `BepInEx/plugins/LiveRaid/logs` if you can). Send the logs to me on Discord: **n1x7192**, or open an issue on GitHub: https://github.com/Lir-byte/LiveRaid . Credits: QuestingBots (DanW), SAIN (Solarint), LootingBots (Skwizzy), BigBrain and Waypoints (DrakiaXYZ), the SPT and Fika teams.

---

# Русский

## Что это

LiveRaid делает ботов SPT **гораздо умнее, тактичнее и способными на то, на что способны игроки онлайн.** Бот здесь - не мишень, бегущая по квесту: у него есть характер и набор снаряжения, он играет в отряде по своей доктрине и тактике (фланги, клещи, засады, перебежки с прикрытием, месть за павшего), взвешивает бой и выходит из проигранного, открывает запертые комнаты настоящими ключами и обыскивает их, лутает с умом, обходит всю карту и уходит через любой выход, даже за рычагом и на бронепоезде.

Как: QuestingBots, SAIN и LootingBots делают то же, что и делали - ходят по квестам, стреляют, подбирают вещи, - а LiveRaid добавляет то, чего нет ни в одном из них: **кто такой каждый бот, что у него с собой и что он решает делать с рейдом вокруг.** Это копия ORBIT? Цель та же, путь другой: ORBIT заменяет эти моды, LiveRaid стоит поверх них и их кода в себе не содержит.

## Что нового с версии 1.0.5 (версии 1.2.4 - 1.4.16)

- **Оружие и одежда из WTT-ContentBackport у ботов** - стволы, ножи, патроны и одежда этого мода теперь появляются у ЧВК и диких (при установке мода; переключатель в F12).
- **Все выходы открыты для ботов.** Гермозатвор бункера, Д-2, убежище Развязки, ворота и лифты, канализационный люк и бронепоезд на Резерве: бот находит рычаг, дёргает, стоит таймер и уходит, как игрок. Выход, открывшийся посреди рейда, - шанс уйти «под шумок», когда на выход идут все.
- **Карта обходится равномерно, этаж за этажом.** Атлас помнит каждое место, которое боты пропустили, и почему; у каждого бота 2-4 цели на рейд; верхние этажи и «холодные углы» получают свою долю времени.
- **Мод учит карту по вашему проходу.** Где вы прошли вне навмеша, он сам пишет связку или патч (с проверкой стены) и использует со следующего рейда.
- **Ключи обрели смысл.** Ключ с шансом лежит в защищённом подсумке; бот с ключом от двери идёт к своей двери, заходит в отпертую комнату и обыскивает ВСЮ; ключи раздаются рядом с дверями, которые открывают.
- **Барахольщик** - ЧВК-одиночка, который приходит только за лутом, с хитбоксами Кабана; лутает по комнатам и от боя бежит прочь от толпы. Шанс - в F12.
- **Осторожный выбор.** Одиночка или отряд с раненым взвешивает бой, прежде чем лезть; места гибели ботов запоминаются; загнанный в угол дерётся там, где стоит; боты устраивают засады на услышанного врага.
- **16 доктрин отрядов** (новые: разведчики, засадники, волчья стая, группа поддержки), строй, который дышит, полный разлут павшего товарища.
- **Исправления ошибок EFT:** вечная перезарядка, запасные магазины у каждого ЧВК (у трети их не было), «патроны не легли в магазин», ствол без магазина ломал SAIN, самоубийства гранатой.
- **Спавн:** стартовые отряды ставятся врозь, запасные приходят не раньше N минут, необязательные поздние заходы ЧВК.

## Кто за что отвечает

| | Что делает | Чего не делает |
|---|---|---|
| **QuestingBots** | водит ботов по квестам, спавнит ЧВК | не решает, кто этот бот и что у него есть |
| **SAIN** | прицел, укрытия, сам бой | не решает, что делать с боем за 200 м |
| **LootingBots** | подбирает вещи | не решает, *подходящий ли сейчас момент* |
| **LiveRaid** | даёт каждому боту снаряжение, характер и причину; управляет тремя модами выше из одного меню F12 | не стреляет, не целится, сам не лутает |

## Что добавляет LiveRaid (и чего нет в модах, которые он расширяет)

- **У бота есть прошлое и набор.** Снаряжение и уровень ЧВК не зависят от вашего: от бомжа с пистолетом без брони до полной меты, как в онлайне. Стартуют с пустым рюкзаком и разгрузкой (только набор: оружие, магазины, немного аптечек) - лут это то, что нашли в рейде. Каждый отряд враг любому другому отряду и вам, своя сторона тоже.
- **Каждый - кто-то.** Стиль игры (кемпер, крыса, рашер, лутер, квестер, исследователь, охотник, снайпер), навык от новичка до профи, настроение (мстительный ищет того, кто в него стрелял, чудом выживший становится осторожным, нашедший дорогое торопится к выходу).
- **Лобби настоящего онлайн-рейда.** QuestingBots умеет спавнить ЧВК; LiveRaid выставляет ему полное лобби именно этой карты, добавляет запасных, отправляет игроков-диких по расписанию и на замену погибшим, а **умный баланс численности** держит число ботов под карту и время рейда: не толпа вокруг вас и не пустыня.
- **Вся карта, этаж за этажом.** QuestingBots идёт к точкам квестов. LiveRaid берёт из данных самого SPT каждую точку лута, контейнер и запертую комнату и строит по ним цели исследования, помнит, кто где был, чтобы места «остывали», и водит ботов по тропам через завалы, которые навмеш не соединяет. Пропущенные ботами места запоминаются в **атласе** и обходятся этаж за этажом в следующих рейдах. Включите запись маршрута - боты начнут ходить и по тем путям, которыми ходите **вы**, а связки и навмеш-патчи пишутся сами.
- **Ключи, которые что-то значат.** Настоящие ключи, нередко в защищённом подсумке: нет ключа - нет двери. Бот с ключом идёт к своей запертой комнате или контейнеру, заходит и обыскивает ВСЮ, а отряд этого не повторяет; у некоторых ЧВК ключ от самого богатого места карты (его можно снять с тела). Помощник открывает двери на пути, если бот застрял.
- **Решения вокруг лута.** LootingBots хватает; LiveRaid решает, *сейчас ли* время: ценность против опасности, осмотреть тело издали, обыскать всю комнату, выбросить дешёвое ради находки, вынести снаряжение павшего товарища, поделиться аптечками в отряде.
- **Решения в бою.** Стреляет SAIN; остальное решает LiveRaid: мораль и роли, отступление из проигранного боя, забывание остывшего, бросок / фланг / ожидание / засада / уход, когда бой слышен далеко, - по характеру. Отряды играют по одной из 16 доктрин и колоде из 21 тактики (фланги, клещи, дым, перебежки с прикрытием, месть за павшего).
- **Лечение и уход.** Перевязка в первую тихую минуту, шины и обезболивающие при сломанных ногах; у каждого отряда своё время ухода, ждут у выхода присев и уходят вместе. Боты пользуются **всеми выходами**, в том числе теми, что открываются рычагом (гермозатвор, Д-2, люк), и бронепоездом.
- **Исправления ошибок ИИ самой EFT.** Зависшие руки после перезарядки, выброшенные на пол магазины, огонь по своим внутри отряда, замирание под огнём, ход к выходу спиной.
- **WTT-ContentBackport у ботов.** Стволы, ножи, патроны и одежда этого мода появляются у ЧВК и диких (только если мод установлен).
- **Барахольщик.** ЧВК-одиночка, который приходит только за лутом и обходит бои.
- **После смерти - вторая жизнь.** **Только в офлайн-версии мода (без Fika) - в LiveRaid Online этой функции нет.** Свободная камера, режим наблюдателя и вселение в любого другого бота: играйте дальше за этого ЧВК или дикого с его снаряжением и инвентарём (см. «Жизнь после смерти» ниже).
- **Одно меню вместо четырёх конфигов.** Файлы остальных модов остаются оригинальными; LiveRaid выставляет нужные значения в памяти из F12 и возвращает их, когда вы выключаете функцию. Пресеты, английский и русский.

Чего он **не** делает: не улучшает прицеливание, не даёт видеть сквозь стены, не читерит, не добавляет боссов и не поднимает число диких выше лимита карты.

## Подробнее: механики мода

### Доктрины отрядов: у каждого отряда своя игра

ЧВК-отряд не просто идёт за лидером. Когда он заходит в рейд, ему выпадает одна из **16 доктрин**: штурмовики, PvP-охотники, лутеры, квестеры, кэрри (сильный ведёт слабых), рандомы, слаженный клан, охотники на боссов, тихушники, дальники, торопыги, бронированный кулак, разведчики, засадники, волчья стая и группа поддержки. Важно, кто в отряде: отряд рашеров штурмует чаще, крысы сидят тихо, отряд с длинными стволами играет дальников.

Доктрина - это набор из одиннадцати чисел (агрессия, сплочённость, темп, разговорчивость, точность, стойкость, любимая дистанция, жадность к луту, тяга ударить в спину третьей стороне, жажда мести, готовность уйти пораньше). Сверху каждому отряду достаётся **две особенности** из четырнадцати: болтливые или молчуны, торопливые или педанты, осторожные или дерзкие, разбредаются или держатся кучей, любят дальний бой или ближний, стервятники или сами по себе, мстительные, жадные. Доктрина попадается в рейде не больше двух раз, и двух одинаковых отрядов не бывает: если жребий выпал слишком похожим на отряд, уже вышедший на карту, он бросается заново.

Внутри отряда каждый играет доктрину по-своему: рашер давит сильнее товарищей, новичок хуже держит строй и на секунду-две позже понимает приказ, снайпер хочет дистанции.

### Карточки тактик

В бою лидер отряда разыгрывает карточку из колоды в **21 тактику**. Выбор зависит от доктрины, от самого боя (численность, дистанция, здоровье, раненый враг) и от того, что отряд уже разыгрывал в этом рейде: сыгранная карточка выпадает реже, так что отряд пробует разное. Карточки:

- **Прямо в лоб:** общий штурм, двойка вперёд (идут двое самых дерзких), подавление и рывок (один бежит вперёд, остальные стреляют).
- **В обход:** огневая база и обход (один идёт кругом), клещи (двое, по одному с каждой стороны), широкий обход (далеко, на сторону врага), обход по высоте (через этаж, крышу или холм над ним).
- **Хитрости:** приманка (один показывается, другой заходит сбоку), выглядывание-провокация (выглянуть, собрать огонь и убрать голову), растяжка и штурм (каждый занимает место на 10-20 с, потом все сразу), дымовая завеса.
- **Из укрытия:** держать позиции, перебежки с прикрытием (две группы по очереди каждые 6-9 с: одна идёт вперёд, другая прикрывает; слаженный отряд держит такт, рандомы нет).
- **Здания:** штурм здания (два броска до него, потом внутрь), оборона здания (держать входы, потом на него или уйти другим путём).
- **Читаем бой:** дожать раненого (товарищ попал во врага, все давят его, пока он не перевязался), преследование (враг скрылся из вида, двое самых дерзких бегут за ним), разворот на третью сторону (новые выстрелы с другой стороны).
- **Когда всё пошло не так или стало личным:** откат и перегруппировка (весь отряд отходит на 45-90 м и возвращается, скорее всего в обход), рассыпаться и уходить (бой проигран, все выходят из боя, отряд скоро уходит с карты), месть за своего (товарищ пал, отряд мстительный: все идут за убийцей).
- И **«каждый сам»** - вообще без команды, каждый по своему характеру.

Карточка раздаёт роли в бою (держать, штурм, обход), а бойцы играют их по-своему. Боец может пойти своим путём (рандомы часто, клан почти никогда). Мораль всё равно главнее любой карточки: бот, потерявший самообладание, отходит, что бы ему ни велели.

### Мораль: когда бот уходит из проигранного боя

SAIN хорошо воюет от секунды к секунде, но сам по себе отходит, только когда кончились патроны. LiveRaid продолжает взвешивать бой так, как это делает игрок. Давление «пора уходить» складывается из: врагов больше, чем своих; остался один после отряда; доля потерь в отряде; здоровье; бот прижат огнём; снаряжение врага лучше. Каждая составляющая мала, поэтому что-то одно (одиночка против двоих) редко ломает кого-то, а несколько сразу (один, ранен, трое против одного, у врага снаряжение лучше) ломают часто.

Дальше характер: стойкость умножает давление на 1,5 у самых робких и на 0,45 у самых смелых (агрессия, навык, стиль, личность). Бот проверяет себя каждые 1,5 с: стабильно сильное давление превращается в бегство секунд за двадцать, слабое не превращается никогда, а от врага ближе 12 м не бегут. Сорвавшись, бот убегает на 45-90 м, SAIN на минуту-две перестаёт считать его врагов известными, а проигравший бой бот раньше уходит к выходу.

**Настроения.** По жребию у каждого ЧВК и игрока-дикого есть некоторые из четырёх натур:
- **мстительный** - помнит, кто в него стрелял, и в тишине идёт его искать (не больше двух раз);
- **напуганный** - упал ниже 25 % здоровья и выжил, поэтому до конца рейда осторожен: меньше драк, меньше стойкости, уходит раньше;
- **в кураже** - после двух убийств за десять минут смелее, но небрежнее;
- **скряга** - с богатым рюкзаком его бережёт, избегает драк и раньше уходит.

Бомж, которому нечего терять, смелее человека с полным набором и рюкзаком лута.

### Разговоры в отряде

Первый, кто увидел врага, докладывает о контакте. Товарищи, которых QuestingBots выделил в отдельную группу, узнают об этом с задержкой: полсекунды в болтливом клане, три секунды в молчаливом, а отряд, который почти не разговаривает, иногда не передаёт вовсе. Когда падает товарищ, мстительный отряд идёт за убийцей, напуганный отходит, а отряд, потерявший лидера и не готовый мстить, скоро уходит к выходу. После боя, когда какое-то время тихо, лидер на несколько секунд останавливается, чтобы отряд собрался; перевязка и передача аптечек идут тем временем.

### Бой как у игрока

- **Смена угла.** SAIN выглядывает снова и снова из одного укрытия. Здесь после нескольких выстрелов с одного места (профи после 4-9 патронов, новичок после 18-30, длинный ствол после 2-4), когда враг спрятался, бот идёт на другое место, а не выглядывает туда, где его ждут.
- **Попали на открытом месте** с 25 м и дальше: бежать в укрытие, а не стоять и меняться выстрелами.
- **Оружие под дистанцию.** Дробовик против врага далеко за его досягаемостью или затворная винтовка в упор - не ответ, а SAIN этого не видит. Бот берёт другое оружие, или бежит ближе, или обходит, или оставляет врага в покое.
- **Оставить дикого в покое.** Лутер, крыса или тихий отряд не затевают перестрелку с ИИ-диким дальше 30 м, если он в них не стреляет: шум привёл бы ЧВК.
- **Гранаты по привычке.** Штурмовой отряд бросает перед входом, крыса бережёт гранату (бросок выдаёт), новичок забывает, что она у него есть.
- **Ночная дисциплина.** Дисциплинированные держат фонарь выключенным, пока кто-то рядом, а в ближнем контакте включают все огни, чтобы ослепить врага; отряд в засаде остаётся тёмным до первого выстрела.
- **Взвешивание ставок.** Полный набор с рюкзаком лута делает бота осторожным, нечего терять - смелым.

### Слух и взгляд

Боты слышат не только стрельбу: бег (38 м), дверь (30 м), обыск тела (14 м), перезарядку (16 м). Стена вдвое сокращает дальность, другой этаж - до 40 %. Что делает бот, зависит от того, кто он: пойти посмотреть, обойти, остановиться и прислушаться, тихо уйти или не обращать внимания. Живой игрок шумит как все, и его тоже ищут.

Двери тоже не бросают открытыми: крысы и слаженные отряды прикрывают дверь за собой, осторожные останавливаются и слушают у закрытой. Бот, знающий, что рядом неспокойно, на ходу заглядывает в дверные проёмы. А осторожный бот не пересекает чистое поле так, как повёл бы кратчайший путь QuestingBots: он бежит вдоль опушки, стены, машин.

### Охота на боссов

В онлайне некоторые отряды идут за Решалой, Глухарём, Санитаром или Тагиллой ради снаряжения босса. Отряды «охотники на боссов» всегда, около трети штурмовиков и часть PvP-охотников делают босса карты своей целью через 2-8 минут после начала рейда. Лидер ведёт отряд туда по этапам; охота отменяется, если отряд сильно потрёпан, через 14 минут или когда босс убит; тогда охотники поблизости обыскивают его тело и тела его охраны. Сами боссы не затронуты.

### Снаряжение павшего товарища

В онлайне товарищ приходит за лутом друга, когда стихнет. Здесь так же, и даже больше: у тела бот меняет свои находки на оружие, броню, шлем и рюкзак друга; то, что не поместилось, он уносит в укромное место в 6-22 м (куст, угол) или уходит с тем, что есть. Пока убийца ближе 120 м к телу, никто не идёт.

### Выход из рейда: любой выход, даже за рычагом

Раньше боты уходили только через выходы без условий. Теперь они уходят **как игроки**:

- **Выходы через рубильник.** Гермозатвор бункера и Д-2 (Резерв), убежище Развязки, ворота и лифты. Бот находит первый ещё не включённый рычаг цепочки выхода, бежит к нему, дёргает, а когда выход открылся - бежит туда, стоит таймер и уходит. Отряд делает это вместе. К рычагам вне навмеша бот идёт до конца пути и ждёт.
- **Канализационный люк** не берёт никого с рюкзаком: бот бросает рюкзак у люка и уходит.
- **Бронепоезд** (Резерв): боты знают его правила (приходит во второй половине рейда, открыт, пока стоит, уходит через 5 с после отсчёта), бегут к зоне, **заходят внутрь вагона и расходятся по нему** и уходят с отправлением. *Посадка в вагон - новая, в многих рейдах её ещё не видели.*
- **«Под шумок».** Выход, открывшийся после старта (рычаг, питание, поезд, таймер), - событие: каждый свободный ЧВК или игрок-дикий не в бою бросает кубик (крыса и добытчик чаще, квестер реже), победитель уходит, пока выход открыт. Шанс - в F12.

### Вся карта: обход, атлас, выученные пути

- **Равномерный обход.** Регион делится на части по 12-20 мест, у каждого места свой квест; «холодные углы», где никто не бывал, тянут сильнее с 4-й по 12-ю минуту.
- **Цели рейда.** У каждого ЧВК и игрока-дикого 2-4 цели по характеру (район для лута, свой квест, высота, холодный угол); верхние этажи получают свою долю времени.
- **Атлас.** Каждое пропущенное ботами место запоминается с причиной; квесты-обходы идут этаж за этажом по недостигнутым местам, вес растёт с каждым рейдом, где место пропустили.
- **Ваш проход учит карту.** Где вы прошли вне навмеша, мод учит связку или навмеш-патч (с проверкой стены) и использует со следующего рейда, вам ничего делать не нужно.

### Барахольщик

ЧВК-одиночка приходит в рейд с пистолетом и одним из самых больших рюкзаков только ради лута, обходит бои и имеет хитбокс Кабана (грудь 355, живот 260). Лутает по комнатам, от боя бежит прочь от толпы, не реагирует на врагов дальше дистанции тревоги (F12, 70 м) и уходит домой только с добычей. Шанс его появления настраивается в F12 («Спавн»).

### WTT-ContentBackport: оружие и одежда для ботов

Если установлен мод WTT-ContentBackport, его стволы, ножи и патроны попадают в пулы ЧВК (дешёвые - диким), а одежда - во внешность ЧВК по стороне. Переключатель есть в F12 и виден только при установленной клиентской части этого мода.

### Жизнь после смерти: играйте дальше за любого бота

> **Только в офлайн-версии мода (без Fika) - в LiveRaid Online этой функции нет.**

Смерть не обязана заканчивать рейд. Над картой отрывается свободная камера; наведитесь на любого живого ЧВК или дикого и нажмите **F** - теперь вы этот бот, **с его оружием, бронёй, разгрузкой, рюкзаком и всем, что в них лежит**: стреляйте, лутайте, лечитесь, открывайте его инвентарь, идите к его выходу. Погиб - вы снова в свободной камере и можете вселиться в следующего; **Delete** возвращает бота его ИИ. Включается в F12 → «После смерти» → «Вселяться в бота». То, что делает бот (убийства, лут, его выход), в ваш профиль не идёт: рейд всё равно заканчивается вашей смертью. Если у бота, в которого вы вселились, не работают руки (оружие не стреляет, не перезаряжается) - выйдите из него клавишей Delete и вселитесь снова.

## Глубина: пресеты

| Пресет | Коротко |
|---|---|
| **Рекомендуемый** | всё включено, как играю я |
| **Слабый ПК** | дальние боты упрощены и усыплены, ботов меньше, без подробных журналов |
| **Тихие рейды** | меньше ботов и ЧВК, спокойнее |
| **Людные рейды** | больше ботов и ЧВК, больше запасных |
| **Только исправления** | всё поведение LiveRaid выключено, остаются исправления ошибок EFT и лечение |

Каждую группу функций (ключи, жизнь на карте, характеры, спавн, лут, отряды, бой, лечение, маршруты) можно отключить отдельно. У каждой настройки есть подсказка.

## Установка

1. Поставьте нужные моды (ниже), убедитесь, что SPT 4.1.6 запускается. Закройте игру и сервер.
2. Скачайте **`LiveRaid-<версия>.zip`**: один архив с двумя равноправными папками, `Single` (одиночная игра) и `Online` (кооператив через Project Fika).
3. Распакуйте нужную папку в корень папки игры (где `EscapeFromTarkov.exe`), согласившись на замену файлов. **LiveRaid Launcher** (в архиве) потом переключает редакцию.
4. Запустите сервер и игру. F12 → LiveRaid, выберите пресет или оставьте «Рекомендуемый».
5. Проверка: в `BepInEx/LogOutput.log` есть `LiveRaid <версия> loaded` без `disabled: …` после неё.

Обновление: так же, поверх старых файлов; настройки и записанные маршруты сохраняются. Удаление: удалите `BepInEx/plugins/LiveRaid`, `SPT_Runtime/user/mods/LiveRaid`, `LiveRaid Launcher.exe` и папку `LiveRaid Launcher`.

## Требования и совместимость

SPT **4.1.6** и: **QuestingBots 1.2.0**, **SAIN 4.5.1**, **LootingBots 1.8.0**, **BigBrain 1.5.0**, **Waypoints 1.9.0**. *SAIN 4.5.1 - последняя версия на Forge; со старыми 4.4.x часть поведения может работать хуже или не работать.* **Очень рекомендуется: Smoothie 1.1.0** (автор morphox, https://sp-mod.com/mod/3132/smoothie) - рисует только то, что вы реально видите; заметный прирост производительности без ущерба для LiveRaid (с 20+ живыми ботами выигрыш больше всего). Отдельное спасибо автору. **Совместим с WTT-ContentBackport** (необязательно): при его установке боты носят и его оружие и одежду. Для **LiveRaid Online** ещё **Project Fika 2.4.x** и **QuestingBots Fika sync**: боты живут у хоста, настройки задаёт хост. Если сервер SPT стоит на другом компьютере (выделенный или headless), вставьте текст файла `user/mods/LiveRaid/host.token` с сервера в поле **Host key** файла `BepInEx/config/com.lir.liveraid.cfg` (раздел `0. Online`) на машине игрока, который должен менять настройки, и перезапустите игру.

С **ORBIT** не совместим - удалите. Другие моды, меняющие то же самое (спавн, квесты, лут), будут конфликтовать с LiveRaid; оставьте один. **LiveRaid Launcher** (в архиве) переключает одиночный ↔ Online и стабильную ↔ тестовую ветку одним нажатием.

## Известные проблемы

- **Вселение в бота** (только офлайн-версия): если у бота, в которого вы вселились, не работают руки (оружие не стреляет, не перезаряжается), выйдите из него (Delete) и вселитесь снова.

## Сообщить о проблеме

Опишите, что делали, на какой карте и в какую минуту, и приложите `BepInEx/LogOutput.log` (и самый свежий файл из `BepInEx/plugins/LiveRaid/logs`, если можете). Логи можно скинуть мне в Discord: **n1x7192**, или открыть issue на GitHub: https://github.com/Lir-byte/LiveRaid . Спасибо авторам QuestingBots (DanW), SAIN (Solarint), LootingBots (Skwizzy), BigBrain и Waypoints (DrakiaXYZ), командам SPT и Fika.
