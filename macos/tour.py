"""The README's screenshots of the Mac app, with made-up lectures (the repo is public).

    sh macos/build.sh && uv run --with pillow python macos/tour.py

Serves a sample library and the laptop's page (set up, and part way through setup), runs the app's
tour mode against them (GRANOLA_SHARE_TOUR in GranolaShare.swift: the app captures its own window,
so no Screen Recording permission is needed), then gives each picture macOS's rounded corners and
shadow and writes it to docs/screenshots/.
"""

from __future__ import annotations

import json
import os
import shutil
import subprocess
import sys
import tempfile
import threading
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

import uvicorn  # noqa: E402
from PIL import Image, ImageDraw, ImageFilter  # noqa: E402

from granola_share import client_app  # noqa: E402
from granola_share.classify import Classification  # noqa: E402
from granola_share.config import UNSORTED, ClassDef, ClientConfig, Config, save_client_config  # noqa: E402
from granola_share.granola import Meeting  # noqa: E402
from granola_share.pipeline import Pipeline  # noqa: E402
from granola_share.store import Store  # noqa: E402
from granola_share.web import create_app  # noqa: E402

APP = ROOT / "dist" / "Granola Share.app" / "Contents" / "MacOS" / "Granola Share"
OUT = ROOT / "docs" / "screenshots"
LIB_PORT, MAC_PORT, SETUP_PORT = 8871, 8872, 8873
PASSWORD = "maple-otter-42"

CLASSES = [ClassDef("Biology 110", ["bio"], "Cells, genetics, and evolution"),
           ClassDef("Calculus II", ["calc"], "Integration techniques and series"),
           ClassDef("Psychology 101", ["psych"]), ClassDef("Data Structures", ["cs 201"]),
           ClassDef("World History", ["hist"])]
SUMMARY = """## Overview
How substances cross the cell membrane, and why water follows solutes.

## Key ideas
- The membrane is a **phospholipid bilayer**: small nonpolar molecules slip through, ions need proteins.
- *Diffusion* runs down a concentration gradient and costs no energy.
- **Osmosis** is water diffusing toward the side with more dissolved solute.
- Active transport (the sodium–potassium pump) spends ATP to move ions *against* their gradient.

## Formula
Osmotic pressure: $\\Pi = iMRT$, where $i$ is the number of particles per formula unit.

## Likely on the exam
1. Predict which way water moves between a hypotonic and a hypertonic solution.
2. Explain why red blood cells burst in pure water.
"""
LECTURES = [  # id, title, date, class, topics, has transcript
    ("n1", "Membranes, diffusion, and osmosis", "2026-09-24T09:00:00", "Biology 110",
     ["osmosis", "diffusion", "active transport"], True),
    ("n2", "Integration by parts", "2026-09-23T13:00:00", "Calculus II", ["integration by parts", "LIATE"], True),
    ("n3", "Classical and operant conditioning", "2026-09-23T10:00:00", "Psychology 101",
     ["Pavlov", "reinforcement"], True),
    ("n4", "Hash tables and collisions", "2026-09-22T15:00:00", "Data Structures", ["hashing", "chaining"], True),
    ("n5", "The printing press and the Reformation", "2026-09-22T11:00:00", "World History",
     ["Gutenberg", "Luther"], True),
    ("n6", "Mendel and inheritance", "2026-09-19T09:00:00", "Biology 110", ["alleles", "Punnett squares"], True),
    ("n7", "Trigonometric substitution", "2026-09-18T13:00:00", "Calculus II", ["trig substitution"], True),
    ("n8", "Study group planning", "2026-09-17T18:00:00", UNSORTED, ["exam prep"], False),
]


def library(root: Path):
    cfg = Config(home=root / "lib", pool_dir=root / "pool", pool_name="Fall 2026", pool_password=PASSWORD,
                 ollama_enabled=True, summary_model="qwen3.6:35b-a3b", ollama_model="qwen3.6:35b-a3b",
                 classes=CLASSES)
    store = Store(cfg.db_path, cfg.pool_dir)
    for nid, title, date, cls, topics, transcript in LECTURES:
        m = Meeting(id=nid, title=title, date=date, notes_markdown="## Notes\n- Granola's summary of the lecture",
                    transcript=("Professor: today we look at how water moves across the membrane.\n" * 40)
                    if transcript else "")
        store.save(m, Classification(cls, 0.93, "rules" if cls != UNSORTED else "ollama", title, topics))
        if transcript:
            store.conn.execute("UPDATE notes SET summary_md=?, summary_model=? WHERE id=?",
                               (SUMMARY if nid == "n1" else "## Overview\nNotes from the transcript.",
                                "qwen3.6:35b-a3b", nid))
    store.conn.commit()
    models = [{"name": "qwen3.6:35b-a3b", "size_gb": 23.9}, {"name": "gemma4:e4b", "size_gb": 9.6}]
    return create_app(cfg, store, Pipeline(cfg, store, log=lambda *_: None), list_models=lambda h: models,
                      tailscale=lambda: {"running": True, "dns": "mac-mini.tail5c1e2.ts.net", "ips": ["100.64.0.2"]},
                      latest=lambda *a: None)


class DemoRuntime(client_app.ClientRuntime):
    """The laptop's page without a real watcher behind it."""

    def __init__(self, home: Path, watching: bool, allowed: bool):
        super().__init__(home, log=lambda s: None)
        self._watching, self._allowed = watching, allowed

    @property
    def watching(self):
        return self._watching

    def start_watching(self):
        pass

    def copy_status(self):
        return {"available": True, "enabled": True, "allowed": self._allowed, "why": None}


