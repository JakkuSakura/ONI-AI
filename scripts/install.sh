#!/usr/bin/env zsh
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "$0")/.." && pwd)"
BUILD_DIR="$ROOT_DIR/build"
MOD_SRC_DIR="$ROOT_DIR/mod"

ONI_MODS_DIR="${ONI_MODS_DIR:-$HOME/Library/Application Support/unity.Klei.Oxygen Not Included/mods}"
TARGET_DIR="$ONI_MODS_DIR/local/jakku.oni_ai_assistant"

mkdir -p "$TARGET_DIR"

cp "$BUILD_DIR/OniAiAssistant.dll" "$TARGET_DIR/"
cp "$MOD_SRC_DIR/mod.yaml" "$TARGET_DIR/"
cp "$MOD_SRC_DIR/mod_info.yaml" "$TARGET_DIR/"
cp "$MOD_SRC_DIR/oni_ai_config.ini" "$TARGET_DIR/"

echo "Installed to: $TARGET_DIR"
