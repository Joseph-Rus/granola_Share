"""The laptop's Study Stash page (setup and status) and its launcher icon."""

import json
from pathlib import Path

from fastapi.testclient import TestClient

from granola_share import client_app, launcher
from granola_share.client_app import ClientRuntime, create_client_app, write_prefill
from granola_share.config import ClientConfig, load_client_config, save_client_config

H = {"X-Granola-Share": "1"}


class FakeRuntime(ClientRuntime):
    def __init__(self, home, allowed=False):
        super().__init__(home, log=lambda s: None)
        self.started = self.logins = self.permission = self.checks = 0
        self.allowed = allowed
        self._watching = False

    @property
    def watching(self):
        return self._watching

    def start_watching(self):
        self.started += 1
        self._watching = True

    def start_login(self):
        self.logins += 1
        self.login = {"running": True, "error": None, "started": 1}

    def copy_status(self):
        return {"available": True, "enabled": self.config().copy_transcripts, "allowed": self.allowed, "why": None}

    def request_permission(self):
        self.permission += 1

    def check_now(self):
        self.checks += 1

    def reload(self):
        pass


def app(tmp_path, runtime=None, check=None):
    rt = runtime or FakeRuntime(tmp_path)
    check = check or (lambda url, key: {"pool_name": "Fall pool", "classes": ["CS 101"]})
    c = TestClient(create_client_app(rt, port=8765, check_server=check, extra_hosts=("testserver",)),
                   follow_redirects=False)
    return rt, c


def login(c, home):
    token = (home / "ui_token").read_text().strip()
    r = c.get(f"/?t={token}")
    assert r.status_code == 303 and "gs_app" in r.cookies
    c.cookies.set("gs_app", token)


def test_page_needs_the_token_and_this_computer(tmp_path, monkeypatch):
    rt, c = app(tmp_path)
    here = client_app._where_the_app_is()
    assert f"from your {here} to see this page" in c.get("/").text  # no token: nothing about the setup
    assert f"from your {here} to see this page" in c.get("/?t=wrong").text
    for system, said in (("Darwin", "Applications folder"), ("Windows", "Start Menu"), ("Linux", "apps menu")):
        monkeypatch.setattr(client_app.platform, "system", lambda s=system: s)
        assert client_app._where_the_app_is() == said
    monkeypatch.undo()
    assert c.post("/api/pool", json={"server": "x"}, headers=H).status_code == 403
    assert c.get("/", headers={"host": "evil.example:8765"}).status_code == 403  # DNS rebinding
    login(c, tmp_path)
    assert c.post("/api/prefs", json={}).status_code == 403  # no header: a cross-site form can't do this
    assert "Connect to your library" in c.get("/").text


def test_setup_from_the_invite_to_finished(tmp_path, monkeypatch):
    monkeypatch.setattr(client_app, "mac", lambda: True)
    write_prefill(tmp_path, "http://mini:8787", "pw")
    rt, c = app(tmp_path)
    login(c, tmp_path)
    page = c.get("/").text
    assert 'value="http://mini:8787"' in page and 'value="pw"' in page  # from the invite line
    assert (tmp_path / "ui_prefill.json").exists()  # kept until it connects, so a reload doesn't lose it

    r = c.post("/api/pool", json={"server": "mini:8787", "key": "pw"}, headers=H)
    assert r.status_code == 200 and "Fall pool" in r.json()["message"]
    cc = load_client_config(tmp_path)
    assert cc.server_url == "http://mini:8787" and cc.pool_name == "Fall pool"
    assert not (tmp_path / "ui_prefill.json").exists()

    assert c.post("/api/finish", headers=H).status_code == 400  # not signed in yet
    assert c.post("/api/login", headers=H).status_code == 200 and rt.logins == 1
    assert "Waiting for you to finish signing in" in c.get("/").text
    cc.tokens_path.write_text("{}")  # the Granola sign-in finished
    rt.login = {"running": False, "error": None, "started": None}

    r = c.post("/api/prefs", json={"display_name": "Alex", "mode": "auto", "copy_transcripts": True}, headers=H)
    assert r.status_code == 200
    cc = load_client_config(tmp_path)
    assert cc.display_name == "Alex" and cc.mode == "auto" and cc.copy_transcripts

    page = c.get("/").text
    assert "Allow transcript copying" in page and "python3.12" in page
    assert "⌘⇧G" in page and "Copy the path" in page  # the fallback when python3.12 isn't in the list
    assert c.post("/api/allow", headers=H).json()["reload"] is False and rt.permission == 1

    assert c.post("/api/finish", headers=H).status_code == 200 and rt.started == 1 and rt.checks == 1
    assert "Sending your lectures to Fall pool" in c.get("/").text


