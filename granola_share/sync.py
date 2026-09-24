"""The server's own sync loop: ask Granola for new notes and queue them for the pipeline."""

from __future__ import annotations

import asyncio
import json
import time
import traceback
from dataclasses import dataclass, field
from datetime import date, datetime, timedelta, timezone

from .config import Config
from .granola import GranolaClient, Meeting
from .store import Store

FIRST_RUN_LOOKBACK_DAYS = 30  # free plan only exposes 30 days anyway
OVERLAP_DAYS = 2


@dataclass
class SyncReport:
    listed: int = 0
    new: int = 0
    queued: list[str] = field(default_factory=list)  # titles handed to the pipeline
    errors: list[str] = field(default_factory=list)


def _since(store: Store) -> date:
    last = store.get_state("last_sync")
    if last:
        return datetime.fromisoformat(last).date() - timedelta(days=OVERLAP_DAYS)
    return date.today() - timedelta(days=FIRST_RUN_LOOKBACK_DAYS)


async def sync_once(cfg: Config, client: GranolaClient, store: Store, log=print, on_queued=None) -> SyncReport:
    report = SyncReport()
    since = _since(store)
    async with client.session() as session:
        stubs = await client.list_meetings(session, since=since)
        report.listed = len(stubs)
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
                store.enqueue(m)
                report.queued.append(m.title)
                log(f"[sync] queued '{m.title}'")
            except Exception as e:
                report.errors.append(f"{mid}: {e}")
                log(f"[sync] failed on {mid}: {e}\n{traceback.format_exc()}")
        _dump_debug(cfg, full or list(stub_by_id.values()))
    if report.queued and on_queued:
        on_queued()
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


def run_loop(cfg: Config, client: GranolaClient, store: Store, log=print, stop=None, on_queued=None) -> None:
    while True:
        try:
            asyncio.run(sync_once(cfg, client, store, log=log, on_queued=on_queued))
        except Exception as e:
            log(f"[sync] error: {e}")
        if stop is not None and stop.is_set():
            return
        for _ in range(cfg.poll_interval_seconds):
            if stop is not None and stop.is_set():
                return
            time.sleep(1)
