#!/usr/bin/env bash
# Compiles src/SoCPixelUpscale.cs -> dist/SoCPixelUpscale.dll
set -euo pipefail
: "${GAME_DIR:?Set GAME_DIR to the Songs of Conquest folder}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
M="$GAME_DIR/SongsOfConquest_Data/Managed"
B="${BEPINEX_CORE:-$GAME_DIR/BepInEx/core}"
refs=()
for d in mscorlib System System.Core netstandard UnityEngine UnityEngine.CoreModule UnityEngine.ImageConversionModule \
         UnityEngine.InputLegacyModule Unity.InputSystem UnityEngine.UI UnityEngine.UIModule UnityEngine.AssetBundleModule \
         UnityEngine.IMGUIModule UnityEngine.TextRenderingModule; do refs+=("-r:$M/$d.dll"); done
refs+=("-r:$B/BepInEx.dll" "-r:$B/0Harmony.dll")
mkdir -p "$ROOT/dist"
mcs -target:library -nostdlib -langversion:7 -optimize -out:"$ROOT/dist/SoCPixelUpscale.dll" "${refs[@]}" "$ROOT/src/SoCPixelUpscale.cs"
echo "Built $ROOT/dist/SoCPixelUpscale.dll"
