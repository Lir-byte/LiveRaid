# LiveRaid - bots that play like people online (SPT 4.1.6)

**Version 1.2.4** · by **Lir** · [CC BY-NC-SA 4.0](LICENSE)

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
- **Decisions in a fight.** SAIN shoots; LiveRaid adds morale and roles, retreat from a lost fight, forgetting a cold one, and rush / flank / hold / ambush / leave when a fight is heard far away. Squads play one of 12 doctrines and a deck of 21 tactics.
- **Healing and leaving.** Bandage at the first quiet moment, splints and painkillers for legs; each squad has its own time to leave and goes out together.
- **Repairs of EFT's own AI bugs.** Stuck hands, reload flags, magazines thrown away, friendly fire inside a squad, bots frozen under fire.
- **After death - a second life.** A free camera, an observer mode, and taking over any other bot: play on as that PMC or scav with its own gear and inventory (see *Life after death* below).

It does **not** make bots aim better or cheat, adds no bosses and never goes above the map's own scav limit. Presets (Recommended, Weak PC, Quiet raids, Busy raids, Fixes only) set the depth; every group of features can be switched off on its own.

## In detail: the mod's mechanics

### Squad doctrines: every squad plays its own game

A PMC squad doesn't just follow its leader. When it comes in, it draws one of **12 doctrines**: storm troopers, PvP hunters, looters, questers, carry (the strong one leads the weak), randoms, a tight clan, boss hunters, the quiet ones, marksmen, speedrunners, armoured fist. Who is in the squad matters: a squad of rushers storms more often, rats keep quiet, a squad of long guns plays marksmen.

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

### Life after death: play on as any bot

Your death does not have to end the raid. A free camera comes loose over the map; aim at any living PMC or scav and press **F** - you are that bot now, with **its own gun, armour, rig, backpack and everything in them**: shoot, loot, heal, open its inventory, walk to its exit. When it dies, you are back in the free camera and can take over the next one; **Delete** hands it back to its AI. Switch it on in F12 → *After death* → *Take over a bot*. What the bot does - kills, loot, its exit - does not go to your profile: the raid still ends as your death. If the hands of the bot you took do not work (the gun does not fire or reload), leave it with Delete and take it over again.

## Requirements

SPT **4.1.6** (EFT 0.16.9) and these mods (the versions LiveRaid is built and tested against):

| Mod | Version | Needed for |
|---|---|---|
| QuestingBots | 1.2.0 | bot quests and spawning |
| SAIN | 4.5.1 | combat AI |
| LootingBots | 1.8.0 | looting |
| BigBrain | 1.5.0 | AI layers (needed by the three above) |
| Waypoints | 1.9.0 | bot navigation |
| Smoothie *(recommended)* | 1.1.0 | renders only what you see: a big performance gain, no downside for LiveRaid (by morphox) |
| Project Fika + QuestingBots Fika sync | 2.4.x | **LiveRaid Online** only |

Not compatible with **ORBIT** (remove it). Install guide: [INSTALL.md](INSTALL.md).

## Build

```
dotnet build LiveRaid.sln -c Release
dotnet build src/LiveRaid.Client/LiveRaid.Client.csproj -c Release -p:Edition=Online
```

`-p:Deploy=false` builds without copying into the game; `-p:SptRoot=<folder>` points to the game. .NET 10 SDK (pinned in `global.json`). Archives: `python -I tools/make_release.py <version>`. Checks without the game: `dotnet run --project tools/bindtest`. The architecture and the history of the work are in [PROJECT_STATE.md](PROJECT_STATE.md) (Russian).

## Reporting a problem

Say what you did, which map and which minute, and attach `BepInEx/LogOutput.log` (and the newest file from `BepInEx/plugins/LiveRaid/logs` if you can). Send the logs to me on Discord: **n1x7192**, or open an issue here on GitHub.

## Credits and licence

By **Lir**. Licensed under [Creative Commons Attribution-NonCommercial-ShareAlike 4.0](LICENSE): share and adapt with credit, not for commerce, adaptations under the same licence. LiveRaid does not contain code of the mods it extends; see [NOTICE.md](NOTICE.md). Thanks to the authors of QuestingBots (DanW), SAIN (Solarint), LootingBots (Skwizzy), BigBrain and Waypoints (DrakiaXYZ), Smoothie (morphox - the performance mod LiveRaid is best played with), the SPT and Fika teams.

---

