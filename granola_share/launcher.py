"""The "Granola Share" icon: an app in /Applications or ~/Applications (macOS), a Start Menu shortcut (Windows),
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


SYSTEM_APPS = Path("/Applications")


def mac_app_paths() -> list[Path]:
    return [SYSTEM_APPS / f"{APP_NAME}.app", Path.home() / "Applications" / f"{APP_NAME}.app"]


def mac_app_path() -> Path:
    """/Applications when this account can write there (admins can), so it's in Finder's Applications;
    otherwise ~/Applications. Spotlight and Launchpad find it in either."""
    system, personal = mac_app_paths()
    return system if os.access(SYSTEM_APPS, os.W_OK) else personal


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


NATIVE_EXE = "Granola Share"  # the native app's binary; the script launcher's is granola-share-app


def is_native(app: Path) -> bool:
    return (app / "Contents" / "MacOS" / NATIVE_EXE).is_file()


def native_installed() -> Path | None:
    return next((a for a in mac_app_paths() if is_native(a)), None)


def install_native(url: str, *, log=print, get=None, run=subprocess.run) -> Path | None:
    """Download the native Mac app (a zip from the release) into Applications, replacing any older copy.
    Fetched here rather than in a browser, macOS doesn't quarantine it, so it opens without a warning."""
    import tempfile

    import httpx

    get = get or httpx.get
    try:
        with tempfile.TemporaryDirectory() as tmp:
            zip_path = Path(tmp) / "app.zip"
            r = get(url, follow_redirects=True, timeout=120)
            r.raise_for_status()
            zip_path.write_bytes(r.content)
            unpacked = Path(tmp) / "unpacked"
            p = run(["ditto", "-x", "-k", str(zip_path), str(unpacked)], capture_output=True, text=True)
            new = unpacked / f"{APP_NAME}.app"
            if p.returncode != 0 or not is_native(new):
                log("The Granola Share app in that release didn't unpack; keeping the one you have.")
                return None
            dest = mac_app_path()
            for old in mac_app_paths():
                shutil.rmtree(old, ignore_errors=True)
            dest.parent.mkdir(parents=True, exist_ok=True)
            shutil.move(str(new), str(dest))
            log(f"Installed the Granola Share app in {dest.parent}.")
            return dest
    except Exception as e:
        log(f"Couldn't install the Granola Share app ({e}); the one in Applications still works.")
        return None


def install(home: Path, *, system: str | None = None, python: str | None = None, run=subprocess.run) -> Path | None:
    system = system or platform.system()
    args = _command(home, python)
    try:
        if system == "Darwin":
            native = native_installed()
            if native is not None:  # the real app is there: never swap it for the script launcher
                for other in mac_app_paths():
                    if other != native and not is_native(other):
                        shutil.rmtree(other, ignore_errors=True)
                return native
            app = mac_app_path()
            (app / "Contents" / "MacOS").mkdir(parents=True, exist_ok=True)
            (app / "Contents" / "Info.plist").write_text(render_info_plist(), encoding="utf-8")
            exe = app / "Contents" / "MacOS" / "granola-share-app"
            exe.write_text(render_mac_script(args), encoding="utf-8")
            exe.chmod(0o755)
            for other in mac_app_paths():
                if other != app:
                    shutil.rmtree(other, ignore_errors=True)  # one copy only: 0.2.0 used ~/Applications
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
            for app in mac_app_paths():
                shutil.rmtree(app, ignore_errors=True)
        elif system == "Windows":
            windows_shortcut_path().unlink(missing_ok=True)
        else:
            linux_desktop_path().unlink(missing_ok=True)
    except OSError:
        pass


def installed(system: str | None = None) -> bool:
    system = system or platform.system()
    if system == "Darwin":
        return any(app.exists() for app in mac_app_paths())
    path = windows_shortcut_path() if system == "Windows" else linux_desktop_path()
    return path.exists()
