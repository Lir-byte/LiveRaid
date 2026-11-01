using EFT;
using SAIN.Components;

namespace LiveRaid.Client.Bots
{
    /// <summary>
    /// SAIN's component of a bot, only once SAIN has finished setting it up. A bot that is still spawning already
    /// carries the component, but its enemy controller is not there yet and HasEnemy / GoalEnemy throw.
    /// </summary>
    public static class SainBot
    {
        public static BotComponent Of(BotOwner bot)
        {
            if (bot == null || bot.IsDead || bot.BotState != EBotState.Active)
            {
                return null;
            }

            BotComponent sain = bot.GetComponent<BotComponent>();
            return sain != null && sain.EnemyController != null && sain.BotActivation != null ? sain : null;
        }
    }
}
