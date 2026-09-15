import asyncio
import json
from contextlib import asynccontextmanager

from fastapi.testclient import TestClient

from granola_share.config import UNSORTED, ClassDef, Config
from granola_share.granola import Meeting
from granola_share.store import Store
from granola_share.sync import sync_once
from granola_share.web import create_app


class FakeClient:
    """Stands in for GranolaClient: same method shapes, canned data."""

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


def make_cfg(tmp_path, password=""):
    return Config(home=tmp_path / "home", pool_dir=tmp_path / "pool", pool_password=password, ollama_enabled=False,
                  classes=[ClassDef("CS 101", ["cs101"]), ClassDef("Bio 110", ["biology"])])


def test_sync_once_saves_new_and_skips_known(tmp_path):
    cfg = make_cfg(tmp_path)
    store = Store(cfg.db_path, cfg.pool_dir)
    stubs = [Meeting(id="a", title="CS101 lec 1", date="2026-09-10"), Meeting(id="b", title="Lunch", date="2026-09-11")]
    full = [Meeting(id="a", title="CS101 lec 1", date="2026-09-10", notes_markdown="loops", raw={"id": "a"}),
            Meeting(id="b", title="Lunch", date="2026-09-11", notes_markdown="tacos", raw={"id": "b"})]
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


def test_web_login_browse_move_download(tmp_path):
    cfg = make_cfg(tmp_path, password="pw")
    store = Store(cfg.db_path, cfg.pool_dir)
    m = Meeting(id="n1", title="Cells", date="2026-09-01", owner="Sam", notes_markdown="# Cells\n\nmembranes",
                raw={"id": "n1", "title": "Cells", "date": "2026-09-01", "notes": "# Cells\n\nmembranes"})
    from granola_share.store import Classification
    store.save(m, Classification(UNSORTED, 0.3, "ollama", "Cells", ["cells"]))

    app = create_app(cfg, store)
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


def test_web_no_password_means_open(tmp_path):
    cfg = make_cfg(tmp_path)
    store = Store(cfg.db_path, cfg.pool_dir)
    c = TestClient(create_app(cfg, store))
    assert c.get("/").status_code == 200
