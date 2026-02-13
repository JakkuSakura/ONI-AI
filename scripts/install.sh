#!/usr/bin/env zsh
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "$0")/.." && pwd)"
BUILD_DIR="$ROOT_DIR/build"
MOD_SRC_DIR="$ROOT_DIR/mod"

ONI_MODS_DIR="${ONI_MODS_DIR:-$HOME/Library/Application Support/unity.Klei.Oxygen Not Included/mods}"
TARGET_DIR="$ONI_MODS_DIR/local/jakku.oni_ai_assistant"
RUNTIME_TARGET_DIR="$TARGET_DIR/runtime"

mkdir -p "$TARGET_DIR"
mkdir -p "$RUNTIME_TARGET_DIR"

cp "$BUILD_DIR/OniAiAssistant.dll" "$TARGET_DIR/"
cp "$BUILD_DIR/OniAiRuntime.dll" "$RUNTIME_TARGET_DIR/OniAiRuntime.dll"
cp "$MOD_SRC_DIR/mod.yaml" "$TARGET_DIR/"
cp "$MOD_SRC_DIR/mod_info.yaml" "$TARGET_DIR/"

if [ ! -f "$TARGET_DIR/oni_ai_config.ini" ]; then
  cp "$MOD_SRC_DIR/oni_ai_config.ini" "$TARGET_DIR/"
fi

echo "Installed to: $TARGET_DIR"
