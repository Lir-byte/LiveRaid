using BepInEx.Configuration;
using UnityEngine;

namespace LiveRaid.Client
{
    /// <summary>
    /// Every F12 setting. The names in the config file (section / key) stay as they always were - a renamed key would lose what the player set;
    /// what F12 shows (Russian name, description, group, order, visibility) comes from SettingsMenu. The compact menu: each group has a pack switch
    /// (all of its features at once) or a level choice that sets several values together; "Developer mode" opens every single setting.
    /// </summary>
    internal static class Settings
    {
        public static ConfigEntry<bool> Enabled;
        public static ConfigEntry<bool> DevMode;
        public static ConfigEntry<string> MenuStatus;
        public static ConfigEntry<LogDetail> LogLevel;
        public static ConfigEntry<bool> CoverageLog;
        public static ConfigEntry<bool> LogNetwork;
        public static ConfigEntry<bool> EncounterLog;
        public static ConfigEntry<bool> SmoothDecisions;
        public static ConfigEntry<bool> Harmonize;
        public static ConfigEntry<bool> SmoothCombatDecisions;

        public static ConfigEntry<bool> RealKeys;
        public static ConfigEntry<float> PmcKeyChance;
        public static ConfigEntry<float> ScavKeyChance;
        public static ConfigEntry<int> MaxKeysPerBot;
        public static ConfigEntry<bool> ContainerKeys;
        public static ConfigEntry<bool> PrizeKeys;
        public static ConfigEntry<float> PrizeKeyChance;
        public static ConfigEntry<float> PrizeKeyStep;
        public static ConfigEntry<bool> DoorAssist;
        public static ConfigEntry<bool> FreeShutIn;

        public static ConfigEntry<bool> MapWideQuests;
        public static ConfigEntry<bool> DatabaseLootSpots;
        public static ConfigEntry<float> RoamRegionSize;
        public static ConfigEntry<float> RoamDesirability;
        public static ConfigEntry<bool> ScavsQuest;
        public static ConfigEntry<bool> ScavPatrol;
        public static ConfigEntry<bool> PlayerScavLife;
        public static ConfigEntry<bool> BossHunts;
        public static ConfigEntry<bool> NoiseResponse;
        public static ConfigEntry<bool> Intercept;
        public static ConfigEntry<float> InterceptWeight;
        public static ConfigEntry<bool> FightLikePlayer;
        public static ConfigEntry<bool> GrenadeHabit;
        public static ConfigEntry<bool> SpecialExits;
        public static ConfigEntry<bool> ExitArrival;
        public static ConfigEntry<bool> ExitWait;
        public static ConfigEntry<bool> CarLeaves;
        public static ConfigEntry<bool> GuardsCare;
        public static ConfigEntry<bool> ShedWeight;
        public static ConfigEntry<int> ShedWeightChance;
        public static ConfigEntry<bool> WeightMatters;
        public static ConfigEntry<bool> FoundKeys;
        public static ConfigEntry<bool> StormStims;
        public static ConfigEntry<bool> DoorManners;
        public static ConfigEntry<bool> RoomPeek;
        public static ConfigEntry<bool> CoverRoutes;
        public static ConfigEntry<bool> HotZones;
        public static ConfigEntry<bool> PeopleCorridors;
        public static ConfigEntry<bool> TempoLikePlayers;
        public static ConfigEntry<int> CorridorStrength;
        public static ConfigEntry<bool> MoveOn;
        public static ConfigEntry<bool> HotDrop;
        public static ConfigEntry<bool> KeyPlaces;
        public static ConfigEntry<bool> RoomSweep;
        public static ConfigEntry<bool> ThoroughLooting;
        public static ConfigEntry<bool> LearnFromMyRoute;
        public static ConfigEntry<bool> BotsVisitLearned;
        public static ConfigEntry<bool> UseLearnedShortcuts;

        public static ConfigEntry<bool> Personalities;
        public static ConfigEntry<bool> SkillSpread;
        public static ConfigEntry<float> MemoryMultiplier;
        public static ConfigEntry<float> MaxSearchWait;
        public static ConfigEntry<bool> FarBotsFullSenses;
        public static ConfigEntry<bool> LodByCamera;
        public static ConfigEntry<int> LodNear;
        public static ConfigEntry<bool> LodAdaptive;
        public static ConfigEntry<bool> QbSleeping;
        public static ConfigEntry<bool> PersonalExtract;
        public static ConfigEntry<bool> ExtractCurve;
        public static ConfigEntry<bool> SwitchExits;

        public static ConfigEntry<bool> SimplifyDistantBots;

        public static ConfigEntry<bool> OnlineSpawns;
        public static ConfigEntry<bool> BalancedSpawnSides;
        public static ConfigEntry<bool> BiggerLobby;
        public static ConfigEntry<int> BiggerLobbyCount;
        public static ConfigEntry<int> PackratChance;
        public static ConfigEntry<int> PmcSoloShare;

        public static ConfigEntry<bool> LootThinking;
        public static ConfigEntry<bool> NoLootInDanger;
        public static ConfigEntry<bool> LootNerve;
        public static ConfigEntry<bool> LootWalk;
        public static ConfigEntry<bool> CleanUpAfterFight;
        public static ConfigEntry<bool> NoticeBodies;
        public static ConfigEntry<bool> LootGivenFirst;
        public static ConfigEntry<bool> WoundedLoot;
        public static ConfigEntry<bool> NoJumpOnBrokenLegs;
        public static ConfigEntry<bool> NeverIdle;
        public static ConfigEntry<bool> LootProne;
        public static ConfigEntry<int> LootProneChance;
        public static ConfigEntry<bool> LootStill;
        public static ConfigEntry<bool> LootRightSide;
        public static ConfigEntry<bool> MapBalance;
        public static ConfigEntry<int> MapBalanceStrength;
        public static ConfigEntry<int> MapBalanceUneven;
        public static ConfigEntry<float> LootSpeed;
        public static ConfigEntry<bool> DropCheapForBetter;
        public static ConfigEntry<bool> SquadShare;
        public static ConfigEntry<bool> SquadTactics;
        public static ConfigEntry<bool> TightSquads;
        public static ConfigEntry<bool> SquadLootWait;
        public static ConfigEntry<float> SquadLootSpread;
        public static ConfigEntry<bool> HelpSquadmates;
        public static ConfigEntry<bool> RecoverFallenMate;
        public static ConfigEntry<bool> MateFullKit;
        public static ConfigEntry<int> MateStashChance;
        public static ConfigEntry<float> MateStashRadius;
        public static ConfigEntry<bool> ExtractTogether;
        public static ConfigEntry<bool> ExitsBySide;
        public static ConfigEntry<bool> SquadFormation;
        public static ConfigEntry<bool> SquadVoice;
        public static ConfigEntry<bool> SquadDoctrines;
        public static ConfigEntry<bool> SquadPlaybook;
        public static ConfigEntry<bool> SquadListen;
        public static ConfigEntry<bool> SquadComms;
        public static ConfigEntry<bool> PeekBait;
        public static ConfigEntry<bool> ThirdPartyTurn;
        public static ConfigEntry<bool> SmokeScreens;
        public static ConfigEntry<bool> Swivel;
        public static ConfigEntry<bool> Overlook;
        public static ConfigEntry<bool> Overwatch;
        public static ConfigEntry<bool> RaidMoods;
        public static ConfigEntry<bool> SelfPreservation;
        public static ConfigEntry<bool> NightDiscipline;
        public static ConfigEntry<bool> AirdropEvent;
        public static ConfigEntry<bool> SummaryOnQuit;
        public static ConfigEntry<bool> SquadFlanking;
        public static ConfigEntry<bool> SquadSpreadOut;
        public static ConfigEntry<bool> SquadTrade;
        public static ConfigEntry<bool> CombatMorale;
        public static ConfigEntry<bool> GunfireResponse;
        public static ConfigEntry<float> GunfireRange;
        public static ConfigEntry<float> DistantFightRange;
        public static ConfigEntry<bool> FleeMorale;
        public static ConfigEntry<float> FleeChance;
        public static ConfigEntry<bool> ColdFights;
        public static ConfigEntry<float> ColdFightPatience;
        public static ConfigEntry<bool> SearchFullPace;
        public static ConfigEntry<bool> HealFirst;
        public static ConfigEntry<bool> HealBreakOff;
        public static ConfigEntry<bool> CloseNotice;
        public static ConfigEntry<bool> FireDiscipline;
        public static ConfigEntry<bool> BlindFireHold;
        public static ConfigEntry<bool> FrozenWatch;
        public static ConfigEntry<bool> LayerCalm;
        public static ConfigEntry<bool> PressHurt;
        public static ConfigEntry<bool> QuestDriveOn;
        public static ConfigEntry<bool> QuestSpread;
        public static ConfigEntry<float> QuestStepGiveUpMin;
        public static ConfigEntry<bool> SquadmatesNeverEnemies;
        public static ConfigEntry<bool> KeepMagazines;
        public static ConfigEntry<bool> BottomlessAmmo;
        public static ConfigEntry<int> BottomlessReserve;
        public static ConfigEntry<bool> SummonedYield;
        public static ConfigEntry<bool> AimWithoutShotWatch;
        public static ConfigEntry<bool> ExfilPatchUp;
        public static ConfigEntry<bool> BtrPatience;
        public static ConfigEntry<bool> TreatLegs;
        public static ConfigEntry<bool> LegSprintLimit;
        public static ConfigEntry<bool> UsableMedkit;
        public static ConfigEntry<bool> SurgeryFromBag;
        public static ConfigEntry<bool> FloorAware;
        public static ConfigEntry<bool> FightersOnly;
        public static ConfigEntry<bool> LongGunHolds;
        public static ConfigEntry<bool> GunRange;
        public static ConfigEntry<bool> SidearmCare;
        public static ConfigEntry<bool> DogFightSanity;
        public static ConfigEntry<bool> BushSight;
        public static ConfigEntry<float> GunRangeMargin;
        public static ConfigEntry<string> GunRangeMarginClasses;
        public static ConfigEntry<bool> SteadyWeapons;
        public static ConfigEntry<bool> ReturnFire;
        public static ConfigEntry<bool> FreeStuckHands;
        public static ConfigEntry<bool> SurgeryWhenSafe;
        public static ConfigEntry<bool> ReloadUnderFire;
        public static ConfigEntry<bool> FightRoles;
        public static ConfigEntry<bool> KnifeAtArmsLength;
        public static ConfigEntry<bool> BackOffWhenStuck;
        public static ConfigEntry<bool> Eviction;
        public static ConfigEntry<float> EvictionMinutes;
        public static ConfigEntry<bool> EarlyExit;
        public static ConfigEntry<bool> BodiesBeforeLeaving;
        public static ConfigEntry<bool> ShortQbHolds;
        public static ConfigEntry<bool> SpawnAllPoints;
        public static ConfigEntry<bool> PmcAlwaysHostile;
        public static ConfigEntry<float> OnlineSpawnMinDistance;
        public static ConfigEntry<bool> LobbyLikeOnline;
        public static ConfigEntry<bool> LivelyMap;
        public static ConfigEntry<bool> PScavReplace;
        public static ConfigEntry<int> PeopleShare;
        public static ConfigEntry<bool> PopulationBalance;
        public static ConfigEntry<int> PopulationDensity;
        public static ConfigEntry<int> PopulationStrength;
        public static ConfigEntry<int> CrowdCap;
        public static ConfigEntry<int> PmcReserve;
        public static ConfigEntry<bool> GroupFillsLobby;

        public static ConfigEntry<LangChoice> Language;
        public static ConfigEntry<PresetChoice> Preset;
        public static ConfigEntry<bool> QuestAudit;
        public static ConfigEntry<bool> QuestPointFix;
        public static ConfigEntry<bool> QuestSimulation;
        public static ConfigEntry<bool> QuestAnimations;
        public static ConfigEntry<bool> Routes;
        public static ConfigEntry<bool> RouteTeleportUnseen;
        public static ConfigEntry<bool> RouteRecorder;
        public static ConfigEntry<int> RouteAreaBots;
        public static ConfigEntry<KeyboardShortcut> RouteRecordPoint;
        public static ConfigEntry<KeyboardShortcut> RouteRecordJump;
        public static ConfigEntry<KeyboardShortcut> RouteUndo;
        public static ConfigEntry<KeyboardShortcut> RouteSave;
        public static ConfigEntry<bool> MapEdits;
        public static ConfigEntry<bool> MapEditsAutoReload;
        public static ConfigEntry<bool> MapEditsDump;
        public static ConfigEntry<bool> MapEditsRedump;
        public static ConfigEntry<bool> MapEditsOverlay;
        public static ConfigEntry<KeyboardShortcut> MapEditsReloadKey;
        public static ConfigEntry<KeyboardShortcut> MapEditsOverlayKey;

        public static ConfigEntry<bool> SpectateAfterDeath;
        public static ConfigEntry<bool> ObserverFromStart;
        public static ConfigEntry<bool> ObserverFollowAtStart;
        public static ConfigEntry<bool> ObserverFollowPackrat;
        public static ConfigEntry<bool> TakeOverBots;
        public static ConfigEntry<bool> EndRaidWhenTakenBotDies;
        public static ConfigEntry<bool> SpectatorBotNames;
        public static ConfigEntry<float> FreeCameraSpeed;
        public static ConfigEntry<KeyboardShortcut> SpectatorEndRaid;
        public static ConfigEntry<KeyboardShortcut> SpectatorTakeOver;
        public static ConfigEntry<KeyboardShortcut> SpectatorLeaveBot;
        public static ConfigEntry<KeyboardShortcut> SpectatorFollow;
        public static ConfigEntry<KeyboardShortcut> SpectatorNextBot;
        public static ConfigEntry<KeyboardShortcut> SpectatorToggleView;

        // Groups of the compact menu (their order here is their order in F12)
        private const string CMain = "1. Основное";
        private const string CKeys = "2. Ключи и двери";
        private const string CMap = "3. Жизнь на карте";
        private const string CChars = "4. Характеры и эвакуация";
        private const string CSpawn = "5. Спавн и лобби";
        private const string CLoot = "6. Лут";
        private const string CSquad = "7. Отряды";
        private const string CFight = "8. Бой";
        private const string CHeal = "9. Лечение, оружие и патроны";
        private const string CTasks = "10. Квесты и маршруты";
        private const string CPerf = "11. Производительность";
        private const string CDeath = "12. После смерти";

        private const string Menu = "0. Compact menu";
        private static string _cat;

