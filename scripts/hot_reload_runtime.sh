#!/usr/bin/env zsh
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "$0")/.." && pwd)"
SRC_RUNTIME="$ROOT_DIR/runtime/OniAiRuntime.cs"
BUILD_RUNTIME="$ROOT_DIR/build/OniAiRuntime.dll"
ONI_MODS_DIR="${ONI_MODS_DIR:-$HOME/Library/Application Support/unity.Klei.Oxygen Not Included/mods}"
TARGET_RUNTIME="$ONI_MODS_DIR/local/jakku.oni_ai_assistant/runtime/OniAiRuntime.dll"

mkdir -p "$(dirname "$TARGET_RUNTIME")"

last_sig=""
echo "Watching $SRC_RUNTIME"
echo "Target runtime DLL: $TARGET_RUNTIME"

while true; do
  sig="$(cksum "$SRC_RUNTIME" | awk '{print $1":"$2}')"
  if [ "$sig" != "$last_sig" ]; then
    echo "[hot-reload] Change detected at $(date +%H:%M:%S), rebuilding runtime..."
    "$ROOT_DIR/scripts/build_runtime.sh"
    cp "$BUILD_RUNTIME" "$TARGET_RUNTIME"
    echo "[hot-reload] Runtime DLL copied. ONI should reload within ~1s."
    last_sig="$sig"
  fi

  sleep 1
done
