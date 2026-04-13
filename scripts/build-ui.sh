#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CLIENT_APP="$ROOT_DIR/src/GnOuGo.Agent.Server/ClientApp"

echo "Building GnOuGo.Agent UI with Vite..."
cd "$CLIENT_APP"
corepack pnpm install --frozen-lockfile
corepack pnpm build

echo "✅ UI built into: $ROOT_DIR/src/GnOuGo.Agent.Server/wwwroot/ui"