        /// <summary>
        /// Binds one setting under its old section/key and registers how F12 shows it. simple: shown in the compact menu (otherwise only in developer mode);
        /// synced: in LiveRaid Online the host's value is used by everyone (otherwise each machine keeps its own).
        /// </summary>
        private static ConfigEntry<T> B<T>(ConfigFile cfg, string section, string key, T def, string name, string desc,
            AcceptableValueBase range = null, bool simple = false, bool synced = true, string[] labels = null)
        {
            string enName = key;
            string enDesc = desc;
            if (LocEn.Entries.TryGetValue(section + "|" + key, out (string name, string desc) en))
            {
                enName = en.name;
                enDesc = en.desc;
            }

            ConfigurationManagerAttributes attr = new ConfigurationManagerAttributes { DispName = Loc.Ru ? name : enName, Category = Loc.Tr(_cat) };
            ConfigEntry<T> entry = cfg.Bind(section, key, def, new ConfigDescription(Loc.Ru ? desc : enDesc, range, attr));
            SettingsMenu.Register(entry, attr, _cat, simple, synced, labels);
            Loc.Track(entry, section, key, name, desc);
            return entry;
        }

        private static ConfigEntry<SettingsMenu.PackState> Pack(ConfigFile cfg, string key, string name, string desc) =>
            B(cfg, Menu, key, SettingsMenu.PackState.On, name, desc, simple: true, synced: false);

        private static ConfigEntry<SettingsMenu.Level> Level(ConfigFile cfg, string key, string name, string desc) =>
            B(cfg, Menu, key, SettingsMenu.Level.Normal, name, desc, simple: true, synced: false);

        private static ConfigEntry<int> Amount(ConfigFile cfg, string key, int def, int max, string name, string desc) =>
            B(cfg, Menu, key, def, name, desc, new AcceptableValueRange<int>(0, max), simple: true, synced: false);

        /// <summary>
        /// "Scavs roam the map" and "Scavs patrol their area" are two answers to one question: turning one on turns the other off. Both on (an old config,
        /// or a pack switched on) - the patrol, as online, stays.
        /// </summary>
        private static void ExclusiveScavModes()
        {
            if (ScavsQuest.Value && ScavPatrol.Value)
            {
                ScavsQuest.Value = false;
            }

            ScavsQuest.SettingChanged += (s, e) =>
            {
                if (ScavsQuest.Value && ScavPatrol.Value)
                {
                    ScavPatrol.Value = false;
                }
            };
            ScavPatrol.SettingChanged += (s, e) =>
            {
                if (ScavPatrol.Value && ScavsQuest.Value)
                {
                    ScavsQuest.Value = false;
                }
            };
        }

