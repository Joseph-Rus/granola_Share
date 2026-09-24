"""Small helpers around the local Ollama install: list, start, pull, and pick a model."""

from __future__ import annotations

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


def installed() -> bool:
    return bool(shutil.which("ollama")) or Path("/Applications/Ollama.app").exists()


def start(host: str, wait: float = 20) -> bool:
    """Start Ollama if it is installed but not running. True once it answers."""
    if list_models(host) is not None:
        return True
    try:
        if platform.system() == "Darwin" and Path("/Applications/Ollama.app").exists():
            subprocess.run(["open", "-g", "-a", "Ollama"], capture_output=True, timeout=10)
        elif shutil.which("ollama"):
            flags = {}
            if platform.system() == "Windows":
                flags["creationflags"] = 0x00000008 | 0x00000200  # DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP
            else:
                flags["start_new_session"] = True
            subprocess.Popen([shutil.which("ollama"), "serve"], stdout=subprocess.DEVNULL,
                             stderr=subprocess.DEVNULL, **flags)
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


def pull(model: str) -> bool:
    exe = shutil.which("ollama")
    if not exe:
        return False
    return subprocess.run([exe, "pull", model]).returncode == 0
