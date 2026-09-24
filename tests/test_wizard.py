import builtins
from types import SimpleNamespace

import pytest

from granola_share import cli, wizard
from granola_share.config import ClassDef, load_client_config, load_config
from granola_share.wizard import Prompter, ScriptedPrompter, _normalize_url, client_setup, parse_class, server_setup

MODELS = [{"name": "qwen3:1.7b", "size_gb": 1.4}, {"name": "llama3.2:latest", "size_gb": 2.0}]
TS = {"running": True, "dns": "mini.tail.ts.net", "ips": ["100.64.0.7"]}


def server_kw(**over):
    kw = dict(list_models=lambda h: MODELS, start_ollama=lambda h: False, ollama_installed=lambda: False,
              pull_model=lambda m: pytest.fail("nothing to pull"), ram_gb=lambda: 64.0,
              do_login=lambda c: pytest.fail("login should not run"), install_autostart=lambda role, home: "plist",
              tailscale=lambda: TS, port_status=lambda p: "free", wait_healthy=lambda cfg: True,
              cleanup=lambda home, log: False)
    kw.update(over)
    return kw


def test_server_setup_writes_config_and_installs(tmp_path):
    io = ScriptedPrompter([
        "Fall pool",             # pool name
        "",                      # password -> generated
        str(tmp_path / "pool"),  # folder
        "8790",                  # port
        "CS 101", "cs101, intro", "Intro programming",   # class 1
        "Bio 110", "", "",       # class 2
        "",                      # end classes
        "1",                     # summary model by number
        "",                      # sorting model = same
        False,                   # server_sync? no
        True,                    # auto update
        True,                    # autostart
    ])
    installed = []
    server_setup(tmp_path, io, **server_kw(install_autostart=lambda role, home: installed.append(role) or "plist"))
    back = load_config(tmp_path)
    assert back.pool_name == "Fall pool" and len(back.pool_password) >= 8 and back.web_port == 8790
    assert back.class_names() == ["CS 101", "Bio 110"] and back.classes[0].aliases == ["cs101", "intro"]
    assert back.summary_model == back.ollama_model == "qwen3:1.7b" and back.ollama_enabled and not back.server_sync
    assert installed == ["server"] and back.auto_update
    out = "\n".join(io.output)
    assert "http://mini.tail.ts.net:8790" in out and "http://100.64.0.7:8790" in out and back.pool_password in out
    assert "GRANOLA_SHARE_SERVER=http://mini.tail.ts.net:8790" in out and "install.ps1" in out
    assert "It's up." in out and "paid plans" in out and "Connect your laptop" in out and "friend" not in out.lower()


def test_server_setup_without_ollama_continues_on_rules(tmp_path):
    io = ScriptedPrompter(["P", "pw", str(tmp_path / "pool"), "8787", "", False, False, True, False])
    cfg = server_setup(tmp_path, io, **server_kw(list_models=lambda h: None))
    assert cfg.ollama_enabled is False and cfg.classes == []
    out = "\n".join(io.output)
    assert "https://ollama.com" in out and "Start it with" in out


def test_server_setup_starts_ollama_when_installed_but_closed(tmp_path):
    state = {"up": False}
    io = ScriptedPrompter(["P", "pw", str(tmp_path / "pool"), "8787", "", "", "", False, True, False])
    cfg = server_setup(tmp_path, io, **server_kw(
        list_models=lambda h: MODELS if state["up"] else None, ollama_installed=lambda: True,
        start_ollama=lambda h: state.update(up=True) or True))
    assert cfg.ollama_enabled and cfg.summary_model == "qwen3:1.7b"
    assert "installed but not running" in "\n".join(io.output)


def test_server_setup_pulls_a_missing_model_once(tmp_path):
    io = ScriptedPrompter(["P", "pw", str(tmp_path / "pool"), "8787", "", "qwen3:4b", "", True, False, True, False])
    pulled = []
    server_setup(tmp_path, io, **server_kw(list_models=lambda h: [{"name": "llama3.2:latest", "size_gb": 2.0}],
                                           pull_model=lambda m: pulled.append(m) or True))
    assert pulled == ["qwen3:4b"]


