using System.Text.Json;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Tables;
using Path = System.IO.Path;

namespace LiveRaid.Server.Core;

/// <summary>
/// Clothing of content mods on the PMC bots (data/config.json bots.modClothes, F12 on the client). WTT-ContentBackport registers its outfits (top, bottom,
/// hands) as customisation items and trader offers, but nothing puts them into the bots' appearance pools, so no PMC bot ever wore one. Here every outfit
/// the mod lists in db/CustomClothing joins the pools of the roles its "side" allows - tops into Body, bottoms into Feet - with the weight every look of the pool
/// has (SPT's are all 1: all looks are equally likely, the mod's and SPT's alike, no setting);
/// a top that has its own hands gets them (SPT picks random hands from the pool for a body that has none registered, and sleeves would not match).
/// Undone in reverse when the player switches it off.
/// </summary>
public static class ModClothes
{
    private sealed class Piece
    {
        public MongoId Id;
        public bool Top;
        public MongoId? Hands;
        public bool Usec;
        public bool Bear;
    }

    private static readonly List<Action> _undo = new();
    private static readonly object Gate = new();
    private static TemplateTable _templates;
    private static GlobalTable _globals;
    private static BotTable _botTable;
    private static ModClothesConfig _cfg;
    private static string _modsDir;
    private static bool _applied;

    public static int Tops { get; private set; }
    public static int Bottoms { get; private set; }
    public static bool Applied => _applied;

    public static void Init(TemplateTable templates, GlobalTable globals, BotTable botTable, ModClothesConfig cfg, string modsDir)
    {
        _templates = templates;
        _globals = globals;
        _botTable = botTable;
        _cfg = cfg;
        _modsDir = modsDir;
    }

    public static bool ContentModFound => _cfg != null && FindFolders().Count > 0;

    /// <summary>The client's F12 switch: on puts the clothing into the PMC bots' looks, off takes it out again.</summary>
    public static string SetState(bool on)
    {
        lock (Gate)
        {
            try
            {
                if (_applied && !on)
                {
                    Remove();
                }

                if (on && !_applied && _cfg != null)
                {
                    Apply();
                }
            }
            catch (Exception e)
            {
                LrState.Error("mod clothes switch failed", e);
            }

            return Status();
        }
    }

    public static string Status() =>
        "{\"applied\":" + (_applied ? "true" : "false") + ",\"found\":" + (ContentModFound ? "true" : "false") + ",\"tops\":" + Tops + ",\"bottoms\":" + Bottoms + "}";

    public static void Apply()
    {
        lock (Gate)
        {
            if (_applied || _cfg == null)
            {
                return;
            }

            ApplyInner();
        }
    }

    private static void Remove()
    {
        for (int i = _undo.Count - 1; i >= 0; i--)
        {
            try
            {
                _undo[i]();
            }
            catch (Exception e)
            {
                LrState.Warn("mod clothes: could not take one entry out: " + e.Message);
            }
        }

        _undo.Clear();
        _applied = false;
        LrState.Info("mod clothes: the content mod's clothing is out of the PMC bots' looks again");
    }

    private static List<string> FindFolders()
    {
        List<string> found = new();
        if (!Directory.Exists(_modsDir))
        {
            return found;
        }

        foreach (string dir in Directory.GetDirectories(_modsDir))
        {
            string name = Path.GetFileName(dir);
            if (name.Equals("LiveRaid", StringComparison.OrdinalIgnoreCase) || !Directory.Exists(Path.Combine(dir, "db", "CustomClothing")))
            {
                continue;
            }

            if (_cfg.ModFolders.Count == 0 || _cfg.ModFolders.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                found.Add(dir);
            }
        }

        return found;
    }

