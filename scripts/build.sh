#!/usr/bin/env zsh
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "$0")/.." && pwd)"
MOD_DIR="$ROOT_DIR/mod"
RUNTIME_DIR="$ROOT_DIR/runtime"
OUT_DIR="$ROOT_DIR/build"

ONI_APP="${ONI_APP:-$HOME/Library/Application Support/Steam/steamapps/common/OxygenNotIncluded/OxygenNotIncluded.app}"
MANAGED_DIR="$ONI_APP/Contents/Resources/Data/Managed"

mkdir -p "$OUT_DIR"

CSC_BIN="${CSC_BIN:-csc}"

"$CSC_BIN" \
  -nologo \
  -target:library \
  -out:"$OUT_DIR/OniAiAssistant.dll" \
  -r:"$MANAGED_DIR/netstandard.dll" \
  -r:"$MANAGED_DIR/Assembly-CSharp.dll" \
  -r:"$MANAGED_DIR/Assembly-CSharp-firstpass.dll" \
  -r:"$MANAGED_DIR/0Harmony.dll" \
  -r:"$MANAGED_DIR/Newtonsoft.Json.dll" \
  -r:"$MANAGED_DIR/UnityEngine.CoreModule.dll" \
  -r:"$MANAGED_DIR/UnityEngine.UIModule.dll" \
  -r:"$MANAGED_DIR/UnityEngine.UI.dll" \
  -r:"$MANAGED_DIR/Unity.TextMeshPro.dll" \
  -r:"$MANAGED_DIR/UnityEngine.TextRenderingModule.dll" \
  -r:"$MANAGED_DIR/UnityEngine.InputLegacyModule.dll" \
  -r:"$MANAGED_DIR/UnityEngine.IMGUIModule.dll" \
  -r:"$MANAGED_DIR/UnityEngine.ScreenCaptureModule.dll" \
  -r:"$MANAGED_DIR/UnityEngine.UnityWebRequestModule.dll" \
  "$MOD_DIR"/*.cs

"$CSC_BIN" \
  -nologo \
  -target:library \
  -out:"$OUT_DIR/OniAiRuntime.dll" \
  -r:"$OUT_DIR/OniAiAssistant.dll" \
  -r:"$MANAGED_DIR/netstandard.dll" \
  -r:"$MANAGED_DIR/UnityEngine.CoreModule.dll" \
  "$RUNTIME_DIR/OniAiRuntime.cs"

echo "Build output: $OUT_DIR/OniAiAssistant.dll"
echo "Build output: $OUT_DIR/OniAiRuntime.dll"