def test_pool_errors_say_what_to_check(tmp_path):
    def bad(url, key):
        raise RuntimeError("wrong password")

    rt, c = app(tmp_path, check=bad)
    login(c, tmp_path)
    r = c.post("/api/pool", json={"server": "mini:8787", "key": "nope"}, headers=H)
    assert r.status_code == 400 and "Check the password from your library" in r.json()["detail"]


def test_status_page_lists_recent_lectures(tmp_path, monkeypatch):
    monkeypatch.setattr(client_app, "mac", lambda: True)
    cc = ClientConfig(home=tmp_path, server_url="http://mini:8787", pool_key="pw", pool_name="Fall pool",
                      display_name="Sam", copy_transcripts=True)
    save_client_config(cc)
    cc.tokens_path.write_text("{}")
    cc.state_path.write_text(json.dumps({"seen": {
        "a": {"decision": "shared", "title": "Membranes lecture", "date": "2026-09-24", "class_name": "Data Science",
              "filed": True, "transcript_chars": 8000, "at": "2026-09-24T16:00:00+00:00"},
        "b": {"decision": "pending", "title": "Bio lab", "date": "2026-09-23", "at": "2026-09-23T10:00:00+00:00"},
        "c": {"decision": "skipped", "title": "Standup", "date": "2026-09-22", "at": "2026-09-22T09:00:00+00:00"}}}))
    rt = FakeRuntime(tmp_path, allowed=False)
    rt._watching = True
    rt, c = app(tmp_path, runtime=rt)
    login(c, tmp_path)
    page = c.get("/").text
    assert "Filed in Data Science with its transcript" in page
    assert "Waiting for your answer" in page and "Skipped" in page
    assert "needs permission" in page and "Allow transcript copying" in page
    assert c.post("/api/check", headers=H).status_code == 200 and rt.checks == 1
    state = c.get("/api/state").json()
    assert state["configured"] and state["watching"] and state["pool_name"] == "Fall pool"


def test_launcher_icons(tmp_path, monkeypatch):
    monkeypatch.setattr(Path, "home", classmethod(lambda cls: tmp_path))
    monkeypatch.setenv("APPDATA", str(tmp_path / "AppData"))
    app_path = launcher.install(tmp_path / ".granola-share", system="Darwin", python="/py")
    assert app_path == tmp_path / "Applications" / "Study Stash.app"
    plist = (app_path / "Contents" / "Info.plist").read_text()
    script = app_path / "Contents" / "MacOS" / "granola-share-app"
    assert "<string>granola-share-app</string>" in plist and "LSUIElement" in plist
    assert "'client' 'open'" in script.read_text() and "'/py' '-m' 'granola_share.cli'" in script.read_text()
    assert launcher.installed(system="Darwin")
    launcher.uninstall(system="Darwin")
    assert not app_path.exists()

    desktop = launcher.install(tmp_path / ".granola-share", system="Linux", python="/py")
    assert "Exec=/py -m granola_share.cli --home" in desktop.read_text() and "client open" in desktop.read_text()

    ran = []
    link = launcher.install(tmp_path / ".granola-share", system="Windows", python="C:\\py\\python.exe",
                            run=lambda a, **k: ran.append(a))
    assert link.name == "Study Stash.lnk" and "CreateShortcut" in ran[0][-1] and "client open" in ran[0][-1]


def test_mac_app_goes_where_finder_shows_it_when_allowed(tmp_path, monkeypatch):
    monkeypatch.setattr(Path, "home", classmethod(lambda cls: tmp_path))
    old = launcher.install(tmp_path / ".granola-share", system="Darwin", python="/py")  # /Applications not writable
    assert old == tmp_path / "Applications" / "Study Stash.app"
    (tmp_path / "SystemApps").mkdir()
    monkeypatch.setattr(launcher, "SYSTEM_APPS", tmp_path / "SystemApps")  # an admin account can write there
    app = launcher.install(tmp_path / ".granola-share", system="Darwin", python="/py")
    assert app == tmp_path / "SystemApps" / "Study Stash.app" and app.exists()
    assert not old.exists() and launcher.installed(system="Darwin")  # one copy, not two
    launcher.uninstall(system="Darwin")
    assert not launcher.installed(system="Darwin")


