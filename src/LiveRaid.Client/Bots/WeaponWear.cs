using System;
using System.Globalization;
using System.Threading.Tasks;

namespace LiveRaid.Client.Bots
{
    /// <summary>
    /// The state of PMC guns (F12 "PMC gun condition like online" and its three numbers). The server rolls the durability of every gun when it generates a
    /// PMC, so the values go to it: at the start of the game and whenever one of them changes (like the packrat chance); they stand over bots.pmcWeaponCondition
    /// of the server's data/config.json.
    /// </summary>
    internal static class WeaponWear
    {
        public static void Send()
        {
            if (Settings.WeaponWearOn == null)
            {
                return;
            }

            string route = "/liveraid/weaponcondition/set/" + (Settings.WeaponWearOn.Value ? "1" : "0") + "/"
                           + Settings.WeaponNewChance.Value.ToString(CultureInfo.InvariantCulture) + "/"
                           + Settings.WeaponMaxAbove.Value.ToString(CultureInfo.InvariantCulture);
            for (int i = 0; i < 4; i++)
            {
                route += "/" + Settings.WeaponBandMin[i].Value.ToString(CultureInfo.InvariantCulture)
                         + "/" + Settings.WeaponBandMax[i].Value.ToString(CultureInfo.InvariantCulture)
                         + "/" + Settings.WeaponBandWeight[i].Value.ToString(CultureInfo.InvariantCulture);
            }

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
    }
}
