"""The Windows app (windows/StudyStash.cs): fetched from the release, updated in place while it runs,
opened by `client open` and the Start Menu. And the laptop's own checks: Granola and Tailscale."""

import io
import zipfile
from pathlib import Path
from types import SimpleNamespace

import pytest
from fastapi.testclient import TestClient

from granola_share import client_app, doctor, launcher, ready, update, wizard
from granola_share.config import ClientConfig, save_client_config

from test_client_app import H, FakeRuntime, login


def release_zip(version: bytes = b"new") -> bytes:
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w") as z:
        z.writestr("Study Stash.exe", version)
        z.writestr("Microsoft.Web.WebView2.Core.dll", b"dll")
        z.writestr("runtimes/win-x64/native/WebView2Loader.dll", b"loader")
    return buf.getvalue()


def served(content: bytes):
    return lambda url, **kw: SimpleNamespace(status_code=200, content=content, raise_for_status=lambda: None)


@pytest.fixture
def local(tmp_path, monkeypatch):
    monkeypatch.setenv("LOCALAPPDATA", str(tmp_path / "Local"))
    return tmp_path / "Local" / "Programs" / "Study Stash"


def test_the_release_says_where_the_windows_app_is():
    data = {"tag_name": "v0.4.0", "html_url": "h", "assets": [
        {"name": "Study-Stash-mac.zip", "browser_download_url": "https://gh/dl/Study-Stash-mac.zip"},
        {"name": "Study-Stash-windows.zip", "browser_download_url": "https://gh/dl/Study-Stash-windows.zip"},
        {"name": "Study-Stash-Setup.exe", "browser_download_url": "https://gh/dl/Study-Stash-Setup.exe"}]}
    ok = SimpleNamespace(status_code=200, raise_for_status=lambda: None, json=lambda: data)
    rel = update.latest_release(get=lambda url, **kw: ok)
    assert rel.windows_app == "https://gh/dl/Study-Stash-windows.zip" and rel.mac_app.endswith("mac.zip")


def test_install_windows_app_unpacks_the_release(local):
    said = []
    exe = launcher.install_windows_app("u", log=said.append, get=served(release_zip()))
    assert exe == local / "Study Stash.exe" and launcher.windows_app_exe() == exe
    assert (local / "runtimes" / "win-x64" / "native" / "WebView2Loader.dll").read_bytes() == b"loader"
    assert "Installed the Study Stash app" in said[0]
    broken = io.BytesIO()
    with zipfile.ZipFile(broken, "w") as z:
        z.writestr("readme.txt", b"no app here")
    assert launcher.install_windows_app("u", log=said.append, get=served(broken.getvalue())) is None
    assert exe.read_bytes() == b"new" and "keeping the one you have" in said[-1]


def test_an_update_moves_the_running_app_aside(local, monkeypatch):
    launcher.install_windows_app("u", log=lambda s: None, get=served(release_zip(b"v1")))
    running = local / "Study Stash.exe"
    real_unlink = Path.unlink

    def locked(self, missing_ok=False):  # Windows: a running .exe can't be deleted, only renamed
        if self == running:
            raise PermissionError("in use")
        return real_unlink(self, missing_ok=missing_ok)

    monkeypatch.setattr(Path, "unlink", locked)
    launcher.install_windows_app("u", log=lambda s: None, get=served(release_zip(b"v2")))
    assert running.read_bytes() == b"v2" and (local / "Study Stash.exe.old").read_bytes() == b"v1"


def test_start_menu_opens_the_windows_app_once_it_is_there(tmp_path, local):
    scripts = []
    run = lambda args, **k: scripts.append(args[-1])  # noqa: E731
    launcher.install(tmp_path, system="Windows", python=str(tmp_path / "python.exe"), run=run)
    assert "client" in scripts[0] and "study-stash.ico" in scripts[0]  # before: the helper opens the page
    launcher.install_windows_app("u", log=lambda s: None, get=served(release_zip()))
    launcher.install(tmp_path, system="Windows", python=str(tmp_path / "python.exe"), run=run)
    exe = str(local / "Study Stash.exe")
    assert f"$s.TargetPath='{exe}'" in scripts[1] and "$s.Arguments=''" in scripts[1]
    assert f"IconLocation='{exe},0'" in scripts[1]


