"""Builds the two release archives of a LiveRaid version from the built files.

    python -I tools/make_release.py 0.8.0
    python -I tools/make_release.py 0.12.0 --channel test     (test branch: "<v>-test" DLLs built with -p:LiveRaidChannel=test)

Before: dotnet build src/LiveRaid.Server/LiveRaid.Server.csproj -c Release   (server stopped; deploys into the game)
        dotnet build src/LiveRaid.Client/LiveRaid.Client.csproj -c Release -p:Deploy=false
        dotnet build src/LiveRaid.Client/LiveRaid.Client.csproj -c Release -p:Edition=Online -p:Deploy=false
        dotnet build src/LiveRaid.Launcher/LiveRaid.Launcher.csproj -c Release -p:Deploy=false
Writes release/LiveRaid-<v>.zip (single player) and release/LiveRaid-Online-<v>.zip (Project Fika) and prints their SHA-256.
Both archives carry LiveRaid Launcher.exe and both editions in "LiveRaid Launcher/editions" (the launcher switches between them);
they differ only in the edition that is active in BepInEx/plugins/LiveRaid.
The test channel writes release/LiveRaid-Test-<v>.zip and release/LiveRaid-Test-Online-<v>.zip: the test DLLs go to
"LiveRaid Launcher/editions/Test/*" (client of both editions and the server); the stable copies are not shipped.
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

    os.makedirs(os.path.join(ROOT, "release"), exist_ok=True)
    for prefix, client in clients.items():
        out = os.path.join(ROOT, "release", f"{name}{prefix}{v}.zip")
        with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
            z.write(os.path.join(ROOT, "INSTALL.md"), "INSTALL.md")
            for extra, arc in (("LICENSE", "LICENSE.txt"), ("NOTICE.md", "NOTICE.md"), ("README.md", "README.md")):
                z.write(os.path.join(ROOT, extra), "BepInEx/plugins/LiveRaid/" + arc)
            if prefix:
                z.write(os.path.join(ROOT, "docs", "FIKA.md"), "FIKA.md")
            z.write(client, "BepInEx/plugins/LiveRaid/LiveRaid.Client.dll")
            z.write(launcher, "LiveRaid Launcher.exe")
            z.write(clients[""], ed + "Single/LiveRaid.Client.dll")
            z.write(clients["Online-"], ed + "Online/LiveRaid.Client.dll")
            z.write(server_dll, "SPT_Runtime/user/mods/LiveRaid/LiveRaid.Server.dll")
            for full, rel in data_files:
                z.write(full, "SPT_Runtime/user/mods/LiveRaid/" + rel)
            # The launcher brings the branch's server back from here when it switches branches
            z.write(server_dll, ed + "Server/LiveRaid.Server.dll")
            for full, rel in data_files:
                z.write(full, ed + "Server/" + rel)

        digest = hashlib.sha256(open(out, "rb").read()).hexdigest()
        print(f"{os.path.basename(out)}  {os.path.getsize(out)} bytes  SHA-256 {digest}")

    return 0


if __name__ == "__main__":
    sys.exit(main())
