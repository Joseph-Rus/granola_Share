"""The pool API: /api/health, /api/preview (guess without saving) and /api/ingest (file it)."""

import json
from datetime import datetime, timezone

from fastapi.testclient import TestClient

from granola_share import __version__
from granola_share.config import UNSORTED, ClassDef, Config
from granola_share.store import Store
from granola_share.web import create_app

NOW = datetime(2026, 9, 15, 12, 0, 0, tzinfo=timezone.utc)
NOW_ISO = "2026-09-15T12:00:00+00:00"

KEY = {"Authorization": "Bearer pw"}

# A note no folder/title rule can place, so the model (our fake chat) has to decide.
MODEL_NOTE = {"id": "n7", "title": "Lecture 7", "date": "2026-09-14T10:00:00Z", "owner": "Sam",
              "attendees": ["Sam"], "folder": "", "notes_markdown": "Today: mitosis and the cell cycle.",
              "private_notes": "", "transcript": "", "raw": {"id": "n7"}}


def make(tmp_path, password="pw", ollama=False, chat=None):
    cfg = Config(home=tmp_path / "home", pool_dir=tmp_path / "pool", pool_name="Fall pool", pool_password=password,
                 ollama_enabled=ollama, classes=[ClassDef("CS 101", ["cs101"]), ClassDef("Bio 110", ["biology"])])
    store = Store(cfg.db_path, cfg.pool_dir)
    app = create_app(cfg, store, chat=chat, now=lambda: NOW)
    return cfg, store, TestClient(app)


def counting_chat(class_name="Bio 110", confidence=0.9):
    """A fake Ollama that records every prompt it is asked, so we can prove caching works."""
    prompts: list[str] = []

    def chat(cfg, prompt, schema):
        prompts.append(prompt)
        return json.dumps({"class_name": class_name, "confidence": confidence,
                           "lecture_title": "Mitosis", "topics": ["mitosis", "cell cycle"]})

    return chat, prompts


def md_files(cfg):
    return sorted(p.name for p in cfg.pool_dir.rglob("*.md"))


def test_health_requires_key_and_reports_pool(tmp_path):
    cfg, store, c = make(tmp_path)
    assert c.get("/api/health").status_code == 401
    assert c.get("/api/health", headers={"Authorization": "Bearer nope"}).status_code == 401
    r = c.get("/api/health", headers=KEY)
    assert r.status_code == 200
    body = r.json()
    assert body["ok"] is True and body["pool_name"] == "Fall pool"
    assert body["classes"] == ["CS 101", "Bio 110"] and body["notes"] == 0
    assert body["version"] == __version__ and body["server_time"] == NOW_ISO
    assert body["last_ingest"] is None


def test_ingest_classifies_and_files(tmp_path):
    cfg, store, c = make(tmp_path)
    payload = {"id": "not_abc", "title": "CS101 lecture 2", "date": "2026-09-14T10:00:00Z", "owner": "Sam",
               "attendees": ["Sam"], "folder": "", "notes_markdown": "# Loops\nfor and while", "private_notes": "",
               "transcript": "", "raw": {"id": "not_abc"}}
    r = c.post("/api/ingest", json=payload, headers=KEY)
    assert r.status_code == 200, r.text
    body = r.json()
    assert body["class_name"] == "CS 101" and body["classified_by"] == "rules" and body["id"] == "not_abc"
    assert body["url"] == "/note/not_abc"
    assert (cfg.pool_dir / "CS 101" / body["file"]).exists()
    assert store.get("not_abc")["owner"] == "Sam"
    # idempotent
    r2 = c.post("/api/ingest", json=payload, headers=KEY)
    assert r2.status_code == 200 and len(store.list_notes()) == 1
    # unknown goes to Unsorted (ollama disabled)
    r3 = c.post("/api/ingest", json={"id": "x2", "title": "Lunch"}, headers=KEY)
    assert r3.json()["class_name"] == UNSORTED


