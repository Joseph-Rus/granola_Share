"""Native yes/no popups and notifications, no extra dependencies.

macOS: osascript. Windows: user32 MessageBox. Linux: zenity, else tkinter.
`ask_yes_no` returns True (share), False (skip) or None (nobody answered in time).
"""

from __future__ import annotations

import platform
import re
import shutil
import subprocess
from pathlib import Path


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


def ask_choice(title: str, text: str, buttons: list[str], default: str | None = None, timeout: int = 300,
               runner=subprocess.run, system: str | None = None) -> str | None:
    """A dialog with up to three buttons. Returns the label clicked, or None if nobody answered in time.

    Only macOS draws three buttons; elsewhere the first and the default button are offered.
    """
    system = system or platform.system()
    default = default or buttons[-1]
    if system == "Darwin":
        labels = ", ".join(f'"{_esc(b)}"' for b in buttons)
        script = (f'display dialog "{_esc(text)}" with title "{_esc(title)}" buttons {{{labels}}} '
                  f'default button "{_esc(default)}" with icon note giving up after {int(timeout)}')
        p = runner(["osascript", "-e", script], capture_output=True, text=True)
        if p.returncode != 0 or "gave up:true" in p.stdout:
            return None
        m = re.search(r"button returned:([^,\n]*)", p.stdout)
        return m.group(1).strip() if m else None
    other = buttons[0] if buttons[0] != default else buttons[-1]
    answer = ask_yes_no(title, text, yes=default, no=other, timeout=timeout, runner=runner, system=system)
    return None if answer is None else (default if answer else other)


ACCESSIBILITY_SETTINGS = "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility"


def open_url(url: str, runner=subprocess.run, system: str | None = None) -> None:
    """Open a web page or a System Settings pane with the system's default handler."""
    system = system or platform.system()
    try:
        if system == "Darwin":
            runner(["open", url], capture_output=True, text=True)
        elif system == "Windows":
            import os

            os.startfile(url)  # type: ignore[attr-defined]
        else:
            runner(["xdg-open", url], capture_output=True, text=True)
    except Exception:
        pass


def app_browsers(system: str | None = None) -> list[str]:
    """Browsers that can show a page as its own app window (no tabs or address bar), best first. Every
    Windows 10 and 11 has Edge."""
    import os

    system = system or platform.system()
    if system == "Windows":
        roots = [os.environ.get(v) for v in ("ProgramFiles(x86)", "ProgramFiles", "LOCALAPPDATA")]
        found = [str(Path(r, *sub)) for sub in (("Microsoft", "Edge", "Application", "msedge.exe"),
                                                 ("Google", "Chrome", "Application", "chrome.exe")) for r in roots if r]
        return [f for f in dict.fromkeys(found) if Path(f).exists()]
    if system == "Linux":
        names = ["google-chrome", "chromium", "chromium-browser", "microsoft-edge", "brave-browser"]
        return [p for p in (shutil.which(n) for n in names) if p]
    return []


def open_window(url: str, spawn=subprocess.Popen, system: str | None = None) -> bool:
    """Show a page in its own window, like an app (Windows and Linux; the Mac has the real Study Stash
    app). Falls back to a browser tab. True when it opened as a window."""
    system = system or platform.system()
    for exe in app_browsers(system):
        try:
            spawn([exe, f"--app={url}", "--window-size=1180,820"], stdout=subprocess.DEVNULL,
                  stderr=subprocess.DEVNULL)
            return True
        except OSError:
            continue
    open_url(url)
    return False


def open_app(name: str, runner=subprocess.run, system: str | None = None, start=None) -> bool:
    """Bring an app to the front (macOS), or start one from its .exe (Windows; the app brings its own
    window forward when it's already open)."""
    system = system or platform.system()
    if system == "Windows":
        import os

        try:
            (start or os.startfile)(name)  # type: ignore[attr-defined]
            return True
        except Exception:
            return False
    if system != "Darwin":
        return False
    try:
        return runner(["open", "-a", name], capture_output=True, text=True).returncode == 0
    except Exception:
        return False


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
