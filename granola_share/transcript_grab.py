"""Copy transcripts out of the Granola app window (macOS), for accounts whose plan doesn't share them.

Granola's MCP connector only returns transcripts on paid plans, but the app shows a
"Copy transcript" button on every plan. While Granola is in front with the transcript panel
open, this presses that button through the macOS Accessibility API (no mouse, no window
switching), keeps the text, and puts the user's clipboard, cursor, and selection back.

What the Granola app allows, found by probing it (7.580):
- The window's content is readable over Accessibility from any desktop, but only the rows of
  the transcript near the scroll position exist: the list is virtualized. "Copy transcript"
  gives the whole thing.
- Chromium ignores clicks and clipboard writes from a window that isn't in front, so this
  only acts while Granola is frontmost, and only in a pause in the user's typing.
- Pressing the button moves keyboard focus to it, so focus is put back afterwards.
"""

from __future__ import annotations

import hashlib
import json
import re
import threading
import time
from dataclasses import dataclass
from datetime import date, datetime, timedelta
from pathlib import Path

BUNDLE_ID = "com.granola.app"
COPY_LABEL = "Copy transcript"
TICK_SECONDS = 3
IDLE_BEFORE_ACTING = 2.0  # seconds without keyboard or mouse input
AFTER_RECORDING = 8  # seconds after the microphone goes quiet: Granola has written the last lines
SETTLE_OPENED = 3  # a transcript that isn't changing: copy soon after it's opened
SETTLE_LIVE = 15  # still being recorded: wait until new lines stop arriving
MIN_GAP = 20  # never copy more often than this


# --- the copied text -----------------------------------------------------------------

@dataclass
class Copied:
    title: str
    date: date | None
    body: str  # the transcript without Granola's header lines


_HEADER = re.compile(r"^(Meeting Title|Date|Meeting participants|Transcript):\s*(.*)$")


def parse_copied(text: str, today: date | None = None) -> Copied | None:
    """Split what "Copy transcript" puts on the clipboard into title, date, and transcript."""
    title, when, body_start = "", None, 0
    lines = (text or "").splitlines()
    for i, line in enumerate(lines[:8]):
        m = _HEADER.match(line.strip())
        if not m:
            continue
        key, value = m.groups()
        if key == "Meeting Title":
            title = value.strip()
        elif key == "Date":
            when = _parse_day(value, today or date.today())
        elif key == "Transcript":
            body_start = i + 1
            break
    body = "\n".join(lines[body_start:]).strip()
    if not body:
        return None
    return Copied(title, when, body)


def _parse_day(value: str, today: date) -> date | None:
    """'Sep 24' (Granola leaves out the year) or 'Sep 24, 2026'."""
    value = value.strip()
    for fmt in ("%b %d, %Y", "%B %d, %Y", "%b %d %Y"):
        try:
            return datetime.strptime(value, fmt).date()
        except ValueError:
            pass
    for fmt in ("%b %d", "%B %d"):
        try:
            d = datetime.strptime(f"{value} {today.year}", f"{fmt} %Y").date()
            return d.replace(year=today.year - 1) if d > today else d
        except ValueError:
            pass
    return None


def _norm_title(s: str) -> str:
    return re.sub(r"[^a-z0-9]+", " ", (s or "").lower()).strip()


