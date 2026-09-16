"""SQLite index plus a Markdown folder tree (the "pool")."""

from __future__ import annotations

import json
import re
import shutil
import sqlite3
import threading
from functools import wraps
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path

from .config import UNSORTED
from .granola import Meeting

SCHEMA = """
CREATE TABLE IF NOT EXISTS notes (
    id TEXT PRIMARY KEY,
    title TEXT,
    date TEXT,
    owner TEXT,
    attendees TEXT,
    folder TEXT,
    class_name TEXT,
    confidence REAL,
    classified_by TEXT,
    lecture_title TEXT,
    topics TEXT,
    md_path TEXT,
    has_transcript INTEGER DEFAULT 0,
    raw_json TEXT,
    first_seen TEXT,
    updated_at TEXT
);
CREATE TABLE IF NOT EXISTS sync_state (key TEXT PRIMARY KEY, value TEXT);
"""


@dataclass
class Classification:
    class_name: str
    confidence: float
    by: str  # folder | rules | ollama | none | human
    lecture_title: str = ""
    topics: list[str] | None = None


def slugify(text: str, max_len: int = 80) -> str:
    text = re.sub(r"[\\/:*?\"<>|\x00-\x1f]", " ", text or "").strip()
    text = re.sub(r"\s+", " ", text)
    return (text or "untitled")[:max_len].strip()


def _date_prefix(date_str: str) -> str:
    m = re.match(r"(\d{4}-\d{2}-\d{2})", date_str or "")
    return m.group(1) if m else datetime.now(timezone.utc).strftime("%Y-%m-%d")


def render_markdown(m: Meeting, c: Classification) -> str:
    topics = ", ".join(c.topics or [])
    lines = [
        "---",
        f"title: {json.dumps(m.title)}",
        f"lecture_title: {json.dumps(c.lecture_title or m.title)}",
        f"class: {json.dumps(c.class_name)}",
        f"date: {json.dumps(m.date)}",
        f"source: {json.dumps(m.owner)}",
        f"granola_id: {json.dumps(m.id)}",
        f"granola_folder: {json.dumps(m.folder)}",
        f"attendees: {json.dumps(m.attendees)}",
        f"topics: {json.dumps(c.topics or [])}",
        f"classified_by: {c.by} ({c.confidence:.2f})",
        "---",
        "",
        f"# {c.lecture_title or m.title}",
        "",
        f"*{m.date}*  ·  class: **{c.class_name}**" + (f"  ·  topics: {topics}" if topics else ""),
        "",
        "## Notes",
        "",
        m.notes_markdown.strip() or "_(no AI notes returned)_",
        "",
    ]
    if m.private_notes.strip():
        lines += ["## Private notes", "", m.private_notes.strip(), ""]
    if m.transcript.strip():
        lines += ["## Transcript", "", m.transcript.strip(), ""]
    return "\n".join(lines)


def _locked(fn):
    @wraps(fn)
    def inner(self, *a, **kw):
        with self.lock:
            return fn(self, *a, **kw)
    return inner


