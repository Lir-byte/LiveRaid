using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Spt.Config;

namespace LiveRaid.Server.Core;

/// <summary>
/// Online every other squad is hostile, whatever its side. SPT ships same-side PMCs as only partly hostile
/// (pmc.json 85 %, map bases 75 %, Ground Zero 35-50 %, Halloween 90 %): a BEAR squad walked past another BEAR squad.
/// Here every PMC is always an enemy of every PMC and player that is not in its own group (squads stay squads:
/// EFT keeps members of one bot group allies before it ever looks at these chances).
/// </summary>
public static class PmcHostility
{
    private static readonly string[] PmcRoles = ["pmcBEAR", "pmcUSEC"];

    private static bool IsPmcRole(string role) => PmcRoles.Any(r => string.Equals(r, role, StringComparison.OrdinalIgnoreCase));

    /// <summary>SPT's pmc.json values, copied into the map at every raid start.</summary>
    public static int Force(HostilitySettings settings)
    {
        if (settings == null)
        {
            return 0;
        }

        int changed = 0;
        changed += Set100(settings.BearEnemyChance, v => settings.BearEnemyChance = v);
        changed += Set100(settings.UsecEnemyChance, v => settings.UsecEnemyChance = v);
        settings.ChancedEnemies ??= [];
        changed += ForceChanced(settings.ChancedEnemies);
        if (settings.AdditionalFriendlyTypes != null)
        {
            changed += settings.AdditionalFriendlyTypes.RemoveAll(IsPmcRole);
        }

        return changed;
    }

    /// <summary>One bot role's entry in a map's BotLocationModifier.AdditionalHostilitySettings.</summary>
    public static int Force(AdditionalHostilitySettings settings)
    {
        if (settings == null || !IsPmcRole(settings.BotRole))
        {
            return 0;
        }

        int changed = 0;
        changed += Set100(settings.BearEnemyChance, v => settings.BearEnemyChance = v);
        changed += Set100(settings.UsecEnemyChance, v => settings.UsecEnemyChance = v);

        // How the bot treats players (you) of each side: like online, always an enemy
        if (settings.BearPlayerBehaviour != "AlwaysEnemies")
        {
            settings.BearPlayerBehaviour = "AlwaysEnemies";
            changed++;
        }

        if (settings.UsecPlayerBehaviour != "AlwaysEnemies")
        {
            settings.UsecPlayerBehaviour = "AlwaysEnemies";
            changed++;
        }

        settings.ChancedEnemies ??= [];
        changed += ForceChanced(settings.ChancedEnemies);
        changed += RemovePmc(settings.AlwaysFriends);
        changed += RemovePmc(settings.Neutral);
        if (settings.Warn != null && settings.Warn.Any(IsPmcRole))
        {
            settings.Warn = settings.Warn.Where(r => !IsPmcRole(r)).ToList();
            changed++;
        }

        return changed;
    }

    public static int Force(LocationBase location)
    {
        int changed = 0;
        foreach (AdditionalHostilitySettings s in location?.BotLocationModifier?.AdditionalHostilitySettings ?? [])
        {
            changed += Force(s);
        }

        return changed;
    }

    /// <summary>What the map currently says, for the debug route and the log.</summary>
    public static string Describe(LocationBase location)
    {
        IEnumerable<string> parts = (location?.BotLocationModifier?.AdditionalHostilitySettings ?? [])
            .Where(s => IsPmcRole(s.BotRole))
            .Select(s => $"{s.BotRole}: vs BEAR {s.BearEnemyChance}% ({s.BearPlayerBehaviour}), vs USEC {s.UsecEnemyChance}% ({s.UsecPlayerBehaviour}), " +
                         $"chanced {string.Join("/", (s.ChancedEnemies ?? []).Where(e => IsPmcRole(e.Role)).Select(e => $"{e.Role} {e.EnemyChance}%"))}, " +
                         $"friends {(s.AlwaysFriends ?? []).Count(IsPmcRole)}, warn {(s.Warn ?? []).Count(IsPmcRole)}, neutral {(s.Neutral ?? []).Count(IsPmcRole)}");
        return string.Join("; ", parts);
    }

    private static int Set100(double? current, Action<double?> set)
    {
        if (current == 100)
        {
            return 0;
        }

        set(100);
        return 1;
    }

    private static int ForceChanced(List<ChancedEnemy> chanced)
    {
        int changed = 0;
        foreach (string role in PmcRoles)
        {
            ChancedEnemy entry = chanced.FirstOrDefault(e => string.Equals(e.Role, role, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                chanced.Add(new ChancedEnemy { Role = role, EnemyChance = 100 });
                changed++;
            }
            else if (entry.EnemyChance != 100)
            {
                entry.EnemyChance = 100;
                changed++;
            }
        }

        return changed;
    }

    private static int RemovePmc(HashSet<string> set) => set?.RemoveWhere(IsPmcRole) ?? 0;
}