def test_client_open_on_windows_installs_and_opens_the_app(tmp_path, local, monkeypatch):
    monkeypatch.setattr(client_app, "mac", lambda: False)
    monkeypatch.setattr(client_app, "windows", lambda: True)
    monkeypatch.setattr(client_app.autostart, "install", lambda role, home: None)
    monkeypatch.setattr(client_app.autostart, "status", lambda role: "running")
    monkeypatch.setattr(launcher, "install", lambda home: None)
    monkeypatch.setattr(client_app, "wait_for_app", lambda home: "http://127.0.0.1:8765/?t=x")
    monkeypatch.setattr("granola_share.update.cleanup_legacy", lambda home, log: False)
    rel = update.Release("v0.4.0", (0, 4, 0), "src", "h", "", "https://gh/dl/Study-Stash-windows.zip")
    monkeypatch.setattr(update, "latest_release", lambda: rel)
    fetched = []
    real = launcher.install_windows_app
    monkeypatch.setattr(launcher, "install_windows_app",
                        lambda url, log=print: fetched.append(url) or real(url, log=log, get=served(release_zip())))
    opened = []
    monkeypatch.setattr(client_app.dialogs, "open_app", lambda name: opened.append(name) or True)
    assert client_app.open_app(tmp_path, install=True, log=lambda s: None)
    assert fetched == [rel.windows_app] and opened == [str(local / "Study Stash.exe")]


def test_two_windows_installers_one_app():
    """Study-Stash-Laptop-Setup.exe and Study-Stash-Library-Setup.exe: the same app, in the folders the
    updater looks in, and the library's copy marked as the library's."""
    iss = (Path(__file__).resolve().parents[1] / "windows" / "setup.iss").read_text()
    assert r"DefaultDirName={localappdata}\Programs\{#Name}" in iss and "PrivilegesRequired=lowest" in iss
    assert '#define Name "Study Stash Library"' in iss and '#define Output "Study-Stash-Library-Setup"' in iss
    assert '#define Name "Study Stash"' in iss and '#define Output "Study-Stash-Laptop-Setup"' in iss
    assert 'Key: "role"; String: "library"' in iss and 'Name: "{userprograms}\\{#Name}"' in iss
    assert launcher.windows_app_dir().name == "Study Stash"
    assert launcher.windows_app_dir(launcher.LIBRARY_APP_NAME).name == "Study Stash Library"
    cs = (Path(__file__).resolve().parents[1] / "windows" / "StudyStash.cs").read_text()
    assert '"role=library"' in cs and '"--library"' in cs  # what the library installer writes, the app reads


def test_updates_refresh_every_installed_windows_app(local, monkeypatch):
    lib = local.with_name("Study Stash Library")
    for folder in (local, lib):
        launcher.install_windows_app("u", log=lambda s: None, get=served(release_zip(b"old")), dest=folder)
    (lib / "study-stash.ini").write_text("[app]\nrole=library\n")
    assert launcher.windows_apps_installed() == [local, lib]
    for folder in launcher.windows_apps_installed():
        launcher.install_windows_app("u", log=lambda s: None, get=served(release_zip(b"new")), dest=folder)
    assert (local / "Study Stash.exe").read_bytes() == (lib / "Study Stash.exe").read_bytes() == b"new"
    assert "role=library" in (lib / "study-stash.ini").read_text()  # the update keeps what it is


# --- the laptop's own checks ---------------------------------------------------------------------------

