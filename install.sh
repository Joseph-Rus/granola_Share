#!/bin/sh
# granola-share one-line installer (macOS / Linux).
#   The Mac mini (keeps the library):
#     curl -fsSL https://raw.githubusercontent.com/Joseph-Rus/granola_Share/main/install.sh | sh -s -- server
#   Your laptop (the Mac mini's setup prints this line with the address and password filled in):
#     curl -fsSL .../install.sh | GRANOLA_SHARE_SERVER=http://mac-mini:8787 GRANOLA_SHARE_KEY=pw sh
# Anything after the role goes to the setup wizard, e.g. `sh -s -- server --yes --pool-name Fall`.
#
# Installs the `granola-share` command with uv (no admin rights, no git, its own Python),
# from the newest release. Safe to rerun: it updates in place and setup keeps your answers.
#
#   GRANOLA_SHARE_VERSION=v0.2.0 | main   what to install (default: newest release, else main)
#   GRANOLA_SHARE_SRC=/path/to/checkout   install from a local copy (development, CI)
#   GRANOLA_SHARE_NO_SETUP=1              install only, skip setup
#   GRANOLA_SHARE_TERMINAL=1              laptop: answer setup in the terminal instead of the browser
#   GRANOLA_SHARE_HOME=...                data directory (default ~/.granola-share)
set -e

ROLE=client
case "${1:-}" in
  server|client) ROLE="$1"; shift ;;
esac
SLUG="Joseph-Rus/granola_Share"
HOME_DIR="${GRANOLA_SHARE_HOME:-$HOME/.granola-share}"
LOG="$HOME_DIR/install.log"
ORIG_PATH="$PATH"

say() { printf '%s\n' "$*"; }
fail() {
  say ""
  say "granola-share install failed: $*"
  say "Rerunning the same command is safe. Help: https://github.com/$SLUG#troubleshooting"
  exit 1
}

say "== granola-share installer ($ROLE) =="
command -v curl >/dev/null 2>&1 || fail "curl is missing"
mkdir -p "$HOME_DIR" || fail "can't create $HOME_DIR"

# 1. uv: installs Python and the app into your home folder.
export PATH="$HOME/.local/bin:$HOME/.cargo/bin:$PATH"
if ! command -v uv >/dev/null 2>&1; then
  say "Installing uv (Python manager)..."
  curl -LsSf https://astral.sh/uv/install.sh | sh >"$LOG" 2>&1 || { tail -n 15 "$LOG"; fail "could not install uv"; }
  command -v uv >/dev/null 2>&1 || fail "uv was installed but can't be found; open a new terminal and rerun"
fi

# 2. Which version.
if [ -n "${GRANOLA_SHARE_SRC:-}" ]; then
  SRC="$GRANOLA_SHARE_SRC"
  WHAT="local copy at $SRC"
else
  REF="${GRANOLA_SHARE_VERSION:-}"
  if [ -z "$REF" ]; then
    REF=$(curl -fsSL "https://api.github.com/repos/$SLUG/releases/latest" 2>/dev/null \
          | sed -n 's/.*"tag_name": *"\([^"]*\)".*/\1/p' | head -n 1) || REF=""
  fi
  if [ -z "$REF" ] || [ "$REF" = "main" ]; then
    SRC="granola-share @ https://github.com/$SLUG/archive/refs/heads/main.tar.gz"
    WHAT="latest main"
  else
    SRC="granola-share @ https://github.com/$SLUG/archive/refs/tags/$REF.tar.gz"
    WHAT="$REF"
  fi
fi

# 3. The command itself. A uv-managed Python means a Homebrew or system upgrade can't break it.
say "Installing granola-share ($WHAT)..."
UV_PYTHON_PREFERENCE=only-managed uv tool install --force --python 3.12 \
  --reinstall-package granola-share --refresh-package granola-share "$SRC" >"$LOG" 2>&1 \
  || { tail -n 20 "$LOG"; fail "uv could not install granola-share (full log: $LOG)"; }
BIN_DIR="$(uv tool dir --bin 2>/dev/null || printf '%s' "$HOME/.local/bin")"
BIN="$BIN_DIR/granola-share"
[ -x "$BIN" ] || fail "installed, but $BIN is missing (log: $LOG)"
say "Installed $("$BIN" --version)."
case ":$ORIG_PATH:" in
  *":$BIN_DIR:"*) ;;
  *) uv tool update-shell >/dev/null 2>&1 || true  # adds $BIN_DIR to PATH for new terminals
     say "The granola-share command works in new terminal windows (it lives in $BIN_DIR)." ;;
esac

# 4. Setup.
if [ "${GRANOLA_SHARE_NO_SETUP:-}" = "1" ]; then
  say "Skipping setup. Next: granola-share setup (the Mac mini) or granola-share client open (your laptop)."
  exit 0
fi
say ""
if [ "$ROLE" = client ] && [ -z "${GRANOLA_SHARE_TERMINAL:-}" ] && [ "$#" -eq 0 ]; then
  # The laptop sets up in the Granola Share app (on a Mac; elsewhere the browser): this starts the
  # background service, adds the app, and opens its setup page with the library's address and password.
  "$BIN" --home "$HOME_DIR" client open --install || fail "Granola Share didn't start (log: $HOME_DIR/logs/client.log)"
  say "Setup continues in the Granola Share window. Later, open Granola Share from your Applications folder."
  exit 0
fi
if [ "$ROLE" = server ]; then set -- setup "$@"; else set -- client setup "$@"; fi
# `curl | sh` feeds this script on stdin, so the wizard reads answers from the terminal instead.
if (: </dev/tty) 2>/dev/null; then
  exec "$BIN" --home "$HOME_DIR" "$@" </dev/tty
fi
exec "$BIN" --home "$HOME_DIR" "$@"
