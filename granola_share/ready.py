"""Get each computer ready. The library's: Tailscale, so your laptop and phone reach it from anywhere,
and Ollama, which writes the study notes. The laptop's: Granola, which records the lectures, and
Tailscale, to reach the library. (The library's computer doesn't need Granola.) `granola-share setup`
offers each install or fix and asks first; the laptop's setup page does the same, and `granola-share
doctor` says what's still missing.

Installs use each app's official download, the way you'd install it by hand: the Ollama app and
Tailscale's own installer. Nothing here takes admin rights by itself. When an installer needs them,
the system asks you (macOS for your password, Windows with its permission prompt).
"""

from __future__ import annotations

import base64
import os
import platform
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

import httpx

from . import hostinfo, ollama

OLLAMA_MAC = "https://ollama.com/download/Ollama-darwin.zip"
OLLAMA_WINDOWS = "https://ollama.com/download/OllamaSetup.exe"
OLLAMA_LINUX = "curl -fsSL https://ollama.com/install.sh | sh"
TAILSCALE_MAC = "https://pkgs.tailscale.com/stable/Tailscale-latest-macos.pkg"
TAILSCALE_WINDOWS = "https://pkgs.tailscale.com/stable/tailscale-setup-latest.exe"
TAILSCALE_LINUX = "curl -fsSL https://tailscale.com/install.sh | sh"
TAILSCALE_DOWNLOAD = "https://tailscale.com/download"
GRANOLA_DOWNLOAD = "https://www.granola.ai/download"
TAILNET = "100.64.0.0/10"  # every Tailscale address is in this range
FIREWALL_RULE = "Study Stash library"


def remote_session() -> bool:
    """Signed in over SSH: an installer window would open on this computer's own screen, where nobody sees it."""
    return bool(os.environ.get("SSH_CONNECTION") or os.environ.get("SSH_TTY"))


def disk_free_gb(path: Path | None = None) -> float | None:
    """Free space where Ollama keeps its models (your home folder)."""
    try:
        return shutil.disk_usage(path or Path.home()).free / 1e9
    except OSError:
        return None


def download(url: str, dest: Path, progress=None) -> Path:
    """Fetch `url` into `dest`, calling `progress(done_bytes, total_bytes)` as it goes."""
    with httpx.stream("GET", url, follow_redirects=True, timeout=httpx.Timeout(60, connect=15)) as r:
        r.raise_for_status()
        total, done = int(r.headers.get("content-length") or 0), 0
        with open(dest, "wb") as f:
            for chunk in r.iter_bytes(1 << 20):
                f.write(chunk)
                done += len(chunk)
                if progress and total:
                    progress(done, total)
    return dest


def _downloads() -> Path:
    """Where an installer that outlives this command is saved: an installer window may still be reading it."""
    folder = Path.home() / "Downloads"
    return folder if folder.is_dir() else Path(tempfile.gettempdir())


# --- Ollama --------------------------------------------------------------------------------

def install_ollama(log=print, progress=None, *, system: str | None = None, run=subprocess.run,
                   fetch=download) -> bool:
    """Install Ollama from ollama.com. True when it's installed afterwards."""
    system = system or platform.system()
    try:
        if system == "Darwin":
            with tempfile.TemporaryDirectory() as tmp:
                archive = fetch(OLLAMA_MAC, Path(tmp) / "Ollama-darwin.zip", progress)
                unpacked = Path(tmp) / "unpacked"
                p = run(["ditto", "-x", "-k", str(archive), str(unpacked)], capture_output=True, text=True)
                if p.returncode != 0 or not (unpacked / "Ollama.app").is_dir():
                    log("    The download didn't unpack. Get Ollama from https://ollama.com/download instead.")
                    return False
                apps = Path("/Applications")
                dest = (apps if os.access(apps, os.W_OK) else Path.home() / "Applications") / "Ollama.app"
                dest.parent.mkdir(parents=True, exist_ok=True)
                shutil.rmtree(dest, ignore_errors=True)
                shutil.move(str(unpacked / "Ollama.app"), str(dest))
                log(f"    Installed in {dest.parent}.")
        elif system == "Windows":
            with tempfile.TemporaryDirectory() as tmp:
                setup = fetch(OLLAMA_WINDOWS, Path(tmp) / "OllamaSetup.exe", progress)
                log("    Running Ollama's installer (it installs in your account, no admin needed)...")
                # Its installer takes the usual Inno Setup options: progress only, no questions.
                p = run([str(setup), "/SILENT", "/NORESTART", "/SUPPRESSMSGBOXES"], capture_output=True, text=True)
                if p.returncode != 0:
                    log(f"    Ollama's installer stopped (code {p.returncode}).")
        else:
            log(f"    Running Ollama's installer: {OLLAMA_LINUX}")
            log("    It asks for your password, because it sets Ollama up for the whole computer.")
            run(["sh", "-c", OLLAMA_LINUX])
    except Exception as e:
        log(f"    Couldn't install Ollama: {e}")
        return False
    return ollama.installed(system)


