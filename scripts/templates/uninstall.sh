#!/usr/bin/env bash
# Reloaded Drop-In uninstaller.
#
# Run from wherever this script was extracted (extras/ in current packages).
# With the game CLOSED: restores every game file the drop-in changed, then
# removes the drop-in's own files. Your mods/ folder is left in place -- delete
# it yourself if you no longer want it.
set -uo pipefail

# The game directory is the folder holding reloaded-dropin.asi. This script ships
# under extras/, so that is usually the parent; running it from the game root
# (older layout, or after copying it out) still works.
SELF_DIR="$(cd "$(dirname "$0")" && pwd)"
if [ -f "$SELF_DIR/reloaded-dropin.asi" ]; then
  GAME="$SELF_DIR"
else
  GAME="$(cd "$SELF_DIR/.." && pwd)"
fi
BACKUPS="$GAME/reloaded-dropin/backups/gbfr"
# Marker files moved into extras/ alongside this script; 0.6.1 and earlier wrote
# them to the game root, so both locations are read.
MARKER_DIR="$SELF_DIR"
[ -f "$SELF_DIR/.dropin-proxy-name" ] || MARKER_DIR="$GAME"

echo "Uninstalling Reloaded Drop-In from: $GAME"
[ -f "$GAME/reloaded-dropin.asi" ] || { echo "reloaded-dropin.asi not found here — run this from the game directory."; exit 1; }

# 1. Restore the pristine archive index.
if [ -f "$BACKUPS/data.i.orig" ]; then
  cp "$BACKUPS/data.i.orig" "$GAME/data.i"
  echo "restored original data.i"
else
  echo "no data.i backup found (nothing was applied, or already restored)"
fi

# 2. Remove mirrored mod files from data/ and restore displaced stock files.
if [ -f "$BACKUPS/mirror-manifest.json" ]; then
  python3 - "$GAME" "$BACKUPS" <<'PYEOF'
import json, os, shutil, sys

game, backups = sys.argv[1], sys.argv[2]
with open(os.path.join(backups, "mirror-manifest.json")) as f:
    manifest = json.load(f)

def canon(p):
    return p.replace("\\", "/")

removed = restored = 0
for rel in manifest.get("OwnedPaths", []):
    target = os.path.join(game, "data", canon(rel))
    if os.path.isfile(target):
        os.remove(target)
        removed += 1

for rel in manifest.get("DisplacedStockPaths", []):
    stock = os.path.join(backups, "displaced-stock", canon(rel))
    target = os.path.join(game, "data", canon(rel))
    if os.path.isfile(stock):
        os.makedirs(os.path.dirname(target), exist_ok=True)
        shutil.move(stock, target)
        restored += 1

# Prune directories the mirror created that are now empty.
for root, dirs, files in os.walk(os.path.join(game, "data"), topdown=False):
    if not dirs and not files:
        try:
            os.rmdir(root)
        except OSError:
            pass

print(f"removed {removed} mirrored file(s), restored {restored} stock file(s)")
PYEOF
else
  echo "no mirror manifest found (no files were mirrored)"
fi

# 3. Remove loader-generated additive archives (FFXVI: ff16.utility.modloader
#    rebuilds data/*.diff.pac each launch; originals are never modified, so
#    deleting them returns the game to vanilla). No-op for other games.
if [ -d "$GAME/data" ]; then
  count=0
  while IFS= read -r -d '' diffpac; do
    rm -f "$diffpac"
    count=$((count + 1))
  done < <(find "$GAME/data" -name '*.diff.pac' -type f -print0 2>/dev/null)
  [ "$count" -gt 0 ] && echo "removed $count generated .diff.pac archive(s)"
fi

# 4. Remove the drop-in's own files. The proxy DLL is only removed if it is
#    the one we shipped (Ultimate ASI Loader) — never someone else's file.
rm -f "$GAME/reloaded-dropin.asi"
rm -rf "$GAME/reloaded-dropin"
rm -f "$GAME/collect-diagnostics.sh" "$SELF_DIR/collect-diagnostics.sh" "$GAME/dropin-diagnostics.zip"
want="$(cat "$MARKER_DIR/.dropin-proxy-sha256" 2>/dev/null)"
shipped_proxy="$(cat "$MARKER_DIR/.dropin-proxy-name" 2>/dev/null)"
for proxy in winmm.dll dinput8.dll version.dll; do
  [ -f "$GAME/$proxy" ] || continue
  # New packages record the filename as well as the hash. This matters when a
  # game changes proxy names: all Ultimate ASI Loader copies have identical
  # bytes, so a hash alone cannot identify which one this package installed.
  if [ -n "$shipped_proxy" ] && [ "$proxy" != "$shipped_proxy" ]; then
    continue
  fi
  if [ -n "$want" ]; then
    have="$(sha256sum "$GAME/$proxy" | cut -d' ' -f1)"
    if [ "$want" = "$have" ]; then
      rm -f "$GAME/$proxy"
      echo "removed proxy $proxy"
    else
      echo "left $proxy in place (not the one this package shipped)"
    fi
  fi
done
rm -f "$GAME/.dropin-proxy-sha256" "$GAME/.dropin-proxy-name"

# extras/ holds this script and the licenses; last, so nothing else is read
# after the running script's own directory is gone. Also drops an uninstall.sh
# left at the game root by a 0.6.1 extract that 0.6.2 was upgraded over.
rm -f "$SELF_DIR/uninstall.sh" "$GAME/uninstall.sh"
rm -rf "$GAME/extras"

echo
echo "Done. Also remove the WINEDLLOVERRIDES launch option from the game's"
echo "Steam properties, and delete mods/ if you no longer want your mods."
