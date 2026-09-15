from types import SimpleNamespace

from granola_share.granola import build_args, extract_meetings, normalize_meeting, parse_tool_result


def test_parse_tool_result_prefers_structured():
    r = SimpleNamespace(structuredContent={"meetings": [{"id": "a"}]}, content=[SimpleNamespace(text="ignored")])
    assert parse_tool_result(r) == {"meetings": [{"id": "a"}]}


def test_parse_tool_result_json_text_and_plain_text():
    r = SimpleNamespace(structuredContent=None, content=[SimpleNamespace(text='{"notes": [{"id": "x"}]}')])
    assert parse_tool_result(r) == {"notes": [{"id": "x"}]}
    r2 = SimpleNamespace(structuredContent=None, content=[SimpleNamespace(text="hello")])
    assert parse_tool_result(r2) == "hello"


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
        "attendees": [{"name": "Alex", "email": "j@x"}, "Sam"],
        "folder": {"name": "Biology"},
        "creator": {"name": "Alex"},
    })
    assert m.id == "doc1" and m.title == "Bio 101 - Cells"
    assert m.date.startswith("2026-09-14")
    assert m.notes_markdown.startswith("# Cells")
    assert m.attendees == ["Alex", "Sam"]
    assert m.folder == "Biology" and m.owner == "Alex"


def test_build_args_uses_real_param_names():
    schema = {"properties": {"date_from": {"type": "string"}, "limit": {"type": "integer"}, "document_ids": {"type": "string"}}}
    assert build_args(schema, {"since": "2026-09-01", "limit": 20, "offset": None}) == {"date_from": "2026-09-01", "limit": 20}
    assert build_args(schema, {"ids": ["a", "b"]}) == {"document_ids": "a,b"}
    assert build_args({"properties": {"meeting_ids": {"type": "array"}}}, {"ids": ["a"]}) == {"meeting_ids": ["a"]}
    assert build_args(None, {"since": "x"}) == {}
