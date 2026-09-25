"""The icon files the Python package ships, for Windows, Linux, and web pages (the Mac app draws its own).

    uv run --with pillow python macos/icon_assets.py

Writes granola_share/assets/: study-stash.ico (the Start Menu shortcut, and each page's favicon, which
Edge and Chrome also show on an app window's taskbar button), icon.png (Linux menus, and a sharp icon
for browsers), and apple-touch-icon.png (the library on an iPhone's home screen).
"""

from __future__ import annotations

import subprocess
import sys
import tempfile
from pathlib import Path

from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / "granola_share" / "assets"


def main() -> None:
    with tempfile.TemporaryDirectory() as tmp:
        tiles = Path(tmp) / "icon.tiles"
        subprocess.run(["xcrun", "swift", str(ROOT / "macos" / "make_icon.swift"), str(tiles)], check=True)
        sizes = [16, 24, 32, 48, 64, 128, 256]
        # Each size is drawn on its own (the small ones have fewer, thicker lines), so none is a blurry resize.
        images = [Image.open(tiles / f"icon-{px}.png").convert("RGBA") for px in sizes]
        OUT.mkdir(parents=True, exist_ok=True)
        images[-1].save(OUT / "study-stash.ico", sizes=[(px, px) for px in sizes], append_images=images[:-1])
        Image.open(tiles / "icon-256.png").save(OUT / "icon.png", optimize=True)
        Image.open(tiles / "icon-180.png").save(OUT / "apple-touch-icon.png", optimize=True)
    for f in sorted(OUT.iterdir()):
        print(f"wrote {f.relative_to(ROOT)} ({f.stat().st_size // 1024} KB)")


if __name__ == "__main__":
    sys.exit(main())
