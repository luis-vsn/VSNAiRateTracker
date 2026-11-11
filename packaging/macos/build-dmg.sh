#!/usr/bin/env bash
# VSN AI Rate Tracker - macOS App Bundle & DMG Builder Script
#
# Usage: build-dmg.sh [Configuration] [Version] [Runtime]
#   Version: MAJOR.MINOR.PATCH.BUILD. When empty/omitted it is derived from the latest "vX.Y.Z"
#            git tag with BUILD = 0 (local development builds).
#   Runtime: osx-arm64 (Apple Silicon, default) | osx-x64 (Intel)
#
# Code signing (optional, via environment):
#   CODESIGN_IDENTITY        "Developer ID Application: <Org> (<TEAMID>)". When unset the bundle is
#                            signed ad-hoc (runs locally; downloaded copies need right-click > Open).
#   Notarization (only with CODESIGN_IDENTITY), either:
#     NOTARY_KEYCHAIN_PROFILE  profile stored with `xcrun notarytool store-credentials`
#     or APPLE_ID + APPLE_TEAM_ID + APPLE_APP_PASSWORD (app-specific password)
set -euo pipefail

CONFIGURATION="${1:-Release}"
VERSION="${2:-}"
RUNTIME="${3:-osx-arm64}"

case "${RUNTIME}" in
    osx-arm64|osx-x64) ;;
    *) echo "ERROR: unsupported runtime '${RUNTIME}' (expected osx-arm64 or osx-x64)" >&2; exit 1 ;;
esac

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "${SCRIPT_DIR}/../.." && pwd)"

if [[ -z "${VERSION}" ]]; then
    BASE_VERSION="0.0.0"
    LATEST_TAG="$(git -C "${ROOT_DIR}" describe --tags --abbrev=0 --match 'v[0-9]*' 2>/dev/null || true)"
    if [[ "${LATEST_TAG}" =~ ^v([0-9]+\.[0-9]+\.[0-9]+)$ ]]; then
        BASE_VERSION="${BASH_REMATCH[1]}"
    fi
    VERSION="${BASE_VERSION}.0"
