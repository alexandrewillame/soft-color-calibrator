#!/bin/bash
# Builds SoftCal Camera for iPhone and iPad (src/SoftCal.Phone.iOS). iOS apps are built on a Mac: run this there, or let
# GitHub Actions run it on one of its Macs (.github/workflows/ios.yml; no Mac of your own needed).
#
#   bash build-ios.sh check    a simulator build: checks that everything compiles (no signing)
#   bash build-ios.sh ipa      the signed app for the App Store: artifacts/release/<version>/app-store/SoftCalCamera-<version>.ipa
#   bash build-ios.sh upload   the same, then uploaded to App Store Connect (TestFlight)
#
# Needs the .NET SDK of global.json; the iOS workload and the Xcode it asks for are found or installed here.
# Signing (ipa, upload) comes from these variables when they are set (GitHub Actions passes its secrets), otherwise from
# the Mac's own keychain and provisioning profiles.
#   IOS_CERTIFICATE_P12_BASE64, IOS_CERTIFICATE_PASSWORD   the Apple Distribution certificate and its key (.p12)
#   IOS_PROVISIONING_PROFILE_BASE64                         the App Store provisioning profile (.mobileprovision)
#   APP_STORE_CONNECT_KEY_ID, APP_STORE_CONNECT_ISSUER_ID, APP_STORE_CONNECT_KEY_BASE64   the API key (.p8), for upload
set -euo pipefail
cd "$(dirname "$0")"

MODE="${1:-check}"
PROJECT="src/SoftCal.Phone.iOS/SoftCal.Phone.iOS.csproj"
BUNDLE_ID="com.softcolorcalibrator.camera"

if [ "$(uname)" != "Darwin" ]; then
  echo "iOS apps are built on a Mac: run this on one, or use GitHub Actions (.github/workflows/ios.yml)."
  exit 1
fi
case "$MODE" in check | ipa | upload) ;; *) echo "Usage: bash build-ios.sh [check|ipa|upload]"; exit 1 ;; esac
if ! command -v dotnet >/dev/null; then
  echo "The .NET SDK was not found: install the version in global.json from https://dotnet.microsoft.com/download."
  exit 1
fi

VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props | head -1)"
echo "SoftCal Camera $VERSION for iOS ($MODE)"

echo
echo "=== .NET iOS workload"
dotnet workload install ios --skip-manifest-update >/dev/null 2>&1 || dotnet workload install ios

# Each version of the workload builds with one version of Xcode (major.minor); use it when this Mac has it.
DOTNET_HOME="${DOTNET_ROOT:-$(dirname "$(readlink -f "$(command -v dotnet)")")}"
SDK_PACK="$(ls -d "$DOTNET_HOME"/packs/Microsoft.iOS.Sdk.net10.0_*/*/ 2>/dev/null | sort -V | tail -1 || true)"
WANTED="$(sed -n 's:.*<RecommendedXcodeVersion>\(.*\)</RecommendedXcodeVersion>.*:\1:p' "${SDK_PACK}targets/Microsoft.iOS.Sdk.Versions.props" 2>/dev/null | head -1 || true)"
XCODE=""
for app in /Applications/Xcode*.app; do
  [ -d "$app" ] || continue
  version="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$app/Contents/Info.plist" 2>/dev/null || true)"
  if [ -n "$WANTED" ] && { [ "$(echo "$version" | cut -d. -f1-2)" = "$WANTED" ] || [ "$version" = "$WANTED" ]; }; then
    XCODE="$app"
  fi
done
# Arrays are expanded as ${A[@]+"${A[@]}"}: macOS's own bash (3.2) calls an empty one unbound.
EXTRA=()
if [ -n "$XCODE" ]; then
  echo "Xcode $WANTED: $XCODE"
  export DEVELOPER_DIR="$XCODE/Contents/Developer" MD_APPLE_SDK_ROOT="$XCODE"
else
  echo "Warning: the iOS workload asks for Xcode ${WANTED:-?}, not found in /Applications; building with $(xcode-select -p) instead."
  EXTRA+=(-p:ValidateXcodeVersion=false)
fi

if [ "$MODE" = "check" ]; then
  echo
  echo "=== Simulator build"
  SIMULATOR="iossimulator-$([ "$(uname -m)" = "arm64" ] && echo arm64 || echo x64)"
  dotnet build "$PROJECT" -c Debug -r "$SIMULATOR" -nologo ${EXTRA[@]+"${EXTRA[@]}"}
  echo
  echo "Built for the simulator: the app compiles."
  exit 0
fi

echo
echo "=== Signing"
WORK="$(mktemp -d)"
KEYCHAIN=""
cleanup() {
  [ -n "$KEYCHAIN" ] && security delete-keychain "$KEYCHAIN" >/dev/null 2>&1 || true
  rm -rf "$WORK"
}
trap cleanup EXIT

