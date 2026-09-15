#!/bin/sh
# granola-share one-line installer (macOS / Linux).
#   Friend's laptop:  curl -fsSL https://raw.githubusercontent.com/Joseph-Rus/granola_Share/main/install.sh | sh
#   Pool server:      curl -fsSL https://raw.githubusercontent.com/Joseph-Rus/granola_Share/main/install.sh | sh -s -- server
set -e
ROLE="${1:-client}"
REPO="${GRANOLA_SHARE_REPO:-https://github.com/Joseph-Rus/granola_Share}"
HOME_DIR="${GRANOLA_SHARE_HOME:-$HOME/.granola-share}"
APP="$HOME_DIR/app"
VENV="$HOME_DIR/venv"
mkdir -p "$HOME_DIR"

echo "== granola-share installer ($ROLE) =="

if ! command -v uv >/dev/null 2>&1; then
  echo "Installing uv (Python manager)..."
  curl -LsSf https://astral.sh/uv/install.sh | sh
  export PATH="$HOME/.local/bin:$PATH"
fi

if [ -d "$APP/.git" ] && command -v git >/dev/null 2>&1; then
  echo "Updating app..."
  git -C "$APP" pull --ff-only -q || true
elif command -v git >/dev/null 2>&1; then
  echo "Downloading app..."
  git clone -q "$REPO" "$APP"
else
  echo "Downloading app (zip)..."
  rm -rf "$APP" "$HOME_DIR/app.zip" "$HOME_DIR/app-tmp"
  curl -fsSL "$REPO/archive/refs/heads/main.zip" -o "$HOME_DIR/app.zip"
  mkdir -p "$HOME_DIR/app-tmp" && unzip -q "$HOME_DIR/app.zip" -d "$HOME_DIR/app-tmp"
  mv "$HOME_DIR"/app-tmp/* "$APP" && rm -rf "$HOME_DIR/app.zip" "$HOME_DIR/app-tmp"
fi

echo "Setting up Python..."
uv venv --python 3.12 -q "$VENV"
uv pip install -q --python "$VENV/bin/python" -e "$APP"

echo
if [ "$ROLE" = "server" ]; then
  exec "$VENV/bin/granola-share" --home "$HOME_DIR" setup < /dev/tty
else
  exec "$VENV/bin/granola-share" --home "$HOME_DIR" client setup < /dev/tty
fi
