"""Where this machine can be reached (Tailscale), and the one-line install for your laptop."""

from __future__ import annotations

import json
import shlex
import shutil
import socket
import subprocess
from pathlib import Path

import httpx

RAW = "https://raw.githubusercontent.com/Joseph-Rus/granola_Share/main"
TAILSCALE_PATHS = ["/Applications/Tailscale.app/Contents/MacOS/Tailscale", r"C:\Program Files\Tailscale\tailscale.exe"]


def tailscale_info(runner=subprocess.run) -> dict:
    """{"installed", "running", "dns", "ips"} from `tailscale status --json`."""
    info = {"installed": False, "running": False, "dns": "", "ips": []}
    for exe in [shutil.which("tailscale"), *TAILSCALE_PATHS]:
        if not exe or not Path(exe).exists():
            continue
        info["installed"] = True
        try:
            out = runner([exe, "status", "--json"], capture_output=True, text=True, timeout=10).stdout
            data = json.loads(out)
        except Exception:
            continue
        me = data.get("Self") or {}
        info["running"] = data.get("BackendState") == "Running"
        info["dns"] = str(me.get("DNSName") or "").rstrip(".")
        info["ips"] = [ip for ip in me.get("TailscaleIPs") or [] if "." in ip]
        break
    return info


def server_urls(port: int, ts: dict | None = None) -> list[str]:
    """Addresses your laptop can use, best first: Tailscale name, Tailscale IP, then the local name."""
    ts = ts if ts is not None else tailscale_info()
    urls = []
    if ts.get("running"):
        if ts.get("dns"):
            urls.append(f"http://{ts['dns']}:{port}")
        urls += [f"http://{ip}:{port}" for ip in ts.get("ips", [])[:1]]
    urls.append(f"http://{socket.gethostname()}:{port}")
    return urls


def _ps_quote(s: str) -> str:
    return "'" + s.replace("'", "''") + "'"


def invite_commands(url: str, key: str) -> dict[str, str]:
    """One-liners that install the laptop side with this library's address and password filled in."""
    return {
        "mac": f"curl -fsSL {RAW}/install.sh | GRANOLA_SHARE_SERVER={shlex.quote(url)} "
               f"GRANOLA_SHARE_KEY={shlex.quote(key)} sh",
        "windows": f"$env:GRANOLA_SHARE_SERVER={_ps_quote(url)}; $env:GRANOLA_SHARE_KEY={_ps_quote(key)}; "
                   f"irm {RAW}/install.ps1 | iex",
    }


def port_status(port: int, host: str = "0.0.0.0", get=httpx.get) -> str:
    """'free', 'ours' (granola-share already answers there), or 'busy' (something else has it)."""
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        if hasattr(socket, "SO_REUSEPORT"):  # POSIX: ignore sockets lingering in TIME_WAIT, like uvicorn does
            s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        try:
            s.bind((host, port))
            return "free"
        except OSError:
            pass
    try:
        r = get(f"http://127.0.0.1:{port}/api/health", timeout=3)
        if "x-granola-share" in r.headers:
            return "ours"
        # 0.1 didn't send that header, but its health check answers in a recognizable way.
        body = r.json() if r.headers.get("content-type", "").startswith("application/json") else {}
        if (r.status_code == 401 and body.get("detail") == "bad pool password") or \
                (r.status_code == 200 and "pool_name" in body and "classes" in body):
            return "ours"
    except Exception:
        pass
    return "busy"
