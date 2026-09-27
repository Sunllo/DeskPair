#!/bin/bash
# Builds DeskPair.app, and optionally signs and notarises it. Run on a Mac.
#
#   ./build/macos/package.sh                                  # unsigned, for a local run
#   ./build/macos/package.sh --sign "Developer ID Application: ... (F27AC4MJ76)"
#   ./build/macos/package.sh --sign "..." --notarize deskpair-notary
#   ./build/macos/package.sh --sign "..." --install            # and replace /Applications/DeskPair.app
#
# What it leaves behind is the disk image and nothing else. The .app is built, signed, stapled and put
# inside the image, and then deleted, because an .app under the home directory is one Spotlight indexes
# and Launchpad offers: search for DeskPair and two of them come back, one a build artifact that is
# wiped and rebuilt on the next run. A .metadata_never_index marker in artifacts/ was tried first and
# does not stop it.
#
# macOS keys Screen Recording and Accessibility on the signature. Every distinct identity is a different
# app to TCC, so a machine that has granted an ad-hoc build will be asked again for a signed one, and
# replacing a signed build with another signed by the same identity keeps both grants.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
ARCH=arm64
IDENTITY=""
NOTARY_PROFILE=""
INSTALL=0

while [ $# -gt 0 ]; do
    case "$1" in
        --arch)     ARCH="$2"; shift 2 ;;
        --sign)     IDENTITY="$2"; shift 2 ;;
        --notarize) NOTARY_PROFILE="$2"; shift 2 ;;
        --install)  INSTALL=1; shift ;;
        *) echo "unknown argument: $1" >&2; exit 2 ;;
    esac
done

case "$ARCH" in
    arm64)  RID=osx-arm64 ;;
    x86_64) RID=osx-x64 ;;
    *) echo "--arch takes arm64 or x86_64" >&2; exit 2 ;;
esac

# .NET has no universal binary: the app host is per-architecture and lipo cannot merge two of them into
# something the runtime will load. Two builds is the honest answer, not one that silently runs translated.
VERSION="$(sed -n 's/.*<VersionPrefix>\(.*\)<\/VersionPrefix>.*/\1/p' "$ROOT/Directory.Build.props" | head -1)"
BUILD_NUMBER="${BUILD_NUMBER:-$(git -C "$ROOT" rev-list --count HEAD 2>/dev/null || echo 1)}"
[ -n "$VERSION" ] || { echo "could not read VersionPrefix from Directory.Build.props" >&2; exit 1; }

OUT="$ROOT/artifacts/macos/$ARCH"
APP="$OUT/DeskPair.app"
rm -rf "$OUT"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

echo "==> publishing $RID"
# -f is spelled out because the Desktop project grows a net10.0-windows target when it is built on
# Windows, and a multi-target project refuses to publish without being told which one.
dotnet publish "$ROOT/src/DeskPair.Desktop" -c Release -f net10.0 -r "$RID" --self-contained true \
    -p:DebugType=none -o "$APP/Contents/MacOS"

echo "==> building the native shim"
ARCH="$ARCH" "$ROOT/native/macos/SunlloMacShim/build.sh" "$APP/Contents/MacOS/libSunlloMacShim.dylib"

