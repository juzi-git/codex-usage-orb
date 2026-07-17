#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
BUILD_DIR="$SCRIPT_DIR/build"
APP_DIR="$BUILD_DIR/CodexUsageOrb.app"
CONTENTS_DIR="$APP_DIR/Contents"
MACOS_DIR="$CONTENTS_DIR/MacOS"

rm -rf "$APP_DIR"
mkdir -p "$MACOS_DIR"

xcrun swiftc \
  -O \
  -framework AppKit \
  -framework Foundation \
  -o "$MACOS_DIR/CodexUsageOrb" \
  "$SCRIPT_DIR/Program.swift"

cp "$SCRIPT_DIR/Info.plist" "$CONTENTS_DIR/Info.plist"
chmod +x "$MACOS_DIR/CodexUsageOrb"

echo "Build complete: $APP_DIR"
echo "Run with: open '$APP_DIR'"
