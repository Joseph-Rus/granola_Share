"""The library's setup as a page (library_setup.py): what the Study Stash Library app shows, so nobody
needs a terminal."""

import time

import pytest
from fastapi.testclient import TestClient

from granola_share import library_setup
from granola_share.config import load_config

H = {"X-Granola-Share": "1"}
TS = {"installed": True, "running": True, "state": "Running", "dns": "pc.tail.ts.net", "ips": ["100.64.0.9"]}
MODELS = [{"name": "qwen3:1.7b", "size_gb": 1.4}]


def fakes(**over):
    kw = dict(list_models=lambda host: MODELS, ollama_installed=lambda: True, start_ollama=lambda host: True,
              install_ollama=lambda log, bar, system=None: pytest.fail("Ollama is there"),
              pull_model=lambda model, host, bar: (True, ""), try_model=lambda host, model: (2.0, ""),
              tailscale=lambda: TS, install_tailscale=lambda *a, **k: pytest.fail("Tailscale is there"),
              open_tailscale=lambda system=None: True, firewall=lambda port: True,
              open_firewall=lambda port: True, sleep_minutes=lambda system=None: 0, keep_awake=lambda system=None: True,
              ram_gb=lambda: 32.0, disk_free=lambda: 200.0, port_status=lambda port: "free",
              install_autostart=lambda role, home: None, wait_healthy=lambda cfg: True, system="Darwin")
    kw.update(over)
    return kw


def client(tmp_path, **over):
    s = library_setup.LibrarySetup(tmp_path / "home", **fakes(**over))
    c = TestClient(library_setup.create_setup_app(s, port=8764, extra_hosts=("testserver",)), follow_redirects=False)
    token = (tmp_path / "home" / "ui_token").read_text().strip()
    assert c.get(f"/?t={token}").status_code == 303
    c.cookies.set("gs_app", token)
    return s, c


def settle(s, name, timeout=5):
    deadline = time.time() + timeout
    while s.jobs.running(name) and time.time() < deadline:
        time.sleep(0.02)
    return s.jobs.state.get(name, {})


def test_setup_from_start_to_finish_without_a_terminal(tmp_path):
    started = []
    s, c = client(tmp_path, install_autostart=lambda role, home: started.append(role))
    page = c.get("/").text
    assert "Set up your library" in page and "connected" in page and "running" in page
    assert not (tmp_path / "home" / "config.toml").exists()
    r = c.post("/api/library", headers=H, json={"name": "Fall 2026", "password": "maple-otter",
                                               "folder": str(tmp_path / "notes"), "port": "8791"})
    assert r.status_code == 200 and r.json()["message"] == "Saved."
    assert c.post("/api/classes", headers=H, json={"name": "Bio 110", "aliases": "bio, bio110"}).status_code == 200
    assert c.post("/api/classes", headers=H, json={"name": "Calc II"}).status_code == 200
    assert c.post("/api/classes", headers=H, json={"remove": "1"}).json()["message"] == "Removed Calc II."
    assert c.post("/api/models", headers=H, json={"summary": "qwen3:1.7b", "sort": ""}).status_code == 200
    assert "answered in 2 s" in settle(s, "try")["note"]
    assert not (tmp_path / "home" / "config.toml").exists()  # nothing is set up until you finish
    assert c.post("/api/finish", headers=H).status_code == 200
    assert settle(s, "finish")["note"] == "Your library is ready." and started == ["server"]
    cfg = load_config(tmp_path / "home")
    assert cfg.pool_name == "Fall 2026" and cfg.pool_password == "maple-otter" and cfg.web_port == 8791
    assert cfg.class_names() == ["Bio 110"] and cfg.classes[0].aliases == ["bio", "bio110"]
    assert cfg.summary_model == cfg.ollama_model == "qwen3:1.7b" and cfg.ollama_enabled
    done = c.get("/").text
    assert "http://pc.tail.ts.net:8791" in done and "maple-otter" in done and "Study-Stash-Laptop-Setup.exe" in done
    assert not (tmp_path / "home" / "setup_draft.json").exists()


