using System.Collections.Generic;
using DrakiaXYZ.BigBrain.Brains;
using EFT;
using LiveRaid.Client.Bots;
using SAIN.SAINComponent.Classes.EnemyClasses;
using UnityEngine;
using UnityEngine.AI;

namespace LiveRaid.Client.Tactics
{
    /// <summary>
    /// Out of a shooter's sight (F12 -> 9 "Return fire", the other half of it).
    /// Raid 2026-10-22 13:52: Jeo stood five minutes at (156, 1, -9) in plain view of a marksman Scav on a tower 112 m away - SAIN asked for cover again and
    /// again and found none it could reach ("nowhere to fall back to"), the marksman was too far for his gun, and Jeo bled from 99 % to 0 % where he stood
    /// (no heal: "an enemy in sight"). A player steps behind the nearest wall.
    /// A bot that has had an enemy in view for 4 s, 25+ m away, without firing for 6 s and without moving 1.5 m in that time, goes to the nearest point
    /// within 15 m that the enemy cannot see (a line from his head to the bot's chest is blocked) and that it can walk to; there it crouches and SAIN
    /// takes over again (heal, reload, a new peek). No such point - nothing for 15 s.
    /// </summary>
    public static class SightCover
    {
        private class State
        {
            public Vector3 StillAt;
            public float StillSince;
            public string EnemyId;
            public float SeenSince;
            public Vector3? Target;
            public float Until;
            public float NextTry;

            // Where it already stood in a shooter's view (SAIN tends to lead it back to the very same "cover")
            public readonly List<(Vector3 At, string Shooter, float Time)> Exposed = new List<(Vector3, string, float)>();
            public int Returns;
        }

        private static readonly Dictionary<string, State> _states = new Dictionary<string, State>();
        private static readonly Vector3[] _dirs = MakeDirs(16);

        public static int Hidden { get; private set; }
        public static int NoSpot { get; private set; }

        public static string Summary => $"stepped out of a shooter's sight {Hidden} (no spot found {NoSpot}), out-gunned at range - went round or left {CombatSense.OutrangedLeft}";

        public static void Clear()
        {
            _states.Clear();
            Hidden = NoSpot = 0;
        }

        private static Vector3[] MakeDirs(int n)
        {
            var d = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                d[i] = Quaternion.Euler(0f, i * 360f / n, 0f) * Vector3.forward;
            }

            return d;
        }

        public static Vector3? TargetOf(BotOwner bot) => bot != null && _states.TryGetValue(bot.ProfileId, out State s) ? s.Target : null;

