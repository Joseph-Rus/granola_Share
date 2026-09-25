"""Write what the Python engine produces, so the C# engine's tests can check they match it byte for byte.

    python engine/tests/golden.py

writes engine/tests/StudyStash.Core.Tests/Golden/. CI runs this again and fails if the files change, so the
C# port always matches the Python engine that is still shipping.
"""

import json
import os
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))
os.environ.setdefault("HOME", "/home/student")

from granola_share import classify, store, summarize  # noqa: E402
from granola_share.config import ClassDef, ClientConfig, Config, dump_client_config, dump_config, load_config  # noqa: E402
from granola_share.granola import (meeting_from_dict, meeting_to_dict, normalize_meeting, parse_granola_date,  # noqa: E402
                                   parse_participant, parse_participants)
from granola_share.store import Classification  # noqa: E402

OUT = ROOT / "engine" / "tests" / "StudyStash.Core.Tests" / "Golden"
LINE = "The derivative measures how fast a function changes at a point.\n"


def write(name: str, text: str) -> None:
    (OUT / name).write_bytes(text.encode("utf-8"))


def outcome(fn, *args):
    """The result, or the name of the exception it raised."""
    try:
        return {"ok": fn(*args)}
    except Exception as e:  # noqa: BLE001
        return {"error": type(e).__name__}


CLASSES = [ClassDef("CS 101", ["cs101", "intro programming"], "Intro to programming"),
           ClassDef("Bio \U0001F9EC 110", ["bio110", "biology"]), ClassDef("Calc II", [], "Series — and \"sequences\"")]


def configs() -> None:
    cfg = Config(home=Path("/srv/home"), pool_dir=Path("/srv/Lecture notes"), pool_name='Fall "26" — Café \\ notes',
                 pool_password="maple-otter", web_port=9000, server_sync=True, ollama_model="qwen3:1.7b",
                 min_confidence=0.7, summary_model="gemma4:e4b", summary_max_context=16384, keep_granola_notes=True,
                 classes=CLASSES)
    write("config.toml", dump_config(cfg))
    write("config-default.toml", dump_config(Config(home=Path("/srv/home"), pool_dir=Path("/srv/notes"))))
    cc = ClientConfig(home=Path("/srv/home"), server_url="http://mini.example.ts.net:8787", pool_key="tulip 2027",
                      pool_name="Fall", display_name="Sam ☕", mode="ask", poll_interval_seconds=60, copy_transcripts=True)
    write("client.toml", dump_client_config(cc))
    # A file a person edited by hand: odd spacing, values of the wrong type, keys in another order.
    hand = ('pool_dir = "/srv/notes//fall/./"\nweb_port = 8790.0\n[ollama]\nmin_confidence = 1\nenabled = "false"\n'
            '[summary]\nmax_context = "8_192"\n[[classes]]\nname = 101\naliases = ["a", 2]\n')
    home = OUT / "_hand"
    home.mkdir(exist_ok=True)
    (home / "config.toml").write_text(hand, encoding="utf-8")
    loaded = dump_config(load_config(home))
    (home / "config.toml").unlink()
    home.rmdir()
    write("config-hand.toml", hand)
    write("config-hand-saved.toml", loaded)


def meeting() -> "store.Meeting":
    return meeting_from_dict({
        "id": "not_1a2b3c4d5e6f", "title": "Lec 7: \"Recursion\" / trees?", "date": "2026-09-14T10:00:00",
        "owner": "Sam", "attendees": ["Sam", "Dr. Ada Lovelace"], "folder": "CS101 Fall",
        "notes_markdown": "# Recursion\n- base case\n```python\n# not a heading\ndef f(n): ...\n```\n### Trees",
        "private_notes": "ask about HW3", "transcript": LINE * 3,
        "raw": {"id": "not_1a2b3c4d5e6f", "score": 1.50, "n": 3, "ok": True, "none": None, "tags": ["é", "😀"],
                "nested": {"deep": [1e16, 1e-5, 0.1]}},
    })


def notes() -> None:
    m = meeting()
    c = Classification("CS 101", 0.875, "ollama", "Recursion and trees", ["recursion", "trees", "base case"])
    write("note-notes.md", store.render_markdown(m, c))
    summary = "## Overview\nWe met recursion.\n\n```\n## not a heading\n```\n\n#### Deep\n###### Six"
    write("note-summary.md", store.render_markdown(m, c, summary, "big:35b", keep_granola=True))
    write("note-bare.md", store.render_markdown(meeting_from_dict({"id": "x"}), Classification("Unsorted", 0.125, "none")))


