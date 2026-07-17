#!/usr/bin/env bash
# Assembles THE Reloaded Drop-In package — one universal zip for every
# supported game. The bootstrap detects which game it was extracted into at
# launch (all game adapters ship in every package), so nothing game-specific
# is decided at packaging time.
#
# Usage: ./scripts/package-release.sh
#
# Output layout (extract into the game directory):
#   <game proxy>.dll        Ultimate ASI Loader (override with PROXY_NAME)
#   reloaded-dropin.asi     our native bootstrap
#   mods/                   user mods (+ our overlay under _base-mods/; the
#                           third-party base mods are NOT redistributed — the
#                           first online launch downloads them from their
#                           authors' official releases)
#   reloaded-dropin/
#     runtime/              .NET Desktop Runtime (win-x64)
#     loader/               Reloaded-II mod loader
#     bootstrap/            our managed sync component
#     logs/
#
# Requires: dotnet (~/.dotnet ok), x86_64-w64-mingw32-gcc, 7zz, unzip, curl.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
export PATH="$HOME/.dotnet:$PATH"

# Single source of truth for the drop-in version: the VERSION file at repo
# root. It flows into version.json, which the overlay watermark reads.
DROPIN_VERSION="$(tr -d '[:space:]' < "$ROOT/VERSION")"
RELOADED_VERSION="1.30.2"
# All supported games deliberately share the winmm proxy and launch option;
# override (PROXY_NAME=dinput8.dll) only to build an experimental variant for
# a game whose exe turns out not to import winmm.
PROXY_NAME="${PROXY_NAME:-winmm.dll}"
VENDOR="$ROOT/vendor/reloaded-release"
STAGE="$ROOT/dist/dropin"
PACKAGE="$ROOT/dist/unloaded-ii-$DROPIN_VERSION.zip"
echo "==> packaging universal drop-in, proxy $PROXY_NAME"

echo "==> checking inputs"
[ -f "$VENDOR/extracted/Loader/X64/Reloaded.Mod.Loader.dll" ] || {
  echo "Reloaded release not extracted; run:"
  echo "  curl -sL -o $VENDOR/Release.zip https://github.com/Reloaded-Project/Reloaded-II/releases/download/$RELOADED_VERSION/Release.zip"
  echo "  unzip -q $VENDOR/Release.zip -d $VENDOR/extracted"
  exit 1
}
[ -f "$VENDOR/extracted/LICENSE.txt" ] || {
  echo "missing Reloaded-II upstream license bundle: $VENDOR/extracted/LICENSE.txt"
  exit 1
}
[ -f "$VENDOR/windowsdesktop-runtime-win-x64.zip" ] || {
  echo "missing $VENDOR/windowsdesktop-runtime-win-x64.zip (get from https://aka.ms/dotnet/9.0/windowsdesktop-runtime-win-x64.zip)"
  exit 1
}
[ -f "$VENDOR/dotnet-runtime-win-x64.zip" ] || {
  echo "missing $VENDOR/dotnet-runtime-win-x64.zip (get from https://aka.ms/dotnet/9.0/dotnet-runtime-win-x64.zip)"
  exit 1
}
echo "==> building native .asi"
"$ROOT/src/native/dropin-asi/build.sh" "$ROOT/dist/native"

echo "==> publishing managed bootstrap"
dotnet publish "$ROOT/src/ReloadedDropIn.Bootstrap" -c Release -o "$ROOT/dist/bootstrap-publish" \
  --no-restore --disable-build-servers -p:UseSharedCompilation=false -p:NuGetAudit=false \
  -p:WarningsNotAsErrors=NU1900 -m:1 -v quiet

echo "==> publishing overlay mod"
dotnet publish "$ROOT/src/ReloadedDropIn.Overlay" -c Release -o "$ROOT/dist/overlay-publish" \
  --no-restore --disable-build-servers -p:UseSharedCompilation=false -p:NuGetAudit=false \
  -p:WarningsNotAsErrors=NU1900 -m:1 -v quiet

