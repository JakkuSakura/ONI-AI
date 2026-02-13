#!/usr/bin/env zsh
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "$0")/.." && pwd)"
RUNTIME_DIR="$ROOT_DIR/runtime"
OUT_DIR="$ROOT_DIR/build"

ONI_APP="${ONI_APP:-$HOME/Library/Application Support/Steam/steamapps/common/OxygenNotIncluded/OxygenNotIncluded.app}"
MANAGED_DIR="$ONI_APP/Contents/Resources/Data/Managed"

CSC_BIN="${CSC_BIN:-csc}"

if [ ! -f "$OUT_DIR/OniAiAssistant.dll" ]; then
  "$ROOT_DIR/scripts/build.sh"
fi

"$CSC_BIN" \
  -nologo \
  -target:library \
  -out:"$OUT_DIR/OniAiRuntime.dll" \
  -r:"$OUT_DIR/OniAiAssistant.dll" \
  -r:"$MANAGED_DIR/netstandard.dll" \
  -r:"$MANAGED_DIR/UnityEngine.CoreModule.dll" \
  "$RUNTIME_DIR/OniAiRuntime.cs"

echo "Build output: $OUT_DIR/OniAiRuntime.dll"
