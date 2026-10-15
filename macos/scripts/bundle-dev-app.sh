#!/bin/bash
# Builds AerControl with SwiftPM and wraps it into dist/AerControl.app (ad-hoc signed,
# not sandboxed, no widget). For quick local runs without Xcode. Release builds use
# the Xcode project (project.yml), which embeds the widget and signs with your team.
set -euo pipefail
cd "$(dirname "$0")/.."

CONFIG="${CONFIG:-release}"
swift build -c "$CONFIG" --product AerControl
BIN="$(swift build -c "$CONFIG" --show-bin-path)/AerControl"

APP="dist/AerControl.app"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN" "$APP/Contents/MacOS/AerControl"

# Fill in the Xcode build-setting placeholders. No team prefix, so the App Group suite
# falls back to com.aercontrol.shared (see AerShared.appGroupID).
sed -e 's/$(EXECUTABLE_NAME)/AerControl/' \
    -e 's/$(PRODUCT_BUNDLE_IDENTIFIER)/com.aercontrol.app.dev/' \
    -e 's/$(MARKETING_VERSION)/1.0.0-dev/' \
    -e 's/$(CURRENT_PROJECT_VERSION)/1/' \
    -e 's/$(TeamIdentifierPrefix)//' \
    App/Info.plist > "$APP/Contents/Info.plist"
[ -f App/AppIcon.icns ] && cp App/AppIcon.icns "$APP/Contents/Resources/AppIcon.icns" \
    && /usr/libexec/PlistBuddy -c "Add :CFBundleIconFile string AppIcon" "$APP/Contents/Info.plist"

codesign --force --sign - --timestamp=none "$APP"
echo "Built $APP"
echo "Run:  open $APP"
