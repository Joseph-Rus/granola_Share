"""Keep granola-share running: launchd (macOS), Startup folder (Windows), systemd --user (Linux)."""

from __future__ import annotations

import os
import platform
import subprocess
import sys
from pathlib import Path
from xml.sax.saxutils import escape

ROLES = {"server": ["run"], "client": ["client", "run"]}


def role_args(role: str, home: Path, python: str | None = None) -> list[str]:
    if role not in ROLES:
        raise ValueError(f"role must be one of {sorted(ROLES)}")
    return [python or sys.executable, "-m", "granola_share.cli", "--home", str(home), *ROLES[role]]


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
    <dict><key>PATH</key><string>/usr/local/bin:/opt/homebrew/bin:/usr/bin:/bin:{escape(str(Path.home() / '.local/bin'))}</string></dict>
</dict>
</plist>
"""


def render_systemd(description: str, args: list[str]) -> str:
    cmd = " ".join(f'"{a}"' if " " in a else a for a in args)
    return f"""[Unit]
Description={description}

[Service]
ExecStart={cmd}
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
    return f'@echo off\r\nstart "" /min "{exe_s}" {rest}\r\n'


def _quiet(run_cmd, args: list[str]) -> None:
    try:
        run_cmd(args, capture_output=True, text=True)
    except Exception:
        pass


def install(role: str, home: Path, *, system: str | None = None, launch_agents_dir: Path | None = None,
            startup_dir: Path | None = None, systemd_dir: Path | None = None, run_cmd=subprocess.run,
            python: str | None = None) -> Path:
    system = system or platform.system()
    args = role_args(role, home, python)
    (home / "logs").mkdir(parents=True, exist_ok=True)
    log_path = home / "logs" / f"{role}.log"
    if system == "Darwin":
        d = launch_agents_dir or (Path.home() / "Library" / "LaunchAgents")
        d.mkdir(parents=True, exist_ok=True)
        label = f"com.granola-share.{role}"
        plist = d / f"{label}.plist"
        plist.write_text(render_plist(label, args, log_path))
        target = f"gui/{os.getuid()}"
        _quiet(run_cmd, ["launchctl", "bootout", target, str(plist)])
        _quiet(run_cmd, ["launchctl", "bootstrap", target, str(plist)])
        return plist
    if system == "Windows":
        d = startup_dir or (Path(os.environ.get("APPDATA", str(Path.home()))) / "Microsoft" / "Windows"
                            / "Start Menu" / "Programs" / "Startup")
        d.mkdir(parents=True, exist_ok=True)
        cmd = d / f"granola-share-{role}.cmd"
        cmd.write_text(render_cmd(args))
        # start it right away too
        _quiet(run_cmd, ["cmd", "/c", "start", "", "/min", str(cmd)])
        return cmd
    d = systemd_dir or (Path.home() / ".config" / "systemd" / "user")
    d.mkdir(parents=True, exist_ok=True)
    unit = d / f"granola-share-{role}.service"
    unit.write_text(render_systemd(f"granola-share {role}", args))
    _quiet(run_cmd, ["systemctl", "--user", "daemon-reload"])
    _quiet(run_cmd, ["systemctl", "--user", "enable", "--now", unit.name])
    return unit


def uninstall(role: str, *, system: str | None = None, launch_agents_dir: Path | None = None,
              startup_dir: Path | None = None, systemd_dir: Path | None = None, run_cmd=subprocess.run) -> bool:
    system = system or platform.system()
    if system == "Darwin":
        d = launch_agents_dir or (Path.home() / "Library" / "LaunchAgents")
        plist = d / f"com.granola-share.{role}.plist"
        if not plist.exists():
            return False
        _quiet(run_cmd, ["launchctl", "bootout", f"gui/{os.getuid()}", str(plist)])
        plist.unlink()
        return True
    if system == "Windows":
        d = startup_dir or (Path(os.environ.get("APPDATA", str(Path.home()))) / "Microsoft" / "Windows"
                            / "Start Menu" / "Programs" / "Startup")
        cmd = d / f"granola-share-{role}.cmd"
        if not cmd.exists():
            return False
        cmd.unlink()
        return True
    d = systemd_dir or (Path.home() / ".config" / "systemd" / "user")
    unit = d / f"granola-share-{role}.service"
    if not unit.exists():
        return False
    _quiet(run_cmd, ["systemctl", "--user", "disable", "--now", unit.name])
    unit.unlink()
    return True