class TranscriptStore:
    """Copied transcripts on disk, one file per note: <home>/transcripts/<date> <title>.txt"""

    def __init__(self, home: Path):
        self.dir = Path(home) / "transcripts"

    def save(self, copied: Copied, raw: str, recorded_to: datetime | None = None) -> tuple[Path, bool]:
        """Keep the newest copy, unless it is much shorter (a partial panel) than one we have.

        `recorded_to` is when the recording stopped, for copies made right then: Granola may not have
        named the note yet, so those are matched to their note by time instead of title.
        """
        self.dir.mkdir(parents=True, exist_ok=True)
        slug = re.sub(r"[\\/:*?\"<>|\x00-\x1f]", " ", copied.title or "untitled").strip()[:80]
        stamp = f" {recorded_to:%H%M}" if recorded_to else ""
        path = self.dir / f"{copied.date or 'undated'}{stamp} {slug}.txt"
        if path.exists():
            old = parse_copied(path.read_text(encoding="utf-8"))
            if old and (old.body == copied.body or len(copied.body) < 0.8 * len(old.body)):
                return path, False
        path.write_text(raw, encoding="utf-8")
        if recorded_to:
            path.with_suffix(".json").write_text(json.dumps({"recorded_to": recorded_to.isoformat(timespec="seconds")}))
        return path, True

    def find(self, title: str, day: str | date | None = None) -> str:
        """The transcript for a note: matched on title (and date), else by time for copies made as the
        recording stopped. `day` can be the note's full start time ('2026-09-24T15:00:00')."""
        want = _norm_title(title)
        if not self.dir.exists():
            return ""
        when = day if isinstance(day, date) or day is None else _iso_day(day)
        started = _iso_time(day) if isinstance(day, str) else None
        best, by_time = "", None
        for path in self.dir.glob("*.txt"):
            c = parse_copied(path.read_text(encoding="utf-8", errors="replace"))
            if not c:
                continue
            if want and _norm_title(c.title) == want:
                if when and c.date and abs((c.date - when).days) > 1:
                    continue
                if len(c.body) > len(best):
                    best = c.body
            elif started:
                stopped = _recorded_to(path)
                # The first recording that ended after this note started (and within a long lecture's length).
                if stopped and started <= stopped <= started + timedelta(hours=5):
                    if by_time is None or stopped < by_time[0]:
                        by_time = (stopped, c.body)
        return best or (by_time[1] if by_time else "")


def _iso_day(s: str) -> date | None:
    m = re.match(r"(\d{4})-(\d{2})-(\d{2})", s or "")
    return date(int(m[1]), int(m[2]), int(m[3])) if m else None


def _iso_time(s: str) -> datetime | None:
    """'2026-09-24T15:00:00' → a naive local datetime; None for a bare date."""
    if not s or len(s) < 16:
        return None
    try:
        return datetime.fromisoformat(s[:19]).replace(tzinfo=None)
    except ValueError:
        return None


def _recorded_to(path: Path) -> datetime | None:
    try:
        return datetime.fromisoformat(json.loads(path.with_suffix(".json").read_text())["recorded_to"])
    except (OSError, ValueError, KeyError):
        return None


# --- the live app (macOS Accessibility) -------------------------------------------------

