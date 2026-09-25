"""Small helpers around the local Ollama install: list, start, pull, and pick a model."""

from __future__ import annotations

import json
import os
import platform
import shutil
import subprocess
import time
from pathlib import Path

import httpx

# Best first. MoE "a3b" models are fast (3B active) with big-model judgment; 64 GB Macs run the 35B fine.
MODEL_PREFERENCE = ["qwen3.6:35b", "qwen3.6", "qwen3.8", "qwen3:30b", "gemma4", "qwen3", "gemma3", "llama3"]
NOT_FOR_TEXT = ("embed", "whisper", "clip", "rerank")


def list_models(host: str) -> list[dict] | None:
    """Installed chat models as [{"name", "size_gb"}], or None when Ollama is not reachable."""
    try:
        r = httpx.get(host.rstrip("/") + "/api/tags", timeout=5)
        r.raise_for_status()
    except Exception:
        return None
    out = []
    for m in r.json().get("models", []):
        name = str(m.get("name") or "")
        if name and not any(bad in name.lower() for bad in NOT_FOR_TEXT):
            out.append({"name": name, "size_gb": round((m.get("size") or 0) / 1e9, 1)})
    return sorted(out, key=lambda m: -m["size_gb"])


def size_label(m: dict) -> str:
    """'23.1 GB', or 'cloud' for Ollama cloud models (they send the text to Ollama's servers)."""
    return f"{m['size_gb']} GB" if m.get("size_gb") else "cloud, runs off this computer"


def model_names(host: str) -> list[str] | None:
    models = list_models(host)
    return None if models is None else [m["name"] for m in models]


def has_model(installed: list[str], name: str) -> bool:
    """Ollama lists 'llama3.2:latest' for a model pulled as 'llama3.2'."""
    return name in installed or (":" not in name and f"{name}:latest" in installed)


def pick_default_model(installed: list[str], fallback: str) -> str:
    for pref in MODEL_PREFERENCE:
        for m in installed:
            if m.lower().startswith(pref):
                return m
    return fallback


def total_ram_gb() -> float | None:
    system = platform.system()
    try:
        if system == "Darwin":
            out = subprocess.run(["sysctl", "-n", "hw.memsize"], capture_output=True, text=True, timeout=5).stdout
            return int(out.strip()) / 2**30
        if system == "Linux":
            for line in Path("/proc/meminfo").read_text().splitlines():
                if line.startswith("MemTotal:"):
                    return int(line.split()[1]) / 2**20
        if system == "Windows":
            import ctypes

            class MEMORYSTATUSEX(ctypes.Structure):
                _fields_ = [("dwLength", ctypes.c_ulong), ("dwMemoryLoad", ctypes.c_ulong),
                            ("ullTotalPhys", ctypes.c_ulonglong), ("ullAvailPhys", ctypes.c_ulonglong),
                            ("ullTotalPageFile", ctypes.c_ulonglong), ("ullAvailPageFile", ctypes.c_ulonglong),
                            ("ullTotalVirtual", ctypes.c_ulonglong), ("ullAvailVirtual", ctypes.c_ulonglong),
                            ("sullAvailExtendedVirtual", ctypes.c_ulonglong)]

            stat = MEMORYSTATUSEX()
            stat.dwLength = ctypes.sizeof(MEMORYSTATUSEX)
            ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(stat))  # type: ignore[attr-defined]
            return stat.ullTotalPhys / 2**30
    except Exception:
        pass
    return None


def recommended_model(ram_gb: float | None) -> str:
    """A model this machine can actually run, for when nothing suitable is installed yet."""
    if ram_gb is None or ram_gb >= 40:
        return "qwen3.6:35b-a3b"  # ~24 GB
    if ram_gb >= 14:
        return "gemma4:e4b"  # ~10 GB
    return "qwen3:1.7b"  # ~1.4 GB


def _mac_apps() -> list[Path]:
    return [Path("/Applications/Ollama.app"), Path.home() / "Applications" / "Ollama.app"]


def _windows_dir() -> Path:
    """Where OllamaSetup.exe puts it: in your own account, no admin needed."""
    return Path(os.environ.get("LOCALAPPDATA", str(Path.home() / "AppData" / "Local"))) / "Programs" / "Ollama"