def cases() -> dict:
    floats = [0.6, 0.7, 1.0, 0.95, 0.85, 1e16, 1e-5, 1e-4, 123456789.123, 0.1 + 0.2, 1e22, 5e-324, 1234567890123456.0,
              12345678901234567.0, -0.0, 2.5, 100.0, 0.001, 3.14159, 1.5e300, -2.5e-7, 32768.0]
    fixed = [0.125, 0.135, 0.875, 0.005, 0.015, 0.025, 1.0, 0.0, 0.95, 0.85, 0.333333, 0.666666, 2.675, 1.005, 0.994,
             0.995, 0.9951]
    dumps = ['{"a": 1, "b": [1.50, 2e3, -0, true, null, "\\u00e9\\ud83d\\ude00\\u0000\\u007f\\u2028\\t"], "c": {}}',
             '[]', '"\\t\\n\\"\\\\/\\b\\f"', "1.0", "12345678901234567890", "1E400", "-1E400", '{"k": {"j": [[], {}]}}',
             '"caf\\u00e9 \\u2014 notes"', "0.1", "-0.0", "1e-7"]
    slugs = [['Lec 3: "Loops" / while?', 80], ["", 80], ["   ", 80], ["a\tb\nc", 80], ["x" * 100, 80],
             ["Bio 110 ", 60], ["Ümlaut Café Notes", 80], ['a/b\\c:d*e?f"g<h>i|j', 80], ["x" * 70 + "   y", 72],
             ["\x00\x1f", 80]]
    dates = ["Feb 4, 2026 7:30 PM", "Feb 4, 2026", "February 4, 2026 7:30 PM", "2026-02-04T19:30:00Z", "2026-02-04",
             "sometime next week", "", "Sep 15, 2026 2:20 PM PDT", "Sep 14, 2026 8:15 AM PDT", "Feb 4, 2026 19:30 CEST",
             "Feb 4, 2026 7:30 AM", "Sept 15, 2026", "sep 15, 2026 2:20 pm", "Feb 30, 2026", "4 Feb 2026 19:30",
             "4 Feb 2026", "Feb 4 , 2026", "Feb 4, 2026", "  Feb 4, 2026  ", "Feb 4, 2026 7:30 PM (PDT)",
             "February 4, 2026 7:30 PM EST", "Feb 04, 2026 07:05 AM", "Feb 4, 2026 12:05 AM", "Feb 4, 2026 12:05 PM",
             "Feb 4, 2026 13:05 PM", "Feb 29, 2028", "Feb 29, 2027"]
    people = ["John Doe (note creator) from Acme <john@acme.com>", "Jane Smith from Acme <jane@acme.com>", "<solo@x.com>",
              "", "Dr. Ada Lovelace (Organiser) &amp; co <ada@x.org>", "— Sam —", "Jane from Acme Corp",
              "&lt;solo@x.com&gt;", "  Pat (OWNER)  ", "Lee <lee@x.io> (creator)"]
    raws = [
        {"document_id": "doc1", "title": "Bio 101 - Cells", "start_time": "2026-09-14T10:00:00Z",
         "summary_markdown": "# Cells\n- membranes", "attendees": [{"name": "Alex", "email": "j@x"}, "Sam"],
         "folder": {"name": "Biology"}, "creator": {"name": "Alex"}},
        {"id": 12345, "name": "", "subject": "Physics", "date": "Sep 15, 2026 2:20 PM PDT",
         "known_participants": "Ann (note creator) from U <ann@u.edu>\nBo <bo@u.edu>", "folders": ["A", {"name": "B"}],
         "notes": [{"text": "one"}, {"markdown": "two"}], "transcript": [{"text": "t1"}, "t2"]},
        {"id": "p", "participants": "Ann; Bo, Cy", "folder_name": "", "folder_id": 7, "content": {"x": 1},
         "private_notes": 3, "owner": {"email": "o@x"}},
        {"id": "q", "attendees": [{"x": 1}], "known_participants": {"participant": ["Zed (owner)", {"name": "Y"}]}},
        {"id": "r", "title": None, "date": [], "notes_markdown": "", "summary": "fallback", "creator": ""},
        {"title": "no id"},
    ]
    payloads = [
        {"id": "a", "title": "", "attendees": "Ann, Bo;Cy", "raw": [1]},
        {"id": " b ", "attendees": {"k": 1, "j": 2}, "raw": {"x": 1}, "date": 20260914, "folder": 0},
        {"id": 7, "title": "T", "owner": True, "notes_markdown": None},
        {"id": ""}, {"id": "   "}, {"title": "x"}, {"id": 0},
        meeting_to_dict(meeting()),
    ]
    demote = ["# A\n## B\n###### F\n####### G\n#nospace", "```\n# in code\n```\n# out", "  ```\n# x\n", "",
              "text\n\n##\tTabbed", "# A\r\n## B\r\n"]
    sections = [["## Notes\n\nmembranes\n\n## Transcript\n\nosmosis talk\n", "Notes"],
                ["## Notes\n\nmembranes\n\n## Transcript\n\nosmosis talk\n", "Transcript"],
                ["# t\n\n## Private notes\n\nsecret\n\n## Notes\n\nn", "Private notes"],
                ["## Notes\n\n", "Notes"], ["no sections", "Notes"]]
    loop = "## Key concepts\n" + "- **Osmosis** is water moving across a membrane.\n" * 8
    clean = ["<think>hmm</think>\n```markdown\n## Overview\nx\n```", "  ## A  ", "```md\n## A\n```", "```\n## A\n```",
             "```python\ncode\n```", loop, "## Overview\nOne.\n\n## Key concepts\n- **A** is a.\n- **B** is b.",
             "<think>\nlong\nthoughts\n</think><think>again</think>## B", None]
    many = "\n".join(f"- point number {i % 9} is about limits" for i in range(24))
    repetitive = [loop, "short\nshort\nshort\nshort\nshort", many, "\n".join(f"- distinct line number {i}" for i in range(30)),
                  "", "a line that repeats\n" * 4]
    splits = [[LINE * 200, 1000], [("Limits come first. " * 300).strip(), 500], ["x" * 2000, 500],
              ["short\n\n\nlines\r\nand\rbreaks here", 12], ["One. Two! Three? " * 40 + "\n" + "word " * 300, 300],
              ["a" * 300 + " " + "b" * 400 + ". " + "c" * 50, 250]]
    norms = ["CS101 Fall", "  Bio—110 ", "Intro_Programming!", "", "ÜBER 101", "calc ii / series"]
    rules = [
        [{"id": "1", "title": "Random meeting", "folder": "Biology"}],
        [{"id": "2", "title": "CS101 lecture 4 - recursion"}],
        [{"id": "3", "title": "Dentist"}],
        [{"id": "4", "title": "calc ii review", "folder": ""}],
        [{"id": "5", "title": "x", "folder": "Fall 2026 / CS 101 Section 2"}],
        [{"id": "6", "title": "Intro Programming lab"}],
        [{"id": "7", "title": "bio"}],
    ]
    m = meeting()
    no_folder = meeting_from_dict({"id": "n", "title": "T", "date": "2026-09-01"})
    return {
        "floats": [[f, repr(f)] for f in floats],
        "fixed2": [[f, f"{f:.2f}"] for f in fixed],
        "dumps": [[t, json.dumps(json.loads(t))] for t in dumps],
        "slugify": [[t, n, store.slugify(t, n)] for t, n in slugs],
        "date_prefix": [[d, store._date_prefix(d)] for d in ["2026-09-14T10:00:00", "2026-9-14", "x2026-09-14"]],
        "dates": [[d, parse_granola_date(d)] for d in dates],
        "participant": [[p, list(parse_participant(p))] for p in people],
        "participants": [[v, outcome(lambda v: list(parse_participants(v)), v)]
                         for v in ["A (creator)\nB\n\nC (owner)", ["x <x@y.z>", {"name": "Q"}], {"text": "T (owner)"}, [], ""]],
        "normalize": [[r, outcome(lambda r: meeting_to_dict(normalize_meeting(r)), r)] for r in raws],
        "from_dict": [[p, outcome(lambda p: json.dumps(meeting_to_dict(meeting_from_dict(p))), p)] for p in payloads],
        "demote": [[t, store.demote_headings(t)] for t in demote],
        "md_section": [[t, h, store._md_section(t, h)] for t, h in sections],
        "like": [[q, store._like(q)] for q in ["50%_off", "a\\b", "plain"]],
        "clean": [[t, outcome(summarize.clean_output, t)] for t in clean],
        "repetitive": [[t, summarize.repetitive(t)] for t in repetitive],
        "split": [[t, n, summarize.split_transcript(t, n)] for t, n in splits],
        "budget": [[c, summarize.transcript_budget(c)] for c in [4096, 8192, 12288, 32768, 131072]],
        "norm": [[s, classify._norm(s)] for s in norms],
        "rules": [[d, (lambda c: c and [c.class_name, c.confidence, c.by, c.lecture_title])(
            classify.classify_by_rules(meeting_from_dict(d), CLASSES))] for [d] in rules],
        "classes": [[c.name, c.aliases, c.description] for c in CLASSES],
        "meeting": meeting_to_dict(m),
        "prompts": {
            "sort": classify.build_prompt(m, CLASSES),
            "sort_bare": classify.build_prompt(no_folder, CLASSES[:1]),
            "whole": summarize.whole_prompt(m, "TRANSCRIPT"),
            "whole_bare": summarize.whole_prompt(no_folder, "T"),
            "part": summarize.part_prompt(m, "PART", 2, 3),
            "condense": summarize.condense_prompt(m, ["a", "b"]),
            "merge": summarize.merge_prompt(m, ["a", "b", "c"]),
            "schema": classify._schema([c.name for c in CLASSES]),
        },
    }


