"""Granola's MCP output: the XML dialect it switched to, its real list_meetings schema, dates and accounts.

The XML samples below are the ones captured live from mcp.granola.ai on 2026-09-15, warts and
all: participant lines carry raw ``<john@acme.com>`` tokens and prose has bare ``&`` and ``<``,
which is what made the old parser read zero notes and say nothing.
"""

import asyncio
from datetime import date
from types import SimpleNamespace

from granola_share.config import Config
from granola_share.granola import (GranolaClient, account_label, build_args, extract_meetings, list_args,
                                   normalize_account, normalize_meeting, parse_granola_date, parse_participant,
                                   parse_tool_result, transcript_text, xml_to_data)

LIST_XML = """<meetings_data from="Jan 27, 2026" to="Feb 4, 2026" count="2">
<meeting id="0dba4400-50f1-4262-9ac7-89cd27b79371" title="Team sync" date="Feb 4, 2026 7:30 PM">
    <known_participants>
    John Doe (note creator) from Acme <john@acme.com>
    Jane Smith from Acme <jane@acme.com>
    </known_participants>
</meeting>
</meetings_data>"""

GET_XML = """<meetings_data from="Feb 4, 2026" to="Feb 4, 2026" count="1">
<meeting id="0dba4400-50f1-4262-9ac7-89cd27b79371" title="Team sync">
  <summary>
## Key Decisions
- Approved Q1 roadmap
  </summary>
</meeting>
</meetings_data>"""

MESSY_XML = """<meetings_data from="Feb 4, 2026" to="Feb 4, 2026" count="1">
<meeting id="m2" title="Ops & Eng sync" date="Feb 4, 2026">
  <summary>
## Notes
- Cost < 5% of budget & falling
- Ping <john@acme.com> for the deck
  </summary>
</meeting>
</meetings_data>"""

TRANSCRIPT_XML = '<transcript meeting_id="0dba4400">[00:00:15] John: Let\'s get started…</transcript>'

EMPTY_JSON = '{"count":0,"total_in_range":0,"date_range":{"from":"2026-08-17T00:00:00.000Z"},"meetings":[]}'

ZERO_XML = '<meetings_data from="Aug 17, 2026" to="Sep 15, 2026" count="0">\n</meetings_data>'

# The real list_meetings input schema (no offset/limit at all — hence the old "capped at 30 days" bug).
LIST_SCHEMA = {"type": "object", "properties": {
    "time_range": {"type": "string", "enum": ["this_week", "last_week", "last_30_days", "custom"],
                   "default": "last_30_days"},
    "custom_start": {"type": "string"}, "custom_end": {"type": "string"},
    "folder_id": {"type": "string"}, "workspace_only": {"type": "boolean"}, "involvement": {"type": "string"}}}

LEGACY_SCHEMA = {"properties": {"date_from": {"type": "string"}, "limit": {"type": "integer"},
                                "offset": {"type": "integer"}}}

ACCOUNT_JSON = {"email": "josephrussell4st@gmail.com",
                "active_workspace": {"id": "ws-77", "display_name": "Joey's workspace"},
                "mcp_note_access": {"scopes": ["personal", "public"]}}


def result(text: str):
    """An MCP CallToolResult as Granola sends it: one TextContent block, no structuredContent."""
    return SimpleNamespace(structuredContent=None, content=[SimpleNamespace(text=text)], isError=False)


# --- the old JSON shapes still work -------------------------------------------

def test_parse_tool_result_prefers_structured():
    r = SimpleNamespace(structuredContent={"meetings": [{"id": "a"}]}, content=[SimpleNamespace(text="ignored")])
    assert parse_tool_result(r) == {"meetings": [{"id": "a"}]}


def test_parse_tool_result_json_text_and_plain_text():
    assert parse_tool_result(result('{"notes": [{"id": "x"}]}')) == {"notes": [{"id": "x"}]}
    assert parse_tool_result(result("hello")) == "hello"


