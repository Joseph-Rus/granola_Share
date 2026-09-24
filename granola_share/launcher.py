"""The "Granola Share" icon: an app in ~/Applications (macOS), a Start Menu shortcut (Windows),
or a menu entry (Linux). Opening it runs `granola-share client open`, which starts the
background service if needed and shows its page in the browser.
"""

from __future__ import annotations

import os
import platform
import shutil
import subprocess
import sys
from pathlib import Path
from xml.sax.saxutils import escape

APP_NAME = "Granola Share"
BUNDLE_ID = "com.granola-share.app"


def _command(home: Path, python: str | None = None) -> list[str]:
    return [python or sys.executable, "-m", "granola_share.cli", "--home", str(home), "client", "open"]


def mac_app_path() -> Path:
    return Path.home() / "Applications" / f"{APP_NAME}.app"


def windows_shortcut_path() -> Path:
    return (Path(os.environ.get("APPDATA", str(Path.home()))) / "Microsoft" / "Windows" / "Start Menu" / "Programs"
            / f"{APP_NAME}.lnk")


def linux_desktop_path() -> Path:
    return Path.home() / ".local" / "share" / "applications" / "granola-share.desktop"


def render_info_plist() -> str:
    return f"""<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key><string>{escape(APP_NAME)}</string>
    <key>CFBundleDisplayName</key><string>{escape(APP_NAME)}</string>
    <key>CFBundleIdentifier</key><string>{BUNDLE_ID}</string>
    <key>CFBundleExecutable</key><string>granola-share-app</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleShortVersionString</key><string>1.0</string>
    <key>LSMinimumSystemVersion</key><string>11.0</string>
    <key>LSUIElement</key><true/>
</dict>
</plist>
"""


def render_mac_script(args: list[str]) -> str:
    quoted = " ".join("'" + a.replace("'", "'\\''") + "'" for a in args)
    return f"#!/bin/sh\n# Opens the Granola Share page (starting its background service if needed).\nexec {quoted}\n"


def install(home: Path, *, system: str | None = None, python: str | None = None, run=subprocess.run) -> Path | None:
    system = system or platform.system()
    args = _command(home, python)
    try:
        if system == "Darwin":
            app = mac_app_path()
            (app / "Contents" / "MacOS").mkdir(parents=True, exist_ok=True)
            (app / "Contents" / "Info.plist").write_text(render_info_plist(), encoding="utf-8")
            exe = app / "Contents" / "MacOS" / "granola-share-app"
            exe.write_text(render_mac_script(args), encoding="utf-8")
            exe.chmod(0o755)
            return app
        if system == "Windows":
            link = windows_shortcut_path()
            link.parent.mkdir(parents=True, exist_ok=True)
            exe = Path(args[0])
            target = exe.with_name("pythonw.exe") if exe.with_name("pythonw.exe").exists() else exe
            arguments = subprocess.list2cmdline(args[1:]).replace("'", "''")
            ps = (f"$s=(New-Object -ComObject WScript.Shell).CreateShortcut('{str(link).replace(chr(39), chr(39) * 2)}');"
                  f"$s.TargetPath='{str(target).replace(chr(39), chr(39) * 2)}';$s.Arguments='{arguments}';"
                  f"$s.Description='Open Granola Share';$s.Save()")
            run(["powershell", "-NoProfile", "-Command", ps], capture_output=True, text=True)
            return link
        desktop = linux_desktop_path()
        desktop.parent.mkdir(parents=True, exist_ok=True)
        exec_line = " ".join(f'"{a}"' if " " in a else a for a in args)
        desktop.write_text(f"[Desktop Entry]\nType=Application\nName={APP_NAME}\n"
                           f"Comment=Send your Granola lectures to your library\nExec={exec_line}\n"
                           "Terminal=false\nCategories=Office;Education;\n", encoding="utf-8")
        return desktop
    except Exception:
        return None


def uninstall(system: str | None = None) -> None:
    system = system or platform.system()
    try:
        if system == "Darwin":
            shutil.rmtree(mac_app_path(), ignore_errors=True)
        elif system == "Windows":
            windows_shortcut_path().unlink(missing_ok=True)
        else:
            linux_desktop_path().unlink(missing_ok=True)
    except OSError:
        pass


def installed(system: str | None = None) -> bool:
    system = system or platform.system()
    path = {"Darwin": mac_app_path(), "Windows": windows_shortcut_path()}.get(system, linux_desktop_path())
    return path.exists()
