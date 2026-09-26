#!/bin/sh
# Study Stash installer (Mac only; on Windows, run install.ps1 instead).
#   The computer that keeps the library:
#     curl -fsSL https://raw.githubusercontent.com/Joseph-Rus/study-stash/main/install.sh | sh -s -- library
#   Your laptop (the library's setup shows this line, with its address filled in):
#     curl -fsSL https://raw.githubusercontent.com/Joseph-Rus/study-stash/main/install.sh | sh
#
# Downloads the newest release's Mac installer (a signed DMG holding one universal app, for Apple
# silicon and Intel), checks it against the release's SHA256SUMS.txt when there is one, and drags
# the app into Applications. Safe to rerun: it replaces an older Study Stash.app only once the new
# copy is fully in place.
#
#   STUDYSTASH_DMG=<path>          install this DMG instead of downloading one (development, CI)
#   STUDYSTASH_APPLICATIONS=<dir>  install here instead of /Applications (or ~/Applications)
#   STUDYSTASH_NO_OPEN=1           don't open the app once it's installed
set -eu

if [ "$(uname -s)" != Darwin ]; then
  echo "Study Stash runs on a Mac or a Windows PC. On Windows, run install.ps1 instead." >&2
  exit 1
fi

# The old server/client wording still works; STUDYSTASH_ROLE and a plain argument do too.
ROLE="${1:-${STUDYSTASH_ROLE:-${GRANOLA_SHARE_ROLE:-laptop}}}"
case "$ROLE" in
  library | server) NAME=Study-Stash-Library.dmg ;;
  *) NAME=Study-Stash-Laptop.dmg ;;
esac
SLUG=Joseph-Rus/study-stash

say() { printf '%s\n' "$*"; }
fail() {
  say "Study Stash install failed: $*" >&2
  exit 1
}

# Older releases connected a laptop with an address and password passed as env vars; Study
# Stash's own setup asks for them now, in the app, so this just points you at them.
ADDR="${STUDYSTASH_SERVER:-${GRANOLA_SHARE_SERVER:-}}"
[ -n "$ADDR" ] && say "Type this address in setup: $ADDR"

WORK=$(mktemp -d "${TMPDIR:-/tmp}/study-stash-install.XXXXXX")
cleanup() {
  hdiutil detach "$WORK/mount" -quiet >/dev/null 2>&1 || true
  rm -rf "$WORK"
}
trap cleanup EXIT

DMG="${STUDYSTASH_DMG:-}"
if [ -z "$DMG" ]; then
  command -v curl >/dev/null 2>&1 || fail "curl is missing"
  DMG="$WORK/$NAME"
  say "Downloading $NAME..."
  curl -fsSL -o "$DMG" "https://github.com/$SLUG/releases/latest/download/$NAME" \
    || fail "couldn't download $NAME (https://github.com/$SLUG/releases/latest)"
  SUMS="$WORK/SHA256SUMS.txt"
  if curl -fsSL -o "$SUMS" "https://github.com/$SLUG/releases/latest/download/SHA256SUMS.txt" 2>/dev/null; then
    WANT=$(awk -v n="$NAME" '$2 == n { print $1 }' "$SUMS")
    GOT=$(shasum -a 256 "$DMG" | awk '{ print $1 }')
    [ -n "$WANT" ] && [ "$WANT" = "$GOT" ] || fail "the download didn't match its checksum, so nothing was installed"
  fi
fi

say "Opening $NAME..."
mkdir -p "$WORK/mount"
hdiutil attach -nobrowse -readonly -noautoopen -mountpoint "$WORK/mount" "$DMG" >/dev/null \
  || fail "couldn't open $NAME"
[ -d "$WORK/mount/Study Stash.app" ] || fail "$NAME has no Study Stash.app in it"

DEST="${STUDYSTASH_APPLICATIONS:-}"
if [ -z "$DEST" ]; then
  DEST=/Applications
  [ -w "$DEST" ] || DEST="$HOME/Applications"
fi
mkdir -p "$DEST"
say "Installing into $DEST..."
rm -rf "$DEST/Study Stash.app.new"
ditto "$WORK/mount/Study Stash.app" "$DEST/Study Stash.app.new" || fail "couldn't copy the app into $DEST"
rm -rf "$DEST/Study Stash.app"
mv "$DEST/Study Stash.app.new" "$DEST/Study Stash.app"

say "Installed Study Stash in $DEST."
if [ "${STUDYSTASH_NO_OPEN:-}" != 1 ]; then
  open "$DEST/Study Stash.app"
fi