def granola_cases() -> dict:
    """Stage 2: Granola's replies, the sign-in URL, and the two sign-in files."""
    import tempfile
    import textwrap
    from datetime import date
    from types import SimpleNamespace
    from urllib.parse import parse_qs, urlencode

    sys.path.insert(0, str(ROOT / "tests"))
    import test_granola as tg  # the replies captured from mcp.granola.ai
    import test_granola_main as tgm

    from granola_share import granola
    from granola_share.oauth import GranolaOAuth, _write_private

    xml = [tg.LIST_XML, tg.GET_XML, tg.MESSY_XML, tg.TRANSCRIPT_XML, tg.ZERO_XML, tg.LIVE_LIST, tgm.GRANOLA_XML,
           '<account email="x@y.z"><active_workspace id="w1" display_name="Lab"/>'
           "<mcp_note_access><scopes>personal</scopes></mcp_note_access></account>",
           "not xml at all", "<broken><a>1", "   <a>  padded  </a>  ",
           "<?xml version=\"1.0\"?><folders><folder id=\"f1\">CS</folder><folder id=\"f2\">Bio</folder></folders>",
           "<a><![CDATA[x < y & z]]><!-- note --><b>1</b>tail</a>",
           "<a>before<!-- c -->after<b/>tail</a>",
           "<m title='single' other=\"double\" empty=\"\"><s>x</s><s>y</s><s k=\"v\">z</s></m>",
           "<m id='m1' title=\"Two\">x &nbsp; <b>y</b> <inaudible> <c/></m>",
           "<meetings_data><meeting id=\"1\" title=\"&amp; &lt;b&gt; &#x41;&#66;\"/></meetings_data>",
           "<meetings_data count=\"1\"><meeting id=\"only\"/></meetings_data>",
           "<notice>plan limits</notice>\n<warning>x</warning>\n<results><item id=\"r1\"><n>1</n></item></results>",
           "<a>\n    line one\n      indented\n    line three\n  </a>",
           "<ns:a xmlns:ns=\"urn:x\" ns:k=\"v\"><ns:b>1</ns:b></ns:a>",
           "<a title=\"Ops &amp; Eng\"><summary>Cost < 5% & more <x@y.io></summary></a>",
           "<transcript>just the words</transcript>",
           "<root>\n<child a=\"1\">\n  text\n</child>\n<child>two</child>\n</root>", "<unclosed a=\"1\">x", "<self a='1'/>",
           "<?xml version=\"1.0\"?>\n<a b=\"1\">&bogus; entity</a>", "<a>&amp;&amp; & &lt;</a>"]
    tool_texts = [["{\"notes\": [{\"id\": \"x\"}]}"], ["hello"], ["```json\n{\"a\": 1}\n```"], ["```\n<a k=\"v\"/>\n```"],
                  ["Here are your meetings:\n<meetings_data count=\"0\"></meetings_data>"], ["line one\nline two <b>"],
                  ["", "   "], ["{\"a\": 1, \"a\": 2}"], ["123"], ["null"], ["part one", "<a k=\"1\"/>"],
                  [tg.TRANSCRIPT_XML], ["<transcript>no attributes</transcript>"], [tg.LIVE_LIST], ["[1, 2]"]]
    payloads = [[{"id": "1"}, "junk"], {"meetings": [{"document_id": "2"}]}, {"data": {"results": [{"id": "3"}]}},
                {"id": "solo", "title": "t"}, "text", tg.LIST_XML, {"meetings_data": {"meeting": [{"id": "n"}]}},
                {"data": {"x": 1}, "items": [{"id": "i"}, 3]}, {"count": 0}, None, 5, tgm.GRANOLA_XML]
    schemas = [tg.LIST_SCHEMA, tg.LEGACY_SCHEMA, None, {"properties": {"meeting_ids": {"type": "array"}}},
               {"properties": {"document_ids": {"type": "string"}, "limit": {"type": "integer"}}},
               {"properties": {"time_range": {"type": "string", "enum": ["this_week", "last_week", "last_30_days"]}}},
               {"properties": {"time_range": {"type": "string"}, "start_date": {}, "skip": {}}},
               {"properties": {"id": {}, "since": {}, "to": {}, "max_results": {}, "page": {}}}]
    wants = [{"since": "2026-09-01", "limit": 20, "offset": None}, {"ids": ["a", "b"]}, {"id": ["only"]},
             {"id": "one", "until": "2026-09-30"}, {"folder": "f1", "unknown": 1}]
    list_calls = [[None, None, 50, 0], ["2026-02-01", None, 50, 0], ["2026-02-01", "2026-02-03", 20, 40],
                  [None, "2026-02-03", 10, 0]]
    today = date(2026, 2, 4)

    def day(s):
        return date.fromisoformat(s) if s else None

    accounts = [tg.ACCOUNT_JSON, {}, None, {"email": "a@b.c"},
                '<account email="x@y.z"><active_workspace id="w1" display_name="Lab"/>'
                "<mcp_note_access><scopes>personal</scopes></mcp_note_access></account>",
                "Signed in as sam@example.edu (personal)", {"account": {"email": "w@x.y", "workspace": "Plain"},
                                                            "workspace_id": "ignored"},
                {"user": {"email": "u@v.w"}, "workspace": "Top", "workspace_id": "w9", "scopes": "a, b;c d"},
                {"data": {"active_workspace": {"name": "N", "workspace_id": 7}}, "email": "outer@x.y"},
                {"email": {"email": "nested@x.y"}, "mcp_note_access": ["personal", {"text": "public"}, ""]},
                {"email": "e@f.g", "mcp_note_access": "personal public"}, [1, 2]]
    transcripts = [{"transcript": "plain"}, {"text": "t"}, {"content": [{"text": "a"}, "b"]}, "just text", None,
                   {"transcript": "", "text": "fallback"}, {"other": 1}, ["x", {"markdown": "y"}]]
    dedents = ["    a\n    b", "  a\n    b\n  c", "\ta\n  b", "    a\n\n    b\n   \n", "no indent", "  x\n\ty",
               "   \n   ", "  a\r\n  b", ""]

    with tempfile.TemporaryDirectory() as tmp:
        cc = ClientConfig(home=Path(tmp), oauth_callback_port=3334)
        oauth = GranolaOAuth(cc)
        oauth._meta = GRANOLA_AUTH_META
        _write_private(cc.client_path, {"client_id": "client-abc/123 x+y", "redirect_uri": oauth.redirect_uri})
        client_file = cc.client_path.read_text()
        url = oauth.build_authorize_url("st4te_-x", "ch4llenge~")
        cc.oauth_prompt = ""
        url_bare = oauth.build_authorize_url("s", "c")
        tokens = {"access_token": "at.é", "token_type": "Bearer", "expires_in": 3600, "refresh_token": "rt",
                  "scope": "mcp offline_access", "expires_at": 1758812345.123456, "nested": {"a": [1, {}], "b": []}}
        _write_private(Path(tmp) / "tokens.json", tokens)
        tokens_file = (Path(tmp) / "tokens.json").read_text()

    return {
        "xml": [[x, granola.xml_to_data(x)] for x in xml],
        "loose": [[x, granola._loose_parse(x)] for x in xml],
        "access_notice": [[x, granola.access_notice(x)] for x in xml[:7]],
        "tool_result": [[t, granola.parse_tool_result(SimpleNamespace(structuredContent=None,
                                                                      content=[SimpleNamespace(text=s) for s in t]))]
                        for t in tool_texts],
        "extract": [[p, granola.extract_meetings(p)] for p in payloads],
        "build_args": [[s, w, granola.build_args(s, w)] for s in schemas for w in wants],
        "list_args": [[s, c, granola.list_args(s, day(c[0]), day(c[1]), c[2], c[3], today=today)]
                      for s in schemas for c in list_calls],
        "account": [[a, (lambda i: {k: v for k, v in i.items() if k != "raw"})(granola.normalize_account(a))]
                    for a in accounts],
        "account_label": [[i, granola.account_label(i)] for i in [{"email": "a@b.c", "workspace": "W"}, {"email": " a@b.c "},
                                                                  {"email": ""}, None, {"email": "x", "workspace": " "}]],
        "transcript_text": [[t, granola.transcript_text(t)] for t in transcripts],
        "dedent": [[t, textwrap.dedent(t)] for t in dedents],
        "urlencode": urlencode({"a b": "c/d:e?f=g&h", "é": "~_.-!*()'", "plus": "1+1"}),
        "parse_qs": [[q, {k: v[0] for k, v in parse_qs(q).items()}]
                     for q in ["code=abc&state=x%2By+z", "error=access_denied&error_description=", "a=1&a=2&b=%E2%9C%93",
                               "noval&=x&c=d;e=f"]],
        "authorize_url": url,
        "authorize_url_bare": url_bare,
        "auth_meta": GRANOLA_AUTH_META,
        "client_file": client_file,
        "tokens": tokens,
        "tokens_file": tokens_file,
    }