        /// <summary>Asked by DisengageLayer: the bot is (or should now be) on its way out of sight.</summary>
        public static bool Active(BotOwner bot)
        {
            if (!Settings.ReturnFire.Value || bot == null || bot.IsDead)
            {
                return false;
            }

            if (!_states.TryGetValue(bot.ProfileId, out State s))
            {
                _states[bot.ProfileId] = s = new State { StillAt = bot.Position, StillSince = Time.time };
            }

            float now = Time.time;
            Vector3 pos = bot.Position;
            if (s.Target != null)
            {
                if (now > s.Until || (pos - s.Target.Value).sqrMagnitude < 1f || HitReaction.SinceHit(bot) < 0.5f && (pos - s.Target.Value).sqrMagnitude < 9f)
                {
                    s.Target = null;
                    s.NextTry = now + 12f;
                    bot.SetPose(0.4f);
                    return false;
                }

                return true;
            }

            if ((pos - s.StillAt).sqrMagnitude > 1.5f * 1.5f)
            {
                s.StillAt = pos;
                s.StillSince = now;
            }

            Enemy e = SainBot.Of(bot)?.GoalEnemy;
            if (e == null || !e.IsVisible)
            {
                s.EnemyId = null;
                return false;
            }

            if (s.EnemyId != e.EnemyProfileId)
            {
                s.EnemyId = e.EnemyProfileId;
                s.SeenSince = now;
            }

            // Back on a spot where it already stood in this shooter's view, or facing him beyond the reach of its gun: no waiting, and its own shots
            // (a shotgun at 90 m) are no answer
            s.Exposed.RemoveAll(x => now - x.Time > 90f);
            bool back = s.Exposed.Exists(x => x.Shooter == e.EnemyProfileId && (x.At - pos).sqrMagnitude < 16f);
            bool outranged = e.RealDistance > CombatSense.Reach(bot) && e.CanShoot;
            bool fast = back || outranged;
            if ((now < s.NextTry && !(back && now > s.NextTry - 12f)) || now - s.SeenSince < (fast ? 1.5f : 4f) || now - s.StillSince < (fast ? 2f : 6f)
                || e.RealDistance < 25f || (!outranged && WorldEvents.ShotBy(bot.ProfileId, 6f))
                || bot.Medecine?.Using == true || BotProfiles.Get(bot)?.Kind == BotKind.Other || Spectator.TakeOver.IsTaken(bot))
            {
                return false;
            }

            s.NextTry = now + 15f;
            if (back)
            {
                s.Returns++;
            }

            // Out-gunned and walked back into his view (or hit there): going round him or leaving beats another peek. In reach but twice back on the
            // same open spot without a shot: another spot altogether (SAIN's shift of cover)
            if (outranged && (back || HitReaction.SinceHit(bot) < 10f))
            {
                s.Returns = 0;
                if (Settings.GunRange.Value ? RangeSense.React(bot, e) : CombatSense.LeaveOutranged(bot, e))
                {
                    return false;
                }
            }
            else if (s.Returns >= 2)
            {
                s.Returns = 0;
                CombatSense.Nudge(bot, CombatStance.Reposition, $"back on the same open spot in {e.EnemyPlayer?.Profile?.Nickname}'s view - another spot");
            }

            s.Exposed.Add((pos, e.EnemyProfileId, now));
            Vector3? seen = e.EnemyPlayer != null ? e.EnemyPlayer.Position : e.LastKnownPosition;
            if (seen == null)
            {
                return false;
            }

            Vector3 eye = seen.Value + Vector3.up * 1.5f;
            Vector3? spot = FindSpot(pos, eye);
            if (spot == null)
            {
                NoSpot++;
                Log.Detail("combat", $"{bot.Profile.Nickname} stands in {e.EnemyPlayer?.Profile?.Nickname}'s sight ({e.RealDistance:0} m) without a shot - no spot out of sight within 15 m");
                return false;
            }

            s.Target = spot;
            s.Until = now + 8f;
            Hidden++;
            Log.Info($"LR Combat: {bot.Profile.Nickname} stood {now - s.StillSince:0} s in {e.EnemyPlayer?.Profile?.Nickname}'s sight ({e.RealDistance:0} m) without a shot - " +
                     $"steps out of it {Vector3.Distance(pos, spot.Value):0} m away");
            return true;
        }

        /// <summary>The nearest point (walking) within 15 m that the eye cannot see.</summary>
        private static Vector3? FindSpot(Vector3 pos, Vector3 eye)
        {
            Vector3? best = null;
            float bestLength = 25f;
            var path = new NavMeshPath();
            foreach (float r in new[] { 3f, 6f, 10f, 15f })
            {
                foreach (Vector3 d in _dirs)
                {
                    if (!NavMesh.SamplePosition(pos + d * r, out NavMeshHit hit, 1.5f, NavMesh.AllAreas) || Mathf.Abs(hit.position.y - pos.y) > 3f)
                    {
                        continue;
                    }

                    Vector3 p = hit.position;
                    if (!Physics.Linecast(eye, p + Vector3.up * 1.1f, LayersMaskController.HighPolyWithTerrainMask))
                    {
                        continue;
                    }

                    if (!NavMesh.CalculatePath(pos, p, NavMesh.AllAreas, path) || path.status != NavMeshPathStatus.PathComplete)
                    {
                        continue;
                    }

                    float length = 0f;
                    for (int i = 1; i < path.corners.Length; i++)
                    {
                        length += Vector3.Distance(path.corners[i - 1], path.corners[i]);
                    }

                    if (length < bestLength)
                    {
                        bestLength = length;
                        best = p;
                    }
                }

                // The nearest ring with a spot is good enough
                if (best != null)
                {
                    return best;
                }
            }

            return best;
        }
    }

    /// <summary>Runs to the spot out of sight, low and fast.</summary>
    public class SightCoverLogic : CustomLogic
    {
        private float _nextOrder;

        public SightCoverLogic(BotOwner botOwner) : base(botOwner)
        {
        }

        public override void Update(CustomLayer.ActionData data)
        {
            try
            {
                Vector3? target = SightCover.TargetOf(BotOwner);
                if (target == null)
                {
                    return;
                }

                BotOwner.SetPose(1f);
                BotOwner.SetTargetMoveSpeed(1f);
                BotOwner.Sprint(Vector3.Distance(BotOwner.Position, target.Value) > 4f);
                BotOwner.Steering.LookToMovingDirection();
                if (Time.time > _nextOrder)
                {
                    _nextOrder = Time.time + 1f;
                    BotOwner.GoToPoint(target.Value, false, -1f, false, false);
                }
            }
            catch (System.Exception e)
            {
                Diag.Report("SightCoverLogic", e);
            }
        }
    }
}
