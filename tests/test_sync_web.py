"""The server's own Granola sync and the pool web UI (browsing + the /status page)."""

import asyncio
import json
import threading
from contextlib import asynccontextmanager

from fastapi.testclient import TestClient

from granola_share.config import UNSORTED, ClassDef, Config
from granola_share.granola import Meeting
from granola_share.store import Classification, Store
from granola_share.sync import account_label, record_sync_error, run_loop, sync_once
from granola_share.ui import esc
from granola_share.web import NO_NOTES_WARNING, create_app

ACCOUNT = {"email": "joey@example.com", "workspace": "Joey's workspace", "workspace_id": "ws-77",
           "scopes": ["personal"], "raw": {}}

OLLAMA_OFF = {"enabled": False, "ok": None, "host": "http://localhost:11434", "model": "m",
              "installed": None, "models": [], "error": None}


class FakeClient:
    """Stands in for GranolaClient: same method shapes, canned data. No get_account_info (a v0.1 client)."""

    def __init__(self, stubs, full, transcript=""):
        self.stubs, self.full, self.transcript = stubs, full, transcript
        self.calls = []

    @asynccontextmanager
    async def session(self):
        yield "session"

    async def list_meetings(self, session, since=None, limit=50):
        self.calls.append(("list", since))
        return self.stubs

    async def get_meetings(self, session, ids):
        self.calls.append(("get", ids))
        return [m for m in self.full if m.id in ids]

    async def get_transcript(self, session, mid):
        return self.transcript


class FakeClientWithAccount(FakeClient):
    """The v0.2 client: it can also say which Granola account it is signed in as."""

    def __init__(self, *a, account=ACCOUNT, **kw):
        super().__init__(*a, **kw)
        self.account = account

    async def get_account_info(self, session):
        self.calls.append(("account", None))
        return self.account


def make_cfg(tmp_path, password="", **kw):
    cfg = Config(home=tmp_path / "home", pool_dir=tmp_path / "pool", pool_password=password, ollama_enabled=False,
                 classes=[ClassDef("CS 101", ["cs101"]), ClassDef("Bio 110", ["biology"])])
    for k, v in kw.items():
        setattr(cfg, k, v)
    return cfg


def two_notes():
    stubs = [Meeting(id="a", title="CS101 lec 1", date="2026-09-10"), Meeting(id="b", title="Lunch", date="2026-09-11")]
    full = [Meeting(id="a", title="CS101 lec 1", date="2026-09-10", notes_markdown="loops", raw={"id": "a"}),
            Meeting(id="b", title="Lunch", date="2026-09-11", notes_markdown="tacos", raw={"id": "b"})]
    return stubs, full


def app_for(cfg, store, **kw):
    kw.setdefault("ollama_check", lambda: dict(OLLAMA_OFF))
    return TestClient(create_app(cfg, store, **kw))


# --- sync ------------------------------------------------------------------------

def test_sync_once_saves_new_and_skips_known(tmp_path):
    cfg = make_cfg(tmp_path)
    store = Store(cfg.db_path, cfg.pool_dir)
    stubs, full = two_notes()
    client = FakeClient(stubs, full, transcript="hello transcript")
    rep = asyncio.run(sync_once(cfg, client, store, log=lambda *_: None))
    assert rep.listed == 2 and rep.new == 2 and len(rep.saved) == 2 and not rep.errors
    assert dict(rep.saved) == {"CS101 lec 1": "CS 101", "Lunch": UNSORTED}
    assert store.get("a")["has_transcript"] == 1
    assert (cfg.home / "debug" / "last_meetings.json").exists()
    assert store.get_state("last_sync")

    rep2 = asyncio.run(sync_once(cfg, client, store, log=lambda *_: None))
    assert rep2.new == 0 and client.calls[-1][0] == "list"
    assert client.calls[-1][1] is not None  # since date derived from last_sync


def test_sync_falls_back_to_stub_when_get_fails(tmp_path):
    cfg = make_cfg(tmp_path)
    store = Store(cfg.db_path, cfg.pool_dir)

    class Broken(FakeClient):
        async def get_meetings(self, session, ids):
            raise RuntimeError("boom")

    client = Broken([Meeting(id="z", title="biology lab", date="2026-09-12", notes_markdown="stub notes")], [])
    rep = asyncio.run(sync_once(cfg, client, store, log=lambda *_: None))
    assert rep.errors and len(rep.saved) == 1
    assert "stub notes" in open(store.get("z")["md_path"]).read()


def test_sync_records_who_is_signed_in_and_how_many_notes(tmp_path):
    cfg = make_cfg(tmp_path)
    store = Store(cfg.db_path, cfg.pool_dir)
    stubs, full = two_notes()
    lines = []
    rep = asyncio.run(sync_once(cfg, FakeClientWithAccount(stubs, full), store, log=lines.append))
    assert rep.account == ACCOUNT
    assert json.loads(store.get_state("sync_account"))["email"] == "joey@example.com"
    assert store.get_state("sync_listed") == "2"
    assert store.get_state("sync_checked_at") and store.get_state("sync_error") == ""
    assert any("signed in as joey@example.com · Joey's workspace" in ln and "2 notes since" in ln for ln in lines)


