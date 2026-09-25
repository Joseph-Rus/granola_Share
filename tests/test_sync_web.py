import asyncio
from contextlib import asynccontextmanager

from fastapi.testclient import TestClient

from granola_share.config import UNSORTED, ClassDef, Config, load_config
from granola_share.granola import Meeting
from granola_share.pipeline import Pipeline
from granola_share.store import Classification, Store
from granola_share.sync import sync_once
from granola_share.update import Release
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


def make_cfg(tmp_path, password="", admin=""):
    return Config(home=tmp_path / "home", pool_dir=tmp_path / "pool", pool_password=password, admin_password=admin,
                  ollama_enabled=False, classes=[ClassDef("CS 101", ["cs101"]), ClassDef("Bio 110", ["biology"])])


def app_for(cfg, store, pipeline=None, **kw):
    kw.setdefault("list_models", lambda h: [{"name": "qwen3:1.7b", "size_gb": 1.4}])
    kw.setdefault("tailscale", lambda: {"running": True, "dns": "mini.tail.ts.net", "ips": ["100.1.2.3"]})
    kw.setdefault("latest", lambda *a: None)
    return create_app(cfg, store, pipeline or Pipeline(cfg, store, log=lambda *_: None), **kw)


def test_sync_once_queues_new_and_skips_known(tmp_path):
    cfg = make_cfg(tmp_path)
    store = Store(cfg.db_path, cfg.pool_dir)
    stubs = [Meeting(id="a", title="CS101 lec 1", date="2026-09-10"), Meeting(id="b", title="Lunch", date="2026-09-11")]
    full = [Meeting(id="a", title="CS101 lec 1", date="2026-09-10", notes_markdown="loops", raw={"id": "a"}),
            Meeting(id="b", title="Lunch", date="2026-09-11", notes_markdown="tacos", raw={"id": "b"})]
    client = FakeClient(stubs, full, transcript="hello transcript")
    woke = []
    rep = asyncio.run(sync_once(cfg, client, store, log=lambda *_: None, on_queued=lambda: woke.append(1)))
    assert rep.listed == 2 and rep.new == 2 and rep.queued == ["CS101 lec 1", "Lunch"] and not rep.errors
    assert woke == [1] and store.get("a")["has_transcript"] == 1
    assert Pipeline(cfg, store, log=lambda *_: None).run_pending() == 2
    assert {r["id"]: r["class_name"] for r in store.list_notes()} == {"a": "CS 101", "b": UNSORTED}
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
    assert rep.errors and rep.queued == ["biology lab"]
    Pipeline(cfg, store, log=lambda *_: None).run_pending()
    assert "stub notes" in open(store.get("z")["md_path"], encoding="utf-8").read()


def _seed(store):
    m = Meeting(id="n1", title="Cells", date="2026-09-01", owner="Sam", notes_markdown="# Cells\n\nmembranes",
                transcript="today we talk about the cell membrane and osmosis",
                raw={"id": "n1", "title": "Cells", "date": "2026-09-01", "notes": "# Cells\n\nmembranes"})
    store.save(m, Classification(UNSORTED, 0.3, "ollama", "Cells", ["cells"]))
    return m


def test_web_login_browse_move_download(tmp_path):
    cfg = make_cfg(tmp_path, password="pw", admin="boss")
    store = Store(cfg.db_path, cfg.pool_dir)
    _seed(store)
    c = TestClient(app_for(cfg, store), follow_redirects=False)
    r = c.get("/note/n1")
    assert r.status_code == 303 and r.headers["location"].startswith("/login?next=")
    bad = c.post("/login", data={"password": "nope", "next": "/"})
    assert bad.status_code == 303 and "bad=1" in bad.headers["location"]
    r = c.post("/login", data={"password": "pw", "next": "/note/n1"})
    assert r.status_code == 303 and r.headers["location"] == "/note/n1" and "pool" in r.cookies

    home = c.get("/").text
    assert "Recent lectures" in home and "Cells" in home and "Unsorted" in home
    assert "Content-Security-Policy" in c.get("/").headers or "content-security-policy" in c.get("/").headers
    assert "Cells" in c.get("/unsorted").text
    page = c.get("/note/n1").text
    assert "membranes" in page and "<select" in page and "Transcript" in page and "osmosis" in page
    assert "Rewrite summary" in page and "from Sam" not in page  # one person: everything, no "from" labels
    r = c.post("/note/n1/class", data={"class_name": "Bio 110"})
    assert r.status_code == 303 and store.get("n1")["class_name"] == "Bio 110"
    # moving keeps the transcript in the file (0.1 dropped it)
    dl = c.get("/note/n1/download")
    assert dl.status_code == 200 and "membranes" in dl.text and "osmosis" in dl.text
    z = c.get("/class/Bio 110/zip")
    assert z.status_code == 200 and z.headers["content-type"].startswith("application/zip")
    api = c.get("/api/notes", params={"class_name": "Bio 110"}).json()
    assert api[0]["id"] == "n1" and api[0]["topics"] == ["cells"]
    assert c.post("/note/n1/class", data={"class_name": "Nope"}).status_code == 400

    hits = c.get("/search", params={"q": "osmosis"}).text
    assert "1 lecture mention" in hits and "<mark>osmosis</mark>" in hits
    assert "No matches" in c.get("/search", params={"q": "quantum"}).text

    assert c.get("/settings").status_code == 200  # the one password opens Settings too
    logged_out = TestClient(app_for(cfg, store), follow_redirects=False)
    assert logged_out.post("/note/n1/delete").status_code == 303 and store.get("n1")  # not deleted without a login


