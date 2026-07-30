#!/usr/bin/env bash
set -euo pipefail

VERSION="${1:-0.1.0-preview.4}"
RID="${2:-osx-arm64}"
case "$RID" in
  osx-arm64|osx-x64) ;;
  *) echo "RID must be osx-arm64 or osx-x64" >&2; exit 2 ;;
esac

ROOT="$(cd "$(dirname "$0")" && pwd)"
export AVALONIA_TELEMETRY_OPTOUT=1
PROJECT="$ROOT/NovaDesktop.Mac/NovaDesktop.Mac.csproj"
PUBLISH="$ROOT/.mac-release/$RID"
PACKAGE="$ROOT/dist/macos/NOVA-Mac-$VERSION-$RID"
APP="$PACKAGE/NOVA.app"
CONTENTS="$APP/Contents"
MACOS="$CONTENTS/MacOS"
RESOURCES="$CONTENTS/Resources"
PLIST_TEMPLATE="$ROOT/packaging/macos/Info.plist"
ZIP="$ROOT/dist/macos/NOVA-Mac-$VERSION-$RID.zip"
DMG="$ROOT/dist/macos/NOVA-Mac-$VERSION-$RID.dmg"
MANIFEST="$ROOT/dist/macos/NOVA-Mac-$VERSION-$RID.release.json"

if [[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  if [[ -z "${NOVA_SIGNING_IDENTITY:-}" || -z "${NOVA_NOTARY_PROFILE:-}" ]]; then
    echo "Stable macOS release blocked: Developer ID and NOVA_NOTARY_PROFILE are required." >&2
    exit 3
  fi
fi

dotnet run --project "$PROJECT" --configuration Release -- --startup-smoke
dotnet run --project "$PROJECT" --configuration Release -- --agentos-smoke

rm -rf "$PUBLISH" "$PACKAGE"
rm -f "$ZIP" "$DMG" "$MANIFEST"
mkdir -p "$MACOS" "$RESOURCES"

dotnet publish "$PROJECT" \
  --configuration Release \
  --runtime "$RID" \
  --self-contained true \
  --output "$PUBLISH" \
  -p:Version="$VERSION" \
  -p:UseAppHost=true \
  --nologo

cp -R "$PUBLISH/." "$MACOS/"
chmod +x "$MACOS/NovaDesktop.Mac"
sed \
  -e "s/__VERSION__/$VERSION/g" \
  -e "s/__BUILD__/$(date -u +%s)/g" \
  "$PLIST_TEMPLATE" > "$CONTENTS/Info.plist"
plutil -lint "$CONTENTS/Info.plist"

if [[ -n "${NOVA_SIGNING_IDENTITY:-}" ]]; then
  find "$MACOS" -type f -perm +111 -print0 |
    xargs -0 -I{} codesign --force --timestamp --options runtime \
      --sign "$NOVA_SIGNING_IDENTITY" "{}"
  codesign --force --timestamp --options runtime \
    --sign "$NOVA_SIGNING_IDENTITY" "$APP"
  SIGNING_STATUS="DEVELOPER_ID_SIGNED"
else
  codesign --force --deep --sign - "$APP"
  SIGNING_STATUS="ADHOC_PREVIEW"
fi

codesign --verify --deep --strict --verbose=2 "$APP"
if [[ "$(uname -m)" == "arm64" && "$RID" == "osx-arm64" ]] \
  || [[ "$(uname -m)" == "x86_64" && "$RID" == "osx-x64" ]]; then
  "$MACOS/NovaDesktop.Mac" --startup-smoke
fi

mkdir -p "$ROOT/dist/macos"
ditto -c -k --sequesterRsrc --keepParent "$APP" \
  "$ZIP"

NOTARIZED=false
if [[ -n "${NOVA_NOTARY_PROFILE:-}" ]]; then
  if [[ -z "${NOVA_SIGNING_IDENTITY:-}" ]]; then
    echo "Notarization requires NOVA_SIGNING_IDENTITY." >&2
    exit 4
  fi
  xcrun notarytool submit "$ZIP" \
    --keychain-profile "$NOVA_NOTARY_PROFILE" \
    --wait
  xcrun stapler staple "$APP"
  xcrun stapler validate "$APP"
  rm -f "$ZIP"
  ditto -c -k --sequesterRsrc --keepParent "$APP" "$ZIP"
  NOTARIZED=true
fi

hdiutil create \
  -volname "NOVA" \
  -srcfolder "$APP" \
  -ov \
  "$DMG"

if [[ -n "${NOVA_SIGNING_IDENTITY:-}" ]]; then
  codesign --force --timestamp --sign "$NOVA_SIGNING_IDENTITY" "$DMG"
fi
if [[ -n "${NOVA_NOTARY_PROFILE:-}" ]]; then
  xcrun notarytool submit "$DMG" \
    --keychain-profile "$NOVA_NOTARY_PROFILE" \
    --wait
  xcrun stapler staple "$DMG"
  xcrun stapler validate "$DMG"
  spctl --assess --type execute --verbose=4 "$APP"
fi

ZIP_SHA="$(shasum -a 256 "$ZIP" | awk '{print $1}')"
DMG_SHA="$(shasum -a 256 "$DMG" | awk '{print $1}')"
cat > "$MANIFEST" <<EOF
{
  "schema_version": 1,
  "product": "NOVA for Mac",
  "version": "$VERSION",
  "runtime": "$RID",
  "bundle_id": "ai.nova.agentos.desktop",
  "minimum_macos": "12.0",
  "signing_status": "$SIGNING_STATUS",
  "notarized": $NOTARIZED,
  "automatic_updates_enabled": false,
  "zip": { "file": "$(basename "$ZIP")", "sha256": "$ZIP_SHA" },
  "dmg": { "file": "$(basename "$DMG")", "sha256": "$DMG_SHA" }
}
EOF

echo "App: $APP"
echo "Zip: $ZIP"
echo "DMG: $DMG"
echo "Manifest: $MANIFEST"
if [[ -z "${NOVA_SIGNING_IDENTITY:-}" ]]; then
  echo "Warning: ad-hoc signed build. Set NOVA_SIGNING_IDENTITY for Developer ID signing."
fi
if [[ -z "${NOVA_NOTARY_PROFILE:-}" ]]; then
  echo "Warning: build is not notarized. Set NOVA_NOTARY_PROFILE for notarytool."
fi
