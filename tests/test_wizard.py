"""The two setup wizards. Both now check *which* Granola account signed in and how many notes it has,
because a wrong account with an empty workspace is the failure that silently emptied the pool.
"""

from pathlib import Path

import pytest

from granola_share.config import load_client_config, load_config
from granola_share.wizard import (ACCOUNT_LOOKBACK_DAYS, ZERO_NOTES_MSG, Prompter, ScriptedPrompter, _normalize_url,
                                  account_line, client_setup, login_and_check, resolve_pool_dir, server_setup)

ACCOUNT = {"email": "joey@example.com", "workspace": "Joey's workspace"}
OTHER = {"email": "school@example.com", "workspace": "School"}


class Recorder(ScriptedPrompter):
    """A scripted prompter that also keeps the prompt texts, so wording can be asserted on."""

    def __init__(self, answers):
        super().__init__(answers)
        self.prompts: list[str] = []

    def ask(self, text, default=None):
        self.prompts.append(text)
        return super().ask(text, default)

    def confirm(self, text, default=True):
        self.prompts.append(text)
        return super().confirm(text, default)


def healthy(url, key):
    if key != "pw":
        raise RuntimeError("bad password")
    return {"ok": True, "pool_name": "Fall pool", "classes": ["CS 101"]}


def account_checker(*answers):
    """An account_check that returns each (info, n) in turn, repeating the last one."""
    queue = list(answers)
    calls = []

    def check(cfg):
        calls.append(cfg)
        return queue.pop(0) if len(queue) > 1 else queue[0]

    return check, calls


# --- the server wizard ------------------------------------------------------------

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
                       hostname=lambda: "mini.tail.ts.net",
                       account_check=lambda c: pytest.fail("no account check without server sync"))
    back = load_config(tmp_path)
    assert back.pool_name == "Fall pool" and len(back.pool_password) >= 8 and back.web_port == 8790
    assert back.class_names() == ["CS 101", "Bio 110"] and back.classes[0].aliases == ["cs101", "intro"]
    assert back.ollama_model == "qwen3:1.7b" and back.ollama_enabled and back.server_sync is False
    assert installed == ["server"] and pulled == []
    out = "\n".join(io.output)
    assert "http://mini.tail.ts.net:8790" in out and back.pool_password in out and "install.sh" in out
    assert f"Notes will be stored in {tmp_path / 'pool'}" in out


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


def test_server_setup_with_sync_says_which_account_signed_in(tmp_path):
    io = ScriptedPrompter([
        "P", "pw", str(tmp_path / "pool"), "8787", "",  # pool, password, folder, port, no classes
        True,       # no Ollama: continue with folder/title rules
        True,       # server_sync? yes
        False,      # autostart? no
    ])
    check, calls = account_checker((ACCOUNT, 9))
    logins = []
    cfg = server_setup(tmp_path, io, ollama_models=lambda h: None, pull_model=lambda m: True,
                       do_login=lambda c: logins.append(c), install_autostart=lambda r, h: "x",
                       hostname=lambda: "h", account_check=check)
    assert cfg.server_sync is True and len(logins) == 1 and len(calls) == 1
    assert account_line(ACCOUNT, 9) in "\n".join(io.output)


def test_server_setup_offers_to_switch_an_empty_account(tmp_path):
    io = ScriptedPrompter([
        "P", "pw", str(tmp_path / "pool"), "8787", "",
        True,       # no Ollama: continue with folder/title rules
        True,       # server_sync? yes
        True,       # "Sign in as someone else?" after 0 notes
        False,      # autostart? no
    ])
    check, calls = account_checker((ACCOUNT, 0), (OTHER, 3))
    logins = []
    server_setup(tmp_path, io, ollama_models=lambda h: None, pull_model=lambda m: True,
                 do_login=lambda c: logins.append(c), install_autostart=lambda r, h: "x",
                 hostname=lambda: "h", account_check=check)
    out = "\n".join(io.output)
    assert ZERO_NOTES_MSG in out and len(logins) == 2 and len(calls) == 2
    assert account_line(OTHER, 3) in out


def test_resolve_pool_dir_makes_the_answer_absolute(tmp_path):
    assert resolve_pool_dir(str(tmp_path / "pool")) == (tmp_path / "pool").resolve()
    assert resolve_pool_dir("GranolaShare") == (Path.home() / "GranolaShare").resolve()
    assert resolve_pool_dir("  ~/Lectures  ") == (Path.home() / "Lectures").resolve()
    assert resolve_pool_dir("") == (Path.home() / "GranolaShare").resolve()


# --- the friend wizard -------------------------------------------------------------

def test_client_setup_retries_then_saves(tmp_path):
    io = Recorder([
        "mini:8787", "wrong",        # attempt 1 fails
        "http://mini:8787", "pw",    # attempt 2 ok
        "Sam",                       # name
        True,                        # review each note in the panel
        True,                        # share now
        True,                        # autostart
    ])
    calls = {"login": 0, "share": 0, "auto": []}
    check, checked = account_checker((ACCOUNT, 12))

    def check_server(url, key):
        assert url == "http://mini:8787" or key != "pw"
        return healthy(url, key)

    cc = client_setup(tmp_path, io, check_server=check_server,
                      do_login=lambda c: calls.__setitem__("login", calls["login"] + 1),
                      install_autostart=lambda r, h: calls["auto"].append(r) or "p",
                      share_now=lambda c, log: calls.__setitem__("share", calls["share"] + 1),
                      account_check=check)
    back = load_client_config(tmp_path)
    assert back.server_url == "http://mini:8787" and back.pool_key == "pw" and back.pool_name == "Fall pool"
    assert back.display_name == "Sam" and back.mode == "ask"
    assert calls == {"login": 1, "share": 1, "auto": ["client"]} and len(checked) == 1
    out = "\n".join(io.output)
    assert "Could not connect" in out
    assert account_line(ACCOUNT, 12) in out
    assert f"Control panel: {cc.panel_url}" in out and "http://127.0.0.1:8790" in out
    assert any("your-mac-mini:8787" in p for p in io.prompts)  # not a stale example hostname
    assert any(p.startswith("Review each finished note in the control panel") and "share everything automatically" in p
               for p in io.prompts)