def test_ingest_records_the_last_ingest(tmp_path):
    cfg, store, c = make(tmp_path)
    c.post("/api/ingest", json=MODEL_NOTE, headers=KEY)
    assert store.get_state("last_ingest") == NOW_ISO
    assert store.get_state("last_ingest_owner") == "Sam"
    health = c.get("/api/health", headers=KEY).json()
    assert health["last_ingest"] == NOW_ISO and health["notes"] == 1


def test_ingest_rejects_bad_key_and_bad_payload(tmp_path):
    cfg, store, c = make(tmp_path)
    assert c.post("/api/ingest", json={"id": "a"}).status_code == 401
    assert c.post("/api/ingest", json={"title": "no id"}, headers=KEY).status_code == 400
    assert c.post("/api/ingest", json=["not", "an", "object"], headers=KEY).status_code == 400


def test_open_pool_without_password(tmp_path):
    cfg, store, c = make(tmp_path, password="")
    assert c.get("/api/health").status_code == 200


# --- preview -------------------------------------------------------------------

def test_preview_guesses_without_saving(tmp_path):
    chat, prompts = counting_chat()
    cfg, store, c = make(tmp_path, ollama=True, chat=chat)
    assert c.post("/api/preview", json=MODEL_NOTE).status_code == 401

    r = c.post("/api/preview", json=MODEL_NOTE, headers=KEY)
    assert r.status_code == 200, r.text
    body = r.json()
    assert body == {"id": "n7", "class_name": "Bio 110", "confidence": 0.9, "classified_by": "ollama",
                    "lecture_title": "Mitosis", "topics": ["mitosis", "cell cycle"]}
    assert store.count() == 0 and md_files(cfg) == []  # nothing filed, nothing indexed
    assert len(prompts) == 1

    assert c.post("/api/preview", json={"title": "no id"}, headers=KEY).status_code == 400


def test_preview_then_ingest_classifies_only_once(tmp_path):
    chat, prompts = counting_chat()
    cfg, store, c = make(tmp_path, ollama=True, chat=chat)
    guess = c.post("/api/preview", json=MODEL_NOTE, headers=KEY).json()
    filed = c.post("/api/ingest", json=MODEL_NOTE, headers=KEY).json()
    assert len(prompts) == 1, "the ingest should reuse the preview's answer, not ask Ollama again"
    assert filed["class_name"] == guess["class_name"] and filed["topics"] == guess["topics"]
    assert store.get("n7")["class_name"] == "Bio 110"

    # edit the note and the cache key changes, so it is classified again
    edited = MODEL_NOTE | {"notes_markdown": "Today: meiosis instead."}
    c.post("/api/ingest", json=edited, headers=KEY)
    assert len(prompts) == 2


def test_class_name_override_files_as_human(tmp_path):
    chat, prompts = counting_chat()
    cfg, store, c = make(tmp_path, ollama=True, chat=chat)
    c.post("/api/preview", json=MODEL_NOTE, headers=KEY)

    body = c.post("/api/ingest", json=MODEL_NOTE | {"class_name": "CS 101"}, headers=KEY).json()
    assert body["class_name"] == "CS 101" and body["classified_by"] == "human" and body["confidence"] == 1.0
    assert body["lecture_title"] == "Mitosis" and body["topics"] == ["mitosis", "cell cycle"]  # kept from the preview
    assert len(prompts) == 1
    row = store.get("n7")
    assert row["class_name"] == "CS 101" and row["classified_by"] == "human"
    assert (cfg.pool_dir / "CS 101").is_dir()


def test_class_name_override_without_a_preview_uses_the_note_title(tmp_path):
    cfg, store, c = make(tmp_path)
    body = c.post("/api/ingest", json=MODEL_NOTE | {"class_name": UNSORTED}, headers=KEY).json()
    assert body["class_name"] == UNSORTED and body["classified_by"] == "human"
    assert body["lecture_title"] == "Lecture 7" and body["topics"] == []


def test_unknown_class_override_is_rejected(tmp_path):
    cfg, store, c = make(tmp_path)
    r = c.post("/api/ingest", json=MODEL_NOTE | {"class_name": "Astro 900"}, headers=KEY)
    assert r.status_code == 400 and "unknown class" in r.text
    assert store.count() == 0
