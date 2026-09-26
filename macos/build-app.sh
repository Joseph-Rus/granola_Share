#!/bin/sh
# Builds the new Study Stash app (the C# engine and its Avalonia windows: menu bar panel, recorder, quick panel,
# library, setup) as a Mac app, while the shipping apps (build.sh) still wrap the Python engine:
#   sh macos/build-app.sh [arm64|x64] [out-dir]      e.g. sh macos/build-app.sh arm64 dist/app
# Makes "Study Stash.app" (self-contained: no .NET install needed), signed ad hoc, and a DMG of it.
# Needs the .NET 10 SDK and the Xcode command line tools.
set -eu

HERE=$(cd "$(dirname "$0")" && pwd)
ARCH=${1:-$(uname -m | sed 's/x86_64/x64/')}
OUT=${2:-"$HERE/../dist/app"}
VERSION=$(sed -n 's/^__version__ = "\(.*\)"/\1/p' "$HERE/../granola_share/__init__.py")
APP="$OUT/Study Stash.app"

rm -rf "$OUT"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
dotnet publish "$HERE/../engine/src/StudyStash.App" -c Release -r "osx-$ARCH" --self-contained true \
  -p:DebugType=None -o "$APP/Contents/MacOS" --nologo -v quiet
# Whisper ships native libraries for every platform: keep only this Mac's.
KEEP=$(echo "macos-$ARCH" | sed 's/x64$/x64/')
for d in "$APP/Contents/MacOS/runtimes/"*; do
  [ "$(basename "$d")" = "$KEEP" ] || rm -rf "$d"
done

xcrun swift "$HERE/make_icon.swift" "$OUT/AppIcon.iconset"
iconutil -c icns "$OUT/AppIcon.iconset" -o "$APP/Contents/Resources/AppIcon.icns"
rm -rf "$OUT/AppIcon.iconset"

PLIST="$APP/Contents/Info.plist"
sed "s/__VERSION__/$VERSION/g" "$HERE/Info.plist" > "$PLIST"
/usr/libexec/PlistBuddy -c "Set :CFBundleExecutable StudyStash" -c "Set :CFBundleIdentifier com.study-stash.app" \
  -c "Add :LSUIElement bool true" \
  -c "Add :NSMicrophoneUsageDescription string 'Study Stash records your lectures so it can write them down. The recording stays on this computer.'" \
  -c "Add :NSAudioCaptureUsageDescription string 'Study Stash can also record what this computer plays, like a lecture on Zoom.'" \
  "$PLIST"
printf 'APPL????' > "$APP/Contents/PkgInfo"
codesign --force --deep --sign - "$APP"

STAGE="$OUT/dmg"
mkdir -p "$STAGE"
cp -R "$APP" "$STAGE/"
ln -s /Applications "$STAGE/Applications"
hdiutil create -quiet -volname "Study Stash" -srcfolder "$STAGE" -ov -format UDZO "$OUT/Study-Stash-$VERSION-$ARCH.dmg"
rm -rf "$STAGE"

echo "Built Study Stash $VERSION ($ARCH):"
du -sh "$APP" "$OUT"/*.dmg