if [ -n "${IOS_CERTIFICATE_P12_BASE64:-}" ]; then
  # A keychain of its own for this build, deleted at the end.
  KEYCHAIN="$WORK/signing.keychain-db"
  KEYCHAIN_PASSWORD="$(openssl rand -base64 24)"
  security create-keychain -p "$KEYCHAIN_PASSWORD" "$KEYCHAIN"
  security set-keychain-settings -lut 21600 "$KEYCHAIN"
  security unlock-keychain -p "$KEYCHAIN_PASSWORD" "$KEYCHAIN"
  echo "$IOS_CERTIFICATE_P12_BASE64" | base64 --decode > "$WORK/certificate.p12"
  security import "$WORK/certificate.p12" -P "${IOS_CERTIFICATE_PASSWORD:-}" -A -t cert -f pkcs12 -k "$KEYCHAIN"
  # Apple's intermediate certificate, which a fresh Mac may lack: without it the identity is not valid.
  curl -fsSL https://www.apple.com/certificateauthority/AppleWWDRCAG3.cer -o "$WORK/wwdr.cer" && security import "$WORK/wwdr.cer" -k "$KEYCHAIN" >/dev/null 2>&1 || true
  security set-key-partition-list -S apple-tool:,apple: -k "$KEYCHAIN_PASSWORD" "$KEYCHAIN" >/dev/null
  security list-keychains -d user -s "$KEYCHAIN" $(security list-keychains -d user | tr -d '"')
fi

# (|| true: with several identities, head's early exit must not end the script through pipefail.)
IDENTITY="$(security find-identity -v -p codesigning ${KEYCHAIN:+"$KEYCHAIN"} | sed -n 's/.*"\(Apple Distribution: .*\)"/\1/p' | head -1 || true)"
if [ -z "$IDENTITY" ]; then
  echo "No Apple Distribution certificate was found in this Mac's keychain."
  exit 1
fi
echo "Certificate: $IDENTITY"

PROFILE_ARGS=()
if [ -n "${IOS_PROVISIONING_PROFILE_BASE64:-}" ]; then
  echo "$IOS_PROVISIONING_PROFILE_BASE64" | base64 --decode > "$WORK/profile.mobileprovision"
  security cms -D -i "$WORK/profile.mobileprovision" > "$WORK/profile.plist"
  UUID="$(/usr/libexec/PlistBuddy -c 'Print :UUID' "$WORK/profile.plist")"
  APP_ID="$(/usr/libexec/PlistBuddy -c 'Print :Entitlements:application-identifier' "$WORK/profile.plist")"
  if [ "${APP_ID#*.}" != "$BUNDLE_ID" ]; then
    echo "The provisioning profile is for $APP_ID, not $BUNDLE_ID: make one for this app."
    exit 1
  fi
  # Where Xcode 16 and later look, and where earlier ones did.
  for folder in "$HOME/Library/Developer/Xcode/UserData/Provisioning Profiles" "$HOME/Library/MobileDevice/Provisioning Profiles"; do
    mkdir -p "$folder"
    cp "$WORK/profile.mobileprovision" "$folder/$UUID.mobileprovision"
  done
  echo "Provisioning profile: $UUID"
  PROFILE_ARGS+=("-p:CodesignProvision=$UUID")
fi

echo
echo "=== App for the App Store"
OUT="artifacts/release/$VERSION/app-store"
rm -rf "$OUT" src/SoftCal.Phone.iOS/bin/Release
mkdir -p "$OUT"
dotnet publish "$PROJECT" -c Release -r ios-arm64 -nologo "-p:CodesignKey=$IDENTITY" ${PROFILE_ARGS[@]+"${PROFILE_ARGS[@]}"} ${EXTRA[@]+"${EXTRA[@]}"}
IPA="$(find src/SoftCal.Phone.iOS/bin/Release -name '*.ipa' | head -1 || true)"
if [ -z "$IPA" ]; then
  echo "The built app (.ipa) was not found."
  exit 1
fi
cp "$IPA" "$OUT/SoftCalCamera-$VERSION.ipa"
echo "Built: $OUT/SoftCalCamera-$VERSION.ipa"

if [ "$MODE" = "upload" ]; then
  echo
  echo "=== Upload to App Store Connect"
  if [ -z "${APP_STORE_CONNECT_KEY_ID:-}" ] || [ -z "${APP_STORE_CONNECT_ISSUER_ID:-}" ] || [ -z "${APP_STORE_CONNECT_KEY_BASE64:-}" ]; then
    echo "The App Store Connect API key is missing."
    exit 1
  fi
  # altool reads the key from this folder, by its ID.
  mkdir -p "$HOME/.appstoreconnect/private_keys"
  KEY_FILE="$HOME/.appstoreconnect/private_keys/AuthKey_$APP_STORE_CONNECT_KEY_ID.p8"
  echo "$APP_STORE_CONNECT_KEY_BASE64" | base64 --decode > "$KEY_FILE"
  xcrun altool --upload-app --type ios --file "$OUT/SoftCalCamera-$VERSION.ipa" \
    --apiKey "$APP_STORE_CONNECT_KEY_ID" --apiIssuer "$APP_STORE_CONNECT_ISSUER_ID"
  rm -f "$KEY_FILE"
  echo "Uploaded: it appears in App Store Connect › TestFlight once Apple has processed it (about 10 to 30 minutes)."
fi