# --- Tailscale -----------------------------------------------------------------------------

def install_tailscale(log=print, progress=None, ask=input, *, system: str | None = None, run=subprocess.run,
                      fetch=download, remote: bool | None = None, start=None) -> bool:
    """Install Tailscale from tailscale.com. Its installers need an administrator's OK, so on a Mac and
    on Windows the installer opens for you to click through, and `ask` waits until you're done.
    True when it's installed afterwards."""
    system = system or platform.system()
    remote = remote_session() if remote is None else remote
    start = start or (lambda path: os.startfile(path))  # type: ignore[attr-defined]  # Windows: asks to elevate
    try:
        if system == "Darwin":
            if remote:
                log("    Tailscale needs a few clicks on this Mac's own screen, and you're connected over SSH.")
                log(f"    At this Mac, install it from {TAILSCALE_DOWNLOAD}/mac, open it, and sign in.")
                return False
            pkg = fetch(TAILSCALE_MAC, _downloads() / "Tailscale.pkg", progress)
            run(["open", str(pkg)], capture_output=True, text=True)
            log("    Tailscale's installer is open. Click through it; macOS asks for your password.")
            ask("    Press Enter when the installer has finished: ")
        elif system == "Windows":
            setup = fetch(TAILSCALE_WINDOWS, _downloads() / "tailscale-setup.exe", progress)
            start(str(setup))
            log("    Tailscale's installer is open. Windows asks for permission first: click Yes, then Install.")
            ask("    Press Enter when the installer has finished: ")
        else:
            log(f"    Running Tailscale's installer: {TAILSCALE_LINUX}")
            log("    It asks for your password, because it sets Tailscale up for the whole computer.")
            run(["sh", "-c", TAILSCALE_LINUX])
    except Exception as e:
        log(f"    Couldn't install Tailscale: {e}")
        return False
    return bool(hostinfo.tailscale_exe())


def connect_tailscale(ts: dict, log=print, *, system: str | None = None, run=subprocess.run,
                      remote: bool | None = None) -> bool:
    """Connect Tailscale. `tailscale up` prints a sign-in link, which works even over SSH. True if it
    says it's connected (the caller checks again either way)."""
    system = system or platform.system()
    remote = remote_session() if remote is None else remote
    exe = ts.get("exe") or hostinfo.tailscale_exe()
    if not exe:
        return False
    if system == "Darwin" and "Tailscale.app" in exe and not remote:
        # The Mac app answers the command only while it's open; the first time, macOS also asks to
        # allow its VPN configuration.
        run(["open", "-g", "-a", "Tailscale"], capture_output=True, text=True)
    cmd = [exe, "up"]
    if system == "Linux" and hasattr(os, "geteuid") and os.geteuid() != 0:
        cmd = ["sudo", *cmd]
    log("    Connecting. If a link appears, open it and sign in with the same account as your laptop.")
    try:
        return run(cmd, timeout=300).returncode == 0
    except (OSError, subprocess.TimeoutExpired):
        return False


def open_tailscale(system: str | None = None, run=subprocess.run) -> bool:
    """Open the Tailscale app, where you sign in or turn it on."""
    system = system or platform.system()
    try:
        if system == "Darwin":
            return run(["open", "-a", "Tailscale"], capture_output=True, text=True).returncode == 0
        if system == "Windows":
            tray = Path(os.environ.get("ProgramFiles", r"C:\Program Files")) / "Tailscale" / "tailscale-ipn.exe"
            if tray.exists():
                os.startfile(str(tray))  # type: ignore[attr-defined]
                return True
    except Exception:
        pass
    return False


