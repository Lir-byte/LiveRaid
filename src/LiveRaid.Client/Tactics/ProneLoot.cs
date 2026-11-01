using System.Collections.Generic;
using EFT;
using LiveRaid.Client.Bots;
using LootingBots.Components;
using UnityEngine;

namespace LiveRaid.Client.Tactics
{
    /// <summary>
    /// TEST FUNCTION (F12 -> 6 "Loot lying down"): at a BODY a bot lies down before going through it with a chance (50/50 by default, the rest
    /// crouch like every bot); containers and loose things are always looted crouching. The first version never worked in the game for four reasons:
    ///  1. EFT's prone needs room (MovementContext.CanProne: 1.7 m of level ground along the body, no steep slope, no water) and silently ignores
    ///     BotLay.IsLay = true when there is none - the bot stayed crouched while the code believed it lay. Now the room is checked first and a bot with
    ///     no room crouches (counted, so the next raid shows how often that is).
    ///  2. LootingBots starts the loot task the same frame, so the inventory opened while the bot was still going down. Now StartLooting is held back
    ///     (the loot logic asks again every 0.2 s) until the bot has lain for a second.
    ///  3. After a successful loot LootingBots never calls StopLooting (OnLootTaskEnd only), so nothing stood the bot up - it kept lying and crawled off.
    ///     Now OnLootTaskEnd, StopLooting and a guard in the worker all stand it up, and the bot is never left lying by this module.
    ///  4. BotLay.GetUp(true) does nothing before _nextPosibleGetUp (DELTA_GETUP after lying down), so "get up" right after a short loot was ignored.
    ///     Now the timer is cleared first.
    /// The draw is made once per body (re-drawn for the next body), not per character: pure chance.
    /// </summary>
    public static class ProneLoot
    {
        // The animation from crouching to lying takes about a second; the inventory opens only after it
        private const float SettleSeconds = 1.1f;
        // No room to lie down for this long: crouch like usual
        private const float NoRoomSeconds = 1.0f;
        // Stood up by something while looting this many times: it is not going to stay down, crouch
        private const int MaxStandUps = 2;

        private class Entry
        {
            public BotOwner Bot;
            public string LootId;
            public bool Prone;
            public bool Gave;
            public float RefusedSince;
            public float DownAt;
            public float LastAsked;
            public int StandUps;
            public bool Started;
        }

        private static readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>();
        private static readonly System.Random _rng = new System.Random();
        private static bool _forcing;
        private static float _nextTick;

        public static int Drawn { get; private set; }
        public static int DrawnProne { get; private set; }
        public static int WentDown { get; private set; }
        public static int NoRoom { get; private set; }
        public static int StoodUpBySomething { get; private set; }
        public static int GotUp { get; private set; }
        public static int Abandoned { get; private set; }

        public static string Summary =>
            $"lying at bodies (test): drawn {Drawn} (lie {DrawnProne}, crouch {Drawn - DrawnProne}), went down {WentDown}, no room to lie {NoRoom}, " +
            $"stood up by something while looting {StoodUpBySomething}, got up after {GotUp}, left lying and stood up by the guard {Abandoned}";

        public static void Clear()
        {
            _entries.Clear();
            _forcing = false;
            _nextTick = 0f;
            Drawn = DrawnProne = WentDown = NoRoom = StoodUpBySomething = GotUp = Abandoned = 0;
        }

        private static int Chance => Mathf.Clamp(Settings.LootProneChance.Value, 0, 100);

