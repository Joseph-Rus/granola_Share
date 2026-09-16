"""`granola-share doctor` / `client doctor`: every quiet failure gets a line of its own.

All IO is injected, so each branch is exercised with fakes and nothing here touches the network,
the real Granola account, or anything outside tmp_path.
"""

from pathlib import Path
from types import SimpleNamespace

from granola_share.config import ClassDef, ClientConfig, Config, save_client_config, save_config
from granola_share.doctor import (Check, _model_installed, client_doctor, dir_writable, failed, last_log_line,
                                  render, server_doctor, store_note_count)

ACCOUNT = {"email": "joey@example.com", "workspace": "Joey's workspace", "workspace_id": "ws-77",
           "scopes": ["personal"], "raw": {}}

HEALTH_OK = (200, {"ok": True, "pool_name": "Fall pool", "classes": ["CS 101"], "notes": 7})
STATE_OK = (200, {"counts": {"waiting": 2}, "mode": "ask"})
TAGS_OK = (200, {"models": [{"name": "qwen3:1.7b"}]})


class FakeHTTP:
    """Maps a URL fragment to (status, json body) or to an exception to raise."""

    def __init__(self, routes):
        self.routes = dict(routes)
        self.calls: list[tuple[str, dict | None, float]] = []

    def __call__(self, url, headers=None, timeout=10):
        self.calls.append((url, headers, timeout))
        for fragment, answer in self.routes.items():
            if fragment in url:
                if isinstance(answer, Exception):
                    raise answer
                status, body = answer
                return SimpleNamespace(status_code=status, json=lambda body=body: body)
        raise ConnectionError(f"Connection refused: {url}")


def by_name(checks) -> dict:
    return {c.name: c for c in checks}


# --- the friend's laptop -----------------------------------------------------------

def client_config(tmp_path, *, config=True, tokens=True, log="[client] queued 'CS101 lec 1'\n", **kw):
    kw.setdefault("server_url", "http://mini:8787")
    cc = ClientConfig(home=tmp_path, pool_key="pw", pool_name="Fall pool", **kw)
    if config:
        save_client_config(cc)
    if tokens:
        cc.tokens_path.write_text('{"refresh_token": "x"}')
    if log is not None:
        cc.log_dir.mkdir(parents=True, exist_ok=True)
        (cc.log_dir / "client.log").write_text(log)
    return cc


def run_client(cc, *, routes=None, account=(ACCOUNT, 12), port_taken=False, autostart="/tmp/plist"):
    http = FakeHTTP(routes if routes is not None else {"/api/health": HEALTH_OK, "/api/state": STATE_OK})
    checks = client_doctor(cc, http_get=http, account_check=lambda cfg: account,
                           port_open=lambda host, port, timeout=1.0: port_taken,
                           autostart_installed=lambda role: Path(autostart) if autostart else None)
    return checks, http


def test_client_doctor_all_green(tmp_path):
    cc = client_config(tmp_path)
    checks, http = run_client(cc)
    got = by_name(checks)
    assert [c.ok for c in checks] == [True] * len(checks)
    assert failed(checks) is False
    assert str(cc.config_path) in got["Config"].detail and "mode ask" in got["Config"].detail
    assert "pool 'Fall pool'" in got["Pool server"].detail and "7 notes in the pool" in got["Pool server"].detail
    assert got["Granola login"].detail == "Signed in as joey@example.com · Joey's workspace"
    assert got["Notes in Granola"].detail == "found 12 notes in the last 30 days"
    assert "answering at http://127.0.0.1:8790" in got["Control panel"].detail
    assert "2 waiting" in got["Control panel"].detail
    assert "installed: /tmp/plist" in got["Autostart"].detail
    assert "client.log" in got["Log file"].detail and "queued" in got["Log file"].detail

    out = render(checks)
    assert out.splitlines()[0].startswith("✓ Config") and out.strip().endswith("All good.")
    assert "→" not in out  # no hints when everything is fine
    # the pool password is sent as a bearer token, the panel needs none
    assert http.calls[0][1] == {"Authorization": "Bearer pw"} and http.calls[1][1] == {}


