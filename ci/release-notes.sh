#!/usr/bin/env bash
# Prints the CHANGELOG.md section for the given version; fails if there is none.
set -euo pipefail
V="${1:?version}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
notes=$(awk -v v="$V" '
  $0 ~ "^## \\[" v "\\]" {on=1; next}
  on && /^## \[/ {exit}
  on {print}' "$ROOT/CHANGELOG.md")
[ -n "$(echo "$notes" | tr -d '[:space:]')" ] || { echo "CHANGELOG.md has no section for [$V]" >&2; exit 1; }
printf '%s\n\n**Install:** BepInEx 5.4.23+ (x64), then unzip into the game folder. See the [README](../../blob/main/README.md).\n' "$notes"