def page_text() -> None:
    """The pages' CSS and JavaScript, copied into C# exactly (src/StudyStash.Library/PageText.cs)."""
    from granola_share import client_app, library_setup, ui, web

    consts = [("Css", ui.CSS), ("Js", ui.JS), ("MathJs", ui.MATH_JS), ("Katex", ui.KATEX), ("IconLinks", ui.ICON_LINKS),
              ("LibraryCsp", web.CSP), ("AppCss", client_app.APP_CSS), ("AppJs", client_app.APP_JS),
              ("AppCsp", client_app.CSP), ("SetupCss", library_setup.SETUP_CSS), ("SetupJs", library_setup.SETUP_JS)]

    def literal(s: str) -> str:
        out = []
        for ch in s:
            if ch == "\\":
                out.append("\\\\")
            elif ch == '"':
                out.append('\\"')
            elif ch == "\n":
                out.append("\\n")
            elif ch == "\t":
                out.append("\\t")
            elif " " <= ch <= "~":
                out.append(ch)
            else:
                out.append(f"\\u{ord(ch):04x}")
        return '"' + "".join(out) + '"'

    lines = ["// Made by engine/tests/golden.py from the Python engine's own text: run that again rather than edit this.",
             "// (The pages must look the same whichever engine serves them.)", "", "namespace StudyStash.Library;", "",
             "internal static class PageText", "{"]
    for name, value in consts:
        parts = value.splitlines(keepends=True) or [""]
        lines.append(f"    public const string {name} =")
        lines += [f"        {literal(p)}" + (" +" if i < len(parts) - 1 else ";") for i, p in enumerate(parts)]
        lines.append("")
    lines[-1] = "}"
    (ROOT / "engine" / "src" / "StudyStash.Library" / "PageText.cs").write_bytes(("\n".join(lines) + "\n").encode())


def library_cases() -> dict:
    """Stage 3: the helpers the library's pages are made of."""
    from granola_share import hostinfo, ui, update

    names = ["CS 101", "Bio 110", "Unsorted", "", "Calc II", "Bio 🧬 110", "Ümlaut", "a/b c"]
    dates = ["2026-09-14T10:00:00", "2026-09-14T10:00:00Z", "2026-09-14", "2026-09-14T00:05:00", "2026-09-14T12:30",
             "2026-09-14 23:59:59", "Sep 14", "", None, "2026-02-29", "2026-09-14T10", "2026-09-14T10:00:00.123+00:00"]
    snippets = [["The derivative measures how fast a function changes at a point.", "fast"],
                ["# Heading\n\n**Bold** osmosis and *more* `code` > quote | pipe", "osmosis"], ["short", "SHORT"],
                ["no match here", "zebra"], ["x" * 200 + "needle" + "y" * 200, "needle"], ["text", ""],
                ["<b>&amp;</b> tags & osmosis", "OSMOSIS"]]
    ts = [{"installed": False}, {"installed": True, "state": "NeedsLogin"}, {"installed": True, "state": "Stopped"},
          {"installed": True, "state": "NeedsMachineAuth"}, {"installed": True, "state": "Starting"},
          {"installed": True, "running": True}]
    return {
        "esc": [[s, ui.esc(s)] for s in ["<b>\"x\" & 'y'</b>", "plain", "", "é → ✓"]],
        "hue": [[n, ui.hue(n), ui.hue_style(n)] for n in names],
        "class_url": [[n, ui.class_url(n)] for n in names],
        "short_date": [[d, ui.short_date(d)] for d in dates],
        "long_date": [[d, ui.long_date(d)] for d in dates],
        "snippet": [[t, q, ui.snippet(t, q)] for t, q in snippets],
        "tailscale_problem": [[t, hostinfo.tailscale_problem(t)] for t in ts],
        "invite": [[u, k, hostinfo.invite_commands(u, k)] for u, k in
                   [["http://mini.tail.ts.net:8787", "maple-otter"], ["http://100.64.0.9:8787", "it's a \"pass\" $word"],
                    ["http://pc:8787", ""], ["http://x:1", "a b;c"]]],
        "parse_version": [[v, list(update.parse_version(v))] for v in ["v0.4.4", "0.4", "v1.2.3.4", "", "x", "2.0.0+dev"]],
        "safe_next": [[n, __import__("granola_share.web", fromlist=["safe_next"]).safe_next(n)]
                      for n in ["/note/n1?x=1", "https://evil.com", "//evil.com", "/\\evil.com", "/\t/evil.com", "/\n/evil.com",
                                "evil.com", "", "/", "/settings?saved=1"]],
    }


PAGE_MODELS = [{"name": "qwen3:1.7b", "size_gb": 1.4}, {"name": "gemma4:e4b", "size_gb": 9.6}]
PAGE_TAILSCALE = {"installed": True, "running": True, "state": "Running", "dns": "mini.tail.ts.net", "ips": ["100.64.0.9"]}


def seed_library(store) -> None:
    """The lectures the page cases show: the C# tests seed the same ones (LibraryPageTests.Seed)."""
    from granola_share.granola import Meeting

    def save(mid, title, date, cls, by, conf, topics, **kw):
        store.save(Meeting(id=mid, title=title, date=date, owner="Sam", **kw),
                   Classification(cls, conf, by, kw.pop("lecture", ""), topics))

    save("n1", "Cells", "2026-09-01T09:00:00", "Bio 110", "folder", 0.95, ["cells", "membranes"],
         notes_markdown="membranes", transcript="today we talk about the cell membrane and osmosis")
    save("n2", "Loops & <b>ranges</b>", "2026-09-03", "CS 101", "ollama", 0.875, ["loops"], notes_markdown="for loops")
    save("n3", "Lunch", "2026-09-02T12:30:00", "Unsorted", "none", 0.0, [])
    save("n4", "Old class lecture", "2026-08-30", "History 9", "human", 1.0, [], notes_markdown="x")
    store.enqueue(Meeting(id="q1", title="Waiting lecture", date="2026-09-04"))
    store.enqueue(Meeting(id="f1", title="Broken lecture", date="2026-09-05"))
    store.conn.execute("UPDATE notes SET status='failed', error=? WHERE id='f1'", ("Summary with big:35b failed: " + "x" * 200,))
    store.conn.commit()


def library_pages() -> dict:
    """Pages as the Python engine serves them, with the nonce pinned to "NONCE"."""
    import secrets
    import tempfile

    from fastapi.testclient import TestClient

    from granola_share.pipeline import Pipeline
    from granola_share.store import Store
    from granola_share.update import Release
    from granola_share.web import create_app

    real = secrets.token_urlsafe
    secrets.token_urlsafe = lambda n=None: "NONCE"
    pages = {}
    try:
        with tempfile.TemporaryDirectory() as tmp:
            cfg = Config(home=Path(tmp) / "home", pool_dir=Path(tmp) / "pool", pool_name="Fall \"26\" & co",
                         pool_password="pw", ollama_model="qwen3:1.7b", summary_model="gemma4:e4b", min_confidence=0.7,
                         classes=[ClassDef("CS 101", ["cs101"], "Intro to programming"), ClassDef("Bio 110", [], ""),
                                  ClassDef("Calc II", ["math"], "Series.")])
            store = Store(cfg.db_path, cfg.pool_dir)
            seed_library(store)
            pipeline = Pipeline(cfg, store, log=lambda *_: None)
            newer = Release("v9.9.9", (9, 9, 9), "https://x/v9.9.9.tar.gz", "https://x/releases/v9.9.9")
            apps = {
                "": create_app(cfg, store, pipeline, list_models=lambda h: PAGE_MODELS, tailscale=lambda: PAGE_TAILSCALE,
                               latest=lambda *a: None),
                "no-ollama:": create_app(cfg, store, pipeline, list_models=lambda h: None, tailscale=lambda: {},
                                         latest=lambda *a: newer),
            }
            paths = ["/", "/unsorted", "/class/Bio%20110", "/class/CS%20101", "/class/Calc%20II", "/class/History%209",
                     "/search?q=osmosis", "/search?q=zzz", "/search?q=%20", "/search", "/settings", "/settings?saved=1",
                     "/settings?queued=3", "/settings?queued=1", "/login", "/login?bad=1&next=/x%3Fy", "/note/missing"]
            for prefix, app in apps.items():
                c = TestClient(app, follow_redirects=False)
                c.post("/login", data={"password": "pw", "next": "/"})
                for p in (paths if not prefix else ["/settings"]):
                    r = c.get(p)
                    pages[prefix + p] = {"status": r.status_code, "html": r.text, "csp": r.headers.get("content-security-policy")}
            cfg.pool_password = ""
            pages["open:/"] = {"status": 200, "html": TestClient(apps[""]).get("/").text, "csp": None}
    finally:
        secrets.token_urlsafe = real
    return pages