def test_sync_records_an_empty_account_and_a_client_without_account_info(tmp_path):
    cfg = make_cfg(tmp_path)
    store = Store(cfg.db_path, cfg.pool_dir)
    rep = asyncio.run(sync_once(cfg, FakeClientWithAccount([], [], account=None), store, log=lambda *_: None))
    assert rep.account is None and store.get_state("sync_account") == "{}" and store.get_state("sync_listed") == "0"

    store2 = Store(cfg.home / "other.db", cfg.pool_dir)
    stubs, full = two_notes()
    rep2 = asyncio.run(sync_once(cfg, FakeClient(stubs, full), store2, log=lambda *_: None))  # v0.1 client
    assert rep2.account is None and rep2.new == 2
    assert store2.get_state("sync_account") == "{}" and store2.get_state("sync_listed") == "2"


def test_sync_records_the_error_when_the_poll_blows_up(tmp_path):
    cfg = make_cfg(tmp_path, poll_interval_seconds=1)
    store = Store(cfg.db_path, cfg.pool_dir)

    class Dead(FakeClient):
        async def list_meetings(self, session, since=None, limit=50):
            raise RuntimeError("not logged in")

    client = Dead([], [])
    try:
        asyncio.run(sync_once(cfg, client, store, log=lambda *_: None))
    except RuntimeError:
        pass
    else:
        raise AssertionError("sync_once should re-raise")
    assert store.get_state("sync_error") == "RuntimeError: not logged in"
    assert store.get_state("sync_checked_at")

    # run_loop records it too, and then honours stop
    store.set_state("sync_error", "")
    stop = threading.Event()
    stop.set()
    run_loop(cfg, client, store, log=lambda *_: None, stop=stop)
    assert "not logged in" in store.get_state("sync_error")


def test_account_label_and_record_sync_error(tmp_path):
    assert account_label(ACCOUNT) == "joey@example.com · Joey's workspace"
    assert account_label({"email": "a@b.c"}) == "a@b.c" and account_label(None) == "not signed in"
    cfg = make_cfg(tmp_path)
    store = Store(cfg.db_path, cfg.pool_dir)
    record_sync_error(store, "x" * 3000)
    assert len(store.get_state("sync_error")) == 2000


# --- web UI ----------------------------------------------------------------------

def test_web_login_browse_move_download(tmp_path):
    cfg = make_cfg(tmp_path, password="pw")
    store = Store(cfg.db_path, cfg.pool_dir)
    m = Meeting(id="n1", title="Cells", date="2026-09-01", owner="Sam", notes_markdown="# Cells\n\nmembranes",
                raw={"id": "n1", "title": "Cells", "date": "2026-09-01", "notes": "# Cells\n\nmembranes"})
    store.save(m, Classification(UNSORTED, 0.3, "ollama", "Cells", ["cells"]))

    app = create_app(cfg, store, ollama_check=lambda: dict(OLLAMA_OFF))
    c = TestClient(app, follow_redirects=False)
    assert c.get("/").status_code == 307  # not logged in
    assert c.post("/login", data={"password": "nope"}).status_code == 200
    r = c.post("/login", data={"password": "pw"})
    assert r.status_code == 303 and "pool" in r.cookies

    assert "Unsorted" in c.get("/").text
    assert "Cells" in c.get("/unsorted").text
    page = c.get("/note/n1").text
    assert "membranes" in page and "<select" in page
    r = c.post("/note/n1/class", data={"class_name": "Bio 110"})
    assert r.status_code == 303
    assert store.get("n1")["class_name"] == "Bio 110"
    dl = c.get("/note/n1/download")
    assert dl.status_code == 200 and "membranes" in dl.text
    z = c.get("/class/Bio 110/zip")
    assert z.status_code == 200 and z.headers["content-type"].startswith("application/zip")
    api = c.get("/api/notes", params={"class_name": "Bio 110"}).json()
    assert api[0]["id"] == "n1" and api[0]["topics"] == ["cells"]
    assert c.post("/note/n1/class", data={"class_name": "Nope"}).status_code == 400
    assert c.get("/class/Nope").status_code == 404


def test_web_no_password_means_open(tmp_path):
    cfg = make_cfg(tmp_path)
    store = Store(cfg.db_path, cfg.pool_dir)
    c = app_for(cfg, store)
    assert c.get("/").status_code == 200