def test_only_this_computers_app_can_use_it(tmp_path):
    s, c = client(tmp_path)
    assert c.post("/api/finish").status_code == 403  # no header: a cross-site form can't send it
    c.cookies.clear()
    assert c.get("/").status_code == 403 and c.post("/api/library", headers=H, json={}).status_code == 403
    assert c.get("/", headers={"host": "evil.example:8764"}).status_code == 403  # DNS rebinding


def test_bad_answers_say_what_to_fix(tmp_path):
    s, c = client(tmp_path, port_status=lambda port: "busy")
    r = c.post("/api/library", headers=H, json={"name": "L", "password": "pw", "folder": str(tmp_path), "port": "80"})
    assert r.status_code == 400 and "at least 4" in r.json()["detail"]
    r = c.post("/api/library", headers=H, json={"name": "L", "password": "long enough", "folder": str(tmp_path),
                                               "port": "8787"})
    assert r.status_code == 400 and "Another app is using port 8787" in r.json()["detail"]
    assert c.post("/api/finish", headers=H).status_code == 400  # nothing saved yet
    assert c.post("/api/classes", headers=H, json={"name": ""}).status_code == 400


def test_answers_survive_closing_the_window(tmp_path):
    s, c = client(tmp_path)
    c.post("/api/library", headers=H, json={"name": "Spring", "password": "tulip-2027", "folder": str(tmp_path / "n"),
                                            "port": "8792"})
    c.post("/api/classes", headers=H, json={"name": "Chem 101"})
    again = library_setup.LibrarySetup(tmp_path / "home", **fakes())
    assert again.cfg.pool_name == "Spring" and again.cfg.web_port == 8792 and again.cfg.class_names() == ["Chem 101"]
    assert again.draft["library"]


def test_missing_ollama_and_tailscale_get_installed_from_the_page(tmp_path):
    state = {"ollama": False, "tailscale": False}

    def install_ollama(log, bar, system=None):
        bar(50, 100)
        state["ollama"] = True
        return True

    def install_tailscale(log, bar, ask, system=None, remote=None):
        state["tailscale"] = True
        return True

    s, c = client(tmp_path, list_models=lambda host: MODELS if state["ollama"] else None,
                  ollama_installed=lambda: state["ollama"], install_ollama=install_ollama,
                  tailscale=lambda: TS if state["tailscale"] else {"installed": False, "running": False},
                  install_tailscale=install_tailscale)
    page = c.get("/").text
    assert "Install Ollama" in page and "Install Tailscale" in page
    assert c.post("/api/ollama", headers=H).status_code == 200
    assert settle(s, "ollama")["note"] == "Ollama is running."
    assert c.post("/api/tailscale", headers=H).status_code == 200
    assert "installer is open" in settle(s, "tailscale")["note"]
    s._checked = None
    page = c.get("/").text
    assert "Install Ollama" not in page and "Install Tailscale" not in page


def test_a_missing_model_downloads_then_answers_once(tmp_path):
    have = {"models": []}

    def pull(model, host, bar):
        bar(1, 2)
        bar(2, 2)
        have["models"] = [{"name": model, "size_gb": 2.0}]
        return True, ""

    s, c = client(tmp_path, list_models=lambda host: have["models"], pull_model=pull)
    c.post("/api/library", headers=H, json={"name": "L", "password": "long enough", "folder": str(tmp_path / "n"),
                                            "port": "8793"})
    assert c.post("/api/models", headers=H, json={"summary": "gemma4:e4b"}).json()["message"] == "Downloading gemma4:e4b."
    assert settle(s, "pull")["note"] == "Downloaded gemma4:e4b."
    time.sleep(0.05)
    assert "answered" in settle(s, "try")["note"]


def test_windows_asks_about_its_firewall_and_sleep(tmp_path):
    opened = []
    s, c = client(tmp_path, system="Windows", firewall=lambda port: False, sleep_minutes=lambda system=None: 30,
                  open_firewall=lambda port: opened.append(port) or True)
    c.post("/api/library", headers=H, json={"name": "L", "password": "long enough", "folder": str(tmp_path / "n"),
                                            "port": "8794"})
    page = c.get("/").text
    assert "Let my laptop in" in page and "Keep it awake while plugged in" in page
    c.post("/api/firewall", headers=H)
    assert "can reach port 8794" in settle(s, "firewall")["note"] and opened == [8794]
    assert "stays awake" in c.post("/api/awake", headers=H).json()["message"]