def setup_pages() -> dict:
    """The setup page in three states, with every check answered the same way the C# tests answer it."""
    import tempfile

    from granola_share import library_setup

    def fakes(**over):
        kw = dict(list_models=lambda host: PAGE_MODELS, ollama_installed=lambda: True, start_ollama=lambda host: True,
                  install_ollama=lambda *a, **k: True, pull_model=lambda *a, **k: (True, ""),
                  try_model=lambda host, model: (2.0, ""), tailscale=lambda: PAGE_TAILSCALE,
                  install_tailscale=lambda *a, **k: True, open_tailscale=lambda system=None: True,
                  firewall=lambda port: True, open_firewall=lambda port: True, sleep_minutes=lambda system=None: 0,
                  keep_awake=lambda system=None: True, ram_gb=lambda: 32.0, disk_free=lambda: 200.4,
                  port_status=lambda port: "free", install_autostart=lambda role, home: None,
                  wait_healthy=lambda cfg: True, system="Darwin")
        kw.update(over)
        return kw

    draft = {"pool_name": "Fall <2026>", "pool_password": "maple-otter", "pool_dir": "/srv/Lecture notes", "web_port": 8791}
    states = {
        "fresh": ({"pool_password": "maple-otter", "pool_dir": "/srv/Lecture notes"}, dict(list_models=lambda h: None, ollama_installed=lambda: False,
                                                        tailscale=lambda: {"installed": False}, ram_gb=lambda: 15.6,
                                                        disk_free=lambda: 12.5)),
        "windows": ({**draft, "library": True, "models": True, "summary_model": "gemma4:e4b", "ollama_model": "qwen3:1.7b",
                     "classes": [{"name": "CS 101", "aliases": ["cs101", "intro"], "description": "x"}, {"name": "Bio 110"}],
                     "autostart": False, "auto_update": False},
                    dict(system="Windows", firewall=lambda port: False, sleep_minutes=lambda system=None: 30,
                         tailscale=lambda: {"installed": True, "state": "NeedsLogin"}, ram_gb=lambda: None, disk_free=lambda: None)),
        "mac-asleep": ({**draft, "library": True}, dict(sleep_minutes=lambda system=None: 1, list_models=lambda h: [])),
        "finished": ({**draft, "library": True, "models": True, "summary_model": "qwen3:1.7b"}, {}),
    }
    out = {}
    for name, (data, over) in states.items():
        with tempfile.TemporaryDirectory() as tmp:
            home = Path(tmp)
            (home / "setup_draft.json").write_text(json.dumps(data))
            s = library_setup.LibrarySetup(home, **fakes(**over))
            if name == "finished":
                s.draft["finished"] = True
            out[name] = {"draft": data, "html": library_setup.render(s)}
    return out


# --- stage 5: the platform ---------------------------------------------------------------------------------

# Every doctor scenario: what each check is told, and (in platform_cases) what the Python engine says back. The C#
# tests build the same fakes from these inputs, so a new scenario needs nothing in C#.
HEALTHY = {"status": 200, "body": {"ok": True, "version": "{version}"}}
BOTH_MODELS = [["qwen3:1.7b", 1.4], ["big:35b", 23]]
SERVER_SCENARIOS = {
    "all-good": dict(system="Linux", health=HEALTHY, service="running", models=BOTH_MODELS,
                     tailscale={"running": True, "dns": "mini.ts.net", "ips": ["100.1.1.1"]}),
    "problems": dict(system="Darwin", health=None, service="stopped", models=[["qwen3:1.7b", 1.4]],
                     tailscale={"installed": False}, sleep=10, cfg={"classes": []},
                     log="started\n\nOSError: [Errno 48] address already in use\n"),
    "old-version": dict(system="Linux", health={"status": 200, "body": {"ok": True}}, service="running", models=None,
                        ollama_installed=True, tailscale={"running": False, "installed": True}),
    "no-ollama-mac": dict(system="Darwin", health=HEALTHY, service="running", models=None, ollama_installed=False,
                          tailscale={"installed": True, "running": False, "state": "NeedsLogin"}, sleep=0),
    "closed-ollama-mac": dict(system="Darwin", health=HEALTHY, service="running", models=None, ollama_installed=True,
                              tailscale={"installed": True, "state": "Stopped"}, sleep=0),
    "closed-ollama-windows": dict(system="Windows", health=HEALTHY, service="missing", models=None, ollama_installed=True,
                                  tailscale={"installed": True, "running": True, "dns": "pc.ts.net"}, firewall=None,
                                  sleep=0),
    "windows-blocked": dict(system="Windows", health=HEALTHY, service="running", models=BOTH_MODELS,
                            tailscale={"installed": True, "running": True, "dns": "pc.ts.net", "ips": []},
                            firewall=False, sleep=30),
    "windows-fine": dict(system="Windows", health=HEALTHY, service="running", models=BOTH_MODELS,
                         tailscale={"installed": True, "running": True, "ips": ["100.9.9.9"]}, firewall=True, sleep=0),
    "port-taken": dict(system="Linux", health={"status": 404, "body": {}}, service="missing", models=BOTH_MODELS,
                       tailscale={"installed": True, "state": "NeedsMachineAuth"}, latest="v9.9.9",
                       cfg={"pool_password": "", "server_sync": True}),
    "down-not-installed": dict(system="Linux", health=None, service="missing", models=BOTH_MODELS,
                               tailscale={"running": True, "ips": ["100.1.1.1"]}, log="something\n",
                               cfg={"ollama_enabled": False}, latest="v0.0.1"),
    "no-summaries": dict(system="Linux", health={"status": 200, "body": {"version": "0.0.9"}}, service="stopped",
                         models=[["big:35b", 23]], tailscale={}, cfg={"summary_enabled": False}),
}
CLIENT_SCENARIOS = {
    "not-signed-in": dict(system="Linux", server={"ok": {}}, service="running", tokens=False),
    "free-plan": dict(system="Linux", server={"ok": {"pool_name": "Fall"}}, probe={"ok": [4, False]}, service="running"),
    "unreachable": dict(system="Linux", server={"error": "could not reach http://mini:8787/api/health"}, probe={"ok": [0, True]},
                        service="missing"),
    "mac-copying": dict(system="Darwin", server={"ok": {"pool_name": "Fall"}}, probe={"ok": [1, False]}, service="stopped",
                        laptop={"granola": "/Applications/Granola.app", "granola_here": True,
                                "tailscale": {"installed": True, "running": True, "dns": "air.ts.net"}},
                        cfg={"copy_transcripts": True, "mode": "ask"},
                        status={"trusted": True, "last_copy": {"title": "Cells", "chars": 5210, "at": "10:02"}},
                        log="line one\nline two\n"),
    "mac-untrusted": dict(system="Darwin", server={"ok": {}}, probe={"ok": [2, None]}, service="running",
                          laptop={"granola": None, "granola_here": True, "tailscale": {"installed": False}},
                          cfg={"copy_transcripts": True}, status={"trusted": False}),
    "mac-watcher-new": dict(system="Darwin", server={"ok": {"pool_name": "Fall"}}, probe={"error": "401 Unauthorized " + "x" * 300},
                            service="running", laptop={"granola": None, "granola_here": True,
                                                       "tailscale": {"installed": True, "state": "NeedsLogin"}},
                            cfg={"copy_transcripts": True}),
    "mac-copy-off": dict(system="Darwin", server={"ok": {"pool_name": "Fall"}}, probe={"ok": [3, False]}, service="running",
                         latest="v9.9.9", status={"trusted": True}),
    "mac-allowed": dict(system="Darwin", server={"ok": {"pool_name": "Fall"}}, probe={"ok": [1, True]}, service="running",
                        cfg={"copy_transcripts": True}, status={"trusted": True}),
    "no-config": dict(system="Linux", server={"ok": {}}, service="running", cfg={"server_url": ""}),
}


