using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using LiveRaid.Server.Core;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Utils;

namespace LiveRaid.Server;

/// <summary>
/// LiveRaid Online: one set of F12 settings for the whole group, owned by the host - the player whose machine runs this SPT server.
/// The server keeps the host's settings (shared-settings.json next to this DLL, so they outlive a restart) and hands them to every client.
/// Who the host is: at every start the server writes a fresh random id into host.token in its own folder and tells it to anyone who asks.
/// Only a game installed in the same SPT folder can read that file - the host's game - so only it may change the settings; the others take
/// them and show their F12 locked. Single player never calls these routes.
/// /liveraid/settings/get - {"server":"&lt;id&gt;","rev":N,"by":"...","values":{"Section/Key":"value",...}}
/// /liveraid/settings/set - body {"token":"&lt;id&gt;","by":"...","values":{...}} (the whole set), answer {"ok":true,"rev":N}
/// </summary>
[Injectable(TypePriority = OnLoadOrder.Routers + 53)]
public class SettingsRouter(JsonUtil jsonUtil)
    : StaticRouter(
        jsonUtil,
        [
            new RouteAction<EmptyRequestData>(
                "/liveraid/settings/get",
                (url, info, sessionId, output, ct) => new ValueTask<string>(SharedSettings.Get())
            ),
            new RouteAction<SettingsSetRequest>(
                "/liveraid/settings/set",
                (url, info, sessionId, output, ct) => new ValueTask<string>(SharedSettings.Set(info))
            ),
        ]
    );

public record SettingsSetRequest : IRequestData
{
    [JsonPropertyName("token")]
    public string Token { get; set; }

    [JsonPropertyName("by")]
    public string By { get; set; }

    [JsonPropertyName("values")]
    public Dictionary<string, string> Values { get; set; }
}

public static class SharedSettings
{
    private sealed class Stored
    {
        [JsonPropertyName("rev")]
        public int Rev { get; set; }

        [JsonPropertyName("by")]
        public string By { get; set; } = "";

        [JsonPropertyName("values")]
        public Dictionary<string, string> Values { get; set; } = new();
    }

    private static readonly object Gate = new();
    private static readonly string Dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
    private static readonly string FilePath = Path.Combine(Dir, "shared-settings.json");
    private static string _token;
    private static Stored _stored;

    /// <summary>
    /// The host key of this server: made once and kept in host.token next to this DLL (an older server wrote a new one at every start - a player
    /// on another machine would have to copy it again each time). A game installed next to this server reads the file itself; a game on another
    /// machine (a dedicated or headless server) takes the file's text once into its LiveRaid cfg ("Host key").
    /// </summary>
    private static string Token()
    {
        if (_token != null)
        {
            return _token;
        }

        string path = Path.Combine(Dir, "host.token");
        try
        {
            string old = File.Exists(path) ? File.ReadAllText(path).Trim() : "";
            if (old.Length >= 16)
            {
                _token = old;
            }
        }
        catch (Exception e)
        {
            LrState.Warn("host.token could not be read - a new key will be made: " + e.Message);
        }

        bool made = _token == null;
        _token ??= Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        if (made)
        {
            try
            {
                File.WriteAllText(path, _token);
            }
            catch (Exception e)
            {
                LrState.Error("could not write host.token (LiveRaid Online: nobody will be able to change the shared settings)", e);
            }
        }

        LrState.Info($"LiveRaid Online: the host key is in {path} - a game in this SPT folder is the host; a game on another machine puts the key's text into its LiveRaid cfg (Host key) to be the host");
        return _token;
    }

    private static Stored Load()
    {
        if (_stored != null)
        {
            return _stored;
        }

        try
        {
            _stored = File.Exists(FilePath) ? JsonSerializer.Deserialize<Stored>(File.ReadAllText(FilePath)) : null;
        }
        catch (Exception e)
        {
            LrState.Error("shared-settings.json could not be read - starting from an empty set", e);
        }

        _stored ??= new Stored();
        _stored.Values ??= new();
        return _stored;
    }

    public static string Get()
    {
        lock (Gate)
        {
            Stored s = Load();
            return JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["server"] = Token(),
                ["rev"] = s.Rev,
                ["by"] = s.By,
                ["values"] = s.Values,
            });
        }
    }

    public static string Set(SettingsSetRequest request)
    {
        lock (Gate)
        {
            if (request?.Values == null || request.Token != Token())
            {
                LrState.Warn("LiveRaid Online: a player who is not the host tried to change the shared settings - refused");
                return "{\"ok\":false}";
            }

            Stored s = Load();
            bool same = s.Values.Count == request.Values.Count
                && request.Values.All(kv => s.Values.TryGetValue(kv.Key, out string v) && v == kv.Value);
            if (same)
            {
                return "{\"ok\":true,\"rev\":" + s.Rev + "}";
            }

            int changed = request.Values.Count(kv => !s.Values.TryGetValue(kv.Key, out string v) || v != kv.Value);
            s.Rev++;
            s.By = request.By ?? "";
            s.Values = new Dictionary<string, string>(request.Values);
            try
            {
                File.WriteAllText(FilePath, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception e)
            {
                LrState.Error("shared-settings.json could not be saved", e);
            }

            LrState.Info($"LiveRaid Online: the host's settings saved for the group (rev {s.Rev}, {changed} changed)");
            return "{\"ok\":true,\"rev\":" + s.Rev + "}";
        }
    }
}
