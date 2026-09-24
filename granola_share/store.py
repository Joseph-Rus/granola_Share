"""SQLite index plus a Markdown folder tree (the "pool").

A shared note arrives as a full payload (transcript included) and is queued. The pipeline
(pipeline.py) then writes our own summary from the transcript, sorts the note into a class,
and saves it here as a Markdown file.
"""

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
from .granola import Meeting, meeting_from_dict, meeting_to_dict, normalize_meeting

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

# Columns added after 0.1.0. Databases from older versions get them when opened.
MIGRATIONS = {
    "payload_json": "TEXT",  # the full shared note (meeting_to_dict), so it can be re-processed
    "summary_md": "TEXT",  # our summary written from the transcript
    "summary_model": "TEXT",
    "status": "TEXT DEFAULT 'done'",  # queued | working | done
    "error": "TEXT",
}

QUEUED, WORKING, DONE, FAILED = "queued", "working", "done", "failed"
# A note is in the library once it has a file, even while it is being re-summarized.
FILED = "md_path IS NOT NULL AND class_name IS NOT NULL"


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


def demote_headings(text: str) -> str:
    """Push every Markdown heading down one level so a summary nests under '## Summary'."""
    out, fenced = [], False
    for line in text.splitlines():
        if line.lstrip().startswith("```"):
            fenced = not fenced
        if not fenced and re.match(r"#{1,5}\s", line):
            line = "#" + line
        out.append(line)
    return "\n".join(out)


def render_markdown(m: Meeting, c: Classification, summary_md: str = "", summary_model: str = "",
                    keep_granola: bool = False) -> str:
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
        f"summary_by: {json.dumps(summary_model if summary_md.strip() else 'granola')}",
        "---",
        "",
        f"# {c.lecture_title or m.title}",
        "",
        f"*{m.date}*  ·  class: **{c.class_name}**" + (f"  ·  topics: {topics}" if topics else ""),
        "",
    ]
    if summary_md.strip():
        lines += ["## Summary", "", demote_headings(summary_md.strip()), "",
                  f"_Written by {summary_model} from the transcript._", ""]
        if keep_granola and m.notes_markdown.strip():
            lines += ["## Granola's notes", "", demote_headings(m.notes_markdown.strip()), ""]
    else:
        lines += ["## Notes", "", m.notes_markdown.strip() or "_(no AI notes returned)_", ""]
    if m.private_notes.strip():
        lines += ["## Private notes", "", m.private_notes.strip(), ""]
    if m.transcript.strip():
        lines += ["## Transcript", "", m.transcript.strip(), ""]
    return "\n".join(lines)


def _md_section(text: str, heading: str) -> str:
    """Body of '## heading' in a note file written by render_markdown (used for pre-0.2 rows)."""
    m = re.search(rf"^## {re.escape(heading)}\n\n(.*?)(?=^## |\Z)", text, re.DOTALL | re.MULTILINE)
    return m.group(1).strip() if m else ""


def _locked(fn):
    @wraps(fn)
    def inner(self, *a, **kw):
        with self.lock:
            return fn(self, *a, **kw)
    return inner


def _like(q: str) -> str:
    return "%" + q.replace("\\", "\\\\").replace("%", "\\%").replace("_", "\\_") + "%"