# --- the laptop: Granola ---------------------------------------------------------------------

def granola_app(system: str | None = None, run=subprocess.run) -> str | None:
    """Where the Granola app is, or None. Granola has apps for macOS and Windows only."""
    system = system or platform.system()
    if system == "Darwin":
        for app in (Path("/Applications/Granola.app"), Path.home() / "Applications" / "Granola.app"):
            if app.is_dir():
                return str(app)
        try:  # anywhere else Spotlight knows about
            out = run(["mdfind", 'kMDItemCFBundleIdentifier == "com.granola.app"'], capture_output=True, text=True,
                      timeout=5).stdout
            return next((line for line in out.splitlines() if line.endswith(".app")), None)
        except Exception:
            return None
    if system == "Windows":
        # Its installer puts it in your account, in a folder named after the project (@granolaelectron).
        local = Path(os.environ.get("LOCALAPPDATA", str(Path.home() / "AppData" / "Local"))) / "Programs"
        roots = [local] + [Path(p) for p in {os.environ.get("ProgramFiles"), os.environ.get("ProgramW6432")} if p]
        for root in roots:
            for folder in ("@granolaelectron", "Granola"):
                if (root / folder / "Granola.exe").is_file():
                    return str(root / folder / "Granola.exe")
        return _granola_from_registry()
    return None


def _granola_from_registry() -> str | None:
    """Granola's entry in Windows' installed apps, wherever it went. (A power-saving app from MiserWare
    is also called Granola: not that one.)"""
    try:
        import winreg
    except ImportError:
        return None
    for root in (winreg.HKEY_CURRENT_USER, winreg.HKEY_LOCAL_MACHINE):  # type: ignore[attr-defined]
        try:
            apps = winreg.OpenKey(root, r"Software\Microsoft\Windows\CurrentVersion\Uninstall")  # type: ignore[attr-defined]
        except OSError:
            continue
        with apps:
            for i in range(winreg.QueryInfoKey(apps)[0]):  # type: ignore[attr-defined]
                try:
                    with winreg.OpenKey(apps, winreg.EnumKey(apps, i)) as app:  # type: ignore[attr-defined]
                        def value(name: str) -> str:
                            try:
                                return str(winreg.QueryValueEx(app, name)[0] or "")  # type: ignore[attr-defined]
                            except OSError:
                                return ""
                        if value("DisplayName").startswith("Granola") and "miserware" not in value("Publisher").lower():
                            return value("InstallLocation") or value("DisplayIcon").split(",")[0] or value("DisplayName")
                except OSError:
                    continue
    return None


def laptop_checks(system: str | None = None, granola=granola_app, tailscale=hostinfo.tailscale_info) -> dict:
    """What the laptop has: {"granola": path or None, "granola_here": whether Granola makes an app for this
    system, "tailscale": Tailscale's status}."""
    system = system or platform.system()
    here = system in ("Darwin", "Windows")
    return {"granola": granola(system) if here else None, "granola_here": here, "tailscale": tailscale()}


# --- Windows: the firewall -----------------------------------------------------------------

def _powershell(script: str, run=subprocess.run, timeout: float = 60):
    return run(["powershell", "-NoProfile", "-NonInteractive", "-Command", script],
               capture_output=True, text=True, timeout=timeout)


def firewall_open(port: int, run=subprocess.run) -> bool | None:
    """Windows: can your laptop get through Windows Firewall to the library's port? None when it can't tell."""
    ps = ("if (-not @(Get-NetFirewallProfile | Where-Object { $_.Enabled }).Count) { 'off'; exit } "
          f"$r = @(Get-NetFirewallRule -DisplayName '{FIREWALL_RULE}' -ErrorAction SilentlyContinue "
          "| Where-Object { $_.Enabled -eq 'True' }); "
          "if (-not $r.Count) { 'none' } else { ($r | Get-NetFirewallPortFilter).LocalPort -join ',' }")
    try:
        p = _powershell(ps, run)
    except Exception:
        return None
    out = (p.stdout or "").strip()
    if p.returncode != 0 or not out:
        return None
    return out == "off" or str(port) in out.split(",")


