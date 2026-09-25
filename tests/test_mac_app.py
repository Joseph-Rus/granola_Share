"""The native Mac app: fetched from the release, never replaced by the script launcher, opened by `client open`."""

from pathlib import Path
from types import SimpleNamespace

from granola_share import client_app, launcher, update


def native_app(where: Path) -> Path:
    app = where / "Study Stash.app"
    (app / "Contents" / "MacOS").mkdir(parents=True)
    (app / "Contents" / "MacOS" / "Study Stash").write_text("binary")
    return app


def test_the_release_says_where_the_mac_app_is():
    data = {"tag_name": "v0.2.4", "html_url": "h", "assets": [
        {"name": "Study-Stash.dmg", "browser_download_url": "https://gh/dl/Study-Stash.dmg"},
        {"name": "Study-Stash-mac.zip", "browser_download_url": "https://gh/dl/Study-Stash-mac.zip"}]}
    ok = SimpleNamespace(status_code=200, raise_for_status=lambda: None, json=lambda: data)
    assert update.latest_release(get=lambda url, **kw: ok).mac_app == "https://gh/dl/Study-Stash-mac.zip"
    old = SimpleNamespace(status_code=200, raise_for_status=lambda: None, json=lambda: {"tag_name": "v0.2.2"})
    assert update.latest_release(get=lambda url, **kw: old).mac_app == ""


def test_the_script_launcher_never_replaces_the_real_app(tmp_path, monkeypatch):
    monkeypatch.setattr(Path, "home", classmethod(lambda cls: tmp_path))
    script = launcher.install(tmp_path / ".granola-share", system="Darwin", python="/py")  # 0.2 had this one
    (tmp_path / "SystemApps").mkdir()
    monkeypatch.setattr(launcher, "SYSTEM_APPS", tmp_path / "SystemApps")
    real = native_app(tmp_path / "SystemApps")  # dragged in from the DMG
    assert launcher.install(tmp_path / ".granola-share", system="Darwin", python="/py") == real
    assert launcher.is_native(real) and not script.exists()  # the old script copy is tidied away


def test_install_native_unpacks_the_zip_into_applications(tmp_path, monkeypatch):
    monkeypatch.setattr(Path, "home", classmethod(lambda cls: tmp_path))
    old = launcher.install(tmp_path / ".granola-share", system="Darwin", python="/py")

    def ditto(cmd, **kw):
        assert cmd[:3] == ["ditto", "-x", "-k"]
        native_app(Path(cmd[4]))
        return SimpleNamespace(returncode=0)

    got = SimpleNamespace(status_code=200, content=b"zip", raise_for_status=lambda: None)
    app = launcher.install_native("https://gh/dl/Study-Stash-mac.zip", log=lambda s: None,
                                  get=lambda url, **kw: got, run=ditto)
    assert app == old and launcher.is_native(app) and launcher.native_installed() == app

    said = []
    broken = launcher.install_native("u", log=said.append, get=lambda url, **kw: got,
                                     run=lambda cmd, **kw: SimpleNamespace(returncode=1))
    assert broken is None and launcher.is_native(app) and "keeping the one you have" in said[0]


def test_client_open_shows_the_app_not_the_browser(tmp_path, monkeypatch):
    monkeypatch.setattr(Path, "home", classmethod(lambda cls: tmp_path))
    monkeypatch.setattr(client_app, "mac", lambda: True)
    monkeypatch.setattr(client_app.autostart, "status", lambda role: "running")
    monkeypatch.setattr(client_app, "wait_for_app", lambda home: "http://127.0.0.1:8765/?t=x")
    opened = []
    monkeypatch.setattr(client_app.dialogs, "open_app", lambda name: opened.append(("app", name)) or True)
    # no app yet: the page opens in the browser (as its own window, when Edge or Chrome is there)
    monkeypatch.setattr(client_app.dialogs, "open_window", lambda url: opened.append(("url", url)))
    assert client_app.open_app(tmp_path) and opened == [("url", "http://127.0.0.1:8765/?t=x")]  # no app yet
    opened.clear()
    app = native_app(tmp_path / "Applications")
    client_app.open_app(tmp_path)
    assert opened == [("app", str(app))]
    opened.clear()
    assert client_app.open_app(tmp_path, browser=False) == "http://127.0.0.1:8765/?t=x" and opened == []


def test_the_old_granola_share_app_is_replaced_by_study_stash(tmp_path, monkeypatch):
    monkeypatch.setattr(Path, "home", classmethod(lambda cls: tmp_path))
    old = tmp_path / "Applications" / "Granola Share.app"  # what 0.2 installed
    (old / "Contents" / "MacOS").mkdir(parents=True)
    (old / "Contents" / "MacOS" / "Granola Share").write_text("binary")
    assert launcher.native_installed() == old  # updates still find it, and replace it

    def ditto(cmd, **kw):
        app = Path(cmd[4]) / "Study Stash.app"
        (app / "Contents" / "MacOS").mkdir(parents=True)
        (app / "Contents" / "MacOS" / "Study Stash").write_text("binary")
        return SimpleNamespace(returncode=0)

    got = SimpleNamespace(status_code=200, content=b"zip", raise_for_status=lambda: None)
    new = launcher.install_native("u", log=lambda s: None, get=lambda url, **kw: got, run=ditto)
    assert new == tmp_path / "Applications" / "Study Stash.app" and launcher.is_native(new)
    assert not old.exists() and launcher.native_installed() == new


def test_the_library_mac_app_installs_and_updates_on_its_own(tmp_path, monkeypatch):
    monkeypatch.setattr(Path, "home", classmethod(lambda cls: tmp_path))
    data = {"tag_name": "v0.4.1", "html_url": "h", "assets": [
        {"name": "Study-Stash-mac.zip", "browser_download_url": "https://gh/dl/Study-Stash-mac.zip"},
        {"name": "Study-Stash-Library-mac.zip", "browser_download_url": "https://gh/dl/Study-Stash-Library-mac.zip"}]}
    ok = SimpleNamespace(status_code=200, raise_for_status=lambda: None, json=lambda: data)
    rel = update.latest_release(get=lambda url, **kw: ok)
    assert rel.mac_library_app == "https://gh/dl/Study-Stash-Library-mac.zip"

    def ditto(cmd, **kw):
        app = Path(cmd[4]) / "Study Stash Library.app"
        (app / "Contents" / "MacOS").mkdir(parents=True)
        (app / "Contents" / "MacOS" / "Study Stash").write_text("binary")
        return SimpleNamespace(returncode=0)

    got = SimpleNamespace(status_code=200, content=b"zip", raise_for_status=lambda: None)
    laptop_app = native_app(tmp_path / "Applications")
    app = launcher.install_native(rel.mac_library_app, log=lambda s: None, get=lambda url, **kw: got, run=ditto,
                                  name=launcher.LIBRARY_APP_NAME)
    assert app == tmp_path / "Applications" / "Study Stash Library.app" and launcher.library_app_installed() == app
    assert launcher.native_installed() == laptop_app  # the laptop's app is its own thing, untouched
