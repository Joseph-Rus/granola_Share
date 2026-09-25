"""Granola MCP client.

Talks to Granola's official remote MCP server (https://mcp.granola.ai/mcp) with the
user's OAuth token. Tool argument names are discovered from the server's own tool
schemas at runtime, so small changes on Granola's side do not break the sync.

Results come back as one text block that is either JSON (empty accounts, account
info, folders) or a loose XML dialect (accounts with notes). The XML is not always
well-formed — participant lines carry raw ``<john@acme.com>`` tokens and prose has
bare ``&`` — so ``xml_to_data`` sanitises before ElementTree and falls back to a
regex block parser when that still fails.
"""

from __future__ import annotations

import html
import json
import re
import textwrap
import xml.etree.ElementTree as ET
from contextlib import asynccontextmanager
from dataclasses import dataclass, field
from datetime import date, datetime, timedelta
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
PARTICIPANT_TEXT_KEYS = ["known_participants", "known_attendees"]
OWNER_KEYS = ["owner", "creator", "author"]

# Keys under which a list of meetings may hide, JSON or XML-derived.
MEETING_LIST_KEYS = ("meetings", "notes", "documents", "results", "items", "data", "meetings_data", "meeting")

# Granola's human-readable dates, e.g. "Feb 4, 2026 7:30 PM" / "Feb 4, 2026".
_TZ_SUFFIX = re.compile(r"\s+\(?(?!(?:AM|PM)\)?$)[A-Z]{2,5}\)?$")
_DATE_FORMATS = (
    ("%b %d, %Y %I:%M %p", True),
    ("%B %d, %Y %I:%M %p", True),
    ("%b %d, %Y %H:%M", True),
    ("%B %d, %Y %H:%M", True),
    ("%b %d, %Y", False),
    ("%B %d, %Y", False),
    ("%d %b %Y %H:%M", True),
    ("%d %b %Y", False),
)


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


# --- dates ---------------------------------------------------------------

def parse_granola_date(s: Any) -> str:
    """'Feb 4, 2026 7:30 PM' -> '2026-02-04T19:30:00'; 'Feb 4, 2026' -> '2026-02-04'.

    ISO input passes through unchanged; anything unrecognised is returned as given.
    """
    if not isinstance(s, str):
        return s
    text = s.strip().replace(" ", " ").replace(" ", " ")
    if not text:
        return s
    try:
        datetime.fromisoformat(text.replace("Z", "+00:00"))
        return s
    except ValueError:
        pass
    squashed = re.sub(r"\s+", " ", text)
    # Granola stamps the user's local zone on listings ("Sep 15, 2026 2:20 PM PDT"). Keep the
    # wall-clock time and drop the abbreviation: it is the lecture's local time either way.
    # AM/PM is never a zone, so it must survive — stripping it would turn 7:30 PM into 07:30.
    squashed = _TZ_SUFFIX.sub("", squashed)
    for fmt, with_time in _DATE_FORMATS:
        try:
            d = datetime.strptime(squashed, fmt)
        except ValueError:
            continue
        return d.strftime("%Y-%m-%dT%H:%M:%S") if with_time else d.strftime("%Y-%m-%d")
    return s


# --- tolerant XML ----------------------------------------------------------

_BARE_AMP = re.compile(r"&(?!(?:[A-Za-z][A-Za-z0-9]*|#\d+|#x[0-9A-Fa-f]+);)")
_EMAIL_TOKEN = re.compile(r"<([^<>\s\"']+@[^<>\s\"']+)>")
_BARE_LT = re.compile(r"<(?![A-Za-z_/?!])")
_FENCE = re.compile(r"^```[A-Za-z0-9_-]*\s*\n(.*?)\n?```\s*$", re.DOTALL)
_TAG = re.compile(r"<([A-Za-z_][\w.:-]*)((?:\s[^<>]*?)?)\s*(/?)>", re.DOTALL)
_ATTR = re.compile(r"([A-Za-z_][\w.:-]*)\s*=\s*(?:\"([^\"]*)\"|'([^']*)')", re.DOTALL)


