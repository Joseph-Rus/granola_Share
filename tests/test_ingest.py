from fastapi.testclient import TestClient

from granola_share import __version__
from granola_share.config import UNSORTED, ClassDef, Config
from granola_share.pipeline import Pipeline
from granola_share.store import Store
from granola_share.web import create_app


def make(tmp_path, password="pw"):
    cfg = Config(home=tmp_path / "home", pool_dir=tmp_path / "pool", pool_name="Fall pool", pool_password=password,
                 ollama_enabled=False, classes=[ClassDef("CS 101", ["cs101"]), ClassDef("Bio 110", ["biology"])])
    store = Store(cfg.db_path, cfg.pool_dir)
    pipeline = Pipeline(cfg, store, log=lambda *_: None)
    app = create_app(cfg, store, pipeline, list_models=lambda h: [], tailscale=lambda: {}, latest=lambda *a: None)
    return cfg, store, pipeline, TestClient(app)


def test_health_requires_key_and_reports_pool(tmp_path):
    cfg, store, pipeline, c = make(tmp_path)
    assert c.get("/api/health").status_code == 401
    assert c.get("/api/health", headers={"Authorization": "Bearer nope"}).status_code == 401
    r = c.get("/api/health", headers={"Authorization": "Bearer pw"})
    assert r.status_code == 200
    assert r.json() == {"ok": True, "pool_name": "Fall pool", "classes": ["CS 101", "Bio 110"], "notes": 0,
                        "version": __version__}
    assert r.headers["x-granola-share"] == __version__


def test_ingest_queues_then_pipeline_files(tmp_path):
    cfg, store, pipeline, c = make(tmp_path)
    payload = {"id": "not_abc", "title": "CS101 lecture 2", "date": "2026-09-14T10:00:00Z", "owner": "Sam",
               "attendees": ["Sam"], "folder": "", "notes_markdown": "# Loops\nfor and while", "private_notes": "",
               "transcript": "", "raw": {"id": "not_abc"}}
    r = c.post("/api/ingest", json=payload, headers={"Authorization": "Bearer pw"})
    assert r.status_code == 200, r.text
    body = r.json()
    assert body == {"id": "not_abc", "status": "queued", "class_name": "CS 101", "has_transcript": False}
    assert store.get("not_abc")["status"] == "queued" and store.list_notes() == []

    assert pipeline.run_pending() == 1
    row = store.get("not_abc")
    assert row["status"] == "done" and row["class_name"] == "CS 101" and row["owner"] == "Sam"
    assert (cfg.pool_dir / "CS 101").is_dir() and len(store.list_notes()) == 1

    # sharing again re-queues the same note instead of duplicating it
    c.post("/api/ingest", json=payload, headers={"Authorization": "Bearer pw"})
    pipeline.run_pending()
    assert len(store.list_notes()) == 1 and len(list((cfg.pool_dir / "CS 101").iterdir())) == 1

    # unknown goes to Unsorted (AI off)
    r3 = c.post("/api/ingest", json={"id": "x2", "title": "Lunch"}, headers={"Authorization": "Bearer pw"})
    assert r3.json()["class_name"] is None
    pipeline.run_pending()
    assert store.get("x2")["class_name"] == UNSORTED


def test_ingest_rejects_bad_key_and_bad_payload(tmp_path):
    cfg, store, pipeline, c = make(tmp_path)
    assert c.post("/api/ingest", json={"id": "a"}).status_code == 401
    assert c.post("/api/ingest", json={"title": "no id"}, headers={"Authorization": "Bearer pw"}).status_code == 400


def test_open_pool_without_password(tmp_path):
    cfg, store, pipeline, c = make(tmp_path, password="")
    assert c.get("/api/health").status_code == 200