class MacUI:
    """Granola's window through the Accessibility API. Methods never raise; they return None/False."""

    def __init__(self):
        import ApplicationServices as AS
        import Quartz
        from AppKit import NSPasteboard, NSPasteboardItem, NSRunningApplication

        self.AS, self.Quartz = AS, Quartz
        self.NSPasteboard, self.NSPasteboardItem = NSPasteboard, NSPasteboardItem
        self.NSRunningApplication = NSRunningApplication
        self._pid: int | None = None
        self._app = None

    # -- plumbing
    def _get(self, el, name):
        err, v = self.AS.AXUIElementCopyAttributeValue(el, name, None)
        return v if err == 0 else None

    def _many(self, el, names):
        err, vals = self.AS.AXUIElementCopyMultipleAttributeValues(el, names, 0, None)
        if err != 0 or vals is None:
            return [None] * len(names)
        # A missing attribute comes back as an AXValue holding an error code, not as None.
        return [None if type(v).__name__ == "AXValueRef" else v for v in vals]

    def app(self):
        apps = self.NSRunningApplication.runningApplicationsWithBundleIdentifier_(BUNDLE_ID)
        pid = apps[0].processIdentifier() if apps and len(apps) else None
        if pid is None:
            self._pid = self._app = None
            return None
        if pid != self._pid:
            self._pid, self._app = pid, self.AS.AXUIElementCreateApplication(pid)
            # Electron only builds the page's accessibility tree when asked; invisible to the user.
            self.AS.AXUIElementSetAttributeValue(self._app, "AXManualAccessibility", True)
        return self._app

    def trusted(self, prompt: bool = False) -> bool:
        try:
            return bool(self.AS.AXIsProcessTrustedWithOptions({self.AS.kAXTrustedCheckOptionPrompt: prompt}))
        except Exception:
            return False

    def idle_seconds(self) -> float:
        q = self.Quartz
        return float(q.CGEventSourceSecondsSinceLastEventType(q.kCGEventSourceStateHIDSystemState, q.kCGAnyInputEventType))

    def frontmost(self) -> bool:
        app = self.app()
        return bool(app is not None and self._get(app, "AXFrontmost"))

    def _running(self):
        apps = self.NSRunningApplication.runningApplicationsWithBundleIdentifier_(BUNDLE_ID)
        return apps[0] if apps and len(apps) else None

    # -- recording, the transcript panel, and who's in front
    def recording(self) -> bool | None:
        """Is Granola capturing audio right now? None when macOS can't say (before 14.2)."""
        return _granola_capturing()

    def note_title(self) -> str:
        """The open note's title (the first text area in the note), or ''."""
        try:
            win = self._get(self.app(), "AXMainWindow")
            stack = [win] if win is not None else []
            while stack:
                el = stack.pop()
                role, value, kids = self._many(el, ["AXRole", "AXValue", "AXChildren"])
                if role == "AXTextArea" and isinstance(value, str) and value.strip():
                    return value.strip()[:120]
                stack.extend(reversed(list(kids or [])))
        except Exception:
            pass
        return ""

    def _toggle(self):
        """The transcript button in the note's bottom bar (the waveform icon). It has no label, so it's
        found by place: the one unlabeled button along the bottom edge of the window."""
        import re

        def box(el, name):
            return [float(x) for x in re.findall(r"[xywh]:(-?[\d.]+)", str(self._get(el, name)))]

        win = self._get(self.app(), "AXMainWindow")
        if win is None:
            return None
        wy, wh = box(win, "AXPosition")[1], box(win, "AXSize")[1]
        found, stack = [], [win]
        while stack:
            el = stack.pop()
            role, title, desc, kids = self._many(el, ["AXRole", "AXTitle", "AXDescription", "AXChildren"])
            if role == "AXButton" and not (title or desc):
                classes = " ".join(list(self._get(el, "AXDOMClassList") or []))
                pos = box(el, "AXPosition")
                if pos and pos[1] > wy + wh - 120 and "group/button" in classes:
                    found.append(el)
            stack.extend(reversed(list(kids or [])))
        return found[0] if len(found) == 1 else None  # never guess between two buttons

    def open_panel(self, wait: float = 3.0) -> bool:
        if self._panel()[0] is not None:
            return True
        toggle = self._toggle()
        if toggle is None:
            return False
        self.AS.AXUIElementPerformAction(toggle, "AXPress")
        deadline = time.time() + wait
        while time.time() < deadline:
            if self._panel()[0] is not None:
                return True
            time.sleep(0.2)
        return False

    def close_panel(self, wait: float = 2.0) -> None:
        button = self._find_button("Close transcript")
        if button is None:
            return
        self.AS.AXUIElementPerformAction(button, "AXPress")
        # Wait for it to close while Granola is still in front: a window sent to the back stops updating.
        deadline = time.time() + wait
        while time.time() < deadline and self._panel()[0] is not None:
            time.sleep(0.1)

    def _find_button(self, label: str):
        win = self._get(self.app(), "AXMainWindow")
        stack = [win] if win is not None else []
        while stack:
            el = stack.pop()
            role, title, desc, kids = self._many(el, ["AXRole", "AXTitle", "AXDescription", "AXChildren"])
            if role == "AXButton" and label in (title, desc):
                return el
            stack.extend(reversed(list(kids or [])))
        return None

    def open_note(self, title: str, wait: float = 3.0) -> bool:
        """Show the lecture called `title`: click it where Granola lists it (the current view, else Home).
        Only an element whose text is exactly that title is clicked, and the result is checked."""
        want = _norm_title(title)
        if not want:
            return False
        if _norm_title(self.note_title()) == want:
            return True
        for attempt in range(2):
            target = self._clickable_with_text(want)
            if target is not None:
                self.AS.AXUIElementPerformAction(target, "AXPress")
                deadline = time.time() + wait
                while time.time() < deadline:
                    if _norm_title(self.note_title()) == want:
                        return True
                    time.sleep(0.25)
            if attempt == 0:
                home = self._find_link("Home")
                if home is None:
                    return False
                self.AS.AXUIElementPerformAction(home, "AXPress")
                time.sleep(1.5)
        return False

    def _clickable_with_text(self, want: str):
        win = self._get(self.app(), "AXMainWindow")
        stack = [win] if win is not None else []
        while stack:
            el = stack.pop()
            role, value, title, kids = self._many(el, ["AXRole", "AXValue", "AXTitle", "AXChildren"])
            text = value if isinstance(value, str) else title if isinstance(title, str) else ""
            if role in ("AXStaticText", "AXLink", "AXButton") and _norm_title(text) == want:
                node = el
                for _ in range(5):  # the row that takes the click is usually a parent of the text
                    err, actions = self.AS.AXUIElementCopyActionNames(node, None)
                    if err == 0 and actions and "AXPress" in actions:
                        return node
                    node = self._get(node, "AXParent")
                    if node is None:
                        break
            stack.extend(reversed(list(kids or [])))
        return None

    def _find_link(self, label: str):
        win = self._get(self.app(), "AXMainWindow")
        stack = [win] if win is not None else []
        while stack:
            el = stack.pop()
            role, title, desc, kids = self._many(el, ["AXRole", "AXTitle", "AXDescription", "AXChildren"])
            if role == "AXLink" and label in (title, desc):
                return el
            stack.extend(reversed(list(kids or [])))
        return None

    def front_app(self) -> str | None:
        """Bundle ID of the app in front, asked live. (NSWorkspace needs a run loop to notice changes, and the
        system-wide Accessibility element often answers "cannot complete"; lsappinfo is built in and reliable.)"""
        import re
        import subprocess

        try:
            asn = subprocess.run(["lsappinfo", "front"], capture_output=True, text=True, timeout=5).stdout.strip()
            out = subprocess.run(["lsappinfo", "info", "-only", "bundleid", asn],
                                 capture_output=True, text=True, timeout=5).stdout
            m = re.search(r'"CFBundleIdentifier"="([^"]+)"', out)
            return m.group(1) if m else None
        except Exception:
            return None

    def activate(self, bundle_id: str) -> None:
        import subprocess

        try:
            subprocess.run(["open", "-b", bundle_id], capture_output=True, timeout=10)
        except Exception:
            pass

    def bring_to_front(self) -> None:
        """Open Granola, however it's tucked away: launch it, unhide it, restore minimized windows, activate it."""
        import subprocess

        try:
            ra = self._running()
            if ra is not None:
                if ra.isHidden():
                    ra.unhide()
                app = self.app()
                for w in self._get(app, "AXWindows") or []:
                    if self._get(w, "AXMinimized"):
                        self.AS.AXUIElementSetAttributeValue(w, "AXMinimized", False)
            # Through LaunchServices: allowed from a background process, and it also sends the "reopen"
            # event that makes Electron show its window again.
            subprocess.run(["open", "-a", "Granola"], capture_output=True, timeout=10)
        except Exception:
            pass

    def _panel(self):
        """(copy button, transcript panel element) or (None, None) when the panel is closed."""
        app = self.app()
        win = self._get(app, "AXMainWindow") if app is not None else None
        if win is None:
            return None, None
        stack = [win]
        while stack:
            el = stack.pop()
            role, title, desc, kids = self._many(el, ["AXRole", "AXTitle", "AXDescription", "AXChildren"])
            if role == "AXButton" and COPY_LABEL in (title, desc):
                panel = el
                for _ in range(6):  # climb to the group that also holds the transcript rows
                    panel = self._get(panel, "AXParent")
                    if panel is None or len(self._texts(panel, limit=3)) >= 3:
                        break
                return el, panel
            stack.extend(reversed(list(kids or [])))
        return None, None

    def _texts(self, el, limit: int | None = None) -> list[str]:
        out, stack = [], [el]
        while stack and (limit is None or len(out) < limit):
            node = stack.pop()
            role, value, kids = self._many(node, ["AXRole", "AXValue", "AXChildren"])
            if role == "AXStaticText" and isinstance(value, str) and value.strip():
                out.append(value.strip())
            stack.extend(reversed(list(kids or [])))
        return out

    def rows_fingerprint(self) -> str | None:
        """A hash of the transcript rows on screen, or None when the transcript panel is closed."""
        try:
            button, panel = self._panel()
            if button is None:
                return None
            return hashlib.sha1("\n".join(self._texts(panel) if panel is not None else []).encode()).hexdigest()
        except Exception:
            return None

    def copy_transcript(self, timeout: float = 2.0) -> str | None:
        """Press "Copy transcript" and return the text; clipboard, focus, and selection are put back."""
        try:
            button, _ = self._panel()
            if button is None:
                return None
            app = self.app()
            focused = self._get(app, "AXFocusedUIElement")
            selection = self._get(focused, "AXSelectedTextRange") if focused is not None else None
            text = press_and_read(self.NSPasteboard.generalPasteboard(), self.NSPasteboardItem,
                                  lambda: self.AS.AXUIElementPerformAction(button, "AXPress"), timeout)
            if focused is not None and self._get(app, "AXFocusedUIElement") != focused:
                self.AS.AXUIElementSetAttributeValue(focused, "AXFocused", True)
                if selection is not None:
                    self.AS.AXUIElementSetAttributeValue(focused, "AXSelectedTextRange", selection)
            return text
        except Exception:
            return None


