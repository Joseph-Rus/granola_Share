import json
import sqlite3
from pathlib import Path
from types import SimpleNamespace

import pytest

from granola_share.config import UNSORTED, ClassDef, Config
from granola_share.granola import Meeting
from granola_share.pipeline import Pipeline
from granola_share.store import Store
from granola_share.summarize import (clean_output, split_transcript, summarize_transcript, transcript_budget,
                                     wants_summary)

LINE = "The derivative measures how fast a function changes at a point.\n"


def cfg_for(tmp_path, **kw) -> Config:
    c = Config(home=tmp_path / "home", pool_dir=tmp_path / "pool", ollama_model="small:1b",
               summary_model="big:35b", classes=[ClassDef("Calc 1", ["math 151"]), ClassDef("Bio 110")])
    for k, v in kw.items():
        setattr(c, k, v)
    return c


def test_split_transcript_respects_budget_and_keeps_text():
    text = LINE * 200
    parts = split_transcript(text, 1000)
    assert len(parts) > 1 and all(len(p) <= 1000 for p in parts)
    assert "".join(parts).replace("\n", "") == text.replace("\n", "")
    # one giant line with no breaks still splits, at sentence ends
    one = ("Limits come first. " * 300).strip()
    parts = split_transcript(one, 500)
    assert all(len(p) <= 500 for p in parts) and parts[0].endswith("first.")


def test_clean_output_strips_think_and_fences():
    assert clean_output("<think>hmm</think>\n```markdown\n## Overview\nx\n```") == "## Overview\nx"
    assert clean_output("  ## A  ") == "## A"


def test_short_transcript_is_one_call_with_chosen_model(tmp_path):
    cfg = cfg_for(tmp_path)
    calls = []

    def chat(cfg, model, prompt, num_ctx):
        calls.append((model, num_ctx, prompt))
        return "## Overview\nDerivatives."

    m = Meeting(id="1", title="Calc lecture 3", date="2026-09-20T09:00", transcript=LINE * 40)
    out = summarize_transcript(m, cfg, chat=chat, show=lambda c, model: 131072)
    assert out == "## Overview\nDerivatives."
    assert len(calls) == 1 and calls[0][0] == "big:35b" and calls[0][1] == 32768  # capped at max_context
    assert "## Key concepts" in calls[0][2] and "Calc lecture 3" in calls[0][2] and "derivative" in calls[0][2]


