using System.Collections.Generic;
using Comfort.Common;
using EFT;
using UnityEngine;

namespace LiveRaid.Client.Bots
{
    /// <summary>
    /// EFT's squad requests ("go to point", "hold", "hide") have no sensible end for a follower that already stands at the point:
    /// two PMCs stood 2 minutes in the request layer (raid 2026-10-19 01:34, "PcReq:goToPoint", moved 0 m) until a fight woke them.
    /// A follower that has not moved a metre for 25 s on such a request while nothing happens around it is released from it,
    /// so QuestingBots and the squad logic take over again.
    /// </summary>
    public static class RequestWatch
    {
        private const float StandSeconds = 25f;

        private struct Watch
        {
            public BotRequest Request;
            public Vector3 Pos;
            public float Since;
        }

        private static readonly Dictionary<string, Watch> _watch = new Dictionary<string, Watch>();
        private static float _next;

        public static int Released { get; private set; }

        public static void Clear()
        {
            _watch.Clear();
            Released = 0;
        }

        public static void Tick()
        {
            if (Time.time < _next)
            {
                return;
            }

            _next = Time.time + 1f;
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null)
            {
                return;
            }

            foreach (Player player in world.AllAlivePlayersList)
            {
                BotOwner bot = player?.AIData?.BotOwner;
                BotRequest request = bot?.BotRequestController?.CurRequest;
                if (bot == null || bot.IsDead || request == null || Spectator.TakeOver.IsTaken(bot)
                    || (request.BotRequestType != BotRequestType.goToPoint && request.BotRequestType != BotRequestType.hold && request.BotRequestType != BotRequestType.hide))
                {
                    _watch.Remove(bot?.ProfileId ?? "");
                    continue;
                }

                Vector3 pos = bot.Position;
                if (!_watch.TryGetValue(bot.ProfileId, out Watch w) || w.Request != request || (pos - w.Pos).sqrMagnitude > 1f)
                {
                    _watch[bot.ProfileId] = new Watch { Request = request, Pos = pos, Since = Time.time };
                    continue;
                }

                if (Time.time - w.Since < StandSeconds || !Tactics.TacticalTasks.NoThreat(bot))
                {
                    continue;
                }

                _watch.Remove(bot.ProfileId);
                Released++;
                Log.Info($"LR Stuck: {bot.Profile.Nickname} stood {Time.time - w.Since:0} s on the squad request '{request.BotRequestType}' - released from it");
                bot.BotRequestController.CompleteRequest(request);
            }
        }
    }
}
