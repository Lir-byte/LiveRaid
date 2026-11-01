**License: CC BY-NC-SA 4.0** - credit Lir, non-commercial, adaptations under the same licence ([text](https://creativecommons.org/licenses/by-nc-sa/4.0/), also `LICENSE.txt` in the archive).  
**🇷🇺 Описание на русском - ниже на этой же странице** ([перейти](#русский)).

> ⚠ **Taking over a bot after death is raw and often breaks** (textures may stop loading, sound may go wrong). It is off by default; everything else works without it.

# English

## What it is, in two sentences

LiveRaid does **not** replace QuestingBots, SAIN or LootingBots. They keep doing what they do - walk quests, shoot, pick things up - and I added the part none of them has: **who each bot is, what it carries, and what it decides to do with the raid around it.**

Is it a copy of ORBIT? Same goal, different way: ORBIT replaces those mods, LiveRaid sits on top of them and does not contain their code.

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
- **The whole map, floor by floor.** QuestingBots goes to quest points. LiveRaid reads every loot spawn, container and locked room from SPT's own data and builds exploration goals over all of it, remembers who has been where so places "cool down", and walks routes over rubble the navmesh doesn't connect. Switch on the route recorder and the bots also learn the paths **you** walk.
- **Keys that matter.** Real key items in pockets: no key, no door. A bot with a key walks to its locked room or container; some PMCs carry the key to the richest place of the map (you can take it off the body). A helper opens doors in the way when a bot gets stuck.
- **Decisions around loot.** LootingBots grabs; LiveRaid decides whether *now* is the time: value against danger, look at a body before walking up to it, search a whole room, drop cheap stuff for a find, carry out a fallen mate's kit, share meds in the squad.
- **Decisions in a fight.** SAIN does the shooting; LiveRaid decides the rest: morale and roles, retreating from a lost fight, forgetting a cold one, rush / flank / hold / ambush / leave when a fight is heard far away, by character. Squads play one of 12 doctrines and a deck of 18 tactics (flanks, pincers, smoke, bounds under cover, revenge for a fallen mate).
- **Healing and leaving.** Bandage at the first quiet moment, splints and painkillers for broken legs; each squad has its own time to leave, waits crouched at the exit and goes out together.
- **Repairs of EFT's own AI bugs.** Stuck hands after a reload, magazines thrown on the floor, friendly fire inside a squad, bots frozen under fire, bots walking to the exit backwards.
- **After death.** A free camera and an observer mode - watch the raid without being in it.
- **One menu instead of four config files.** The other mods' files stay original; LiveRaid sets the values it needs in memory from F12 and gives them back when you switch a feature off. Presets, English and Russian.

What it does **not** do: it doesn't make bots aim better, see through walls or cheat, it adds no bosses and never goes above the map's own scav limit.

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
2. Download **`LiveRaid-<version>.zip`** (single player) or **`LiveRaid-Online-<version>.zip`** (co-op through Project Fika).
3. Unpack into the game's root folder (where `EscapeFromTarkov.exe` is), agreeing to replace files.
4. Start the server and the game. F12 → LiveRaid, pick a preset or leave *Recommended*.
5. Check `BepInEx/LogOutput.log` for `LiveRaid <version> loaded` with no `disabled: …` after it.

Updating: the same way over the old files; your settings and recorded routes are kept. Removing: delete `BepInEx/plugins/LiveRaid`, `SPT_Runtime/user/mods/LiveRaid`, `LiveRaid Launcher.exe` and the `LiveRaid Launcher` folder.

## Requirements and compatibility

SPT **4.1.6** and: **QuestingBots 1.2.0**, **SAIN 4.5.1**, **LootingBots 1.8.0**, **BigBrain 1.5.0**, **Waypoints 1.9.0**. *The Forge lists SAIN 4.4.4 at the moment, but LiveRaid is built and tested with **4.5.1** - get it from the SAIN author; with 4.4.x some behaviour may work worse or not at all.* **LiveRaid Online** also needs **Project Fika 2.4.x** and **QuestingBots Fika sync**: the bots live on the host, the host's settings apply to everybody.

Not compatible with **ORBIT** - remove it. Other mods that change the same things (spawns, quests, looting) will fight with LiveRaid; keep one of them. **LiveRaid Launcher** (in the archive) switches single player ↔ Online and the stable ↔ test branch in one click.

## Known problems

- **Taking over a bot is raw** (see the warning at the top).
- The newest behaviour (population balance, standoff breaker, quick idle watchdog) was written from the logs of a few raids and has not been through many long ones. If a bot does something silly, tell me.

## Reporting a problem

Say what you did, which map and which minute, and attach `BepInEx/LogOutput.log` (and the newest file from `BepInEx/plugins/LiveRaid/logs` if you can). Send the logs to me on Discord: **n1x7192**, or open an issue on GitHub: https://github.com/Lir-byte/LiveRaid . Credits: QuestingBots (DanW), SAIN (Solarint), LootingBots (Skwizzy), BigBrain and Waypoints (DrakiaXYZ), the SPT and Fika teams.

---

# Русский

## Что это, в двух предложениях

LiveRaid **не заменяет** QuestingBots, SAIN и LootingBots. Они делают то же, что и делали - ходят по квестам, стреляют, подбирают вещи, - а я добавил то, чего нет ни в одном из них: **кто такой каждый бот, что у него с собой и что он решает делать с рейдом вокруг.**

Это копия ORBIT? Цель та же, путь другой: ORBIT заменяет эти моды, LiveRaid стоит поверх них и их кода в себе не содержит.

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
- **Вся карта, этаж за этажом.** QuestingBots идёт к точкам квестов. LiveRaid берёт из данных самого SPT каждую точку лута, контейнер и запертую комнату и строит по ним цели исследования, помнит, кто где был, чтобы места «остывали», и водит ботов по тропам через завалы, которые навмеш не соединяет. Включите запись маршрута - боты начнут ходить и по тем путям, которыми ходите **вы**.
- **Ключи, которые что-то значат.** Настоящие ключи в карманах: нет ключа - нет двери. Бот с ключом идёт к своей запертой комнате или контейнеру; у некоторых ЧВК ключ от самого богатого места карты (его можно снять с тела). Помощник открывает двери на пути, если бот застрял.
- **Решения вокруг лута.** LootingBots хватает; LiveRaid решает, *сейчас ли* время: ценность против опасности, осмотреть тело издали, обыскать всю комнату, выбросить дешёвое ради находки, вынести снаряжение павшего товарища, поделиться аптечками в отряде.
- **Решения в бою.** Стреляет SAIN; остальное решает LiveRaid: мораль и роли, отступление из проигранного боя, забывание остывшего, бросок / фланг / ожидание / засада / уход, когда бой слышен далеко, - по характеру. Отряды играют по одной из 12 доктрин и колоде из 18 тактик (фланги, клещи, дым, перебежки с прикрытием, месть за павшего).
- **Лечение и уход.** Перевязка в первую тихую минуту, шины и обезболивающие при сломанных ногах; у каждого отряда своё время ухода, ждут у выхода присев и уходят вместе.
- **Исправления ошибок ИИ самой EFT.** Зависшие руки после перезарядки, выброшенные на пол магазины, огонь по своим внутри отряда, замирание под огнём, ход к выходу спиной.
- **После смерти.** Свободная камера и режим наблюдателя - смотреть рейд, не участвуя в нём.
- **Одно меню вместо четырёх конфигов.** Файлы остальных модов остаются оригинальными; LiveRaid выставляет нужные значения в памяти из F12 и возвращает их, когда вы выключаете функцию. Пресеты, английский и русский.

Чего он **не** делает: не улучшает прицеливание, не даёт видеть сквозь стены, не читерит, не добавляет боссов и не поднимает число диких выше лимита карты.

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
2. Скачайте **`LiveRaid-<версия>.zip`** (одиночная игра) или **`LiveRaid-Online-<версия>.zip`** (кооператив через Project Fika).
3. Распакуйте в корень папки игры (где `EscapeFromTarkov.exe`), согласившись на замену файлов.
4. Запустите сервер и игру. F12 → LiveRaid, выберите пресет или оставьте «Рекомендуемый».
5. Проверка: в `BepInEx/LogOutput.log` есть `LiveRaid <версия> loaded` без `disabled: …` после неё.

Обновление: так же, поверх старых файлов; настройки и записанные маршруты сохраняются. Удаление: удалите `BepInEx/plugins/LiveRaid`, `SPT_Runtime/user/mods/LiveRaid`, `LiveRaid Launcher.exe` и папку `LiveRaid Launcher`.

## Требования и совместимость

SPT **4.1.6** и: **QuestingBots 1.2.0**, **SAIN 4.5.1**, **LootingBots 1.8.0**, **BigBrain 1.5.0**, **Waypoints 1.9.0**. *На хабе сейчас SAIN 4.4.4, а LiveRaid собран и проверен с **4.5.1** - возьмите его у автора SAIN; с 4.4.x часть поведения может работать хуже или не работать.* Для **LiveRaid Online** ещё **Project Fika 2.4.x** и **QuestingBots Fika sync**: боты живут у хоста, настройки задаёт хост.

С **ORBIT** не совместим - удалите. Другие моды, меняющие то же самое (спавн, квесты, лут), будут конфликтовать с LiveRaid; оставьте один. **LiveRaid Launcher** (в архиве) переключает одиночный ↔ Online и стабильную ↔ тестовую ветку одним нажатием.

## Известные проблемы

- **Вселение в бота сырое** (см. предупреждение вверху).
- Новейшее поведение (баланс численности, выход из тупика, быстрый сторож простоя) написано по логам нескольких рейдов и ещё не прошло много длинных. Если бот делает глупость, напишите.

## Сообщить о проблеме

Опишите, что делали, на какой карте и в какую минуту, и приложите `BepInEx/LogOutput.log` (и самый свежий файл из `BepInEx/plugins/LiveRaid/logs`, если можете). Логи можно скинуть мне в Discord: **n1x7192**, или открыть issue на GitHub: https://github.com/Lir-byte/LiveRaid . Спасибо авторам QuestingBots (DanW), SAIN (Solarint), LootingBots (Skwizzy), BigBrain и Waypoints (DrakiaXYZ), командам SPT и Fika.
