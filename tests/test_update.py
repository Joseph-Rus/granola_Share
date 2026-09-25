import sys
from types import SimpleNamespace

import pytest

from granola_share import __version__, autostart, update
from granola_share.update import Release


class Resp(SimpleNamespace):
    def raise_for_status(self):
        if self.status_code >= 400:
            raise RuntimeError(self.status_code)

    def json(self):
        return self.data


def test_parse_and_compare_versions():
    assert update.parse_version("v0.10.2") == (0, 10, 2) > update.parse_version("0.9.9")
    assert update.parse_version("1.2") == (1, 2)
    newer = Release("v99.0.0", (99, 0, 0), "u", "p")
    assert update.is_newer(newer) and not update.is_newer(None)
    assert not update.is_newer(Release("v0.0.1", (0, 0, 1), "u", "p"))


def test_latest_release_reads_github_and_handles_no_releases():
    get = lambda url, **kw: Resp(status_code=200, data={"tag_name": "v0.3.0", "html_url": "https://gh/r/v0.3.0"})
    rel = update.latest_release(get=get)
    assert rel.tag == "v0.3.0" and rel.version == (0, 3, 0)
    assert rel.url == "https://github.com/Joseph-Rus/study-stash/archive/refs/tags/v0.3.0.tar.gz"
    assert update.latest_release(get=lambda url, **kw: Resp(status_code=404, data={})) is None


def test_install_command_pins_python_and_source():
    cmd = update.install_command("/bin/uv", "https://x/v1.tar.gz")
    assert cmd[:4] == ["/bin/uv", "tool", "install", "--force"] and "3.12" in cmd
    assert cmd[-1] == "granola-share @ https://x/v1.tar.gz"
    assert update.install_env()["UV_PYTHON_PREFERENCE"] == "only-managed"


def test_auto_update_only_restarts_a_supervised_tool_install(tmp_path, monkeypatch):
    rel = Release("v99.0.0", (99, 0, 0), "https://x/v99.tar.gz", "p")
    logs, applied, exits = [], [], []
    monkeypatch.setattr(update, "why_not_updatable", lambda: None)
    monkeypatch.setattr(update.platform, "system", lambda: "Darwin")

    def do_apply(r, home, log, restart_services):
        applied.append((r.tag, restart_services))
        return True

    kw = dict(log=logs.append, latest=lambda: rel, do_apply=do_apply, exit_fn=exits.append)
    # run by hand in a terminal: only say so
    assert update.check_and_update(tmp_path, supervised=False, **kw) == "available" and not applied
    # under launchd/systemd: install, then exit so the service manager starts the new version
    assert update.check_and_update(tmp_path, supervised=True, **kw) == "restarting"
    assert applied == [("v99.0.0", False)] and exits == [0]
    assert not (tmp_path / "update.lock").exists()
    # nothing newer
    none = dict(kw, latest=lambda: Release("v0.0.1", (0, 0, 1), "u", "p"))
    assert update.check_and_update(tmp_path, supervised=True, **none) == "up to date"


def test_auto_update_waits_for_another_updater(tmp_path, monkeypatch):
    monkeypatch.setattr(update, "why_not_updatable", lambda: None)
    (tmp_path / "update.lock").write_text("123")
    rel = Release("v99.0.0", (99, 0, 0), "u", "p")
    got = update.check_and_update(tmp_path, supervised=True, log=lambda s: None, latest=lambda: rel,
                                  do_apply=lambda *a, **k: pytest.fail("must not install twice"), exit_fn=lambda c: None)
    assert got == "another update is running"


def test_apply_refuses_source_checkouts(tmp_path, monkeypatch):
    monkeypatch.setattr(update, "install_kind", lambda: "checkout")
    said = []
    assert update.apply(Release("v9", (9,), "u", "p"), tmp_path, log=said.append) is False
    assert "git pull" in said[0]


def test_apply_installs_then_restarts_services(tmp_path, monkeypatch):
    monkeypatch.setattr(update, "why_not_updatable", lambda: None)
    monkeypatch.setattr(update, "find_uv", lambda: "/bin/uv")
    monkeypatch.setattr(update.platform, "system", lambda: "Linux")
    monkeypatch.setattr(autostart, "installed_roles", lambda system=None: ["client"])
    restarted = []
    monkeypatch.setattr(autostart, "restart", lambda role: restarted.append(role))
    runs = []
    run = lambda cmd, **kw: runs.append(cmd) or SimpleNamespace(returncode=0, stdout="", stderr="")
    assert update.apply(Release("v9.0.0", (9, 0, 0), "https://x/v9.tar.gz", "p"), tmp_path, log=lambda s: None, run=run)
    assert runs[0][0] == "/bin/uv" and restarted == ["client"]
    fail = lambda cmd, **kw: SimpleNamespace(returncode=2, stdout="", stderr="network down")
    said = []
    assert not update.apply(Release("v9.0.0", (9, 0, 0), "u", "p"), tmp_path, log=said.append, run=fail)
    assert "network down" in said[-1]


def test_windows_update_hands_off_to_a_helper(tmp_path, monkeypatch):
    monkeypatch.setattr(update, "why_not_updatable", lambda: None)
    monkeypatch.setattr(update, "find_uv", lambda: r"C:\u\uv.exe")
    monkeypatch.setattr(update.platform, "system", lambda: "Windows")
    monkeypatch.setattr(autostart, "installed_roles", lambda system=None: ["client"])
    spawned = []
    monkeypatch.setattr(update, "_spawn_detached", lambda args: spawned.append(args))
    assert update.apply(Release("v9.0.0", (9, 0, 0), "https://x/v9.tar.gz", "p"), tmp_path, log=lambda s: None)
    script = (tmp_path / "update.cmd").read_text()
    assert "Stop-Process" in script and "tool install" in script and "granola-share-client.cmd" in script
    assert spawned and spawned[0][:2] == ["cmd", "/c"]