def laptop(home: Path, port: int, *, set_up: bool):
    home.mkdir(parents=True)
    cc = ClientConfig(home=home, server_url=f"http://127.0.0.1:{LIB_PORT}", pool_key=PASSWORD, pool_name="Fall 2026",
                      mode="auto", display_name="me")
    save_client_config(cc)
    cc.tokens_path.write_text("{}")  # signed in to Granola
    if set_up:
        states = [("filed", True), ("filed", True), ("sent", True), ("filed", True), ("filed", True), ("filed", True)]
        seen = {}
        for i, ((nid, title, date, cls, *_), (state, _)) in enumerate(zip(LECTURES, states)):
            seen[nid] = {"decision": "shared", "title": title, "date": date[:10], "class_name": cls,
                         "at": f"2026-09-2{4 - min(i, 4)}T12:0{i}:00+00:00", "transcript_chars": 9000,
                         "filed": state == "filed"}
        seen["n1"]["filed"] = False  # the one still being written
        seen["n3"]["filed"] = True
        (home / "client_state.json").write_text(json.dumps({"seen": seen}))
    (home / "ui_port").write_text(str(port))
    rt = DemoRuntime(home, watching=set_up, allowed=set_up)
    return client_app.create_client_app(rt, port=port, check_server=lambda u, k: {"pool_name": "Fall 2026"})


def serve(app, port: int) -> None:
    threading.Thread(target=uvicorn.run, args=(app,), daemon=True,
                     kwargs={"host": "127.0.0.1", "port": port, "log_level": "warning"}).start()


# Blurred in every picture, even though the data is made up: the password, the Tailscale address and
# anything else that looks like an address, and file paths. Inputs that hold an address or password too.
BLUR = (PASSWORD.replace("-", "\\-") + r"|[a-z0-9-]+\.tail[0-9a-z]+\.ts\.net(:\d+)?|https?://[0-9.]+(:\d+)?"
        r"|\b100\.\d+\.\d+\.\d+\b|/(private|var|Users|tmp)/[^\s'\"]+")
BLUR_SELECTOR = "input[type=url], input[type=password]"


def tour(home: Path, shots: str, raw: Path, look: str = "light", engine: str | None = None) -> None:
    env = {**os.environ, "GRANOLA_SHARE_HOME": str(home), "GRANOLA_SHARE_TOUR": shots,
           "GRANOLA_SHARE_TOUR_DIR": str(raw), "GRANOLA_SHARE_APPEARANCE": look,
           "GRANOLA_SHARE_TOUR_BLUR": BLUR, "GRANOLA_SHARE_TOUR_BLUR_SELECTOR": BLUR_SELECTOR}
    if engine:
        env["GRANOLA_SHARE_ENGINE"] = engine
    subprocess.run([str(APP)], env=env, timeout=180, check=True)


def framed(src: Path, dest: Path, width: int = 1600) -> None:
    """macOS window corners and shadow on a transparent background, sized for the README."""
    win = Image.open(src).convert("RGBA")
    scale = win.width / 1180  # points to pixels (2 on a Retina screen)
    radius, pad = int(16 * scale), int(44 * scale)
    mask = Image.new("L", win.size, 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, win.width - 1, win.height - 1), radius, fill=255)
    canvas = Image.new("RGBA", (win.width + 2 * pad, win.height + 2 * pad), (0, 0, 0, 0))
    shadow = Image.new("RGBA", canvas.size, (0, 0, 0, 0))
    shadow.paste((0, 0, 0, 90), (pad, pad + int(10 * scale)), mask)
    canvas = Image.alpha_composite(canvas, shadow.filter(ImageFilter.GaussianBlur(18 * scale)))
    canvas.paste(win, (pad, pad), mask)
    edge = Image.new("RGBA", win.size, (0, 0, 0, 0))  # the thin outline macOS draws around windows
    ImageDraw.Draw(edge).rounded_rectangle((0, 0, win.width - 1, win.height - 1), radius, outline=(0, 0, 0, 40),
                                           width=max(1, int(scale)))
    canvas.alpha_composite(edge, (pad, pad))
    canvas = canvas.resize((width, round(canvas.height * width / canvas.width)), Image.LANCZOS)
    canvas.save(dest, optimize=True)


def main() -> None:
    if not APP.exists():
        sys.exit("Build the app first: sh macos/build.sh")
    work = Path(tempfile.mkdtemp(prefix="gs-tour-"))
    raw = work / "raw"
    raw.mkdir()
    serve(library(work), LIB_PORT)
    serve(laptop(work / "mac", MAC_PORT, set_up=True), MAC_PORT)
    serve(laptop(work / "setup", SETUP_PORT, set_up=False), SETUP_PORT)
    (work / "fresh").mkdir()
    (work / "fresh" / "ui_port").write_text("8874")  # nothing answers: the helper isn't installed
    time.sleep(2)

    tour(work / "mac", "this-mac=mac:/,library=library:/,lecture=library:/note/n1,settings=library:/settings", raw)
    tour(work / "setup", "setup=mac:/", raw)
    tour(work / "fresh", "welcome=mac:-", raw, engine="/nonexistent/granola-share")
    # No dark-mode pictures: captured in dark mode, the toolbar's glass buttons come out solid white.

    OUT.mkdir(parents=True, exist_ok=True)
    for png in sorted(raw.glob("*.png")):
        framed(png, OUT / png.name)
        print("wrote", (OUT / png.name).relative_to(ROOT))
    shutil.rmtree(work, ignore_errors=True)


if __name__ == "__main__":
    main()