def test_status_page_asks_for_the_new_password_when_the_library_refuses_it(tmp_path):
    cc = ClientConfig(home=tmp_path, server_url="http://mini:8787", pool_key="old", pool_name="Fall pool",
                      display_name="Alex")
    save_client_config(cc)
    cc.tokens_path.write_text("{}")
    (tmp_path / "client_state.json").write_text(json.dumps({"seen": {"n1": {
        "decision": "pending", "title": "Membranes", "date": "2026-09-24", "transcript_chars": 8180,
        "at": "2026-09-24T19:37:49+00:00", "error": "401 Unauthorized"}}}))
    rt = FakeRuntime(tmp_path)
    rt._watching = True

    class Refused:
        send_problem_kind = "password"
        last_error = "Fall pool turned down this laptop's password, so lectures are waiting here."

    rt.client = Refused()
    rt, c = app(tmp_path, runtime=rt)
    login(c, tmp_path)
    page = c.get("/").text
    assert "needs its new password" in page and "turned down this laptop" in page
    assert 'data-action="/api/pool"' in page and 'value="http://mini:8787"' in page and "Reconnect" in page
    assert "Waiting to send with its transcript" in page and "Waiting for your answer" not in page


def test_reconnecting_clears_the_password_warning_and_sends_right_away(tmp_path):
    from types import SimpleNamespace

    cc = ClientConfig(home=tmp_path, server_url="http://mini:8787", pool_key="old", pool_name="Fall pool")
    save_client_config(cc)
    rt = client_app.ClientRuntime(tmp_path, log=lambda s: None)
    rt.apply_copying = lambda cc: None
    woke = []
    rt.client = SimpleNamespace(cc=cc, send_problem="turned down", send_problem_kind="password",
                                last_error="turned down", wake=lambda: woke.append(1))
    cc2 = ClientConfig(home=tmp_path, server_url="http://mini:8787", pool_key="new", pool_name="Fall pool")
    save_client_config(cc2)  # what the Reconnect box saves
    rt.reload()
    assert rt.client.cc.pool_key == "new" and woke == [1]
    assert rt.client.last_error is None and rt.client.send_problem_kind is None  # no stale red card
    rt.reload()  # nothing changed: nothing to redo
    assert woke == [1]


def test_status_page_asks_to_sign_in_again_when_granola_signed_out(tmp_path):
    cc = ClientConfig(home=tmp_path, server_url="http://mini:8787", pool_key="pw", pool_name="Fall pool",
                      display_name="Alex")
    save_client_config(cc)
    cc.tokens_path.write_text("{}")
    rt = FakeRuntime(tmp_path)
    rt._watching = True

    class Broken:
        last_error = "token refresh failed (401): invalid_grant. Run `granola-share login`."

    rt.client = Broken()
    rt, c = app(tmp_path, runtime=rt)
    login(c, tmp_path)
    page = c.get("/").text
    assert "Granola signed you out" in page and "Sign in to Granola again" in page and "needs you to sign in again" in page
    assert "granola-share login" not in page  # no terminal commands on the page


def test_open_your_library_signs_in_from_a_browser(tmp_path, monkeypatch):
    monkeypatch.setattr(client_app, "mac", lambda: False)  # Windows and Linux: no Mac app to sign in for us
    cc = ClientConfig(home=tmp_path, server_url="http://mini.tail.ts.net:8787", pool_key="p&w", pool_name="Fall pool")
    save_client_config(cc)
    cc.tokens_path.write_text("{}")
    rt = FakeRuntime(tmp_path)
    rt._watching = True
    rt, c = app(tmp_path, runtime=rt)
    assert c.get("/library").status_code == 303  # not without the page's own cookie
    login(c, tmp_path)
    assert 'href="/library"' in c.get("/").text
    r = c.get("/library")
    assert r.status_code == 200 and 'action="http://mini.tail.ts.net:8787/login"' in r.text
    assert 'name="password" value="p&amp;w"' in r.text and ".submit()" in r.text
    csp = r.headers["content-security-policy"]
    assert "form-action http://mini.tail.ts.net:8787" in csp and "'unsafe-inline'" not in csp.split("script-src")[1].split(";")[0]
    assert r.headers["cache-control"] == "no-store"
    monkeypatch.setattr(client_app, "mac", lambda: True)  # the Mac app signs in by itself
    assert 'href="http://mini.tail.ts.net:8787"' in c.get("/").text


def test_pages_have_the_study_stash_icon(tmp_path):
    rt, c = app(tmp_path)
    r = c.get("/favicon.ico")
    assert r.status_code == 200 and r.headers["content-type"] == "image/x-icon" and len(r.content) > 1000
    assert c.get("/apple-touch-icon.png").headers["content-type"] == "image/png"
    assert 'rel="icon" href="/favicon.ico"' in c.get("/").text
