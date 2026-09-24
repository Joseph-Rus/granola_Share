#!/bin/sh
# Builds "Study Stash.app" (Apple silicon + Intel), a zip of it for the installer, and a DMG.
#   sh macos/build.sh [version] [out-dir]      e.g. sh macos/build.sh 0.2.4 dist
# Needs the Xcode command line tools (swiftc, iconutil, hdiutil). The app is signed ad hoc: opened from
# a downloaded DMG, macOS asks once (System Settings → Privacy & Security → Open Anyway). The installer
# fetches the zip with curl instead, which macOS doesn't quarantine, so that copy opens straight away.
set -eu

HERE=$(cd "$(dirname "$0")" && pwd)
VERSION=${1:-$(sed -n 's/^__version__ = "\(.*\)"/\1/p' "$HERE/../granola_share/__init__.py")}
OUT=${2:-"$HERE/../dist"}
NAME="Study Stash"
APP="$OUT/$NAME.app"

rm -rf "$OUT"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

for arch in arm64 x86_64; do
  xcrun swiftc -O -swift-version 5 -target "$arch-apple-macos12.0" -framework AppKit -framework WebKit \
    -o "$OUT/app-$arch" "$HERE/StudyStash.swift"
done
lipo -create -output "$APP/Contents/MacOS/$NAME" "$OUT/app-arm64" "$OUT/app-x86_64"
rm -f "$OUT/app-arm64" "$OUT/app-x86_64"

sed "s/__VERSION__/$VERSION/g" "$HERE/Info.plist" > "$APP/Contents/Info.plist"
printf 'APPL????' > "$APP/Contents/PkgInfo"

xcrun swift "$HERE/make_icon.swift" "$OUT/AppIcon.iconset"
iconutil -c icns "$OUT/AppIcon.iconset" -o "$APP/Contents/Resources/AppIcon.icns"
rm -rf "$OUT/AppIcon.iconset"

codesign --force --deep --sign - "$APP"

(cd "$OUT" && ditto -c -k --keepParent "$NAME.app" "Study-Stash-mac.zip")

STAGE="$OUT/dmg"
mkdir -p "$STAGE"
cp -R "$APP" "$STAGE/"
ln -s /Applications "$STAGE/Applications"
hdiutil create -quiet -volname "$NAME" -srcfolder "$STAGE" -ov -format UDZO "$OUT/Study-Stash.dmg"
rm -rf "$STAGE"

echo "Built $VERSION:"
ls -lh "$OUT"