echo "==> assembling the bundle"
# The XML documentation of every referenced project follows it into the publish output. It is not code
# and not a resource, and codesign will not seal a bundle over a file in Contents/MacOS that is neither.
rm -f "$APP"/Contents/MacOS/*.xml
sed -e "s/@SHORT_VERSION@/$VERSION/" -e "s/@BUILD_VERSION@/$BUILD_NUMBER/" \
    "$ROOT/build/macos/Info.plist" > "$APP/Contents/Info.plist"
printf 'APPL????' > "$APP/Contents/PkgInfo"
if [ -f "$ROOT/build/macos/DeskPair.icns" ]; then
    cp "$ROOT/build/macos/DeskPair.icns" "$APP/Contents/Resources/DeskPair.icns"
else
    echo "    no DeskPair.icns; the bundle will show the generic application icon" >&2
fi

# Everything nested is signed before the bundle is, because signing the bundle seals what is inside it and
# will not seal over an unsigned code object.
#
# Every file, not only the binaries. codesign treats the whole of Contents/MacOS as code, so a bundle whose
# .dll and .dylib files are all signed still fails to seal -- naming, of all things, DeskPair.runtimeconfig.json.
# That is what the layout costs: .NET's app host needs its runtime configuration and its assemblies beside
# it, where Apple expects executables and nothing else.
#
# The main executable is left out on purpose: the bundle signature covers it, and that is the one that
# carries the entitlements.
sign_inner() {
    find "$APP/Contents/MacOS" -type f ! -name DeskPair -print0 |
        while IFS= read -r -d '' f; do
            codesign --force "$@" "$f"
        done
}

if [ -z "$IDENTITY" ]; then
    echo "==> ad-hoc signing (unsigned bundles will not launch on Apple silicon at all)"
    sign_inner --sign - --timestamp=none
    codesign --force --sign - --timestamp=none "$APP"
    echo "built $APP (ad-hoc, not distributable)"
    exit 0
fi

# --options runtime is the hardened runtime, without which notarisation is refused.
echo "==> signing with $IDENTITY"
sign_inner --timestamp --options runtime --sign "$IDENTITY"
codesign --force --timestamp --options runtime \
    --entitlements "$ROOT/build/macos/DeskPair.entitlements" \
    --sign "$IDENTITY" "$APP"

codesign --verify --deep --strict --verbose=2 "$APP"

DMG="$OUT/DeskPair-$VERSION-$ARCH.dmg"

# Replaces /Applications/DeskPair.app, old one first.
#
# Copying over the top leaves whatever the new build no longer has -- a renamed assembly, a dropped
# resource -- and the result is a bundle that is neither version and whose signature no longer seals it.
# So the old one goes first, and the new one is verified in a staging directory before anything is
# removed, because the moment between the two is the only moment there is no DeskPair installed.
install_app() {
    echo "==> installing to /Applications"
    STAGE="$(mktemp -d)/DeskPair.app"
    ditto "$APP" "$STAGE"
    codesign --verify --strict "$STAGE"
    rm -rf /Applications/DeskPair.app
    ditto "$STAGE" /Applications/DeskPair.app
    rm -rf "$(dirname "$STAGE")"
    echo "installed $(/Applications/DeskPair.app/Contents/MacOS/DeskPair --version 2>/dev/null | tail -1)"
}

# The image holds the app, so the loose copy is redundant -- and an .app in the home directory is a
# second DeskPair in Spotlight and Launchpad. Anyone who wants it can open the image.
tidy_up() {
    rm -rf "$APP"
}

build_dmg() {
    echo "==> building $DMG"
    STAGE="$(mktemp -d)"
    cp -R "$APP" "$STAGE/"
    ln -s /Applications "$STAGE/Applications"
    hdiutil create -volname DeskPair -srcfolder "$STAGE" -ov -format UDZO "$DMG" >/dev/null
    rm -rf "$STAGE"
    codesign --force --timestamp --sign "$IDENTITY" "$DMG"
}

if [ -z "$NOTARY_PROFILE" ]; then
    build_dmg
    [ "$INSTALL" = 1 ] && install_app
    tidy_up
    echo "built $DMG (signed, not notarised)"
    echo "Gatekeeper will still refuse it on a machine that has not seen it before."
    exit 0
fi

# The app is notarised and stapled first, before the disk image is built around it.
#
# Stapling the image alone is not enough, and it looks like it is: spctl accepts the app afterwards, so
# the mistake only shows on a machine that is offline or behind something that blocks Apple. The ticket
# lives in whatever was stapled, and what somebody ends up running is the app they dragged out of the
# image -- which, if only the image was stapled, carries no ticket and has to ask Apple at every first
# launch. Measured here: an app copied out of a stapled image reported "does not have a ticket stapled to
# it" while spctl called it accepted.
#
# notarytool will not take a bare bundle, so the app goes up inside a zip that exists only for that.
echo "==> notarising the app"
APPZIP="$OUT/DeskPair-$VERSION-$ARCH-app.zip"
ditto -c -k --keepParent "$APP" "$APPZIP"
xcrun notarytool submit "$APPZIP" --keychain-profile "$NOTARY_PROFILE" --wait
xcrun stapler staple "$APP"
xcrun stapler validate "$APP"
rm -f "$APPZIP"

build_dmg

# And the image as well, so that opening the download is as quiet as running what is inside it.
echo "==> notarising the disk image"
xcrun notarytool submit "$DMG" --keychain-profile "$NOTARY_PROFILE" --wait
xcrun stapler staple "$DMG"
xcrun stapler validate "$DMG"
spctl --assess --type open --context context:primary-signature --verbose=2 "$DMG"

[ "$INSTALL" = 1 ] && install_app
tidy_up
echo "built $DMG (signed, notarised, stapled -- both the app and the image)"