echo "==> publishing FFXVI Faith overlay bridge"
dotnet publish "$ROOT/src/ReloadedDropIn.Overlay.Faith" -c Release -o "$ROOT/dist/faith-overlay-publish" \
  --no-restore --disable-build-servers -p:UseSharedCompilation=false -p:NuGetAudit=false \
  -p:WarningsNotAsErrors=NU1900 -m:1 -v quiet

echo "==> staging package"
rm -rf "$STAGE"
mkdir -p "$STAGE/reloaded-dropin"/{loader,bootstrap,logs} "$STAGE/mods"

# Proxy DLL: Ultimate ASI Loader x64 under the chosen proxy name.
TMP_ASI="$(mktemp -d)"
7zz e -y -o"$TMP_ASI" "$VENDOR/extracted/Loader/Asi/UltimateAsiLoader.7z" ASILoader64.dll > /dev/null
cp "$TMP_ASI/ASILoader64.dll" "$STAGE/$PROXY_NAME"
rm -rf "$TMP_ASI"

# Our native bootstrap.
cp "$ROOT/dist/native/reloaded-dropin.asi" "$STAGE/reloaded-dropin.asi"

# Reloaded loader (x64 files; X86 omitted — Relink is 64-bit only).
cp -R "$VENDOR/extracted/Loader/X64/." "$STAGE/reloaded-dropin/loader/"

# .NET runtime: base (host/fxr + NETCore.App) plus the WindowsDesktop.App layer
# (the loader's runtimeconfig requires both frameworks).
echo "==> extracting .NET runtime"
unzip -q "$VENDOR/dotnet-runtime-win-x64.zip" -d "$STAGE/reloaded-dropin/runtime"
unzip -qo "$VENDOR/windowsdesktop-runtime-win-x64.zip" -d "$STAGE/reloaded-dropin/runtime"

echo "==> pruning runtime files we never use"
# The muxer and crash-dump helper: the .asi hosts hostfxr directly, so no
# .exe in the package is ever executed — removing them also leaves nothing
# for malware scanners to chew on.
rm -f "$STAGE/reloaded-dropin/runtime/dotnet.exe"
find "$STAGE/reloaded-dropin/runtime" -name "createdump.exe" -delete
# Locale satellite assemblies (translated error strings only).
for locale in cs de es fr it ja ko pl pt-BR ru tr zh-Hans zh-Hant; do
  find "$STAGE/reloaded-dropin/runtime/shared" -mindepth 3 -maxdepth 3 -type d -name "$locale" -exec rm -rf {} +
done

# Managed bootstrap.
cp -R "$ROOT/dist/bootstrap-publish/." "$STAGE/reloaded-dropin/bootstrap/"

# The third-party base mods (utility manager + libraries) are NOT packaged:
# per their maintainers' wishes we don't redistribute them. BaseModInstaller
# downloads them from their official GitHub releases on the first online
# launch and keeps them updated afterwards. FFXVI's Faith DX12 replacement is
# likewise downloaded from mjsxi/dxd12-patch-files only when FFXVI is detected.

# In-game overlay mod (INSERT key) — ours, ships as a base mod. Its ModVersion is
# stamped from VERSION (numeric part) so the panel always shows the real one.
mkdir -p "$STAGE/mods/_base-mods/reloaded.dropin.overlay"
cp -R "$ROOT/dist/overlay-publish/." "$STAGE/mods/_base-mods/reloaded.dropin.overlay/"
# Loaded reflectively only on FFXVI after Faith has exported its ImGui
# controllers. Keeping this side assembly out of the main overlay's type table
# lets GBFR/P5R load without Faith or NenTools being present.
cp "$ROOT/dist/faith-overlay-publish/ReloadedDropIn.Overlay.Faith.dll" \
  "$STAGE/mods/_base-mods/reloaded.dropin.overlay/"