        public static void Bind(ConfigFile cfg)
        {
            // The compact menu first: the order in which groups first appear is the order F12 shows them in
            _cat = CMain;
            Language = B(cfg, Menu, "Language", LangChoice.Auto, "Язык / Language",
                "Язык текстов F12 и подсказок свободной камеры. Авто - язык интерфейса самой игры (русский - по-русски, любой другой - по-английски). Откройте F12 заново, чтобы увидеть перевод",
                simple: true, synced: false, labels: new[] { "Auto", "English", "Русский" });
            Language.SettingChanged += (s, e) => Loc.Changed();
            Preset = B(cfg, Menu, "Preset", PresetChoice.Recommended, "Готовые наборы настроек",
                "Готовые наборы, чтобы не разбирать две сотни настроек. Рекомендуемый - как задумал автор: все пакеты включены, население и производительность по умолчанию. " +
                "Слабый ПК - дальних ботов упрощают и усыпляют, ИИ против ИИ только вблизи, меньше ботов, без подробных журналов. Тихие рейды - меньше ботов и ЧВК, ниже потолок толпы. " +
                "Людные рейды - больше ботов и ЧВК, больше запасных, чаще бои. Только исправления - всё поведение LiveRaid выключено, остаются исправления ошибок EFT и лечение. " +
                "Выборочно - ваши значения (показываются все настройки). В LiveRaid Online выбирает только хост",
                simple: true, synced: false,
                labels: new[] { "Выборочно", "Рекомендуемый", "Слабый ПК", "Тихие рейды", "Людные рейды", "Только исправления" });
            MenuStatus = B(cfg, Menu, "Menu status", "", "Состояние", "Кто управляет настройками в LiveRaid Online", simple: true, synced: false);
            Enabled = B(cfg, "1. Main", "Enabled", true, Plugin.Channel == "" ? "Включить LiveRaid" : "Включить LiveRaid (ТЕСТОВАЯ ВЕТКА " + Plugin.Version + ")",
                "Главный выключатель мода (нужен перезапуск игры)" + (Plugin.Channel == "" ? "" : ". Это тестовая ветка - вернуть стабильную можно в LiveRaid Launcher"), simple: true);
            DevMode = B(cfg, "1. Main", "Developer mode", false, "Режим разработчика",
                "Показать каждую настройку по отдельности (как раньше), а не только пакеты и уровни. Пакет со своими значениями показывает «Выборочно»",
                simple: true, synced: false);

            _cat = CKeys;
            var packKeys = Pack(cfg, "Keys and doors pack", "Ключи и двери",
                "Настоящие ключи у ботов, помощь у дверей и в застреваниях, запертые контейнеры");
            var keyAmount = Level(cfg, "Keys carried", "Ключей у ботов",
                "Сколько ключей от дверей этой карты носят боты. Мало: ЧВК 30 %, дикие 10 %, до 2 ключей. Как онлайн: 60 % / 25 %, до 3. Много: 85 % / 45 %, до 4");
            var prizeKeys = Level(cfg, "Prize keys", "Призовые ключи у ЧВК",
                "Ключ от самого богатого места карты (меченая комната, КИБА, карта Лабы...) в подсумке ЧВК - его не видно и не снять. Редко: 40 % на рейд. " +
                "Обычно: 75 %. Часто: всегда. Каждый следующий носитель в рейде реже");

            _cat = CMap;
            var packMap = Pack(cfg, "Map life pack", "Передвижение по карте",
                "Боты ходят по всей карте как игроки: свои квесты-исследования, горячие зоны, ключевые места, хот-дроп, без простоя и долгих стоянок");
            var eviction = Amount(cfg, "Move out after (min)", 6, 20, "Уходить с места через, мин",
                "Лидер или одиночка, проторчавший в одном районе (~120 м) столько минут, уходит в другой конец карты. 0 = выкл. (минимум 3)");
            var packLearn = Pack(cfg, "Learning pack", "Учиться на моих маршрутах",
                "Мод запоминает, где вы ходите, и отправляет ботов в эти места (с следующего рейда). Срезки по найденным залезам - только в режиме разработчика");

            _cat = CChars;
            var packChars = Pack(cfg, "Characters pack", "Характеры ботов",
                "У каждого бота свой стиль (кемпер, крыса, рашер, лутер, квестер...) и уровень навыка от новичка до профи");
            var packExfil = Pack(cfg, "Extract pack", "Эвакуация как онлайн",
                "У каждого отряда своё время ухода, почти никто не уходит в первые 20 минут, выходы своей стороны карты, рубильники, уход всем отрядом");

            _cat = CSpawn;
            var packSpawn = Pack(cfg, "Spawn pack", "Спавн как онлайн",
                "Лобби ЧВК размером с онлайн, случайные свободные точки (отряд может появиться рядом), разнос по сторонам карты");
            var extraPmcs = Amount(cfg, "Extra PMCs", 5, 10, "ЧВК сверх онлайн-лобби",
                "Сколько ЧВК добавить к онлайн-лобби карты на старте; в смеси больше одиночек. 0 = ровно онлайн-лобби (со следующего рейда)");

            _cat = CLoot;
            var packLoot = Pack(cfg, "Loot pack", "Лут как у игрока",
                "Боты взвешивают ценность и риск, не лутают под огнём, обыскивают тела после боя и всю комнату, выбрасывают дешёвое ради находки");

            _cat = CSquad;
            var packSquad = Pack(cfg, "Squads pack", "Отряды",
                "Роли в отряде, держатся вместе, ждут товарищей, спешат на помощь, строй, голосовые команды, фланги, обмен аптечками и лутом");

            _cat = CFight;
            var packFight = Pack(cfg, "Fight pack", "Тактика боя",
                "Мораль и роли в бою, реакция на стрельбу издалека, ответный огонь, бой на дистанции своего оружия, поиск обычным шагом");
            var flee = Level(cfg, "Leave a lost fight", "Бегство из проигранного боя",
                "Бот против явного перевеса может сдать нервы и уйти из боя. Редко / Обычно / Часто - насколько охотно");
            var coldFights = Level(cfg, "Forget a cold fight", "Забывать остывший бой",
                "Врага, которого давно не видно и не слышно, бот бросает и возвращается к делам. Быстро / Обычно / Долго - сколько он его ищет");

            _cat = CHeal;
            var packHeal = Pack(cfg, "Healing pack", "Лечение",
                "Перевязка в первую тихую минуту, прерывание лечения под огнём, шины и обезболивающие на ноги, хирургия из рюкзака");
            var packFixes = Pack(cfg, "EFT fixes pack", "Исправления ИИ EFT",
                "Чинит ошибки EFT и модов: зависшие руки и перезарядка, выброшенные магазины, мигающая смена оружия, свои стреляют в своих");
            var bottomless = Amount(cfg, "Summoned ammo (rounds per stack)", 40, 100, "Патроны из воздуха, патронов в пачке",
                "Пачка россыпи по требованию, у ЧВК в защищённом контейнере, у диких в разгрузке или кармане; для найденного оружия - один запасной магазин. 0 = выкл. (как в SPT)");

            _cat = CTasks;
            var packQuests = Pack(cfg, "Quests pack", "Квесты EFT",
                "ЧВК идут к шагам своих квестов, отыгрывают их (поднять, заложить, осмотреть), отряды расходятся по разным квестам, точки квестов проверяются");
            var packRoutes = Pack(cfg, "Routes pack", "Маршруты через завалы",
                "Боты ходят по записанным маршрутам (routes/<карта>.json) туда, куда навмеш не ведёт: завалы, перила, «Утёс». Здесь же правки карты из LiveRaid Studio (свой навмеш, запреты, маршруты ботов)");

            _cat = CMain;
            LogLevel = B(cfg, "1. Main", "Log detail", LogDetail.Normal, "Подробность журнала",
                "Обычный: строки «LR ...» в журнале BepInEx. Подробный: ещё файл трассы рейда в BepInEx/plugins/LiveRaid/logs (каждое решение, раз в 15 с что делает каждый бот, " +
                "застрявшие, здоровье систем раз в минуту). Полный: как подробный, плюс всё в журнал BepInEx и раз в 5 с (тяжело, для поиска ошибки). Со следующего рейда",
                simple: true, labels: new[] { "Обычный", "Подробный", "Полный" }); // shown through Loc.Tr
            LogNetwork = B(cfg, "1. Main", "Log network and frame time", true, "Журнал сети и кадров",
                "Раз в 10 с: время кадра этой машины (fps, худший кадр, кадры дольше 100/250 мс), а на хосте Fika - задержка каждого игрока, очередь надёжных пакетов, потери. " +
                "Строка в журнал, когда что-то не так (задержка или кадр > 250 мс, очередь), иначе в трассу. Отличает лагающий хост от плохой линии");
            SummaryOnQuit = B(cfg, "1. Main", "Raid summary when the game is closed", true, "Итог рейда при закрытии игры",
                "Если закрыть игру посреди рейда, итог рейда (LR Health on, журнал встреч) всё равно пишется; каждые 5 минут промежуточный итог - в трассу");
            CoverageLog = B(cfg, "1. Main", "Coverage log", true, "Журнал покрытия карты",
                "Раз в 10 с позиции ботов в BepInEx/plugins/LiveRaid/coverage/*.csv - проверить, что боты обходят всю карту (tools/coverage.py)");
            EncounterLog = B(cfg, "1. Main", "Encounter log", true, "Журнал встреч",
                "Замеры рейда: число ботов, враждебные группы на виду, первая встреча, реакции на стрельбу. Файлы в BepInEx/plugins/LiveRaid/encounters (tools/encounters.py)");
            Harmonize = B(cfg, "1. Main", "Harmonize behaviour", true, "Согласовывать моды",
                "Моды не тянут бота в две стороны: поиск лута идёт в фоне (бот не замирает каждые несколько секунд), QuestingBots уступает, когда есть лут, " +
                "и пропускает перерыв на лут, если в 40 м нет ничего ценного; бот, мечущийся между двумя поведениями, на время успокаивается");
            SmoothDecisions = B(cfg, "1. Main", "Smooth decisions", true, "Плавные решения",
                "Единая картина обстановки бота: опасность замечается сразу, спокойствие возвращается постепенно (8 с после взгляда или попадания, 10-25 с после боя). " +
                "Поручения, обходы и реакции на далёкий бой начинаются только в настоящем спокойствии. Выкл.: старые мгновенные проверки (для сравнения, см. «LR Brain on»)");

            _cat = CKeys;
            RealKeys = B(cfg, "2. Keys and locked doors", "Real keys only", true, "Только настоящие ключи",
                "Боты открывают запертые двери только ключом, который реально лежит у них в инвентаре. Ключи выдаются при появлении, их можно снять с тела");
            PmcKeyChance = B(cfg, "2. Keys and locked doors", "PMC key chance", 0.6f, "Шанс ключа у ЧВК",
                "Шанс, что ЧВК появится хотя бы с одним ключом от двери этой карты", new AcceptableValueRange<float>(0, 1));
            ScavKeyChance = B(cfg, "2. Keys and locked doors", "Scav key chance", 0.25f, "Шанс ключа у дикого",
                "Шанс, что дикий появится с ключом от двери этой карты", new AcceptableValueRange<float>(0, 1));
            MaxKeysPerBot = B(cfg, "2. Keys and locked doors", "Max keys per bot", 3, "Ключей на бота, не больше",
                "Сколько ключей от этой карты может нести один бот", new AcceptableValueRange<int>(0, 6));
            DoorManners = B(cfg, "2. Keys and locked doors", "Door manners", true, "Двери как у игроков",
                "Вошёл в здание - иногда закрывает дверь за собой (если товарищей у двери не осталось), подходит к закрытой двери - замирает на 1-2 с послушать. " +
                "Крысы и чёткие отряды чаще, рашеры почти никогда");
            RoomPeek = B(cfg, "2. Keys and locked doors", "Look into a room you unlocked", true, "Заходить в отпертую комнату",
                "Бот отпер дверь ключом - всегда открывает её, заходит на 3-6 м, осматривается и обыскивает комнату ЦЕЛИКОМ: каждый контейнер, тело и ценную вещь, " +
                "без шанса уйти раньше (пока не кончится что брать, не заполнится рюкзак и не прогонит бой). Раньше после отпирания он сразу уходил дальше по квесту");
            FrozenWatch = B(cfg, "2. Keys and locked doors", "Free frozen bots", true, "Размораживать застывших ботов",
                "1) Бот не может ходить, потому что оборванное лечение оставило ему флаги «лечится» при пустых руках - флаги и эффект лечения снимаются через 2 с. " +
                "2) Бот хочет двигаться (укрытие, отход, рывок), но 10 с не сошёл с места при враге рядом - в журнал пишется его состояние, и он уходит из-под взгляда врага на 8-30 м",
                synced: false);
            LayerCalm = B(cfg, "2. Keys and locked doors", "Calm the flicker of the QuestingBots idle layer", true, "Без мигания простоя QuestingBots",
                "Слой простоя QuestingBots (BotRecoveryLayer) включался и выключался по нескольку раз в секунду (205 мельканий за рейд у патрулей диких и ботов между шагами квеста) - " +
                "ходьба бота начиналась и обрывалась. Теперь его ответ меняется не чаще раза в секунду. Бой, лут и остальные слои не затронуты", synced: false);
            DoorAssist = B(cfg, "2. Keys and locked doors", "Unstick bots (doors, door leaves, corners)", true, "Помощь у дверей и углов",
                "Бот, 2 с стоящий у закрытой двери на пути, открывает её; застрял снова - отходит и проходит ещё раз; после двух неудач оставляет дверь на минуту " +
                "(боты EFT открывают только двери со ссылкой в навмеше)");
            FreeShutIn = B(cfg, "2. Keys and locked doors", "Free bots shut in a room", true, "Выпускать запертых в комнате",
                "Бот, у которого разом не выходит ни один путь (три шага квеста или лут, недостижимые за 2,5 мин), открывает ближайшую закрытую дверь, а без неё - " +
                "пока никто не видит - переносится на последнюю точку своего пути снаружи");
            BackOffWhenStuck = B(cfg, "2. Keys and locked doors", "Back off and re-route when stuck again", true, "Отход и новый путь при застревании",
                "Если помощь у двери не освободила бота (снова застрял за 90 с там же), он отходит по своему следу и строит путь в обход; дальше - отход подальше, затем бросает поручение");
            FoundKeys = B(cfg, "2. Keys and locked doors", "Use keys found in the raid", true, "Найденный ключ - в дело",
                "Ключ, снятый с тела или найденный в ящике, от запертой двери или контейнера этой карты - бот 10 минут хочет туда попасть (комната x3, контейнер - в обход)");
            ContainerKeys = B(cfg, "2. Keys and locked doors", "Open locked containers with keys", true, "Отпирать контейнеры ключом",
                "Бот с ключом от запертого сейфа, кейса или багажника может пойти к нему (по жадности, один раз на контейнер), отпереть и облутать");
            PrizeKeys = B(cfg, "2. Keys and locked doors", "Prize keys in a PMC secure container", true, "Призовые ключи",
                "Как онлайн, ЧВК может зайти с ключом от самого богатого места карты (меченая комната на Таможне, КИБА на Развязке, меченые на Резерве, карта Лабы...). " +
                "Ключ в подсумке: его не видно и не снять. Скорее всего, бот побежит туда первым - сразу, чуть позже или под конец рейда");
            PrizeKeyChance = B(cfg, "2. Keys and locked doors", "Prize key chance", 0.75f, "Шанс призового ключа",
                "Шанс, что один ЧВК рейда несёт призовой ключ", new AcceptableValueRange<float>(0f, 1f));
            PrizeKeyStep = B(cfg, "2. Keys and locked doors", "Prize key chance drop per carrier", 0.2f, "Снижение шанса на носителя",
                "Каждый следующий носитель в рейде на столько менее вероятен (0,75: первый 75 %, второй 55 %, третий 35 %, четвёртый 15 %)",
                new AcceptableValueRange<float>(0.05f, 1f));

            _cat = CMap;
            BtrPatience = B(cfg, "3. Map-wide movement", "Wait out the BTR only briefly", true, "Пережидать БТР недолго",
                "EFT сажает каждого бота у БТР в укрытие, пока машина не уйдёт, - на остановках это минуты. Здесь бот уступает ей дорогу 20-35 с (крысы и кемперы чуть дольше), " +
                "потом 3 минуты занят своим делом; ближе 12 м к машине всё равно отходит");
            NeverIdle = B(cfg, "3. Map-wide movement", "Never stand idle", true, "Без простоя",
                "Бот, 40 с не сдвинувшийся в слое, который сам ничего не делает (восстановление QuestingBots, патруль, LootingBots без лута, ведомый без лидера), " +
                "и без причины стоять (лечение, лут, задача, таймер выхода), получает команду идти; через 75 с - отправляется в другое место карты (LR Idle)");
            MapWideQuests = B(cfg, "3. Map-wide movement", "Map-wide quests", true, "Квесты по всей карте",
                "Цели исследования и лута по всей карте из навмеша и точек лута (кэш на карту)");
            DatabaseLootSpots = B(cfg, "3. Map-wide movement", "All loot spawn spots", true, "Все точки лута из базы",
                "Маршруты ботов используют все точки рассыпного лута карты из базы SPT (все этажи и комнаты), а не только лут первого рейда. " +
                "Действует при постройке кэша карты: после смены удалить BepInEx/plugins/LiveRaid/cache/<карта>.json");
            RoamRegionSize = B(cfg, "3. Map-wide movement", "Region size (m)", 140f, "Размер района, м",
                "Размер одного района исследования; меньше - больше и короче походов", new AcceptableValueRange<float>(60, 400));
            RoamDesirability = B(cfg, "3. Map-wide movement", "Exploration desirability", 24f, "Желанность исследования",
                "Базовая желанность целей исследования (квесты EFT - 50)", new AcceptableValueRange<float>(1, 100));
            ScavsQuest = B(cfg, "3. Map-wide movement", "Scavs roam the map", false, "Дикие бродят по карте",
                "ИИ-дикие берут цели исследования по всей карте, как ЧВК. Включение выключает «Дикие патрулируют свой район»; оба выключены - дикие как в чистом SPT");
            ScavPatrol = B(cfg, "3. Map-wide movement", "Scavs patrol their area", true, "Дикие патрулируют свой район (как онлайн)",
                "ИИ-дикие, как онлайн, держатся района, где появились (70-120 м, растёт, если там нечего делать): обходят его с короткими остановками, " +
                "идут на шум и стрельбу рядом и возвращаются, зовут соседей своего района в бой. Игроки-дикие не затрагиваются. " +
                "Включение выключает «Дикие бродят по карте»; оба выключены - дикие как в чистом SPT");
            PlayerScavLife = B(cfg, "3. Map-wide movement", "Player scavs play like online", true, "Игроки-дикие как онлайн",
                "Игрок-дикий заходит позже (как QuestingBots), рейд у него короткий - 7-15 мин (лутер 10-18), потом на выход; после боя бежит к телам ЧВК, когда там стихло; " +
                "в бои ЧВК не лезет (ждёт победителя или уходит), кроме каждого восьмого «чад-дикого» - тот охотится на ЧВК; убил ИИ-дикого - дикие рядом ему враги");
            CoverRoutes = B(cfg, "3. Map-wide movement", "Keep to cover across open ground", true, "Обходить открытые места",
                "Если впереди 40+ м открытого места (поле, середина дороги), осторожный боец бежит краем - к укрытию в 15-35 м сбоку, если путь длиннее не больше чем на треть; " +
                "после стрельбы рядом - почти каждый, рашер в тихом рейде - напрямик");
            Swivel = B(cfg, "3. Map-wide movement", "Head on a swivel", true, "Голова на шарнире",
                "Насторожённый боец (слышал врага, недавний бой) на ходу то и дело смотрит в двери, проёмы и в сторону врага или шума, а не только туда, куда идёт");
            Overlook = B(cfg, "3. Map-wide movement", "Look over open ground first", true, "Осмотр перед открытым местом",
                "Перед переходом поля или широкой открытой улицы осторожный боец 2-4 с осматривает её из укрытия (не каждый раз и не каждый)");
            MapBalance = B(cfg, "3. Map-wide movement", "Spread the bots over the map", true, "Боты распределены по карте",
                "Мягкая подстройка: квест (район, ключевое место, комната) в части карты, где ботов больше, чем положено, весит меньше, в пустой части - больше; " +
                "бот, которого отправляют «в другое место», реже выбирает людное. Никого не двигает силой, босса и аирдроп не трогает. " +
                "Положенная доля района растёт с его лутом и каждый рейд сдвинута случайно (см. «Неровность»): ровно поровну не бывает", simple: true);
            MapBalanceStrength = B(cfg, "3. Map-wide movement", "Spread strength, %", 50, "Сила выравнивания, %",
                "Как сильно людный район отпугивает, а пустой манит. 0 - выкл., 50 - район втрое людней положенного весит вдвое меньше, 100 - сильно (до 0,4 и 2,2 раза). " +
                "Каждый отряд следует этому по характеру: исследователи охотнее, кемперы, снайперы и квестеры слабее", new AcceptableValueRange<int>(0, 100));
            MapBalanceUneven = B(cfg, "3. Map-wide movement", "Spread unevenness, %", 35, "Неровность распределения, %",
                "Насколько положенная доля районов в этом рейде отличается от «по лутовой ценности»: 0 - строго по ценности, 35 - заметный крен в одну-две части карты (меняется каждые 6 минут), " +
                "100 - сильный", new AcceptableValueRange<int>(0, 100));
            Overwatch = B(cfg, "3. Map-wide movement", "Long gun takes the high ground", true, "Дальнобой на высоте",
                "Боец отряда с длинным стволом время от времени коротко (15-30 с) занимает высоту над маршрутом отряда и прикрывает его, потом догоняет");
            AirdropEvent = B(cfg, "3. Map-wide movement", "Airdrop fights", true, "Аирдроп как событие",
                "К упавшему аирдропу идут не все, а до трёх отрядов: те, кто рядом (до ~220 м), успевает по времени рейда, в силах (здоровье) и хочет по характеру и доктрине - " +
                "там и случается схватка");
            PeopleCorridors = B(cfg, "3. Map-wide movement", "Walk where people walk", true, "Ходить коридорами людей",
                "ЧВК и игроки-дикие на дальнем пути заходят на «коридоры людей» - там, где ходите вы (ваши записанные маршруты) и где ходят онлайн: от спавна к ключевым местам и горячим районам, оттуда к выходам. " +
                "Не больше трети лишнего пути; уже идущий по коридору путь не меняется. Так пути пересекаются и встречи случаются по дороге, а не только у случайного лута");
            CorridorStrength = B(cfg, "3. Map-wide movement", "Corridor pull, %", 50, "Сила коридоров, %",
                "Насколько охотно боты заходят на коридоры (50 = исследователи ~65 %, крысы ~20 % дальних путей) и насколько районы с коридорами желаннее", new AcceptableValueRange<int>(0, 100));
            TempoLikePlayers = B(cfg, "3. Map-wide movement", "Tempo like players", true, "Темп как у игроков",
                "Быстрые отряды (по доктрине: штурмовики, PvP-охотники) меньше задерживаются на одном месте и в точках квестов; бот в районе, который уже обыскан (им самим недавно или многими другими), " +
                "через 2-3 мин без лута и боя уходит в другую часть карты; нежадный до лута бот короче осматривается в поисках лута. Лутеры и квестеры - как раньше");
            HotZones = B(cfg, "3. Map-wide movement", "Hot zones", true, "Горячие зоны",
                "Богатые лутом районы (промзона, общаги, торговые центры...) притягивают лутеров и агрессивные отряды, особенно в первые минуты; " +
                "районы, где только что прошёл другой отряд или бот сам всё вычистил, на время теряют интерес");
            MoveOn = B(cfg, "3. Map-wide movement", "Move on across the map", true, "Двигаться дальше по карте",
                "Отряд или одиночка, побывший в одном месте (исследователь ~3 мин ... кемпер ~8), идёт дальше: цели рядом на время теряют привлекательность, дальние набирают");
            Eviction = B(cfg, "3. Map-wide movement", "Move bots out of a place they stay in too long", true, "Выгонять засидевшихся",
                "Лидер или одиночка, проторчавший в одном районе (~120 м) заданное время, бросает текущую цель, всё рядом становится неинтересным на 8 минут, " +
                "а если всё равно стоит - получает приказ идти в дальнее место карты. Выкл.: боты решают сами");
            EvictionMinutes = B(cfg, "3. Map-wide movement", "Stay limit in one area (min)", 6f, "Предел в одном районе, мин",
                "Сколько бот может пробыть в одном районе (исследователи и рашеры чуть меньше, крысы и снайперы чуть больше)", new AcceptableValueRange<float>(3f, 20f));
            ShortQbHolds = B(cfg, "3. Map-wide movement", "Short holds on QuestingBots quests", true, "Короткие засады квестов QB",
                "В стандартных квестах QuestingBots (общаги на Таможне и др.) шаги засады и удержания по 30-120 с - боты стояли как кемперы. Здесь 5-15 с (засада 8-15 с). " +
                "Боссы и их охрана - как в оригинале");
            HotDrop = B(cfg, "3. Map-wide movement", "Hot-drop rushers", true, "Хот-дроп",
                "Часть ЧВК (в основном лутеры и рашеры, 1 из 4-5) с появления бежит в самое богатое место карты - общаги на Таможне, бункер на Резерве - как игроки онлайн. Первые 6 минут");
            KeyPlaces = B(cfg, "3. Map-wide movement", "Key places draw a third of the PMCs", true, "Ключевые места",
                "Несколько мест, куда бегают игроки онлайн (общаги, бункеры...), получают свои квесты с целями на всех этажах. Около трети отрядов ЧВК выбирают одно из них, " +
                "равномерно по местам; остальные заходят туда чуть реже, чем в другие места");
            ThoroughLooting = B(cfg, "3. Map-wide movement", "Thorough looting", true, "Прочёсывать после цели",
                "Закончив цель, бот может прочесать округу в поисках лута (по характеру и богатству места), а не бежать сразу к следующей точке");
            LearnFromMyRoute = B(cfg, "3. Map-wide movement", "Learn places from my route", true, "Записывать мой маршрут",
                "В фоне каждый рейд записывается, где вы ходите. Места, которых нет на карте ботов и куда они могут дойти, - в learned/<карта>.json; " +
                "залезы, соединяющие места, которые навмеш не соединяет, - кандидаты маршрутов (learned/<карта>.shortcuts.json). Удалить learned/<карта>.json - забыть карту");
            BotsVisitLearned = B(cfg, "3. Map-wide movement", "Bots visit places from my routes", true, "Боты ходят по моим местам",
                "Квесты исследования отправляют ботов и в места с ваших маршрутов (со следующего рейда). Выкл.: места пишутся, но не используются");

            _cat = CChars;
            RaidMoods = B(cfg, "4. Bot characters", "Moods over the raid", true, "Настроение за рейд",
                "У каждого бота по шансу своя натура: мстительный ищет того, кто в него стрелял; чудом выживший становится осторожным и раньше уходит; " +
                "после серии убийств - кураж (смелее, но небрежнее); с дорогим лутом - бережёт его, избегает драк и торопится к выходу. Не у всех и не всё");
            SelfPreservation = B(cfg, "4. Bot characters", "Flee when the squad is wiped", true, "Бегство, когда отряд погиб",
                "Последний живой из отряда (2+) с высоким шансом бросает бой и уходит к выходу - инстинкт самосохранения есть у всех");
            Personalities = B(cfg, "4. Bot characters", "Characters", true, "Характеры",
                "У каждого бота стиль игры (кемпер, крыса, рашер, лутер, квестер, исследователь, охотник, снайпер), от него личность SAIN и выбор целей");
            SkillSpread = B(cfg, "4. Bot characters", "Skill spread", true, "Разброс навыка",
                "Боты от новичка до профи: скорость прицеливания, разброс, зрение и слух у каждого свои");
            MemoryMultiplier = B(cfg, "4. Bot characters", "Memory multiplier", 2f, "Память о враге, x",
                "Во сколько раз дольше боты помнят врага, прежде чем бросить (дальше - по характеру)", new AcceptableValueRange<float>(0.5f, 5f));
            MaxSearchWait = B(cfg, "4. Bot characters", "Max wait before searching, s", 40f, "Ожидание перед поиском, с",
                "SAIN отпускает бота проверить врага, только когда того не слышно время личности (крыса 240 с, «черепаха» 90, «чад» 16), и каждый выстрел рядом сбрасывает отсчёт. " +
                "Это потолок (±20 %), чтобы даже крысы снова двигались через пару тихих минут. 0 = как в SAIN", new AcceptableValueRange<float>(0f, 600f));
            PersonalExtract = B(cfg, "4. Bot characters", "Personal extract time", true, "Своё время ухода",
                "Каждый отряд уходит в свой момент: кто-то рано, большинство в середине, кемперы поздно. Полный рюкзак по-прежнему гонит домой раньше (SAIN)");
            ExtractCurve = B(cfg, "4. Bot characters", "Online-like extract times", true, "Время ухода как онлайн",
                "Как онлайн, почти никто не уходит в первые 20 минут (или первую половину короткого рейда): ранний момент срабатывает лишь по жребию - 3 % в начале, 20 % к 20-й минуте " +
                "(60 % тяжело раненым без лечения или потерявшим пол-отряда), иначе сдвигается позже, чаще к ~70 % рейда");
            SwitchExits = B(cfg, "4. Bot characters", "Leave through switch exits", true, "Выходы через рубильник",
                "Часть отрядов ЧВК уходит через выход, который открывает рубильник (Развязка, бункер и D-2 на Резерве, ворота Лабы...): бегут к рубильнику, дёргают его " +
                "(или находят уже включённым), идут к выходу, стоят таймер и уходят");
            EarlyExit = B(cfg, "3. Map-wide movement", "Leave the raid early when it is time", true, "Уходить раньше, когда пора",
                "ЧВК взвешивает состояние, лут и обстановку: ранен без аптечек, рюкзак ценностей, отряд потерял половину, долгий рейд с лутом - и может уйти раньше своего времени. " +
                "Выкл.: только личное время");
            BodiesBeforeLeaving = B(cfg, "3. Map-wide movement", "Strip the bodies before leaving", true, "Обыскать тела перед уходом",
                "ЧВК, которому пора уходить, а рядом лежит необысканное тело павшего товарища или его жертвы, сперва обыскивает их (до 4 минут, только в тишине). " +
                "Не когда уходить надо немедленно (нет патронов, тяжело ранен без лечения)");
            ExtractTogether = B(cfg, "8. Squads", "Leave the raid together", true, "Уходить всем отрядом",
                "Лидер решает, когда уходить, и все идут с ним к ЕГО выходу; ведомые не уходят сами. Таймер выхода начинается, когда отряд собрался (в 15 м, не дольше 90 с ожидания)");
            SpecialExits = B(cfg, "8. Squads", "Paid and conditional exits", true, "Платные и особые выходы",
                "Боты уходят и через выходы с условием, если выполняют его как игрок: машина - если есть нужные рубли (сервер даёт половине ЧВК 6-40k в подсумок), " +
                "выход без рюкзака, спуск по скале и т.п. - по собственной проверке игры. Выходы с рубильником - как раньше, совместные, поезд и события ботам не доступны");
            ExitArrival = B(cfg, "8. Squads", "Leave when standing at the exit", true, "Выход засчитан у точки",
                "Игра запускает таймер выхода у бота только в 1 м от точки выхода; у некоторых выходов бот упирается в 1-4 м (забор, бордюр) и стоит. " +
                "Простоял 3 с у самой точки - таймер идёт и бот выходит (отряд по-прежнему ждёт друг друга)");
            ExitWait = B(cfg, "8. Squads", "Wait at the exit like a player", true, "Ждать у выхода как игрок",
                "Дойдя до точки выхода, бот приседает и смотрит туда, откуда пришёл, пока идёт таймер. Игра заставляла его смотреть в точку внутри своей головы - бот дёргался на месте");
            CarLeaves = B(cfg, "8. Squads", "Paid car drives off", true, "Платная машина уезжает",
                "Бот, уходящий на платной машине (А-выход у общаги и т.п.), запускает её отсчёт, как игрок, заплативший водителю: через время выхода машина уезжает и выход закрыт для всех. " +
                "В Online отсчёт и отъезд ведёт сам Fika у всех игроков; в одиночной машина не уедет, если вы в ней");
            ExitsBySide = B(cfg, "8. Squads", "Exits of the spawn side", true, "Выходы своей стороны",
                "ЧВК уходит через выход той стороны, где появился, как игрок (на Таможне: ЗБ-1011, ЗБ-1012, Старая заправка с одной стороны; Перекрёсток, Трейлерный парк, " +
                "Блокпост, Лодка контрабандиста - с другой); ближние вероятнее, дальний - изредка. EFT отправлял всех к ближайшему выходу карты");

            _cat = CSpawn;
            OnlineSpawns = B(cfg, "6. Spawns", "Online-like spawns", true, "Спавн как онлайн",
                "Вкл.: группы ЧВК и диких-игроков появляются на случайной свободной точке, так что отряд может появиться рядом с вами, как онлайн. " +
                "Выкл.: безопасный режим QuestingBots - группы как можно дальше от всех");
            SpawnAllPoints = B(cfg, "6. Spawns", "PMCs use every player spawn point", true, "Все точки спавна игроков",
                "QuestingBots ставит ЧВК только на точки «Player» их стороны. С этим - ещё на точки групп/другой стороны и на точки, которые сама игра отмечает для ЧВК-ботов. " +
                "Никогда - на вашу точку и ближе 25 м к ней");
            BalancedSpawnSides = B(cfg, "6. Spawns", "Spread squads over the map sides", true, "Разносить отряды по сторонам",
                "Как онлайн, лобби делится по карте: длинные карты (Таможня, Берег, Маяк) - на две половины, остальные - на четыре четверти. Каждый отряд идёт туда, где ЧВК меньше");
            BiggerLobby = B(cfg, "6. Spawns", "Bigger lobby", true, "Лобби больше онлайна",
                "Больше ЧВК со старта (число ниже), с большей долей одиночек: около 45 % групп - одиночки, остальные двойки-пятёрки. Выкл.: размер онлайн-лобби. Со следующего рейда");
            BiggerLobbyCount = B(cfg, "6. Spawns", "Extra PMCs at raid start", 5, "Добавочных ЧВК",
                "Сколько ЧВК добавить к онлайн-лобби карты", new AcceptableValueRange<int>(1, 10));
            OnlineSpawnMinDistance = B(cfg, "6. Spawns", "Min distance at raid start (m)", 15f, "Мин. расстояние на старте, м",
                "Ближе всего, на сколько группа может появиться к другому игроку на старте рейда (у QuestingBots - 30). Позже в рейде - расстояние QuestingBots",
                new AcceptableValueRange<float>(5f, 100f));
            LobbyLikeOnline = B(cfg, "6. Spawns", "Online-sized lobby", true, "Лобби как онлайн",
                "ЧВК на старте = онлайн-лобби карты (между мин. и макс. игроков), все сразу; позже заходят только запасные");
            PmcReserve = B(cfg, "6. Spawns", "Reserve PMCs", 3, "Запасные ЧВК",
                "Сколько ЧВК зайдёт позже, когда погибнут ЧВК стартового лобби (нужен перезапуск игры)", new AcceptableValueRange<int>(0, 8), simple: true);
            PackratChance = B(cfg, "6. Spawns", "Packrat chance, %", 15, "Шанс Барахольщика, %",
                "Шанс, что в рейде появится Барахольщик: ЧВК-одиночка с пистолетом и самым большим рюкзаком, который пришёл только за лутом - " +
                "избегает встреч, уходит от стрельбы и шума, дерётся только загнанным в угол. Не больше одного за рейд. 0 - не появляется", new AcceptableValueRange<int>(0, 100), simple: true);
            PmcSoloShare = B(cfg, "6. Spawns", "Lone PMCs among groups, %", 45, "Одиночек среди групп ЧВК, %",
                "Какая доля групп ЧВК - одиночки (остальные - двойки, тройки, четвёрки и пятёрки в прежней пропорции). 45 - как было с «Лобби больше онлайна»; " +
                "0 - одиночек почти нет, 100 - все ЧВК поодиночке. Доля ЧВК-одиночек из всех ЧВК заметно меньше доли групп (по трассам 31.10-02.11: 50 % групп = 28 % ЧВК). " +
                "Со следующего рейда", new AcceptableValueRange<int>(0, 100), simple: true);
            GroupFillsLobby = B(cfg, "6. Spawns", "Players of the group take lobby places", true, "Игроки группы занимают места",
                "Кооп (LiveRaid Online): каждый игрок-ЧВК группы сверх первого занимает место одного ЧВК-бота, как в онлайн-лобби (4 игрока на Таможне - на 3 бота меньше). " +
                "Легче для хоста. В одиночной - без эффекта");
            LivelyMap = B(cfg, "6. Spawns", "Lively map", true, "Живая карта",
                "Игроки-дикие приходят по своему расписанию с полутора минут рейда (до 2-5 одновременно), а не только когда перебита половина ЧВК и зашли все запасные; " +
                "в кооперативе на каждого игрока сверх первого - ещё 2 запасных ЧВК", simple: true);
            PScavReplace = B(cfg, "6. Spawns", "Player scavs as replacements", true, "Игроки-дикие на замену",
                "Когда ЧВК выбиты или вышли и «людей» (ЧВК, игроки-дикие, вы и ваша группа) меньше доли лобби, раньше своего часа заходит ближайшая по расписанию группа игроков-диких - "+
                "до последних 10 минут рейда. Новых ботов не создаётся (группы QuestingBots расписаны на весь рейд), потолок одновременных игроков-диких - «Живой карты». "+
                "После громкого боя заходят быстрее и с его стороны карты (не ближе 150 м к игроку). Нужна «Живая карта»");
            PeopleShare = B(cfg, "6. Spawns", "People share of the lobby, %", 60, "Доля людей, %",
                "Сколько «людей» держать на карте от лобби, пока не начались последние 10 минут. Действует, когда «Умный баланс численности» выключен " +
                "(включённый сам меняет долю по ходу рейда и по карте)", new AcceptableValueRange<int>(20, 100));
            PopulationBalance = B(cfg, "6. Spawns", "Smart population balance", true, "Умный баланс численности",
                "Сколько ЧВК, игроков-диких и ИИ-диких держит карта - чтобы не перебор и не пустыня. Старт: онлайн-лобби именно этой карты (по её размеру и закрытости) вместо общей цифры. " +
                "Каждый рейд со своим настроем (тихий / обычный / людный - раз в четыре рейда тише или гуще). Замена выбывших: доля людей зависит от возраста рейда " +
                "(в начале почти никого не добавляют, к концу всё меньше; на больших открытых картах людей держат больше), а если вся карта давно молчит - чуть больше. " +
                "Комфорт: если вокруг человека (или свободной камеры) в 120 м слишком много ботов, новые не заходят и волны ИИ-диких придерживаются. " +
                "Меняются только числа: никого не посылают к вам и события к вам не подталкивают. Часть действует со следующего рейда", simple: true);
            PopulationDensity = B(cfg, "6. Spawns", "Population density, %", 100, "Густота населения, %",
                "100 - как в онлайн-лобби карты, 70 - тише (меньше ботов и людей), 130 - гуще. Влияет на старт, на долю людей и на потолок ИИ-диких",
                new AcceptableValueRange<int>(50, 150));
            PopulationStrength = B(cfg, "6. Spawns", "Follow the lobby of the map, %", 60, "Следовать лобби карты, %",
                "Насколько число ЧВК на старте подтягивается от общей цифры (QuestingBots + «Лобби больше онлайна») к онлайн-лобби этой карты: 0 - не трогать, 100 - ровно лобби карты",
                new AcceptableValueRange<int>(0, 100));
            CrowdCap = B(cfg, "6. Spawns", "Comfort cap: bots near a human", 7, "Потолок толпы у человека",
                "Сколько ботов в 120 м от человека (или от свободной камеры) ещё терпимо: на этом числе новые игроки-дикие не заходят, а волны ИИ-диких придерживаются; " +
                "снова идут, когда станет на 2 меньше", new AcceptableValueRange<int>(3, 15));

            _cat = CLoot;
            LootThinking = B(cfg, "7. Looting like a player", "Think before looting", true, "Думать перед лутом",
                "Боты взвешивают видимую ценность тела (фуллка или бомж) против опасности (недавние выстрелы, неизвестный убийца рядом, открытое место, своё снаряжение, отряд). " +
                "Лутают сразу (своих жертв почти всегда), возвращаются позже или бросают тело, которое не стоит похода. Без засад у тела");
            NoLootInDanger = B(cfg, "7. Looting like a player", "No looting in danger", true, "Не лутать в опасности",
                "ЧВК и дикие прекращают лут (и не начинают), когда небезопасно: попадание или обстрел, выстрелы в 50 м, враг на виду или слышен рядом, чужой в 50 м, " +
                "которого слышно. Возвращаются к луту через 6 с после последнего признака опасности. Товарищи и союзники не считаются");
            LootNerve = B(cfg, "7. Looting like a player", "Nerve at the loot", true, "Выдержка при луте",
                "Не всех шум отрывает от лута одинаково: жадные и смелые (лутеры, рашеры, охотники) продолжают обыск при далёких шагах и бросают его только когда чужой близко, " +
                "стреляют рядом или враг на виду; крысы и кемперы осторожны как раньше. Выкл. - все бросают лут на любой шаг в 50 м");
            WoundedLoot = B(cfg, "7. Looting like a player", "Loot while hurt without meds", true, "Лутать раненым без аптечек",
                "LootingBots разрешает лут только когда нечего лечить; бот с раной и без аптечки так и не лутал до конца рейда. Здесь лутает, пока лечить нечем (с аптечкой - сперва лечится)");
            CleanUpAfterFight = B(cfg, "7. Looting like a player", "Clean up after a fight", true, "Зачистка после боя",
                "После боя отряд (или одиночка) обыскивает место: свои жертвы, убитых товарищами, тело павшего товарища и всё ценное в 50 м. В тишине бот идёт к самому важному и ближнему; " +
                "прерванное продолжает позже (до 5 раз за 8 минут). Лидер ждёт, пока товарищи обыскивают");
            NoticeBodies = B(cfg, "7. Looting like a player", "Notice bodies nobody has searched", true, "Замечать необысканные тела",
                "Бот, увидевший необысканное тело (впереди до 45 м или в 12 м вокруг), оценивает его как игрок: оружие, броня, шлем, разгрузка, ЧВК или дикий - рюкзак почти всегда стоит взгляда. " +
                "Да - тело становится его задачей (в тишине). Каждое тело оценивается ботом один раз");
            LootGivenFirst = B(cfg, "7. Looting like a player", "Bots go for the loot they are given", true, "Идти к выданному луту",
                "У диких слой лута LootingBots с тем же приоритетом, что слой ожидания QuestingBots, - дикий, получивший тело, стоял и уходил (обыскано 7 из 63). " +
                "Здесь слой ожидания QuestingBots уступает, как только у LootingBots есть цель");
            RoomSweep = B(cfg, "7. Looting like a player", "Search the whole room", true, "Обыскивать всю комнату",
                "Облутав что-то, бот проходит остальную комнату, как игрок в надежде на жирное: каждый контейнер в 12 м на этом этаже (до 6, 2,5 минуты на комнату), если тихо и есть место");
            WeightMatters = B(cfg, "7. Looting like a player", "Weight matters", true, "Вес имеет значение",
                "От 45 кг бот выбрасывает тяжёлое и дешёвое за килограмм ради находки (не только за клетку), от 60 кг - заметно раньше уходит из рейда");
            ShedWeight = B(cfg, "7. Looting like a player", "Shed weight to run", true, "Сбросить лишнее, чтобы бежать",
                "Бот так нагружен, что не может бежать (перегруз EFT), - в затишье он с некоторым шансом выкладывает самое дешёвое за килограмм из рюкзака, разгрузки и карманов, пока снова не сможет бежать. " +
                "Не трогает аптечки, гранаты, ключи, деньги, патроны и магазины к своим стволам, снаряжение и оружие павшего товарища, которое он выносит (дешёвое по весу из его вещей - можно)");
            ShedWeightChance = B(cfg, "7. Looting like a player", "Chance to shed weight, %", 60, "Шанс сбросить лишнее, %",
                "Один бросок на каждый перегруз бота: жадные реже, осторожные чаще", new AcceptableValueRange<int>(0, 100));
            DropCheapForBetter = B(cfg, "7. Looting like a player", "Drop cheap stuff for a better find", true, "Выбросить дешёвое ради находки",
                "Находка (от 15k) не влезает: бот выкидывает самое дешёвое, если оно вместе стоит не больше 40 % находки (никогда - аптечки, патроны, магазины, гранаты, ключи, квестовое, деньги). " +
                "С полным рюкзаком всё равно идёт к телу ценой 50k+ в 30 м");
            LootSpeed = B(cfg, "7. Looting like a player", "Loot speed x", 1.5f, "Скорость лута, x",
                "Во сколько раз быстрее LootingBots боты ищут и берут вещи (пауза после вещи, осмотр, задержка перед началом). 1 = как в LootingBots",
                new AcceptableValueRange<float>(1f, 3f), simple: true);
            LootRightSide = B(cfg, "7. Looting like a player", "Loot from the right side of the wall", true, "Лут со своей стороны стены",
                "LootingBots ставил точку, откуда лутать, на ближайший навмеш у вещи - у шкафа под тонкой стеной это часто соседняя комната: бот упирался в стену и бросал вещь " +
                "или лутал сквозь стену. Теперь точка, от которой вещь не видно сквозь стену или до которой нет пути, переносится на сторону вещи, а без такой вещь пропускается");
            LootStill = B(cfg, "7. Looting like a player", "Squat still while looting", true, "Не вертеться при луте",
                "Бот у тела или контейнера больше не крутится на месте (LootingBots переприцеливался на вещь под ногами при каждом качании анимации) и считает 1,6 м достаточно близким");
            LootWalk = B(cfg, "7. Looting like a player", "Walk to the loot at full step", true, "К луту - полным шагом",
                "Бот идёт к следующему ящику или полке в полный рост и обычным шагом до самого места: без торможения EFT на последних 1,5 м (скорость падала до 0,15 шага), " +
                "без присядки и без переключений в патрульную стойку SAIN по дороге. Приседает уже на месте. Идущего к далёкому телу QuestingBots больше не обрывает на 7-й секунде. Выкл. - мелкие медленные шажки, как раньше");
            LootProne = B(cfg, "7. Looting like a player", "Loot lying down (test)", true, "Лутать тела лёжа",
                "Подойдя к ТЕЛУ, бот с заданным шансом (ниже) сперва ложится, потом облутывает его лёжа и встаёт; иначе приседает, как раньше. " +
                "Контейнеры и вещи на земле всегда лутаются сидя. Негде лечь (склон, стена, вода) - присядет. Выкл.: всегда присед",
                simple: true);
            LootProneChance = B(cfg, "7. Looting like a player", "Chance to lie down at a body, %", 50, "Шанс лечь у тела, %",
                "Шанс, что бот ляжет перед обыском тела (жребий на каждое тело отдельно). 50 = пополам лёжа и сидя, 0 = всегда сидя, 100 = всегда лёжа",
                new AcceptableValueRange<int>(0, 100), simple: true);

            _cat = CSquad;
            SquadTactics = B(cfg, "8. Squads", "Squad tactics", true, "Тактика отряда",
                "Роли в отряде ЧВК (лидер, головной, прикрытие, стрелок, замыкающий - перераздаются при гибели лидера), лут места вместе, помощь товарищу в бою, квесты по вкусу всего отряда");
            TightSquads = B(cfg, "8. Squads", "Stay together", true, "Держаться вместе",
                "Ведомые держатся рядом, как отряд игроков: 4-12 м в движении (QuestingBots: 7-20), 8-22 м в бою (15-35); лидер ждёт отставших дальше 10 м и собирает дальше 18 м; " +
                "никто не уходит за лутом дальше 30 м и по поручению дальше 25 м");
            SquadLootWait = B(cfg, "8. Squads", "Wait for squadmates who loot", true, "Ждать лутающих",
                "Лидер не уходит, пока рядом лутает товарищ: ждёт (и возвращается к ушедшим на 20+ м) 100-190 с (40 с, если лутающий дальше 25 м), потом зовёт - лут прекращается, они идут следом");
            SquadLootSpread = B(cfg, "8. Squads", "Squad loot spread, m", 60f, "Разнос отряда при луте, м",
                "Насколько товарищи расходятся при луте. Первый начавший задаёт точку отряда; все берут лут не дальше половины этого от неё. QuestingBots позволяет ведомому лутать так далеко от лидера (было 30 м)",
                new AcceptableValueRange<float>(20f, 150f));
            HelpSquadmates = B(cfg, "8. Squads", "Drop everything to help a squadmate", true, "Бросить всё ради товарища",
                "Когда по товарищу стреляют или он в перестрелке, остальные бросают дела (лут, шаги квеста, поручения) и бегут к нему (кто рядом - сразу берут его врага)");
            RecoverFallenMate = B(cfg, "8. Squads", "Recover the loot of a fallen squadmate", true, "Забрать лут павшего",
                "Когда товарищ погиб и стало тихо, ближайший идёт к телу и забирает сколько влезет; не влезает - сперва прячет своё дешёвое рядом (куст, угол)");
            MateFullKit = B(cfg, "8. Squads", "Carry out the whole kit of a fallen squadmate", true, "Полный разлут товарища",
                "Поверх «Забрать лут павшего»: товарищ забирает всё основное снаряжение павшего (оружие, броню, шлем, рюкзак - рюкзак кладёт в свой целиком, если игра пускает) и его дорогие личные вещи, с которыми тот вышел в рейд; свои находки ради этого бросает у тела (не снаряжение и не свой набор: аптечки, патроны, магазины, ключи остаются), снаряжение и вещи друга весят вдвое. Что не влезло - по шансу ниже прячет в кустах, иначе почти сразу идёт на выход. Выкл. - как раньше");
            MateStashChance = B(cfg, "8. Squads", "Chance to hide the rest of a fallen squadmate loot in a bush, %", 50, "Шанс спрятать лут друга в кустах, %",
                "При полном разлуте товарища: шанс, что лут друга, который не влез, бот отнесёт и спрячет в укромном месте рядом (куст, если есть, иначе угол или завал); иначе с тем, что влезло, почти сразу идёт на выход",
                new AcceptableValueRange<int>(0, 100));
            MateStashRadius = B(cfg, "8. Squads", "How far to look for a bush for a fallen squadmate loot, m", 35f, "Дальность поиска куста для лута друга, м",
                "Как далеко от тела товарища бот ищет куст (или другое укромное место), чтобы спрятать его невлезший лут. Больше - чаще находит куст, но дольше идёт",
                new AcceptableValueRange<float>(10f, 100f));
            SquadFormation = B(cfg, "8. Squads", "Formation", true, "Строй",
                "Ведомые держат места в строю вокруг лидера (в зданиях - колонна), а не бегут все в точку лидера");
            SquadShare = B(cfg, "8. Squads", "Share loot and meds", true, "Обмен аптечками и лутом",
                "Товарищи в 40 м передают вещи: кровоточащему без перевязки, раненому без аптечки, с разбитой частью без хирургии - нужное от того, кто может поделиться; " +
                "с полным рюкзаком - лучшую лишнюю вещь (10k+) тому, у кого есть место. Подходит, бросает в руки, тот сразу подбирает");
            SquadVoice = B(cfg, "8. Squads", "Voice callouts", true, "Голосовые команды",
                "Отряды переговариваются: прикрой, прикрываю, на позиции, готов, лутай, принял, левый/правый фланг, рассредоточиться, товарищ ранен");
            SquadDoctrines = B(cfg, "8. Squads", "Squad doctrines", true, "Доктрины отрядов",
                "Каждый отряд ЧВК (и одиночка) играет рейд по-своему: штурмовики, PvP-охотники, лутеры, квестеры, кэрри, рандомы, клан, охотники на боссов, тихушники, дальники, торопыги, бронированный кулак. " +
                "Одна доктрина - не больше чем у двух отрядов за рейд; числа и две особенности отряда тянутся заново, пока отряд не отличается от всех остальных; " +
                "каждый боец играет доктрину по-своему - по своему стилю, навыку, агрессии, жадности и оружию");
            SquadPlaybook = B(cfg, "8. Squads", "Squad playbook", true, "Колода тактик отряда",
                "В бою лидер объявляет тактику из десяти: общий штурм, держать позиции, огневая база и обход, клещи, подавление и рывок, перебежки с прикрытием, двойка вперёд, " +
                "дожать раненого, откат и перегруппировка, каждый сам. Выбор - по доктрине, раскладу боя и тому, что отряд уже делал в этом рейде (повтор реже). " +
                "Кто-то может пойти своим путём, новичок подхватывает с опозданием");
            SquadListen = B(cfg, "8. Squads", "Stop and listen", true, "«Стоп, слышал»",
                "Услышав бой, отряд замирает на 3-6 с и поворачивается на звук, потом решает (чёткие отряды чаще, рандомы реже)");
            SquadComms = B(cfg, "8. Squads", "Squad comms", true, "Связь отряда",
                "Первый увидевший врага объявляет контакт (направление, дистанция) - отделённые QuestingBots товарищи узнают о нём через 0,5-3 с, молчаливые отряды иногда молчат; " +
                "павший товарищ - мстительный отряд идёт на убийцу, неустойчивый откатывается (без лидера - скоро на выход); после боя лидер ждёт несколько секунд, пока отряд соберётся; " +
                "на входе в здание замыкающий пару секунд держит улицу");
            PeekBait = B(cfg, "8. Squads", "Peek to draw fire", true, "Выглядывание-провокация",
                "Карточка колоды тактик: один боец 3-4 раза выглядывает на полсекунды и прячется, чтобы враг выдал себя выстрелом, остальные держат его позицию. Выпадает не всегда");
            ThirdPartyTurn = B(cfg, "8. Squads", "Turn to a third party", true, "Разворот на третью сторону",
                "Карточка колоды тактик: отряд обстреляли с нового направления (не со стороны его врага) - половина отряда поворачивается туда, остальные держат прежнего врага");
            SmokeScreens = B(cfg, "8. Squads", "Smoke screens", true, "Дымовая завеса",
                "Карточка колоды тактик и отход: боец с дымовой гранатой ставит завесу между отрядом и врагом - отряд под ней идёт вперёд или отходит; " +
                "в отходе и у тела павшего товарища, когда рядом враг, тоже может бросить дым");
            BossHunts = B(cfg, "8. Squads", "Boss hunts", true, "Охота на боссов",
                "Отряды доктрины «Охотники на боссов» всегда, часть штурмовиков, PvP-охотников, бронированного кулака и клана идут на босса карты на 2-8 минуте: " +
                "подходят в два-три этапа, бой - SAIN; бросают охоту раненым лидером, при потере половины отряда или через 14 мин; убитого босса и охрану обыскивают. Сами боссы не меняются");
            SquadFlanking = B(cfg, "8. Squads", "Flank in fights", true, "Фланги в бою",
                "SAIN никого не посылает в обход. В затихшем бою (враг известен, но не виден) отряд может послать одного - головного, рашера или охотника - во фланг незаметным путём, " +
                "пока остальные держат врага; слаженные отряды от 4 иногда берут с двух сторон. Смелый одиночка может обойти сам. Любой контакт - снова SAIN");
            SquadSpreadOut = B(cfg, "8. Squads", "Spread out in fights", true, "Расходиться в бою",
                "Двое из отряда бьют одного врага с одной точки (в 2,5 м): один перебегает к другому укрытию, чтобы граната или очередь не накрыла обоих");
            SquadTrade = B(cfg, "8. Squads", "React to a fallen squadmate", true, "Ответ за павшего",
                "Когда товарища убили, остальные примерно знают, где убийца: смелые идут разменять, остальные держат и смотрят в ту сторону");

            _cat = CFight;
            CombatMorale = B(cfg, "9. Combat morale", "Combat morale", true, "Мораль в бою",
                "Каждый бот взвешивает врагов и друзей, раны, потери, подавление и свою выдержку. Выигрывает - давит. Прижат или кровит - меняет позицию. " +
                "Проигрывает - отходит в укрытие. Разбит - рвёт контакт и бежит, потом лечится");
            SmoothCombatDecisions = B(cfg, "9. Combat morale", "Smooth combat decisions", true, "Плавные боевые решения",
                "SAIN перерешает 10 раз в секунду и ничего не доводит до конца. Здесь манёвр держится мгновение (поиск/рывок 2,5 с, укрытие 1,8, стоять и стрелять 1,2, замереть 1), " +
                "если не случилось срочного (граната, ближний бой, отход), враг не появился или не пропал, бот не ранен");
            GunfireResponse = B(cfg, "9. Combat morale", "Respond to gunfire", true, "Реакция на стрельбу",
                "Боты слышат перестрелки (и вашу) издалека и решают по характеру, снаряжению и опасности: ворваться, подойти, обойти, затаиться и слушать, уйти или не обращать внимания. " +
                "Реагируют только лидеры и одиночки, немного ботов на бой, с перерывами");
            NoiseResponse = B(cfg, "9. Combat morale", "Respond to noise", true, "Реакция на шум",
                "Боты слышат не только стрельбу: бег (50 м), шаги (20 м), дверь (40 м), лут (14 м), перезарядку (16 м); стена вдвое глушит, другой этаж - сильнее. " +
                "Спокойный лидер или одиночка идёт посмотреть, обходит, замирает послушать на пару секунд, тихо уходит или не обращает внимания - по характеру и доктрине. " +
                "Ваш шум слышат так же - вас ищут");
            Intercept = B(cfg, "9. Combat morale", "Lie in wait for an enemy you hear", true, "Засада на услышанного",
                "Услышав бегущего, идущего или дверь, бот (по характеру, как одна из реакций на шум) не бежит на звук, а предсказывает, куда враг идёт - дверь, место с лутом, " +
                "выход или рубильник выхода - и затаивается неподалёку, присев и глядя туда, на 12-25 с. Крысы, кемперы, снайперы и охотники часто, рашеры почти никогда. " +
                "Не пришёл - бот ещё раз угадывает (если слышит врага) или бросает засаду. Заранее, не слыша врага, засад нет. До 3 засад одновременно");
            InterceptWeight = B(cfg, "9. Combat morale", "Ambush frequency x", 1f, "Частота засад, ×",
                "Множитель веса засады среди реакций на шум: 0 - засад нет, 1 - как задумано, 3 - в три раза чаще", new AcceptableValueRange<float>(0f, 3f));
            FightLikePlayer = B(cfg, "9. Combat morale", "Fight like a player", true, "Бой как у игрока",
                "После нескольких выстрелов с одной точки (профи - 4-9, новичок - 18-30, длинный ствол - 2-4) боец меняет угол, а не выглядывает там, где его ждут; " +
                "попали на открытом месте издалека - бежит в укрытие, а не стоит; дорогой кит и рюкзак лута - осторожнее (реже в чужие бои, меньше упорства), бомжу терять нечего - смелее; " +
                "лутер, крыса, тихий отряд не начинают перестрелку с далёким ИИ-диким, который в них не стреляет");
            GrenadeHabit = B(cfg, "9. Combat morale", "Grenades like a player", true, "Гранаты как у игрока",
                "У каждого своя привычка: после броска пауза 10-60 с (штурмовики почти без паузы, крысы и лутеры берегут гранату, новичок забывает); " +
                "когда отряд идёт на штурм (общий штурм, штурм здания, растяжка и штурм, приманка, месть) - каждый может сразу бросить одну. Куда и можно ли бросить - решает SAIN");
            StormStims = B(cfg, "9. Combat morale", "Stims before a storm", true, "Стимулятор перед штурмом",
                "Смелые бойцы штурмового вызова отряда со стимулятором в сумке колют его перед штурмом (в трети случаев); тяжело раненые - как раньше, решает SAIN");
            NightDiscipline = B(cfg, "9. Combat morale", "Night discipline", true, "Ночная дисциплина",
                "Ночью дисциплинированный боец выключает фонарь, когда рядом враг, шум или бой вдалеке (и в засаде), и обходит открытые места. В ближнем контакте (до 70 м, стрельба началась) многие включают свет, чтобы слепить врага. Дисциплина — черта характера: у кого-то есть, у кого-то нет");
            GunfireRange = B(cfg, "9. Combat morale", "Gunfire hearing range x", 1.5f, "Слышимость стрельбы, x",
                "Множитель дальности, на которой слышен выстрел (дальность SAIN; глушители остаются тихими). Не больше 300 м", new AcceptableValueRange<float>(0.5f, 3f), simple: true);
            PmcAlwaysHostile = B(cfg, "9. Combat morale", "PMC squads always hostile", true, "ЧВК враждебны всем",
                "Как онлайн, отряд ЧВК - враг любому другому отряду ЧВК, включая свою сторону (BEAR-BEAR, USEC-USEC). Товарищи по отряду - свои", simple: true);
            DistantFightRange = B(cfg, "9. Combat morale", "Distant fights are not my fight (m)", 100f, "Далёкий бой не мой, м",
                "SAIN считает врагом всякого, о ком слышал, и боты минутами стояли в бою из-за перестрелки в 250 м. Дальше этого враг, которого бот не видел минуту и который в него не стреляет, " +
                "отдаётся «Реакции на стрельбу». 0 = как в SAIN", new AcceptableValueRange<float>(0f, 400f));
            FleeMorale = B(cfg, "9. Combat morale", "Leave a lost fight", true, "Уходить из проигранного боя",
                "Бот против явного перевеса (врагов больше, остался один, ранен, прижат, снаряжение хуже) может сдать нервы и уйти, забыв бой на минуту-две. " +
                "Никогда не наверняка: малый шанс, растущий с давлением, больше у робких, меньше у смелых, и никогда с врагом ближе 12 м");
            FleeChance = B(cfg, "9. Combat morale", "Leave a lost fight: chance x", 1f, "Шанс ухода из боя, x",
                "Множитель того, насколько охотно боты уходят из проигранного боя (0,2 - очень редко, 3 - часто)", new AcceptableValueRange<float>(0.2f, 3f));
            ColdFights = B(cfg, "9. Combat morale", "Lose interest in a cold fight", true, "Остывший бой забывается",
                "SAIN держит врага «известным», пока бот его ищет, и бот, никого не видевший минутами, крался за призраком до конца рейда. Здесь, когда никто не виден и не стрелял " +
                "весь запас терпения (крыса 20-45 с, большинство 35-65, рашер 55-95, охотник 70-125), враг забывается на 1,5-3 минуты. Новый взгляд, выстрел или попадание возвращают бой");
            GuardsCare = B(cfg, "9. Combat morale", "Boss guards forget cold fights and get unstuck", true, "Охрана боссов не стоит столбом",
                "Охрана боссов (Решала, Глухарь и т.п.) получает то же, что ЧВК и дикие: забывает врага, которого минутами не видит, и SAIN снова лечит её; застрявшего на ходу охранника сдвигают. " +
                "Рейд 17:08: Витёк Заводской 6 минут стоял в укрытии у общаг и истёк кровью с 33 % до 2 %, держа «в уме» врага в 100-160 м");
            ColdFightPatience = B(cfg, "9. Combat morale", "Cold fight patience x", 1f, "Терпение в поиске, x",
                "Множитель того, как долго бот ищет потерянного врага", new AcceptableValueRange<float>(0.4f, 3f));
            SearchFullPace = B(cfg, "9. Combat morale", "Search at full pace", true, "Поиск обычным шагом",
                "Поиск SAIN крался (вполсилы присев, четверть - у «скрытных») и стоял 1-4 с на каждой точке. Здесь бот ищет врага обычным шагом в рост и оглядывается меньше секунды");
            FloorAware = B(cfg, "9. Combat morale", "Fights on another floor are not my fight", true, "Бой на другом этаже не мой",
                "Бой над или под ботом (3+ м по высоте, без линии огня, никто не стреляет в бота) не держит врага «известным» и не гонит в укрытие или к стрельбе. Лестницы и выстрелы в бота - сразу");
            FightersOnly = B(cfg, "9. Combat morale", "Only fighters react to far shooting", true, "На далёкую стрельбу - только бойцы",
                "Дальность реакции на стрельбу зависит от бота: рашеры и охотники ~170 м, снайперы ~200, большинство 60-110, дикие меньше, занятые лутом или квестом ещё меньше. Выкл.: реагирует любой, кто слышит");
            ReturnFire = B(cfg, "9. Combat morale", "Return fire", true, "Ответный огонь",
                "Бот с врагом на виду стреляет, а не бегает: видел врага 2,5 с в 80 м и 5 с не стрелял - стоит и стреляет; не смог пройти - держит место 12 с; " +
                "разрыв контакта кончается, когда враг на виду ближе 25 м или бежать некуда");
            GunRange = B(cfg, "9. Combat morale", "Fight at the range of the gun", true, "Бой на дистанции оружия",
                "Бот, чьё оружие не достаёт до врага (дальше эффективной дальности типа из игры плюс запас, или болтовка ближе 20 м / марксманка ближе 15 м), берёт другой ствол, если тот достаёт, " +
                "а без него - сближается рывком, обходит, уходит с глаз или бросает врага (по характеру, здоровью и численности)");
            SidearmCare = B(cfg, "9. Combat morale", "Sidearm kept loaded", true, "Пистолет всегда заряжен",
                "Пустой пистолет в кобуре никто не заряжал (игра заряжает только оружие в руках): в затишье бот достаёт его, перезаряжает и убирает. " +
                "Пистолет опустел в ближнем бою - бот перезаряжает его, а не берёт болтовку или марксманку, которая на этой дистанции хуже (до 8 с, потом как обычно)");
            DogFightSanity = B(cfg, "9. Combat morale", "No dogfight with far enemies", true, "Без ближнего боя с дальним врагом",
                "SAIN включает «ближний бой» (стоять, стрейфить, стрелять - без укрытия), когда путь до врага короче 10 м, но если путь не построился, считает его длину 0 - " +
                "и бот часами стоит в «ближнем бою» с врагом в 100 м. Путь, короче прямой до той же точки, здесь не считается путём");
            BushSight = B(cfg, "9. Combat morale", "Bots see and shoot through bushes at each other", true, "Кусты между ботами",
                "У SAIN куст полностью закрывает и взгляд, и выстрел, трава - выстрел. Между двумя ботами (не против вас): ближе 25 м куст не прячет (заметить всё равно нужно время), " +
                "трава - ближе 10 м; сквозь листву бот стреляет ближе 25 м, а по тому, кого видел последние 3 с, - до 60 м; сквозь траву - до 30 м");
            GunRangeMargin = B(cfg, "9. Combat morale", "Effective range margin, m", 100f, "Запас дальности, м",
                "Прибавка к эффективной дальности для типов ниже. Дальность типа - верх эффективных дальностей его стволов в игре: дробовик 100 м, пистолет 70, ПП 500, карабин 400, " +
                "автомат 900, марксманка 1000, болтовка 1000, пулемёт 800. 0 = без запаса", new AcceptableValueRange<float>(0f, 500f));
            GunRangeMarginClasses = B(cfg, "9. Combat morale", "Effective range margin for", "pistol, sniperRifle, assaultRifle, marksmanRifle", "Запас дальности для",
                "Типы оружия с запасом через запятую: pistol, sniperRifle (болтовка), assaultRifle, marksmanRifle, assaultCarbine, smg, shotgun, machinegun. Остальные - дальность игры как есть");
            LongGunHolds = B(cfg, "9. Combat morale", "Long gun holds its position", true, "Длинный ствол держит позицию",
                "Бот со снайперской или марксманской винтовкой (или снайпер по стилю) не отходит и не «идёт искать», пока враги дальше 35 м и он здоров: стреляет оттуда, где стоит");
            FireDiscipline = B(cfg, "9. Combat morale", "Fire discipline", true, "Огневая дисциплина",
                "Бот не высаживает весь магазин в стену, за которой спрятался враг: подавляет очередями с паузами и оставляет часть магазина на выглядывание " +
                "(новичок - треть, профи - две трети). Без ограничений - только когда прикрывает товарища, который идёт на штурм или в обход, или сам давит. " +
                "Новички и рашеры всё равно иногда сыплют патронами");
            BlindFireHold = B(cfg, "9. Combat morale", "Hold the blind-fire pose", true, "Держать позу стрельбы из-за угла",
                "Бот, стреляющий из-за укрытия «по-сомалийски» (оружие поднято над углом или сбоку), держит позу, а не включает и выключает её каждые 0,2 с: " +
                "ответ проверки SAIN держится 2 с, сброс позы откладывается на 1,5 с (если бот не пошёл, не остался без патронов и в него не попали). Выкл. - как в SAIN");
            PressHurt = B(cfg, "9. Combat morale", "Press a hurt enemy", true, "Дожимать раненого",
                "Бот попал по врагу (или попал его отряд), видит, что тот тяжело ранен, или слышит в 25 м, как он лечится - и по характеру идёт на него, пока тот не залечился: " +
                "охотники и рашеры почти всегда, лутеры и квестеры иногда, крысы, кемперы и снайперы редко; раненый сам или вдвое меньший числом - никогда. " +
                "Отряд дожимает раненого дольше (18-30 с)");
            CloseNotice = B(cfg, "9. Combat morale", "Notice an enemy right next to you", true, "Замечать врага вплотную",
                "Враг в 5 м (или движущийся в 10 м) на том же этаже и на открытой линии замечается за 1-2,5 с (по навыку), как игрок его слышит и краем глаза видит. " +
                "Бегущий в 12 м или идущий в 6 м слышен и сквозь стену или на лестнице этажом выше/ниже - бот поворачивается к нему. " +
                "Занятый лутом или квестом бот раньше давал врагу минуту стоять за спиной");
            ReloadUnderFire = B(cfg, "9. Combat morale", "Reload under fire", true, "Перезарядка под огнём",
                "Застигнутый близким врагом посреди долгой перезарядки (магазин больше 30 или 4+ патрона в трубу): с заряженным пистолетом или вторым стволом - меняет, вплотную - нож, " +
                "остальные перезаряжаются на бегу к укрытию. Обычная перезарядка при враге на виду в 15 м - так же (чуть реже), иначе тоже на бегу к укрытию. Опытные чаще новичков. Никто не давит и не стоит стреляя, пока перезаряжается");
            KnifeAtArmsLength = B(cfg, "9. Combat morale", "Knife only at arms length", true, "Нож только вплотную",
                "Нож - только для врага в 3 м или когда ни в одном стволе нет патрона; с ножом и далёким (или пропавшим) врагом бот сразу берёт ствол с патронами. SAIN бегал с ножом на любой дистанции");
            FightRoles = B(cfg, "9. Combat morale", "Fight roles", true, "Роли в бою",
                "Каждый бот в бою берёт роль - штурм, фланг или удержание - по характеру, здоровью и прежде всего численности: двое на одного - давит даже лутер, один на двоих - держит даже рашер. " +
                "Выкл.: давят только рашеры и охотники");

            _cat = CHeal;
            HealFirst = B(cfg, "9. Combat morale", "Heal at the first quiet moment", true, "Лечиться в первую тихую минуту",
                "Раненый бот, по которому сейчас не стреляют (врага не видно, попаданий несколько секунд нет), сразу перевязывается, и приказы его не сбивают. " +
                "SAIN начинал лечение, только когда остынут все враги, - боты с аптечками в рюкзаке истекали кровью");
            HealBreakOff = B(cfg, "9. Combat morale", "Stop treatment when shot at", true, "Прерывать лечение под огнём",
                "Бот прерывает перевязку, укол или операцию, как игрок (ПКМ): обстрел, попадание, враг на виду в 50 м или известен в 12 м - анимация прерывается, ничего не тратится, ствол снова в руках. " +
                "Пока идёт лечение, другая смена оружия его не срывает");
            TreatLegs = B(cfg, "9. Combat morale", "Treat a broken leg", true, "Лечить сломанную ногу",
                "Со сломанной или разбитой ногой без обезболивающего EFT запрещает бег и режет скорость до 30 %, а боты EFT шин не накладывают. Здесь бот в тишине ставит шину " +
                "(или берёт обезболивающее, если шины нет или нога разбита); нечем - товарищ передаст");
            LegSprintLimit = B(cfg, "9. Combat morale", "Run on a hurt leg only when it must", true, "Бег на больной ноге - только по нужде",
                "На обезболивающем бот с больной ногой снова может бегать, и каждая секунда бьёт по ноге. Здесь он идёт обычным шагом и бежит рывками только по нужде " +
                "(сломанная - при попадании, обстреле, враге рядом, до 4 с; разбитая - только при попадании или обстреле, до 2,5 с)");
            SurgeryFromBag = B(cfg, "9. Combat morale", "Surgery with a kit from the bag", true, "Хирургия набором из рюкзака",
                "Бот восстанавливает разбитую часть хирургическим набором (CMS, Surv12) из рюкзака, разгрузки или карманов, найденным в рейде. EFT искал набор только в подсумке - ЧВК не оперировались");
            UsableMedkit = B(cfg, "9. Combat morale", "Only a medkit that can stop the bleeding", true, "Аптечка, что остановит кровь",
                "Боты EFT брали первую аптечку, способную остановить кровь, даже с остатком, которого не хватит, и ставили её каждые 5 с до смерти. Здесь берётся аптечка с запасом; " +
                "без такой лечится, что можно, а бесполезное лечение дважды подряд не повторяется 45 с");
            SurgeryWhenSafe = B(cfg, "9. Combat morale", "Surgery only when safe", true, "Операция только в безопасности",
                "SAIN начинал «операцию» при любом известном враге и бросал её через тик, а через 2 с снова - каждый старт останавливал бота на 3 с, и раненый замирал на минуты под огнём. " +
                "Здесь операция начинается только при чистой обстановке. Выкл.: как в SAIN");
            ExfilPatchUp = B(cfg, "9. Combat morale", "Patch up and fight before heading out", true, "Сначала бой и перевязка, потом выход",
                "Путь EFT к выходу был выше всего: бот уходил кровоточащим, с пустым стволом, мимо стреляющего врага. Здесь сначала враг в 60 м, кровотечение или тяжёлая рана, " +
                "на которые есть лечение, и сломанная нога, которую можно зашинировать (до 45 с за раз)");
            NoJumpOnBrokenLegs = B(cfg, "4. Bot characters", "No jumping on broken legs", true, "Не прыгать на сломанных ногах",
                "Бот со сломанной или разбитой ногой не прыгает (рывки SAIN, застревания QuestingBots, маршруты LiveRaid) - приземление снова бьёт по ноге");
            KeepMagazines = B(cfg, "9. Combat morale", "Keep emptied magazines", true, "Не выбрасывать магазины",
                "EFT кладёт пустой магазин после перезарядки в разгрузку или карманы, а если там полно - бросает на землю. Бот с забитой разгрузкой терял магазин на каждой перезарядке " +
                "(охрана Решалы целилась и не стреляла). Здесь пустой уходит в рюкзак или подсумок, а недоступные магазины возвращаются в разгрузку и снаряжаются");
            BottomlessAmmo = B(cfg, "9. Combat morale", "Bottomless ammo", true, "Патроны из воздуха",
                "Выкл. = как в SPT: только то, что выдано боту. Вкл.: когда нечем зарядить пустой магазин, призывается россыпь (у ЧВК - только в защищённый контейнер, у диких - в разгрузку или карман), " +
                "новая пачка - когда прежняя кончилась; найденные в рейде патроны не считаются. Для оружия, с которым бот начал рейд, магазины не делаются; для найденного - один запасной, " +
                "если магазинов к нему у бота нет. Перезарядка идёт как обычно, с анимацией и временем");
            BottomlessReserve = B(cfg, "9. Combat morale", "Summoned rounds per stack", 40, "Патронов в призванной пачке, не больше",
                "Пачка россыпи - от этого числа минус 10 до этого числа (30-40 по умолчанию)", new AcceptableValueRange<int>(10, 100));
            SummonedYield = B(cfg, "9. Combat morale", "Summoned items give way", true, "Призванное уступает место добыче",
                "Призванные патроны и магазины не считаются занятыми клетками рюкзака (бот не «полон» из-за них) и убираются, когда добыче не хватает места");
            AimWithoutShotWatch = B(cfg, "9. Combat morale", "Watch for aiming without a shot", true, "Следить за прицелом без выстрела",
                "Любой бот (и боссы с охраной), 6 с державший врага на виду и в досягаемости без выстрела: пустой или не дослан - перезарядка, зависшая перезарядка - снимается, " +
                "состояние рук, ствола и магазинов - в журнал («LR Weapon: ... aims at ... without a shot»)");
            FreeStuckHands = B(cfg, "9. Combat morale", "Free stuck hands", true, "Освобождать зависшие руки",
                "EFT снимает «перезарядку» только по концу анимации; если её прервали аптечка, смена оружия или лут, флаг висит вечно: бот не стреляет, не меняет оружие и не лечится. " +
                "Здесь висящий флаг снимается, пустые руки получают ствол обратно, незавершённая смена отпускается");
            SteadyWeapons = B(cfg, "9. Combat morale", "Steady weapon handling", true, "Спокойная смена оружия",
                "LootingBots брал основной ствол после каждой облутанной вещи, EFT / SAIN / LootingBots меняли оружие каждый сам по себе. Бот, сменивший ствол 3 раза за 15 с, держит текущий 12 с; " +
                "шанс EFT сменить оружие вместо перезарядки снижен с 80 % до 35 %");
            SquadmatesNeverEnemies = B(cfg, "9. Combat morale", "Squadmates are never enemies", true, "Свои никогда не враги",
                "Бот никогда не считает врагом товарища, с которым появился, что бы ни случилось с группами потом. QuestingBots выделял бота, не вылечившегося 2 минуты, в свою группу, " +
                "и EFT считал его чужим ЧВК - DeadW0Lf застрелил своего лидера с 4 м");

            _cat = CTasks;
            QuestDriveOn = B(cfg, "10. Quests and routes", "Head for the task", true, "Идти к шагу квеста",
                "ЧВК, получивший шаг квеста (или долго торчащий у одного), в основном идёт к нему, как игрок на задание: останавливается на то, что прямо на пути (12 м), и на тела, " +
                "товарищи лутают только рядом, лутающего ждут не больше ~45 с вместо 2-3 мин");
            QuestSpread = B(cfg, "10. Quests and routes", "Squads spread over quests", true, "Отряды расходятся по квестам",
                "Квест, который уже держат другие отряды ЧВК, менее желанен - вдвое за каждый такой отряд, - и отряды идут разными путями, а не шестеро в один район");
            QuestStepGiveUpMin = B(cfg, "10. Quests and routes", "Give up a quest step after (min)", 10f, "Бросить шаг квеста через, мин",
                "ЧВК, державший один шаг квеста столько времени и не сделавший (бои не в счёт), бросает его и берёт другой; у каждого 75-130 % от этого. 0 = никогда",
                new AcceptableValueRange<float>(0f, 60f));
            QuestAudit = B(cfg, "10. Quests and routes", "Check quest objectives", true, "Проверять цели квестов",
                "На старте рейда проверить каждую цель квестов EFT карты: есть ли у QuestingBots точка, дойдут ли боты, на месте ли точка. Итог в журнале (LR Quests on <карта>) и questaccess/<карта>.csv");
            QuestPointFix = B(cfg, "10. Quests and routes", "Fix quest points", true, "Исправлять точки квестов",
                "Если QuestingBots поставил точку квеста вне зоны или на другом этаже, перенести её на настоящее место (достижимый пол в зоне / рядом с предметом). Запертым комнатам назначается дверь");
            QuestSimulation = B(cfg, "10. Quests and routes", "Act out quest objectives", true, "Отыгрывать цели квестов",
                "ЧВК на квесте EFT отыгрывает его как игрок: присесть и поднять квестовый предмет (предмет остаётся), заложить маркер на время закладки, осмотреться на месте. Только тогда цель засчитана");
            QuestAnimations = B(cfg, "10. Quests and routes", "Quest hand animations", true, "Анимации рук в квестах",
                "Анимация «взять / положить» во время целей квестов (выключить, если у бота зависают руки)");
            Routes = B(cfg, "10. Quests and routes", "Routes through blocked places", true, "Маршруты через завалы",
                "Записанные маршруты BepInEx/plugins/LiveRaid/routes/<карта>.json: боты лезут через завалы, идут по перилам туда, куда навмеш не ведёт. Со следующего рейда");
            RouteTeleportUnseen = B(cfg, "10. Quests and routes", "Skip the climb when nobody sees it", true, "Без лазания, когда никто не видит",
                "Бот, которого никто не видит, сразу переносится в конец маршрута; тот, кого видно, лезет по точкам");
            RouteAreaBots = B(cfg, "10. Quests and routes", "Bots sent up a route per raid", 2, "Ботов на маршрут за рейд",
                "Места, куда ведёт только маршрут («Утёс» на Эпицентре), получают цели не больше чем для стольких разных ботов за рейд. 0 = никого", new AcceptableValueRange<int>(0, 10));
            UseLearnedShortcuts = B(cfg, "10. Quests and routes", "Use learned shortcuts", false, "Использовать найденные срезки",
                "Экспериментально. Залезы из ваших маршрутов (learned/<карта>.shortcuts.json) становятся маршрутами, как записанные. Сначала просмотрите файл - вы могли залезть туда, куда ботам не надо");
            RouteRecorder = B(cfg, "10. Quests and routes", "Route recorder", false, "Запись маршрута",
                "Записать маршрут самому: встаньте перед препятствием и нажмите «Точка», жмите на каждой опоре (присесть - низкая точка, «Точка с прыжком» - где нужен прыжок), " +
                "закончите на полу за препятствием и нажмите «Сохранить». Маршрут работает сразу", synced: false);
            RouteRecordPoint = B(cfg, "10. Quests and routes", "Record point", new KeyboardShortcut(KeyCode.Keypad7), "Маршрут: точка", "Записать точку маршрута", synced: false);
            RouteRecordJump = B(cfg, "10. Quests and routes", "Record jump point", new KeyboardShortcut(KeyCode.Keypad8), "Маршрут: точка с прыжком", "Записать точку с прыжком", synced: false);
            RouteUndo = B(cfg, "10. Quests and routes", "Remove last point", new KeyboardShortcut(KeyCode.Keypad4), "Маршрут: убрать точку", "Убрать последнюю точку", synced: false);
            RouteSave = B(cfg, "10. Quests and routes", "Save route", new KeyboardShortcut(KeyCode.Keypad9), "Маршрут: сохранить", "Сохранить маршрут", synced: false);

            MapEdits = B(cfg, "10. Quests and routes", "Map edits from Studio", true, "Правки карты из Studio",
                "Применять то, что нарисовано в LiveRaid Studio (BepInEx/plugins/LiveRaid/studio/edits/<карта>.json): свои куски навмеша, запреты, связки, лазания, маршруты и места для ботов. Правки навмеша и запреты работают сразу, маршруты и места - со следующего рейда");
            MapEditsAutoReload = B(cfg, "10. Quests and routes", "Reload edits when the file changes", true, "Подхватывать правки на лету",
                "Когда Studio сохранил файл правок, навмеш, запреты, связки и лазания обновляются в идущем рейде за пару секунд (свободная камера покажет результат)", synced: false);
            MapEditsDump = B(cfg, "10. Quests and routes", "Dump the navmesh for Studio", true, "Выгружать навмеш для Studio",
                "В первом рейде на карте записать её навмеш, двери, выходы и точки спавна в studio/dump/<карта> - это та карта, которую Studio открывает для правки", synced: false);
            MapEditsRedump = B(cfg, "10. Quests and routes", "Dump the navmesh again", false, "Выгрузить навмеш заново",
                "В начале следующего рейда перезаписать выгрузку навмеша этой карты (после обновления игры или чужого мода навмеша). Включится обратно в «Выкл.» сам", synced: false);
            MapEditsOverlay = B(cfg, "10. Quests and routes", "Show the edits in the world", false, "Показывать правки в мире",
                "Нарисованное в Studio видно в мире: куски навмеша зелёные, запреты красные, связки жёлтые, лазания оранжевые, маршруты ботов голубые, места розовые", synced: false);
            MapEditsReloadKey = B(cfg, "10. Quests and routes", "Reload edits", new KeyboardShortcut(KeyCode.Keypad1), "Правки: перечитать", "Перечитать файл правок Studio прямо сейчас", synced: false);
            MapEditsOverlayKey = B(cfg, "10. Quests and routes", "Toggle edits overlay", new KeyboardShortcut(KeyCode.Keypad3), "Правки: показать / скрыть", "Показать или скрыть правки Studio в мире", synced: false);

            _cat = CPerf;
            SimplifyDistantBots = B(cfg, "5. Performance", "Simplify distant bots", true, "Упрощать дальних ботов",
                "Вкл.: боты далеко от вас и не в бою реже «думают» (зрение и решения SAIN), но двигаются, квестуют, лутают и уходят - никто не спит. Выкл.: каждый бот полноценный на любом расстоянии (тяжелее для ЦП)",
                simple: true);
            QbSleeping = B(cfg, "5. Performance", "Allow QuestingBots to put far bots to sleep", false, "Разрешить QuestingBots усыплять дальних",
                "Выкл. (LiveRaid): никто не спит - дальние боты двигаются, квестуют, лутают и дерутся, лишь думают реже. Вкл.: ограничитель QuestingBots (его F12 и дистанции) может " +
                "полностью заморозить дальних ботов. Легче для ЦП", simple: true);
            LodByCamera = B(cfg, "5. Performance", "Tiers by the camera", true, "Ярусы по камере",
                "Наблюдатель и после смерти нет живого человека, и SAIN не умеет делить ботов на ближних и дальних. Вкл.: делит LiveRaid - по расстоянию до камеры: " +
                "ближе «Ближний ярус» - каждый кадр, дальше - каждый 2-й, 3-й, 4-й. Бот с врагом и все возле камеры думают каждый кадр; движение, квесты, лут и выходы не режутся. " +
                "Облегчает ЦП, когда ботов много", simple: true);
            LodNear = B(cfg, "5. Performance", "Near tier (m)", 150, "Ближний ярус, м",
                "Радиус вокруг камеры, где боты думают каждый кадр (дальше - вдвое, втрое и вчетверо реже)", new AcceptableValueRange<int>(60, 400));
            LodAdaptive = B(cfg, "5. Performance", "Think less when the frame rate is low", true, "Реже думать при низком FPS",
                "Когда сглаженный FPS падает ниже 38 (ниже 25), дальние ярусы думают ещё на 1 (на 2) кадра реже, а при ярусах по камере ближний радиус сужается; " +
                "выше 46 FPS всё возвращается. Ближние боты и боты в бою не затрагиваются", simple: true);
            FarBotsFullSenses = B(cfg, "5. Performance", "Far bots see and hear each other", true, "Дальние боты видят и слышат друг друга",
                "Вкл. (как онлайн): боты дерутся друг с другом на полной дальности по всей карте. Выкл.: ограничения SAIN «ИИ против ИИ» - вдали от вас боты видят друг друга лишь на " +
                "200 / 100 / 50 м и слышат на 100 / 60 / 25 м, дальние отряды проходят мимо друг друга. Чуть легче для ЦП", simple: true);

            // After death and observer raids are single player only. LiveRaid Online keeps these settings out of F12 (a file of their own) and off:
            // in Fika the raid goes on for the others and Fika has its own camera after death
#if LIVERAID_ONLINE
            ConfigFile dcfg = new ConfigFile(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(cfg.ConfigFilePath), "com.lir.liveraid.single-player-only.cfg"), false);
#else
            ConfigFile dcfg = cfg;
#endif
            _cat = CDeath;
            const string death = "11. After death";
            SpectateAfterDeath = B(dcfg, death, "Free camera after death", true, "Свободная камера после смерти",
                "Когда вы погибли, рейд не кончается: свободная камера летает над картой, вы смотрите на ботов. Рейд заканчивается клавишей «Закончить рейд» (внизу слева) " +
                "или по времени. Итог - всегда ваша смерть: ничто после неё не идёт в профиль", simple: true, synced: false);
            ObserverFromStart = B(dcfg, death, "Start raids as an observer", false, "Начинать рейд наблюдателем",
                "Вкл.: вы не играете рейд - он сразу начинается в свободной камере (следить за ботами, вселиться, клавиша End). Ваш персонаж вне игры: скрыт, невредим, " +
                "никто его не видит. Рейд не засчитывается: снаряжение как было, ни опыта, ни статистики (нужен серверный мод LiveRaid). Со следующего рейда", simple: true, synced: false);
            ObserverFollowAtStart = B(dcfg, death, "Follow a random PMC at the start", true, "В начале следить за случайным ЧВК",
                "Работает с «Начинать рейд наблюдателем». Как только в рейде появился ЧВК, камера сразу прыгает к случайному и следит за ним, как по клавише «Следить за ботом». " +
                "Свободный полёт (WASD) отменяет слежение, «Следующий бот» переключает. Нет ЧВК за первую минуту - остаётся свободная камера", simple: true, synced: false);
            ObserverFollowPackrat = B(dcfg, death, "Follow the packrat at the start", false, "В начале следить за Барахольщиком",
                "Только для проверки (виден лишь в режиме разработчика). С «Начинать рейд наблюдателем»: камера в начале рейда ждёт Барахольщика (до 3 минут: он приходит с группами ЧВК), " +
                "прыгает к нему и следит за ним. Не появился - к случайному ЧВК, как обычно. Шанс его появления - в разделе «Спавн»", synced: false);
            TakeOverBots = B(dcfg, death, "Take over a bot", false, "Вселяться в бота",
                "Жизнь после смерти: в свободной камере наведитесь на любого бота и нажмите «Вселиться» - играете им с его снаряжением и инвентарём, пока он не погибнет или не выйдет, потом можно вселиться в следующего. Если у вселённого не работают руки (оружие не стреляет, не перезаряжается) - выйдите из него и вселитесь снова. Его убийства, лут и выход в профиль не идут", simple: true, synced: false);
            EndRaidWhenTakenBotDies = B(dcfg, death, "End the raid when the taken bot dies", false, "Конец рейда со смертью бота",
                "Выкл.: назад в свободную камеру, можно вселиться в другого. Вкл.: рейд сразу заканчивается", synced: false);
            SpectatorBotNames = B(dcfg, death, "Bot names in the free camera", true, "Имена ботов в камере",
                "Маленькие таблички с именами над ботами в поле зрения (до 150 м) в свободной камере", synced: false);
            FreeCameraSpeed = B(dcfg, death, "Free camera speed (m/s)", 8f, "Скорость камеры, м/с",
                "Базовая скорость; Shift x4, Alt x0,25, колесо мыши меняет её в полёте", new AcceptableValueRange<float>(1f, 60f), synced: false);
            SpectatorEndRaid = B(dcfg, death, "End raid", new KeyboardShortcut(KeyCode.End), "Закончить рейд",
                "Закончить рейд как вашу смерть (из свободной камеры или из бота)", synced: false);
            SpectatorTakeOver = B(dcfg, death, "Take over", new KeyboardShortcut(KeyCode.F), "Вселиться",
                "Вселиться в бота в центре экрана (или в того, за кем следите)", synced: false);
            SpectatorLeaveBot = B(dcfg, death, "Leave the bot", new KeyboardShortcut(KeyCode.Delete), "Покинуть бота",
                "Вернуть бота его ИИ и вернуться в свободную камеру", synced: false);
            SpectatorFollow = B(dcfg, death, "Follow bot", new KeyboardShortcut(KeyCode.V), "Следить за ботом",
                "Лететь за ботом в центре экрана (мышь - вокруг него, колесо - расстояние); WASD - снова свободный полёт", synced: false);
            SpectatorNextBot = B(dcfg, death, "Next bot", new KeyboardShortcut(KeyCode.N), "Следующий бот",
                "Следить за следующим живым ботом (сначала ближайшие)", synced: false);
            SpectatorToggleView = B(dcfg, death, "Watch through the bot eyes", new KeyboardShortcut(KeyCode.Home), "Глазами бота",
                "При слежении (без вселения): камера за ботом или его глазами", synced: false);

#if LIVERAID_ONLINE
            SpectateAfterDeath.Value = false;
            ObserverFromStart.Value = false;
            TakeOverBots.Value = false;
#endif

