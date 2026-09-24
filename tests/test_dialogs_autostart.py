from types import SimpleNamespace

from granola_share import autostart
from granola_share.dialogs import ask_yes_no, build_osascript, notify, parse_osascript


def test_osascript_build_and_parse():
    s = build_osascript('Ti"tle', "Share it?", "Share", "Skip", 42)
    assert 'with title "Ti\\"tle"' in s and 'buttons {"Skip", "Share"}' in s and "giving up after 42" in s
    assert parse_osascript("button returned:Share, gave up:false", "Share") is True
    assert parse_osascript("button returned:Skip, gave up:false", "Share") is False
    assert parse_osascript("button returned:, gave up:true", "Share") is None


def test_ask_yes_no_darwin_with_fake_runner():
    calls = []

    def runner(args, capture_output, text):
        calls.append(args)
        return SimpleNamespace(returncode=0, stdout="button returned:Share, gave up:false")

    assert ask_yes_no("t", "x", runner=runner, system="Darwin") is True
    assert calls[0][0] == "osascript"
    cancelled = lambda a, capture_output, text: SimpleNamespace(returncode=1, stdout="")
    assert ask_yes_no("t", "x", runner=cancelled, system="Darwin") is False
    notify("t", "x", runner=runner, system="Darwin")
    assert "display notification" in calls[-1][2]


def test_role_args_and_renderers(tmp_path):
    args = autostart.role_args("client", tmp_path, python="/py")
    assert args == ["/py", "-u", "-m", "granola_share.cli", "--home", str(tmp_path), "client", "run"]
    plist = autostart.render_plist("com.granola-share.client", args, tmp_path / "c.log")
    assert "<string>client</string>" in plist and "<key>KeepAlive</key><true/>" in plist
    unit = autostart.render_systemd("d", ["/py", "-m", "x", "--home", "/a b"])
    assert 'ExecStart=/py -m x --home "/a b"' in unit
    cmd = autostart.render_cmd(["C:\\py\\python.exe", "-m", "granola_share.cli", "run"])
    assert cmd.startswith("@echo off") and '"run"' in cmd


def test_install_uninstall_darwin(tmp_path):
    calls = []
    run = lambda a, capture_output, text: calls.append(a)
    la = tmp_path / "LaunchAgents"
    p = autostart.install("server", tmp_path / "home", system="Darwin", launch_agents_dir=la, run_cmd=run, python="/py")
    assert p.exists() and p.name == "com.granola-share.server.plist"
    assert calls[-1][:2] == ["launchctl", "bootstrap"] and (tmp_path / "home" / "logs").is_dir()
    assert autostart.uninstall("server", system="Darwin", launch_agents_dir=la, run_cmd=run) is True
    assert not p.exists()
    assert autostart.uninstall("server", system="Darwin", launch_agents_dir=la, run_cmd=run) is False


def test_install_windows_and_linux(tmp_path):
    run = lambda a, capture_output, text: None
    p = autostart.install("client", tmp_path / "h", system="Windows", startup_dir=tmp_path / "Startup", run_cmd=run, python="C:\\py.exe")
    assert p.suffix == ".cmd" and "client" in p.read_text()
    assert autostart.uninstall("client", system="Windows", startup_dir=tmp_path / "Startup", run_cmd=run)
    u = autostart.install("client", tmp_path / "h", system="Linux", systemd_dir=tmp_path / "sd", run_cmd=run, python="/py")
    assert u.suffix == ".service" and "Restart=always" in u.read_text()
    assert autostart.uninstall("client", system="Linux", systemd_dir=tmp_path / "sd", run_cmd=run)


def test_services_are_marked_for_the_auto_updater(tmp_path):
    args = ["/py", "-u", "-m", "granola_share.cli", "run"]
    assert "<key>GRANOLA_SHARE_SERVICE</key><string>1</string>" in autostart.render_plist("l", args, tmp_path / "x")
    assert "Environment=GRANOLA_SHARE_SERVICE=1" in autostart.render_systemd("d", args)
    assert "set GRANOLA_SHARE_SERVICE=1" in autostart.render_cmd(args)


def test_status_and_restart(tmp_path, monkeypatch):
    monkeypatch.setattr(autostart, "default_launch_agents_dir", lambda: tmp_path)
    monkeypatch.setattr(autostart, "default_systemd_dir", lambda: tmp_path)
    ok = lambda out: SimpleNamespace(returncode=0, stdout=out)
    assert autostart.status("client", system="Darwin", run_cmd=lambda a, **k: ok("state = running")) == "missing"
    (tmp_path / "com.granola-share.client.plist").write_text("x")
    assert autostart.status("client", system="Darwin", run_cmd=lambda a, **k: ok("state = running")) == "running"
    assert autostart.status("client", system="Darwin", run_cmd=lambda a, **k: ok("state = waiting")) == "stopped"
    assert autostart.installed_roles(system="Darwin") == ["client"]
    calls = []
    autostart.restart("client", system="Darwin", run_cmd=lambda a, **k: calls.append(a) or ok(""))
    assert calls[0][:3] == ["launchctl", "kickstart", "-k"] and calls[0][3].endswith("/com.granola-share.client")

    (tmp_path / "granola-share-server.service").write_text("x")
    assert autostart.status("server", system="Linux", run_cmd=lambda a, **k: ok("active\n")) == "running"
    calls.clear()
    autostart.restart("server", system="Linux", run_cmd=lambda a, **k: calls.append(a) or ok(""))
    assert calls == [["systemctl", "--user", "restart", "granola-share-server.service"]]


def test_windows_process_filter_tells_roles_apart():
    assert "-like '*client*run*'" in autostart._ps_filter("client")
    assert "-notlike '*client*run*'" in autostart._ps_filter("server")