def platform_cases() -> dict:
    """Stage 5: the service files, the firewall rule, and doctor's checks and how it prints them."""
    import tempfile
    from types import SimpleNamespace

    from granola_share import __version__, autostart, doctor, ready
    from granola_share.config import save_client_config, save_config
    from granola_share.update import Release

    args = ["/opt/Study Stash/studystash", "--home", "/home/student/.granola-share", "run"]
    odd = ["/opt/a&b <c>/studystash", "--home", "/home/student/x & y", "client", "run"]
    log = Path("/home/student/.granola-share/logs/server.log")
    programs = [r"C:\Program Files\Study Stash\studystash.exe", r"C:\Users\it's me\studystash.exe"]
    real_pythons = ready._pythons
    ready._pythons = lambda: programs
    try:
        seen = []
        ready.open_firewall(8790, run=lambda a, **k: seen.append(a) or SimpleNamespace(returncode=0, stdout="8790"))
        firewall = {"script": ready.firewall_script(8790), "elevate": seen[0][-1], "programs": programs}
    finally:
        ready._pythons = real_pythons

    def release(tag):
        return Release(tag, tuple(int(n) for n in tag[1:].split(".")), "u", "p") if tag else None

    def fill(value):
        return json.loads(json.dumps(value).replace("{version}", __version__))

    def scrub(value, root):
        return json.loads(json.dumps(value).replace(json.dumps(str(root))[1:-1], "{root}"))

    def shown(checks, root):
        encodings = {}
        for name, enc in (("unicode", "utf-8"), ("ascii", "ascii")):
            real = sys.stdout
            sys.stdout = SimpleNamespace(encoding=enc)
            try:
                encodings[name] = doctor.format_checks("title", checks)
            finally:
                sys.stdout = real
        return scrub({"checks": [[c.name, c.state, c.detail, c.fix] for c in checks], **encodings}, root)

    def raises(message):
        def fail(*a, **k):
            raise RuntimeError(message)
        return fail

    # The plist names the home folder's .local/bin: a made-up home, never this computer's.
    real_home = Path.home
    Path.home = classmethod(lambda cls: Path("/home/student"))
    try:
        services = {"plist": autostart.render_plist("com.granola-share.server", args, log),
                    "plist_odd": autostart.render_plist("com.granola-share.client", odd, log),
                    "systemd": autostart.render_systemd("granola-share server", args),
                    "systemd_odd": autostart.render_systemd("granola-share client", odd),
                    "args": args, "odd": odd, "log": str(log), "user_home": "/home/student"}
    finally:
        Path.home = real_home
    out = {"services": services, "firewall": firewall, "server": {}, "client": {}}
    for name, sc in SERVER_SCENARIOS.items():
        sc = fill(sc)
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            cfg = Config(home=root / "home", pool_dir=root / "pool", pool_password="pw", ollama_model="qwen3:1.7b",
                         summary_model="big:35b", classes=[ClassDef("CS 101")])
            for k, v in sc.get("cfg", {}).items():
                setattr(cfg, k, [ClassDef(**c) for c in v] if k == "classes" else v)
            save_config(cfg)
            if sc.get("log") is not None:
                cfg.log_dir.mkdir(parents=True, exist_ok=True)
                (cfg.log_dir / "server.log").write_text(sc["log"])
            health = sc["health"]
            get = raises("refused") if health is None else \
                (lambda url, headers, timeout, h=health: SimpleNamespace(status_code=h["status"], json=lambda: h["body"]))
            checks = doctor.server_checks(
                cfg, http_get=get, service_status=lambda r, v=sc["service"]: v,
                list_models=lambda h, m=sc["models"]: None if m is None else [{"name": n, "size_gb": g} for n, g in m],
                ollama_installed=lambda v=sc.get("ollama_installed", False): v, tailscale=lambda t=sc["tailscale"]: t,
                sleep_minutes=lambda v=sc.get("sleep"): v, firewall=lambda port, v=sc.get("firewall"): v,
                latest=lambda t=sc.get("latest"): release(t), system=sc["system"])
            out["server"][name] = {"given": sc, **shown(checks, root)}
    for name, sc in CLIENT_SCENARIOS.items():
        sc = fill(sc)
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            cc = ClientConfig(home=root / "home", server_url="http://mini:8787", pool_key="pw", pool_name="Fall")
            for k, v in sc.get("cfg", {}).items():
                setattr(cc, k, v)
            save_client_config(cc)
            if sc.get("tokens", True):
                cc.tokens_path.write_text("{}")
            if "status" in sc:
                (cc.home / "transcripts").mkdir(parents=True)
                (cc.home / "transcripts" / "status.json").write_text(json.dumps(sc["status"]))
            if sc.get("log") is not None:
                cc.log_dir.mkdir(parents=True, exist_ok=True)
                (cc.log_dir / "client.log").write_text(sc["log"])
            server, probe = sc["server"], sc.get("probe", {"ok": [0, None]})
            laptop = sc.get("laptop", {"granola": None, "granola_here": False, "tailscale": {"running": True}})
            checks = doctor.client_checks(
                cc, check_server=raises(server["error"]) if "error" in server else (lambda u, k, v=server["ok"]: v),
                probe=raises(probe["error"]) if "error" in probe else (lambda c, v=probe["ok"]: tuple(v)),
                service_status=lambda r, v=sc["service"]: v, latest=lambda t=sc.get("latest"): release(t),
                system=sc["system"], laptop=lambda v=laptop: v)
            out["client"][name] = {"given": sc, **shown(checks, root)}
    with tempfile.TemporaryDirectory() as tmp:
        said = []
        doctor.run(Path(tmp), print_fn=said.append)
        out["nothing"] = scrub(said, Path(tmp))
    return out


# --- stage 4: the laptop ---------------------------------------------------------------------------------

COPIED_TEXTS = [
    "Meeting Title: Membranes and osmosis\nDate: Sep 24\n\nTranscript:\nMe: osmosis and membranes, three cases.",
    "Meeting Title: Year end\nDate: Dec 31\nTranscript:\nMe: the last one",
    "Meeting Title: Full\nDate: September 3, 2025\nMeeting participants: Me, You\nTranscript:\nline one\nline two\n\n",
    "Meeting Title: Odd date\nDate: 2026-09-24\nTranscript:\nbody",
    "no headers at all, just text",
    "Meeting Title: Empty\nTranscript:\n   \n",
    "",
    "Meeting Title:   Spaces  \r\nDate:  sep 5, 2026 \r\nTranscript:\r\nwindows lines\r\n",
    "Meeting Title: Leap\nDate: Feb 29\nTranscript:\nx",
    "Date: Sep 24 2026\nTranscript:\nno title",
    "Meeting Title: Café — ünïcode\nDate: Sep 20\nTranscript:\nMe: ça va",
]
# (text, recorded_to) saved in order, then (title, day) looked up: the watcher finds a lecture's copied transcript.
STORE_SAVES = [
    ["Meeting Title: Membranes and osmosis\nDate: Sep 24\nTranscript:\nshort", None],
    ["Meeting Title: Membranes and osmosis\nDate: Sep 24\nTranscript:\na much longer transcript of the lecture", None],
    ["Meeting Title: Membranes and osmosis\nDate: Sep 24\nTranscript:\ntiny", None],
    ["Meeting Title: \nDate: Sep 24\nTranscript:\ncopied as the recording stopped", "2026-09-24T15:58:00"],
    ["Meeting Title: Later\nDate: Sep 24\nTranscript:\nthe next recording", "2026-09-24T18:30:00"],
    ["Meeting Title: Bio: lab/2 <draft>\nDate: Sep 23\nTranscript:\nfile name with odd characters", None],
]
STORE_FINDS = [["Membranes and osmosis", "2026-09-24"], ["membranes  AND osmosis!", "2026-09-25T09:00:00"],
               ["Membranes and osmosis", "2026-09-27"], ["Untitled", "2026-09-24T15:00:00"], ["Untitled", "2026-09-24T16:30:00"],
               ["Untitled", "2026-09-24"], ["Bio: lab/2 <draft>", None], ["Nothing like it", "2026-09-24T12:00:00"],
               ["Untitled", "2026-09-24T09:00:00"]]