def _sanitize_xml(text: str) -> str:
    """Escape the things Granola leaves raw: bare '&', '<email@host>' tokens, and stray '<'."""
    text = _BARE_AMP.sub("&amp;", text)
    text = _EMAIL_TOKEN.sub(lambda m: "&lt;" + m.group(1) + "&gt;", text)
    text = _BARE_LT.sub("&lt;", text)
    return text


def _clean_text(text: str | None) -> str:
    return textwrap.dedent(text or "").strip()


def _element_to_data(el: ET.Element) -> dict | str:
    data: dict[str, Any] = dict(el.attrib)
    children = list(el)
    if not children:
        text = _clean_text(el.text)
        if not data:
            return text
        if text:
            data.setdefault("text", text)
        return data
    for child in children:
        value = _element_to_data(child)
        if child.tag in data:
            existing = data[child.tag]
            if isinstance(existing, list):
                existing.append(value)
            else:
                data[child.tag] = [existing, value]
        else:
            data[child.tag] = value
    text = _clean_text(el.text)
    if text:
        data.setdefault("text", text)
    return data


def _loose_parse(text: str) -> dict | str | None:
    """Regex block parser for XML that ElementTree rejects even after sanitising.

    Handles the shallow shapes Granola uses: a root element with attributes, child
    elements (repeated tags -> list), and text leaves. Returns None if there is no
    recognisable root element.
    """
    text = text.strip()
    if text.startswith("<?"):
        text = text.split("?>", 1)[-1].strip()
    m = _TAG.match(text)
    if not m:
        return None
    tag, attr_text, self_closing = m.group(1), m.group(2), m.group(3)
    # findall gives "" for the quote style not used, so a single-quoted value is in v2.
    attrs = {k: html.unescape(v or v2) for k, v, v2 in _ATTR.findall(attr_text)}
    if self_closing:
        return attrs
    close = "</" + tag + ">"
    end = text.rfind(close)
    if end < 0:
        return None
    inner = text[m.end():end]
    data: dict[str, Any] = dict(attrs)
    pos = 0
    loose_text: list[str] = []
    found_child = False
    while True:
        cm = _TAG.search(inner, pos)
        if not cm:
            loose_text.append(inner[pos:])
            break
        loose_text.append(inner[pos:cm.start()])
        ctag = cm.group(1)
        if cm.group(3):
            child_src = inner[cm.start():cm.end()]
            nxt = cm.end()
        else:
            cend = inner.find("</" + ctag + ">", cm.end())
            if cend < 0:  # an unclosed tag such as "<inaudible>" in prose: keep it as text
                loose_text.append(inner[cm.start():cm.end()])
                pos = cm.end()
                continue
            nxt = cend + len(ctag) + 3
            child_src = inner[cm.start():nxt]
        value = _loose_parse(child_src)
        if value is None:
            loose_text.append(child_src)
            pos = nxt
            continue
        found_child = True
        if ctag in data:
            existing = data[ctag]
            if isinstance(existing, list):
                existing.append(value)
            else:
                data[ctag] = [existing, value]
        else:
            data[ctag] = value
        pos = nxt
    free = _clean_text(html.unescape("".join(loose_text)))
    if not found_child and not attrs:
        return free
    if free:
        data.setdefault("text", free)
    return data


def _normalise_root(tag: str, data: dict | str) -> dict | str:
    if isinstance(data, dict) and tag == "meetings_data":
        meetings = data.get("meeting")
        if meetings is None:
            data["meeting"] = []
        elif not isinstance(meetings, list):
            data["meeting"] = [meetings]
    return data