class Store:
    def __init__(self, db_path: Path, pool_dir: Path):
        self.db_path = Path(db_path)
        self.pool_dir = Path(pool_dir)
        self.db_path.parent.mkdir(parents=True, exist_ok=True)
        self.pool_dir.mkdir(parents=True, exist_ok=True)
        # The web server calls us from a thread pool; serialize access with a lock.
        self.lock = threading.RLock()
        self.conn = sqlite3.connect(str(self.db_path), check_same_thread=False)
        self.conn.row_factory = sqlite3.Row
        self.conn.executescript(SCHEMA)

    # -- sync state --------------------------------------------------------
    @_locked
    def get_state(self, key: str) -> str | None:
        row = self.conn.execute("SELECT value FROM sync_state WHERE key=?", (key,)).fetchone()
        return row["value"] if row else None

    @_locked
    def set_state(self, key: str, value: str) -> None:
        self.conn.execute(
            "INSERT INTO sync_state(key,value) VALUES(?,?) ON CONFLICT(key) DO UPDATE SET value=excluded.value",
            (key, value),
        )
        self.conn.commit()

    # -- notes -------------------------------------------------------------
    @_locked
    def known_ids(self) -> set[str]:
        return {r["id"] for r in self.conn.execute("SELECT id FROM notes")}

    @_locked
    def class_dir(self, class_name: str) -> Path:
        d = self.pool_dir / slugify(class_name or UNSORTED, 60)
        d.mkdir(parents=True, exist_ok=True)
        return d

    def _target_path(self, m: Meeting, class_name: str) -> Path:
        base = f"{_date_prefix(m.date)} {slugify(m.title)}"
        path = self.class_dir(class_name) / f"{base}.md"
        if path.exists():
            existing = self.conn.execute("SELECT id FROM notes WHERE md_path=?", (str(path),)).fetchone()
            if existing and existing["id"] != m.id:
                path = self.class_dir(class_name) / f"{base} [{m.id[-6:]}].md"
        return path

    @_locked
    def save(self, m: Meeting, c: Classification) -> Path:
        path = self._target_path(m, c.class_name)
        path.write_text(render_markdown(m, c))
        now = datetime.now(timezone.utc).isoformat(timespec="seconds")
        row = self.conn.execute("SELECT md_path, first_seen FROM notes WHERE id=?", (m.id,)).fetchone()
        if row and row["md_path"] and row["md_path"] != str(path) and Path(row["md_path"]).exists():
            Path(row["md_path"]).unlink()
        self.conn.execute(
            """INSERT INTO notes(id,title,date,owner,attendees,folder,class_name,confidence,classified_by,
                lecture_title,topics,md_path,has_transcript,raw_json,first_seen,updated_at)
               VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)
               ON CONFLICT(id) DO UPDATE SET title=excluded.title, date=excluded.date, owner=excluded.owner,
                attendees=excluded.attendees, folder=excluded.folder, class_name=excluded.class_name,
                confidence=excluded.confidence, classified_by=excluded.classified_by,
                lecture_title=excluded.lecture_title, topics=excluded.topics, md_path=excluded.md_path,
                has_transcript=excluded.has_transcript, raw_json=excluded.raw_json, updated_at=excluded.updated_at""",
            (
                m.id, m.title, m.date, m.owner, json.dumps(m.attendees), m.folder, c.class_name, c.confidence,
                c.by, c.lecture_title or m.title, json.dumps(c.topics or []), str(path),
                1 if m.transcript.strip() else 0, json.dumps(m.raw)[:200_000],
                row["first_seen"] if row else now, now,
            ),
        )
        self.conn.commit()
        return path

    @_locked
    def get(self, note_id: str) -> sqlite3.Row | None:
        return self.conn.execute("SELECT * FROM notes WHERE id=?", (note_id,)).fetchone()

    @_locked
    def list_notes(self, class_name: str | None = None) -> list[sqlite3.Row]:
        if class_name is None:
            return list(self.conn.execute("SELECT * FROM notes ORDER BY date DESC, title"))
        return list(self.conn.execute("SELECT * FROM notes WHERE class_name=? ORDER BY date DESC, title", (class_name,)))

    @_locked
    def classes_summary(self) -> list[tuple[str, int]]:
        rows = self.conn.execute(
            "SELECT class_name, COUNT(*) AS n FROM notes GROUP BY class_name ORDER BY class_name"
        ).fetchall()
        return [(r["class_name"], r["n"]) for r in rows]

    def class_counts(self) -> dict[str, int]:
        return dict(self.classes_summary())

    @_locked
    def count(self) -> int:
        return int(self.conn.execute("SELECT COUNT(*) AS n FROM notes").fetchone()["n"])

    @_locked
    def latest_date(self, class_name: str) -> str | None:
        """Newest note date filed under `class_name`, or None when the class is empty."""
        row = self.conn.execute("SELECT MAX(date) AS d FROM notes WHERE class_name=?", (class_name,)).fetchone()
        return row["d"] or None

    @_locked
    def owners_summary(self) -> list[tuple[str, int, str]]:
        """(owner, note count, latest note date) per contributor, busiest first."""
        rows = self.conn.execute(
            "SELECT COALESCE(owner, '') AS owner, COUNT(*) AS n, MAX(date) AS d FROM notes "
            "GROUP BY COALESCE(owner, '') ORDER BY n DESC, owner"
        ).fetchall()
        return [(r["owner"], r["n"], r["d"] or "") for r in rows]

    @_locked
    def recent(self, limit: int = 10) -> list[sqlite3.Row]:
        """The most recently filed notes (by arrival, not lecture date)."""
        return list(self.conn.execute(
            "SELECT * FROM notes ORDER BY first_seen DESC, date DESC, title LIMIT ?", (int(limit),)
        ))

    @_locked
    def set_class(self, note_id: str, class_name: str, by: str = "human") -> Path | None:
        row = self.get(note_id)
        if not row:
            return None
        old = Path(row["md_path"]) if row["md_path"] else None
        raw = json.loads(row["raw_json"] or "{}")
        from .granola import normalize_meeting  # local import avoids cycle at module load

        m = normalize_meeting(raw) if raw else Meeting(id=note_id, title=row["title"], date=row["date"])
        if not m.notes_markdown and old and old.exists():
            m.notes_markdown = old.read_text()
        c = Classification(class_name=class_name, confidence=1.0, by=by,
                           lecture_title=row["lecture_title"] or "", topics=json.loads(row["topics"] or "[]"))
        new_path = self.save(m, c)
        # remove now-empty old class dir
        if old and old.parent != new_path.parent and old.parent.exists() and not any(old.parent.iterdir()):
            shutil.rmtree(old.parent, ignore_errors=True)
        return new_path
