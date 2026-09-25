"""Updates: find the newest release on GitHub, install it with uv, restart the background service.

Releases are cut by CI (.github/workflows/ci.yml) when the version in granola_share/__init__.py
changes on main and every test passes on macOS, Linux, and Windows. Installed copies pick them
up with `granola-share update`, or on their own when auto_update is on (the default).
"""

from __future__ import annotations

import os
import platform
import re
import shutil
import subprocess
import sys
import threading
import time
from dataclasses import dataclass
from pathlib import Path

import httpx

from . import __version__, autostart

REPO_SLUG = "Joseph-Rus/study-stash"
LATEST_API = f"https://api.github.com/repos/{REPO_SLUG}/releases/latest"
MAC_APP_ASSET = "Study-Stash-mac.zip"  # the native Mac app (macos/build.sh), attached to each release by CI
WINDOWS_APP_ASSET = "Study-Stash-windows.zip"  # the Windows app (windows/build.ps1), likewise
FIRST_CHECK_AFTER = 10 * 60
CHECK_EVERY = 6 * 3600
LOCK_STALE_AFTER = 20 * 60


@dataclass
class Release:
    tag: str
    version: tuple[int, ...]
    url: str  # source archive uv installs from
    page: str  # release notes
    mac_app: str = ""  # download URL of the native Mac app, when the release has one
    windows_app: str = ""  # and of the Windows app


def parse_version(v: str) -> tuple[int, ...]:
    nums = re.findall(r"\d+", (v or "").split("+")[0])
    return tuple(int(n) for n in nums[:3]) or (0,)


def archive_url(ref: str, branch: bool = False) -> str:
    kind = "heads" if branch else "tags"
    return f"https://github.com/{REPO_SLUG}/archive/refs/{kind}/{ref}.tar.gz"


def latest_release(get=httpx.get) -> Release | None:
    """The newest published release, or None when there is none yet."""
    r = get(LATEST_API, headers={"Accept": "application/vnd.github+json"}, timeout=10, follow_redirects=True)
    if r.status_code == 404:
        return None
    r.raise_for_status()
    data = r.json()
    tag = str(data["tag_name"])
    assets = {str(a.get("name")): str(a.get("browser_download_url") or "") for a in data.get("assets") or []}
    return Release(tag, parse_version(tag), archive_url(tag), str(data.get("html_url") or ""),
                   assets.get(MAC_APP_ASSET, ""), assets.get(WINDOWS_APP_ASSET, ""))


_cache: dict = {"at": 0.0, "release": None}


def cached_latest(max_age: float = 3600) -> Release | None:
    """For the web UI: at most one GitHub call an hour, and never an exception."""
    if time.time() - _cache["at"] > max_age:
        try:
            _cache["release"] = latest_release()
        except Exception:
            pass
        _cache["at"] = time.time()
    return _cache["release"]


def is_newer(release: Release | None, current: str = __version__) -> bool:
    return bool(release) and release.version > parse_version(current)


def installed_version() -> str:
    """The version on disk right now (may be newer than the code this process loaded)."""
    try:
        from importlib.metadata import version

        return version("granola-share")
    except Exception:
        return __version__


def install_kind() -> str:
    """'tool' (installed by install.sh / install.ps1), 'checkout' (pip -e from a git clone), or 'pip'."""
    if (Path(sys.prefix) / "uv-receipt.toml").exists():
        return "tool"
    if "site-packages" not in Path(__file__).resolve().parts:
        return "checkout"
    return "pip"


def find_uv() -> str | None:
    home = Path.home()
    for c in (shutil.which("uv"), home / ".local/bin/uv", home / ".local/bin/uv.exe",
              home / ".cargo/bin/uv", home / ".cargo/bin/uv.exe"):
        if c and Path(c).exists():
            return str(c)
    return None


def install_command(uv: str, url: str) -> list[str]:
    return [uv, "tool", "install", "--force", "--python", "3.12", "--reinstall-package", "granola-share",
            f"granola-share @ {url}"]


def install_env() -> dict:
    # A uv-managed Python, so a Homebrew/system Python upgrade can never break the installed tool.
    return {**os.environ, "UV_PYTHON_PREFERENCE": "only-managed"}


def why_not_updatable() -> str | None:
    kind = install_kind()
    if kind == "checkout":
        return f"This copy runs from a source checkout ({Path(__file__).resolve().parents[1]}). Update it with `git pull`."
    if kind == "pip":
        return "This copy was installed with pip, not the installer. Reinstall with the one-line installer to get updates."
    if not find_uv():
        return "uv is missing, so updates cannot be installed. Rerun the one-line installer."
    return None


def cleanup_legacy(home: Path, log=print) -> bool:
    """0.1 installed into <home>/venv and <home>/app. Remove them once nothing runs from there."""
    old = [home / "venv", home / "app"]
    if not any(p.exists() for p in old):
        return False
    here = Path(sys.prefix).resolve()
    if any(here == p.resolve() or p.resolve() in here.parents for p in old if p.exists()):
        return False  # this very process runs from the old install
    for role in autostart.ROLES:
        path = autostart.service_path(role)
        try:
            if path.exists() and str(home / "venv") in path.read_text(errors="ignore"):
                return False  # a background service still starts from it
        except OSError:
            return False
    for p in old:
        shutil.rmtree(p, ignore_errors=True)
    log(f"Removed the old install from {home / 'venv'} and {home / 'app'}.")
    return True