def xml_to_data(text: str) -> dict | list | str:
    """Parse Granola's XML-ish tool output into plain python data.

    Element -> dict of attributes + child elements; repeated child tags -> list;
    text-only leaves -> stripped str. ``<meetings_data>`` always yields a
    ``"meeting"`` list. On hopeless input the original string is returned.
    """
    if not isinstance(text, str):
        return text
    src = text.strip()
    if not src.startswith("<"):
        return text
    try:
        root = ET.fromstring(_sanitize_xml(src))
    except ET.ParseError:
        root = None
    if root is not None:
        return _normalise_root(root.tag, _element_to_data(root))
    fragments = _parse_fragments(src)
    if fragments is not None:
        return _normalise_root(*fragments)
    loose = _loose_parse(src)
    if loose is None:
        return text
    m = _TAG.match(src.split("?>", 1)[-1].strip() if src.startswith("<?") else src)
    return _normalise_root(m.group(1) if m else "", loose)


def _strip_fences(blob: str) -> str:
    m = _FENCE.match(blob)
    return m.group(1).strip() if m else blob


def _xml_candidate(blob: str) -> str | None:
    """The XML part of a text block: the whole thing, or what follows one preamble line."""
    if blob.startswith("<"):
        return blob
    first, sep, rest = blob.partition("\n")
    rest = rest.strip()
    if sep and "<" not in first and rest.startswith("<"):
        return rest
    return None


# Granola wraps results in advisories: an <access_notice> about plan limits, then a sentence
# telling the reader to treat the notes as data. So a response is a sequence of fragments,
# not one document — we wrap it and pull out the element that actually carries the payload.
PAYLOAD_TAGS = ("meetings_data", "transcript", "folders", "meeting")
_WRAP_ROOT = "granola_response"


def _richest_child(root: ET.Element) -> ET.Element | None:
    children = [c for c in root if c.tag not in ("access_notice", "notice", "warning")]
    return max(children, key=lambda c: len(list(c.iter())), default=None)


def find_payload_element(root: ET.Element) -> ET.Element | None:
    """The element holding the data: a known payload tag anywhere, else the biggest child."""
    for tag in PAYLOAD_TAGS:
        el = root.find(f".//{tag}")
        if el is not None:
            return el
    return _richest_child(root)


def access_notice(text: str) -> str:
    """The plan-limit sentence Granola prepends, if any — worth repeating to the human."""
    if not isinstance(text, str):
        return ""
    m = re.search(r"<access_notice>(.*?)</access_notice>", text, re.DOTALL)
    return _clean_text(m.group(1)) if m else ""


def _parse_fragments(src: str) -> tuple[str, dict | str] | None:
    """Parse a blob of mixed notices, prose and XML by wrapping it in one synthetic root."""
    try:
        wrapped = ET.fromstring(f"<{_WRAP_ROOT}>{_sanitize_xml(src)}</{_WRAP_ROOT}>")
    except ET.ParseError:
        return None
    el = find_payload_element(wrapped)
    if el is None:
        return None
    return el.tag, _element_to_data(el)


# --- participants ------------------------------------------------------------

_CREATOR_MARK = re.compile(r"\(\s*(?:note\s+)?(?:creator|owner|organi[sz]er)\s*\)", re.IGNORECASE)
_ANGLE_TOKEN = re.compile(r"<[^<>]*>")
_FROM_ORG = re.compile(r"\s+from\s+.+$", re.IGNORECASE)


def parse_participant(line: str) -> tuple[str, bool, str]:
    """'John Doe (note creator) from Acme <john@acme.com>' -> ('John Doe', True, 'john@acme.com')."""
    text = html.unescape(str(line or "")).strip()
    is_creator = bool(_CREATOR_MARK.search(text))
    email = ""
    em = _EMAIL_TOKEN.search(text)
    if em:
        email = em.group(1).strip()
    text = _CREATOR_MARK.sub(" ", text)
    text = _ANGLE_TOKEN.sub(" ", text)
    text = _FROM_ORG.sub("", text.strip())
    text = re.sub(r"\s+", " ", text).strip(" ,;-–—")
    if not text and email:
        text = email
    return text, is_creator, email


def parse_participants(value: Any) -> tuple[list[str], str]:
    """Clean attendee names from a known_participants block; also the note creator's name."""
    if not value:
        return [], ""
    if isinstance(value, dict):
        value = first_key(value, ["participant", "text", "name"]) or []
    lines = value.splitlines() if isinstance(value, str) else [_as_text(v) for v in value]
    names: list[str] = []
    creator = ""
    for line in lines:
        name, is_creator, _ = parse_participant(line)
        if not name:
            continue
        names.append(name)
        if is_creator and not creator:
            creator = name
    return names, creator


