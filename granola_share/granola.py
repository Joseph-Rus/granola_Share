"""Granola MCP client.

Talks to Granola's official remote MCP server (https://mcp.granola.ai/mcp) with the
user's OAuth token. Tool argument names are discovered from the server's own tool
schemas at runtime, so small changes on Granola's side do not break the sync.
"""

from __future__ import annotations

import json
import re
from contextlib import asynccontextmanager
from dataclasses import dataclass, field
from datetime import date
from typing import Any

from .config import Config

# Candidate argument names, in order of preference, for what we want to pass.
ARG_CANDIDATES = {
    "since": ["date_from", "start_date", "from_date", "since", "after", "updated_after", "created_after", "from"],
    "until": ["date_to", "end_date", "to_date", "until", "before", "to"],
    "limit": ["limit", "max_results", "page_size", "count"],
    "offset": ["offset", "skip", "page"],
    "ids": ["document_ids", "meeting_ids", "ids", "note_ids"],
    "id": ["document_id", "meeting_id", "id", "note_id"],
    "folder": ["folder_id", "folder"],
}

ID_KEYS = ["id", "document_id", "meeting_id", "note_id", "doc_id"]
TITLE_KEYS = ["title", "name", "subject"]
DATE_KEYS = ["date", "meeting_date", "start_time", "started_at", "created_at", "start", "datetime"]
NOTES_KEYS = ["notes_markdown", "summary_markdown", "notes", "summary", "content", "markdown", "text", "body", "enhanced_notes"]
PRIVATE_KEYS = ["private_notes", "private_notes_markdown", "my_notes", "user_notes"]
FOLDER_KEYS = ["folder", "folder_name", "folders", "folder_id"]
ATTENDEE_KEYS = ["attendees", "participants", "people"]
OWNER_KEYS = ["owner", "creator", "author"]


@dataclass
class Meeting:
    id: str
    title: str = ""
    date: str = ""
    owner: str = ""
    attendees: list[str] = field(default_factory=list)
    folder: str = ""
    notes_markdown: str = ""
    private_notes: str = ""
    transcript: str = ""
    raw: dict = field(default_factory=dict)


# --- pure helpers (unit tested) -------------------------------------------

def first_key(d: dict, keys: list[str]) -> Any:
    for k in keys:
        if k in d and d[k] not in (None, "", []):
            return d[k]
    return None


def _as_text(v: Any) -> str:
    if v is None:
        return ""
    if isinstance(v, str):
        return v
    if isinstance(v, dict):
        return str(first_key(v, ["markdown", "text", "content", "name", "email"]) or json.dumps(v))
    if isinstance(v, list):
        return "\n".join(_as_text(x) for x in v)
    return str(v)


def _as_name_list(v: Any) -> list[str]:
    if not v:
        return []
    if isinstance(v, str):
        return [s.strip() for s in re.split(r"[,;\n]", v) if s.strip()]
    out = []
    for item in v:
        if isinstance(item, dict):
            out.append(str(first_key(item, ["name", "email", "display_name"]) or json.dumps(item)))
        else:
            out.append(str(item))
    return out


def normalize_meeting(raw: dict) -> Meeting:
    mid = first_key(raw, ID_KEYS)
    if mid is None:
        raise ValueError(f"meeting has no id: {list(raw)[:10]}")
    folder = first_key(raw, FOLDER_KEYS)
    if isinstance(folder, list):
        folder = ", ".join(_as_name_list(folder))
    elif isinstance(folder, dict):
        folder = str(first_key(folder, ["name", "title", "id"]) or "")
    return Meeting(
        id=str(mid),
        title=str(first_key(raw, TITLE_KEYS) or "Untitled"),
        date=str(first_key(raw, DATE_KEYS) or ""),
        owner=_as_text(first_key(raw, OWNER_KEYS)),
        attendees=_as_name_list(first_key(raw, ATTENDEE_KEYS)),
        folder=str(folder or ""),
        notes_markdown=_as_text(first_key(raw, NOTES_KEYS)),
        private_notes=_as_text(first_key(raw, PRIVATE_KEYS)),
        transcript=_as_text(raw.get("transcript")),
        raw=raw,
    )


def parse_tool_result(result: Any) -> Any:
    """Turn an MCP CallToolResult into python data (dict/list/str)."""
    structured = getattr(result, "structuredContent", None)
    if structured:
        return structured
    texts = []
    for item in getattr(result, "content", []) or []:
        text = getattr(item, "text", None)
        if text is not None:
            texts.append(text)
    blob = "\n".join(texts).strip()
    if not blob:
        return None
    try:
        return json.loads(blob)
    except json.JSONDecodeError:
        return blob


def extract_meetings(payload: Any) -> list[dict]:
    """Find the list of meeting dicts inside whatever shape the tool returned."""
    if payload is None:
        return []
    if isinstance(payload, list):
        return [p for p in payload if isinstance(p, dict)]
    if isinstance(payload, dict):
        for key in ("meetings", "notes", "documents", "results", "items", "data"):
            v = payload.get(key)
            if isinstance(v, list):
                return [p for p in v if isinstance(p, dict)]
            if isinstance(v, dict):
                inner = extract_meetings(v)
                if inner:
                    return inner
        if first_key(payload, ID_KEYS) is not None:
            return [payload]
    return []


