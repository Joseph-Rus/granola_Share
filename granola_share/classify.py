"""Decide which class a note belongs to.

Order: the user's own Granola folder/title tags (rules) win; otherwise ask a local
Ollama model with a strict JSON schema; otherwise Unsorted.
"""

from __future__ import annotations

import json
import re
from typing import Callable

import httpx

from .config import UNSORTED, ClassDef, Config
from .granola import Meeting
from .store import Classification

MAX_NOTE_CHARS = 12000  # ~3k tokens; fits num_ctx below with room for the prompt


def _norm(s: str) -> str:
    return re.sub(r"[^a-z0-9]+", " ", (s or "").lower()).strip()


def classify_by_rules(m: Meeting, classes: list[ClassDef]) -> Classification | None:
    folder = _norm(m.folder)
    title = _norm(m.title)
    for c in classes:
        names = [c.name] + list(c.aliases)
        for n in names:
            nn = _norm(n)
            if not nn:
                continue
            if folder and (folder == nn or nn in folder.split(" / ") or nn in folder):
                return Classification(c.name, 0.95, "folder", lecture_title=m.title)
    for c in classes:
        for n in [c.name] + list(c.aliases):
            nn = _norm(n)
            if nn and len(nn) >= 3 and re.search(rf"\b{re.escape(nn)}\b", title):
                return Classification(c.name, 0.85, "rules", lecture_title=m.title)
    return None


def _schema(class_names: list[str]) -> dict:
    return {
        "type": "object",
        "properties": {
            "class_name": {"type": "string", "enum": class_names + [UNSORTED]},
            "confidence": {"type": "number"},
            "lecture_title": {"type": "string"},
            "topics": {"type": "array", "items": {"type": "string"}},
        },
        "required": ["class_name", "confidence", "lecture_title", "topics"],
    }


def build_prompt(m: Meeting, classes: list[ClassDef]) -> str:
    lines = ["You file lecture notes into the right class. Classes:"]
    for c in classes:
        extra = f" (also called: {', '.join(c.aliases)})" if c.aliases else ""
        desc = f" — {c.description}" if c.description else ""
        lines.append(f"- {c.name}{extra}{desc}")
    lines += [
        f"- {UNSORTED} — use this when the note is not a lecture for any class above, or you are unsure.",
        "",
        "Return JSON with: class_name (one of the classes above), confidence (0 to 1),",
        "lecture_title (a short specific title for this lecture), topics (3-6 short topic tags).",
        "",
        f"Title: {m.title}",
        f"Date: {m.date}",
        f"Granola folder: {m.folder or '(none)'}",
        f"Attendees: {', '.join(m.attendees) or '(none)'}",
        "",
        "Notes:",
        (m.notes_markdown or m.transcript or "")[:MAX_NOTE_CHARS],
    ]
    return "\n".join(lines)


def ollama_chat(cfg: Config, prompt: str, schema: dict) -> str:
    body = {
        "model": cfg.ollama_model,
        "messages": [{"role": "user", "content": prompt}],
        "stream": False,
        "format": schema,
        "think": False,
        "options": {"temperature": 0, "num_ctx": 8192},
    }
    r = httpx.post(f"{cfg.ollama_host.rstrip('/')}/api/chat", json=body, timeout=180)
    r.raise_for_status()
    return r.json()["message"]["content"]


def classify_with_ollama(m: Meeting, classes: list[ClassDef], cfg: Config,
                         chat: Callable[[Config, str, dict], str] = ollama_chat) -> Classification | None:
    if not classes:
        return None
    names = [c.name for c in classes]
    try:
        content = chat(cfg, build_prompt(m, classes), _schema(names))
        data = json.loads(content)
    except Exception:
        return None
    name = data.get("class_name")
    if name not in names + [UNSORTED]:
        return None
    try:
        conf = float(data.get("confidence", 0))
    except (TypeError, ValueError):
        conf = 0.0
    conf = max(0.0, min(1.0, conf))
    topics = [str(t) for t in data.get("topics", [])][:8]
    title = str(data.get("lecture_title") or m.title)
    if name == UNSORTED or conf < cfg.min_confidence:
        return Classification(UNSORTED, conf, "ollama", lecture_title=title, topics=topics)
    return Classification(name, conf, "ollama", lecture_title=title, topics=topics)


def classify(m: Meeting, cfg: Config, chat: Callable[[Config, str, dict], str] | None = None) -> Classification:
    c = classify_by_rules(m, cfg.classes)
    if c:
        return c
    if cfg.ollama_enabled and cfg.classes:
        c = classify_with_ollama(m, cfg.classes, cfg, chat or ollama_chat)
        if c:
            return c
    return Classification(UNSORTED, 0.0, "none", lecture_title=m.title)