class GoldenGranola:
    """The watcher's Granola: three lectures, their full notes, and a transcript for each."""

    def __init__(self):
        from granola_share.granola import Meeting

        self.stubs = [Meeting(id="a", title="CS101 lec 1", date="2026-09-10"),
                      Meeting(id="b", title="Bio lab — café", date="2026-09-11T14:05:00"),
                      Meeting(id="c", title="Standup", date="2026-09-12T09:00:00Z")]
        self.full = [Meeting(id=m.id, title=m.title, date=m.date, notes_markdown=f"notes {m.id}", attendees=["Ada"],
                             raw={"id": m.id, "n": 1}) for m in self.stubs]

    def session(self):
        from contextlib import asynccontextmanager

        @asynccontextmanager
        async def s():
            yield "session"
        return s()

    async def list_meetings(self, session, since=None, limit=50):
        return list(self.stubs)

    async def get_meetings(self, session, ids):
        from dataclasses import replace

        return [replace(m) for m in self.full if m.id in ids]

    async def get_transcript(self, session, mid):
        return "" if mid == "b" else "the transcript"


LAPTOP_READY = {"granola": "/Applications/Granola.app", "granola_here": True,
                "tailscale": {"installed": True, "running": True, "state": "Running", "dns": "air.tail.ts.net", "ips": ["100.64.0.7"]}}
SEEN = {
    "a": {"decision": "shared", "title": "Membranes lecture", "date": "2026-09-24", "class_name": "Data Science",
          "filed": True, "transcript_chars": 8000, "at": "2026-09-24T16:00:00+00:00"},
    "b": {"decision": "pending", "title": "Bio lab <3>", "date": "2026-09-23", "at": "2026-09-23T10:00:00+00:00"},
    "c": {"decision": "skipped", "title": "Standup", "date": "2026-09-22", "at": "2026-09-22T09:00:00+00:00"},
    "d": {"decision": "shared", "title": "Calc II", "date": "2026-09-21", "filed": False, "at": "2026-09-21T09:00:00+00:00"},
    "e": {"decision": "pending", "title": "Waiting one", "date": "2026-09-20", "error": "401", "transcript_chars": 50,
          "at": "2026-09-20T09:00:00+00:00"},
    "f": {"decision": "shared", "title": "Old", "date": "2026-09-19", "class_name": "CS 101", "at": "2026-09-19T09:00:00+00:00"},
    "g": {"decision": "shared", "title": "Filed bare", "date": "", "filed": True, "at": "2026-09-18T09:00:00+00:00"},
}
# name: (system, client.toml fields, prefill, signed in, watching, runtime fields, seen, readiness)
LAPTOP_STATES = {
    "fresh-linux": ("Linux", {}, None, False, False, {}, None,
                    {"granola": None, "granola_here": False, "tailscale": {"installed": False}}),
    "invite-mac": ("Darwin", {}, {"server": "http://mini:8787", "key": "p&w \"x\""}, False, False,
                   {"tailscale_job": {"running": True, "error": None}}, None,
                   {"granola": None, "granola_here": True, "tailscale": {"installed": True, "state": "NeedsLogin"}}),
    "connected-mac": ("Darwin", {"server_url": "http://mini:8787", "pool_key": "pw", "pool_name": "Fall <pool>"}, None, False,
                      False, {"login": {"running": True, "error": None, "started": 1.5}}, None, LAPTOP_READY),
    "login-error-windows": ("Windows", {"server_url": "http://mini:8787", "pool_key": "pw", "pool_name": "Fall"}, None, False,
                            False, {"login": {"running": False, "error": "Granola said <no>", "started": None},
                                    "tailscale_job": {"running": False, "error": "it isn't installed yet"}}, None,
                            {"granola": None, "granola_here": True, "tailscale": {"installed": False}}),
    "signed-in-mac": ("Darwin", {"server_url": "http://mini:8787", "pool_key": "pw", "pool_name": "Fall", "mode": "ask",
                                 "copy_transcripts": True}, None, True, False, {}, None, LAPTOP_READY),
    "status-mac": ("Darwin", {"server_url": "http://mini.tail.ts.net:8787", "pool_key": "pw", "pool_name": "Fall pool",
                              "display_name": "Sam", "copy_transcripts": True}, None, True, True, {}, SEEN, LAPTOP_READY),
    "status-password-windows": ("Windows", {"server_url": "http://mini:8787", "pool_key": "old", "pool_name": "Fall"}, None,
                                True, True, {"client": {"last_error": "Fall turned down this laptop's password, so lectures are waiting here.",
                                                        "send_problem_kind": "password"}}, SEEN,
                                {"granola": "C:\\Granola.exe", "granola_here": True, "tailscale": {"installed": True, "running": True}}),
    "status-signin-linux": ("Linux", {"server_url": "http://mini:8787", "pool_key": "pw", "pool_name": "Fall"}, None, True, True,
                            {"client": {"last_error": "token refresh failed (401): invalid_grant. Run `granola-share login`.",
                                        "send_problem_kind": None}}, {}, {"granola": None, "granola_here": False,
                                                                           "tailscale": {"installed": True, "state": "Stopped"}}),
    "status-other-mac": ("Darwin", {"server_url": "http://mini:8787", "pool_key": "pw", "pool_name": "Fall"}, None, True, True,
                         {"client": {"last_error": "boom " + "x" * 300, "send_problem_kind": None}}, None,
                         {"granola": None, "granola_here": True, "tailscale": {"installed": True, "running": True}}),
    "status-unreachable-windows": ("Windows", {"server_url": "http://mini:8787", "pool_key": "pw", "pool_name": "Fall"}, None,
                                   True, True, {"client": {"last_error": "Can't reach Fall right now.",
                                                           "send_problem_kind": "unreachable"}}, None,
                                   {"granola": "C:\\G.exe", "granola_here": True, "tailscale": {"running": True}}),
    "not-watching-yet": ("Linux", {"server_url": "http://mini:8787", "pool_key": "pw", "pool_name": "Fall"}, None, True, False,
                         {}, SEEN, {"granola": None, "granola_here": False, "tailscale": {"running": True}}),
}