def test_server_setup_reasks_bad_or_busy_port(tmp_path):
    io = ScriptedPrompter(["P", "pw", str(tmp_path / "pool"), "eighty", "8787", "8788", "", "", "", False, True, False])
    cfg = server_setup(tmp_path, io, **server_kw(port_status=lambda p: "busy" if p == 8787 else "free"))
    assert cfg.web_port == 8788
    out = "\n".join(io.output)
    assert "1024 to 65535" in out and "Another app is using port 8787" in out


def test_server_setup_login_failure_does_not_abort(tmp_path):
    io = ScriptedPrompter(["P", "pw", str(tmp_path / "pool"), "8787", "", "", "", True, False, True, False])

    def boom(cfg):
        raise RuntimeError("timed out waiting for the browser callback")

    cfg = server_setup(tmp_path, io, **server_kw(do_login=boom))
    assert cfg.server_sync and "granola-share login" in "\n".join(io.output)


def test_server_setup_from_flags_needs_no_keyboard(tmp_path, monkeypatch):
    monkeypatch.setattr(builtins, "input", lambda *a: pytest.fail("asked a question"))
    io = Prompter({"pool_name": "Fall", "password": "pw", "pool_dir": str(tmp_path / "pool"), "port": "8791",
                   "classes": [parse_class("CS 101=cs101,intro")], "summary_model": "llama3.2:latest",
                   "server_sync": False, "autostart": False}, assume_defaults=True)
    cfg = server_setup(tmp_path, io, **server_kw())
    assert cfg.pool_name == "Fall" and cfg.web_port == 8791 and cfg.class_names() == ["CS 101"]
    assert cfg.classes[0].aliases == ["cs101", "intro"]
    assert cfg.summary_model == cfg.ollama_model == "llama3.2:latest" and cfg.auto_update


def test_prompter_without_terminal_explains_itself(monkeypatch):
    def eof(*a):
        raise EOFError

    monkeypatch.setattr(builtins, "input", eof)
    with pytest.raises(SystemExit, match="--yes"):
        Prompter().ask("Name")


def client_kw(**over):
    kw = dict(do_login=lambda c: None, is_logged_in=lambda c: False, install_autostart=lambda r, h: "p",
              share_now=lambda c, log: None, service_status=lambda r: "running", cleanup=lambda home, log: False,
              mac=False)
    kw.update(over)
    return kw


def test_client_setup_retries_then_saves(tmp_path):
    io = ScriptedPrompter([
        "mini:8787", "wrong",        # attempt 1 fails
        "http://mini:8787", "pw",    # attempt 2 ok
        True,                        # ask each
        True,                        # share now
        True,                        # auto update
        True,                        # autostart
    ])
    calls = {"login": 0, "share": 0, "auto": []}

    def check(url, key):
        if key != "pw":
            raise RuntimeError("wrong password")
        assert url == "http://mini:8787"
        return {"ok": True, "pool_name": "Lecture notes", "classes": ["CS 101"]}

    client_setup(tmp_path, io, check_server=check, **client_kw(
        do_login=lambda c: calls.__setitem__("login", calls["login"] + 1),
        install_autostart=lambda r, h: calls["auto"].append(r) or "p",
        share_now=lambda c, log: calls.__setitem__("share", calls["share"] + 1)))
    back = load_client_config(tmp_path)
    assert back.server_url == "http://mini:8787" and back.pool_key == "pw" and back.pool_name == "Lecture notes"
    assert back.display_name and back.mode == "ask" and back.auto_update  # the name is just the login name now
    assert calls == {"login": 1, "share": 1, "auto": ["client"]}
    out = "\n".join(io.output)
    assert "Could not connect" in out and "Double-check the password" in out and "watcher is running" in out
    assert "friend" not in out.lower() and "pool" not in out.lower()


def test_client_setup_keeps_a_working_login(tmp_path):
    io = ScriptedPrompter(["http://mini:8787", "pw", True, False, False, True, False])
    client_setup(tmp_path, io, check_server=lambda u, k: {"pool_name": "P"},
                 **client_kw(is_logged_in=lambda c: True, do_login=lambda c: pytest.fail("no second login")))
    assert load_client_config(tmp_path).mode == "auto"


