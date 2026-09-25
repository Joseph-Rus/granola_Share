"""Getting the library's computer ready (Tailscale, Ollama, Windows' firewall and sleep), and the
Windows pieces around it: the background service's keep-alive loop and the app window."""

import base64
import json
from pathlib import Path
from types import SimpleNamespace

import httpx
import pytest

from granola_share import autostart, cli, dialogs, hostinfo, launcher, ollama, ready

POWERCFG = """Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced)
  Subgroup GUID: 238c9fa8-0aad-41ed-83f4-97be242c8f20  (Sleep)
    Power Setting GUID: 29f6c1db-86da-48c5-9fdb-f2b67b1f44da  (Sleep after)
      Minimum Possible Setting: 0x00000000
      Maximum Possible Setting: 0xffffffff
      Possible Settings increment: 0x00000001
      Possible Settings units: Seconds
    Current AC Power Setting Index: 0x00000708
    Current DC Power Setting Index: 0x00000384
"""


def saved(url, dest, progress):
    dest.write_bytes(b"installer")
    return dest


def ran(stdout="", code=0):
    return SimpleNamespace(stdout=stdout, stderr="", returncode=code)


def test_windows_sleep_is_read_whatever_the_language():
    assert ready.windows_sleep_minutes(lambda *a, **k: ran(POWERCFG)) == 30
    german = POWERCFG.replace("Current AC Power Setting Index", "Index der aktuellen Wechselstromeinstellung")
    assert ready.windows_sleep_minutes(lambda *a, **k: ran(german)) == 30
    never = POWERCFG.replace("0x00000708", "0x00000000")
    assert ready.windows_sleep_minutes(lambda *a, **k: ran(never)) == 0
    assert ready.windows_sleep_minutes(lambda *a, **k: ran("")) is None


def test_keep_awake_only_on_windows_and_checks_it_took():
    calls = []

    def run(args, **kw):
        calls.append(args)
        return ran(POWERCFG.replace("0x00000708", "0x00000000"))

    assert ready.keep_awake("Windows", run) is True
    assert calls[0] == ["powercfg", "/change", "standby-timeout-ac", "0"]
    assert ready.keep_awake("Darwin", run) is False


@pytest.mark.parametrize("out,code,want", [("off", 0, True), ("none", 0, False), ("8787", 0, True),
                                           ("8000,8787", 0, True), ("8000", 0, False), ("", 1, None)])
def test_firewall_status(out, code, want):
    assert ready.firewall_open(8787, lambda *a, **k: ran(out, code)) is want


def test_firewall_rule_is_narrow_and_asks_windows_for_permission():
    script = ready.firewall_script(8790)
    assert "-LocalPort 8790" in script and "100.64.0.0/10,LocalSubnet" in script and "-Action Allow" in script
    assert "Remove-NetFirewallRule" in script and "Action -eq 'Block'" in script  # Python's dismissed-prompt blocks
    seen = []

    def run(args, **kw):
        seen.append(args[-1])
        return ran("8790")

    assert ready.open_firewall(8790, run) is True
    elevate = seen[0]
    assert "-Verb RunAs" in elevate and "-EncodedCommand" in elevate
    encoded = elevate.split("'-EncodedCommand','")[1].split("'")[0]
    assert base64.b64decode(encoded).decode("utf-16-le") == script


def test_install_ollama_on_a_mac_unpacks_the_app(tmp_path, monkeypatch):
    monkeypatch.setattr(ready.os, "access", lambda *a: False)  # not an admin: ~/Applications
    fetched = []

    def fetch(url, dest, progress):
        fetched.append(url)
        progress(5, 10)
        dest.write_bytes(b"zip")
        return dest

    def run(args, **kw):
        assert args[:3] == ["ditto", "-x", "-k"]
        (Path(args[-1]) / "Ollama.app" / "Contents").mkdir(parents=True)
        return ran()

    bars = []
    assert ready.install_ollama(lambda s: None, lambda d, t: bars.append((d, t)), system="Darwin", run=run, fetch=fetch)
    assert fetched == [ready.OLLAMA_MAC] and bars == [(5, 10)]
    assert (Path.home() / "Applications" / "Ollama.app" / "Contents").is_dir()


def test_install_ollama_on_windows_runs_its_installer_quietly(tmp_path, monkeypatch):
    monkeypatch.setattr(ready.ollama, "installed", lambda system=None: True)
    runs = []
    assert ready.install_ollama(lambda s: None, None, system="Windows", fetch=saved,
                                run=lambda args, **kw: runs.append(args) or ran())
    assert runs[0][0].endswith("OllamaSetup.exe") and "/SILENT" in runs[0]


