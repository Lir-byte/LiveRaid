using System;
using System.IO;
using System.Threading.Tasks;

namespace LiveRaid.Client.Bots
{
    /// <summary>
    /// Guns of the WTT-ContentBackport mod (the weapons of the 1.0 update) for the bots. The pools are filled by the server part of LiveRaid
    /// (data/config.json bots.modWeapons); this is the F12 switch for it - shown only when the content mod is installed on this machine
    /// (its client part in BepInEx/plugins), sent to the server when the game starts and whenever it changes.
    /// </summary>
    internal static class ContentGuns
    {
        private static bool? _installed;

        /// <summary>The content mod's client part is in BepInEx/plugins (a folder or a DLL named WTT-ContentBackport...).</summary>
        public static bool Installed()
        {
            if (_installed.HasValue)
            {
                return _installed.Value;
            }

            bool found = false;
            try
            {
                string plugins = BepInEx.Paths.PluginPath;
                foreach (string dir in Directory.GetDirectories(plugins))
                {
                    if (Path.GetFileName(dir).StartsWith("WTT-ContentBackport", StringComparison.OrdinalIgnoreCase))
                    {
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    found = Directory.GetFiles(plugins, "WTT-ContentBackport*.dll", SearchOption.AllDirectories).Length > 0;
                }
            }
            catch (Exception)
            {
                // cannot look: no setting shown
            }

            _installed = found;
            return found;
        }

        /// <summary>The clothes switch (F12) go to the server, which puts the outfits into the PMC bots' looks (or takes them out).</summary>
        public static void SendClothes()
        {
            if (Settings.ModClothesOn == null)
            {
                return;
            }

            string route = "/liveraid/modclothes/set/" + (Settings.ModClothesOn.Value ? "1" : "0");
            Task.Run(() =>
            {
                try
                {
                    SPT.Common.Http.RequestHandler.GetJson(route);
                }
                catch (Exception)
                {
                    // the server part is off or not reached - the config of the server decides
                }
            });
        }

        public static void SendState()
        {
            if (Settings.ModWeaponsOn == null)
            {
                return;
            }

            bool on = Settings.ModWeaponsOn.Value;
            Task.Run(() =>
            {
                try
                {
                    SPT.Common.Http.RequestHandler.GetJson("/liveraid/modweapons/set/" + (on ? "1" : "0"));
                }
                catch (Exception)
                {
                    // the server part is off or not reached - the config of the server decides
                }
            });
        }
    }
}