def _now() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="seconds")


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
        self._migrate()

    def _migrate(self) -> None:
        cols = {r["name"] for r in self.conn.execute("PRAGMA table_info(notes)")}
        for name, decl in MIGRATIONS.items():
            if name not in cols:
                self.conn.execute(f"ALTER TABLE notes ADD COLUMN {name} {decl}")
        self.conn.execute("CREATE INDEX IF NOT EXISTS notes_status ON notes(status)")
        self.conn.commit()

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

    # -- the processing queue ------------------------------------------------
    @_locked
    def enqueue(self, m: Meeting) -> None:
        """Accept a shared note. The pipeline picks it up and files it."""
        now = _now()
        self.conn.execute(
            """INSERT INTO notes(id,title,date,owner,attendees,folder,has_transcript,raw_json,payload_json,
                status,error,first_seen,updated_at)
               VALUES(?,?,?,?,?,?,?,?,?,?,NULL,?,?)
               ON CONFLICT(id) DO UPDATE SET title=excluded.title, date=excluded.date, owner=excluded.owner,
                attendees=excluded.attendees, folder=excluded.folder, has_transcript=excluded.has_transcript,
                raw_json=excluded.raw_json, payload_json=excluded.payload_json, status=excluded.status,
                error=NULL, updated_at=excluded.updated_at""",
            (m.id, m.title, m.date, m.owner, json.dumps(m.attendees), m.folder, 1 if m.transcript.strip() else 0,
             json.dumps(m.raw)[:200_000], json.dumps(meeting_to_dict(m)), QUEUED, now, now),
        )
        self.conn.commit()

    @_locked
    def claim_next(self) -> sqlite3.Row | None:
        row = self.conn.execute(
            "SELECT id FROM notes WHERE status=? ORDER BY updated_at, first_seen LIMIT 1", (QUEUED,)
        ).fetchone()
        if not row:
            return None
        self.conn.execute("UPDATE notes SET status=? WHERE id=?", (WORKING, row["id"]))
        self.conn.commit()
        return self.get(row["id"])

    @_locked
    def reset_working(self) -> int:
        """After a restart, anything that was mid-processing goes back in the queue."""
        n = self.conn.execute("UPDATE notes SET status=? WHERE status=?", (QUEUED, WORKING)).rowcount
        self.conn.commit()
        return n

    @_locked
    def requeue(self, note_id: str) -> bool:
        n = self.conn.execute("UPDATE notes SET status=?, updated_at=? WHERE id=?", (QUEUED, _now(), note_id)).rowcount
        self.conn.commit()
        return n > 0

    @_locked
    def requeue_all(self) -> int:
        n = self.conn.execute("UPDATE notes SET status=?, updated_at=? WHERE status IN (?, ?)",
                              (QUEUED, _now(), DONE, FAILED)).rowcount
        self.conn.commit()
        return n

    @_locked
    def mark_failed(self, note_id: str, error: str) -> None:
        # Only if still ours: a re-share or re-queue meanwhile means it runs again instead.
        self.conn.execute("UPDATE notes SET status=?, error=?, updated_at=? WHERE id=? AND status=?",
                          (FAILED, error[:2000], _now(), note_id, WORKING))
        self.conn.commit()

    @_locked
    def finish(self, claimed, m: Meeting, c: Classification, summary_md: str = "", summary_model: str = "",
               error: str = "", keep_granola: bool = False) -> Path | None:
        """Save the pipeline's result for a row it claimed, unless the note changed in the meantime.

        The pipeline works for minutes on a snapshot. If the note was deleted, re-shared, or re-queued
        since, its result is stale: drop it (a re-queued note simply runs again). If a person moved the
        note while it was being written, their class wins.
        """
        row = self.get(claimed["id"])
        if not row or row["status"] != WORKING or row["updated_at"] != claimed["updated_at"]:
            return None
        if row["classified_by"] == "human" and row["class_name"]:
            c = Classification(row["class_name"], 1.0, "human", c.lecture_title, c.topics)
        return self.save(m, c, summary_md, summary_model, error, keep_granola)

    @_locked
    def status_counts(self) -> dict[str, int]:
        rows = self.conn.execute("SELECT status, COUNT(*) AS n FROM notes GROUP BY status").fetchall()
        return {r["status"] or DONE: r["n"] for r in rows}

    @_locked
    def processing(self) -> list[sqlite3.Row]:
        """Notes not filed yet: waiting, being written right now, or stuck with an error."""
        return list(self.conn.execute(
            "SELECT * FROM notes WHERE status IN (?, ?, ?) ORDER BY updated_at", (WORKING, QUEUED, FAILED)))

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
    def save(self, m: Meeting, c: Classification, summary_md: str = "", summary_model: str = "",
             error: str = "", keep_granola: bool = False) -> Path:
        """Write the Markdown file and mark the note done."""
        path = self._target_path(m, c.class_name)
        path.write_text(render_markdown(m, c, summary_md, summary_model, keep_granola), encoding="utf-8")
        now = _now()
        row = self.conn.execute("SELECT md_path, first_seen FROM notes WHERE id=?", (m.id,)).fetchone()
        if row and row["md_path"] and row["md_path"] != str(path) and Path(row["md_path"]).exists():
            Path(row["md_path"]).unlink()
        self.conn.execute(
            """INSERT INTO notes(id,title,date,owner,attendees,folder,class_name,confidence,classified_by,
                lecture_title,topics,md_path,has_transcript,raw_json,payload_json,summary_md,summary_model,
                status,error,first_seen,updated_at)
               VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)
               ON CONFLICT(id) DO UPDATE SET title=excluded.title, date=excluded.date, owner=excluded.owner,
                attendees=excluded.attendees, folder=excluded.folder, class_name=excluded.class_name,
                confidence=excluded.confidence, classified_by=excluded.classified_by,
                lecture_title=excluded.lecture_title, topics=excluded.topics, md_path=excluded.md_path,
                has_transcript=excluded.has_transcript, raw_json=excluded.raw_json,
                payload_json=excluded.payload_json, summary_md=excluded.summary_md,
                summary_model=excluded.summary_model, status=excluded.status, error=excluded.error,
                updated_at=excluded.updated_at""",
            (
                m.id, m.title, m.date, m.owner, json.dumps(m.attendees), m.folder, c.class_name, c.confidence,
                c.by, c.lecture_title or m.title, json.dumps(c.topics or []), str(path),
                1 if m.transcript.strip() else 0, json.dumps(m.raw)[:200_000], json.dumps(meeting_to_dict(m)),
                summary_md or None, summary_model or None, DONE, error or None,
                row["first_seen"] if row else now, now,
            ),
        )
        self.conn.commit()
        return path

    @_locked
    def meeting(self, row: sqlite3.Row) -> Meeting:
        """The full shared note behind a row (rebuilt from the Markdown file for pre-0.2 rows)."""
        if row["payload_json"]:
            return meeting_from_dict(json.loads(row["payload_json"]))
        raw = json.loads(row["raw_json"] or "{}")
        try:
            m = normalize_meeting(raw)
        except ValueError:
            m = Meeting(id=row["id"], raw=raw)
        m.id = row["id"]
        m.title, m.date = row["title"] or m.title, row["date"] or m.date
        m.owner = row["owner"] or m.owner
        m.attendees = json.loads(row["attendees"] or "[]") or m.attendees
        m.folder = row["folder"] or m.folder
        path = Path(row["md_path"]) if row["md_path"] else None
        if path and path.exists():
            text = path.read_text(encoding="utf-8")
            m.notes_markdown = m.notes_markdown or _md_section(text, "Notes")
            m.private_notes = m.private_notes or _md_section(text, "Private notes")
            m.transcript = m.transcript or _md_section(text, "Transcript")
        return m

    @_locked
    def get(self, note_id: str) -> sqlite3.Row | None:
        return self.conn.execute("SELECT * FROM notes WHERE id=?", (note_id,)).fetchone()

    @_locked
    def list_notes(self, class_name: str | None = None, limit: int | None = None) -> list[sqlite3.Row]:
        sql, args = f"SELECT * FROM notes WHERE {FILED}", []
        if class_name is not None:
            sql += " AND class_name=?"
            args.append(class_name)
        sql += " ORDER BY date DESC, title"
        if limit:
            sql += f" LIMIT {int(limit)}"
        return list(self.conn.execute(sql, args))

    @_locked
    def search(self, q: str, limit: int = 200) -> list[sqlite3.Row]:
        like = _like(q.strip())
        fields = ["title", "lecture_title", "topics", "summary_md", "owner", "class_name", "payload_json"]
        where = " OR ".join(f"{f} LIKE ? ESCAPE '\\'" for f in fields)
        return list(self.conn.execute(
            f"SELECT * FROM notes WHERE {FILED} AND ({where}) ORDER BY date DESC LIMIT {int(limit)}",
            [like] * len(fields)))

    @_locked
    def classes_summary(self) -> list[tuple[str, int]]:
        rows = self.conn.execute(
            f"SELECT class_name, COUNT(*) AS n FROM notes WHERE {FILED} GROUP BY class_name ORDER BY class_name"
        ).fetchall()
        return [(r["class_name"], r["n"]) for r in rows]

    @_locked
    def set_class(self, note_id: str, class_name: str, by: str = "human", keep_granola: bool = False) -> Path | None:
        row = self.get(note_id)
        if not row:
            return None
        if row["status"] in (QUEUED, WORKING):
            # The pipeline writes the file when it's done and keeps this choice (see finish()).
            self.conn.execute("UPDATE notes SET class_name=?, classified_by=?, confidence=1.0 WHERE id=?",
                              (class_name, by, note_id))
            self.conn.commit()
            return Path(row["md_path"]) if row["md_path"] else None
        old = Path(row["md_path"]) if row["md_path"] else None
        m = self.meeting(row)
        c = Classification(class_name=class_name, confidence=1.0, by=by,
                           lecture_title=row["lecture_title"] or "", topics=json.loads(row["topics"] or "[]"))
        new_path = self.save(m, c, row["summary_md"] or "", row["summary_model"] or "", row["error"] or "",
                             keep_granola=keep_granola)
        self._drop_empty_dir(old, new_path)
        return new_path

    @_locked
    def delete(self, note_id: str) -> bool:
        row = self.get(note_id)
        if not row:
            return False
        path = Path(row["md_path"]) if row["md_path"] else None
        if path and path.exists():
            path.unlink()
        self.conn.execute("DELETE FROM notes WHERE id=?", (note_id,))
        self.conn.commit()
        self._drop_empty_dir(path, None)
        return True

    def _drop_empty_dir(self, old: Path | None, new: Path | None) -> None:
        if old and (new is None or old.parent != new.parent) and old.parent.exists() \
                and old.parent != self.pool_dir and not any(old.parent.iterdir()):
            shutil.rmtree(old.parent, ignore_errors=True)
