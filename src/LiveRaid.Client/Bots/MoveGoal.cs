using EFT;
using UnityEngine;

namespace LiveRaid.Client.Bots
{
    /// <summary>
    /// Where a bot is walking to. The game's BotMover.RealDestPoint is set by GoToPoint only; QuestingBots walks its bots by GoToByWay (a ready path), which
    /// leaves it at whatever the last GoToPoint was - usually a loot spot a few metres back. Everything that asked "is the goal far enough for a detour"
    /// by RealDestPoint got "no" for every questing bot: raids 2026-11-10 (1.4.1) - people's corridors 0 detours / 0 already on one / 0 no corridor,
    /// ways kept to cover: 1 path looked at in a 35-minute raid. The end of the path the mover is on is the goal; RealDestPoint only when there is no path.
    /// </summary>
    public static class MoveGoal
    {
        public static Vector3 Of(BotOwner bot)
        {
            BotMover mover = bot?.Mover;
            if (mover == null)
            {
                return bot != null ? bot.Position : Vector3.zero;
            }

            try
            {
                if (mover.HasPathAndNoComplete)
                {
                    Vector3[] way = QuestingBots.Helpers.BotPathingHelpers.GetCurrentPath(mover);
                    if (way != null && way.Length > 0)
                    {
                        return way[way.Length - 1];
                    }
                }
            }
            catch (System.Exception e)
            {
                Diag.Report("MoveGoal", e);
            }

            return mover.RealDestPoint;
        }
    }
}