cp "$ROOT/dist/faith-overlay-publish/ReloadedDropIn.Overlay.Faith.pdb" \
  "$STAGE/mods/_base-mods/reloaded.dropin.overlay/"
cp "$ROOT/dist/faith-overlay-publish/NenTools.ImGui.Interfaces.dll" \
  "$STAGE/mods/_base-mods/reloaded.dropin.overlay/"
sed -i '' "s/\"ModVersion\": \"[^\"]*\"/\"ModVersion\": \"${DROPIN_VERSION%-dev}\"/" \
  "$STAGE/mods/_base-mods/reloaded.dropin.overlay/ModConfig.json"
# The publish emits native cimgui for every platform; the game is win-x64 only.
find "$STAGE/mods/_base-mods/reloaded.dropin.overlay/runtimes" \
  -mindepth 1 -maxdepth 1 -type d ! -name "win-x64" -exec rm -rf {} +

# CriFs asks Persona Essentials for a newline-separated bind list. With no user
# CPK files that list is empty; P5R's native binder does not tolerate the empty
# call under Proton. Keep one uniquely named, inert entry in the overlay so the
# list is valid on a clean install and remains valid after the last mod is
# removed. Only CriFs (P5R) ever reads this path — inert for the other games.
KEEPALIVE_DIR="$STAGE/mods/_base-mods/reloaded.dropin.overlay/P5REssentials/CPK/unloaded_ii"
mkdir -p "$KEEPALIVE_DIR"
cp "$ROOT/scripts/templates/p5r-bind-keepalive.txt" "$KEEPALIVE_DIR/bind_keepalive.txt"

cat > "$STAGE/mods/PUT_MODS_HERE.txt" <<'EOF'
Drop downloaded mod archives (.zip/.7z/.rar) or extracted Reloaded-II mod
folders here. They are picked up automatically on the next game launch.

The _base-mods folder holds the required base mods (the in-game overlay,
plus the game's mod loader and its libraries, which are downloaded from
their authors' official releases on the first launch with internet) —
leave it alone. Everything else in here is yours to add and remove.
EOF

echo "==> user-facing docs, uninstaller, version metadata"
cp "$ROOT/scripts/templates/README.txt" "$STAGE/README.txt"
cp "$ROOT/scripts/templates/THIRD-PARTY-LICENSES.txt" "$STAGE/THIRD-PARTY-LICENSES.txt"
cp -R "$ROOT/scripts/templates/licenses" "$STAGE/licenses"
cp "$VENDOR/extracted/LICENSE.txt" "$STAGE/licenses/Reloaded-II-LICENSES.txt"
cp "$ROOT/scripts/templates/uninstall.sh" "$STAGE/uninstall.sh"
chmod +x "$STAGE/uninstall.sh"
cp "$ROOT/scripts/collect-diagnostics.sh" "$STAGE/collect-diagnostics.sh"
chmod +x "$STAGE/collect-diagnostics.sh"

# Proxy hash lets uninstall.sh remove the proxy DLL only if it's ours.
shasum -a 256 "$STAGE/$PROXY_NAME" | cut -d' ' -f1 > "$STAGE/.dropin-proxy-sha256"
printf '%s\n' "$PROXY_NAME" > "$STAGE/.dropin-proxy-name"

cat > "$STAGE/reloaded-dropin/version.json" <<EOF
{
  "dropin": "$DROPIN_VERSION",
  "adapter": "auto",
  "reloaded": "$RELOADED_VERSION",
  "proxy": "$PROXY_NAME",
  "built": "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
}
EOF

echo "==> zipping"
rm -f "$PACKAGE" "$PACKAGE.sha256"
(cd "$STAGE" && zip -qr "$PACKAGE" .)

echo "==> done"
du -sh "$STAGE" "$PACKAGE"
