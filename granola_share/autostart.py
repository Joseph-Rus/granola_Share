"""Keep granola-share running: launchd (macOS), Startup folder (Windows), systemd --user (Linux)."""

from __future__ import annotations

import os
import platform
import subprocess
import sys
import time
from pathlib import Path
from xml.sax.saxutils import escape

ROLES = {"server": ["run"], "client": ["client", "run"]}
# Set for the background service only: tells the auto-updater that exiting means "restart me".
SERVICE_ENV = "GRANOLA_SHARE_SERVICE"
# Windows: set on the service that keep_alive() starts, so it doesn't start another keep_alive.
CHILD_ENV = "GRANOLA_SHARE_CHILD"
LOG_LIMIT = 5_000_000  # bytes; the Windows log starts over (keeping one old copy) past this


def _uid() -> int:
    return os.getuid() if hasattr(os, "getuid") else 0


def role_args(role: str, home: Path, python: str | None = None) -> list[str]:
    if role not in ROLES:
        raise ValueError(f"role must be one of {sorted(ROLES)}")
    # -u keeps stdout/stderr unbuffered so the launchd/systemd log fills in live.
    return [python or sys.executable, "-u", "-m", "granola_share.cli", "--home", str(home), *ROLES[role]]


def label(role: str) -> str:
    return f"com.granola-share.{role}"


def default_launch_agents_dir() -> Path:
    return Path.home() / "Library" / "LaunchAgents"


def default_startup_dir() -> Path:
    return (Path(os.environ.get("APPDATA", str(Path.home()))) / "Microsoft" / "Windows" / "Start Menu"
            / "Programs" / "Startup")


def default_systemd_dir() -> Path:
    return Path.home() / ".config" / "systemd" / "user"


def startup_cmd_path(role: str) -> Path:
    return default_startup_dir() / f"granola-share-{role}.cmd"


def service_path(role: str, system: str | None = None) -> Path:
    system = system or platform.system()
    if system == "Darwin":
        return default_launch_agents_dir() / f"{label(role)}.plist"
    if system == "Windows":
        return startup_cmd_path(role)
    return default_systemd_dir() / f"granola-share-{role}.service"


def render_plist(label: str, args: list[str], log_path: Path) -> str:
    items = "".join(f"\n        <string>{escape(a)}</string>" for a in args)
    return f"""<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key><string>{escape(label)}</string>
    <key>ProgramArguments</key>
    <array>{items}
    </array>
    <key>RunAtLoad</key><true/>
    <key>KeepAlive</key><true/>
    <key>StandardOutPath</key><string>{escape(str(log_path))}</string>
    <key>StandardErrorPath</key><string>{escape(str(log_path))}</string>
    <key>EnvironmentVariables</key>
    <dict>
        <key>PATH</key><string>/usr/local/bin:/opt/homebrew/bin:/usr/bin:/bin:{escape(str(Path.home() / '.local/bin'))}</string>
        <key>{SERVICE_ENV}</key><string>1</string>
    </dict>
</dict>
</plist>
"""


def render_systemd(description: str, args: list[str]) -> str:
    cmd = " ".join(f'"{a}"' if " " in a else a for a in args)
    return f"""[Unit]
Description={description}

[Service]
ExecStart={cmd}
Environment={SERVICE_ENV}=1
Restart=always
RestartSec=10

[Install]
WantedBy=default.target
"""


def render_cmd(args: list[str]) -> str:
    exe = Path(args[0])
    pythonw = exe.with_name("pythonw.exe")
    exe_s = str(pythonw if pythonw.exists() else exe)
    rest = " ".join(f'"{a}"' for a in args[1:])
    return f'@echo off\r\nset {SERVICE_ENV}=1\r\nstart "" /min "{exe_s}" {rest}\r\n'


def _quiet(run_cmd, args: list[str]):
    try:
        return run_cmd(args, capture_output=True, text=True)
    except Exception:
        return None


def install(role: str, home: Path, *, system: str | None = None, launch_agents_dir: Path | None = None,
            startup_dir: Path | None = None, systemd_dir: Path | None = None, run_cmd=subprocess.run,
            python: str | None = None) -> Path:
    system = system or platform.system()
    args = role_args(role, home, python)
    (home / "logs").mkdir(parents=True, exist_ok=True)
    log_path = home / "logs" / f"{role}.log"
    if system == "Darwin":
        d = launch_agents_dir or default_launch_agents_dir()
        d.mkdir(parents=True, exist_ok=True)
        plist = d / f"{label(role)}.plist"
        plist.write_text(render_plist(label(role), args, log_path), encoding="utf-8")
        target = f"gui/{_uid()}"
        _quiet(run_cmd, ["launchctl", "bootout", target, str(plist)])
        _quiet(run_cmd, ["launchctl", "bootstrap", target, str(plist)])
        return plist
    if system == "Windows":
        d = startup_dir or default_startup_dir()
        d.mkdir(parents=True, exist_ok=True)
        cmd = d / f"granola-share-{role}.cmd"
        _stop_windows(run_cmd, role)  # an older copy may still be running
        cmd.write_text(render_cmd(args))
        _start_windows(run_cmd, cmd)  # start it right away too
        return cmd
    d = systemd_dir or default_systemd_dir()
    d.mkdir(parents=True, exist_ok=True)
    unit = d / f"granola-share-{role}.service"
    unit.write_text(render_systemd(f"granola-share {role}", args), encoding="utf-8")
    _quiet(run_cmd, ["systemctl", "--user", "daemon-reload"])
    _quiet(run_cmd, ["systemctl", "--user", "enable", unit.name])
    _quiet(run_cmd, ["systemctl", "--user", "restart", unit.name])
    return unit


