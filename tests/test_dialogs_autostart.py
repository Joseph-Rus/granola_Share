import os
from pathlib import Path
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
    plist = autostart.render_plist("com.granola-share.client", args, tmp_path / "logs" / "c.log", tmp_path)
    assert "<string>-u</string>" in plist and "<string>client</string>" in plist and "<key>KeepAlive</key><true/>" in plist
    assert f"<key>WorkingDirectory</key><string>{tmp_path}</string>" in plist
    assert "<key>PYTHONUNBUFFERED</key><string>1</string>" in plist and "<key>PATH</key>" in plist
    unit = autostart.render_systemd("d", ["/py", "-m", "x", "--home", "/a b"], Path("/a b"))
    assert 'ExecStart=/py -m x --home "/a b"' in unit
    assert "WorkingDirectory=/a b\n" in unit and "Environment=PYTHONUNBUFFERED=1\n" in unit and "Restart=always" in unit
    cmd = autostart.render_cmd(["C:\\py\\python.exe", "-m", "granola_share.cli", "run"], Path("C:\\Users\\me\\.granola-share"))
    assert cmd.startswith("@echo off\r\n") and '"run"' in cmd
    assert "set PYTHONUNBUFFERED=1\r\n" in cmd and 'cd /d "C:\\Users\\me\\.granola-share"\r\n' in cmd
    # the home defaults to the log dir's parent, so older callers get a working directory too
    assert f"<string>{tmp_path}</string>" in autostart.render_plist("l", args, tmp_path / "logs" / "c.log")


def test_install_uninstall_darwin(tmp_path):
    calls = []
    run = lambda a, capture_output, text: calls.append(a)
    la = tmp_path / "LaunchAgents"
    p = autostart.install("server", tmp_path / "home", system="Darwin", launch_agents_dir=la, run_cmd=run, python="/py")
    assert p.exists() and p.name == "com.granola-share.server.plist"
    assert calls[-1][:2] == ["launchctl", "bootstrap"] and (tmp_path / "home" / "logs").is_dir()
    text = p.read_text()
    assert f"<key>WorkingDirectory</key><string>{tmp_path / 'home'}</string>" in text and "PYTHONUNBUFFERED" in text
    assert autostart.installed_path("server", system="Darwin", launch_agents_dir=la) == p
    assert autostart.uninstall("server", system="Darwin", launch_agents_dir=la, run_cmd=run) is True
    assert not p.exists()
    assert autostart.uninstall("server", system="Darwin", launch_agents_dir=la, run_cmd=run) is False
    assert autostart.installed_path("server", system="Darwin", launch_agents_dir=la) is None


def test_install_windows_and_linux(tmp_path):
    run = lambda a, capture_output, text: None
    p = autostart.install("client", tmp_path / "h", system="Windows", startup_dir=tmp_path / "Startup", run_cmd=run, python="C:\\py.exe")
    assert p.suffix == ".cmd" and "client" in p.read_text() and "PYTHONUNBUFFERED=1" in p.read_text()
    assert autostart.installed_path("client", system="Windows", startup_dir=tmp_path / "Startup") == p
    assert autostart.uninstall("client", system="Windows", startup_dir=tmp_path / "Startup", run_cmd=run)
    u = autostart.install("client", tmp_path / "h", system="Linux", systemd_dir=tmp_path / "sd", run_cmd=run, python="/py")
    text = u.read_text()
    assert u.suffix == ".service" and "Restart=always" in text
    assert f"WorkingDirectory={tmp_path / 'h'}" in text and "Environment=PYTHONUNBUFFERED=1" in text and " -u -m granola_share.cli " in text
    assert autostart.installed_path("client", system="Linux", systemd_dir=tmp_path / "sd") == u
    assert autostart.uninstall("client", system="Linux", systemd_dir=tmp_path / "sd", run_cmd=run)
    assert autostart.installed_path("client", system="Linux", systemd_dir=tmp_path / "sd") is None


def test_role_args_rejects_an_unknown_role(tmp_path):
    import pytest

    with pytest.raises(ValueError):
        autostart.role_args("wat", tmp_path)
    assert autostart.role_args("server", tmp_path, python="/py")[-1] == "run"
    assert autostart.role_args("server", tmp_path, python="/py")[1] == "-u"


def test_render_plist_escapes_xml_and_keeps_the_log_paths(tmp_path):
    args = autostart.role_args("client", tmp_path / "a&b", python="/py")
    plist = autostart.render_plist("com.granola-share.client", args, tmp_path / "logs" / "client.log", tmp_path / "a&b")
    assert "a&amp;b" in plist and "a&b<" not in plist
    assert f"<key>StandardOutPath</key><string>{tmp_path / 'logs' / 'client.log'}</string>" in plist
    assert f"<key>StandardErrorPath</key><string>{tmp_path / 'logs' / 'client.log'}</string>" in plist


def test_render_cmd_prefers_pythonw_when_it_exists(tmp_path):
    exe = tmp_path / "python.exe"
    exe.write_text("")
    cmd = autostart.render_cmd([str(exe), "-u", "-m", "granola_share.cli", "client", "run"], tmp_path)
    assert str(exe) in cmd
    (tmp_path / "pythonw.exe").write_text("")  # a windowless interpreter keeps the console hidden
    cmd = autostart.render_cmd([str(exe), "-u", "-m", "granola_share.cli", "client", "run"], tmp_path)
    assert str(tmp_path / "pythonw.exe") in cmd and '"-u"' in cmd and cmd.endswith("\r\n")


def test_installed_path_is_none_before_install(tmp_path):
    assert autostart.installed_path("client", system="Darwin", launch_agents_dir=tmp_path / "LA") is None
    assert autostart.installed_path("server", system="Windows", startup_dir=tmp_path / "S") is None
    assert autostart.installed_path("server", system="Linux", systemd_dir=tmp_path / "sd") is None


def test_ask_yes_no_on_linux_with_zenity(tmp_path, monkeypatch):
    calls = []

    def runner(args, capture_output, text):
        calls.append(args)
        return SimpleNamespace(returncode=5, stdout="")  # 5 = zenity's timeout

    monkeypatch.setattr("granola_share.dialogs.shutil.which", lambda name: "/usr/bin/zenity")
    assert ask_yes_no("t", "x", runner=runner, system="Linux") is None
    assert calls[0][0] == "zenity" and "--timeout=300" in calls[0]

    ok = lambda a, capture_output, text: SimpleNamespace(returncode=0, stdout="")
    assert ask_yes_no("t", "x", runner=ok, system="Linux") is True