def test_extract_meetings_shapes():
    assert extract_meetings([{"id": "1"}, "junk"]) == [{"id": "1"}]
    assert extract_meetings({"meetings": [{"document_id": "2"}]}) == [{"document_id": "2"}]
    assert extract_meetings({"data": {"results": [{"id": "3"}]}}) == [{"id": "3"}]
    assert extract_meetings({"id": "solo", "title": "t"}) == [{"id": "solo", "title": "t"}]
    assert extract_meetings("text") == []


def test_normalize_meeting_maps_alternate_keys():
    m = normalize_meeting({
        "document_id": "doc1",
        "title": "Bio 101 - Cells",
        "start_time": "2026-09-14T10:00:00Z",
        "summary_markdown": "# Cells\n- membranes",
        "attendees": [{"name": "Joey", "email": "j@x"}, "Sam"],
        "folder": {"name": "Biology"},
        "creator": {"name": "Joey"},
    })
    assert m.id == "doc1" and m.title == "Bio 101 - Cells"
    assert m.date.startswith("2026-09-14")
    assert m.notes_markdown.startswith("# Cells")
    assert m.attendees == ["Joey", "Sam"]
    assert m.folder == "Biology" and m.owner == "Joey"


def test_build_args_uses_real_param_names():
    schema = {"properties": {"date_from": {"type": "string"}, "limit": {"type": "integer"}, "document_ids": {"type": "string"}}}
    assert build_args(schema, {"since": "2026-09-01", "limit": 20, "offset": None}) == {"date_from": "2026-09-01", "limit": 20}
    assert build_args(schema, {"ids": ["a", "b"]}) == {"document_ids": "a,b"}
    assert build_args({"properties": {"meeting_ids": {"type": "array"}}}, {"ids": ["a"]}) == {"meeting_ids": ["a"]}
    assert build_args(None, {"since": "x"}) == {}


# --- the XML dialect ------------------------------------------------------------

def test_list_meetings_xml_with_unescaped_participant_emails():
    data = parse_tool_result(result(LIST_XML))
    assert data["from"] == "Jan 27, 2026" and data["to"] == "Feb 4, 2026" and data["count"] == "2"
    assert isinstance(data["meeting"], list) and len(data["meeting"]) == 1  # a single <meeting> becomes a list

    rows = extract_meetings(data)
    assert len(rows) == 1
    m = normalize_meeting(rows[0])
    assert m.id == "0dba4400-50f1-4262-9ac7-89cd27b79371" and m.title == "Team sync"
    assert m.date == "2026-02-04T19:30:00"
    assert m.attendees == ["John Doe", "Jane Smith"]  # cleaned of "(note creator)", "from Acme", <email>
    assert m.owner == "John Doe"                      # the note creator becomes the owner


def test_get_meetings_xml_keeps_markdown_in_the_summary():
    m = normalize_meeting(extract_meetings(parse_tool_result(result(GET_XML)))[0])
    assert m.notes_markdown == "## Key Decisions\n- Approved Q1 roadmap"
    assert m.title == "Team sync" and m.date == ""


def test_xml_survives_bare_ampersands_and_stray_angle_brackets():
    m = normalize_meeting(extract_meetings(parse_tool_result(result(MESSY_XML)))[0])
    assert m.title == "Ops & Eng sync"
    assert "Cost < 5% of budget & falling" in m.notes_markdown
    assert "Ping <john@acme.com> for the deck" in m.notes_markdown


def test_transcript_xml():
    payload = parse_tool_result(result(TRANSCRIPT_XML))
    assert payload["meeting_id"] == "0dba4400"
    assert transcript_text(payload) == "[00:00:15] John: Let's get started…"
    assert transcript_text({"transcript": "plain"}) == "plain" and transcript_text("just text") == "just text"