        /// <summary>
        /// LootingBots is about to start looting its target (the bot stands within reach of it). False = not yet: the bot is going down (asked again
        /// 0.2 s later by the loot logic). True = start now (crouching, or lying with the animation done).
        /// </summary>
        public static bool Gate(LootingBrain brain)
        {
            BotOwner bot = brain?.BotOwner;
            if (bot == null || !Settings.LootProne.Value || brain.ActiveLootType != LootFinder.LootType.Corpse || Spectator.TakeOver.IsTaken(bot))
            {
                return true;
            }

            BotProfile profile = BotProfiles.Get(bot);
            if (profile == null || profile.Kind == BotKind.Other)
            {
                return true;
            }

            string id = bot.ProfileId;
            float now = Time.time;
            if (!_entries.TryGetValue(id, out Entry e) || e.LootId != brain.ActiveLootId)
            {
                if (e != null)
                {
                    // A body drawn earlier was left without the loot task ever ending normally
                    StandUp(bot, "another body");
                }

                bool prone = _rng.Next(100) < Chance;
                _entries[id] = e = new Entry { Bot = bot, LootId = brain.ActiveLootId, Prone = prone };
                Drawn++;
                if (prone)
                {
                    DrawnProne++;
                }

                Log.Detail("loot", () => $"{bot.Profile?.Nickname} draws {(prone ? "lying down" : "crouching")} for {brain.ActiveLoot?.name} (lying chance {Chance} %)");
            }

            e.LastAsked = now;
            if (!e.Prone || e.Gave)
            {
                return true;
            }

            // Danger about: LootDanger refuses the start right after this - do not go down for nothing (and stand a bot up that is already down)
            if (LootDanger.Why(bot) != null)
            {
                return true;
            }

            MovementContext mc = bot.GetPlayer?.MovementContext;
            if (mc == null)
            {
                return true;
            }

            if (mc.IsInPronePose)
            {
                if (e.DownAt <= 0f)
                {
                    e.DownAt = now;
                    WentDown++;
                    Log.Info($"LR Loot: {bot.Profile?.Nickname} lies down to go through {brain.ActiveLoot?.name}");
                }

                if (now - e.DownAt < SettleSeconds)
                {
                    return false;
                }

                e.Started = true;
                return true;
            }

            if (!LayDown(bot))
            {
                if (e.RefusedSince <= 0f)
                {
                    e.RefusedSince = now;
                }

                if (now - e.RefusedSince >= NoRoomSeconds)
                {
                    e.Gave = true;
                    NoRoom++;
                    Log.Info($"LR Loot: {bot.Profile?.Nickname} cannot lie down by {brain.ActiveLoot?.name} (no level room for it, or its legs/meds forbid) - crouches");
                    return true;
                }
            }
            else
            {
                e.RefusedSince = 0f;
            }

            return false;
        }

        /// <summary>Tries to put the bot on the ground. True = it lies (EFT accepted it).</summary>
        public static bool LayDown(BotOwner bot)
        {
            MovementContext mc = bot?.GetPlayer?.MovementContext;
            if (mc == null)
            {
                return false;
            }

            if (mc.IsInPronePose)
            {
                return true;
            }

            // EFT ignores the order without room; asking anyway would also stack up a listener on every refused try
            if (!bot.BotLay.CanLayWithoutCheck() && !mc.CanProne)
            {
                return false;
            }

            bot.BotLay.IsLay = true;
            return mc.IsInPronePose;
        }

        /// <summary>Stands the bot up whatever the get-up timer and the "stay down while looting" guard say.</summary>
        public static void StandUp(BotOwner bot, string why)
        {
            if (bot == null)
            {
                return;
            }

            string id = bot.ProfileId;
            bool known = _entries.Remove(id);
            // A bot that has just died (raid 2026-10-31 00:34: LootingBots ended the loot task of a dying bot) has no BotLay any more, and "?." does
            // not see a destroyed Unity player
            Player player = bot.GetPlayer;
            if (bot.IsDead || player == null || bot.BotLay == null)
            {
                return;
            }

            MovementContext mc = player.MovementContext;
            if (mc == null || !mc.IsInPronePose)
            {
                return;
            }

            _forcing = true;
            try
            {
                bot.BotLay._nextPosibleGetUp = 0f;
                bot.BotLay.GetUp(false);
                if (mc.IsInPronePose)
                {
                    bot.BotLay.IsLay = false;
                }
            }
            finally
            {
                _forcing = false;
            }

            if (known)
            {
                GotUp++;
            }

            Log.Detail("loot", () => $"{bot.Profile?.Nickname} gets up ({why})");
        }

