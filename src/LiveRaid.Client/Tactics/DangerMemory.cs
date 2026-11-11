using System.Collections.Generic;
using EFT;
using LiveRaid.Client.Bots;
using LiveRaid.Client.Raid;
using QuestingBots.Models.Questing;
using UnityEngine;

namespace LiveRaid.Client.Tactics
{
    /// <summary>
    /// Places where people have just died are remembered (F12 -> 4 "Remembers where people died", 1.4.0).
    ///
    /// A player who lost a mate at the dorms, or heard three people die there, does not stroll back to loot the dorms five minutes later - or he does exactly
    /// that, because a fight means bodies. The bots chose their next part of the map as if nothing had happened: raid 2026-11-10 12:37 (Reserve) - seventeen
    /// player scavs in a row walked into the same two squares where the lobby had died, and died there.
    ///
    /// Every death of a PMC, player scav or human in the last 8 minutes that a bot knows of - its squadmate's (counts double), or one within 250 m of where the
    /// bot is now (it heard the fight) - makes the part of the map round it (90 m) "hot" for that bot, fading with time. What the bot makes of it is its
    /// character, rolled once per bot:
    ///  - wary (rats, campers, looters, the shaken, most of the careful): quests there weigh less, down to x0.35;
    ///  - drawn (hunters, rushers, some of the bold): quests there weigh more, up to x1.6 - fresh bodies and somebody to fight;
    ///  - the rest hardly care.
    /// Only QuestingBots' weight of a quest: nobody is turned round on the way, and a bot with nothing else to do still goes there.
    /// </summary>
    public static class DangerMemory
    {
        private const float Remember = 480f;
        private const float Reach = 90f;
        private const float Heard = 250f;

        private static readonly Dictionary<string, float> _lean = new Dictionary<string, float>();
        private static readonly Dictionary<(string, int, int), (float at, float danger)> _cache = new Dictionary<(string, int, int), (float, float)>();

        public static int Asked { get; private set; }
        public static int Avoided { get; private set; }
        public static int Drawn { get; private set; }

        public static bool On => Settings.Enabled.Value && ((Settings.DangerMemory.Value && Settings.DangerStrength.Value > 0) || Caution.On);

        public static string Summary => !On
            ? "places of death remembered: off"
            : $"places of death remembered: quests weighed {Asked} (kept away from {Avoided}, drawn to {Drawn})";

        public static void Clear()
        {
            _lean.Clear();
            _cache.Clear();
            Asked = Avoided = Drawn = 0;
        }

        /// <summary>-1 (keeps well away) .. +1 (goes for it), rolled once per bot from its character.</summary>
        private static float Lean(BotOwner bot, BotProfile p)
        {
            if (_lean.TryGetValue(bot.ProfileId, out float lean))
            {
                return lean;
            }

            float wary, drawn;
            switch (p.Style)
            {
                case PlayStyle.Rat:
                case PlayStyle.Camper: wary = 0.75f; drawn = 0.05f; break;
                case PlayStyle.Looter: wary = 0.55f; drawn = 0.15f; break;
                case PlayStyle.Quester:
                case PlayStyle.Explorer: wary = 0.45f; drawn = 0.15f; break;
                case PlayStyle.Sniper: wary = 0.35f; drawn = 0.25f; break;
                case PlayStyle.Hunter: wary = 0.1f; drawn = 0.65f; break;
                default: wary = 0.15f; drawn = 0.5f; break;
            }

            // The bold lean toward it, the meek away
            float bold = Mathf.Clamp(p.Aggression - 1f, -0.5f, 0.5f);
            wary = Mathf.Clamp01(wary - 0.4f * bold);
            drawn = Mathf.Clamp01(drawn + 0.4f * bold);
            float roll = FreshSteps.Roll(bot.ProfileId, 311, 0);
            float how = 0.5f + 0.5f * FreshSteps.Roll(bot.ProfileId, 312, 0);
            lean = roll < wary ? -how : roll < wary + drawn ? how : (FreshSteps.Roll(bot.ProfileId, 313, 0) - 0.5f) * 0.3f;
            if (p.Packrat)
            {
                lean = -1f;
            }

            _lean[bot.ProfileId] = lean;
            return lean;
        }