def test_tailscale_over_ssh_on_a_mac_says_what_to_do_instead(tmp_path):
    said = []
    assert not ready.install_tailscale(said.append, None, system="Darwin", remote=True,
                                       fetch=lambda *a: pytest.fail("no download over SSH"))
    assert any("tailscale.com/download/mac" in s for s in said)


def test_tailscale_on_windows_opens_its_installer_and_waits(tmp_path, monkeypatch):
    monkeypatch.setattr(ready.hostinfo, "tailscale_exe", lambda: r"C:\Program Files\Tailscale\tailscale.exe")
    started, asked = [], []
    assert ready.install_tailscale(lambda s: None, None, asked.append, system="Windows", remote=False, fetch=saved,
                                   start=started.append)
    assert started[0].endswith("tailscale-setup.exe") and asked and "Enter" in asked[0]


def test_connect_tailscale(monkeypatch):
    runs = []
    run = lambda args, **kw: runs.append(args) or ran()  # noqa: E731
    mac = {"exe": "/Applications/Tailscale.app/Contents/MacOS/Tailscale"}
    assert ready.connect_tailscale(mac, lambda s: None, system="Darwin", run=run, remote=False)
    assert runs == [["open", "-g", "-a", "Tailscale"], [mac["exe"], "up"]]
    runs.clear()
    monkeypatch.setattr(ready.os, "geteuid", lambda: 501, raising=False)
    ready.connect_tailscale({"exe": "/usr/bin/tailscale"}, lambda s: None, system="Linux", run=run, remote=True)
    assert runs == [["sudo", "/usr/bin/tailscale", "up"]]


def test_tailscale_states(tmp_path, monkeypatch):
    exe = tmp_path / "tailscale"
    exe.write_text("")
    monkeypatch.setattr(hostinfo.shutil, "which", lambda name: None)
    monkeypatch.setattr(hostinfo, "TAILSCALE_PATHS", [str(exe)])
    status = {"BackendState": "NeedsLogin", "Self": {"DNSName": "", "TailscaleIPs": []}}
    info = hostinfo.tailscale_info(lambda *a, **k: ran(json.dumps(status)))
    assert info["installed"] and info["state"] == "NeedsLogin" and not info["running"] and info["exe"] == str(exe)
    assert hostinfo.tailscale_problem(info) == "installed, but signed out"
    assert hostinfo.tailscale_problem({"installed": True, "state": "Stopped"}) == "installed, but turned off"
    assert hostinfo.tailscale_problem({"installed": False}) == "not installed"
    assert hostinfo.tailscale_problem({"installed": True, "running": True}) == ""


def test_ollama_is_found_where_its_windows_installer_puts_it(tmp_path, monkeypatch):
    monkeypatch.setenv("LOCALAPPDATA", str(tmp_path))
    monkeypatch.setattr(ollama.shutil, "which", lambda name: None)
    assert ollama.find_exe("Windows") is None and not ollama.installed("Windows")
    folder = tmp_path / "Programs" / "Ollama"
    folder.mkdir(parents=True)
    (folder / "ollama.exe").write_text("")
    (folder / "ollama app.exe").write_text("")
    assert ollama.find_exe("Windows") == str(folder / "ollama.exe")
    assert ollama.app_path("Windows") == folder / "ollama app.exe" and ollama.installed("Windows")


class FakeStream:
    def __init__(self, lines, status=200):
        self.lines, self.status_code = lines, status

    def __enter__(self):
        return self

    def __exit__(self, *a):
        return False

    def iter_lines(self):
        return iter(self.lines)


def test_pull_through_ollamas_api_reports_progress(monkeypatch):
    events = [{"status": "pulling manifest"}, {"status": "pulling a", "digest": "a", "total": 100, "completed": 50},
              {"status": "pulling b", "digest": "b", "total": 100, "completed": 100},
              {"status": "pulling a", "digest": "a", "total": 100, "completed": 100}, {"status": "success"}]
    monkeypatch.setattr(ollama.httpx, "stream", lambda *a, **k: FakeStream([json.dumps(e) for e in events]))
    seen = []
    assert ollama.pull("qwen3:1.7b", "http://x", lambda d, t: seen.append((d, t))) == (True, "")
    assert seen == [(50, 100), (150, 200), (200, 200)]
    monkeypatch.setattr(ollama.httpx, "stream",
                        lambda *a, **k: FakeStream([json.dumps({"error": "pull model manifest: file does not exist"})]))
    assert ollama.pull("nope", "http://x") == (False, "pull model manifest: file does not exist")

    def refused(*a, **k):
        raise httpx.ConnectError("connection refused")

    monkeypatch.setattr(ollama.httpx, "stream", refused)
    assert ollama.pull("x", "http://x") == (False, "connection refused")