def test_client_doctor_without_a_config(tmp_path):
    cc = client_config(tmp_path, config=False, server_url="")
    got = by_name(run_client(cc)[0])
    assert got["Config"].ok is False and "does not exist" in got["Config"].detail
    assert "client setup" in got["Config"].hint
    assert got["Pool server"].ok is None and "no server_url" in got["Pool server"].detail

    cc2 = client_config(tmp_path / "two", server_url="")
    got2 = by_name(run_client(cc2)[0])
    assert got2["Config"].ok is False and "no server_url" in got2["Config"].detail


def test_client_doctor_server_unreachable_and_wrong_password(tmp_path):
    cc = client_config(tmp_path)
    got = by_name(run_client(cc, routes={"/api/state": STATE_OK})[0])  # nothing answers /api/health
    check = got["Pool server"]
    assert check.ok is False and "could not reach http://mini:8787/api/health" in check.detail
    assert "Tailscale" in check.hint

    got = by_name(run_client(cc, routes={"/api/health": (401, {}), "/api/state": STATE_OK})[0])
    assert got["Pool server"].ok is False and "rejected the pool password" in got["Pool server"].detail
    assert "pool owner" in got["Pool server"].hint

    got = by_name(run_client(cc, routes={"/api/health": (500, {}), "/api/state": STATE_OK})[0])
    assert got["Pool server"].ok is False and "unexpected response 500" in got["Pool server"].detail


def test_client_doctor_not_logged_in(tmp_path):
    cc = client_config(tmp_path, tokens=False)
    got = by_name(run_client(cc)[0])
    assert got["Granola login"].ok is False and got["Granola login"].detail == "not signed in"
    assert got["Granola login"].hint == "run `granola-share client login`"
    assert got["Notes in Granola"].ok is None and "skipped" in got["Notes in Granola"].detail


def test_client_doctor_tokens_that_no_longer_work(tmp_path):
    cc = client_config(tmp_path)
    got = by_name(run_client(cc, account=(None, 0))[0])
    assert got["Granola login"].ok is False and "expired session" in got["Granola login"].detail
    assert got["Notes in Granola"].ok is None


def test_client_doctor_logged_in_with_zero_notes(tmp_path):
    cc = client_config(tmp_path)
    got = by_name(run_client(cc, account=(ACCOUNT, 0))[0])
    assert got["Granola login"].ok is True
    notes = got["Notes in Granola"]
    assert notes.ok is False and "0 notes in the last 30 days for joey@example.com" in notes.detail
    assert "different Google account" in notes.hint
    assert failed(run_client(cc, account=(ACCOUNT, 0))[0]) is True

    one = by_name(run_client(cc, account=(ACCOUNT, 1))[0])
    assert one["Notes in Granola"].detail == "found 1 note in the last 30 days"


def test_client_doctor_panel_not_running_or_port_taken(tmp_path):
    cc = client_config(tmp_path)
    got = by_name(run_client(cc, routes={"/api/health": HEALTH_OK})[0])
    panel = got["Control panel"]
    assert panel.ok is False and "not running at http://127.0.0.1:8790" in panel.detail
    assert "granola-share client run" in panel.hint

    got = by_name(run_client(cc, routes={"/api/health": HEALTH_OK}, port_taken=True)[0])
    panel = got["Control panel"]
    assert panel.ok is False and "port 8790 is taken by something that is not the panel" in panel.detail
    assert "panel_port" in panel.hint

    off = client_config(tmp_path / "off", panel_enabled=False)
    got = by_name(run_client(off, routes={"/api/health": HEALTH_OK})[0])
    assert got["Control panel"].ok is None and "panel_enabled = false" in got["Control panel"].detail


def test_client_doctor_autostart_and_log_gaps(tmp_path):
    cc = client_config(tmp_path, log=None)
    got = by_name(run_client(cc, autostart=None)[0])
    assert got["Autostart"].ok is False and got["Autostart"].detail == "not installed"
    assert "--role client" in got["Autostart"].hint
    assert got["Log file"].ok is None and "no log yet" in got["Log file"].detail

    empty = client_config(tmp_path / "empty", log="\n\n")
    got = by_name(run_client(empty)[0])
    assert got["Log file"].ok is None and "is empty" in got["Log file"].detail


# --- the pool server ---------------------------------------------------------------

