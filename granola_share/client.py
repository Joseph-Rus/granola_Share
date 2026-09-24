"""The laptop side: watch your Granola account and send finished lectures to your library.

For each finished note it either sends it automatically or asks "Send it?", then POSTs it to
the library's /api/ingest (on your Mac mini, or whichever computer keeps the library).

On a Mac with a free Granola plan the transcript comes from the Granola app instead
(transcript_grab.py), so when a lecture finishes without one, the popup offers to open
Granola. The lecture is sent once the transcript is copied, and you hear back when the
library has filed it.
"""

from __future__ import annotations

import asyncio
import json
import threading
import time
import traceback
from dataclasses import dataclass, field
from datetime import date, datetime, timedelta, timezone
from urllib.parse import quote

import httpx

from . import dialogs
from .config import ClientConfig
from .granola import GranolaClient, Meeting, meeting_to_dict

OVERLAP_DAYS = 2
FILED_CHECK_HOURS = 24
QUICK_POLL_SECONDS = 30  # while waiting on a transcript or on the library to file something

# What transcript copying can do right now (see ShareClient.copy_state).
COPY_OFF, COPY_NEEDS_PERMISSION, COPY_ON = "off", "needs permission", "on"


@dataclass
class ClientReport:
    listed: int = 0
    considered: int = 0
    shared: list[tuple[str, str]] = field(default_factory=list)  # (title, class)
    skipped: list[str] = field(default_factory=list)
    pending: list[str] = field(default_factory=list)
    reshared: list[str] = field(default_factory=list)  # sent again once a copied transcript arrived
    filed: list[tuple[str, str]] = field(default_factory=list)  # (title, class) the library finished filing
    errors: list[str] = field(default_factory=list)


def http_post(url: str, payload: dict, headers: dict) -> dict:
    r = httpx.post(url, json=payload, headers=headers, timeout=180)
    r.raise_for_status()
    return r.json()


def http_get_json(url: str, headers: dict) -> dict | None:
    """None when the library doesn't know the note (or is too old to answer)."""
    r = httpx.get(url, headers=headers, timeout=15)
    if r.status_code == 404:
        return None
    r.raise_for_status()
    return r.json()


def check_server(server_url: str, pool_key: str) -> dict:
    """GET /api/health; raises on any failure with a readable message."""
    url = server_url.rstrip("/") + "/api/health"
    try:
        r = httpx.get(url, headers={"Authorization": f"Bearer {pool_key}"}, timeout=15)
    except httpx.HTTPError as e:
        raise RuntimeError(f"could not reach {url}: {e}") from e
    if r.status_code == 401:
        raise RuntimeError("wrong password")
    if r.status_code != 200:
        raise RuntimeError(f"unexpected response {r.status_code} from {url}")
    return r.json()


def _now() -> datetime:
    return datetime.now(timezone.utc)