def normalize_meeting(raw: dict) -> Meeting:
    mid = first_key(raw, ID_KEYS)
    if mid is None:
        raise ValueError(f"meeting has no id: {list(raw)[:10]}")
    folder = first_key(raw, FOLDER_KEYS)
    if isinstance(folder, list):
        folder = ", ".join(_as_name_list(folder))
    elif isinstance(folder, dict):
        folder = str(first_key(folder, ["name", "title", "id"]) or "")
    attendees = _as_name_list(first_key(raw, ATTENDEE_KEYS))
    creator = ""
    participants = first_key(raw, PARTICIPANT_TEXT_KEYS)
    if participants is not None:
        names, creator = parse_participants(participants)
        if not attendees:
            attendees = names
    owner = _as_text(first_key(raw, OWNER_KEYS)) or creator
    return Meeting(
        id=str(mid),
        title=str(first_key(raw, TITLE_KEYS) or "Untitled"),
        date=parse_granola_date(str(first_key(raw, DATE_KEYS) or "")),
        owner=owner,
        attendees=attendees,
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
    blob = _strip_fences(blob)
    try:
        return json.loads(blob)
    except json.JSONDecodeError:
        pass
    xml = _xml_candidate(blob)
    if xml is not None:
        data = xml_to_data(xml)
        if not isinstance(data, str):
            return data
    return blob


def extract_meetings(payload: Any) -> list[dict]:
    """Find the list of meeting dicts inside whatever shape the tool returned."""
    if payload is None:
        return []
    if isinstance(payload, str):  # the raw XML text, as Granola sends it
        parsed = xml_to_data(payload)
        return [] if isinstance(parsed, str) else extract_meetings(parsed)
    if isinstance(payload, list):
        return [p for p in payload if isinstance(p, dict)]
    if isinstance(payload, dict):
        for key in MEETING_LIST_KEYS:
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


def _iso_day(v: Any) -> str | None:
    if v is None:
        return None
    if isinstance(v, datetime):
        return v.date().isoformat()
    if isinstance(v, date):
        return v.isoformat()
    return str(v)


def list_args(schema: dict | None, since: Any = None, until: Any = None, limit: int | None = 50,
              offset: int | None = 0, today: date | None = None) -> dict:
    """Arguments for list_meetings given the tool's real schema.

    Granola's schema has ``time_range`` (enum ``this_week|last_week|last_30_days|custom``)
    with ``custom_start``/``custom_end`` and no paging. A ``since`` maps to a custom range
    ending tomorrow (so today's notes are included); no ``since`` means the last 30 days.
    Older/other schemas fall back to the date_from/limit/offset candidate mapping.
    """
    props = (schema or {}).get("properties", {}) or {}
    time_range = props.get("time_range")
    enum = list((time_range or {}).get("enum") or []) if isinstance(time_range, dict) else []
    if isinstance(time_range, dict):
        if since is not None and "custom" in enum:
            end = until if until is not None else (today or date.today()) + timedelta(days=1)
            return {"time_range": "custom", "custom_start": _iso_day(since), "custom_end": _iso_day(end)}
        if since is None:
            return {"time_range": "last_30_days"}
    args = build_args(schema, {"since": _iso_day(since), "until": _iso_day(until), "limit": limit, "offset": offset or None})
    if isinstance(time_range, dict) and not any(c in args for c in ARG_CANDIDATES["since"]) and "last_30_days" in enum:
        args["time_range"] = "last_30_days"
    return args


# --- account info -------------------------------------------------------------

_EMAIL_RE = re.compile(r"[\w.+-]+@[\w-]+(?:\.[\w-]+)+")


def _scope_list(v: Any) -> list[str]:
    if not v:
        return []
    if isinstance(v, str):
        return [s.strip() for s in re.split(r"[,;\s]+", v) if s.strip()]
    if isinstance(v, dict):
        return _scope_list(first_key(v, ["scope", "scopes", "text"]))
    return [str(_as_text(x)).strip() for x in v if _as_text(x).strip()]


def normalize_account(payload: Any) -> dict:
    """{"email", "workspace", "workspace_id", "scopes", "raw"} from get_account_info output (JSON or XML)."""
    info = {"email": "", "workspace": "", "workspace_id": "", "scopes": [], "raw": payload}
    data = payload
    if isinstance(data, str):
        parsed = xml_to_data(data)
        if isinstance(parsed, str):
            m = _EMAIL_RE.search(parsed)
            info["email"] = m.group(0) if m else ""
            return info
        data = parsed
    if not isinstance(data, dict):
        return info
    for wrapper in ("account", "account_info", "user", "data", "result"):
        inner = data.get(wrapper)
        if isinstance(inner, dict) and (first_key(inner, ["email", "active_workspace", "workspace"]) is not None):
            data = inner
            break
    email = first_key(data, ["email", "user_email", "account_email"])
    if email is None:
        for k in ("user", "account"):
            if isinstance(data.get(k), dict):
                email = first_key(data[k], ["email"])
                if email:
                    break
    info["email"] = _as_text(email).strip()
    ws = first_key(data, ["active_workspace", "workspace", "current_workspace"])
    if isinstance(ws, dict):
        info["workspace"] = str(first_key(ws, ["display_name", "name", "title"]) or "")
        info["workspace_id"] = str(first_key(ws, ["id", "workspace_id"]) or "")
    elif ws is not None:
        info["workspace"] = _as_text(ws).strip()
        info["workspace_id"] = str(first_key(data, ["workspace_id", "active_workspace_id"]) or "")
    access = data.get("mcp_note_access")
    scopes = first_key(access, ["scopes", "scope"]) if isinstance(access, dict) else None
    if scopes is None:
        scopes = access if isinstance(access, (str, list)) else first_key(data, ["scopes", "scope"])
    info["scopes"] = _scope_list(scopes)
    return info


def account_label(info: dict | None) -> str:
    """'alex@example.com · Alex's workspace', or 'not signed in'."""
    if not info or not str(info.get("email") or "").strip():
        return "not signed in"
    email = str(info["email"]).strip()
    ws = str(info.get("workspace") or "").strip()
    return f"{email} · {ws}" if ws else email


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

    async def list_meetings(self, session, since: date | None = None, until: date | None = None,
                            limit: int = 50, max_pages: int = 20) -> list[Meeting]:
        tools = await self.tools(session)
        name = self._find_tool(tools, "list_meetings", "list_notes", "search_meetings")
        if not name:
            raise RuntimeError(f"no list_meetings tool on server; tools are {sorted(tools)}")
        schema = tools[name]["schema"]
        out: list[Meeting] = []
        seen: set[str] = set()
        offset = 0
        for _ in range(max_pages):
            args = list_args(schema, since, until, limit, offset)
            payload = await self.call(session, name, args)
            batch = [normalize_meeting(m) for m in extract_meetings(payload)]
            new = [m for m in batch if m.id not in seen]
            out.extend(new)
            seen.update(m.id for m in new)
            can_page = any(c in args for c in ARG_CANDIDATES["offset"]) or (
                offset == 0 and any(c in (schema.get("properties") or {}) for c in ARG_CANDIDATES["offset"])
            )
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
        return transcript_text(payload)

    async def get_account_info(self, session) -> dict | None:
        """Who is signed in: {"email", "workspace", "workspace_id", "scopes", "raw"}; None if unavailable."""
        try:
            tools = await self.tools(session)
            name = self._find_tool(tools, "get_account_info", "account_info", "whoami")
            if not name:
                return None
            payload = await self.call(session, name, {})
            return normalize_account(payload)
        except Exception:
            return None


def transcript_text(payload: Any) -> str:
    """The transcript body from a get_meeting_transcript result (JSON, XML-derived dict, or text)."""
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