def server_config(tmp_path, *, config=True, tokens=True, **kw):
    cfg = Config(home=tmp_path / "home", pool_dir=tmp_path / "pool", pool_name="Fall pool", pool_password="pw",
                 ollama_enabled=True, ollama_model="qwen3:1.7b", classes=[ClassDef("CS 101", ["cs101"])])
    for k, v in kw.items():
        setattr(cfg, k, v)
    cfg.home.mkdir(parents=True, exist_ok=True)
    if config:
        save_config(cfg)
    if tokens:
        cfg.tokens_path.write_text('{"refresh_token": "x"}')
    return cfg


def run_server(cfg, *, routes=None, account=(ACCOUNT, 12), writable=True, notes=7, autostart="/tmp/plist"):
    http = FakeHTTP(routes if routes is not None else {"/api/health": HEALTH_OK, "/api/tags": TAGS_OK})
    checks = server_doctor(cfg, http_get=http, account_check=lambda c: account,
                           writable=lambda p: writable, note_count=lambda c: notes,
                           autostart_installed=lambda role: Path(autostart) if autostart else None)
    return checks, http


def test_server_doctor_all_green(tmp_path):
    cfg = server_config(tmp_path)
    checks, http = run_server(cfg)
    got = by_name(checks)
    assert failed(checks) is False
    assert got["Config"].ok is True and "pool 'Fall pool'" in got["Config"].detail
    assert got["Pool folder"].ok is True and "(writable)" in got["Pool folder"].detail
    assert got["Web server"].ok is True and "port 8787" in got["Web server"].detail
    assert got["Ollama"].ok is True and "model qwen3:1.7b installed" in got["Ollama"].detail
    assert got["Classes"].ok is True and got["Classes"].detail == "CS 101"
    assert got["Server sync"].ok is None and "friends push their own notes" in got["Server sync"].detail
    assert got["Notes in Granola"].ok is None
    assert got["Autostart"].ok is True
    assert got["Notes in the pool"].ok is True and got["Notes in the pool"].detail == "7 notes filed"
    assert http.calls[0][1] == {"Authorization": "Bearer pw"}  # checked with its own password


def test_server_doctor_config_and_pool_folder_problems(tmp_path):
    cfg = server_config(tmp_path, config=False)
    got = by_name(run_server(cfg)[0])
    assert got["Config"].ok is False and "does not exist" in got["Config"].detail

    relative = server_config(tmp_path, pool_dir=Path("GranolaShare"))
    got = by_name(run_server(relative)[0])
    assert got["Pool folder"].ok is False and "is not an absolute path" in got["Pool folder"].detail
    assert "full path" in got["Pool folder"].hint

    got = by_name(run_server(server_config(tmp_path), writable=False)[0])
    assert got["Pool folder"].ok is False and "is not writable" in got["Pool folder"].detail


def test_server_doctor_web_port_problems(tmp_path):
    cfg = server_config(tmp_path)
    got = by_name(run_server(cfg, routes={"/api/tags": TAGS_OK})[0])
    assert got["Web server"].ok is False and "nothing answering on port 8787" in got["Web server"].detail
    assert "granola-share run" in got["Web server"].hint

    got = by_name(run_server(cfg, routes={"/api/health": (401, {}), "/api/tags": TAGS_OK})[0])
    assert got["Web server"].ok is False and "rejects this config's password" in got["Web server"].detail

    got = by_name(run_server(cfg, routes={"/api/health": (503, {}), "/api/tags": TAGS_OK})[0])
    assert got["Web server"].ok is False and "unexpected response 503" in got["Web server"].detail


def test_server_doctor_ollama_problems(tmp_path):
    cfg = server_config(tmp_path)
    got = by_name(run_server(cfg, routes={"/api/health": HEALTH_OK})[0])
    assert got["Ollama"].ok is False and "not reachable at http://localhost:11434" in got["Ollama"].detail
    assert "ollama.com" in got["Ollama"].hint

    other = {"/api/health": HEALTH_OK, "/api/tags": (200, {"models": [{"name": "llama3.2:3b"}]})}
    got = by_name(run_server(cfg, routes=other)[0])
    assert got["Ollama"].ok is False and "model qwen3:1.7b is not installed" in got["Ollama"].detail
    assert "have: llama3.2:3b" in got["Ollama"].detail and got["Ollama"].hint == "run `ollama pull qwen3:1.7b`"

    off = server_config(tmp_path, ollama_enabled=False)
    got = by_name(run_server(off)[0])
    assert got["Ollama"].ok is None and "folder/title rules only" in got["Ollama"].detail