def test_admin_settings_models_classes_and_invite(tmp_path):
    cfg = make_cfg(tmp_path, password="pw", admin="boss")
    store = Store(cfg.db_path, cfg.pool_dir)
    _seed(store)
    pipeline = Pipeline(cfg, store, log=lambda *_: None)
    c = TestClient(app_for(cfg, store, pipeline), follow_redirects=False)
    c.post("/login", data={"password": "boss", "next": "/"})
    s = c.get("/settings").text
    assert "qwen3:1.7b" in s and "Connect your laptop" in s and "GRANOLA_SHARE_SERVER=http://mini.tail.ts.net:8787" in s
    assert "Rewrite summary" in c.get("/note/n1").text

    form = {"summary_model": "qwen3:1.7b", "ollama_model": "qwen3:1.7b", "summary_enabled": "1",
            "ollama_enabled": "1", "min_confidence": "0.7",
            "class_name_0": "CS 101", "class_aliases_0": "cs101, intro", "class_desc_0": "Programming",
            "class_name_1": "Bio 110", "class_aliases_1": "", "class_desc_1": "", "class_remove_1": "1",
            "class_name_2": "Chem 1A", "class_aliases_2": "chem", "class_desc_2": "",
            "class_name_3": "", "class_aliases_3": "", "class_desc_3": ""}
    r = c.post("/settings", data=form)
    assert r.status_code == 303 and r.headers["location"] == "/settings?saved=1"
    back = load_config(cfg.home)
    assert back.summary_model == "qwen3:1.7b" and back.min_confidence == 0.7 and back.keep_granola_notes is False
    assert back.class_names() == ["CS 101", "Chem 1A"] and back.classes[0].aliases == ["cs101", "intro"]
    assert cfg.class_names() == ["CS 101", "Chem 1A"]  # the running app sees it right away

    r = c.post("/settings/resummarize-all")
    assert r.headers["location"] == "/settings?queued=1" and store.get("n1")["status"] == "queued"
    assert "Cells" in c.get("/unsorted").text  # still readable while it waits
    assert c.post("/note/n1/delete").status_code == 303 and store.get("n1") is None


def test_settings_offers_update_when_newer_release(tmp_path):
    cfg = make_cfg(tmp_path, admin="boss")
    store = Store(cfg.db_path, cfg.pool_dir)
    rel = Release("v9.9.9", (9, 9, 9), "https://x/v9.9.9.tar.gz", "https://x/releases/v9.9.9")
    c = TestClient(app_for(cfg, store, latest=lambda *a: rel))
    c.post("/login", data={"password": "boss", "next": "/"})
    assert "v9.9.9 is out" in c.get("/settings").text


def test_note_html_is_neutralized(tmp_path):
    cfg = make_cfg(tmp_path)
    store = Store(cfg.db_path, cfg.pool_dir)
    m = Meeting(id="x", title="<b>Lec</b>", notes_markdown="hi <script>alert(1)</script> [a](javascript:alert(2)) $x_1^2$")
    store.save(m, Classification("CS 101", 1, "human"))
    page = TestClient(app_for(cfg, store)).get("/note/x").text
    assert "<script>alert(1)" not in page and "&lt;script&gt;" in page
    assert "javascript:alert" not in page and "&lt;b&gt;Lec" in page
    assert "$x_1^2$" in page  # math survives Markdown for KaTeX


def test_web_no_password_means_open(tmp_path):
    cfg = make_cfg(tmp_path)
    store = Store(cfg.db_path, cfg.pool_dir)
    c = TestClient(app_for(cfg, store))
    assert c.get("/").status_code == 200
    assert "No lectures yet" in c.get("/").text


def test_login_never_redirects_off_site():
    from granola_share.web import safe_next

    assert safe_next("/note/n1?x=1") == "/note/n1?x=1"
    for bad in ["https://evil.com", "//evil.com", "/\\evil.com", "/\t/evil.com", "/\n/evil.com", "evil.com", ""]:
        assert safe_next(bad) == "/", bad


def test_log_out_is_never_marked_as_the_page_you_are_on(tmp_path):
    """Pages with no sidebar item of their own (search, not found) marked Log out as the current page."""
    cfg = make_cfg(tmp_path, password="pw")
    store = Store(cfg.db_path, cfg.pool_dir)
    c = TestClient(app_for(cfg, store), follow_redirects=False)
    c.post("/login", data={"password": "pw", "next": "/"})
    for page in ("/search?q=x", "/note/missing"):
        assert '<a href="/logout" aria-current' not in c.get(page).text, page
