#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
SOURCE_FILE="$REPO_ROOT/src/macos/Program.swift"
PLIST_FILE="$REPO_ROOT/src/macos/Info.plist"
APP_DIR="$REPO_ROOT/dist/macos/CodexUsageOrb.app"
CONTENTS_DIR="$APP_DIR/Contents"
MACOS_DIR="$CONTENTS_DIR/MacOS"

if [[ ! -f "$SOURCE_FILE" ]]; then
  echo "Source file not found: $SOURCE_FILE" >&2
  exit 1
fi

if [[ ! -f "$PLIST_FILE" ]]; then
  echo "Info.plist not found: $PLIST_FILE" >&2
  exit 1
fi

rm -rf "$APP_DIR"
mkdir -p "$MACOS_DIR"

xcrun swiftc \
  -O \
  -framework AppKit \
  -framework Foundation \
  -o "$MACOS_DIR/CodexUsageOrb" \
  "$SOURCE_FILE"

cp "$PLIST_FILE" "$CONTENTS_DIR/Info.plist"
chmod +x "$MACOS_DIR/CodexUsageOrb"

echo "Build complete: $APP_DIR"
echo "Run with: open '$APP_DIR'"
