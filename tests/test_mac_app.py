"""The native Mac app: fetched from the release, never replaced by the script launcher, opened by `client open`."""

from pathlib import Path
from types import SimpleNamespace

from granola_share import client_app, launcher, update


def native_app(where: Path) -> Path:
    app = where / "Granola Share.app"
    (app / "Contents" / "MacOS").mkdir(parents=True)
    (app / "Contents" / "MacOS" / "Granola Share").write_text("binary")
    return app


def test_the_release_says_where_the_mac_app_is():
    data = {"tag_name": "v0.2.4", "html_url": "h", "assets": [
        {"name": "Granola-Share.dmg", "browser_download_url": "https://gh/dl/Granola-Share.dmg"},
        {"name": "Granola-Share-mac.zip", "browser_download_url": "https://gh/dl/Granola-Share-mac.zip"}]}
    ok = SimpleNamespace(status_code=200, raise_for_status=lambda: None, json=lambda: data)
    assert update.latest_release(get=lambda url, **kw: ok).mac_app == "https://gh/dl/Granola-Share-mac.zip"
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
    app = launcher.install_native("https://gh/dl/Granola-Share-mac.zip", log=lambda s: None,
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
    monkeypatch.setattr(client_app.dialogs, "open_url", lambda url: opened.append(("url", url)))
    assert client_app.open_app(tmp_path) and opened == [("url", "http://127.0.0.1:8765/?t=x")]  # no app yet
    opened.clear()
    app = native_app(tmp_path / "Applications")
    client_app.open_app(tmp_path)
    assert opened == [("app", str(app))]
    opened.clear()
    assert client_app.open_app(tmp_path, browser=False) == "http://127.0.0.1:8765/?t=x" and opened == []
