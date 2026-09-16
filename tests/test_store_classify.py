import json
from pathlib import Path

from granola_share.classify import classify, classify_by_rules, classify_with_ollama
from granola_share.config import UNSORTED, ClassDef, Config
from granola_share.granola import Meeting
from granola_share.store import Classification, Store, render_markdown, slugify


def cfg_for(tmp_path: Path, **kw) -> Config:
    c = Config(home=tmp_path / "home", pool_dir=tmp_path / "pool", classes=[
        ClassDef("CS 101", ["cs101", "intro programming"], "Intro to programming"),
        ClassDef("Bio 110", ["bio110", "biology"]),
    ])
    for k, v in kw.items():
        setattr(c, k, v)
    return c


def test_slugify_strips_bad_chars():
    assert slugify('Lec 3: "Loops" / while?') == "Lec 3 Loops while"


def test_rules_folder_then_title(tmp_path):
    cfg = cfg_for(tmp_path)
    m = Meeting(id="1", title="Random meeting", folder="Biology")
    c = classify_by_rules(m, cfg.classes)
    assert c and c.class_name == "Bio 110" and c.by == "folder"
    m2 = Meeting(id="2", title="CS101 lecture 4 - recursion")
    c2 = classify_by_rules(m2, cfg.classes)
    assert c2 and c2.class_name == "CS 101" and c2.by == "rules"
    assert classify_by_rules(Meeting(id="3", title="Dentist"), cfg.classes) is None


def test_ollama_path_with_fake_chat(tmp_path):
    cfg = cfg_for(tmp_path)
    m = Meeting(id="1", title="Lecture", notes_markdown="Today we covered mitosis and the cell cycle.")

    def fake_chat(cfg, prompt, schema):
        assert "Bio 110" in prompt and "mitosis" in prompt
        assert schema["properties"]["class_name"]["enum"][-1] == UNSORTED
        return json.dumps({"class_name": "Bio 110", "confidence": 0.9, "lecture_title": "Mitosis", "topics": ["mitosis", "cell cycle"]})

    c = classify(m, cfg, chat=fake_chat)
    assert c.class_name == "Bio 110" and c.by == "ollama" and c.lecture_title == "Mitosis"

    low = lambda cfg, p, s: json.dumps({"class_name": "Bio 110", "confidence": 0.2, "lecture_title": "x", "topics": []})
    assert classify(m, cfg, chat=low).class_name == UNSORTED

    bad = lambda cfg, p, s: "not json"
    assert classify_with_ollama(m, cfg.classes, cfg, chat=bad) is None
    assert classify(m, cfg, chat=bad).by == "none"


def test_store_save_move_and_listing(tmp_path):
    cfg = cfg_for(tmp_path)
    store = Store(cfg.db_path, cfg.pool_dir)
    m = Meeting(id="abc123", title="Lec 1: Intro", date="2026-09-10T09:00:00Z", owner="Joey",
                notes_markdown="# Hello\nworld", raw={"id": "abc123", "title": "Lec 1: Intro", "date": "2026-09-10T09:00:00Z", "notes": "# Hello\nworld"})
    path = store.save(m, Classification("CS 101", 0.9, "ollama", "Intro", ["hello"]))
    assert path.exists() and path.parent.name == "CS 101" and path.name == "2026-09-10 Lec 1 Intro.md"
    text = path.read_text()
    assert text.startswith("---") and "## Notes" in text and "world" in text
    assert store.known_ids() == {"abc123"}
    assert store.classes_summary() == [("CS 101", 1)]

    # idempotent re-save keeps one file
    store.save(m, Classification("CS 101", 0.9, "ollama", "Intro", ["hello"]))
    assert len(list((cfg.pool_dir / "CS 101").iterdir())) == 1

    # human move relocates the file and empties the old dir
    new_path = store.set_class("abc123", "Bio 110")
    assert new_path and new_path.parent.name == "Bio 110" and not path.exists()
    assert not (cfg.pool_dir / "CS 101").exists()
    row = store.get("abc123")
    assert row["class_name"] == "Bio 110" and row["classified_by"] == "human"

    # state
    assert store.get_state("last_sync") is None
    store.set_state("last_sync", "2026-09-15T00:00:00+00:00")
    assert store.get_state("last_sync") == "2026-09-15T00:00:00+00:00"


def test_render_markdown_includes_transcript_and_private(tmp_path):
    m = Meeting(id="1", title="T", date="2026-01-01", notes_markdown="n", private_notes="p", transcript="t")
    out = render_markdown(m, Classification("CS 101", 1.0, "folder"))
    assert "## Private notes" in out and "## Transcript" in out


def test_store_summaries_for_the_status_page(tmp_path):
    cfg = cfg_for(tmp_path)
    store = Store(cfg.db_path, cfg.pool_dir)
    assert store.count() == 0 and store.class_counts() == {} and store.owners_summary() == []
    assert store.recent() == [] and store.latest_date("CS 101") is None

    notes = [("a", "Loops", "2026-09-10", "Sam", "CS 101"),
             ("b", "Recursion", "2026-09-12", "Sam", "CS 101"),
             ("c", "Cells", "2026-09-11", "Ada", UNSORTED)]
    for nid, title, date_, owner, cls in notes:
        store.save(Meeting(id=nid, title=title, date=date_, owner=owner, notes_markdown="x"),
                   Classification(cls, 0.9, "ollama", title, []))

    assert store.count() == 3
    assert store.class_counts() == {"CS 101": 2, UNSORTED: 1}
    assert store.latest_date("CS 101") == "2026-09-12" and store.latest_date("Bio 110") is None
    assert store.owners_summary() == [("Sam", 2, "2026-09-12"), ("Ada", 1, "2026-09-11")]

    # recent() is arrival order (first_seen), newest first, and respects the limit; the three saves
    # above land in the same second, so spell the arrival times out to keep this deterministic.
    for i, nid in enumerate(["a", "b", "c"]):
        store.conn.execute("UPDATE notes SET first_seen=? WHERE id=?", (f"2026-09-14T10:00:0{i}", nid))
    store.conn.commit()
    assert [r["id"] for r in store.recent()] == ["c", "b", "a"]
    assert [r["id"] for r in store.recent(2)] == ["c", "b"]


def test_store_owners_summary_counts_notes_without_an_owner(tmp_path):
    cfg = cfg_for(tmp_path)
    store = Store(cfg.db_path, cfg.pool_dir)
    store.save(Meeting(id="a", title="Anon", date="2026-09-10", notes_markdown="x"),
               Classification(UNSORTED, 0.0, "none"))
    assert store.owners_summary() == [("", 1, "2026-09-10")]
