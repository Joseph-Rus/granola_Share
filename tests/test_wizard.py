import pytest

from granola_share.config import load_client_config, load_config
from granola_share.wizard import ScriptedPrompter, _normalize_url, client_setup, server_setup


def test_server_setup_writes_config_and_installs(tmp_path):
    io = ScriptedPrompter([
        "Fall pool",            # pool name
        "",                     # password -> generated
        str(tmp_path / "pool"), # folder
        "8790",                 # port
        "CS 101", "cs101, intro", "Intro programming",   # class 1
        "Bio 110", "", "",      # class 2
        "",                     # end classes
        "qwen3:1.7b",           # model (installed)
        False,                  # server_sync? no
        True,                   # autostart
    ])
    installed, pulled = [], []
    cfg = server_setup(tmp_path, io, ollama_models=lambda h: ["qwen3:1.7b", "llama3.2"], pull_model=lambda m: pulled.append(m) or True,
                       do_login=lambda c: pytest.fail("login should not run"), install_autostart=lambda role, home: installed.append(role) or "plist",
                       hostname=lambda: "mini.tail.ts.net")
    back = load_config(tmp_path)
    assert back.pool_name == "Fall pool" and len(back.pool_password) >= 8 and back.web_port == 8790
    assert back.class_names() == ["CS 101", "Bio 110"] and back.classes[0].aliases == ["cs101", "intro"]
    assert back.ollama_model == "qwen3:1.7b" and back.ollama_enabled and back.server_sync is False
    assert installed == ["server"] and pulled == []
    out = "\n".join(io.output)
    assert "http://mini.tail.ts.net:8790" in out and back.pool_password in out and "install.sh" in out


def test_server_setup_without_ollama_pulls_nothing_and_offers_rules(tmp_path):
    io = ScriptedPrompter(["P", "pw", str(tmp_path / "pool"), "8787", "", True, False, False])
    cfg = server_setup(tmp_path, io, ollama_models=lambda h: None, pull_model=lambda m: True, do_login=lambda c: None,
                       install_autostart=lambda r, h: "x", hostname=lambda: "h")
    assert cfg.ollama_enabled is False and cfg.classes == []
    assert "Start the server with" in "\n".join(io.output)


def test_server_setup_pulls_missing_model(tmp_path):
    io = ScriptedPrompter(["P", "pw", str(tmp_path / "pool"), "8787", "", "qwen3:4b", True, False, False])
    pulled = []
    server_setup(tmp_path, io, ollama_models=lambda h: ["llama3.2"], pull_model=lambda m: pulled.append(m) or True,
                 do_login=lambda c: None, install_autostart=lambda r, h: "x", hostname=lambda: "h")
    assert pulled == ["qwen3:4b"]


def test_client_setup_retries_then_saves(tmp_path):
    io = ScriptedPrompter([
        "mini:8787", "wrong",        # attempt 1 fails
        "http://mini:8787", "pw",    # attempt 2 ok
        "Sam",                       # name
        True,                        # ask each
        True,                        # share now
        True,                        # autostart
    ])
    calls = {"login": 0, "share": 0, "auto": []}

    def check(url, key):
        if key != "pw":
            raise RuntimeError("bad password")
        assert url == "http://mini:8787"
        return {"ok": True, "pool_name": "Fall pool", "classes": ["CS 101"]}

    cc = client_setup(tmp_path, io, check_server=check, do_login=lambda c: calls.__setitem__("login", calls["login"] + 1),
                      install_autostart=lambda r, h: calls["auto"].append(r) or "p", share_now=lambda c, log: calls.__setitem__("share", calls["share"] + 1))
    back = load_client_config(tmp_path)
    assert back.server_url == "http://mini:8787" and back.pool_key == "pw" and back.pool_name == "Fall pool"
    assert back.display_name == "Sam" and back.mode == "ask"
    assert calls == {"login": 1, "share": 1, "auto": ["client"]}
    assert "Could not connect" in "\n".join(io.output)


def test_client_setup_gives_up_after_three_failures(tmp_path):
    io = ScriptedPrompter(["a", "b", "a", "b", "a", "b"])
    with pytest.raises(SystemExit):
        client_setup(tmp_path, io, check_server=lambda u, k: (_ for _ in ()).throw(RuntimeError("no")),
                     do_login=lambda c: None, install_autostart=lambda r, h: "p", share_now=lambda c, log: None)


def test_normalize_url():
    assert _normalize_url(" mini:8787/ ") == "http://mini:8787"
    assert _normalize_url("https://x.ts.net:8787") == "https://x.ts.net:8787"


def test_pick_default_model_prefers_big_moe_models():
    from granola_share.wizard import pick_default_model

    installed = ["nomic-embed-text:latest", "llama3.2:3b", "qwen3:1.7b", "gemma4:e4b", "qwen3.6:35b-a3b"]
    assert pick_default_model(installed, "x") == "qwen3.6:35b-a3b"
    assert pick_default_model(["llama3.2:3b", "gemma4:e4b"], "x") == "gemma4:e4b"
    assert pick_default_model(["nomic-embed-text"], "fallback") == "fallback"