def test_long_transcript_is_split_then_merged(tmp_path):
    cfg = cfg_for(tmp_path, summary_max_context=8192)
    prompts = []

    def chat(cfg, model, prompt, num_ctx):
        prompts.append(prompt)
        return "## Overview\nmerged" if "Merge them into one set of study notes" in prompt else "- notes"

    budget = transcript_budget(8192)
    m = Meeting(id="1", title="Long", transcript=LINE * (budget // len(LINE) * 3))
    out = summarize_transcript(m, cfg, chat=chat, show=lambda c, model: None)
    assert out == "## Overview\nmerged"
    assert sum("Transcript part" in p for p in prompts) >= 3
    assert "Below are notes on consecutive parts" in prompts[-1]


def test_wants_summary_needs_transcript_and_ai(tmp_path):
    cfg = cfg_for(tmp_path)
    assert wants_summary(Meeting(id="1", transcript=LINE * 40), cfg)
    assert not wants_summary(Meeting(id="1", transcript="hi"), cfg)
    cfg.summary_enabled = False
    assert not wants_summary(Meeting(id="1", transcript=LINE * 40), cfg)


def test_pipeline_uses_our_summary_and_sorts_on_it(tmp_path):
    cfg = cfg_for(tmp_path)
    store = Store(cfg.db_path, cfg.pool_dir)
    seen = {}

    def sort_chat(cfg, prompt, schema):
        seen["prompt"] = prompt
        return json.dumps({"class_name": "Calc 1", "confidence": 0.9, "lecture_title": "Derivatives",
                           "topics": ["derivatives"]})

    p = Pipeline(cfg, store, chat=sort_chat, summarize=lambda m, cfg: "## Overview\nOur own notes on derivatives.",
                 log=lambda *_: None)
    store.enqueue(Meeting(id="m1", title="Lecture", owner="Sam", notes_markdown="Granola's weaker summary",
                          transcript=LINE * 40))
    assert p.run_pending() == 1
    row = store.get("m1")
    assert row["summary_md"].startswith("## Overview") and row["summary_model"] == "big:35b"
    assert row["class_name"] == "Calc 1" and "Our own notes" in seen["prompt"]
    text = open(row["md_path"], encoding="utf-8").read()
    assert "### Overview" in text and "Written by big:35b" in text and "Granola's weaker summary" not in text
    assert "## Transcript" in text

    cfg.keep_granola_notes = True
    store.requeue("m1")
    p.run_pending()
    assert "Granola's weaker summary" in open(store.get("m1")["md_path"], encoding="utf-8").read()


def test_pipeline_falls_back_to_granola_when_model_fails(tmp_path):
    cfg = cfg_for(tmp_path, ollama_enabled=True, classes=[])

    def boom(m, cfg):
        raise RuntimeError("model 'big:35b' not found")

    store = Store(cfg.db_path, cfg.pool_dir)
    p = Pipeline(cfg, store, summarize=boom, log=lambda *_: None)
    store.enqueue(Meeting(id="m2", title="Lecture", notes_markdown="Granola notes", transcript=LINE * 40))
    p.run_pending()
    row = store.get("m2")
    assert row["status"] == "done" and row["class_name"] == UNSORTED and row["summary_md"] is None
    assert "not found" in row["error"] and "Granola notes" in open(row["md_path"], encoding="utf-8").read()


def test_pipeline_marks_failed_instead_of_looping(tmp_path):
    cfg = cfg_for(tmp_path, ollama_enabled=False)
    store = Store(cfg.db_path, cfg.pool_dir)
    p = Pipeline(cfg, store, log=lambda *_: None)
    store.enqueue(Meeting(id="m3", title="Lecture"))
    store.conn.execute("UPDATE notes SET payload_json='not json' WHERE id='m3'")
    assert p.run_pending() == 0
    assert store.get("m3")["status"] == "failed" and [r["id"] for r in store.processing()] == ["m3"]


def test_restart_resumes_interrupted_work(tmp_path):
    cfg = cfg_for(tmp_path, ollama_enabled=False)
    store = Store(cfg.db_path, cfg.pool_dir)
    store.enqueue(Meeting(id="m4", title="Lecture"))
    assert store.claim_next()["id"] == "m4" and store.claim_next() is None
    assert store.reset_working() == 1 and store.get("m4")["status"] == "queued"


def test_database_from_0_1_is_upgraded_and_readable(tmp_path):
    """Pools created by 0.1 have no payload column: they must open, list, and re-process."""
    cfg = cfg_for(tmp_path, ollama_enabled=False)
    cfg.home.mkdir(parents=True)
    (cfg.pool_dir / "Bio 110").mkdir(parents=True)
    md = cfg.pool_dir / "Bio 110" / "2026-09-01 Cells.md"
    md.write_text(encoding="utf-8", data="---\ntitle: \"Cells\"\n---\n\n# Cells\n\n## Notes\n\nmembranes\n\n## Transcript\n\nosmosis talk\n")
    old = sqlite3.connect(cfg.db_path)
    old.executescript("""CREATE TABLE notes (id TEXT PRIMARY KEY, title TEXT, date TEXT, owner TEXT, attendees TEXT,
        folder TEXT, class_name TEXT, confidence REAL, classified_by TEXT, lecture_title TEXT, topics TEXT,
        md_path TEXT, has_transcript INTEGER DEFAULT 0, raw_json TEXT, first_seen TEXT, updated_at TEXT);
        CREATE TABLE sync_state (key TEXT PRIMARY KEY, value TEXT);""")
    old.execute("INSERT INTO notes VALUES ('n1','Cells','2026-09-01','Sam','[]','','Bio 110',1,'human','Cells','[]',?,1,"
                "'{\"id\": \"n1\", \"summary\": \"membranes\"}','2026-09-01','2026-09-01')", (str(md),))
    old.commit()
    old.close()

    store = Store(cfg.db_path, cfg.pool_dir)
    assert [r["id"] for r in store.list_notes()] == ["n1"]
    m = store.meeting(store.get("n1"))
    assert m.owner == "Sam" and m.notes_markdown == "membranes" and m.transcript == "osmosis talk"
    assert store.requeue_all() == 1
    Pipeline(cfg, store, log=lambda *_: None).run_pending()
    row = store.get("n1")
    assert row["status"] == "done" and row["payload_json"] and "osmosis talk" in open(row["md_path"], encoding="utf-8").read()
    assert row["class_name"] == "Bio 110" and row["classified_by"] == "human"  # a person's filing survives


def test_rules_pick_the_class_but_the_model_still_names_the_lecture(tmp_path):
    cfg = cfg_for(tmp_path)
    store = Store(cfg.db_path, cfg.pool_dir)
    chat = lambda cfg, prompt, schema: json.dumps({"class_name": "Bio 110", "confidence": 0.9,
                                                   "lecture_title": "Limits and continuity", "topics": ["limits"]})
    p = Pipeline(cfg, store, chat=chat, summarize=lambda m, cfg: "", log=lambda *_: None)
    store.enqueue(Meeting(id="r1", title="Math 151 lecture 2", notes_markdown="limits"))
    p.run_pending()
    row = store.get("r1")
    assert row["class_name"] == "Calc 1" and row["classified_by"] == "rules"  # the rule still decides the class
    assert row["lecture_title"] == "Limits and continuity" and json.loads(row["topics"]) == ["limits"]


def _racing_pipeline(tmp_path, during):
    """A pipeline whose summarizer runs `during(store)` mid-summary, like a person clicking in the web UI."""
    cfg = cfg_for(tmp_path, ollama_enabled=True, classes=[ClassDef("Calc 1"), ClassDef("Bio 110")])
    store = Store(cfg.db_path, cfg.pool_dir)

    def summarize(m, cfg):
        during(store)
        return "## Overview\nnew summary"

    sort = lambda cfg, prompt, schema: json.dumps({"class_name": "Calc 1", "confidence": 0.9, "lecture_title": "t", "topics": []})
    store.enqueue(Meeting(id="r", title="Lecture", notes_markdown="old", transcript=LINE * 40))
    return store, Pipeline(cfg, store, chat=sort, summarize=summarize, log=lambda *_: None)


def test_delete_while_summarizing_is_not_undone(tmp_path):
    store, p = _racing_pipeline(tmp_path, lambda st: st.delete("r"))
    p.run_pending()
    assert store.get("r") is None and not list((tmp_path / "pool").rglob("*.md"))


def test_move_while_summarizing_keeps_the_persons_class_and_the_summary(tmp_path):
    store, p = _racing_pipeline(tmp_path, lambda st: st.set_class("r", "Bio 110"))
    p.run_pending()
    row = store.get("r")
    assert row["class_name"] == "Bio 110" and row["classified_by"] == "human"
    assert row["summary_md"] == "## Overview\nnew summary" and row["status"] == "done"


def test_reshare_while_summarizing_runs_again_with_the_new_copy(tmp_path):
    shared = []

    def reshare(st):
        if not shared:  # the lecture is edited and sent again, mid-summary
            shared.append(1)
            st.enqueue(Meeting(id="r", title="Lecture (edited)", notes_markdown="newer", transcript=LINE * 40))

    store, p = _racing_pipeline(tmp_path, reshare)
    assert p.run_pending() == 2  # the stale result is dropped, the new copy is processed
    row = store.get("r")
    assert row["title"] == "Lecture (edited)" and row["status"] == "done"


def test_notes_are_capped_and_a_runaway_model_is_caught(monkeypatch):
    """A small model once looped for 39,000 tokens on a short transcript (Ollama shifts its context and
    keeps going). Notes are capped at MAX_NOTES_TOKENS, and hitting the cap or repeating lines fails
    the summary, so the lecture keeps Granola's notes instead of the loop."""
    import httpx

    from granola_share import summarize

    sent = {}

    def post(url, json, timeout):
        sent.update(json)
        reply = {"message": {"content": "## Overview\nfine"}, "done_reason": sent.get("_reason", "stop")}
        return SimpleNamespace(raise_for_status=lambda: None, json=lambda: reply)

    monkeypatch.setattr(httpx, "post", post)
    cfg = Config(home=Path("."), pool_dir=Path("."))
    assert summarize.ollama_generate(cfg, "llama3.2:3b", "notes please", 32768) == "## Overview\nfine"
    assert sent["options"]["num_predict"] == summarize.MAX_NOTES_TOKENS and sent["options"]["repeat_penalty"] > 1

    def runaway(url, json, timeout):
        return SimpleNamespace(raise_for_status=lambda: None,
                               json=lambda: {"message": {"content": "x"}, "done_reason": "length"})

    monkeypatch.setattr(httpx, "post", runaway)
    with pytest.raises(summarize.RunawayOutput, match="kept writing"):
        summarize.ollama_generate(cfg, "llama3.2:3b", "notes please", 32768)
    loop = "## Key concepts\n" + "- **Osmosis** is water moving across a membrane.\n" * 8
    with pytest.raises(summarize.RunawayOutput, match="repeated itself"):
        summarize.clean_output(loop)
    assert summarize.clean_output("## Overview\nOne.\n\n## Key concepts\n- **A** is a.\n- **B** is b.").startswith("## Overview")
    assert not summarize.wants_summary(Meeting(id="1", transcript="Short lecture. " * 50), cfg)  # 750 chars