# --- applying an update --------------------------------------------------------------

def _windows_script(home: Path, uv: str, url: str, roles: list[str]) -> Path:
    """Windows locks running files, so a detached helper waits for us to exit, installs, and restarts."""
    log = home / "logs" / "update.log"
    stop = ("Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -like '*granola_share.cli*' } | "
            "ForEach-Object { Stop-Process -Id $_.ProcessId -Force }")
    lines = ["@echo off", "timeout /t 5 /nobreak >nul",
             f'powershell -NoProfile -Command "{stop}" >> "{log}" 2>&1',
             "timeout /t 2 /nobreak >nul",
             "set UV_PYTHON_PREFERENCE=only-managed",
             subprocess.list2cmdline(install_command(uv, url)) + f' >> "{log}" 2>&1']
    lines += [f'call "{autostart.startup_cmd_path(role)}"' for role in roles]
    script = home / "update.cmd"
    script.write_text("\r\n".join(lines) + "\r\n")
    return script


def _spawn_detached(args: list[str]) -> None:
    flags = 0x00000008 | 0x00000200 | 0x08000000  # DETACHED_PROCESS | NEW_PROCESS_GROUP | NO_WINDOW
    subprocess.Popen(args, creationflags=flags, close_fds=True)


def apply(release: Release, home: Path, *, log=print, run=subprocess.run, restart_services: bool = True) -> bool:
    """Install `release`. Background services are restarted onto it. On Windows this hands off to a
    helper and returns before the install happens."""
    problem = why_not_updatable()
    if problem:
        log(problem)
        return False
    uv = find_uv()
    roles = autostart.installed_roles()
    (home / "logs").mkdir(parents=True, exist_ok=True)
    if platform.system() == "Windows":
        from . import launcher

        if release.windows_app and launcher.windows_app_exe():  # the Study Stash app updates along with everything else
            launcher.install_windows_app(release.windows_app, log=log)
        script = _windows_script(home, uv, release.url, roles if restart_services else [])
        _spawn_detached(["cmd", "/c", str(script)])
        log(f"Installing {release.tag} in the background (log: {home / 'logs' / 'update.log'}).")
        return True
    log(f"Installing {release.tag}...")
    p = run(install_command(uv, release.url), env=install_env(), capture_output=True, text=True)
    if p.returncode != 0:
        log(f"Install failed:\n{(p.stderr or p.stdout or '').strip()[-2000:]}")
        return False
    log(f"Installed {release.tag}.")
    if platform.system() == "Darwin" and release.mac_app:
        from . import launcher

        if launcher.native_installed():  # the Study Stash app updates along with everything else
            launcher.install_native(release.mac_app, log=log)
    if restart_services:
        for role in roles:
            autostart.restart(role)
            log(f"Restarted the {role} service.")
    return True


# --- the background checker -------------------------------------------------------

class _Lock:
    """One updater at a time when the server and client run on the same machine."""

    def __init__(self, path: Path):
        self.path = path

    def acquire(self) -> bool:
        try:
            if self.path.exists() and time.time() - self.path.stat().st_mtime > LOCK_STALE_AFTER:
                self.path.unlink()
            fd = os.open(self.path, os.O_CREAT | os.O_EXCL | os.O_WRONLY)
            os.write(fd, str(os.getpid()).encode())
            os.close(fd)
            return True
        except OSError:
            return False

    def release(self) -> None:
        try:
            self.path.unlink()
        except OSError:
            pass


def check_and_update(home: Path, *, log=print, supervised: bool | None = None, latest=latest_release,
                     do_apply=apply, exit_fn=os._exit) -> str:
    """One auto-update round. Returns what happened (for logs and tests)."""
    supervised = os.environ.get("GRANOLA_SHARE_SERVICE") == "1" if supervised is None else supervised
    try:
        rel = latest()
    except Exception as e:
        return f"check failed: {e}"
    if not is_newer(rel):
        return "up to date"
    if not supervised or why_not_updatable():
        log(f"[update] {rel.tag} is available: run `granola-share update`.")
        return "available"
    lock = _Lock(home / "update.lock")
    if not lock.acquire():
        return "another update is running"
    try:
        windows = platform.system() == "Windows"
        if windows or parse_version(installed_version()) < rel.version:
            # Elsewhere launchd/systemd restart us after we exit; on Windows the helper must do it.
            if not do_apply(rel, home, log=lambda s: log(f"[update] {s}"), restart_services=windows):
                return "install failed"
        if windows:
            return "handed off"  # the helper stops this process, installs, and starts the services again
    finally:
        lock.release()
    log(f"[update] now on {rel.tag}; restarting")
    exit_fn(0)  # launchd / systemd start the new version
    return "restarting"


def start_auto_update(home: Path, enabled, stop: threading.Event, log=print) -> threading.Thread:
    """`enabled` is read before each check, so turning auto_update off in config takes effect."""
    def loop():
        if stop.wait(FIRST_CHECK_AFTER):
            return
        while True:
            if enabled():
                result = check_and_update(home, log=log)
                if result not in ("up to date", "available"):
                    log(f"[update] {result}")
            if stop.wait(CHECK_EVERY):
                return

    t = threading.Thread(target=loop, name="auto-update", daemon=True)
    t.start()
    return t