def test_empty_account_payloads_give_no_meetings_without_error():
    assert extract_meetings(parse_tool_result(result(EMPTY_JSON))) == []
    zero = parse_tool_result(result(ZERO_XML))
    assert zero["count"] == "0" and zero["meeting"] == []
    assert extract_meetings(zero) == []


def test_xml_to_data_returns_the_text_when_it_cannot_parse():
    assert xml_to_data("not xml at all") == "not xml at all"
    assert xml_to_data("<broken><a>1") == "<broken><a>1"
    assert xml_to_data(123) == 123


def test_parse_participant_lines():
    assert parse_participant("John Doe (note creator) from Acme <john@acme.com>") == ("John Doe", True, "john@acme.com")
    assert parse_participant("Jane Smith from Acme <jane@acme.com>") == ("Jane Smith", False, "jane@acme.com")
    assert parse_participant("<solo@x.com>") == ("solo@x.com", False, "solo@x.com")
    assert parse_participant("") == ("", False, "")


# --- dates ----------------------------------------------------------------------

def test_parse_granola_date():
    assert parse_granola_date("Feb 4, 2026 7:30 PM") == "2026-02-04T19:30:00"
    assert parse_granola_date("Feb 4, 2026") == "2026-02-04"
    assert parse_granola_date("February 4, 2026 7:30 PM") == "2026-02-04T19:30:00"
    assert parse_granola_date("2026-02-04T19:30:00Z") == "2026-02-04T19:30:00Z"  # ISO passes straight through
    assert parse_granola_date("2026-02-04") == "2026-02-04"
    assert parse_granola_date("sometime next week") == "sometime next week"
    assert parse_granola_date("") == "" and parse_granola_date(None) is None


# --- list_meetings arguments -----------------------------------------------------

def test_list_args_with_the_real_schema():
    assert list_args(LIST_SCHEMA, date(2026, 2, 1), None, 50, 0, today=date(2026, 2, 4)) == {
        "time_range": "custom", "custom_start": "2026-02-01", "custom_end": "2026-02-05"}  # end = tomorrow
    assert list_args(LIST_SCHEMA, date(2026, 2, 1), date(2026, 2, 3)) == {
        "time_range": "custom", "custom_start": "2026-02-01", "custom_end": "2026-02-03"}
    assert list_args(LIST_SCHEMA, None) == {"time_range": "last_30_days"}


def test_list_args_falls_back_to_the_legacy_schema():
    assert list_args(LEGACY_SCHEMA, date(2026, 2, 1), None, 20, 0) == {"date_from": "2026-02-01", "limit": 20}
    assert list_args(LEGACY_SCHEMA, date(2026, 2, 1), None, 20, 40) == {"date_from": "2026-02-01", "limit": 20,
                                                                       "offset": 40}
    assert list_args(None, date(2026, 2, 1)) == {}


# --- accounts -------------------------------------------------------------------

def test_normalize_account_with_the_live_payload():
    info = normalize_account(ACCOUNT_JSON)
    assert info["email"] == "josephrussell4st@gmail.com"
    assert info["workspace"] == "Joey's workspace" and info["workspace_id"] == "ws-77"
    assert info["scopes"] == ["personal", "public"] and info["raw"] is ACCOUNT_JSON


def test_normalize_account_with_missing_fields():
    assert normalize_account({}) == {"email": "", "workspace": "", "workspace_id": "", "scopes": [], "raw": {}}
    assert normalize_account(None)["email"] == ""
    assert normalize_account({"email": "a@b.c"}) == {"email": "a@b.c", "workspace": "", "workspace_id": "",
                                                    "scopes": [], "raw": {"email": "a@b.c"}}
    xml = ('<account email="x@y.z"><active_workspace id="w1" display_name="Lab"/>'
           "<mcp_note_access><scopes>personal</scopes></mcp_note_access></account>")
    info = normalize_account(xml)
    assert info["email"] == "x@y.z" and info["workspace"] == "Lab" and info["scopes"] == ["personal"]


