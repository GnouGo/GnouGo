#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="$ROOT/artifacts/publish/osx-arm64"

echo "Publishing GnOuGo.Agent Desktop (Photino) for macOS arm64 (NativeAOT)..."
dotnet publish "$ROOT/src/GnOuGo.Agent.Desktop/GnOuGo.Agent.Desktop.csproj" -c Release -r osx-arm64 \
  -p:PublishAot=true -p:StripSymbols=true --self-contained true \
  -o "$OUT/desktop"

echo "Creating .app bundle..."
bash "$ROOT/scripts/create-macos-app-bundle.sh" "$OUT/desktop" "$OUT/gnougo.app"

echo "Done:"
echo "  $OUT/gnougo.app"