def _pythons() -> list[str]:
    """This app's Python, and the one it actually runs on (uv's launcher starts the base interpreter)."""
    exes = {sys.executable, getattr(sys, "_base_executable", sys.executable)}
    return sorted({str(Path(e).with_name(n)) for e in exes for n in ("python.exe", "pythonw.exe")})


def firewall_script(port: int) -> str:
    """Our inbound rule for the library's port, open to Tailscale and this network only. Also removes the
    block rules Windows adds for Python when its "allow access?" prompt was dismissed: they'd win."""
    pys = ",".join("'" + p.replace("'", "''") + "'" for p in _pythons())
    return (f"Remove-NetFirewallRule -DisplayName '{FIREWALL_RULE}' -ErrorAction SilentlyContinue; "
            f"New-NetFirewallRule -DisplayName '{FIREWALL_RULE}' "
            "-Description 'Lets your laptop reach your Study Stash library. Added by granola-share setup.' "
            f"-Direction Inbound -Action Allow -Protocol TCP -LocalPort {port} -RemoteAddress {TAILNET},LocalSubnet "
            "-Profile Any | Out-Null; "
            f"$py = @({pys}); Get-NetFirewallApplicationFilter | Where-Object {{ $py -contains $_.Program }} "
            "| Get-NetFirewallRule | Where-Object { $_.Direction -eq 'Inbound' -and $_.Action -eq 'Block' } "
            "| Remove-NetFirewallRule")


def open_firewall(port: int, run=subprocess.run) -> bool:
    """Add the rule, with Windows' permission prompt (changing the firewall needs an administrator)."""
    encoded = base64.b64encode(firewall_script(port).encode("utf-16-le")).decode()
    ps = ("try { Start-Process powershell -Verb RunAs -Wait -WindowStyle Hidden "
          f"-ArgumentList '-NoProfile','-NonInteractive','-EncodedCommand','{encoded}'; exit 0 }} catch {{ exit 1 }}")
    try:
        p = _powershell(ps, run, timeout=300)
    except Exception:
        return False
    return p.returncode == 0 and firewall_open(port, run) is True


# --- sleep ---------------------------------------------------------------------------------

def mac_sleep_minutes(runner=subprocess.run) -> int | None:
    """System sleep timer from `pmset -g` (0 = never sleeps)."""
    try:
        out = runner(["pmset", "-g"], capture_output=True, text=True, timeout=10).stdout
    except Exception:
        return None
    m = re.search(r"^\s*sleep\s+(\d+)", out, re.MULTILINE)
    return int(m.group(1)) if m else None


def windows_sleep_minutes(runner=subprocess.run) -> int | None:
    """Minutes before this PC sleeps while plugged in (0 = never), from powercfg."""
    try:
        out = runner(["powercfg", "/query", "SCHEME_CURRENT", "SUB_SLEEP", "STANDBYIDLE"],
                     capture_output=True, text=True, timeout=10).stdout
    except Exception:
        return None
    # The labels are in the PC's language, but the last two values are always the plugged-in (AC)
    # and battery (DC) settings, in seconds.
    values = re.findall(r"0x([0-9a-fA-F]+)\s*$", out or "", re.MULTILINE)
    return int(values[-2], 16) // 60 if len(values) >= 2 else None


def sleep_minutes(system: str | None = None) -> int | None:
    system = system or platform.system()
    if system == "Darwin":
        return mac_sleep_minutes()
    if system == "Windows":
        return windows_sleep_minutes()
    return None


def keep_awake(system: str | None = None, run=subprocess.run) -> bool:
    """Windows: never sleep while plugged in (the screen can still turn off). True if it took."""
    if (system or platform.system()) != "Windows":
        return False
    try:
        run(["powercfg", "/change", "standby-timeout-ac", "0"], capture_output=True, text=True, timeout=20)
    except Exception:
        return False
    return windows_sleep_minutes(run) == 0


SLEEP_FIX = {
    "Darwin": "System Settings → Energy: turn on \"Prevent automatic sleeping when the display is off\"",
    "Windows": "Settings → System → Power: when plugged in, put the device to sleep after Never",
}