def uninstall(role: str, *, system: str | None = None, launch_agents_dir: Path | None = None,
              startup_dir: Path | None = None, systemd_dir: Path | None = None, run_cmd=subprocess.run) -> bool:
    system = system or platform.system()
    if system == "Darwin":
        d = launch_agents_dir or default_launch_agents_dir()
        plist = d / f"{label(role)}.plist"
        if not plist.exists():
            return False
        _quiet(run_cmd, ["launchctl", "bootout", f"gui/{_uid()}", str(plist)])
        plist.unlink()
        return True
    if system == "Windows":
        d = startup_dir or default_startup_dir()
        cmd = d / f"granola-share-{role}.cmd"
        if not cmd.exists():
            return False
        _stop_windows(run_cmd, role)
        cmd.unlink()
        return True
    d = systemd_dir or default_systemd_dir()
    unit = d / f"granola-share-{role}.service"
    if not unit.exists():
        return False
    _quiet(run_cmd, ["systemctl", "--user", "disable", "--now", unit.name])
    unit.unlink()
    return True


def installed_roles(system: str | None = None) -> list[str]:
    return [r for r in ROLES if service_path(r, system).exists()]


def _ps_filter(role: str | None) -> str:
    """PowerShell test for "this process is the granola-share <role> service" (args are quoted one by one)."""
    base = "$_.CommandLine -like '*granola_share.cli*'"
    if role == "client":
        return base + " -and $_.CommandLine -like '*client*run*'"
    if role == "server":
        return base + " -and $_.CommandLine -notlike '*client*run*'"
    return base


def _start_windows(run_cmd, cmd: Path) -> None:
    """Run a Startup .cmd now. It starts the service in the background, and that program inherits
    whatever the .cmd's output goes to: a pipe we read would stay open as long as the service runs,
    and waiting for its end would never finish. So the output goes nowhere."""
    try:
        run_cmd(["cmd", "/c", str(cmd)], stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL, timeout=60)
    except Exception:
        pass


def _stop_windows(run_cmd, role: str | None = None) -> None:
    ps = (f"Get-CimInstance Win32_Process | Where-Object {{ {_ps_filter(role)} }} | "
          "ForEach-Object { Stop-Process -Id $_.ProcessId -Force }")
    _quiet(run_cmd, ["powershell", "-NoProfile", "-Command", ps])


def status(role: str, *, system: str | None = None, run_cmd=subprocess.run) -> str:
    """'missing' (not installed), 'running', or 'stopped'."""
    system = system or platform.system()
    if not service_path(role, system).exists():
        return "missing"
    if system == "Darwin":
        p = _quiet(run_cmd, ["launchctl", "print", f"gui/{_uid()}/{label(role)}"])
        return "running" if p and p.returncode == 0 and "state = running" in (p.stdout or "") else "stopped"
    if system == "Windows":
        ps = f"@(Get-CimInstance Win32_Process | Where-Object {{ {_ps_filter(role)} }}).Count"
        p = _quiet(run_cmd, ["powershell", "-NoProfile", "-Command", ps])
        try:
            return "running" if p and int((p.stdout or "0").strip() or 0) > 0 else "stopped"
        except ValueError:
            return "stopped"
    p = _quiet(run_cmd, ["systemctl", "--user", "is-active", f"granola-share-{role}.service"])
    return "running" if p and (p.stdout or "").strip() == "active" else "stopped"


def restart(role: str, *, system: str | None = None, run_cmd=subprocess.run) -> None:
    system = system or platform.system()
    path = service_path(role, system)
    if not path.exists():
        return
    if system == "Darwin":
        target = f"gui/{_uid()}"
        p = _quiet(run_cmd, ["launchctl", "kickstart", "-k", f"{target}/{label(role)}"])
        if not p or p.returncode != 0:  # not loaded yet
            _quiet(run_cmd, ["launchctl", "bootstrap", target, str(path)])
    elif system == "Windows":
        _stop_windows(run_cmd, role)
        _start_windows(run_cmd, path)
    else:
        _quiet(run_cmd, ["systemctl", "--user", "restart", path.name])


def keep_alive(home: Path, role: str, argv: list[str], *, spawn=subprocess.Popen, sleep=time.sleep,
               rounds: int | None = None) -> None:
    """Windows has no launchd KeepAlive or systemd Restart=, so there the background service runs under
    this small loop: it writes the log that launchd and systemd keep elsewhere (pythonw has no console,
    so print() would go nowhere) and starts the service again whenever it stops."""
    log = home / "logs" / f"{role}.log"
    log.parent.mkdir(parents=True, exist_ok=True)
    env = {**os.environ, CHILD_ENV: "1"}
    done = 0
    while rounds is None or done < rounds:
        done += 1
        try:
            if log.exists() and log.stat().st_size > LOG_LIMIT:
                log.replace(log.with_suffix(".log.1"))
        except OSError:
            pass
        began = time.time()
        with open(log, "a", encoding="utf-8", errors="replace") as out:
            child = spawn([sys.executable, "-u", "-m", "granola_share.cli", *argv], env=env, stdout=out,
                          stderr=subprocess.STDOUT, creationflags=0x08000000)  # CREATE_NO_WINDOW
            code = child.wait()
            pause = 10 if time.time() - began > 60 else 60  # failing as it starts: don't spin
            out.write(f"[service] stopped (exit {code}); starting it again in {pause} s\n")
        sleep(pause)
