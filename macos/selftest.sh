#!/bin/sh
# Proves a built "Study Stash.app" actually works (D5): its windows open, the pretend microphone records, and
# Whisper (Metal on Apple silicon, from inside the bundle) hears the speech. Also what CI runs on Windows, against
# StudyStash.exe directly, to the same contract.
#   sh macos/selftest.sh "<path>/Study Stash.app" [out-dir]
# STUDYSTASH_WHISPER_MODEL must already be set, to a small model file (the tiny one, so this stays fast and needs no
# download). STUDYSTASH_ARCH=x86_64 runs the bundle's Intel tree through Rosetta instead of natively.
set -eu

APP=${1:?"usage: sh macos/selftest.sh \"<path>/Study Stash.app\" [out-dir]"}
HERE=$(cd "$(dirname "$0")" && pwd)
OUT=${2:-"${TMPDIR:-/tmp}/study-stash-selftest-$(date +%s)"}
: "${STUDYSTASH_WHISPER_MODEL:?"selftest.sh: set STUDYSTASH_WHISPER_MODEL to a Whisper model file (the tiny one, for speed)"}"

SPEECH="$HERE/../engine/tests/StudyStash.Core.Tests/Fixtures/speech.wav"
[ -f "$SPEECH" ] || { echo "selftest.sh: can't find speech.wav at $SPEECH" >&2; exit 1; }
BIN="$APP/Contents/MacOS/StudyStash"
[ -x "$BIN" ] || { echo "selftest.sh: no program at $BIN" >&2; exit 1; }

rm -rf "$OUT"
HOME_DIR="$OUT/home"
mkdir -p "$HOME_DIR"
rm -f "$OUT/selftest.txt"

# A clean environment: no DOTNET_ROOT, so the run proves the self-contained bundle needs no .NET install on the Mac.
run_app() {
  env -u DOTNET_ROOT \
    STUDYSTASH_SELFTEST="$OUT" \
    STUDYSTASH_MIC_FILE="$SPEECH" \
    STUDYSTASH_MIC_SPEED="${STUDYSTASH_MIC_SPEED:-4}" \
    STUDYSTASH_WHISPER_MODEL="$STUDYSTASH_WHISPER_MODEL" \
    STUDYSTASH_MODEL_FILE="$STUDYSTASH_WHISPER_MODEL" \
    "$@" --home "$HOME_DIR"
}

if [ "${STUDYSTASH_ARCH:-}" = "x86_64" ]; then
  run_app arch -x86_64 "$BIN" &
else
  run_app "$BIN" &
fi
pid=$!

seconds=0
while kill -0 "$pid" 2>/dev/null; do
  seconds=$((seconds + 1))
  if [ "$seconds" -ge 480 ]; then
    kill -9 "$pid" 2>/dev/null || true
    echo "selftest.sh: Study Stash didn't exit within 480 s" >&2
    exit 1
  fi
  sleep 1
done
code=0
wait "$pid" || code=$?

TXT="$OUT/selftest.txt"
[ -f "$TXT" ] || { echo "selftest.sh: Study Stash exited ($code) without writing selftest.txt" >&2; exit 1; }
cat "$TXT"

last=$(tail -n 1 "$TXT")
ok=true
[ "$code" = 0 ] || ok=false
[ "$last" = "ok" ] || ok=false
grep -qiE "heard nothing|no model|recording didn't start|FAILED" "$TXT" && ok=false

if [ "$ok" = true ]; then
  echo "selftest.sh: passed ($OUT)"
else
  echo "selftest.sh: failed (exit=$code, last line: $last)" >&2
  exit 1
fi