def test_granola_is_found_on_a_mac_and_on_windows(tmp_path, monkeypatch):
    monkeypatch.setattr(ready.Path, "home", classmethod(lambda cls: tmp_path))
    nothing = lambda *a, **k: SimpleNamespace(stdout="")  # noqa: E731  (Spotlight finds nothing either)
    if not Path("/Applications/Granola.app").exists():
        assert ready.granola_app("Darwin", run=nothing) is None
    (tmp_path / "Applications" / "Granola.app").mkdir(parents=True)
    assert ready.granola_app("Darwin", run=nothing) in ("/Applications/Granola.app",
                                                         str(tmp_path / "Applications" / "Granola.app"))
    monkeypatch.setenv("LOCALAPPDATA", str(tmp_path / "Local"))
    for var in ("ProgramFiles", "ProgramW6432"):
        monkeypatch.setenv(var, str(tmp_path / "PF"))
    assert ready.granola_app("Windows") is None  # no registry off Windows, either
    exe = tmp_path / "Local" / "Programs" / "@granolaelectron" / "Granola.exe"
    exe.parent.mkdir(parents=True)
    exe.write_text("")
    assert ready.granola_app("Windows") == str(exe)
    assert ready.laptop_checks("Linux", tailscale=lambda: {}) == {"granola": None, "granola_here": False, "tailscale": {}}


MISSING = {"granola": None, "granola_here": True, "tailscale": {"installed": False, "running": False}}
FINE = {"granola": "/Applications/Granola.app", "granola_here": True,
        "tailscale": {"installed": True, "running": True, "state": "Running", "dns": "laptop.tail.ts.net"}}


def test_doctor_checks_granola_and_tailscale_on_the_laptop():
    checks = {c.name: c for c in doctor.laptop_app_checks(MISSING)}
    assert checks["Granola app"].state == doctor.FAIL and ready.GRANOLA_DOWNLOAD in checks["Granola app"].fix
    assert checks["Tailscale"].state == doctor.WARN and "tailscale.com/download" in checks["Tailscale"].fix
    checks = {c.name: c for c in doctor.laptop_app_checks(FINE)}
    assert all(c.state == doctor.OK for c in checks.values()) and "laptop.tail.ts.net" in checks["Tailscale"].detail


def test_terminal_setup_says_what_the_laptop_is_missing(tmp_path):
    io_ = wizard.ScriptedPrompter(["http://mini:8787", "pw", False, False, False, False])
    wizard.client_setup(tmp_path, io_, check_server=lambda u, k: {"pool_name": "P"}, do_login=lambda c: None,
                        is_logged_in=lambda c: False, install_autostart=lambda r, h: "p", share_now=lambda c, log: None,
                        service_status=lambda r: "running", cleanup=lambda home, log: False, mac=False,
                        laptop=lambda: MISSING)
    out = "\n".join(io_.output)
    assert f"Granola:    not installed. It records your lectures: get it from {ready.GRANOLA_DOWNLOAD}" in out
    assert "Tailscale:  not installed" in out


class Checked(FakeRuntime):
    def __init__(self, home, checks):
        super().__init__(home)
        self.checks_now, self.fixed = checks, 0

    def readiness(self, fresh=False):
        return self.checks_now

    def fix_tailscale(self):
        self.fixed += 1
        return "Downloading Tailscale."


def page(tmp_path, checks):
    rt = Checked(tmp_path, checks)
    c = TestClient(client_app.create_client_app(rt, port=8765, check_server=lambda u, k: {"pool_name": "P"},
                                                extra_hosts=("testserver",)), follow_redirects=False)
    login(c, tmp_path)
    return rt, c


def test_setup_page_offers_granola_and_tailscale_when_missing(tmp_path):
    rt, c = page(tmp_path, MISSING)
    html = c.get("/").text
    assert "This computer" in html and f'href="{ready.GRANOLA_DOWNLOAD}"' in html and "Get Granola" in html
    assert "Install Tailscale" in html and 'data-action="/api/tailscale"' in html
    r = c.post("/api/tailscale", headers=H)
    assert r.json() == {"message": "Downloading Tailscale.", "reload": False} and rt.fixed == 1
    assert c.get("/api/state").json()["ready"] == [False, "not installed", False]
    rt.checks_now = FINE
    html = c.get("/").text
    assert "Get Granola" not in html and "Install Tailscale" not in html and "connected" in html


def test_status_page_shows_a_missing_granola(tmp_path):
    cc = ClientConfig(home=tmp_path, server_url="http://mini:8787", pool_key="pw", pool_name="Fall")
    save_client_config(cc)
    cc.tokens_path.write_text("{}")
    rt, c = page(tmp_path, MISSING)
    rt._watching = True
    assert "Granola app" in c.get("/").text and "not installed on this computer" in c.get("/").text