            // What each pack and level of the compact menu stands for
            SettingsMenu.PackOf(packKeys, RealKeys, FoundKeys, DoorManners, RoomPeek, DoorAssist, FrozenWatch, LayerCalm, FreeShutIn, BackOffWhenStuck, ContainerKeys);
            SettingsMenu.LevelOf(keyAmount, new ConfigEntryBase[] { PmcKeyChance, ScavKeyChance, MaxKeysPerBot },
                (SettingsMenu.Level.Low, "Мало", new object[] { 0.3f, 0.1f, 2 }),
                (SettingsMenu.Level.Normal, "Как онлайн", new object[] { 0.6f, 0.25f, 3 }),
                (SettingsMenu.Level.High, "Много", new object[] { 0.85f, 0.45f, 4 }));
            SettingsMenu.LevelOf(prizeKeys, new ConfigEntryBase[] { PrizeKeys, PrizeKeyChance, PrizeKeyStep },
                (SettingsMenu.Level.Off, "Нет", new object[] { false, null, null }),
                (SettingsMenu.Level.Low, "Редко", new object[] { true, 0.4f, 0.25f }),
                (SettingsMenu.Level.Normal, "Обычно", new object[] { true, 0.75f, 0.2f }),
                (SettingsMenu.Level.High, "Часто", new object[] { true, 1f, 0.15f }));