def test_try_model(monkeypatch):
    monkeypatch.setattr(ollama.httpx, "post", lambda *a, **k: SimpleNamespace(status_code=200))
    secs, why = ollama.try_model("http://x", "m")
    assert secs is not None and why == ""
    monkeypatch.setattr(ollama.httpx, "post", lambda *a, **k: SimpleNamespace(
        status_code=500, json=lambda: {"error": "model requires more system memory (24 GiB) than is available"}, text=""))
    assert ollama.try_model("http://x", "m") == (None, "model requires more system memory (24 GiB) than is available")


# --- Windows: the background service and the app window -----------------------------------------------

def test_keep_alive_restarts_the_service_and_keeps_its_log(tmp_path):
    runs, sleeps = [], []

    def spawn(args, env, stdout, stderr, creationflags):
        runs.append((args, env.get(autostart.CHILD_ENV)))
        stdout.write("granola-share: pool 'P' on port 8787\n")
        return SimpleNamespace(wait=lambda: 1)

    autostart.keep_alive(tmp_path, "server", ["--home", str(tmp_path), "run"], spawn=spawn, sleep=sleeps.append,
                         rounds=2)
    assert len(runs) == 2 and runs[0][0][-3:] == ["--home", str(tmp_path), "run"] and runs[0][1] == "1"
    assert sleeps == [60, 60]  # it failed straight away both times: wait longer before trying again
    log = (tmp_path / "logs" / "server.log").read_text()
    assert log.count("pool 'P'") == 2 and "stopped (exit 1); starting it again in 60 s" in log


def test_windows_service_runs_under_keep_alive(tmp_path, monkeypatch):
    seen = {}
    monkeypatch.setattr(cli.platform, "system", lambda: "Windows")
    monkeypatch.setenv(autostart.SERVICE_ENV, "1")
    monkeypatch.delenv(autostart.CHILD_ENV, raising=False)
    monkeypatch.setattr(autostart, "keep_alive", lambda home, role, argv: seen.update(home=home, role=role, argv=argv))
    cli.main(["--home", str(tmp_path), "client", "run"])
    assert seen == {"home": tmp_path, "role": "client", "argv": ["--home", str(tmp_path), "client", "run"]}


def test_windows_opens_study_stash_in_its_own_window(tmp_path, monkeypatch):
    edge = tmp_path / "Microsoft" / "Edge" / "Application" / "msedge.exe"
    edge.parent.mkdir(parents=True)
    edge.write_text("")
    monkeypatch.setenv("ProgramFiles(x86)", str(tmp_path))
    spawned = []
    assert dialogs.open_window("http://127.0.0.1:8765/?t=x", spawn=lambda args, **k: spawned.append(args),
                               system="Windows")
    assert spawned == [[str(edge), "--app=http://127.0.0.1:8765/?t=x", "--window-size=1180,820"]]
    opened = []
    monkeypatch.setattr(dialogs, "open_url", opened.append)
    monkeypatch.setenv("ProgramFiles(x86)", str(tmp_path / "none"))
    for var in ("ProgramFiles", "LOCALAPPDATA"):
        monkeypatch.setenv(var, str(tmp_path / "none"))
    assert not dialogs.open_window("http://x", spawn=lambda *a, **k: pytest.fail("no browser"), system="Windows")
    assert opened == ["http://x"]


def test_windows_shortcut_has_the_study_stash_icon(tmp_path):
    scripts = []
    launcher.install(tmp_path, system="Windows", python=str(tmp_path / "python.exe"),
                     run=lambda args, **k: scripts.append(args[-1]))
    assert launcher.WINDOWS_ICON.exists() and f"IconLocation='{launcher.WINDOWS_ICON},0'" in scripts[0]


def test_icon_files_ship_with_the_package():
    for name in ("study-stash.ico", "icon.png", "apple-touch-icon.png"):
        assert (launcher.ASSETS / name).stat().st_size > 1000


def test_windows_starts_console_programs_without_a_window(monkeypatch):
    """Without a console (the background service, the app), each console program started would flash a
    window: tailscale status every few seconds made the laptop's setup page flicker."""
    import ctypes
    import subprocess

    monkeypatch.setattr(cli.platform, "system", lambda: "Windows")
    monkeypatch.setattr(ctypes, "windll", SimpleNamespace(kernel32=SimpleNamespace(GetConsoleWindow=lambda: 0)),
                        raising=False)
    seen = []
    monkeypatch.setattr(subprocess.Popen, "__init__", lambda self, *a, **kw: seen.append(kw.get("creationflags")))
    cli._no_console_flashes()
    subprocess.Popen(["tailscale", "status"])
    subprocess.Popen(["cmd"], creationflags=0x00000008)  # already detached: left alone
    assert seen == [0x08000000, 0x00000008]
