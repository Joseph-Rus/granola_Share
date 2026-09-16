"""Configuration: TOML files under the granola-share home directory.

config.toml  — the server (pool) settings
client.toml  — a friend's laptop client settings
"""

from __future__ import annotations

import json
import os
import tomllib
from dataclasses import dataclass, field
from pathlib import Path

DEFAULT_HOME = Path(os.environ.get("GRANOLA_SHARE_HOME", "~/.granola-share")).expanduser()
UNSORTED = "Unsorted"
MCP_URL = "https://mcp.granola.ai/mcp"


@dataclass
class ClassDef:
    name: str
    aliases: list[str] = field(default_factory=list)
    description: str = ""


@dataclass
class Config:
    """Server / pool configuration."""

    home: Path
    pool_dir: Path
    pool_name: str = "Lecture notes pool"
    poll_interval_seconds: int = 300
    web_host: str = "0.0.0.0"
    web_port: int = 8787
    pool_password: str = ""
    mcp_url: str = MCP_URL
    oauth_callback_port: int = 3334
    server_sync: bool = False  # also pull the server's own Granola account
    ollama_enabled: bool = True
    ollama_host: str = "http://localhost:11434"
    ollama_model: str = "qwen3.6:35b-a3b"
    min_confidence: float = 0.6
    include_transcripts: bool = True
    # OAuth "prompt" value sent on login. "login" forces the account picker so a cached browser
    # session cannot silently sign you into the wrong Google account. Blank = let the server decide.
    oauth_prompt: str = "login"
    classes: list[ClassDef] = field(default_factory=list)

    @property
    def db_path(self) -> Path:
        return self.home / "state.db"

    @property
    def tokens_path(self) -> Path:
        return self.home / "tokens.json"

    @property
    def client_path(self) -> Path:
        return self.home / "oauth_client.json"

    @property
    def config_path(self) -> Path:
        return self.home / "config.toml"

    @property
    def debug_dir(self) -> Path:
        return self.home / "debug"

    @property
    def log_dir(self) -> Path:
        return self.home / "logs"

    def class_names(self) -> list[str]:
        return [c.name for c in self.classes]


@dataclass
class ClientConfig:
    """A friend's laptop: watches their Granola account and pushes approved notes."""

    home: Path
    server_url: str = ""
    pool_key: str = ""
    pool_name: str = ""
    display_name: str = ""
    mode: str = "ask"  # ask (queue in the control panel) | auto (share everything) | dialog (native popup)
    poll_interval_seconds: int = 180
    include_transcripts: bool = True
    share_lookback_days: int = 7
    dialog_timeout_seconds: int = 300
    mcp_url: str = MCP_URL
    oauth_callback_port: int = 3334
    oauth_prompt: str = "login"
    # The local control panel: http://127.0.0.1:<panel_port>, served by `client run`.
    panel_enabled: bool = True
    panel_port: int = 8790
    notifications: bool = True  # desktop notification when notes are waiting / shared

    @property
    def config_path(self) -> Path:
        return self.home / "client.toml"

    @property
    def queue_dir(self) -> Path:
        return self.home / "queue"

    @property
    def panel_url(self) -> str:
        return f"http://127.0.0.1:{self.panel_port}"

    @property
    def state_path(self) -> Path:
        return self.home / "client_state.json"

    @property
    def tokens_path(self) -> Path:
        return self.home / "tokens.json"

    @property
    def client_path(self) -> Path:
        return self.home / "oauth_client.json"

    @property
    def log_dir(self) -> Path:
        return self.home / "logs"


# --- TOML writing (stdlib has no writer; our schema is flat enough) ----------

def _toml_value(v) -> str:
    if isinstance(v, bool):
        return "true" if v else "false"
    if isinstance(v, (int, float)):
        return str(v)
    if isinstance(v, (list, tuple)):
        return "[" + ", ".join(_toml_value(x) for x in v) + "]"
    return json.dumps(str(v), ensure_ascii=True)


def dump_config(cfg: Config) -> str:
    lines = [
        "# granola-share server config. Edit freely, or rerun `granola-share setup`.",
        f"pool_name = {_toml_value(cfg.pool_name)}",
        f"pool_dir = {_toml_value(str(cfg.pool_dir))}",
        f"poll_interval_seconds = {_toml_value(cfg.poll_interval_seconds)}",
        f"web_host = {_toml_value(cfg.web_host)}",
        f"web_port = {_toml_value(cfg.web_port)}",
        "# Friends need this to open the web UI and to push notes. Blank = no login.",
        f"pool_password = {_toml_value(cfg.pool_password)}",
        "# true = this server also pulls notes from its own Granola account (needs `granola-share login`).",
        f"server_sync = {_toml_value(cfg.server_sync)}",
        f"include_transcripts = {_toml_value(cfg.include_transcripts)}",
        f"mcp_url = {_toml_value(cfg.mcp_url)}",
        f"oauth_callback_port = {_toml_value(cfg.oauth_callback_port)}",
        '# "login" forces the account picker at sign-in so a cached session cannot pick the wrong account.',
        f"oauth_prompt = {_toml_value(cfg.oauth_prompt)}",
        "",
        "[ollama]",
        f"enabled = {_toml_value(cfg.ollama_enabled)}",
        f"host = {_toml_value(cfg.ollama_host)}",
        f"model = {_toml_value(cfg.ollama_model)}",
        "# Below this confidence a note goes to Unsorted for a human to file.",
        f"min_confidence = {_toml_value(cfg.min_confidence)}",
        "",
        "# Classes notes get sorted into. Aliases match Granola folder names and note titles",
        "# before the model is asked.",
    ]
    for c in cfg.classes:
        lines += [
            "",
            "[[classes]]",
            f"name = {_toml_value(c.name)}",
            f"aliases = {_toml_value(c.aliases)}",
            f"description = {_toml_value(c.description)}",
        ]
    return "\n".join(lines) + "\n"