            SettingsMenu.PackOf(packMap, NeverIdle, MapWideQuests, MapBalance, MapBalanceStrength, MapBalanceUneven, DatabaseLootSpots, ScavsQuest, ScavPatrol, PlayerScavLife, CoverRoutes, PeopleCorridors, CorridorStrength, TempoLikePlayers, Swivel, Overlook, Overwatch, AirdropEvent, HotZones, MoveOn, HotDrop, KeyPlaces, ThoroughLooting,
                BtrPatience, ShortQbHolds, RoamRegionSize, RoamDesirability);
            SettingsMenu.AmountOf(eviction, Eviction, EvictionMinutes, 3);
            SettingsMenu.PackOf(packLearn, LearnFromMyRoute, BotsVisitLearned, UseLearnedShortcuts);

            SettingsMenu.PackOf(packChars, Personalities, SkillSpread, MemoryMultiplier, MaxSearchWait, RaidMoods, SelfPreservation);
            SettingsMenu.PackOf(packExfil, PersonalExtract, ExtractCurve, SwitchExits, SpecialExits, CarLeaves, ExitArrival, ExitWait, EarlyExit, BodiesBeforeLeaving, ExitsBySide, ExtractTogether);

            SettingsMenu.PackOf(packSpawn, OnlineSpawns, SpawnAllPoints, BalancedSpawnSides, LobbyLikeOnline, GroupFillsLobby, LivelyMap, PScavReplace, PeopleShare, PopulationBalance, PopulationDensity, PopulationStrength, CrowdCap, OnlineSpawnMinDistance);
            SettingsMenu.AmountOf(extraPmcs, BiggerLobby, BiggerLobbyCount, 1);

