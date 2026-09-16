"""The sync loop: ask Granola for new notes, classify them, write them to the pool."""

from __future__ import annotations

import asyncio
import json
import time
import traceback
from dataclasses import dataclass, field
from datetime import date, datetime, timedelta, timezone

from .classify import classify
from .config import Config
from .granola import GranolaClient, Meeting
from .store import Store

FIRST_RUN_LOOKBACK_DAYS = 30  # free plan only exposes 30 days anyway
OVERLAP_DAYS = 2


@dataclass
class SyncReport:
    listed: int = 0
    new: int = 0
    saved: list[tuple[str, str]] = field(default_factory=list)  # (title, class)
    errors: list[str] = field(default_factory=list)
    account: dict | None = None  # {"email", "workspace", ...} from get_account_info, when known


def _since(store: Store) -> date:
    last = store.get_state("last_sync")
    if last:
        return datetime.fromisoformat(last).date() - timedelta(days=OVERLAP_DAYS)
    return date.today() - timedelta(days=FIRST_RUN_LOOKBACK_DAYS)


def _now_iso() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="seconds")


def account_label(info: dict | None) -> str:
    """'me@example.com · Joey's workspace' or 'not signed in' (mirrors granola.account_label)."""
    if not info or not info.get("email"):
        return "not signed in"
    ws = info.get("workspace")
    return f"{info['email']} · {ws}" if ws else str(info["email"])


async def fetch_account_info(client, session) -> dict | None:
    """Ask the client who we are signed in as. Old fakes/clients without the method give None."""
    fn = getattr(client, "get_account_info", None)
    if fn is None:
        return None
    try:
        info = await fn(session)
    except Exception:
        return None
    return info if isinstance(info, dict) else None


def record_sync_state(store: Store, info: dict | None, listed: int) -> None:
    """Persist what the last successful poll saw so the web /status page can say it out loud."""
    store.set_state("sync_account", json.dumps(info or {}))
    store.set_state("sync_listed", str(int(listed)))
    store.set_state("sync_checked_at", _now_iso())
    store.set_state("sync_error", "")


def record_sync_error(store: Store, error: str) -> None:
    store.set_state("sync_error", str(error)[:2000])
    store.set_state("sync_checked_at", _now_iso())


async def sync_once(cfg: Config, client: GranolaClient, store: Store, chat=None, log=print) -> SyncReport:
    try:
        return await _sync_once(cfg, client, store, chat, log)
    except Exception as e:
        record_sync_error(store, f"{type(e).__name__}: {e}")
        raise


async def _sync_once(cfg: Config, client: GranolaClient, store: Store, chat, log) -> SyncReport:
    report = SyncReport()
    since = _since(store)
    async with client.session() as session:
        stubs = await client.list_meetings(session, since=since)
        report.listed = len(stubs)
        report.account = await fetch_account_info(client, session)
        record_sync_state(store, report.account, len(stubs))
        log(f"[sync] signed in as {account_label(report.account)} · {len(stubs)} notes since {since}")
        known = store.known_ids()
        new_ids = [m.id for m in stubs if m.id not in known]
        report.new = len(new_ids)
        log(f"[sync] {len(stubs)} meetings since {since}, {len(new_ids)} new")
        if not new_ids:
            store.set_state("last_sync", datetime.now(timezone.utc).isoformat())
            return report
        stub_by_id = {m.id: m for m in stubs}
        full: list[Meeting] = []
        try:
            full = await client.get_meetings(session, new_ids)
        except Exception as e:  # fall back to the stub content if the batch call fails
            report.errors.append(f"get_meetings: {e}")
            log(f"[sync] get_meetings failed, using list data: {e}")
        full_by_id = {m.id: m for m in full}
        for mid in new_ids:
            m = full_by_id.get(mid) or stub_by_id[mid]
            if not m.notes_markdown and mid in stub_by_id:
                m.notes_markdown = stub_by_id[mid].notes_markdown
            if cfg.include_transcripts and not m.transcript:
                m.transcript = await client.get_transcript(session, mid)
            try:
                c = classify(m, cfg, chat)
                path = store.save(m, c)
                report.saved.append((m.title, c.class_name))
                log(f"[sync] saved '{m.title}' → {c.class_name} ({c.by} {c.confidence:.2f}) {path.name}")
            except Exception as e:
                report.errors.append(f"{mid}: {e}")
                log(f"[sync] failed on {mid}: {e}\n{traceback.format_exc()}")
        _dump_debug(cfg, full or list(stub_by_id.values()))
    store.set_state("last_sync", datetime.now(timezone.utc).isoformat())
    return report


def _dump_debug(cfg: Config, meetings: list[Meeting]) -> None:
    """Keep the last raw payload around so we can adjust field mapping if Granola changes."""
    try:
        cfg.debug_dir.mkdir(parents=True, exist_ok=True)
        (cfg.debug_dir / "last_meetings.json").write_text(
            json.dumps([m.raw for m in meetings[:5]], indent=2)[:500_000]
        )
    except Exception:
        pass


def run_loop(cfg: Config, client: GranolaClient, store: Store, log=print, stop=None) -> None:
    while True:
        try:
            asyncio.run(sync_once(cfg, client, store, log=log))
        except Exception as e:
            log(f"[sync] error: {e}")
            try:
                record_sync_error(store, f"{type(e).__name__}: {e}")
            except Exception as e2:  # the store itself is broken; keep the loop alive anyway
                log(f"[sync] could not record error: {e2}")
        if stop is not None and stop.is_set():
            return
        for _ in range(cfg.poll_interval_seconds):
            if stop is not None and stop.is_set():
                return
            time.sleep(1)