def laptop_cases() -> dict:
    """Stage 4: copied transcripts, the watcher's record of what it sent, and the laptop's Study Stash page."""
    import asyncio
    import secrets
    import tempfile
    from datetime import date, datetime, timezone
    from types import SimpleNamespace

    from fastapi.testclient import TestClient

    from granola_share import client_app
    from granola_share import transcript_grab as tg
    from granola_share.client import ShareClient
    from granola_share.config import save_client_config

    today = date(2026, 9, 25)

    def copied(c):
        return None if c is None else {"title": c.title, "date": str(c.date) if c.date else None, "body": c.body}

    out = {"today": str(today),
           "parse": [[t, copied(tg.parse_copied(t, today=today))] for t in COPIED_TEXTS],
           "norm": [[t, tg._norm_title(t)] for t in ["Membranes & Osmosis!", "  CS 101: Lecture #3 ", "Ümlaut café", "",
                                                    "İstanbul", "a_b-c.d", "ÀÉÎ oe", "Kelvin \u212a", "ΣΊΣΥΦΟΣ ẞ"]]}
    with tempfile.TemporaryDirectory() as tmp:
        store = tg.TranscriptStore(Path(tmp))
        saved = []
        for text, stopped in STORE_SAVES:
            path, changed = store.save(tg.parse_copied(text, today=today), text,
                                       recorded_to=datetime.fromisoformat(stopped) if stopped else None)
            saved.append([path.name, changed])
        files = sorted(p.name for p in store.dir.iterdir())
        out["store"] = {"saved": saved, "files": files, "finds": [[t, d, store.find(t, d)] for t, d in STORE_FINDS]}

    # The watcher: three lectures in ask mode (send, skip, no answer), then the library files what it got, then the
    # unanswered one is sent. What it asked, sent, said, and wrote down each time.
    with tempfile.TemporaryDirectory() as tmp:
        cc = ClientConfig(home=Path(tmp), server_url="http://mini:8787/", pool_key="pw", pool_name="Pool — ü",
                          display_name="Sam", mode="ask", include_transcripts=True)
        answers = [True, False, None]
        asked, posted, notified, got = [], [], [], []

        def ask(title, text, yes=None, no=None, timeout=0):
            asked.append([title, text, yes, no, timeout])
            return answers.pop(0) if answers else True

        def post(url, payload, headers):
            posted.append([url, payload, headers["Authorization"]])
            return {"class_name": "CS 101"} if payload["id"] == "a" else {"class_name": None}

        def get(url, headers):
            got.append(url)
            return {"status": "done", "class_name": "Bio 110", "summary_model": "m"} if "/a/" not in url else {"status": "queued"}

        clock = iter([datetime(2026, 9, 24, 16, 0, 0, 123456, tzinfo=timezone.utc)] * 4
                     + [datetime(2026, 9, 24, 16, 5, 0, tzinfo=timezone.utc)] * 20)
        now = [None]

        def tick():
            now[0] = next(clock)
            return now[0]

        client = ShareClient(cc, GoldenGranola(), ask=ask, notify=lambda t, x: notified.append([t, x]), post=post, get=get,
                             log=lambda *_: None, clock=tick)
        rounds = []
        for _ in range(2):
            rep = asyncio.run(client.poll_once(since=date(2026, 9, 1)))
            rounds.append({"report": {"listed": rep.listed, "considered": rep.considered, "shared": [list(x) for x in rep.shared],
                                      "skipped": rep.skipped, "pending": rep.pending, "filed": [list(x) for x in rep.filed],
                                      "errors": rep.errors},
                           "state": cc.state_path.read_bytes().decode("utf-8")})
        out["watcher"] = {"rounds": rounds, "asked": asked, "posted": posted, "notified": notified, "got": got,
                          "clock": ["2026-09-24T16:00:00.123456+00:00"] * 4 + ["2026-09-24T16:05:00+00:00"] * 20}

    # The page, in each state, with the nonce pinned.
    real_token, real_mac, real_windows = secrets.token_urlsafe, client_app.mac, client_app.windows
    secrets.token_urlsafe = lambda n=None: "NONCE"
    pages = {}
    try:
        for name, (system, fields, prefill, signed, is_watching, rt_fields, seen, ready) in LAPTOP_STATES.items():
            client_app.mac = lambda s=system: s == "Darwin"
            client_app.windows = lambda s=system: s == "Windows"
            with tempfile.TemporaryDirectory() as tmp:
                home = Path(tmp) / "home"
                home.mkdir()
                (home / "ui_token").write_text("TOKEN")
                cc = ClientConfig(home=home)
                for k, v in fields.items():
                    setattr(cc, k, v)
                if fields:
                    save_client_config(cc)
                if prefill:
                    (home / "ui_prefill.json").write_text(json.dumps(prefill))
                if signed:
                    cc.tokens_path.write_text("{}")
                if seen is not None:
                    cc.state_path.write_text(json.dumps({"seen": seen, "last_poll": None}))

                class Runtime(client_app.ClientRuntime):
                    watching = is_watching

                    def readiness(self, fresh=False, r=ready):
                        return r

                    def copy_status(self, s=system):
                        # No copier: the C# engine hands copying to the Study Stash app.
                        return ({"available": False, "enabled": self.config().copy_transcripts, "why": "none here",
                                 "allowed": False} if s == "Darwin" else
                                {"available": False, "enabled": False, "allowed": False, "why": "only on macOS for now"})

                rt = Runtime(home, log=lambda s: None)
                if "login" in rt_fields:
                    rt.login = rt_fields["login"]
                if "tailscale_job" in rt_fields:
                    rt.tailscale_job = rt_fields["tailscale_job"]
                if "client" in rt_fields:
                    rt.client = SimpleNamespace(**rt_fields["client"])
                c = TestClient(client_app.create_client_app(rt, port=8765), base_url="http://127.0.0.1:8765", follow_redirects=False)
                unauthed = c.get("/").text
                c.cookies.set("gs_app", "TOKEN")
                r = c.get("/")
                state = c.get("/api/state", headers={"X-Granola-Share": "1"}).text
                library = c.get("/library")
                scrub = lambda text: text.replace(str(home), "{home}")  # noqa: E731
                pages[name] = {"status": r.status_code, "html": scrub(r.text), "csp": r.headers.get("content-security-policy"),
                               "unauthed": unauthed, "state": scrub(state),
                               "library": {"status": library.status_code, "html": library.text,
                                           "csp": library.headers.get("content-security-policy")}}
    finally:
        secrets.token_urlsafe, client_app.mac, client_app.windows = real_token, real_mac, real_windows
    out["pages"] = {"states": {k: list(v) for k, v in LAPTOP_STATES.items()}, "rendered": pages}
    return out


# Granola's sign-in server metadata as fetched from mcp-auth.granola.ai on 2026-09-25 (public, trimmed).
GRANOLA_AUTH_META = {
    "authorization_endpoint": "https://mcp-auth.granola.ai/oauth2/authorize",
    "code_challenge_methods_supported": ["S256"],
    "issuer": "https://mcp-auth.granola.ai",
    "registration_endpoint": "https://mcp-auth.granola.ai/oauth2/register",
    "scopes_supported": ["email", "offline_access", "openid", "profile"],
    "token_endpoint": "https://mcp-auth.granola.ai/oauth2/token",
}


def main() -> None:
    import socket

    # Pages name this computer when Tailscale is off: a made-up name, never the real one (and the same in CI).
    socket.gethostname = lambda: "library-pc"
    OUT.mkdir(parents=True, exist_ok=True)
    configs()
    notes()
    write("cases.json", json.dumps(cases(), indent=1, ensure_ascii=False) + "\n")
    write("granola.json", json.dumps(granola_cases(), indent=1, ensure_ascii=False) + "\n")
    write("library.json", json.dumps(library_cases(), indent=1, ensure_ascii=False) + "\n")
    write("pages.json", json.dumps({"library": library_pages(), "setup": setup_pages()}, indent=1, ensure_ascii=False) + "\n")
    write("platform.json", json.dumps(platform_cases(), indent=1, ensure_ascii=False) + "\n")
    write("laptop.json", json.dumps(laptop_cases(), indent=1, ensure_ascii=False) + "\n")
    page_text()
    print(f"wrote {OUT.relative_to(ROOT)}")


if __name__ == "__main__":
    main()