class ShareClient:
    def __init__(self, cc: ClientConfig, granola: GranolaClient, *, ask=None, choose=None, notify=None, post=None,
                 get=None, log=print, transcripts=None, copy_state=None, allow_copying=None, capture_for=None,
                 handled_since=None, clock=_now):
        self.cc = cc
        self.granola = granola
        self.transcripts = transcripts  # TranscriptStore: transcripts copied from the Granola app (free plans)
        self._wake = threading.Event()
        self.ask = ask or dialogs.ask_yes_no
        self.choose = choose or dialogs.ask_choice
        self.notify = notify or dialogs.notify
        self.post = post or http_post
        self.get = get or http_get_json
        self.log = log
        # Live: whether transcripts can be copied from the Granola app right now.
        self.copy_state = copy_state or (lambda: COPY_OFF)
        self.allow_copying = allow_copying or (lambda: dialogs.open_url(dialogs.ACCESSIBILITY_SETTINGS))
        # capture_for(title) -> transcript: bring Granola forward, copy that lecture's transcript, come back.
        self.capture_for = capture_for or (lambda title: "")
        # handled_since(start) -> what happened when this recording ended: "copied", "asking", "saved",
        # "not now", or None when the laptop didn't see it end.
        self.handled_since = handled_since or (lambda started: None)
        self.clock = clock
        self.last_error: str | None = None  # the latest check's failure, shown on the Granola Share page
        self.state = self._load_state()

    # -- state -------------------------------------------------------------
    def _load_state(self) -> dict:
        p = self.cc.state_path
        if p.exists():
            try:
                return json.loads(p.read_text())
            except json.JSONDecodeError:
                pass
        return {"seen": {}, "last_poll": None}

    def _save_state(self) -> None:
        self.cc.state_path.parent.mkdir(parents=True, exist_ok=True)
        self.cc.state_path.write_text(json.dumps(self.state, indent=2))

    def _mark(self, m: Meeting, decision: str, extra: dict | None = None) -> None:
        self.state["seen"][m.id] = {"decision": decision, "title": m.title, "date": m.date[:10], "start": m.date[:19],
                                    "transcript_chars": len(m.transcript.strip()),
                                    "at": self.clock().isoformat(timespec="seconds"), **(extra or {})}
        self._save_state()

    def wake(self) -> None:
        """Check again soon (a transcript was just copied from the Granola app)."""
        self._wake.set()

    def _copied_transcript(self, title: str, day: str | None) -> str:
        return self.transcripts.find(title, day) if self.transcripts else ""

    def _since(self) -> date:
        last = self.state.get("last_poll")
        if last:
            return datetime.fromisoformat(last).date() - timedelta(days=OVERLAP_DAYS)
        return date.today() - timedelta(days=self.cc.share_lookback_days)

    @property
    def library(self) -> str:
        return self.cc.pool_name or "your library"

    # -- decisions ---------------------------------------------------------
    def decide(self, m: Meeting) -> bool | None:
        if self.cc.mode == "auto":
            return True
        text = f"Granola finished notes for:\n\n“{m.title}”\n{m.date[:10]}\n\nSend it to {self.library}?"
        return self.ask("granola-share", text, yes="Send", no="Skip", timeout=self.cc.dialog_timeout_seconds)

    def ask_save(self, m: Meeting, copy_state: str) -> str | None:
        """A finished lecture without its transcript: 'save', 'without', 'skip', or None (no answer)."""
        lines = [f"“{m.title}” is finished.", ""]
        if copy_state == COPY_NEEDS_PERMISSION:
            lines.append(f"Save it to {self.library}? To include its transcript, allow granola-share to read the "
                         "Granola window: click Allow, then turn on python3.12. The transcript follows once it's on.")
            go = "Allow"
        else:
            lines.append(f"Save it to {self.library} with its transcript? granola-share opens Granola, copies the "
                         "transcript, and brings you back.")
            go = "Save"
        buttons = (["Skip"] if self.cc.mode == "ask" else []) + ["Without transcript", go]
        answer = self.choose("granola-share", "\n".join(lines), buttons, default=go,
                             timeout=self.cc.dialog_timeout_seconds)
        return {go: "save", "Without transcript": "without", "Skip": "skip"}.get(answer) if answer else None

    def push(self, m: Meeting) -> dict:
        payload = meeting_to_dict(m)
        payload["owner"] = self.cc.display_name or m.owner
        if not self.cc.include_transcripts:
            payload["transcript"] = ""
        url = self.cc.server_url.rstrip("/") + "/api/ingest"
        return self.post(url, payload, {"Authorization": f"Bearer {self.cc.pool_key}"})

    def _share(self, m: Meeting, rep: ClientReport, message: str | None = None) -> None:
        try:
            res = self.push(m)
        except Exception as e:
            rep.errors.append(f"{m.id}: {e}")
            rep.pending.append(m.title)
            self._mark(m, "pending", {"error": str(e)})
            self.log(f"[client] could not share '{m.title}': {e}")
            return
        cls = str(res.get("class_name") or "")  # empty while the server is still sorting it
        rep.shared.append((m.title, cls or "being sorted"))
        self._mark(m, "shared", {"class_name": cls, "filed": False} if cls else {"filed": False})
        with_t = " with its transcript" if m.transcript.strip() else ""
        self.log(f"[client] shared '{m.title}'{with_t}" + (f" → {cls}" if cls else ""))
        if message:
            self.notify("granola-share", message)  # otherwise the one notification is "it's filed"

    # -- main loop ---------------------------------------------------------
    async def poll_once(self, since: date | None = None) -> ClientReport:
        rep = ClientReport()
        seen = self.state["seen"]
        async with self.granola.session() as s:
            stubs = await self.granola.list_meetings(s, since=since or self._since())
            rep.listed = len(stubs)
            todo = [m for m in stubs if seen.get(m.id, {}).get("decision") not in ("shared", "skipped")]
            rep.considered = len(todo)
            self.log(f"[client] {len(stubs)} notes since {since or self._since()}, {len(todo)} to consider")
            full_by_id: dict[str, Meeting] = {}
            if todo:
                try:
                    full_by_id = {m.id: m for m in await self.granola.get_meetings(s, [m.id for m in todo])}
                except Exception as e:
                    rep.errors.append(f"get_meetings: {e}")
                    self.log(f"[client] get_meetings failed, using list data: {e}")
            for stub in todo:
                m = full_by_id.get(stub.id) or stub
                if not m.notes_markdown:
                    m.notes_markdown = stub.notes_markdown
                if self.cc.include_transcripts and not m.transcript:
                    m.transcript = await self.granola.get_transcript(s, m.id) or self._copied_transcript(m.title, m.date)
                self._handle(m, seen.get(m.id, {}), rep)
            if self.cc.include_transcripts and self.transcripts:
                await self._reshare_with_copied_transcripts(s, rep)
        self._check_filed(rep)
        self.state["last_poll"] = self.clock().isoformat()
        self._save_state()
        return rep

    def _handle(self, m: Meeting, entry: dict, rep: ClientReport) -> None:
        copy_state = self.copy_state() if self.cc.include_transcripts else COPY_OFF
        message = None
        ended = self.handled_since(_start(m))
        if ended == "asking":
            rep.pending.append(m.title)  # the "Save it?" popup from the end of the recording is still up
            return
        if not m.transcript.strip() and copy_state != COPY_OFF and ended is None:
            # Nothing copied when the recording ended (the laptop was off, or it was recorded elsewhere).
            choice = self.ask_save(m, copy_state)
            if choice == "save":
                if copy_state == COPY_NEEDS_PERMISSION:
                    self.allow_copying()
                else:
                    m.transcript = self.capture_for(m.title) or ""
                    if not m.transcript:
                        message = (f"Saved “{m.title}” without its transcript. Open it in Granola any time and the "
                                   "transcript is added.")
            decision = {"save": True, "without": True, "skip": False}.get(choice) if choice else None
        elif ended == "saved":
            decision = True  # they already said Save when the recording ended
        else:
            decision = self.decide(m)
        if decision is None:
            rep.pending.append(m.title)
            self._mark(m, "pending")
        elif not decision:
            rep.skipped.append(m.title)
            self._mark(m, "skipped")
            self.log(f"[client] skipped '{m.title}'")
        else:
            self._share(m, rep, message)

    def missing_transcripts(self, days: int = 14) -> set[str]:
        """Titles of recent lectures sent without a transcript: opening one in Granola adds it."""
        from .transcript_grab import _norm_title

        cutoff = self.clock() - timedelta(days=days)
        return {_norm_title(e.get("title", "")) for e in self.state["seen"].values()
                if e.get("decision") == "shared" and not e.get("transcript_chars")
                and datetime.fromisoformat(e["at"]) > cutoff}

    async def _reshare_with_copied_transcripts(self, s, rep: ClientReport, limit: int = 5) -> None:
        """Lectures already shared without a transcript: send them again once one was copied from the app."""
        waiting = []
        for mid, entry in self.state["seen"].items():
            if entry.get("decision") != "shared":
                continue
            body = self._copied_transcript(entry.get("title", ""), entry.get("start") or entry.get("date"))
            if body and len(body.strip()) > 1.1 * entry.get("transcript_chars", 0):
                waiting.append((mid, body))
        if not waiting:
            return
        waiting = waiting[:limit]
        try:
            full = {m.id: m for m in await self.granola.get_meetings(s, [mid for mid, _ in waiting])}
        except Exception as e:
            rep.errors.append(f"get_meetings (re-share): {e}")
            return
        for mid, body in waiting:
            m = full.get(mid)
            if m is None:
                continue
            m.transcript = body
            try:
                res = self.push(m)
            except Exception as e:
                rep.errors.append(f"{mid}: {e}")
                continue
            cls = str(res.get("class_name") or self.state["seen"][mid].get("class_name") or "")
            self._mark(m, "shared", {"class_name": cls, "filed": False, "resent": True})
            rep.reshared.append(m.title)
            self.log(f"[client] sent '{m.title}' again with its transcript ({len(body)} chars)")

    def _check_filed(self, rep: ClientReport, limit: int = 10) -> None:
        """Say so once the library has summarized and filed what was sent."""
        cutoff = self.clock() - timedelta(hours=FILED_CHECK_HOURS)
        waiting = [(mid, e) for mid, e in self.state["seen"].items()
                   if e.get("decision") == "shared" and e.get("filed") is False
                   and datetime.fromisoformat(e["at"]) > cutoff][:limit]
        for mid, entry in waiting:
            url = f"{self.cc.server_url.rstrip('/')}/api/notes/{quote(mid, safe='')}/status"
            try:
                info = self.get(url, {"Authorization": f"Bearer {self.cc.pool_key}"})
            except Exception:
                continue  # library unreachable right now: try again next time
            if info is None:
                entry["filed"] = None  # a library too old to say; stop asking
                continue
            if info.get("status") not in (None, "done"):
                continue
            entry["filed"], entry["class_name"] = True, info.get("class_name") or entry.get("class_name")
            where = f" under {entry['class_name']}" if entry.get("class_name") else ""
            how = (" Its notes were rewritten from the transcript." if entry.get("resent") else
                   " Notes written from the transcript." if info.get("summary_model") else "")
            rep.filed.append((entry.get("title", ""), entry.get("class_name") or ""))
            self.notify("granola-share", f"“{entry.get('title')}” is in {self.library}{where}.{how}")

    def _busy(self) -> bool:
        """Something to follow up on soon: the library finishing what was just sent."""
        recent = self.clock() - timedelta(minutes=30)
        return any(e.get("decision") == "shared" and e.get("filed") is False
                   and datetime.fromisoformat(e["at"]) > recent
                   for e in self.state["seen"].values())

    def run_loop(self, stop=None) -> None:
        while True:
            self._wake.clear()
            try:
                asyncio.run(self.poll_once())
                self.last_error = None
            except Exception as e:
                self.last_error = str(e)
                self.log(f"[client] error: {e}\n{traceback.format_exc()}")
            wait = QUICK_POLL_SECONDS if self._busy() else self.cc.poll_interval_seconds
            for _ in range(wait):
                if stop is not None and stop.is_set():
                    return
                if self._wake.wait(1):
                    time.sleep(5)  # let a burst of copies settle, then check
                    break


def _start(m: Meeting) -> datetime | None:
    """When the lecture started, as a naive local time (Granola's dates carry the time when it has one)."""
    try:
        return datetime.fromisoformat(m.date[:19]) if len(m.date) >= 16 else None
    except ValueError:
        return None
