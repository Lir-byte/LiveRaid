using System.Linq;
// Checks outside the game that the built LiveRaid.Client.dll can register all its F12 settings (BepInEx ConfigFile.Bind).
// A bad setting name stops the whole plugin from loading (session 5: an apostrophe). Run: dotnet run --project tools/bindtest
// The SPT folder is C:\Games\SPT unless the environment variable LR_SPT_ROOT says otherwise.
using System;
using System.IO;
using System.Reflection;
class P {
  static int Main() {
    string root = Environment.GetEnvironmentVariable("LR_SPT_ROOT") ?? @"C:\Games\SPT";
    // LR_DLL: a built DLL to check instead of the one in the game (both editions: bin/Release-Single, bin/Release-Online)
    string dll = Environment.GetEnvironmentVariable("LR_DLL") ?? Path.Combine(root, "BepInEx", "plugins", "LiveRaid", "LiveRaid.Client.dll");
    string[] dirs = { Path.Combine(root, "BepInEx", "core"), Path.Combine(root, "EscapeFromTarkov_Data", "Managed"), Path.GetDirectoryName(Path.GetFullPath(dll)),
                      Path.Combine(root, "BepInEx", "plugins", "Fika"), Path.Combine(root, "BepInEx", "plugins", "spt"), Path.Combine(root, "BepInEx", "plugins") };
    AppDomain.CurrentDomain.AssemblyResolve += (s, e) => {
      string n = new AssemblyName(e.Name).Name + ".dll";
      foreach (var d in dirs) { var f = Path.Combine(d, n); if (File.Exists(f)) return Assembly.LoadFrom(f); }
      return null;
    };
    var bep = Assembly.LoadFrom(Path.Combine(dirs[0], "BepInEx.dll"));
    var lr = Assembly.LoadFrom(dll);
    var cfgType = bep.GetType("BepInEx.Configuration.ConfigFile");
    string path = Path.Combine(Path.GetTempPath(), "lr_bindtest.cfg");
    if (File.Exists(path)) File.Delete(path);
    // LR_CFG_SEED: start from a copy of a real config (checks of how saved values are read, e.g. both scav modes on)
    string seed = Environment.GetEnvironmentVariable("LR_CFG_SEED");
    if (!string.IsNullOrEmpty(seed) && File.Exists(seed)) File.Copy(seed, path, true);
    var cfg = Activator.CreateInstance(cfgType, path, false);
    var settings = lr.GetType("LiveRaid.Client.Settings");
    try {
      settings.GetMethod("Bind", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Invoke(null, new[] { cfg });
    } catch (TargetInvocationException e) { Console.WriteLine("BIND FAILED: " + e.InnerException); return 1; }
    int n = 0; foreach (var f in settings.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)) if (f.GetValue(null) != null) n++;
    Console.WriteLine("BIND OK: " + n + " settings bound");
    // The two scav modes exclude each other: report them, then switch roaming on and report again
    var sq = settings.GetField("ScavsQuest").GetValue(null); var sp = settings.GetField("ScavPatrol").GetValue(null);
    var vq = sq.GetType().GetProperty("Value"); var vp = sp.GetType().GetProperty("Value");
    Console.WriteLine($"SCAV MODES: roam {vq.GetValue(sq)}, patrol {vp.GetValue(sp)}");
    vq.SetValue(sq, true);
    Console.WriteLine($"SCAV MODES after roam on: roam {vq.GetValue(sq)}, patrol {vp.GetValue(sp)}");
    vp.SetValue(sp, true);
    Console.WriteLine($"SCAV MODES after patrol on: roam {vq.GetValue(sq)}, patrol {vp.GetValue(sp)}");
    // Squad doctrines (0.10+): the dealing rules checked on made-up raids - at most two squads per doctrine, no two squads alike
    var deck = lr.GetType("LiveRaid.Client.Tactics.DoctrineDeck");
    if (deck != null) {
      string result = (string)deck.GetMethod("SelfTest").Invoke(null, new object[] { 20000 });
      Console.WriteLine(result);
      if (!result.StartsWith("DOCTRINES OK")) return 2;
    }
    // Language (1.0): every bound setting has an English name and description, and the text follows the chosen language
    var loc = lr.GetType("LiveRaid.Client.Loc");
    if (loc != null) {
      var missing = ((System.Collections.Generic.IEnumerable<string>)loc.GetMethod("MissingEnglish").Invoke(null, null)).ToList();
      int count = (int)loc.GetProperty("Count").GetValue(null);
      Console.WriteLine($"LOC: {count} settings tracked, {missing.Count} without an English text" + (missing.Count > 0 ? ": " + string.Join(", ", missing.Take(12)) : ""));
      if (missing.Count > 0) return 4;
      var lang = settings.GetField("Language").GetValue(null); var vl = lang.GetType().GetProperty("Value");
      var langType = lr.GetType("LiveRaid.Client.LangChoice");
      var pre = settings.GetField("Preset").GetValue(null);
      var sample = settings.GetField("PopulationBalance").GetValue(null);
      var sampleM = loc.GetMethod("Sample");
      vl.SetValue(lang, Enum.Parse(langType, "English"));
      string en = (string)sampleM.Invoke(null, new object[] { sample, false });
      vl.SetValue(lang, Enum.Parse(langType, "Russian"));
      Console.WriteLine("LOC EN: " + en.Substring(0, Math.Min(90, en.Length)));
      Console.WriteLine("LOC RU: " + ((string)sampleM.Invoke(null, new object[] { sample, true })).Substring(0, 60));
      Console.WriteLine("PRESET now: " + pre.GetType().GetProperty("Value").GetValue(pre));
    }
    // Population plan (0.12+): numbers per map stay in range and move the right way
    var plan = lr.GetType("LiveRaid.Client.Raid.PopulationPlan");
    if (plan != null) {
      string result = (string)plan.GetMethod("SelfTest").Invoke(null, null);
      Console.WriteLine(result);
      if (!result.StartsWith("POPULATION OK")) return 3;
    }
    // Arena fighters (1.3.2): the carving of BigBrain's exclusions touches the arena role only
    var arena = lr.GetType("LiveRaid.Client.Bots.ArenaFightFix");
    if (arena != null) {
      string result = (string)arena.GetMethod("SelfTest").Invoke(null, null);
      Console.WriteLine(result);
      if (result.StartsWith("ARENA BAD")) return 5;
    }
    return 0;
  }
}
