#!/usr/bin/env bash
# Gathers everything needed to diagnose a failed launch into one zip:
# reloaded-dropin logs, Reloaded loader logs from the Proton prefix,
# generated configs, mod layout, and game-file state.
#
# This script ships in the drop-in package: run it from the game directory
# (./collect-diagnostics.sh) and dropin-diagnostics.zip appears next to it.
#
# Usage: ./collect-diagnostics.sh [game-dir] [steam-app-id]
set -uo pipefail

SELF_DIR="$(cd "$(dirname "$0")" && pwd)"
if [ -n "${1:-}" ]; then
  GAME_DIR="$1"
elif [ -f "$SELF_DIR/reloaded-dropin.asi" ]; then
  # Running from inside a drop-in game directory.
  GAME_DIR="$SELF_DIR"
else
  GAME_DIR="$HOME/.local/share/Steam/steamapps/common/Granblue Fantasy Relink"
fi
# game dir = <library>/steamapps/common/<game>; prefix lives at <library>/steamapps/compatdata/<appid>/pfx.
# The app id is derived from the library's appmanifest that owns this install dir.
STEAMAPPS="$(dirname "$(dirname "$GAME_DIR")")"
APPID="${2:-}"
if [ -z "$APPID" ]; then
  for acf in "$STEAMAPPS"/appmanifest_*.acf; do
    [ -f "$acf" ] || continue
    if grep -q "\"installdir\"[[:space:]]*\"$(basename "$GAME_DIR")\"" "$acf"; then
      APPID="$(basename "$acf" .acf)"; APPID="${APPID#appmanifest_}"
      break
    fi
  done
fi
[ -n "$APPID" ] || { APPID="881020"; echo "WARNING: could not derive app id from appmanifests; assuming $APPID"; }
echo "app id:    $APPID"
PREFIX="$STEAMAPPS/compatdata/$APPID/pfx"
RELOADED_APPDATA="$PREFIX/drive_c/users/steamuser/AppData/Roaming/Reloaded-Mod-Loader-II"
OUT="$(mktemp -d)/dropin-diagnostics"
mkdir -p "$OUT"

echo "game dir:  $GAME_DIR"
echo "prefix:    $PREFIX"

# Our logs (incl. rotated sync-prev*.log from earlier launches) + configs.
cp -r "$GAME_DIR/reloaded-dropin/logs" "$OUT/dropin-logs" 2>/dev/null
cp -r "$GAME_DIR/reloaded-dropin/generated" "$OUT/generated" 2>/dev/null
cp -r "$GAME_DIR/reloaded-dropin/state" "$OUT/state" 2>/dev/null
cp "$GAME_DIR/reloaded-dropin/overlay/overrides.json" "$OUT/" 2>/dev/null
cp "$GAME_DIR/reloaded-dropin/update.json" "$OUT/" 2>/dev/null

# Cross-launch bookkeeping: baseline hashes + mirror manifest (not the
# data.i.orig backup itself — too big).
cp "$GAME_DIR/reloaded-dropin/backups/gbfr/state.json" "$OUT/index-state.json" 2>/dev/null
cp "$GAME_DIR/reloaded-dropin/backups/gbfr/mirror-manifest.json" "$OUT/" 2>/dev/null
{
  echo "== sha256 =="
  sha256sum "$GAME_DIR/data.i" "$GAME_DIR/reloaded-dropin/backups/gbfr/data.i.orig" 2>/dev/null
  echo "== mtimes =="
  stat -c '%y %n' "$GAME_DIR/data.i" "$GAME_DIR"/mods/*/gbfrelink.utility.manager/temp/data.i \
      "$GAME_DIR"/mods/*/*/temp/data.i 2>/dev/null
  echo "== now =="
  date -u '+%Y-%m-%d %H:%M:%S UTC'
} > "$OUT/index-hashes.txt"

# Reloaded's own state: loader config + logs (the important part).
cp "$RELOADED_APPDATA/ReloadedII.json" "$OUT/" 2>/dev/null
cp -r "$RELOADED_APPDATA/Logs" "$OUT/reloaded-logs" 2>/dev/null
cp -r "$RELOADED_APPDATA/CrashDumps" "$OUT/reloaded-crash-dumps" 2>/dev/null

# A native game/Proton crash happens below Reloaded and leaves no exception in
# its log. PROTON_LOG=1 writes this file in $HOME; include it when present.
cp "$HOME/steam-$APPID.log" "$OUT/proton.log" 2>/dev/null

# Mod layout (structure only, no file contents).
find "$GAME_DIR/mods" -maxdepth 4 | sed "s|$GAME_DIR/||" > "$OUT/mods-tree.txt" 2>/dev/null
find "$GAME_DIR/mods" -maxdepth 4 -type f -name ModConfig.json -print0 2>/dev/null |
while IFS= read -r -d '' config; do
  echo "=== $config" >> "$OUT/mod-configs.txt"
  cat "$config" >> "$OUT/mod-configs.txt" 2>/dev/null
  echo >> "$OUT/mod-configs.txt"
done

if ! find "$GAME_DIR/mods" -mindepth 1 -maxdepth 1 \
    ! -name _base-mods ! -name PUT_MODS_HERE.txt -print -quit 2>/dev/null | grep -q .; then
  echo "WARNING: no user mod folders or archives were present in mods/ when diagnostics were collected." \
    >> "$OUT/WARNING.txt"
fi
if [ ! -f "$OUT/proton.log" ]; then
  echo "INFO: no Proton log found. For a native crash, launch once with PROTON_LOG=1 before collecting diagnostics." \
    >> "$OUT/WARNING.txt"
fi

# Game dir root state: proxy files present? data.i modified?
ls -la "$GAME_DIR" > "$OUT/game-root.txt" 2>/dev/null
stat "$GAME_DIR/data.i" >> "$OUT/game-root.txt" 2>/dev/null

# External-files folder the game reads modded content from — with mtimes,
# so we can see WHICH launch wrote each file.
find "$GAME_DIR/data" -type f -exec stat -c '%y %n' {} + 2>/dev/null \
  | sed "s|$GAME_DIR/||" > "$OUT/data-tree.txt"

# Utility manager working state (it may live under mods/ or mods/_base-mods/).
UM_DIR="$(find "$GAME_DIR/mods" -maxdepth 3 -type d -name 'gbfrelink.utility.manager' | head -1)"
if [ -n "$UM_DIR" ]; then
  ls -laR "$UM_DIR/temp" "$UM_DIR/GBFR" > "$OUT/utility-manager-state.txt" 2>/dev/null
  cp "$UM_DIR/cached_files.txt" "$OUT/" 2>/dev/null
fi

if [ ! -d "$RELOADED_APPDATA" ]; then
  echo "WARNING: $RELOADED_APPDATA not found — loader may never have written config/logs" | tee "$OUT/WARNING.txt"
fi

# The zip lands next to the game files — the same folder the user is already
# working in when something breaks.
ZIPFILE="$GAME_DIR/dropin-diagnostics.zip"
rm -f "$ZIPFILE"
if command -v zip > /dev/null; then
  (cd "$(dirname "$OUT")" && zip -qr "$ZIPFILE" "$(basename "$OUT")")
else
  (cd "$(dirname "$OUT")" && python3 -m zipfile -c "$ZIPFILE" "$(basename "$OUT")")
fi
echo
echo "Wrote $ZIPFILE — copy this file back."
