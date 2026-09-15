from fastapi.testclient import TestClient

from granola_share.config import UNSORTED, ClassDef, Config
from granola_share.store import Store
from granola_share.web import create_app


def make(tmp_path, password="pw"):
    cfg = Config(home=tmp_path / "home", pool_dir=tmp_path / "pool", pool_name="Fall pool", pool_password=password,
                 ollama_enabled=False, classes=[ClassDef("CS 101", ["cs101"]), ClassDef("Bio 110", ["biology"])])
    store = Store(cfg.db_path, cfg.pool_dir)
    return cfg, store, TestClient(create_app(cfg, store))


def test_health_requires_key_and_reports_pool(tmp_path):
    cfg, store, c = make(tmp_path)
    assert c.get("/api/health").status_code == 401
    assert c.get("/api/health", headers={"Authorization": "Bearer nope"}).status_code == 401
    r = c.get("/api/health", headers={"Authorization": "Bearer pw"})
    assert r.status_code == 200
    assert r.json() == {"ok": True, "pool_name": "Fall pool", "classes": ["CS 101", "Bio 110"], "notes": 0}


def test_ingest_classifies_and_files(tmp_path):
    cfg, store, c = make(tmp_path)
    payload = {"id": "not_abc", "title": "CS101 lecture 2", "date": "2026-09-14T10:00:00Z", "owner": "Sam",
               "attendees": ["Sam"], "folder": "", "notes_markdown": "# Loops\nfor and while", "private_notes": "",
               "transcript": "", "raw": {"id": "not_abc"}}
    r = c.post("/api/ingest", json=payload, headers={"Authorization": "Bearer pw"})
    assert r.status_code == 200, r.text
    body = r.json()
    assert body["class_name"] == "CS 101" and body["classified_by"] == "rules" and body["id"] == "not_abc"
    assert (cfg.pool_dir / "CS 101" / body["file"]).exists()
    assert store.get("not_abc")["owner"] == "Sam"
    # idempotent
    r2 = c.post("/api/ingest", json=payload, headers={"Authorization": "Bearer pw"})
    assert r2.status_code == 200 and len(store.list_notes()) == 1
    # unknown goes to Unsorted (ollama disabled)
    r3 = c.post("/api/ingest", json={"id": "x2", "title": "Lunch"}, headers={"Authorization": "Bearer pw"})
    assert r3.json()["class_name"] == UNSORTED


def test_ingest_rejects_bad_key_and_bad_payload(tmp_path):
    cfg, store, c = make(tmp_path)
    assert c.post("/api/ingest", json={"id": "a"}).status_code == 401
    assert c.post("/api/ingest", json={"title": "no id"}, headers={"Authorization": "Bearer pw"}).status_code == 400


def test_open_pool_without_password(tmp_path):
    cfg, store, c = make(tmp_path, password="")
    assert c.get("/api/health").status_code == 200