def build_args(schema: dict | None, wanted: dict[str, Any]) -> dict:
    """Map our generic wants (since/limit/ids/...) onto the tool's real parameter names."""
    props = (schema or {}).get("properties", {}) or {}
    args: dict = {}
    for want, value in wanted.items():
        if value is None:
            continue
        for cand in ARG_CANDIDATES.get(want, [want]):
            if cand in props:
                prop = props[cand] or {}
                ptype = prop.get("type")
                if want == "ids" and ptype == "string" and isinstance(value, list):
                    value = ",".join(value)
                if want == "id" and isinstance(value, list):
                    value = value[0]
                args[cand] = value
                break
    return args


# --- live client ---------------------------------------------------------

class GranolaClient:
    def __init__(self, cfg: Config, oauth):
        self.cfg = cfg
        self.oauth = oauth
        self._tools: dict[str, dict] | None = None

    @asynccontextmanager
    async def session(self):
        from mcp import ClientSession
        from mcp.client.streamable_http import streamablehttp_client

        token = self.oauth.access_token()
        headers = {"Authorization": f"Bearer {token}"}
        async with streamablehttp_client(self.cfg.mcp_url, headers=headers, timeout=90) as (read, write, _):
            async with ClientSession(read, write) as session:
                await session.initialize()
                yield session

    async def tools(self, session) -> dict[str, dict]:
        if self._tools is None:
            res = await session.list_tools()
            self._tools = {
                t.name: {"description": t.description or "", "schema": t.inputSchema or {}}
                for t in res.tools
            }
        return self._tools

    def _find_tool(self, tools: dict[str, dict], *names: str) -> str | None:
        for n in names:
            if n in tools:
                return n
        for n in names:
            for t in tools:
                if n in t:
                    return t
        return None

    async def call(self, session, name: str, args: dict) -> Any:
        res = await session.call_tool(name, args)
        if getattr(res, "isError", False):
            raise RuntimeError(f"{name} failed: {parse_tool_result(res)}")
        return parse_tool_result(res)

    async def list_meetings(self, session, since: date | None = None, limit: int = 50, max_pages: int = 20) -> list[Meeting]:
        tools = await self.tools(session)
        name = self._find_tool(tools, "list_meetings", "list_notes", "search_meetings")
        if not name:
            raise RuntimeError(f"no list_meetings tool on server; tools are {sorted(tools)}")
        schema = tools[name]["schema"]
        out: list[Meeting] = []
        seen: set[str] = set()
        offset = 0
        for _ in range(max_pages):
            wanted = {"since": since.isoformat() if since else None, "limit": limit, "offset": offset or None}
            args = build_args(schema, wanted)
            payload = await self.call(session, name, args)
            batch = [normalize_meeting(m) for m in extract_meetings(payload)]
            new = [m for m in batch if m.id not in seen]
            out.extend(new)
            seen.update(m.id for m in new)
            can_page = any(c in (schema.get("properties") or {}) for c in ARG_CANDIDATES["offset"])
            if not new or not can_page or len(batch) < limit:
                break
            offset += len(batch)
        return out

    async def get_meetings(self, session, ids: list[str]) -> list[Meeting]:
        tools = await self.tools(session)
        name = self._find_tool(tools, "get_meetings", "get_meeting", "get_notes", "get_note")
        if not name:
            raise RuntimeError(f"no get_meetings tool on server; tools are {sorted(tools)}")
        schema = tools[name]["schema"]
        props = schema.get("properties") or {}
        takes_list = any(c in props for c in ARG_CANDIDATES["ids"])
        out: list[Meeting] = []
        if takes_list:
            for i in range(0, len(ids), 10):
                chunk = ids[i : i + 10]
                payload = await self.call(session, name, build_args(schema, {"ids": chunk}))
                out.extend(normalize_meeting(m) for m in extract_meetings(payload))
        else:
            for mid in ids:
                payload = await self.call(session, name, build_args(schema, {"id": mid}))
                out.extend(normalize_meeting(m) for m in extract_meetings(payload))
        return out

    async def get_transcript(self, session, meeting_id: str) -> str:
        tools = await self.tools(session)
        name = self._find_tool(tools, "get_meeting_transcript", "get_transcript")
        if not name:
            return ""
        try:
            payload = await self.call(session, name, build_args(tools[name]["schema"], {"id": meeting_id}))
        except Exception:
            return ""  # paid-plan tool; free accounts get an error here
        if isinstance(payload, dict):
            t = payload.get("transcript") or payload.get("text") or payload.get("content") or ""
            return _as_text(t)
        return _as_text(payload)


# --- wire format between the friend client and the server -------------------

MEETING_FIELDS = ("id", "title", "date", "owner", "attendees", "folder", "notes_markdown", "private_notes", "transcript")


def meeting_to_dict(m: Meeting) -> dict:
    d = {f: getattr(m, f) for f in MEETING_FIELDS}
    d["raw"] = m.raw
    return d


def meeting_from_dict(d: dict) -> Meeting:
    if not isinstance(d, dict) or not str(d.get("id") or "").strip():
        raise ValueError("payload needs a non-empty 'id'")
    attendees = d.get("attendees") or []
    if isinstance(attendees, str):
        attendees = _as_name_list(attendees)
    raw = d.get("raw") if isinstance(d.get("raw"), dict) else {}
    return Meeting(
        id=str(d["id"]),
        title=str(d.get("title") or "Untitled"),
        date=str(d.get("date") or ""),
        owner=str(d.get("owner") or ""),
        attendees=[str(a) for a in attendees],
        folder=str(d.get("folder") or ""),
        notes_markdown=str(d.get("notes_markdown") or ""),
        private_notes=str(d.get("private_notes") or ""),
        transcript=str(d.get("transcript") or ""),
        raw=raw,
    )