def test_client_setup_survives_login_and_first_share_failures(tmp_path):
    io = ScriptedPrompter(["http://mini:8787", "pw", False, True, True, True, True])

    def login_fails(cc):
        raise RuntimeError("timed out waiting for the browser callback")

    def share_fails(cc, log):
        raise RuntimeError("get_meetings failed")

    installed = []
    client_setup(tmp_path, io, check_server=lambda u, k: {"pool_name": "P"},
                 **client_kw(do_login=login_fails, share_now=share_fails,
                             install_autostart=lambda r, h: installed.append(r) or "p"))
    out = "\n".join(io.output)
    assert "Signing in failed" in out and "granola-share client login" in out and "Checking Granola failed" in out
    assert installed == ["client"]


def test_client_setup_gives_up_after_three_failures(tmp_path):
    io = ScriptedPrompter(["a", "b", "a", "b", "a", "b"])

    def unreachable(u, k):
        raise RuntimeError("could not reach")

    with pytest.raises(SystemExit):
        client_setup(tmp_path, io, check_server=unreachable, **client_kw())
    assert "Tailscale" in "\n".join(io.output)


def test_invite_env_vars_prefill_client_setup(monkeypatch):
    monkeypatch.setenv("GRANOLA_SHARE_SERVER", "http://mini:8787")
    monkeypatch.setenv("GRANOLA_SHARE_KEY", "pw")
    io = wizard.make_prompter(SimpleNamespace(yes=False, a_server=None, a_key=None, a_name="Sam"))
    assert io.preset == {"server": "http://mini:8787", "key": "pw", "name": "Sam"} and not io.assume_defaults


def test_cli_setup_flags_reach_the_wizard(tmp_path, monkeypatch):
    seen = {}
    monkeypatch.setattr(wizard, "server_setup", lambda home, io: seen.update(home=home, io=io))
    cli.main(["--home", str(tmp_path), "setup", "--yes", "--pool-name", "Fall", "--class", "CS 101=cs101",
              "--class", "Bio 110", "--no-autostart", "--summary-model", "big:35b"])
    io = seen["io"]
    assert seen["home"] == tmp_path and io.assume_defaults
    assert io.preset["pool_name"] == "Fall" and io.preset["autostart"] is False and io.preset["summary_model"] == "big:35b"
    assert [c.name for c in io.preset["classes"]] == ["CS 101", "Bio 110"] and "pull" not in io.preset

    monkeypatch.setattr(wizard, "client_setup", lambda home, io: seen.update(io=io))
    cli.main(["--home", str(tmp_path), "client", "setup", "--server", "mini:8787", "--mode", "auto", "--share-now"])
    assert seen["io"].preset == {"server": "mini:8787", "ask_each": False, "share_now": True}


def test_normalize_url_and_parse_class():
    assert _normalize_url(" mini:8787/ ") == "http://mini:8787"
    assert _normalize_url("https://x.ts.net:8787") == "https://x.ts.net:8787"
    assert parse_class("Chem 1A") == ClassDef("Chem 1A", [])


def test_pick_default_model_prefers_big_moe_models():
    from granola_share.ollama import has_model, pick_default_model, recommended_model

    installed = ["nomic-embed-text:latest", "llama3.2:3b", "qwen3:1.7b", "gemma4:e4b", "qwen3.6:35b-a3b"]
    assert pick_default_model(installed, "x") == "qwen3.6:35b-a3b"
    assert pick_default_model(["llama3.2:3b", "gemma4:e4b"], "x") == "gemma4:e4b"
    assert pick_default_model(["nomic-embed-text"], "fallback") == "fallback"
    assert recommended_model(64) == "qwen3.6:35b-a3b" and recommended_model(16) == "gemma4:e4b"
    assert recommended_model(8) == "qwen3:1.7b"
    assert has_model(["llama3.2:latest"], "llama3.2") and not has_model(["llama3.2:latest"], "llama3.2:3b")