def test_cleanup_legacy_removes_old_install_only_when_unused(tmp_path, monkeypatch):
    home = tmp_path / "home"
    (home / "venv" / "bin").mkdir(parents=True)
    (home / "app").mkdir()
    agents = tmp_path / "agents"
    agents.mkdir()
    monkeypatch.setattr(autostart, "service_path", lambda role, system=None: agents / f"{role}.plist")
    (agents / "client.plist").write_text(f"<string>{home / 'venv' / 'bin' / 'python'}</string>")
    assert update.cleanup_legacy(home, log=lambda s: None) is False and (home / "venv").exists()
    (agents / "client.plist").write_text("<string>/Users/x/.local/share/uv/tools/granola-share/bin/python</string>")
    said = []
    assert update.cleanup_legacy(home, log=said.append) is True
    assert not (home / "venv").exists() and not (home / "app").exists() and "Removed" in said[0]
    assert update.cleanup_legacy(home, log=said.append) is False


def test_cleanup_legacy_never_removes_the_running_install(tmp_path, monkeypatch):
    home = tmp_path / "home"
    (home / "venv").mkdir(parents=True)
    monkeypatch.setattr(sys, "prefix", str(home / "venv"))
    monkeypatch.setattr(autostart, "service_path", lambda role, system=None: tmp_path / "none")
    assert update.cleanup_legacy(home, log=lambda s: None) is False and (home / "venv").exists()


def test_version_is_single_sourced():
    import tomllib
    from pathlib import Path

    data = tomllib.loads((Path(__file__).resolve().parents[1] / "pyproject.toml").read_text(encoding="utf-8"))
    assert data["project"]["dynamic"] == ["version"]
    assert data["tool"]["setuptools"]["dynamic"]["version"]["attr"] == "granola_share.__version__"
    assert update.parse_version(__version__) >= (0, 2, 0)


def test_windows_auto_update_asks_the_helper_to_restart_services(tmp_path, monkeypatch):
    monkeypatch.setattr(update, "why_not_updatable", lambda: None)
    monkeypatch.setattr(update.platform, "system", lambda: "Windows")
    seen, exits = {}, []

    def do_apply(r, home, log, restart_services):
        seen["restart"] = restart_services
        return True

    got = update.check_and_update(tmp_path, supervised=True, log=lambda s: None,
                                  latest=lambda: Release("v99.0.0", (99, 0, 0), "u", "p"), do_apply=do_apply,
                                  exit_fn=exits.append)
    assert got == "handed off" and seen["restart"] is True and exits == []


def test_setup_recognizes_a_0_1_library_already_on_the_port():
    """Upgrading the Mac mini: the old library holds port 8787 and must not push setup to another port."""
    import socket
    from types import SimpleNamespace

    from granola_share.hostinfo import port_status

    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        s.listen()
        port = s.getsockname()[1]
        old = lambda url, timeout: SimpleNamespace(headers={"content-type": "application/json"}, status_code=401,
                                                   json=lambda: {"detail": "bad pool password"})
        other = lambda url, timeout: SimpleNamespace(headers={"content-type": "text/html"}, status_code=200,
                                                     json=lambda: {})
        assert port_status(port, host="127.0.0.1", get=old) == "ours"
        assert port_status(port, host="127.0.0.1", get=other) == "busy"


def test_windows_keeps_uv_out_of_onedrive(monkeypatch, tmp_path):
    """OneDrive's Files On-Demand blocks the link uv makes to Python in AppData\\Roaming (os error 448), so
    new Windows installs keep uv's Python and tools in AppData\\Local; one already in Roaming stays put."""
    monkeypatch.setenv("APPDATA", str(tmp_path / "Roaming"))
    monkeypatch.setenv("LOCALAPPDATA", str(tmp_path / "Local"))
    for k in ("UV_PYTHON_INSTALL_DIR", "UV_TOOL_DIR"):
        monkeypatch.delenv(k, raising=False)
    env = update.windows_uv_env("Windows", executable=str(tmp_path / "Local" / "uv" / "tools" / "x" / "python.exe"))
    assert env == {"UV_PYTHON_INSTALL_DIR": str(tmp_path / "Local" / "uv" / "python"),
                   "UV_TOOL_DIR": str(tmp_path / "Local" / "uv" / "tools")}
    assert update.windows_uv_env("Windows", executable=str(tmp_path / "Roaming" / "uv" / "tools" / "x" / "python.exe")) == {}
    assert update.windows_uv_env("Darwin") == {}
    monkeypatch.setenv("UV_TOOL_DIR", "D:\\tools")  # yours win
    assert "UV_TOOL_DIR" not in update.windows_uv_env("Windows", executable="C:\\elsewhere\\python.exe")
    monkeypatch.delenv("UV_TOOL_DIR")
    monkeypatch.setattr(update.sys, "executable", str(tmp_path / "Local" / "uv" / "tools" / "x" / "python.exe"))
    script = update._windows_script(tmp_path, "uv.exe", "https://x/v1.tar.gz", ["client"]).read_text()
    assert f'set "UV_PYTHON_INSTALL_DIR={tmp_path / "Local" / "uv" / "python"}"' in script
    assert "$_.Name -like 'python*'" in script and "*granola-share*" in script  # stuck commands too, never cmd