    private static void ApplyInner()
    {
        List<string> folders = FindFolders();
        if (folders.Count == 0)
        {
            LrState.Info("mod clothes: no content mod found (looked for " + (_cfg.ModFolders.Count > 0 ? string.Join(", ", _cfg.ModFolders) : "any WTT-style mod") + " in " + _modsDir + ")");
            return;
        }

        Dictionary<MongoId, Piece> pieces = new();
        HashSet<string> excluded = _cfg.Exclude.ToHashSet(StringComparer.OrdinalIgnoreCase);
        JsonDocumentOptions options = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };
        foreach (string folder in folders)
        {
            foreach (string file in Directory.EnumerateFiles(Path.Combine(folder, "db", "CustomClothing"), "*.json*", SearchOption.AllDirectories))
            {
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file), options);
                    IEnumerable<JsonElement> entries = doc.RootElement.ValueKind == JsonValueKind.Array
                        ? doc.RootElement.EnumerateArray().ToList()
                        : doc.RootElement.EnumerateObject().Select(p => p.Value).ToList();
                    foreach (JsonElement e in entries)
                    {
                        if (e.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }

                        string type = e.TryGetProperty("type", out JsonElement t) && t.ValueKind == JsonValueKind.String ? t.GetString() : "";
                        bool top = type == "top";
                        string key = top ? "topId" : "bottomId";
                        if (type != "top" && type != "bottom" || Id(e, key) is not MongoId id || excluded.Contains(id.ToString()))
                        {
                            continue;
                        }

                        bool usec = true;
                        bool bear = true;
                        if (e.TryGetProperty("side", out JsonElement s) && s.ValueKind == JsonValueKind.Array && s.GetArrayLength() > 0)
                        {
                            List<string> sides = s.EnumerateArray().Select(x => x.GetString() ?? "").ToList();
                            usec = sides.Contains("Usec", StringComparer.OrdinalIgnoreCase);
                            bear = sides.Contains("Bear", StringComparer.OrdinalIgnoreCase);
                        }

                        pieces[id] = new Piece { Id = id, Top = top, Hands = top ? Id(e, "handsId") : null, Usec = usec, Bear = bear };
                    }
                }
                catch (Exception ex)
                {
                    LrState.Warn($"mod clothes: could not read {file}: {ex.Message}");
                }
            }
        }

        // Only what the item database really has (a piece the mod failed to register would crash the bot's look)
        List<Piece> usable = pieces.Values.Where(p => _templates.Customization.ContainsKey(p.Id)).ToList();
        if (usable.Count == 0)
        {
            LrState.Info($"mod clothes: {pieces.Count} pieces listed, none of them in the item database");
            return;
        }

        List<string> done = new();
        foreach (string role in _cfg.PmcRoles)
        {
            if (!_botTable.Types.TryGetValue(role, out BotType type) || type?.BotAppearance == null)
            {
                continue;
            }

            bool isBear = role.Equals("bear", StringComparison.OrdinalIgnoreCase);
            int added = 0;

            // Every new piece gets the weight the pool's own looks have (SPT's are all 1), so all of them - the mod's and SPT's - are picked with the same chance
            foreach (bool tops in new[] { true, false })
            {
                Dictionary<MongoId, double> pool = tops ? type.BotAppearance.Body : type.BotAppearance.Feet;
                if (pool == null || pool.Count == 0)
                {
                    continue;
                }

                double each = pool.Values.GroupBy(v => v).OrderByDescending(g => g.Count()).First().Key;
                foreach (Piece piece in usable.Where(p => p.Top == tops && (isBear ? p.Bear : p.Usec) && !pool.ContainsKey(p.Id)))
                {
                    MongoId id = piece.Id;
                    pool[id] = each;
                    _undo.Add(() => pool.Remove(id));
                    added++;
                }
            }

            done.Add($"{role} {added}");
        }

        // The hands that belong to a top: SPT takes them from the registered body, otherwise random ones from the pool
        int hands = 0;
        Dictionary<string, WildBody> bodies = _globals.Configuration?.Customization?.Body;
        if (bodies != null)
        {
            foreach (Piece piece in usable.Where(p => p.Top && p.Hands.HasValue))
            {
                string name = _templates.Customization[piece.Id].Name?.Trim();
                if (string.IsNullOrEmpty(name) || bodies.ContainsKey(name))
                {
                    continue;
                }

                bodies[name] = new WildBody { Body = piece.Id, Hands = piece.Hands.Value, IsNotRandom = true };
                string made = name;
                _undo.Add(() => bodies.Remove(made));
                hands++;
            }
        }

        Tops = usable.Count(p => p.Top);
        Bottoms = usable.Count(p => !p.Top);
        _applied = true;
        LrState.Info($"mod clothes: {Tops} tops and {Bottoms} bottoms from {string.Join(", ", folders.Select(Path.GetFileName))} into the PMC bots' looks ({string.Join(", ", done)} added), " +
                     $"{hands} tops with their own hands; every look of a pool, the mod's and SPT's, has the same chance");
    }

    private static MongoId? Id(JsonElement e, string key) =>
        e.TryGetProperty(key, out JsonElement v) && v.ValueKind == JsonValueKind.String && v.GetString()?.Length == 24 ? new MongoId(v.GetString()) : null;
}
