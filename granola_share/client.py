"""The friend's laptop client.

Watches the user's own Granola account (official MCP, their own login), and for each
finished note either asks "Share to the pool?" or shares automatically, then POSTs it to
the pool server's /api/ingest.
"""

from __future__ import annotations

import asyncio
import json
import time
import traceback
from dataclasses import dataclass, field
from datetime import date, datetime, timedelta, timezone

import httpx

from . import dialogs
from .config import ClientConfig
from .granola import GranolaClient, Meeting, meeting_to_dict

OVERLAP_DAYS = 2


@dataclass
class ClientReport:
    listed: int = 0
    considered: int = 0
    shared: list[tuple[str, str]] = field(default_factory=list)  # (title, class)
    skipped: list[str] = field(default_factory=list)
    pending: list[str] = field(default_factory=list)
    errors: list[str] = field(default_factory=list)


def http_post(url: str, payload: dict, headers: dict) -> dict:
    r = httpx.post(url, json=payload, headers=headers, timeout=180)
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
        raise RuntimeError("the server rejected the pool password")
    if r.status_code != 200:
        raise RuntimeError(f"unexpected response {r.status_code} from {url}")
    return r.json()


class ShareClient:
    def __init__(self, cc: ClientConfig, granola: GranolaClient, *, ask=None, notify=None, post=None, log=print):
        self.cc = cc
        self.granola = granola
        self.ask = ask or dialogs.ask_yes_no
        self.notify = notify or dialogs.notify
        self.post = post or http_post
        self.log = log
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
        self.state["seen"][m.id] = {"decision": decision, "title": m.title,
                                    "at": datetime.now(timezone.utc).isoformat(timespec="seconds"), **(extra or {})}
        self._save_state()

    def _since(self) -> date:
        last = self.state.get("last_poll")
        if last:
            return datetime.fromisoformat(last).date() - timedelta(days=OVERLAP_DAYS)
        return date.today() - timedelta(days=self.cc.share_lookback_days)

    # -- decisions ---------------------------------------------------------
    def decide(self, m: Meeting) -> bool | None:
        if self.cc.mode == "auto":
            return True
        pool = self.cc.pool_name or "the notes pool"
        text = f"Granola finished notes for:\n\n“{m.title}”\n{m.date[:10]}\n\nShare it to {pool}?"
        return self.ask("granola-share", text, timeout=self.cc.dialog_timeout_seconds)

    def push(self, m: Meeting) -> dict:
        payload = meeting_to_dict(m)
        payload["owner"] = self.cc.display_name or m.owner
        if not self.cc.include_transcripts:
            payload["transcript"] = ""
        url = self.cc.server_url.rstrip("/") + "/api/ingest"
        return self.post(url, payload, {"Authorization": f"Bearer {self.cc.pool_key}"})

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
                    m.transcript = await self.granola.get_transcript(s, m.id)
                decision = self.decide(m)
                if decision is None:
                    rep.pending.append(m.title)
                    self._mark(m, "pending")
                    continue
                if not decision:
                    rep.skipped.append(m.title)
                    self._mark(m, "skipped")
                    self.log(f"[client] skipped '{m.title}'")
                    continue
                try:
                    res = self.push(m)
                    cls = str(res.get("class_name", "?"))
                    rep.shared.append((m.title, cls))
                    self._mark(m, "shared", {"class_name": cls})
                    self.log(f"[client] shared '{m.title}' → {cls}")
                    self.notify("granola-share", f"Shared “{m.title}” → {cls}")
                except Exception as e:
                    rep.errors.append(f"{m.id}: {e}")
                    rep.pending.append(m.title)
                    self._mark(m, "pending", {"error": str(e)})
                    self.log(f"[client] could not share '{m.title}': {e}")
        self.state["last_poll"] = datetime.now(timezone.utc).isoformat()
        self._save_state()
        return rep

    def run_loop(self, stop=None) -> None:
        while True:
            try:
                asyncio.run(self.poll_once())
            except Exception as e:
                self.log(f"[client] error: {e}\n{traceback.format_exc()}")
            for _ in range(self.cc.poll_interval_seconds):
                if stop is not None and stop.is_set():
                    return
                time.sleep(1)