# LiveRaid - боты как в онлайне (SPT 4.1.6)

**Версия 1.2.4** · автор **Lir** · лицензия [CC BY-NC-SA 4.0](LICENSE)

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
- **Решения в бою.** Стреляет SAIN; LiveRaid добавляет мораль и роли, отступление из проигранного боя, забывание остывшего и бросок / фланг / ожидание / засаду / уход, когда бой слышен далеко. Отряды играют по одной из 12 доктрин и колоде из 21 тактики.
- **Лечение и уход.** Перевязка в первую тихую минуту, шины и обезболивающие для ног; у каждого отряда своё время ухода, уходят вместе.
- **Исправления ошибок ИИ самой EFT.** Зависшие руки, флаги перезарядки, выброшенные магазины, огонь по своим внутри отряда, замирание под огнём.
- **После смерти - вторая жизнь.** Свободная камера, режим наблюдателя и вселение в любого другого бота: играйте дальше за этого ЧВК или дикого с его снаряжением и инвентарём (см. «Жизнь после смерти» ниже).

Он **не** улучшает прицеливание и не читерит, не добавляет боссов и не поднимает число диких выше лимита карты. Глубину задают пресеты (Рекомендуемый, Слабый ПК, Тихие рейды, Людные рейды, Только исправления); каждую группу функций можно отключить отдельно.

## Подробнее: механики мода

### Доктрины отрядов: у каждого отряда своя игра

ЧВК-отряд не просто идёт за лидером. Когда он заходит в рейд, ему выпадает одна из **12 доктрин**: штурмовики, PvP-охотники, лутеры, квестеры, кэрри (сильный ведёт слабых), рандомы, слаженный клан, охотники на боссов, тихушники, дальники, торопыги, бронированный кулак. Важно, кто в отряде: отряд рашеров штурмует чаще, крысы сидят тихо, отряд с длинными стволами играет дальников.

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

### Жизнь после смерти: играйте дальше за любого бота

Смерть не обязана заканчивать рейд. Над картой отрывается свободная камера; наведитесь на любого живого ЧВК или дикого и нажмите **F** - теперь вы этот бот, **с его оружием, бронёй, разгрузкой, рюкзаком и всем, что в них лежит**: стреляйте, лутайте, лечитесь, открывайте его инвентарь, идите к его выходу. Погиб - вы снова в свободной камере и можете вселиться в следующего; **Delete** возвращает бота его ИИ. Включается в F12 → «После смерти» → «Вселяться в бота». То, что делает бот (убийства, лут, его выход), в ваш профиль не идёт: рейд всё равно заканчивается вашей смертью. Если у бота, в которого вы вселились, не работают руки (оружие не стреляет, не перезаряжается) - выйдите из него клавишей Delete и вселитесь снова.

## Требования

SPT **4.1.6** (EFT 0.16.9) и моды (версии, под которые собран и проверен LiveRaid): QuestingBots 1.2.0, SAIN 4.5.1, LootingBots 1.8.0, BigBrain 1.5.0, Waypoints 1.9.0; для **LiveRaid Online** ещё Project Fika 2.4.x и QuestingBots Fika sync. **Очень рекомендуется Smoothie 1.1.0** (автор morphox): рисует только то, что вы видите, - заметный прирост производительности без ущерба для LiveRaid. С **ORBIT** не совместим (удалите). Установка - [INSTALL.md](INSTALL.md).

Сборка и устройство мода - выше и в [PROJECT_STATE.md](PROJECT_STATE.md).

## Сообщить о проблеме

Опишите, что делали, на какой карте и в какую минуту, и приложите `BepInEx/LogOutput.log` (и самый свежий файл из `BepInEx/plugins/LiveRaid/logs`, если можете). Логи можно скинуть мне в Discord: **n1x7192**, или открыть issue здесь, на GitHub.

## Автор и лицензия

Автор - **Lir**. Лицензия [CC BY-NC-SA 4.0](LICENSE): можно делиться и изменять с указанием автора, не для коммерции, производные работы - на тех же условиях. Кода модов, которые LiveRaid расширяет, в нём нет ([NOTICE.md](NOTICE.md)). Спасибо авторам QuestingBots (DanW), SAIN (Solarint), LootingBots (Skwizzy), BigBrain и Waypoints (DrakiaXYZ), Smoothie (morphox - мод производительности, с которым LiveRaid играется лучше всего), командам SPT и Fika.