def test_index_shows_the_pool_at_a_glance(tmp_path):
    cfg = make_cfg(tmp_path, pool_name="Fall pool")
    store = Store(cfg.db_path, cfg.pool_dir)
    store.save(Meeting(id="a", title="Loops", date="2026-09-10", owner="Sam", notes_markdown="x"),
               Classification("CS 101", 0.9, "ollama", "Loops", ["loops"]))
    store.save(Meeting(id="b", title="Lunch", date="2026-09-11", owner="Ada", notes_markdown="y"),
               Classification(UNSORTED, 0.1, "none"))
    c = app_for(cfg, store)
    body = c.get("/").text
    assert "Fall pool" in body and "CS 101" in body and "Bio 110" in body
    assert "Notes" in body and "Contributors" in body
    assert "waiting in Unsorted" in body      # the Unsorted callout
    assert "Loops" in body and "Lunch" in body  # the Recent list
    assert "/class/CS%20101/zip" in body


def test_status_page_and_api(tmp_path):
    cfg = make_cfg(tmp_path, pool_name="Fall pool", server_sync=True)
    store = Store(cfg.db_path, cfg.pool_dir)
    store.save(Meeting(id="a", title="Loops", date="2026-09-10", owner="Sam", notes_markdown="x"),
               Classification("CS 101", 0.9, "ollama", "Loops", ["loops"]))
    store.set_state("sync_account", json.dumps(ACCOUNT))
    store.set_state("sync_listed", "4")
    store.set_state("sync_checked_at", "2026-09-15T11:59:00+00:00")
    store.set_state("sync_error", "")
    store.set_state("last_ingest", "2026-09-15T11:58:00+00:00")
    store.set_state("last_ingest_owner", "Sam")
    c = app_for(cfg, store)

    data = c.get("/api/status").json()
    assert data["ok"] is True and data["pool_name"] == "Fall pool" and data["notes"] == 1
    assert data["classes"] == [{"name": "CS 101", "count": 1, "latest": "2026-09-10"},
                               {"name": "Bio 110", "count": 0, "latest": None}]
    assert data["contributors"] == [{"owner": "Sam", "count": 1, "latest": "2026-09-10"}]
    assert data["unsorted"] == 0 and data["last_ingest_owner"] == "Sam"
    assert data["sync"] == {"enabled": True, "account": ACCOUNT, "listed": 4,
                            "checked_at": "2026-09-15T11:59:00+00:00", "error": None,
                            "last_sync": None}
    assert data["ollama"]["enabled"] is False
    assert data["config"]["pool_dir"] == str(cfg.pool_dir) and "pool_password" not in data["config"]
    assert data["version"] and data["server_time"]

    page = c.get("/status").text
    assert "joey@example.com" in page and esc("Joey's workspace") in page  # escaped apostrophe
    assert "Server sync" in page and "Contributors" in page and str(cfg.pool_dir) in page
    assert NO_NOTES_WARNING not in page  # 4 notes listed: nothing to shout about


def test_status_says_the_zero_notes_failure_out_loud(tmp_path):
    cfg = make_cfg(tmp_path, server_sync=True)
    store = Store(cfg.db_path, cfg.pool_dir)
    store.set_state("sync_account", json.dumps(ACCOUNT))
    store.set_state("sync_listed", "0")
    store.set_state("sync_checked_at", "2026-09-15T11:59:00+00:00")
    c = app_for(cfg, store)
    for path in ("/", "/status"):
        page = c.get(path).text
        assert NO_NOTES_WARNING in page and "joey@example.com" in page
        assert "different Google account" in page
    assert c.get("/api/status").json()["sync"]["listed"] == 0


def test_status_reports_sync_and_ollama_trouble(tmp_path):
    cfg = make_cfg(tmp_path, server_sync=True, ollama_enabled=True, ollama_model="qwen3:1.7b")
    store = Store(cfg.db_path, cfg.pool_dir)
    store.set_state("sync_error", "RuntimeError: not logged in")
    store.set_state("sync_checked_at", "2026-09-15T11:59:00+00:00")
    down = {"enabled": True, "ok": False, "host": "http://localhost:11434", "model": "qwen3:1.7b",
            "installed": False, "models": [], "error": "ConnectError: refused"}
    c = app_for(cfg, store, ollama_check=lambda: dict(down))
    page = c.get("/status").text
    assert "Server sync failed" in page and "not logged in" in page
    assert "Ollama is not answering" in page

    missing = dict(down, ok=True, installed=False, models=["llama3.2:3b"])
    c2 = app_for(cfg, store, ollama_check=lambda: dict(missing))
    assert "ollama pull qwen3:1.7b" in c2.get("/status").text


def test_status_page_when_sync_is_off(tmp_path):
    cfg = make_cfg(tmp_path)
    store = Store(cfg.db_path, cfg.pool_dir)
    c = app_for(cfg, store)
    page = c.get("/status").text
    assert "off — notes arrive only from friends' laptops" in page
    assert NO_NOTES_WARNING not in page
    assert c.get("/api/status").json()["sync"]["enabled"] is False
