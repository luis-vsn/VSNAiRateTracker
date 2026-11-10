#!/usr/bin/env bash
# VSN AI Rate Tracker - macOS App Bundle & DMG Builder Script
set -euo pipefail

CONFIGURATION="${1:-Release}"
VERSION="${2:-0.0.1}"
RUNTIME="${3:-osx-arm64}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "${SCRIPT_DIR}/../.." && pwd)"
PROJECT_PATH="${ROOT_DIR}/VSNAiRateTracker/VSNAiRateTracker.csproj"
DIST_DIR="${ROOT_DIR}/packaging/dist"
APP_BUNDLE="${DIST_DIR}/VSN AI Rate Tracker.app"
DMG_PATH="${DIST_DIR}/VSNAiRateTracker-${VERSION}-${RUNTIME}.dmg"

echo "===================================================="
echo "Building VSN AI Rate Tracker macOS Bundle & DMG (${RUNTIME})"
echo "===================================================="

mkdir -p "${DIST_DIR}"
rm -rf "${APP_BUNDLE}" "${DMG_PATH}"

# 1. Publish self-contained executable
echo "--> Publishing self-contained executable (${RUNTIME})..."
dotnet publish "${PROJECT_PATH}" \
    -c "${CONFIGURATION}" \
    -r "${RUNTIME}" \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:Version="${VERSION}" \
    -o "${DIST_DIR}/macos-publish"

# 2. Assemble .app bundle structure
echo "--> Assembling .app bundle structure..."
mkdir -p "${APP_BUNDLE}/Contents/MacOS"
mkdir -p "${APP_BUNDLE}/Contents/Resources"

cp -R "${DIST_DIR}/macos-publish/"* "${APP_BUNDLE}/Contents/MacOS/"
chmod +x "${APP_BUNDLE}/Contents/MacOS/VSNAiRateTracker"

# Copy Info.plist
if [ -f "${ROOT_DIR}/VSNAiRateTracker/Info.plist" ]; then
    cp "${ROOT_DIR}/VSNAiRateTracker/Info.plist" "${APP_BUNDLE}/Contents/Info.plist"
fi

# 3. Create DMG disk image
echo "--> Creating DMG disk image with hdiutil..."
TMP_DMG_DIR="$(mktemp -d)"
cp -R "${APP_BUNDLE}" "${TMP_DMG_DIR}/"
ln -s /Applications "${TMP_DMG_DIR}/Applications"

# hdiutil intermittently fails with "Resource busy" on CI runners, so retry a few times
MAX_ATTEMPTS=5
for attempt in $(seq 1 "${MAX_ATTEMPTS}"); do
    if hdiutil create -volname "VSN AI Rate Tracker" \
        -srcfolder "${TMP_DMG_DIR}" \
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

rm -rf "${TMP_DMG_DIR}" "${DIST_DIR}/macos-publish"

echo "===================================================="
echo "macOS build completed successfully!"
echo "App Bundle: ${APP_BUNDLE}"
echo "DMG Image:  ${DMG_PATH}"
echo "===================================================="