def test_client_setup_no_review_means_auto_mode(tmp_path):
    io = ScriptedPrompter(["http://mini:8787", "pw", "Sam", False, False, False])
    check, _ = account_checker((ACCOUNT, 3))
    cc = client_setup(tmp_path, io, check_server=healthy, do_login=lambda c: None,
                      install_autostart=lambda r, h: pytest.fail("autostart declined"),
                      share_now=lambda c, log: pytest.fail("share declined"), account_check=check)
    assert cc.mode == "auto" and load_client_config(tmp_path).mode == "auto"
    assert "granola-share client run" in "\n".join(io.output)


def test_client_setup_offers_to_switch_a_zero_note_account(tmp_path):
    io = ScriptedPrompter([
        "http://mini:8787", "pw", "Sam",
        True,    # "Sign in as someone else?" after the first account has 0 notes
        True,    # review each
        False,   # share now? no
        False,   # autostart? no
    ])
    check, checked = account_checker((ACCOUNT, 0), (OTHER, 5))
    logins = []
    client_setup(tmp_path, io, check_server=healthy, do_login=lambda c: logins.append(c),
                 install_autostart=lambda r, h: "p", share_now=lambda c, log: None, account_check=check)
    out = "\n".join(io.output)
    assert len(logins) == 2 and len(checked) == 2
    assert ZERO_NOTES_MSG in out and account_line(OTHER, 5) in out


def test_client_setup_gives_up_after_three_failures(tmp_path):
    io = ScriptedPrompter(["a", "b", "a", "b", "a", "b"])
    with pytest.raises(SystemExit):
        client_setup(tmp_path, io, check_server=lambda u, k: (_ for _ in ()).throw(RuntimeError("no")),
                     do_login=lambda c: None, install_autostart=lambda r, h: "p", share_now=lambda c, log: None,
                     account_check=lambda c: pytest.fail("never gets this far"))


# --- the shared login/account check -------------------------------------------------

def test_login_and_check_reports_who_signed_in():
    io = ScriptedPrompter([])
    check, checked = account_checker((ACCOUNT, 4))
    logins = []
    info, n = login_and_check("cfg", io, do_login=lambda c: logins.append(c), account_check=check)
    assert (info, n) == (ACCOUNT, 4) and logins == ["cfg"] and checked == ["cfg"]
    assert io.output == ["  " + account_line(ACCOUNT, 4)]


def test_login_and_check_stops_when_the_user_declines_or_after_three_rounds():
    io = ScriptedPrompter([False])
    check, _ = account_checker((ACCOUNT, 0))
    logins = []
    info, n = login_and_check("cfg", io, do_login=lambda c: logins.append(c), account_check=check)
    assert n == 0 and len(logins) == 1 and ZERO_NOTES_MSG in "\n".join(io.output)

    io2 = ScriptedPrompter([True, True])  # only two questions: the third round does not ask again
    check2, checked2 = account_checker((ACCOUNT, 0))
    logins2 = []
    login_and_check("cfg", io2, do_login=lambda c: logins2.append(c), account_check=check2)
    assert len(logins2) == 3 and len(checked2) == 3 and io2.answers == []


def test_account_line_wording():
    assert account_line(ACCOUNT, 12) == ("Signed in as joey@example.com (Joey's workspace) · found 12 notes "
                                         f"in the last {ACCOUNT_LOOKBACK_DAYS} days")
    assert account_line({"email": "a@b.c"}, 1) == (f"Signed in as a@b.c · found 1 note in the last "
                                                   f"{ACCOUNT_LOOKBACK_DAYS} days")
    assert account_line(None, 0).startswith("Signed in (Granola did not say which account)")


def test_normalize_url():
    assert _normalize_url(" mini:8787/ ") == "http://mini:8787"
    assert _normalize_url("https://x.ts.net:8787") == "https://x.ts.net:8787"


def test_pick_default_model_prefers_big_moe_models():
    from granola_share.wizard import pick_default_model

    installed = ["nomic-embed-text:latest", "llama3.2:3b", "qwen3:1.7b", "gemma4:e4b", "qwen3.6:35b-a3b"]
    assert pick_default_model(installed, "x") == "qwen3.6:35b-a3b"
    assert pick_default_model(["llama3.2:3b", "gemma4:e4b"], "x") == "gemma4:e4b"
    assert pick_default_model(["nomic-embed-text"], "fallback") == "fallback"


def test_scripted_prompter_is_a_prompter():
    io = ScriptedPrompter(["x", True, "secret"])
    assert isinstance(io, Prompter)
    assert io.ask("q", "default") == "x" and io.confirm("q?") is True and io.secret("pw") == "secret"
    with pytest.raises(AssertionError):
        io.ask("nothing left")
