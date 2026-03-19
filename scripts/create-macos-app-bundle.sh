#!/usr/bin/env bash
set -euo pipefail

PUBLISH_DIR="${1:?Publish output directory required}"
APP_PATH="${2:?Destination .app path required}"

APP_NAME="GnOuGo.Agent"
BUNDLE_ID="dev.slimfaas.gnougo-agent"
EXECUTABLE="GnOuGo.Agent"   # AssemblyName in Desktop csproj
VERSION="1.0.0"

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ASSETS_DIR="$ROOT/src/GnOuGo.Agent.Desktop/Assets"

# Ensure icon.icns exists (macOS only)
if [[ "$(uname)" == "Darwin" ]]; then
  if [[ ! -f "$ASSETS_DIR/icon.icns" ]]; then
    if command -v iconutil >/dev/null 2>&1; then
      echo "Generating icon.icns with iconutil..."
      iconutil -c icns "$ASSETS_DIR/icon.iconset" -o "$ASSETS_DIR/icon.icns"
    else
      echo "iconutil not found. Install Xcode Command Line Tools."
      echo "You can still run the binary without a .app bundle."
      exit 1
    fi
  fi
else
  echo "Warning: create-macos-app-bundle.sh is intended to be run on macOS (for iconutil)."
fi

rm -rf "$APP_PATH"
mkdir -p "$APP_PATH/Contents/MacOS" "$APP_PATH/Contents/Resources"

# Copy published files
cp -R "$PUBLISH_DIR/." "$APP_PATH/Contents/MacOS/"

# Info.plist
cat > "$APP_PATH/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>$APP_NAME</string>
  <key>CFBundleDisplayName</key><string>$APP_NAME</string>
  <key>CFBundleIdentifier</key><string>$BUNDLE_ID</string>
  <key>CFBundleVersion</key><string>$VERSION</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleExecutable</key><string>$EXECUTABLE</string>
  <key>CFBundleIconFile</key><string>$APP_NAME</string>
  <key>LSMinimumSystemVersion</key><string>11.0</string>
  <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
EOF

# Copy icon to Resources (name must match CFBundleIconFile without extension)
cp "$ASSETS_DIR/icon.icns" "$APP_PATH/Contents/Resources/$APP_NAME.icns"

echo "App bundle created: $APP_PATH"
