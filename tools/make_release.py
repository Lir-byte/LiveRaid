"""Builds the release archive of a LiveRaid version from the built files: ONE archive that holds both editions (single player and Online).

    python -I tools/make_release.py 0.8.0
    python -I tools/make_release.py 0.12.0 --channel test     (test branch: "<v>-test" DLLs built with -p:LiveRaidChannel=test)

Before: dotnet build src/LiveRaid.Server/LiveRaid.Server.csproj -c Release   (server stopped; deploys into the game)
        dotnet build src/LiveRaid.Client/LiveRaid.Client.csproj -c Release -p:Deploy=false
        dotnet build src/LiveRaid.Client/LiveRaid.Client.csproj -c Release -p:Edition=Online -p:Deploy=false
        dotnet build src/LiveRaid.Launcher/LiveRaid.Launcher.csproj -c Release -p:Deploy=false
Writes release/LiveRaid-<v>.zip and prints its SHA-256. (Until 1.2.4 there were two archives, LiveRaid-<v>.zip and LiveRaid-Online-<v>.zip.) Now one
archive with two equal folders: "Single" (SPT without Fika) and "Online" (Project Fika) - each a complete package to unpack into the game's root;
they differ only in the client DLL that works after unpacking, and both carry "LiveRaid Launcher.exe" with both editions for switching later.
The test channel writes release/LiveRaid-Test-<v>.zip: the test DLLs go to "LiveRaid Launcher/editions/Test/*" (client of both editions and the
server); the stable copies are not shipped.
Every DLL is checked for the version string (UTF-16, as .NET keeps it) - a stale DLL stops the script.
"""
import hashlib
import os
import sys
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
GAME = r"C:/Games/SPT"


def has_version(path, version):
    data = open(path, "rb").read()
    return version.encode("utf-16-le") in data


def main():
    args = sys.argv[1:]
    test = False
    if len(args) == 3 and args[1] == "--channel" and args[2] == "test":
        test = True
        args = args[:1]
    if len(args) != 1:
        print(__doc__)
        return 1

    v = args[0]
    mark = v + "-test" if test else v
    name = "LiveRaid-Test-" if test else "LiveRaid-"
    ed = "LiveRaid Launcher/editions/Test/" if test else "LiveRaid Launcher/editions/"
    server_dir = os.path.join(GAME, "SPT_Runtime", "user", "mods", "LiveRaid")
    server_dll = os.path.join(server_dir, "LiveRaid.Server.dll")
    clients = {
        "": os.path.join(ROOT, "src", "LiveRaid.Client", "bin", "Release-Single", "LiveRaid.Client.dll"),
        "Online-": os.path.join(ROOT, "src", "LiveRaid.Client", "bin", "Release-Online", "LiveRaid.Client.dll"),
    }
    launcher = os.path.join(ROOT, "src", "LiveRaid.Launcher", "bin", "Release", "net48", "LiveRaid Launcher.exe")
    if not os.path.exists(launcher):
        print(f"STOP: {launcher} is missing - build src/LiveRaid.Launcher first")
        return 1

    for path in [server_dll] + list(clients.values()):
        if not os.path.exists(path) or not has_version(path, mark):
            print(f"STOP: {path} is missing or not version {mark}")
            return 1

    online_dll = open(clients["Online-"], "rb").read()
    if b"Fika.Core" not in online_dll or b"Fika.Core" in open(clients[""], "rb").read():
        print("STOP: the Online build must reference Fika.Core and the single-player build must not")
        return 1

    data_files = []
    for base, _, files in os.walk(os.path.join(server_dir, "data")):
        for f in files:
            if f.endswith(".json"):
                full = os.path.join(base, f)
                data_files.append((full, os.path.relpath(full, server_dir).replace("\\", "/")))

    # The map edits of the author's game (Studio and the auto links written after raids) go into the project first, then into the archive
    try:
        sys.path.insert(0, os.path.join(ROOT, "tools"))
        import sync_map_edits
        sync_map_edits.sync(quiet=True)
    except Exception as e:
        print("note: map edits not synced from the game:", e)
    os.makedirs(os.path.join(ROOT, "release"), exist_ok=True)
    out = os.path.join(ROOT, "release", f"{name}{v}.zip")
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
        z.write(os.path.join(ROOT, "INSTALL.md"), "INSTALL.md")
        z.write(os.path.join(ROOT, "docs", "FIKA.md"), "FIKA.md")
        # Two equal folders, each a complete package to unpack into the game's root: "Single" (SPT without Fika) and "Online" (Project Fika).
        # They differ only in the client DLL that works after unpacking; both carry the launcher with both editions for switching later.
        for folder, client in (("Single", clients[""]), ("Online", clients["Online-"])):
            top = folder + "/"
            for extra, arc in (("LICENSE", "LICENSE.txt"), ("NOTICE.md", "NOTICE.md"), ("README.md", "README.md")):
                z.write(os.path.join(ROOT, extra), top + "BepInEx/plugins/LiveRaid/" + arc)
            # Map edits drawn in LiveRaid Studio and kept in the project (data/map_edits/<map>.json): the levers off the bots mesh, stairs, links
            edits_dir = os.path.join(ROOT, "data", "map_edits")
            if os.path.isdir(edits_dir):
                for ef in sorted(os.listdir(edits_dir)):
                    if ef.endswith(".json"):
                        z.write(os.path.join(edits_dir, ef), top + "BepInEx/plugins/LiveRaid/studio/edits/" + ef)

            z.write(client, top + "BepInEx/plugins/LiveRaid/LiveRaid.Client.dll")
            z.write(launcher, top + "LiveRaid Launcher.exe")
            z.write(clients[""], top + ed + "Single/LiveRaid.Client.dll")
            z.write(clients["Online-"], top + ed + "Online/LiveRaid.Client.dll")
            z.write(server_dll, top + "SPT_Runtime/user/mods/LiveRaid/LiveRaid.Server.dll")
            for full, rel in data_files:
                z.write(full, top + "SPT_Runtime/user/mods/LiveRaid/" + rel)
            # The launcher brings the branch's server back from here when it switches branches
            z.write(server_dll, top + ed + "Server/LiveRaid.Server.dll")
            for full, rel in data_files:
                z.write(full, top + ed + "Server/" + rel)

    digest = hashlib.sha256(open(out, "rb").read()).hexdigest()
    print(f"{os.path.basename(out)}  {os.path.getsize(out)} bytes  SHA-256 {digest}")

    return 0


if __name__ == "__main__":
    sys.exit(main())
