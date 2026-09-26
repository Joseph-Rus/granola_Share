#!/bin/sh
# Builds "Study Stash.app": one universal bundle (D1: macos/launcher.c hosts .NET in-process, so the same running
# executable works on Apple silicon and Intel) for the laptop and library roles, then both DMGs (D2):
#   sh macos/build-app.sh [out-dir]      default: dist/mac
# Needs the .NET 10 SDK and the Xcode command line tools (clang, lipo, codesign, hdiutil, PlistBuddy, iconutil,
# xcrun swift, vtool, ditto). Publishing both architectures downloads their runtime packs on first use.
set -eu

HERE=$(cd "$(dirname "$0")" && pwd)
ROOT=$(cd "$HERE/.." && pwd)
OUT=${1:-"$ROOT/dist/mac"}
VERSION=$(sed -n 's:.*<StudyStashVersion>\(.*\)</StudyStashVersion>.*:\1:p' "$ROOT/engine/Directory.Build.props")
[ -n "$VERSION" ] || { echo "build-app.sh: no StudyStashVersion in engine/Directory.Build.props" >&2; exit 1; }

APP="$OUT/Study Stash.app"
MACOS="$APP/Contents/MacOS"

rm -rf "$OUT"
mkdir -p "$MACOS" "$APP/Contents/Resources"

# One self-contained publish per architecture; the two trees can't be lipo-merged (System.Private.CoreLib and the
# R2R framework assemblies differ), so both ship whole and macos/launcher.c picks its tree at run time (D1).
for arch in arm64 x64; do
  dotnet publish "$ROOT/engine/src/StudyStash.App" -c Release -r "osx-$arch" --self-contained true \
    -p:DebugType=None -o "$MACOS/$arch" --nologo -v quiet
  # Whisper.net ships every platform's native libraries; each tree keeps only its own.
  for d in "$MACOS/$arch"/runtimes/*; do
    [ "$(basename "$d")" = "macos-$arch" ] || rm -rf "$d"
  done
done
rm -f "$MACOS/x64/ggml-metal.metal"   # no Metal on Intel

# D6: the Whisper model is never bundled.
big=$(find "$OUT" -name '*.bin' -size +1M -o -iname 'ggml-*.bin')
if [ -n "$big" ]; then
  echo "build-app.sh: a model file ended up in the bundle:" >&2
  echo "$big" >&2
  exit 1
fi

# LSMinimumSystemVersion: the highest minos any Mach-O in either tree asks for (today .NET says 12.0, Avalonia's
# native libraries say less, and Whisper's dylibs decide). vtool -show-build reports every arch slice's
# LC_BUILD_VERSION (modern) or LC_VERSION_MIN_MACOSX (older) load command.
tree_minos() {
  find "$1" -type f \( -perm -u+x -o -name '*.dylib' \) 2>/dev/null | while IFS= read -r f; do
    file "$f" | grep -q 'Mach-O' || continue
    vtool -show-build "$f" 2>/dev/null
  done | awk '
    /cmd LC_BUILD_VERSION/     { mode = "build"; next }
    /cmd LC_VERSION_MIN_MACOSX/ { mode = "min"; next }
    mode == "build" && /minos/     { print $2; mode = ""; next }
    mode == "min"   && /^ *version/ { print $2; mode = ""; next }
  '
}
MINOS=$( { tree_minos "$MACOS/arm64"; tree_minos "$MACOS/x64"; } | sort -t. -k1,1n -k2,2n | tail -1)
[ -n "$MINOS" ] || MINOS=12.0

# The launcher: the one universal binary in the bundle, built for both trees' minimum OS (D1).
clang -O2 -Wall -Werror -arch arm64 -arch x86_64 -mmacosx-version-min="$MINOS" -o "$MACOS/StudyStash" "$HERE/launcher.c"

# Icon.
xcrun swift "$HERE/make_icon.swift" "$OUT/AppIcon.iconset"
iconutil -c icns "$OUT/AppIcon.iconset" -o "$APP/Contents/Resources/AppIcon.icns"
rm -rf "$OUT/AppIcon.iconset"

# Info.plist (laptop role by default), PkgInfo.
sed -e "s/__VERSION__/$VERSION/g" -e "s/__MINOS__/$MINOS/g" -e "s/__ROLE__/laptop/g" \
  "$HERE/Info.plist" > "$APP/Contents/Info.plist"
printf 'APPL????' > "$APP/Contents/PkgInfo"

# Sign inside-out (D1): every file in each per-arch tree first, ad hoc with the hardened runtime, then the launcher,
# then the bundle itself with its entitlements. `codesign --deep` alone can skip a Mach-O with no executable bit, so
# every one is found and signed explicitly — and not only the ones `file` calls Mach-O: some of .NET's own managed
# assemblies (the runtime's R2R-compiled framework dlls) embed native code for their target architecture inside what
# is otherwise a PE container, which recent macOS's --deep verification also treats as nested code needing its own
# signature. `codesign` signs those too (falling back to its generic, non-Mach-O signing format), so the simplest
# correct rule is to sign every regular file, not just the ones that look like a Mach-O from the outside.
sign_tree() {
  find "$1" -type f | while IFS= read -r f; do
    codesign --force --options runtime --sign - "$f"
  done
}
sign_tree "$MACOS/arm64"
sign_tree "$MACOS/x64"
codesign --force --options runtime --sign - "$MACOS/StudyStash"
codesign --force --options runtime --entitlements "$HERE/StudyStash.entitlements" --sign - "$APP"
codesign --verify --deep --strict --verbose=2 "$APP"

echo "Built Study Stash $VERSION (universal, min $MINOS):"
du -sh "$APP"

# The library copy: the same signed app, its role flipped. Editing Info.plist invalidates the outer seal, so the
# bundle (not the Mach-Os inside, untouched) is re-signed.
LIBAPP="$OUT/library/Study Stash.app"
mkdir -p "$OUT/library"
ditto "$APP" "$LIBAPP"
/usr/libexec/PlistBuddy -c "Set :StudyStashRole library" "$LIBAPP/Contents/Info.plist"
codesign --force --options runtime --entitlements "$HERE/StudyStash.entitlements" --sign - "$LIBAPP"
codesign --verify --deep --strict --verbose=2 "$LIBAPP"
echo "Library copy:"
du -sh "$LIBAPP"

# Both DMGs (D2): a stage folder per role with the app plus a link to /Applications.
stage_dmg() {
  app_path=$1
  role_name=$2
  volname=$3
  stage="$OUT/stage-$role_name"
  rm -rf "$stage"
  mkdir -p "$stage"
  ditto "$app_path" "$stage/Study Stash.app"
  ln -s /Applications "$stage/Applications"
  hdiutil create -quiet -volname "$volname" -srcfolder "$stage" -ov -format UDZO "$OUT/Study-Stash-$role_name.dmg"
  rm -rf "$stage"
}
stage_dmg "$APP" "Laptop" "Study Stash"
stage_dmg "$LIBAPP" "Library" "Study Stash Library"

echo "DMGs:"
du -sh "$OUT"/*.dmg