        /// <summary>How hot the place is for this bot: 0 = nothing known, 1 = one death just now, up to 3.</summary>
        private static float Danger(BotOwner bot, Vector3 centre)
        {
            (string, int, int) key = (bot.ProfileId, Mathf.RoundToInt(centre.x / 20f), Mathf.RoundToInt(centre.z / 20f));
            float now = Time.time;
            if (_cache.TryGetValue(key, out var known) && now - known.at < 10f)
            {
                return known.danger;
            }

            float danger = 0f;
            Vector3 here = bot.Position;
            bool memory = Settings.DangerMemory.Value && Settings.DangerStrength.Value > 0;
            bool caution = Caution.On;
            foreach (KillEvent k in WorldEvents.Kills)
            {
                float age = now - k.Time;
                if (age > Remember || !(k.VictimPmc || k.VictimHuman || k.VictimKind == BotKind.PlayerScav) || k.VictimId == bot.ProfileId)
                {
                    continue;
                }

                // A death at the hands of the boss's pack or raiders (Caution): known from further off, and it weighs double
                bool pack = caution && k.KillerIsOther;
                if (!memory && !pack)
                {
                    continue;
                }

                float reach = pack ? Reach * 1.4f : Reach;
                float dx = k.Position.x - centre.x, dz = k.Position.z - centre.z;
                float d2 = dx * dx + dz * dz;
                if (d2 > reach * reach)
                {
                    continue;
                }

                bool mate = SquadTies.AreMates(bot.ProfileId, k.VictimId);
                float heard = pack ? Heard * 1.4f : Heard;
                if (!mate && (k.Position - here).sqrMagnitude > heard * heard)
                {
                    continue;
                }

                danger += (pack ? 2f : 1f) * (mate ? 2f : 1f) * (1f - age / Remember) * (1f - 0.5f * Mathf.Sqrt(d2) / reach);
            }

            if (caution)
            {
                // Where the boss and his guards spawned (a player knows it): the first seven minutes
                danger += Caution.BossDanger(centre, Reach * 1.3f);
            }

            danger = Mathf.Min(3f, danger);
            _cache[key] = (now, danger);
            return danger;
        }

        public static float Multiplier(BotOwner bot, BotProfile p, BotQuest quest)
        {
            if (!On || bot == null || p == null || p.Kind == BotKind.Other || p.Kind == BotKind.Scav)
            {
                return 1f;
            }

            string name = quest.GetName() ?? "";
            if (!QuestFactory.QuestPois.TryGetValue(name, out List<Poi> pois) || pois.Count == 0)
            {
                return 1f;
            }

            Vector3 centre = Vector3.zero;
            foreach (Poi poi in pois)
            {
                centre += poi.Pos;
            }

            centre /= pois.Count;
            float danger = Danger(bot, centre);
            if (danger < 0.15f)
            {
                return 1f;
            }

            float lean = Caution.Lean(bot, p, Lean(bot, p)) * (Settings.DangerMemory.Value ? Settings.DangerStrength.Value / 100f : 1f);
            // Hurt: nobody goes looking for trouble
            float hp = bot.HealthController.GetBodyPartHealth(EBodyPart.Common).Normalized;
            if (hp < 0.6f && lean > 0f)
            {
                lean = -0.3f;
            }

            Asked++;
            float m = lean < 0f ? 1f / (1f + 0.65f * -lean * danger) : 1f + 0.2f * lean * danger;
            m = Mathf.Clamp(m, 0.35f, 1.6f);
            if (m < 0.9f)
            {
                Avoided++;
            }
            else if (m > 1.1f)
            {
                Drawn++;
            }

            return m;
        }
    }
}
