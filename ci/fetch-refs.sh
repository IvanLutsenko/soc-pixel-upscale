#!/usr/bin/env bash
# Builds a fake "game folder" with everything scripts/build.sh needs, without the game:
#   <out>/SongsOfConquest_Data/Managed : Mono BCL + UnityEngine modules (NuGet) + Unity.InputSystem stub
#   <out>/BepInEx/core                 : BepInEx.dll + 0Harmony.dll (NuGet)
# Packages come from the BepInEx NuGet feed; highest version matching the prefix is used.
set -euo pipefail
OUT="${1:?usage: fetch-refs.sh <out-dir>}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
FEED="https://nuget.bepinex.dev/v3"
UNITY_PREFIX="${UNITY_PREFIX:-2022.3.}"   # game: Unity 2022.3.67
BEPINEX_PREFIX="${BEPINEX_PREFIX:-5.4.}"
M="$OUT/SongsOfConquest_Data/Managed"; C="$OUT/BepInEx/core"; TMP="$OUT/.pkg"
mkdir -p "$M" "$C" "$TMP"

base=$(curl -fsSL "$FEED/index.json" | python3 -c "import json,sys;print([r['@id'] for r in json.load(sys.stdin)['resources'] if r['@type'].startswith('PackageBaseAddress')][0])")

fetch() { # id prefix -> unpacked dir
  local id=$(echo "$1" | tr A-Z a-z) prefix="$2" ver
  ver=$(curl -fsSL "${base}${id}/index.json" | python3 -c "
import json,sys,re
p=sys.argv[1]; vs=[v for v in json.load(sys.stdin)['versions'] if v.startswith(p) and '-' not in v]
key=lambda v:[int(x) if x.isdigit() else 0 for x in re.split(r'[.]',v)]
print(sorted(vs,key=key)[-1])" "$prefix")
  echo "  $1 $ver" >&2
  curl -fsSL -o "$TMP/$id.nupkg" "${base}${id}/${ver}/${id}.${ver}.nupkg"
  rm -rf "$TMP/$id"; mkdir -p "$TMP/$id"; (cd "$TMP/$id" && unzip -q ../$id.nupkg)
  echo "$TMP/$id"
}

echo "NuGet packages:" >&2
U=$(fetch UnityEngine.Modules "$UNITY_PREFIX")
find "$U" -name 'UnityEngine*.dll' -exec cp {} "$M/" \;
B=$(fetch BepInEx.Core "$BEPINEX_PREFIX")
find "$B" -name 'BepInEx.dll' -exec cp {} "$C/" \;
H=$(fetch HarmonyX "2.")
HD=$(find "$H" -path '*net4*' -name 0Harmony.dll | sort | head -1)
[ -n "$HD" ] || HD=$(find "$H" -name 0Harmony.dll | sort | head -1)
cp "$HD" "$C/"

# Mono base class library (Unity's Mono profile is .NET 4.x compatible)
MONO=/usr/lib/mono/4.5
for d in mscorlib System System.Core; do cp "$MONO/$d.dll" "$M/"; done
cp "$MONO/Facades/netstandard.dll" "$M/"

# Input System: compile-time stub (the real DLL ships with the game, not on NuGet)
mcs -target:library -nostdlib -langversion:7 -out:"$M/Unity.InputSystem.dll" -r:"$M/mscorlib.dll" "$ROOT/ci/stubs/Unity.InputSystem.cs"

for f in mscorlib System System.Core netstandard UnityEngine UnityEngine.CoreModule UnityEngine.ImageConversionModule \
         UnityEngine.InputLegacyModule Unity.InputSystem UnityEngine.UIModule UnityEngine.AssetBundleModule \
         UnityEngine.IMGUIModule UnityEngine.TextRenderingModule; do test -f "$M/$f.dll" || { echo "missing $f.dll" >&2; exit 1; }; done
test -f "$C/BepInEx.dll" && test -f "$C/0Harmony.dll"
rm -rf "$TMP"
echo "References ready in $OUT" >&2
