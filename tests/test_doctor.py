from types import SimpleNamespace

from granola_share import __version__, doctor
from granola_share.config import ClassDef, ClientConfig, Config, save_client_config, save_config


def server_cfg(tmp_path, **kw):
    cfg = Config(home=tmp_path / "home", pool_dir=tmp_path / "pool", pool_password="pw", ollama_model="qwen3:1.7b",
                 summary_model="big:35b", classes=[ClassDef("CS 101")])
    for k, v in kw.items():
        setattr(cfg, k, v)
    save_config(cfg)
    return cfg


def by_name(checks):
    return {c.name: c for c in checks}


def healthy(url, headers, timeout):
    return SimpleNamespace(status_code=200, json=lambda: {"ok": True, "version": __version__})


def down(url, headers, timeout):
    raise ConnectionError("refused")


def test_server_all_good(tmp_path):
    cfg = server_cfg(tmp_path)
    checks = doctor.server_checks(
        cfg, http_get=healthy, service_status=lambda r: "running", latest=lambda: None, system="Linux",
        list_models=lambda h: [{"name": "qwen3:1.7b", "size_gb": 1.4}, {"name": "big:35b", "size_gb": 23}],
        tailscale=lambda: {"running": True, "dns": "mini.ts.net", "ips": ["100.1.1.1"]})
    assert all(c.state == doctor.OK for c in checks), [c for c in checks if c.state != doctor.OK]
    assert "mini.ts.net:8787" in by_name(checks)["Tailscale"].detail


def test_server_problems_come_with_fixes(tmp_path):
    cfg = server_cfg(tmp_path, classes=[])
    (cfg.log_dir).mkdir(parents=True)
    (cfg.log_dir / "server.log").write_text("OSError: [Errno 48] address already in use\n")
    checks = by_name(doctor.server_checks(
        cfg, http_get=down, service_status=lambda r: "stopped", latest=lambda: None, system="Darwin",
        list_models=lambda h: [{"name": "qwen3:1.7b", "size_gb": 1.4}], tailscale=lambda: {"installed": False},
        sleep_minutes=lambda: 10))
    assert checks["Web server"].state == doctor.FAIL and "address already in use" in checks["Web server"].fix
    assert checks["Background service"].state == doctor.FAIL
    assert checks["Summary model"].state == doctor.FAIL and "ollama pull big:35b" in checks["Summary model"].fix
    assert checks["Sorting model"].state == doctor.OK
    assert checks["Classes"].state == doctor.WARN and checks["Tailscale"].state == doctor.WARN
    assert checks["Sleep"].state == doctor.WARN and "Energy" in checks["Sleep"].fix
    out = doctor.format_checks("t", list(checks.values()))
    assert "Web server" in out and "ollama pull big:35b" in out


def test_server_notices_ollama_down_and_old_version_running(tmp_path):
    cfg = server_cfg(tmp_path)
    old = lambda url, headers, timeout: SimpleNamespace(status_code=200, json=lambda: {"ok": True})
    checks = by_name(doctor.server_checks(cfg, http_get=old, service_status=lambda r: "running", latest=lambda: None,
                                          list_models=lambda h: None, tailscale=lambda: {"running": False, "installed": True},
                                          system="Linux"))
    assert checks["Web server"].state == doctor.WARN and "restart" in checks["Web server"].fix
    assert checks["Ollama"].state == doctor.FAIL


def test_server_says_whether_ollama_needs_installing_or_opening(tmp_path):
    cfg = server_cfg(tmp_path)
    kw = dict(http_get=healthy, service_status=lambda r: "running", latest=lambda: None, list_models=lambda h: None,
              tailscale=lambda: {"installed": True, "running": False, "state": "NeedsLogin"}, system="Darwin",
              sleep_minutes=lambda: 0)
    missing = by_name(doctor.server_checks(cfg, ollama_installed=lambda: False, **kw))
    assert "not installed" in missing["Ollama"].detail and "granola-share setup" in missing["Ollama"].fix
    closed = by_name(doctor.server_checks(cfg, ollama_installed=lambda: True, **kw))
    assert "installed, but not answering" in closed["Ollama"].detail and "open -a Ollama" in closed["Ollama"].fix
    assert "signed out" in closed["Tailscale"].detail and "sign in" in closed["Tailscale"].fix


def test_windows_library_checks_the_firewall_and_sleep(tmp_path):
    cfg = server_cfg(tmp_path)
    checks = by_name(doctor.server_checks(
        cfg, http_get=healthy, service_status=lambda r: "running", latest=lambda: None, system="Windows",
        list_models=lambda h: [{"name": "qwen3:1.7b", "size_gb": 1.4}, {"name": "big:35b", "size_gb": 23}],
        tailscale=lambda: {"installed": True, "running": True, "dns": "pc.ts.net", "ips": []},
        firewall=lambda port: False, sleep_minutes=lambda: 30))
    assert checks["Firewall"].state == doctor.WARN and "8787" in checks["Firewall"].detail
    assert checks["Sleep"].state == doctor.WARN and "Never" in checks["Sleep"].fix
    fine = by_name(doctor.server_checks(
        cfg, http_get=healthy, service_status=lambda r: "running", latest=lambda: None, system="Windows",
        list_models=lambda h: [{"name": "qwen3:1.7b", "size_gb": 1.4}, {"name": "big:35b", "size_gb": 23}],
        tailscale=lambda: {"installed": True, "running": True, "dns": "pc.ts.net", "ips": []},
        firewall=lambda port: True, sleep_minutes=lambda: 0))
    assert "Firewall" not in fine and "Sleep" not in fine


def test_client_checks(tmp_path):
    cc = ClientConfig(home=tmp_path, server_url="http://mini:8787", pool_key="pw", pool_name="Fall")
    save_client_config(cc)
    assert by_name(doctor.client_checks(cc, check_server=lambda u, k: {}, latest=lambda: None,
                                        service_status=lambda r: "running"))["Granola"].state == doctor.FAIL
    cc.tokens_path.write_text("{}")
    checks = by_name(doctor.client_checks(
        cc, check_server=lambda u, k: {"pool_name": "Fall"}, probe=lambda c: (4, False),
        service_status=lambda r: "running", latest=lambda: None, system="Linux"))
    assert checks["Library"].state == doctor.OK and "4 notes" in checks["Granola"].detail
    assert checks["Transcripts"].state == doctor.WARN and "paid" in checks["Transcripts"].fix

    def unreachable(u, k):
        raise RuntimeError("could not reach http://mini:8787/api/health")

    checks = by_name(doctor.client_checks(cc, check_server=unreachable, probe=lambda c: (0, True),
                                          service_status=lambda r: "missing", latest=lambda: None, system="Linux"))
    assert checks["Library"].state == doctor.FAIL and "Tailscale" in checks["Library"].fix
    assert checks["Transcripts"].state == doctor.OK and checks["Background watcher"].state == doctor.WARN


def test_run_with_nothing_set_up(tmp_path):
    said = []
    assert doctor.run(tmp_path, print_fn=said.append) == 1 and "granola-share setup" in said[0]


def test_mac_sleep_parse():
    out = SimpleNamespace(stdout=" standby 1\n sleep                1 (sleep prevented by coreaudiod)\n displaysleep 10\n")
    assert doctor.mac_sleep_minutes(runner=lambda *a, **k: out) == 1
    never = SimpleNamespace(stdout=" sleep 0\n")
    assert doctor.mac_sleep_minutes(runner=lambda *a, **k: never) == 0
