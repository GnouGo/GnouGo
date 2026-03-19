#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="$ROOT/artifacts/publish/osx-arm64"

echo "Publishing GnOuGo.Agent Server (web) for macOS arm64 (NativeAOT)..."
dotnet publish "$ROOT/src/GnOuGo.Agent.Server/GnOuGo.Agent.Server.csproj" -c Release -r osx-arm64 \
  -p:PublishAot=true -p:StripSymbols=true --self-contained true \
  -o "$OUT/server"

echo "Done:"
echo "  $OUT/server"
