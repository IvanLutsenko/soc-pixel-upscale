#!/usr/bin/env bash
# Builds and packs the release zip: dist/SoCPixelUpscale_v<version>.zip
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
VER=$(grep -oP 'const string Version = "\K[^"]+' "$ROOT/src/SoCPixelUpscale.cs")
"$ROOT/scripts/build.sh"
STAGE=$(mktemp -d)
mkdir -p "$STAGE/BepInEx/plugins/SoCPixelUpscale" "$STAGE/source"
cp "$ROOT/dist/SoCPixelUpscale.dll" "$STAGE/BepInEx/plugins/SoCPixelUpscale/"
cp "$ROOT/src/SoCPixelUpscale.cs" "$STAGE/source/"
cp "$ROOT/README.md" "$ROOT/LICENSE" "$ROOT/CHANGELOG.md" "$STAGE/"
OUT="$ROOT/dist/SoCPixelUpscale_v$VER.zip"; rm -f "$OUT"
(cd "$STAGE" && python3 -c "import zipfile,os,sys
z=zipfile.ZipFile(sys.argv[1],'w',zipfile.ZIP_DEFLATED)
for r,_,fs in os.walk('.'):
    for f in fs: z.write(os.path.join(r,f),os.path.relpath(os.path.join(r,f),'.'))" "$OUT")
rm -rf "$STAGE"
echo "Packed $OUT"
