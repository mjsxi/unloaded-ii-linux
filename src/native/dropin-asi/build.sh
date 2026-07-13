#!/usr/bin/env bash
# Cross-compiles reloaded-dropin.asi (Windows x64 PE) from macOS/Linux.
# Requires mingw-w64 (brew install mingw-w64 / dnf install mingw64-gcc).
set -euo pipefail

cd "$(dirname "$0")"
OUT_DIR="${1:-../../../dist/native}"
mkdir -p "$OUT_DIR"

x86_64-w64-mingw32-gcc \
  -shared \
  -O2 \
  -Wall -Wextra \
  -static-libgcc \
  -o "$OUT_DIR/reloaded-dropin.asi" \
  dropin.c

file "$OUT_DIR/reloaded-dropin.asi"

# Ultimate ASI Loader invokes this export after LoadLibrary returns. P5R relies
# on it to keep the startup thread out of CRI until Reloaded's hooks are ready.
x86_64-w64-mingw32-objdump -p "$OUT_DIR/reloaded-dropin.asi" | rg 'InitializeASI' >/dev/null || {
  echo "reloaded-dropin.asi is missing the required InitializeASI export" >&2
  exit 1
}
