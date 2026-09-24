"""Configuration: TOML files under the granola-share home directory.

config.toml  — the library (the Mac mini, or whichever computer keeps it)
client.toml  — the laptop you record on
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
    """The library: where lectures are summarized, sorted, and served."""

    home: Path
    pool_dir: Path
    pool_name: str = "Lecture notes"
    poll_interval_seconds: int = 300
    web_host: str = "0.0.0.0"
    web_port: int = 8787
    pool_password: str = ""
    admin_password: str = ""  # unlocks Settings in the web UI; the server machine itself is always admin
    mcp_url: str = MCP_URL
    oauth_callback_port: int = 3334
    oauth_prompt: str = "login"  # always show Granola's account picker; blank = let the sign-in page decide
    server_sync: bool = False  # also pull the server's own Granola account
    auto_update: bool = True
    ollama_enabled: bool = True
    ollama_host: str = "http://localhost:11434"
    ollama_model: str = "qwen3.6:35b-a3b"  # sorts notes into classes
    min_confidence: float = 0.6
    include_transcripts: bool = True
    summary_enabled: bool = True  # write our own summary from the transcript when there is one
    summary_model: str = ""  # blank = same as ollama_model
    summary_max_context: int = 32768
    keep_granola_notes: bool = False  # also keep Granola's own summary when we wrote one
    classes: list[ClassDef] = field(default_factory=list)

    @property
    def effective_summary_model(self) -> str:
        return self.summary_model or self.ollama_model

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
    """The laptop you record on: watches your Granola account and sends finished lectures to the library."""

    home: Path
    server_url: str = ""
    pool_key: str = ""
    pool_name: str = ""
    display_name: str = ""
    mode: str = "auto"  # auto (send every lecture) | ask (Send/Skip popup each time)
    poll_interval_seconds: int = 180
    include_transcripts: bool = True
    share_lookback_days: int = 7
    dialog_timeout_seconds: int = 300
    auto_update: bool = True
    # macOS: press Granola's own "Copy transcript" when a lecture ends. Off unless you turn it on: it
    # automates the Granola app, which may go against Granola's terms of service.
    copy_transcripts: bool = False
    mcp_url: str = MCP_URL
    oauth_callback_port: int = 3334
    oauth_prompt: str = "login"  # always show Granola's account picker, so the right account signs in

    @property
    def config_path(self) -> Path:
        return self.home / "client.toml"

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
        "# Your laptop and browser use this. Blank = no login (only safe if nothing else can reach this computer).",
        f"pool_password = {_toml_value(cfg.pool_password)}",
        "# From 0.2: a second password that also logs in to the web UI. Not needed any more.",
        f"admin_password = {_toml_value(cfg.admin_password)}",
        "# true = this server also pulls notes from its own Granola account (needs `granola-share login`).",
        f"server_sync = {_toml_value(cfg.server_sync)}",
        "# Install new releases automatically (checked every few hours by the background service).",
        f"auto_update = {_toml_value(cfg.auto_update)}",
        f"include_transcripts = {_toml_value(cfg.include_transcripts)}",
        f"mcp_url = {_toml_value(cfg.mcp_url)}",
        f"oauth_callback_port = {_toml_value(cfg.oauth_callback_port)}",
        "# \"login\" always shows Granola's account picker when signing in. Blank = let the sign-in page decide.",
        f"oauth_prompt = {_toml_value(cfg.oauth_prompt)}",
        "",
        "[ollama]",
        f"enabled = {_toml_value(cfg.ollama_enabled)}",
        f"host = {_toml_value(cfg.ollama_host)}",
        "# Model that sorts notes into classes.",
        f"model = {_toml_value(cfg.ollama_model)}",
        "# Below this confidence a note goes to Unsorted for a human to file.",
        f"min_confidence = {_toml_value(cfg.min_confidence)}",
        "",
        "[summary]",
        "# Write our own lecture notes from the transcript instead of using Granola's summary.",
        "# Granola only hands out transcripts on paid plans; notes without one keep Granola's summary.",
        f"enabled = {_toml_value(cfg.summary_enabled)}",
        "# Ollama model that writes the summary. Blank = same as the sorting model.",
        f"model = {_toml_value(cfg.summary_model)}",
        "# Largest context (tokens) to ask for; longer transcripts are summarized in parts, then merged.",
        f"max_context = {_toml_value(cfg.summary_max_context)}",
        "# true = also keep Granola's own summary in the note file.",
        f"keep_granola_notes = {_toml_value(cfg.keep_granola_notes)}",
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
    cfg.config_path.write_text(dump_config(cfg), encoding="utf-8")
    os.chmod(cfg.config_path, 0o600)
    return cfg.config_path


def load_config(home: Path | None = None) -> Config:
    home = (home or DEFAULT_HOME).expanduser()
    path = home / "config.toml"
    data: dict = {}
    if path.exists():
        with path.open("rb") as fh:
            data = tomllib.load(fh)
    ollama = data.get("ollama", {})
    summary = data.get("summary", {})
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
        pool_name=str(data.get("pool_name", "Lecture notes")),
        poll_interval_seconds=int(data.get("poll_interval_seconds", 300)),
        web_host=str(data.get("web_host", "0.0.0.0")),
        web_port=int(data.get("web_port", 8787)),
        pool_password=str(data.get("pool_password", "")),
        admin_password=str(data.get("admin_password", "")),
        mcp_url=str(data.get("mcp_url", MCP_URL)),
        oauth_callback_port=int(data.get("oauth_callback_port", 3334)),
        oauth_prompt=str(data.get("oauth_prompt", "login")),
        server_sync=bool(data.get("server_sync", False)),
        auto_update=bool(data.get("auto_update", True)),
        ollama_enabled=bool(ollama.get("enabled", True)),
        ollama_host=str(ollama.get("host", "http://localhost:11434")),
        ollama_model=str(ollama.get("model", "qwen3.6:35b-a3b")),
        min_confidence=float(ollama.get("min_confidence", 0.6)),
        include_transcripts=bool(data.get("include_transcripts", True)),
        summary_enabled=bool(summary.get("enabled", True)),
        summary_model=str(summary.get("model", "")),
        summary_max_context=int(summary.get("max_context", 32768)),
        keep_granola_notes=bool(summary.get("keep_granola_notes", False)),
        classes=classes,
    )


def dump_client_config(cc: ClientConfig) -> str:
    lines = [
        "# granola-share laptop config. Change it in the Study Stash app, or rerun `granola-share client setup`.",
        f"server_url = {_toml_value(cc.server_url)}",
        f"pool_key = {_toml_value(cc.pool_key)}",
        f"pool_name = {_toml_value(cc.pool_name)}",
        f"display_name = {_toml_value(cc.display_name)}",
        '# "auto" sends every finished lecture; "ask" pops up Send/Skip for each one.',
        f"mode = {_toml_value(cc.mode)}",
        f"poll_interval_seconds = {_toml_value(cc.poll_interval_seconds)}",
        f"include_transcripts = {_toml_value(cc.include_transcripts)}",
        f"share_lookback_days = {_toml_value(cc.share_lookback_days)}",
        f"dialog_timeout_seconds = {_toml_value(cc.dialog_timeout_seconds)}",
        "# Install new releases automatically (checked every few hours by the background watcher).",
        f"auto_update = {_toml_value(cc.auto_update)}",
        "# macOS: copy each transcript from the Granola window while it's in front (Granola's API only shares",
        "# transcripts on paid plans). Needs Accessibility access for python3.12.",
        f"copy_transcripts = {_toml_value(cc.copy_transcripts)}",
        f"mcp_url = {_toml_value(cc.mcp_url)}",
        f"oauth_callback_port = {_toml_value(cc.oauth_callback_port)}",
        "# \"login\" always shows Granola's account picker when signing in, so the right account connects.",
        f"oauth_prompt = {_toml_value(cc.oauth_prompt)}",
    ]
    return "\n".join(lines) + "\n"


def save_client_config(cc: ClientConfig) -> Path:
    cc.home.mkdir(parents=True, exist_ok=True)
    cc.config_path.write_text(dump_client_config(cc), encoding="utf-8")
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
        mode=str(data.get("mode", "auto")),
        poll_interval_seconds=int(data.get("poll_interval_seconds", 180)),
        include_transcripts=bool(data.get("include_transcripts", True)),
        share_lookback_days=int(data.get("share_lookback_days", 7)),
        dialog_timeout_seconds=int(data.get("dialog_timeout_seconds", 300)),
        auto_update=bool(data.get("auto_update", True)),
        copy_transcripts=bool(data.get("copy_transcripts", False)),
        mcp_url=str(data.get("mcp_url", MCP_URL)),
        oauth_callback_port=int(data.get("oauth_callback_port", 3334)),
        oauth_prompt=str(data.get("oauth_prompt", "login")),
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
