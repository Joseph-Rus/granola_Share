"""Native yes/no popups and notifications, no extra dependencies.

macOS: osascript. Windows: user32 MessageBox. Linux: zenity, else tkinter.
`ask_yes_no` returns True (share), False (skip) or None (nobody answered in time).
"""

from __future__ import annotations

import platform
import re
import shutil
import subprocess


def _esc(s: str) -> str:
    return s.replace("\\", "\\\\").replace('"', '\\"')


def build_osascript(title: str, text: str, yes: str, no: str, timeout: int) -> str:
    return (
        f'display dialog "{_esc(text)}" with title "{_esc(title)}" '
        f'buttons {{"{_esc(no)}", "{_esc(yes)}"}} default button "{_esc(yes)}" '
        f"with icon note giving up after {int(timeout)}"
    )


def parse_osascript(out: str, yes: str) -> bool | None:
    if "gave up:true" in out:
        return None
    m = re.search(r"button returned:([^,\n]*)", out)
    return bool(m and m.group(1).strip() == yes)


def _ask_windows(title: str, text: str, timeout: int) -> bool | None:
    import ctypes  # noqa: WPS433

    MB_YESNO, MB_ICONQUESTION, MB_SETFOREGROUND, MB_TOPMOST = 0x4, 0x20, 0x10000, 0x40000
    flags = MB_YESNO | MB_ICONQUESTION | MB_SETFOREGROUND | MB_TOPMOST
    user32 = ctypes.windll.user32  # type: ignore[attr-defined]
    try:
        r = user32.MessageBoxTimeoutW(0, text, title, flags, 0, int(timeout * 1000))
        if r == 32000:  # MB_TIMEDOUT
            return None
    except AttributeError:
        r = user32.MessageBoxW(0, text, title, flags)
    return r == 6  # IDYES


def _ask_tk(title: str, text: str) -> bool:
    try:
        import tkinter
        from tkinter import messagebox
    except ImportError:
        return False
    root = tkinter.Tk()
    root.withdraw()
    try:
        root.attributes("-topmost", True)
        return bool(messagebox.askyesno(title, text))
    finally:
        root.destroy()


def ask_yes_no(title: str, text: str, yes: str = "Share", no: str = "Skip", timeout: int = 300,
               runner=subprocess.run, system: str | None = None) -> bool | None:
    system = system or platform.system()
    if system == "Darwin":
        p = runner(["osascript", "-e", build_osascript(title, text, yes, no, timeout)],
                   capture_output=True, text=True)
        if p.returncode != 0:
            return False  # dialog closed/cancelled
        return parse_osascript(p.stdout, yes)
    if system == "Windows":
        return _ask_windows(title, f"{text}\n\nYes = {yes}, No = {no}", timeout)
    if shutil.which("zenity"):
        p = runner(["zenity", "--question", f"--title={title}", f"--text={text}",
                    f"--ok-label={yes}", f"--cancel-label={no}", f"--timeout={timeout}"],
                   capture_output=True, text=True)
        return None if p.returncode == 5 else p.returncode == 0
    return _ask_tk(title, f"{text}\n\nYes = {yes}, No = {no}")


def notify(title: str, text: str, runner=subprocess.run, system: str | None = None) -> None:
    """Best-effort, non-blocking notification."""
    system = system or platform.system()
    try:
        if system == "Darwin":
            runner(["osascript", "-e", f'display notification "{_esc(text)}" with title "{_esc(title)}"'],
                   capture_output=True, text=True)
        elif system == "Linux" and shutil.which("notify-send"):
            runner(["notify-send", title, text], capture_output=True, text=True)
    except Exception:
        pass