            SettingsMenu.PackOf(packLoot, LootThinking, LootRightSide, NoLootInDanger, LootNerve, LootWalk, WoundedLoot, CleanUpAfterFight, NoticeBodies, LootGivenFirst, RoomSweep, DropCheapForBetter, WeightMatters, ShedWeight, ShedWeightChance,
                LootStill, LootProne, LootProneChance);
            SettingsMenu.PackOf(packSquad, SquadTactics, TightSquads, SquadLootWait, HelpSquadmates, RecoverFallenMate, MateFullKit, MateStashChance, MateStashRadius, SquadFormation, SquadShare, SquadVoice,
                SquadDoctrines, SquadPlaybook, SquadListen, SquadComms, PeekBait, ThirdPartyTurn, SmokeScreens, BossHunts, SquadFlanking, SquadSpreadOut, SquadTrade, SquadLootSpread);

            SettingsMenu.PackOf(packFight, CombatMorale, SmoothCombatDecisions, GunfireResponse, NoiseResponse, Intercept, InterceptWeight, FightLikePlayer, GrenadeHabit, StormStims, NightDiscipline, SearchFullPace, FightRoles, ReturnFire, GunRange, SidearmCare, DogFightSanity, BushSight, LongGunHolds,
                CloseNotice, FireDiscipline, BlindFireHold, PressHurt, FloorAware, FightersOnly, ReloadUnderFire, KnifeAtArmsLength, DistantFightRange, GunRangeMargin, GunRangeMarginClasses);
            SettingsMenu.LevelOf(flee, new ConfigEntryBase[] { FleeMorale, FleeChance },
                (SettingsMenu.Level.Off, "Никогда", new object[] { false, null }),
                (SettingsMenu.Level.Low, "Редко", new object[] { true, 0.5f }),
                (SettingsMenu.Level.Normal, "Обычно", new object[] { true, 1f }),
                (SettingsMenu.Level.High, "Часто", new object[] { true, 2f }));
            SettingsMenu.LevelOf(coldFights, new ConfigEntryBase[] { ColdFights, ColdFightPatience },
                (SettingsMenu.Level.Off, "Никогда", new object[] { false, null }),
                (SettingsMenu.Level.Low, "Быстро", new object[] { true, 0.6f }),
                (SettingsMenu.Level.Normal, "Обычно", new object[] { true, 1f }),
                (SettingsMenu.Level.High, "Долго", new object[] { true, 2f }));