def _granola_capturing() -> bool | None:
    """True while one of Granola's processes records from an input device (CoreAudio process objects)."""
    try:
        import ctypes
        import ctypes.util
        import struct

        ca = ctypes.CDLL(ctypes.util.find_library("CoreAudio"))
        cf = ctypes.CDLL(ctypes.util.find_library("CoreFoundation"))

        class Addr(ctypes.Structure):
            _fields_ = [("sel", ctypes.c_uint32), ("scope", ctypes.c_uint32), ("elem", ctypes.c_uint32)]

        def cc(s):
            return struct.unpack(">I", s.encode())[0]

        ca.AudioObjectGetPropertyDataSize.argtypes = [ctypes.c_uint32, ctypes.POINTER(Addr), ctypes.c_uint32,
                                                      ctypes.c_void_p, ctypes.POINTER(ctypes.c_uint32)]
        ca.AudioObjectGetPropertyData.argtypes = [ctypes.c_uint32, ctypes.POINTER(Addr), ctypes.c_uint32,
                                                  ctypes.c_void_p, ctypes.POINTER(ctypes.c_uint32), ctypes.c_void_p]
        cf.CFStringGetCString.argtypes = [ctypes.c_void_p, ctypes.c_char_p, ctypes.c_long, ctypes.c_uint32]

        def get(obj, sel, ctype):
            a, size = Addr(cc(sel), cc("glob"), 0), ctypes.c_uint32(0)
            if ca.AudioObjectGetPropertyDataSize(obj, ctypes.byref(a), 0, None, ctypes.byref(size)) != 0:
                return None
            n = size.value // ctypes.sizeof(ctype)
            buf = (ctype * max(n, 1))()
            if ca.AudioObjectGetPropertyData(obj, ctypes.byref(a), 0, None, ctypes.byref(size), buf) != 0:
                return None
            return list(buf[:n])

        procs = get(1, "prs#", ctypes.c_uint32)  # kAudioHardwarePropertyProcessObjectList
        if procs is None:
            return None
        for p in procs:
            ref = (get(p, "pbid", ctypes.c_void_p) or [None])[0]  # kAudioProcessPropertyBundleID
            name = ctypes.create_string_buffer(256)
            if not ref or not cf.CFStringGetCString(ctypes.c_void_p(ref), name, 256, 0x08000100):
                continue
            if name.value.decode().startswith(BUNDLE_ID) and (get(p, "piri", ctypes.c_uint32) or [0])[0]:
                return True  # kAudioProcessPropertyIsRunningInput
        return False
    except Exception:
        return None