def find_exe(system: str | None = None) -> str | None:
    """The `ollama` command. A fresh install isn't on this process's PATH yet, so its usual homes are checked too."""
    system = system or platform.system()
    if shutil.which("ollama"):
        return shutil.which("ollama")
    if system == "Darwin":
        places = [app / "Contents" / "Resources" / "ollama" for app in _mac_apps()]
        places += [Path("/opt/homebrew/bin/ollama"), Path("/usr/local/bin/ollama")]
    elif system == "Windows":
        places = [_windows_dir() / "ollama.exe"]
    else:
        places = [Path("/usr/local/bin/ollama"), Path("/usr/bin/ollama")]
    return next((str(p) for p in places if p.exists()), None)


def app_path(system: str | None = None) -> Path | None:
    """The Ollama app (Mac) or its tray app (Windows): they keep Ollama running and start it at login."""
    system = system or platform.system()
    if system == "Darwin":
        return next((a for a in _mac_apps() if a.exists()), None)
    if system == "Windows":
        tray = _windows_dir() / "ollama app.exe"
        return tray if tray.exists() else None
    return None


def installed(system: str | None = None) -> bool:
    return bool(find_exe(system) or app_path(system))


def _detached() -> dict:
    if platform.system() == "Windows":
        return {"creationflags": 0x00000008 | 0x00000200}  # DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP
    return {"start_new_session": True}


def start(host: str, wait: float = 20) -> bool:
    """Start Ollama if it is installed but not running. True once it answers."""
    if list_models(host) is not None:
        return True
    system = platform.system()
    app, exe = app_path(system), find_exe(system)
    try:
        if system == "Darwin" and app:
            # Over SSH with nobody signed in on the Mac's screen, `open` can't start an app: serve directly.
            if subprocess.run(["open", "-g", "-a", str(app)], capture_output=True, timeout=10).returncode != 0 and exe:
                subprocess.Popen([exe, "serve"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, **_detached())
        elif system == "Windows" and app:
            subprocess.Popen([str(app)], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, **_detached())
        elif exe:
            subprocess.Popen([exe, "serve"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, **_detached())
        else:
            return False
    except Exception:
        return False
    deadline = time.time() + wait
    while time.time() < deadline:
        if list_models(host) is not None:
            return True
        time.sleep(1)
    return False


def pull(model: str, host: str = "http://localhost:11434", progress=None) -> tuple[bool, str]:
    """Download a model through Ollama's own API, so no `ollama` command is needed.
    `progress(done_bytes, total_bytes)` is called as it goes. Returns (ok, why it failed)."""
    layers: dict[str, tuple[int, int]] = {}
    try:
        with httpx.stream("POST", host.rstrip("/") + "/api/pull", json={"model": model},
                          timeout=httpx.Timeout(60, connect=10)) as r:
            for line in r.iter_lines():
                if not line.strip():
                    continue
                try:
                    ev = json.loads(line)
                except ValueError:
                    continue
                if ev.get("error"):
                    return False, str(ev["error"])
                if ev.get("digest") and ev.get("total"):
                    layers[ev["digest"]] = (int(ev.get("completed") or 0), int(ev["total"]))
                    if progress:
                        progress(sum(d for d, _ in layers.values()), sum(t for _, t in layers.values()))
                if ev.get("status") == "success":
                    return True, ""
            if r.status_code != 200:
                return False, f"Ollama answered {r.status_code}"
    except httpx.HTTPError as e:
        return False, str(e) or type(e).__name__
    return False, "the download stopped before it finished"


def try_model(host: str, model: str, timeout: float = 600) -> tuple[float | None, str]:
    """Load the model and have it write a few words, the way a lecture will: (seconds, "") or (None, why)."""
    began = time.time()
    body = {"model": model, "messages": [{"role": "user", "content": "Reply with the single word: ready"}],
            "stream": False, "think": False, "options": {"num_predict": 16}}
    try:
        r = httpx.post(host.rstrip("/") + "/api/chat", json=body, timeout=httpx.Timeout(timeout, connect=10))
        if r.status_code != 200:
            try:
                why = r.json().get("error") or r.text
            except ValueError:
                why = r.text
            return None, str(why).strip()[:300] or f"Ollama answered {r.status_code}"
    except httpx.HTTPError as e:
        return None, str(e) or type(e).__name__
    return time.time() - began, ""