def save_config(cfg: Config) -> Path:
    cfg.home.mkdir(parents=True, exist_ok=True)
    cfg.config_path.write_text(dump_config(cfg))
    return cfg.config_path


def load_config(home: Path | None = None) -> Config:
    home = (home or DEFAULT_HOME).expanduser()
    path = home / "config.toml"
    data: dict = {}
    if path.exists():
        with path.open("rb") as fh:
            data = tomllib.load(fh)
    ollama = data.get("ollama", {})
    classes = [
        ClassDef(
            name=str(c["name"]),
            aliases=[str(a) for a in c.get("aliases", [])],
            description=str(c.get("description", "")),
        )
        for c in data.get("classes", [])
    ]
    return Config(
        home=home,
        pool_dir=Path(data.get("pool_dir", "~/GranolaShare")).expanduser(),
        pool_name=str(data.get("pool_name", "Lecture notes pool")),
        poll_interval_seconds=int(data.get("poll_interval_seconds", 300)),
        web_host=str(data.get("web_host", "0.0.0.0")),
        web_port=int(data.get("web_port", 8787)),
        pool_password=str(data.get("pool_password", "")),
        mcp_url=str(data.get("mcp_url", MCP_URL)),
        oauth_callback_port=int(data.get("oauth_callback_port", 3334)),
        server_sync=bool(data.get("server_sync", False)),
        ollama_enabled=bool(ollama.get("enabled", True)),
        ollama_host=str(ollama.get("host", "http://localhost:11434")),
        ollama_model=str(ollama.get("model", "qwen3.6:35b-a3b")),
        min_confidence=float(ollama.get("min_confidence", 0.6)),
        include_transcripts=bool(data.get("include_transcripts", True)),
        oauth_prompt=str(data.get("oauth_prompt", "login")),
        classes=classes,
    )


def dump_client_config(cc: ClientConfig) -> str:
    lines = [
        "# granola-share client config. Rerun `granola-share client setup` to change.",
        f"server_url = {_toml_value(cc.server_url)}",
        f"pool_key = {_toml_value(cc.pool_key)}",
        f"pool_name = {_toml_value(cc.pool_name)}",
        f"display_name = {_toml_value(cc.display_name)}",
        '# "ask" queues each finished note in the control panel for your Share/Skip;',
        '# "auto" shares everything; "dialog" uses the old native popup instead of the panel.',
        f"mode = {_toml_value(cc.mode)}",
        f"poll_interval_seconds = {_toml_value(cc.poll_interval_seconds)}",
        f"include_transcripts = {_toml_value(cc.include_transcripts)}",
        f"share_lookback_days = {_toml_value(cc.share_lookback_days)}",
        f"dialog_timeout_seconds = {_toml_value(cc.dialog_timeout_seconds)}",
        f"mcp_url = {_toml_value(cc.mcp_url)}",
        f"oauth_callback_port = {_toml_value(cc.oauth_callback_port)}",
        f"oauth_prompt = {_toml_value(cc.oauth_prompt)}",
        "# The control panel runs at http://127.0.0.1:<panel_port> while `client run` is watching.",
        f"panel_enabled = {_toml_value(cc.panel_enabled)}",
        f"panel_port = {_toml_value(cc.panel_port)}",
        f"notifications = {_toml_value(cc.notifications)}",
    ]
    return "\n".join(lines) + "\n"


def save_client_config(cc: ClientConfig) -> Path:
    cc.home.mkdir(parents=True, exist_ok=True)
    cc.config_path.write_text(dump_client_config(cc))
    os.chmod(cc.config_path, 0o600)
    return cc.config_path


def load_client_config(home: Path | None = None) -> ClientConfig:
    home = (home or DEFAULT_HOME).expanduser()
    path = home / "client.toml"
    data: dict = {}
    if path.exists():
        with path.open("rb") as fh:
            data = tomllib.load(fh)
    return ClientConfig(
        home=home,
        server_url=str(data.get("server_url", "")),
        pool_key=str(data.get("pool_key", "")),
        pool_name=str(data.get("pool_name", "")),
        display_name=str(data.get("display_name", "")),
        mode=str(data.get("mode", "ask")),
        poll_interval_seconds=int(data.get("poll_interval_seconds", 180)),
        include_transcripts=bool(data.get("include_transcripts", True)),
        share_lookback_days=int(data.get("share_lookback_days", 7)),
        dialog_timeout_seconds=int(data.get("dialog_timeout_seconds", 300)),
        mcp_url=str(data.get("mcp_url", MCP_URL)),
        oauth_callback_port=int(data.get("oauth_callback_port", 3334)),
        oauth_prompt=str(data.get("oauth_prompt", "login")),
        panel_enabled=bool(data.get("panel_enabled", True)),
        panel_port=int(data.get("panel_port", 8790)),
        notifications=bool(data.get("notifications", True)),
    )


def write_example_config(home: Path | None = None) -> Path:
    """`granola-share init`: write a starter config.toml if none exists."""
    home = (home or DEFAULT_HOME).expanduser()
    path = home / "config.toml"
    if not path.exists():
        cfg = Config(home=home, pool_dir=Path("~/GranolaShare").expanduser(), pool_password="change-me",
                     classes=[ClassDef("Example 101", ["ex101"], "Replace me with a real class")])
        save_config(cfg)
    return path
