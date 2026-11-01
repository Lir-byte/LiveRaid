#!/bin/bash
# Builds a reference "SPT root" for compiling LiveRaid WITHOUT the game (cloud sessions, CI).
#   tools/cloudbuild/setup.sh <_ref folder> <output root>
# _ref = the sources of SAIN / QuestingBots / LootingBots / BigBrain / SptServer of exactly the installed versions (from the user's project zip;
# not in git: too big, and it holds hollowed game assemblies). Needs: .NET 10 SDK (apt: dotnet-sdk-10.0), github.com and api.nuget.org.
# Then: dotnet build src/LiveRaid.Client/LiveRaid.Client.csproj -c Release -p:SptRoot=<output root>   (same for the server project)
# A copy of global.json with "rollForward": "latestMajor" may be needed when the SDK feature band is older than the pinned one.
set -e
REF=$(realpath "$1"); R=$(realpath -m "$2"); HERE=$(dirname "$(realpath "$0")"); W=$R/.work
mkdir -p "$W" "$R/BepInEx/core" "$R/BepInEx/plugins/spt" "$R/BepInEx/plugins/QuestingBots" "$R/BepInEx/plugins/SAIN" "$R/BepInEx/plugins/Skwizzy.LootingBots" "$R/EscapeFromTarkov_Data/Managed" "$R/SPT_Runtime"
M=$R/EscapeFromTarkov_Data/Managed
cd "$W"
[ -f bepinex.zip ] || curl -sSL -o bepinex.zip https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.2/BepInEx_win_x64_5.4.23.2.zip
[ -f unity.nupkg ] || curl -sSL -o unity.nupkg https://api.nuget.org/v3-flatcontainer/unityengine.modules/2021.3.33/unityengine.modules.2021.3.33.nupkg
unzip -q -o bepinex.zip -d bep && unzip -q -o unity.nupkg -d unity
cp bep/BepInEx/core/{0Harmony,BepInEx}.dll "$R/BepInEx/core/"
cp unity/lib/netstandard2.0/*.dll "$M/"
cp "$REF"/SAIN/References/{Comfort,Comfort.Unity,CommonExtensions,DissonanceVoip,ItemComponent.Types,Newtonsoft.Json,Sirenix.Serialization,Unity.Postprocessing.Runtime,Unity.TextMeshPro,bsg.console.core,DOTween,AnimationSystem.Types,websocket-sharp,com.unity.multiplayer-hlapi.Runtime}.dll "$M/"
cp "$REF/SAIN/References/hollowed.dll" "$M/Assembly-CSharp.dll"
cp "$REF"/SAIN/References/spt-*.dll "$R/BepInEx/plugins/spt/"
CECIL=$W/bep/BepInEx/core/Mono.Cecil.dll
dotnet run --project "$HERE/unitypatch" --property:CecilDll="$CECIL" -- "$W/unity/lib/netstandard2.0/UnityEngine.PhysicsModule.dll" "$M/UnityEngine.PhysicsModule.dll"
rm -rf "$W/deps" && cp -r "$HERE/deps" "$W/deps" && cd "$W/deps"
P="-p:R=$R -p:REF=$REF -c Release"
dotnet build BigBrain $P -o out/BigBrain >/dev/null && cp out/BigBrain/DrakiaXYZ-BigBrain.dll "$R/BepInEx/plugins/"
cp "$REF/QuestingBots/Dependencies/Fika.Core.dll" "$R/BepInEx/plugins/"
dotnet build SAIN $P -o out/SAIN >/dev/null && cp out/SAIN/SAIN.dll out/SAIN/SAIN.Preset.Shared.dll "$R/BepInEx/plugins/SAIN/"
dotnet build LB $P -o out/LB >/dev/null && cp out/LB/Skwizzy.LootingBots.dll "$R/BepInEx/plugins/Skwizzy.LootingBots/"
dotnet build QB $P -o out/QB >/dev/null && cp out/QB/QuestingBots-Client.dll "$R/BepInEx/plugins/QuestingBots/"
# Server: SPT server libraries from source
cd "$REF/SptServer"
dotnet build Libraries/SPTarkov.Server.Core/SPTarkov.Server.Core.csproj -c Release >/dev/null
dotnet build Libraries/SPTarkov.Reflection/SPTarkov.Reflection.csproj -c Release >/dev/null
cp Libraries/SPTarkov.Server.Core/bin/Release/net10.0/{SPTarkov.Server.Core,SPTarkov.DI,SPTarkov.Common}.dll "$R/SPT_Runtime/"
cp Libraries/SPTarkov.Reflection/bin/Release/net10.0/SPTarkov.Reflection.dll "$R/SPT_Runtime/"
cp ~/.nuget/packages/semanticversioning/3.0.0/lib/netstandard2.0/SemanticVersioning.dll "$R/SPT_Runtime/"
cp ~/.nuget/packages/harmonyx/2.16.1/lib/netstandard2.0/0Harmony.dll "$R/SPT_Runtime/"
echo "reference root ready: $R"