def test_account_label():
    assert account_label(normalize_account(ACCOUNT_JSON)) == "josephrussell4st@gmail.com · Joey's workspace"
    assert account_label({"email": "a@b.c"}) == "a@b.c"
    assert account_label(None) == "not signed in" and account_label({"email": ""}) == "not signed in"


# --- the client against a fake MCP session ---------------------------------------

class FakeSession:
    """An MCP session with Granola's real tool list; `answers` maps tool name -> text payload."""

    def __init__(self, answers: dict, schemas: dict | None = None, errors=()):
        self.answers = answers
        self.schemas = schemas or {"list_meetings": LIST_SCHEMA,
                                   "get_meetings": {"properties": {"meeting_ids": {"type": "array"}}},
                                   "get_meeting_transcript": {"properties": {"meeting_id": {"type": "string"}}},
                                   "get_account_info": {"properties": {}}}
        self.errors = set(errors)
        self.calls: list[tuple[str, dict]] = []

    async def list_tools(self):
        tools = [SimpleNamespace(name=n, description="", inputSchema=s) for n, s in self.schemas.items()]
        return SimpleNamespace(tools=tools)

    async def call_tool(self, name, args):
        self.calls.append((name, args))
        if name in self.errors:
            return SimpleNamespace(structuredContent=None, content=[SimpleNamespace(text="boom")], isError=True)
        return result(self.answers.get(name, ""))


def client_for(tmp_path) -> GranolaClient:
    return GranolaClient(Config(home=tmp_path, pool_dir=tmp_path / "pool"), oauth=None)


def test_client_sends_a_custom_range_and_parses_the_xml(tmp_path):
    session = FakeSession({"list_meetings": LIST_XML})
    meetings = asyncio.run(client_for(tmp_path).list_meetings(session, since=date(2026, 1, 27)))
    assert session.calls[0][0] == "list_meetings"
    args = session.calls[0][1]
    assert args["time_range"] == "custom" and args["custom_start"] == "2026-01-27" and args["custom_end"]
    assert len(session.calls) == 1  # the real schema has no paging, so we never loop
    assert [m.title for m in meetings] == ["Team sync"] and meetings[0].attendees == ["John Doe", "Jane Smith"]


def test_client_handles_the_empty_json_payload(tmp_path):
    session = FakeSession({"list_meetings": EMPTY_JSON})
    assert asyncio.run(client_for(tmp_path).list_meetings(session)) == []
    assert session.calls[0][1] == {"time_range": "last_30_days"}


def test_client_get_meetings_and_transcript(tmp_path):
    session = FakeSession({"get_meetings": GET_XML, "get_meeting_transcript": TRANSCRIPT_XML})
    client = client_for(tmp_path)
    full = asyncio.run(client.get_meetings(session, ["0dba4400-50f1-4262-9ac7-89cd27b79371"]))
    assert session.calls[0][1] == {"meeting_ids": ["0dba4400-50f1-4262-9ac7-89cd27b79371"]}
    assert full[0].notes_markdown.startswith("## Key Decisions")
    text = asyncio.run(client.get_transcript(session, "0dba4400"))
    assert text.startswith("[00:00:15] John:")


def test_client_get_account_info(tmp_path):
    import json as _json

    session = FakeSession({"get_account_info": _json.dumps(ACCOUNT_JSON)})
    info = asyncio.run(client_for(tmp_path).get_account_info(session))
    assert info["email"] == "josephrussell4st@gmail.com" and info["workspace"] == "Joey's workspace"

    # a tool that errors, and a server without the tool at all, both mean "we cannot say"
    broken = FakeSession({"get_account_info": "{}"}, errors=["get_account_info"])
    assert asyncio.run(client_for(tmp_path).get_account_info(broken)) is None
    missing = FakeSession({}, schemas={"list_meetings": LIST_SCHEMA})
    assert asyncio.run(client_for(tmp_path).get_account_info(missing)) is None