        public static void Forget(BotOwner bot)
        {
            if (bot != null)
            {
                _entries.Remove(bot.ProfileId);
            }
        }

        /// <summary>The bot lies over a body right now (the loot job about to start, or running).</summary>
        public static bool IsLying(BotOwner bot) =>
            bot != null && _entries.TryGetValue(bot.ProfileId, out Entry e) && e.Prone && !e.Gave && bot.GetPlayer?.MovementContext?.IsInPronePose == true;

        /// <summary>BotLay.GetUp is refused while the bot lies and goes through a body (otherwise anything stands it up in the middle of it).</summary>
        public static bool BlocksGetUp(BotOwner bot)
        {
            if (_forcing || bot == null || !_entries.TryGetValue(bot.ProfileId, out Entry e) || !e.Prone || e.Gave)
            {
                return false;
            }

            LootingBrain brain = bot.GetPlayer?.GetComponent<LootingBrain>();
            return brain != null && brain.LootTaskRunning;
        }

        /// <summary>The loot task ended (normally, cancelled or timed out): the bot gets up.</summary>
        public static void OnLootEnded(BotOwner bot)
        {
            if (bot != null && _entries.ContainsKey(bot.ProfileId))
            {
                StandUp(bot, "done with the body");
            }
        }

        /// <summary>
        /// Four times a second: a bot that was stood up by something while it lay over a body goes down again (twice at most); a bot lying with no
        /// loot task behind it (the body was taken away, fight, the setting switched off) is stood up.
        /// </summary>
        public static void Tick()
        {
            float now = Time.time;
            if (_entries.Count == 0 || now < _nextTick)
            {
                return;
            }

            _nextTick = now + 0.25f;
            List<KeyValuePair<string, Entry>> list = new List<KeyValuePair<string, Entry>>(_entries);
            foreach (KeyValuePair<string, Entry> pair in list)
            {
                Entry e = pair.Value;
                BotOwner bot = e.Bot;
                if (bot == null || bot.IsDead || bot.GetPlayer == null)
                {
                    _entries.Remove(pair.Key);
                    continue;
                }

                MovementContext mc = bot.GetPlayer.MovementContext;
                LootingBrain brain = bot.GetPlayer.GetComponent<LootingBrain>();
                bool looting = brain != null && brain.LootTaskRunning;

                if (!Settings.LootProne.Value || Spectator.TakeOver.IsTaken(bot))
                {
                    if (mc != null && mc.IsInPronePose)
                    {
                        Abandoned++;
                    }

                    StandUp(bot, "switched off");
                    continue;
                }

                if (!e.Prone || e.Gave)
                {
                    // Crouching draw: nothing to do; the entry is dropped when the body is left
                    if (!looting && now - e.LastAsked > 3f)
                    {
                        _entries.Remove(bot.ProfileId);
                    }

                    continue;
                }

                if (mc == null)
                {
                    continue;
                }

                if (looting && e.Started)
                {
                    if (!mc.IsInPronePose)
                    {
                        StoodUpBySomething++;
                        e.StandUps++;
                        if (e.StandUps > MaxStandUps)
                        {
                            e.Gave = true;
                            Log.Info($"LR Loot: {bot.Profile?.Nickname} does not stay down over the body (stood up {e.StandUps} times) - goes on crouching");
                        }
                        else
                        {
                            LayDown(bot);
                        }
                    }

                    continue;
                }

                // Lying (or on its way down) and no loot task running: it is waiting for the start (asked a moment ago) or has been left lying
                if (!looting && mc.IsInPronePose && now - e.LastAsked > 1.5f)
                {
                    Abandoned++;
                    StandUp(bot, "left lying without a loot task");
                }
                else if (!looting && !mc.IsInPronePose && now - e.LastAsked > 3f)
                {
                    _entries.Remove(bot.ProfileId);
                }
            }
        }
    }
}
