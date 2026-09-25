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


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    configs()
    notes()
    write("cases.json", json.dumps(cases(), indent=1, ensure_ascii=False) + "\n")
    print(f"wrote {OUT.relative_to(ROOT)}")


if __name__ == "__main__":
    main()
