using Comfort.Common;
using EFT;
using EFT.UI;
using UnityEngine;

namespace LiveRaid.Client.Spectator
{
    internal enum SpectatorState
    {
        Off,
        /// <summary>A couple of seconds on the body (yours or the taken bot's) before the camera comes loose.</summary>
        Dying,
        FreeCam,
        Follow,
        TakenOver,
        Ending,
    }

    /// <summary>
    /// "Free camera after death". EFT ends the raid from <c>BaseLocalGame.InitiateGameStopping</c> (death screen + Stop(Killed) in 5 s)
    /// as soon as your health controller reports death. LiveRaid skips that call and keeps the raid running: a free camera, a follow
    /// camera and taking over a bot (<see cref="TakeOver"/>). The raid result is fixed at that moment: every way the raid can end later
    /// (the End raid key, the taken bot's exit or death, the raid timer, leaving from the menu) goes through LocalGame.Stop, where
    /// <c>SpectatorStopPatch</c> turns it into the original death. The profile EFT sends to the server is the game's own profile
    /// (your dead character), so nothing a taken bot does reaches it: its kills go to the bot's statistics, its loot stays in its inventory.
    /// </summary>
    internal static class SpectatorSession
    {
        public static SpectatorState State { get; set; }

        /// <summary>Your own character died this raid: whatever happens next, the raid ends as that death.</summary>
        public static bool OriginalDead { get; private set; }

        public static Player Original { get; private set; }

        public static BaseLocalGame<EftGamePlayerOwner> Game { get; private set; }

        /// <summary>
        /// "Start raids as an observer": your character never enters the game - its body is hidden, cannot be hurt and no bot takes it
        /// for an enemy (<see cref="ObserverMode"/>); the free camera starts at once. Every end of such a raid becomes a run-through and
        /// the server mod puts back the profile as it was before the raid: no loot lost or gained, no experience, no statistics.
        /// </summary>
        public static bool Observer { get; private set; }

        /// <summary>Set while LiveRaid itself runs the vanilla death flow, so the death hook lets it through.</summary>
        internal static bool EndingOurselves;

        public static bool Active => State != SpectatorState.Off && State != SpectatorState.Ending;

        public static void Clear()
        {
            TakeOver.Clear();
            ObserverMode.Clear();
            CullingProxy.Clear();
            SpectatorCamera.Remove();
            State = SpectatorState.Off;
            OriginalDead = false;
            Observer = false;
            Original = null;
            Game = null;
            EndingOurselves = false;
        }

        /// <summary>
        /// EffectsController closes the eyes (DeathFade to black) and blurs the camera on the death event, which can come before
        /// LiveRaid's death hook. Skip it when the raid goes on after this death: yours with the free camera on, or the taken bot's.
        /// The end of the raid (EndingOurselves) keeps the vanilla look.
        /// </summary>
        public static bool SuppressDeathEffects()
        {
            if (EndingOurselves || State == SpectatorState.Ending)
            {
                return false;
            }

            if (TakeOver.Current != null)
            {
                return true;
            }

            if (OriginalDead)
            {
                return State == SpectatorState.Dying;
            }

            BaseLocalGame<EftGamePlayerOwner> game = Singleton<AbstractGame>.Instance as BaseLocalGame<EftGamePlayerOwner>;
            return Settings.SpectateAfterDeath.Value && !Plugin.DisabledFeatures.Contains("spectator")
                   && game != null && (game.Status == GameStatus.Started || game.Status == GameStatus.SoftStopping);
        }

        /// <summary>Called instead of the vanilla death flow. True: LiveRaid took over and the raid keeps running.</summary>
        public static bool OnOriginalDied(BaseLocalGame<EftGamePlayerOwner> game)
        {
            if (!Settings.SpectateAfterDeath.Value || EndingOurselves || OriginalDead || Plugin.DisabledFeatures.Contains("spectator"))
            {
                return false;
            }

            GameWorld world = Singleton<GameWorld>.Instance;
            Player me = game?.PlayerOwner?.Player;
            if (world == null || me == null || (game.Status != GameStatus.Started && game.Status != GameStatus.SoftStopping))
            {
                return false;
            }

            Game = game;
            Original = me;
            OriginalDead = true;
            State = SpectatorState.Dying;
            world.gameObject.GetOrAddComponent<SpectatorCamera>().BeginDying(Loc.T("Вы погибли", "You died"));
            Log.Info($"LR Spectator: {me.Profile.Nickname} died ({me.LastDamageType}), free camera instead of the raid end; the result stays this death");
            return true;
        }

        /// <summary>The raid starts in the free camera: no body of yours in the game. True when it began.</summary>
        public static bool BeginObserver(BaseLocalGame<EftGamePlayerOwner> game)
        {
            GameWorld world = Singleton<GameWorld>.Instance;
            Player me = game?.PlayerOwner?.Player;
            if (world == null || me == null || game.Status != GameStatus.Started || Observer || OriginalDead)
            {
                return false;
            }

            Game = game;
            Original = me;
            Observer = true;

            // Your controls off, as EFT does at death; the body stays (EFT needs it) but nobody can see, hurt or fight it
            try
            {
                game.PlayerOwner.vmethod_1();
            }
            catch (System.Exception e)
            {
                Log.Warn("LR Spectator: could not take the controls off your character: " + e.Message);
            }

            ObserverMode.Begin(me);
            State = SpectatorState.Dying;
            world.gameObject.GetOrAddComponent<SpectatorCamera>().BeginDying(Loc.T("Режим наблюдателя - рейд не засчитывается", "Observer mode - the raid is not counted"), 0.5f);
            Log.Info($"LR Spectator: observer raid on {world.LocationId} - {me.Profile.Nickname} stays out of it, free camera from the start; " +
                     "the raid will not count (profile kept as before)");
            return true;
        }

        /// <summary>The End raid key, the taken bot's exit, or its death when the raid should end then.</summary>
        public static void EndRaid(string why)
        {
            if (Game == null || State == SpectatorState.Ending)
            {
                return;
            }

            if (Observer)
            {
                Log.Info("LR Spectator: observer raid ends - " + why);
                OnRaidStopping();
                EndingOurselves = true;
                try
                {
                    // A run-through; the server mod keeps the profile as it was (see ObserverRaids on the server)
                    Game.Stop(Original.ProfileId, ExitStatus.Runner, ObserverMode.ExitTag, 0f);
                }
                finally
                {
                    EndingOurselves = false;
                }

                return;
            }

            Log.Info("LR Spectator: raid ends as the original death - " + why);
            OnRaidStopping();
            EndingOurselves = true;
            try
            {
                // Vanilla: death panel, then Stop(profile, Killed) with the usual 5 s - the summary is your death
                Game.InitiateGameStopping();
            }
            finally
            {
                EndingOurselves = false;
            }
        }

        /// <summary>The raid is stopping (any reason): hand the controls back and drop the camera so the end screens work as usual.</summary>
        public static void OnRaidStopping()
        {
            if (State == SpectatorState.Ending)
            {
                return;
            }

            State = SpectatorState.Ending;
            Diag.Guard("Spectator.Release", () => TakeOver.Release(raidEnding: true));
            SpectatorCamera.Remove();
            Cursor.lockState = CursorLockMode.None;
        }
    }
}
