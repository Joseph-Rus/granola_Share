#!/bin/sh
# Builds the two Mac apps (Apple silicon + Intel) from one binary, a zip of each for the installer and
# auto-update, and a DMG of each for people who download them by hand:
#   Study Stash.app          the laptop's: Study-Stash-Laptop.dmg, Study-Stash-mac.zip
#   Study Stash Library.app  the library computer's: Study-Stash-Library.dmg, Study-Stash-Library-mac.zip
#   sh macos/build.sh [version] [out-dir]      e.g. sh macos/build.sh 0.4.1 dist
# Needs the Xcode command line tools (swiftc, iconutil, hdiutil). The apps are signed ad hoc: opened from
# a downloaded DMG, macOS asks once (System Settings → Privacy & Security → Open Anyway). The installer
# fetches the zip with curl instead, which macOS doesn't quarantine, so that copy opens straight away.
set -eu

HERE=$(cd "$(dirname "$0")" && pwd)
VERSION=${1:-$(sed -n 's/^__version__ = "\(.*\)"/\1/p' "$HERE/../granola_share/__init__.py")}
OUT=${2:-"$HERE/../dist"}
EXE="Study Stash"

rm -rf "$OUT"
mkdir -p "$OUT"
for arch in arm64 x86_64; do
  xcrun swiftc -O -swift-version 5 -target "$arch-apple-macos12.0" -framework AppKit -framework WebKit \
    -o "$OUT/app-$arch" "$HERE/StudyStash.swift"
done
lipo -create -output "$OUT/$EXE" "$OUT/app-arm64" "$OUT/app-x86_64"
rm -f "$OUT/app-arm64" "$OUT/app-x86_64"
xcrun swift "$HERE/make_icon.swift" "$OUT/AppIcon.iconset"
iconutil -c icns "$OUT/AppIcon.iconset" -o "$OUT/AppIcon.icns"
rm -rf "$OUT/AppIcon.iconset"

# make_app <name> <bundle id> <role> <zip> <dmg>
make_app() {
  APP="$OUT/$1.app"
  mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
  cp "$OUT/$EXE" "$APP/Contents/MacOS/$EXE"
  cp "$OUT/AppIcon.icns" "$APP/Contents/Resources/AppIcon.icns"
  sed "s/__VERSION__/$VERSION/g" "$HERE/Info.plist" > "$APP/Contents/Info.plist"
  /usr/libexec/PlistBuddy -c "Set :CFBundleName $1" -c "Set :CFBundleDisplayName $1" \
    -c "Set :CFBundleIdentifier $2" -c "Add :StudyStashRole string $3" "$APP/Contents/Info.plist"
  printf 'APPL????' > "$APP/Contents/PkgInfo"
  codesign --force --deep --sign - "$APP"
  (cd "$OUT" && ditto -c -k --keepParent "$1.app" "$4")
  STAGE="$OUT/dmg"
  rm -rf "$STAGE"
  mkdir -p "$STAGE"
  cp -R "$APP" "$STAGE/"
  ln -s /Applications "$STAGE/Applications"
  hdiutil create -quiet -volname "$1" -srcfolder "$STAGE" -ov -format UDZO "$OUT/$5"
  rm -rf "$STAGE"
}
make_app "Study Stash" com.granola-share.mac laptop Study-Stash-mac.zip Study-Stash-Laptop.dmg
make_app "Study Stash Library" com.granola-share.library library Study-Stash-Library-mac.zip Study-Stash-Library.dmg
rm -f "$OUT/$EXE" "$OUT/AppIcon.icns"

echo "Built $VERSION:"
ls -lh "$OUT"