fi
if ! [[ "${VERSION}" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    echo "ERROR: invalid version '${VERSION}' (expected MAJOR.MINOR.PATCH.BUILD)" >&2; exit 1
fi
# CFBundleShortVersionString must be MAJOR.MINOR.PATCH; CFBundleVersion carries the full build version.
SHORT_VERSION="${VERSION%.*}"
PROJECT_PATH="${ROOT_DIR}/VSNAiRateTracker/VSNAiRateTracker.csproj"
DIST_DIR="${ROOT_DIR}/packaging/dist"
APP_NAME="VSN AI Rate Tracker"
APP_BUNDLE="${DIST_DIR}/${RUNTIME}/${APP_NAME}.app"
PUBLISH_DIR="${DIST_DIR}/macos-publish-${RUNTIME}"
DMG_PATH="${DIST_DIR}/VSNAiRateTracker-${VERSION}-${RUNTIME}.dmg"
ENTITLEMENTS="${SCRIPT_DIR}/entitlements.plist"
# Single source of truth for the app icon (same asset used by the app and the Windows build)
ICON_SOURCE="${ROOT_DIR}/VSNAiRateTracker/Assets/icon.png"

WORK_DIR="$(mktemp -d)"
trap 'rm -rf "${WORK_DIR}"' EXIT

echo "===================================================="
echo "Building ${APP_NAME} macOS Bundle & DMG (${RUNTIME}, v${VERSION})"
echo "===================================================="

mkdir -p "${DIST_DIR}/${RUNTIME}"
rm -rf "${APP_BUNDLE}" "${DMG_PATH}" "${PUBLISH_DIR}"

# 1. Publish self-contained app (multi-file: the standard .app layout, no native-lib self-extraction)
echo "--> [1/5] Publishing self-contained app (${RUNTIME})..."
dotnet publish "${PROJECT_PATH}" \
    -c "${CONFIGURATION}" \
    -r "${RUNTIME}" \
    --self-contained true \
    -p:UseAppHost=true \
    -p:Version="${VERSION}" \
    -o "${PUBLISH_DIR}"

# 2. Assemble .app bundle structure
echo "--> [2/5] Assembling .app bundle..."
mkdir -p "${APP_BUNDLE}/Contents/MacOS" "${APP_BUNDLE}/Contents/Resources"
cp -R "${PUBLISH_DIR}/." "${APP_BUNDLE}/Contents/MacOS/"
rm -f "${APP_BUNDLE}/Contents/MacOS/Info.plist" "${APP_BUNDLE}/Contents/MacOS/"*.pdb
chmod +x "${APP_BUNDLE}/Contents/MacOS/VSNAiRateTracker"

cp "${ROOT_DIR}/VSNAiRateTracker/Info.plist" "${APP_BUNDLE}/Contents/Info.plist"
plutil -replace CFBundleShortVersionString -string "${SHORT_VERSION}" "${APP_BUNDLE}/Contents/Info.plist"
plutil -replace CFBundleVersion -string "${VERSION}" "${APP_BUNDLE}/Contents/Info.plist"
plutil -lint "${APP_BUNDLE}/Contents/Info.plist" >/dev/null

# Bundled telemetry defaults (encrypted defaults.enc): pre-activate telemetry
# when both endpoint and headers are provided (via OTEL_EXPORTER_OTLP_* or TELEMETRY_*)
TEL_ENDPOINT="${OTEL_EXPORTER_OTLP_ENDPOINT:-${TELEMETRY_ENDPOINT:-}}"
TEL_HEADERS="${OTEL_EXPORTER_OTLP_HEADERS:-${TELEMETRY_HEADERS:-}}"
if [ -n "${TEL_ENDPOINT}" ] && [ -n "${TEL_HEADERS}" ]; then
    DEFAULTS_PLIST="${WORK_DIR}/defaults.plist"
    DEFAULTS_TEMP_JSON="${WORK_DIR}/temp_defaults.json"
    DEFAULTS_ENC="${APP_BUNDLE}/Contents/MacOS/defaults.enc"
    plutil -create xml1 "${DEFAULTS_PLIST}"
    plutil -insert Telemetry -dictionary "${DEFAULTS_PLIST}"
    plutil -insert Telemetry.Enabled -bool YES "${DEFAULTS_PLIST}"
    plutil -insert Telemetry.Endpoint -string "${TEL_ENDPOINT}" "${DEFAULTS_PLIST}"
    plutil -insert Telemetry.Headers -string "${TEL_HEADERS}" "${DEFAULTS_PLIST}"
    plutil -insert Telemetry.ServiceName -string "vsn-ai-rate-tracker" "${DEFAULTS_PLIST}"
    plutil -insert Telemetry.Protocol -string "HttpProtobuf" "${DEFAULTS_PLIST}"
    plutil -convert json "${DEFAULTS_PLIST}" -o "${DEFAULTS_TEMP_JSON}"
    "${APP_BUNDLE}/Contents/MacOS/VSNAiRateTracker" --encrypt-defaults "${DEFAULTS_TEMP_JSON}" "${DEFAULTS_ENC}"
    rm -f "${DEFAULTS_TEMP_JSON}"
    echo "    Injected encrypted bundled telemetry defaults (defaults.enc)."
fi

# 3. App icon (.icns) generated from the app's icon asset. Only sizes up to the source
#    resolution are emitted (never upscaled); macOS scales the largest one when needed.
echo "--> [3/5] Generating AppIcon.icns..."
ICONSET="${WORK_DIR}/AppIcon.iconset"
mkdir -p "${ICONSET}"
SOURCE_SIZE="$(sips -g pixelWidth "${ICON_SOURCE}" | awk '/pixelWidth/ {print $2}')"
for size in 16 32 128 256 512; do
    if [ "${size}" -le "${SOURCE_SIZE}" ]; then
        sips -z "${size}" "${size}" "${ICON_SOURCE}" --out "${ICONSET}/icon_${size}x${size}.png" >/dev/null
    fi
    double=$((size * 2))
    if [ "${double}" -le "${SOURCE_SIZE}" ]; then
        sips -z "${double}" "${double}" "${ICON_SOURCE}" --out "${ICONSET}/icon_${size}x${size}@2x.png" >/dev/null
    fi
done
if [ "${SOURCE_SIZE}" -lt 1024 ]; then
    echo "    Note: icon source is ${SOURCE_SIZE}px; provide a 1024px icon for sharp large Finder/Launchpad icons."
fi
iconutil -c icns "${ICONSET}" -o "${APP_BUNDLE}/Contents/Resources/AppIcon.icns"

# 4. Code signing
echo "--> [4/5] Code signing..."
if [ -n "${CODESIGN_IDENTITY:-}" ]; then
    echo "    Using identity: ${CODESIGN_IDENTITY} (Hardened Runtime)"
    # Sign nested native code first, then the bundle (avoid --deep for Developer ID)
    find "${APP_BUNDLE}/Contents/MacOS" -type f \( -name "*.dylib" -o -perm -u+x \) \
        ! -name "VSNAiRateTracker" -print0 |
        while IFS= read -r -d '' f; do
            codesign --force --timestamp --options runtime --entitlements "${ENTITLEMENTS}" \
                --sign "${CODESIGN_IDENTITY}" "${f}"
        done
    codesign --force --timestamp --options runtime --entitlements "${ENTITLEMENTS}" \
        --sign "${CODESIGN_IDENTITY}" "${APP_BUNDLE}"
else
    echo "    CODESIGN_IDENTITY not set: ad-hoc signature (local use / internal distribution)"
    codesign --force --deep --sign - "${APP_BUNDLE}"
fi
codesign --verify --deep --strict "${APP_BUNDLE}"

# 5. Create DMG disk image with /Applications shortcut
echo "--> [5/5] Creating DMG disk image..."
STAGING="${WORK_DIR}/dmg"
mkdir -p "${STAGING}"
cp -R "${APP_BUNDLE}" "${STAGING}/"
ln -s /Applications "${STAGING}/Applications"

# hdiutil intermittently fails with "Resource busy" on CI runners, so retry a few times
MAX_ATTEMPTS=5
for attempt in $(seq 1 "${MAX_ATTEMPTS}"); do
    if hdiutil create -volname "${APP_NAME}" \
        -srcfolder "${STAGING}" \
        -ov -format UDZO \
        "${DMG_PATH}"; then
        break
    fi
    if [ "${attempt}" -eq "${MAX_ATTEMPTS}" ]; then
        echo "hdiutil create failed after ${MAX_ATTEMPTS} attempts" >&2
        exit 1
    fi
    echo "hdiutil create failed (attempt ${attempt}/${MAX_ATTEMPTS}), retrying..." >&2
    sleep $((attempt * 3))
done

if [ -n "${CODESIGN_IDENTITY:-}" ]; then
    codesign --force --timestamp --sign "${CODESIGN_IDENTITY}" "${DMG_PATH}"

    if [ -n "${NOTARY_KEYCHAIN_PROFILE:-}" ]; then
        echo "--> Notarizing with keychain profile '${NOTARY_KEYCHAIN_PROFILE}'..."
        xcrun notarytool submit "${DMG_PATH}" --keychain-profile "${NOTARY_KEYCHAIN_PROFILE}" --wait
        xcrun stapler staple "${DMG_PATH}"
    elif [ -n "${APPLE_ID:-}" ] && [ -n "${APPLE_TEAM_ID:-}" ] && [ -n "${APPLE_APP_PASSWORD:-}" ]; then
        echo "--> Notarizing with Apple ID credentials..."
        xcrun notarytool submit "${DMG_PATH}" --apple-id "${APPLE_ID}" --team-id "${APPLE_TEAM_ID}" \
            --password "${APPLE_APP_PASSWORD}" --wait
        xcrun stapler staple "${DMG_PATH}"
    else
        echo "    Notarization skipped: no NOTARY_KEYCHAIN_PROFILE or APPLE_ID/APPLE_TEAM_ID/APPLE_APP_PASSWORD."
    fi
fi

rm -rf "${PUBLISH_DIR}"

echo "===================================================="
echo "macOS build completed successfully!"
echo "App Bundle: ${APP_BUNDLE}"
echo "DMG Image:  ${DMG_PATH}"
echo "===================================================="