            SettingsMenu.PackOf(packHeal, HealFirst, HealBreakOff, TreatLegs, LegSprintLimit, SurgeryFromBag, UsableMedkit, SurgeryWhenSafe, ExfilPatchUp, NoJumpOnBrokenLegs);
            SettingsMenu.PackOf(packFixes, GuardsCare, KeepMagazines, AimWithoutShotWatch, FreeStuckHands, SteadyWeapons, SquadmatesNeverEnemies, Harmonize, SmoothDecisions);
            SettingsMenu.AmountOf(bottomless, BottomlessAmmo, BottomlessReserve, 10);

            SettingsMenu.PackOf(packQuests, QuestDriveOn, QuestSpread, QuestAudit, QuestPointFix, QuestSimulation, QuestAnimations, QuestStepGiveUpMin);
            SettingsMenu.PackOf(packRoutes, Routes, RouteTeleportUnseen, RouteAreaBots, RouteRecorder, RouteRecordPoint, RouteRecordJump, RouteUndo, RouteSave, MapEdits, MapEditsAutoReload, MapEditsDump, MapEditsRedump, MapEditsOverlay, MapEditsReloadKey, MapEditsOverlayKey);

            Presets.Setup(cfg, new[] { packKeys, packMap, packLearn, packChars, packExfil, packSpawn, packLoot, packSquad, packFight, packHeal, packFixes, packQuests, packRoutes }, packHeal, packFixes);
            SettingsMenu.Finish(cfg);
            ExclusiveScavModes();
        }
    }
}