def test_server_doctor_no_classes_and_no_notes(tmp_path):
    cfg = server_config(tmp_path, classes=[])
    got = by_name(run_server(cfg, notes=0)[0])
    assert got["Classes"].ok is False and "everything lands in Unsorted" in got["Classes"].detail
    assert got["Notes in the pool"].ok is None and "nothing shared yet" in got["Notes in the pool"].detail

    got = by_name(run_server(cfg, notes=None)[0])
    assert got["Notes in the pool"].ok is False and "could not open" in got["Notes in the pool"].detail


def test_server_doctor_with_server_sync_on(tmp_path):
    cfg = server_config(tmp_path, server_sync=True)
    got = by_name(run_server(cfg)[0])
    assert got["Server sync"].ok is True and "Signed in as joey@example.com" in got["Server sync"].detail
    assert got["Notes in Granola"].ok is True

    got = by_name(run_server(cfg, account=(ACCOUNT, 0))[0])
    assert got["Server sync"].ok is True and got["Notes in Granola"].ok is False
    assert "granola-share login" in got["Notes in Granola"].hint

    got = by_name(run_server(cfg, account=(None, 0))[0])
    assert got["Server sync"].ok is False and "expired session" in got["Server sync"].detail

    never = server_config(tmp_path / "fresh", server_sync=True, tokens=False)
    got = by_name(run_server(never)[0])
    assert got["Server sync"].ok is False and got["Server sync"].detail == "not signed in"
    assert got["Server sync"].hint == "run `granola-share login`"


# --- output and the small IO helpers ------------------------------------------------

def test_render_lines_marks_hints_and_the_failure_count():
    checks = [Check("Config", True, "fine"),
              Check("Pool server", False, "could not reach", "is the server running?"),
              Check("Ollama", None, "disabled", "a hint nobody needs"),
              Check("Autostart", False, "not installed", "install it")]
    out = render(checks).splitlines()
    assert out[0] == "✓ Config       fine"
    assert out[1] == "✗ Pool server  could not reach"
    assert out[2].strip() == "→ is the server running?"
    assert out[3].startswith("– Ollama") and out[4].strip() == "→ a hint nobody needs"
    assert out[-1] == "2 problems found."
    assert failed(checks) is True

    assert render([Check("Config", False, "gone")]).splitlines()[-1] == "1 problem found."
    assert render([]).strip() == "All good."


def test_model_installed():
    assert _model_installed("qwen3:1.7b", ["qwen3:1.7b", "llama3.2:3b"])
    assert _model_installed("qwen3", ["qwen3:1.7b"])          # a bare name matches any tag
    assert _model_installed("llama3.2", ["llama3.2:latest"])
    assert not _model_installed("qwen3:4b", ["qwen3:1.7b"])
    assert not _model_installed("qwen3", [])


def test_last_log_line(tmp_path):
    assert last_log_line(tmp_path / "nope.log") is None
    (tmp_path / "empty.log").write_text("\n \n")
    assert last_log_line(tmp_path / "empty.log") == ""
    (tmp_path / "c.log").write_text("first\nlast line\n\n")
    assert last_log_line(tmp_path / "c.log") == "last line"


def test_dir_writable(tmp_path):
    assert dir_writable(tmp_path / "pool") is True
    assert (tmp_path / "pool").is_dir() and not list((tmp_path / "pool").iterdir())  # probe cleaned up
    blocker = tmp_path / "a-file"
    blocker.write_text("not a directory")
    assert dir_writable(blocker / "pool") is False


def test_store_note_count(tmp_path):
    cfg = server_config(tmp_path, config=False)
    assert store_note_count(cfg) == 0
    blocker = tmp_path / "blocked"
    blocker.write_text("not a directory")
    broken = Config(home=blocker / "home", pool_dir=tmp_path / "pool")  # the db cannot be opened at all
    assert store_note_count(broken) is None