def press_and_read(pb, item_cls, press, timeout: float = 2.0, sleep=time.sleep) -> str | None:
    """Save every item on the clipboard, press, read what the app copied, and restore the clipboard.

    The user's clipboard is never read as text. If something else writes to the clipboard while
    we wait (the user copying), their new copy is left alone.
    """
    saved = [{t: it.dataForType_(t) for t in it.types()} for it in (pb.pasteboardItems() or [])]
    before = pb.changeCount()
    press()
    deadline = time.time() + timeout
    while pb.changeCount() == before and time.time() < deadline:
        sleep(0.01)
    copied_at = pb.changeCount()
    if copied_at == before:
        return None  # the app didn't copy anything
    text = pb.stringForType_("public.utf8-plain-text")
    if pb.changeCount() == copied_at:
        pb.clearContents()
        items = []
        for item in saved:
            new = item_cls.alloc().init()
            for t, data in item.items():
                new.setData_forType_(data, t)
            items.append(new)
        if items:
            pb.writeObjects_(items)
    return text or None


# --- deciding when to copy ---------------------------------------------------------------

class TranscriptGrabber:
    """Runs in the laptop's background watcher. `tick()` is one look at Granola.

    The main moment is a recording ending (Granola stops using the microphone): the transcript is
    copied then, with no clicks from the user if Granola is in front, or after one "Save" if it isn't.
    It also copies the transcript of any older lecture the user opens in Granola.
    """

    def __init__(self, home: Path, ui=None, store: TranscriptStore | None = None, *, on_new=None,
                 log=print, clock=time.time, open_url=None, ask_save=None, sleep=time.sleep, now=datetime.now,
                 allowed_now=None, restart=None):
        self.home = Path(home)
        self.ui = ui
        self.store = store or TranscriptStore(home)
        self.on_new = on_new or (lambda copied: None)
        self.log = log
        self.clock = clock
        self.now = now  # wall-clock time for recording ends (matched against Granola's meeting times)
        self.sleep = sleep
        self.open_url = open_url
        # ask_save(title) -> bool | None: "Save it with its transcript?" when Granola isn't in front.
        self.ask_save = ask_save or (lambda title: None)
        # wanted() -> normalized titles of lectures still missing a transcript: opening one in Granola is enough.
        self.wanted = lambda: set()
        self.tried: dict[str, float] = {}
        self.lock = threading.Lock()  # one set of clicks in Granola at a time
        self.seen_hash: str | None = None
        self.changes: list[float] = []
        self.copied_hash: str | None = None
        self.last_copy = 0.0
        self.was_recording = False
        self.stop_seen: float | None = None  # when the microphone went quiet (clock)
        self.stopped_at: datetime | None = None  # the same moment, wall-clock
        self.handled: list[tuple[datetime, str]] = []  # (recording end, "copied" | "asking" | "saved" | "not now")
        self.status: dict = {}
        # macOS applies the Accessibility switch to new processes only: a fresh one is asked, and the
        # service restarts itself once the answer is yes (at most once every 30 minutes).
        self.allowed_now = allowed_now or _allowed_in_fresh_process
        self.restart = restart or _restart_service
        self.asked_at: float | None = None
        self.fresh_checked = -1e9
        try:
            restarted = json.loads(self.status_path.read_text()).get("restarted_for_access")
        except (OSError, ValueError, AttributeError):
            restarted = None
        if restarted:
            self.status["restarted_for_access"] = restarted

    @property
    def status_path(self) -> Path:
        return self.store.dir / "status.json"

    def _write_status(self, **kw) -> None:
        self.status.update(kw, checked_at=datetime.now().isoformat(timespec="seconds"))
        try:
            self.store.dir.mkdir(parents=True, exist_ok=True)
            self.status_path.write_text(json.dumps(self.status, indent=2))
        except OSError:
            pass

    def copy_state(self) -> str:
        """For the client's popups: can a transcript be copied right now, or is permission missing?"""
        from .client import COPY_NEEDS_PERMISSION, COPY_ON

        return COPY_ON if self.ui.trusted() else COPY_NEEDS_PERMISSION

    def request_permission(self) -> None:
        """Put python3.12 in the Accessibility list and open that page of System Settings for the user."""
        from .dialogs import ACCESSIBILITY_SETTINGS, open_url

        self.ui.trusted(prompt=True)  # macOS adds this Python to the list (switched off) and may show its own alert
        self.asked_at = self.clock()
        (self.open_url or open_url)(ACCESSIBILITY_SETTINGS)
        self.log("[transcripts] opened System Settings → Privacy & Security → Accessibility")

    def handled_since(self, started: datetime | None) -> str | None:
        """What happened when the recording that started at `started` ended: 'copied' (automatically),
        'asking' (the popup is up), 'saved' (they clicked Save), 'not now', or None (nothing seen)."""
        if not started:
            return None
        for t, outcome in self.handled:
            if started <= t <= started + timedelta(hours=5):
                return outcome
        return None

    def _allowed_after_restart(self) -> bool:
        """Has the user switched this Python on since it started? Asked every few seconds for 15 minutes
        after they clicked Allow (they're in System Settings), else every 5 minutes."""
        now = self.clock()
        soon = self.asked_at is not None and now - self.asked_at < 900
        if now - self.fresh_checked < (5 if soon else 300):
            return False
        self.fresh_checked = now
        try:
            last = datetime.fromisoformat(self.status.get("restarted_for_access") or "")
            if datetime.now() - last < timedelta(minutes=30):
                return False  # a restart didn't help last time; don't loop
        except ValueError:
            pass
        return self.allowed_now()

    def _handled(self, stopped: datetime, outcome: str) -> None:
        self.handled = [h for h in self.handled if h[0] != stopped][-19:] + [(stopped, outcome)]

    # -- doing the clicks ----------------------------------------------------------------
    def capture(self, bring_forward: bool = False, want_title: str | None = None,
                recorded_to: datetime | None = None) -> Copied | None:
        """Copy the open lecture's transcript: open the panel if it's closed, copy, close it again.

        With `bring_forward`, Granola is brought to the front first and the user is put back in the app
        they were using afterwards. With `want_title`, a copy of some other lecture isn't returned
        (it's still kept for that lecture).
        """
        ui = self.ui
        with self.lock:
            previous = ui.front_app() if bring_forward else None
            try:
                if bring_forward:
                    ui.bring_to_front()
                    for _ in range(30):
                        if ui.frontmost():
                            break
                        self.sleep(0.2)
                if not ui.frontmost():
                    return None
                was_showing = ui.note_title()
                if want_title and not ui.open_note(want_title):
                    self._write_status(last_error=f"Couldn't find '{want_title}' in Granola")
                    if was_showing and ui.note_title() != was_showing:
                        ui.open_note(was_showing)  # don't leave Granola somewhere else
                    return None
                opened = False
                if ui.rows_fingerprint() is None:
                    if not ui.open_panel():
                        self._write_status(last_error="Couldn't open the transcript in Granola")
                        return None
                    opened = True
                hash_before = ui.rows_fingerprint()
                text = ui.copy_transcript()
                if opened:
                    ui.close_panel()
            finally:
                if previous and previous != BUNDLE_ID:
                    ui.activate(previous)
            copied = parse_copied(text or "")
            if copied is None:
                self._write_status(last_error="Copy transcript gave nothing (is the transcript empty?)")
                return None
            self.copied_hash, self.last_copy = hash_before, self.clock()
            path, changed = self.store.save(copied, text, recorded_to=recorded_to)
            self._write_status(last_copy={"title": copied.title, "date": str(copied.date), "chars": len(copied.body),
                                          "at": datetime.now().isoformat(timespec="seconds")}, last_error=None)
            if changed:
                self.log(f"[transcripts] copied '{copied.title}' ({len(copied.body)} chars) → {path.name}")
                self.on_new(copied)
        if want_title and _norm_title(copied.title) != _norm_title(want_title):
            return None
        return copied

    def capture_for(self, title: str) -> str:
        """For the client's "Save" button: bring Granola forward, copy this lecture's transcript, come back."""
        copied = self.capture(bring_forward=True, want_title=title)
        return copied.body if copied else ""

    def _recording_ended(self) -> str:
        """Just after a recording: copy it now if Granola is in front, else ask once."""
        stopped = self.stopped_at or self.now()
        ui = self.ui
        if ui.frontmost():
            for _ in range(15):  # the user just clicked Stop; let them finish whatever they're doing
                if ui.idle_seconds() >= IDLE_BEFORE_ACTING:
                    break
                self.sleep(1)
            if self.capture(recorded_to=stopped):
                self._handled(stopped, "copied")
                return "copied after recording"
        self._handled(stopped, "asking")
        if self.ask_save(ui.note_title()):
            self._handled(stopped, "saved")
            return "copied after asking" if self.capture(bring_forward=True, recorded_to=stopped) else "copy failed"
        self._handled(stopped, "not now")
        return "not now"

    # -- one look ---------------------------------------------------------------------------
    def tick(self) -> str:
        ui = self.ui
        if not ui.trusted():
            self._write_status(trusted=False)
            if self._allowed_after_restart():
                self._write_status(restarted_for_access=datetime.now().isoformat(timespec="seconds"))
                self.log("[transcripts] Accessibility is on now; restarting to use it")
                self.restart()
                return "restarting"
            return "not allowed"
        if self.status.get("trusted") is not True:
            self._write_status(trusted=True)

        recording = ui.recording()
        if recording:
            self.was_recording, self.stop_seen = True, None
        elif recording is False and self.was_recording:
            if self.stop_seen is None:
                self.stop_seen, self.stopped_at = self.clock(), self.now()
            elif self.clock() - self.stop_seen >= AFTER_RECORDING:  # Granola writes the last lines by then
                self.was_recording, self.stop_seen = False, None
                return self._recording_ended()
            return "recording ended"

        # Otherwise: an older lecture's transcript, opened in Granola by the user, is copied quietly too.
        if not ui.frontmost():
            return "background"
        h = ui.rows_fingerprint()
        now = self.clock()
        if h is None:
            # A lecture still missing its transcript is on screen: copy it even though the panel is closed.
            title = _norm_title(ui.note_title())
            if (title and title in self.wanted() and now - self.tried.get(title, -1e9) > 600
                    and now - self.last_copy >= MIN_GAP and ui.idle_seconds() >= IDLE_BEFORE_ACTING):
                self.tried[title] = now
                return "copied" if self.capture() else "nothing copied"
            return "panel closed"
        if h != self.seen_hash:
            self.seen_hash = h
            self.changes = [t for t in self.changes if now - t < 60] + [now]
        if h == self.copied_hash:
            return "up to date"
        live = len([t for t in self.changes if now - t < 45]) >= 3  # new lines keep arriving: recording
        if now - self.changes[-1] < (SETTLE_LIVE if live else SETTLE_OPENED):
            return "settling"
        if now - self.last_copy < MIN_GAP or ui.idle_seconds() < IDLE_BEFORE_ACTING:
            return "waiting"
        copied = self.capture()
        return "copied" if copied else "nothing copied"

    def run(self, stop: threading.Event) -> None:
        while not stop.is_set():
            try:
                self.tick()
            except Exception as e:  # never take the watcher down
                self.log(f"[transcripts] error: {e}")
            stop.wait(TICK_SECONDS)

    def start(self, stop: threading.Event) -> threading.Thread:
        t = threading.Thread(target=self.run, args=(stop,), name="transcripts", daemon=True)
        t.start()
        return t


def _allowed_in_fresh_process() -> bool:
    """What a brand-new process of this same Python is told: macOS caches "not allowed" per process."""
    import subprocess
    import sys

    code = "import sys, ApplicationServices as A; sys.exit(0 if A.AXIsProcessTrusted() else 1)"
    try:
        return subprocess.run([sys.executable, "-c", code], capture_output=True, timeout=30).returncode == 0
    except (OSError, subprocess.SubprocessError):
        return False


def _restart_service() -> None:
    """launchd starts the background service again right away (KeepAlive). A copy started some other
    way just keeps running; the next start picks the permission up."""
    import os

    if os.environ.get("GRANOLA_SHARE_SERVICE") == "1":
        os._exit(0)


def available() -> str | None:
    """None when grabbing can work here, else why not."""
    import platform

    if platform.system() != "Darwin":
        return "only on macOS for now"
    try:
        import ApplicationServices  # noqa: F401
        import Quartz  # noqa: F401
    except ImportError:
        return "the macOS Accessibility bindings (pyobjc) are missing; rerun the installer"
    return None
