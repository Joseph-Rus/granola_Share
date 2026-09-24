import os
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
