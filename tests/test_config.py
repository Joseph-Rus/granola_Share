from granola_share.config import (ClassDef, ClientConfig, Config, dump_config, load_client_config, load_config,
                                  save_client_config, save_config)


def test_server_config_roundtrip(tmp_path):
    cfg = Config(home=tmp_path, pool_dir=tmp_path / "pool", pool_name='Fall "26" pool', pool_password="s3cret",
                 web_port=9000, server_sync=True, ollama_model="qwen3:1.7b", min_confidence=0.7,
                 classes=[ClassDef("CS 101", ["cs101", "intro"], "Intro to programming"), ClassDef("Bio 110")])
    path = save_config(cfg)
    assert path == tmp_path / "config.toml"
    back = load_config(tmp_path)
    assert back.pool_name == 'Fall "26" pool' and back.pool_password == "s3cret" and back.web_port == 9000
    assert back.server_sync is True and back.ollama_model == "qwen3:1.7b" and back.min_confidence == 0.7
    assert [c.name for c in back.classes] == ["CS 101", "Bio 110"]
    assert back.classes[0].aliases == ["cs101", "intro"] and back.classes[0].description == "Intro to programming"
    assert back.classes[1].aliases == []
    assert "[[classes]]" in dump_config(cfg)


def test_client_config_roundtrip(tmp_path):
    cc = ClientConfig(home=tmp_path, server_url="http://mini:8787", pool_key="pw", pool_name="Pool",
                      display_name="Sam", mode="auto", poll_interval_seconds=60)
    save_client_config(cc)
    back = load_client_config(tmp_path)
    assert back.server_url == "http://mini:8787" and back.pool_key == "pw" and back.display_name == "Sam"
    assert back.mode == "auto" and back.poll_interval_seconds == 60 and back.pool_name == "Pool"
    assert back.tokens_path == tmp_path / "tokens.json"


def test_missing_files_give_defaults(tmp_path):
    assert load_config(tmp_path).web_port == 8787
    assert load_client_config(tmp_path).mode == "auto"
