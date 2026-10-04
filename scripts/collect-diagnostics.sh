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
# Which game is this? Mirrors the adapters' ExecutableNames; picks the
# game-specific probes below and the app id fallback.
GAME_ID="unknown"
if   [ -f "$GAME_DIR/granblue_fantasy_relink.exe" ]; then GAME_ID="gbfr"
elif [ -f "$GAME_DIR/P5R.exe" ]; then GAME_ID="p5r"
elif [ -f "$GAME_DIR/ffxvi.exe" ] || [ -f "$GAME_DIR/ffxvi_demo.exe" ]; then GAME_ID="ffxvi"
elif [ -f "$GAME_DIR/Digimon Story Time Stranger.exe" ]; then GAME_ID="dsts"
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
# No appmanifest: fall back to the detected game's id, never another game's --
# a wrong prefix means silently missing Proton logs.
if [ -z "$APPID" ]; then
  case "$GAME_ID" in
    gbfr)  APPID="881020" ;;
    p5r)   APPID="1687950" ;;
    ffxvi) APPID="2515020" ;;
    dsts)  APPID="1984270" ;;
  esac
  [ -n "$APPID" ] || echo "WARNING: could not derive app id; pass it as arg 2 to collect Proton logs"
fi
echo "game id:   $GAME_ID"
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
# ImGui's window-state file: its existence proves overlay frames actually ran.
cp "$GAME_DIR/imgui.ini" "$OUT/" 2>/dev/null

# Cross-launch bookkeeping: adapters keep state under backups/<adapter-id>/
# (only GBFR does today). The data.i.orig backup itself is not copied — too big.
for backup_dir in "$GAME_DIR"/reloaded-dropin/backups/*/; do
  [ -d "$backup_dir" ] || continue
  adapter_id="$(basename "$backup_dir")"
  for state_file in state.json mirror-manifest.json; do
    [ -f "$backup_dir/$state_file" ] && cp "$backup_dir/$state_file" "$OUT/${adapter_id}-${state_file}"
  done
done

# State of the game files the stack rewrites on disk.
{
  echo "== game: $GAME_ID"
  echo "== now: $(date -u '+%Y-%m-%d %H:%M:%S UTC')"
  if [ "$GAME_ID" = "gbfr" ]; then
    echo "== sha256 (live data.i vs pristine backup) =="
    sha256sum "$GAME_DIR/data.i" "$GAME_DIR/reloaded-dropin/backups/gbfr/data.i.orig" 2>/dev/null
    echo "== mtimes =="
    # The utility manager may sit directly in mods/ or nested one level deeper;
    # the patterns overlap, hence sort -u.
    stat -c '%y %n' "$GAME_DIR/data.i" \
        "$GAME_DIR"/mods/gbfrelink.utility.manager/temp/data.i \
        "$GAME_DIR"/mods/*/gbfrelink.utility.manager/temp/data.i \
        "$GAME_DIR"/mods/*/*/temp/data.i 2>/dev/null | sort -u
  else
    # Other games redirect at runtime and rewrite nothing, so there is no index
    # to hash. DSTS archive state lives in mvgl-tree.txt.
    echo "== no on-disk index state for $GAME_ID =="
  fi
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

# Media.Vision games (DSTS): MVGL.FileLoader recursively scans the whole game
# directory for *.mvgl, keyed by filename up to the first dot, case-insensitive,
# in a plain ToDictionary. Two archives sharing a stem abort the entire Reloaded
# load ("same key already added. Key: <stem>") and the loader log names the key
# but never the files. tolower() approximates .NET's OrdinalIgnoreCase.
MVGL_LIST="$(find "$GAME_DIR" -type f -iname '*.mvgl' -printf '%P\n' 2>/dev/null | sort -f)"
{
  echo "== scan root: $GAME_DIR"
  echo "== key = path basename up to the first dot, ASCII case-insensitive"
  echo "== count: $([ -n "$MVGL_LIST" ] && printf '%s\n' "$MVGL_LIST" | wc -l | tr -d ' ' || echo 0)"
  echo
  echo "== all .mvgl (path relative to scan root)"
  [ -n "$MVGL_LIST" ] && printf '%s\n' "$MVGL_LIST"
  echo
  # Keys are lowercased first fields, compared as literals (stems can hold regex
  # metacharacters). Each key's paths are buffered and flushed on key change:
  # sorting finished multi-line records would split a key from its own files.
  echo "== colliding keys (each of these is fatal to the Reloaded load)"
  MVGL_DUPS="$(printf '%s\n' "$MVGL_LIST" \
    | awk -F/ 'NF { b=$NF; k=tolower(b); d=index(k,"."); if (d>0) k=substr(k,1,d-1); print k "\t" $0 }' \
    | sort -f \
    | awk -F'\t' 'function flush() { if (n > 1) { printf "KEY: %s (%d files)\n", k, n; for (i = 1; i <= n; i++) print "    " buf[i] } n = 0 } { if ($1 != k) flush(); k = $1; buf[++n] = $2 } END { flush() }')"
  if [ -n "$MVGL_DUPS" ]; then printf '%s\n' "$MVGL_DUPS"; else echo "none"; fi
} > "$OUT/mvgl-tree.txt"

# Mod content the game reads at runtime, with mtimes so we can see WHICH launch
# wrote each file. Listings are capped: a legacy unpacked-data folder can hold
# tens of thousands of files.
list_with_mtimes() {
  local dir="$1" count
  count="$(find "$dir" -type f 2>/dev/null | wc -l | tr -d ' ')"
  echo "== ${dir#"$GAME_DIR"/} ($count files)"
  find "$dir" -type f -exec stat -c '%y %n' {} + 2>/dev/null | sed "s|$GAME_DIR/||" | head -n 200
  [ "$count" -gt 200 ] && echo "    ... truncated, $count total"
}
{
  echo "== game: $GAME_ID"
  # GBFR: data/ beside the exe. gamedata/: a legacy unpacker's output, not ours.
  for content_dir in "$GAME_DIR/data" "$GAME_DIR/gamedata"; do
    [ -d "$content_dir" ] && list_with_mtimes "$content_dir"
  done
  # MVGL FileLoader's MBE cache sits under its own mod folder; files there prove
  # MbeProcessor got past its constructor, i.e. the duplicate-key crash is gone.
  while IFS= read -r -d '' content_dir; do
    list_with_mtimes "$content_dir"
  done < <(find "$GAME_DIR/mods" -maxdepth 4 -type d -name cached -print0 2>/dev/null)
} > "$OUT/data-tree.txt"

# Utility manager working state (GBFR only; may live under mods/ or mods/_base-mods/).
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
