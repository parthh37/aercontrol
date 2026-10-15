#!/bin/bash
# Signed + notarized release build of AerControl.app (with the widget) for distribution
# outside the App Store.
#
# One-time setup:
#   1. Join the Apple Developer Program; create a "Developer ID Application" certificate
#      (Xcode › Settings › Accounts › Manage Certificates).
#   2. Register the App Group  <TEAMID>.com.aercontrol.shared  for both bundle ids
#      (com.aercontrol.app, com.aercontrol.app.widget). Automatic signing does this.
#   3. Store notarization credentials in the keychain:
#        xcrun notarytool store-credentials aercontrol-notary \
#          --apple-id you@example.com --team-id TEAMID --password <app-specific-password>
#
# Usage:  DEVELOPMENT_TEAM=TEAMID ./scripts/release.sh
#         (NOTARY_PROFILE defaults to aercontrol-notary; SKIP_NOTARIZE=1 to skip)
set -euo pipefail
cd "$(dirname "$0")/.."

: "${DEVELOPMENT_TEAM:?Set DEVELOPMENT_TEAM to your 10-character Apple team id}"
NOTARY_PROFILE="${NOTARY_PROFILE:-aercontrol-notary}"
BUILD=build/release
rm -rf "$BUILD" && mkdir -p "$BUILD"

command -v xcodegen >/dev/null || { echo "Install XcodeGen: brew install xcodegen"; exit 1; }
xcodegen generate --spec project.yml

xcodebuild -project AerControl.xcodeproj -scheme AerControl -configuration Release \
  -archivePath "$BUILD/AerControl.xcarchive" -allowProvisioningUpdates \
  DEVELOPMENT_TEAM="$DEVELOPMENT_TEAM" archive

xcodebuild -exportArchive -archivePath "$BUILD/AerControl.xcarchive" \
  -exportOptionsPlist scripts/ExportOptions.plist -exportPath "$BUILD/export" \
  -allowProvisioningUpdates

APP="$BUILD/export/AerControl.app"
codesign --verify --deep --strict --verbose=2 "$APP"

ZIP="$BUILD/AerControl.zip"
ditto -c -k --keepParent "$APP" "$ZIP"
if [ "${SKIP_NOTARIZE:-0}" != "1" ]; then
  xcrun notarytool submit "$ZIP" --keychain-profile "$NOTARY_PROFILE" --wait
  xcrun stapler staple "$APP"
  rm "$ZIP" && ditto -c -k --keepParent "$APP" "$ZIP"
fi

DMG="$BUILD/AerControl.dmg"
hdiutil create -volname AerControl -srcfolder "$APP" -ov -format UDZO "$DMG"
[ "${SKIP_NOTARIZE:-0}" != "1" ] && xcrun notarytool submit "$DMG" --keychain-profile "$NOTARY_PROFILE" --wait \
  && xcrun stapler staple "$DMG"
spctl --assess --type execute --verbose "$APP" || true
echo "Done: $ZIP and $DMG"
