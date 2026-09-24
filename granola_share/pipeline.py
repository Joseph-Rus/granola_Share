"""Turn queued notes into filed ones: write our summary from the transcript, sort, save.

Runs as a background thread inside `granola-share run`, so uploads from your laptop return right
away even when a big model takes a few minutes per lecture.
"""

from __future__ import annotations

import dataclasses
import json
import threading
import time
import traceback
from pathlib import Path

from .classify import classify, classify_with_ollama, ollama_chat
from .config import Config
from .store import Classification, Store
from .summarize import summarize_transcript, wants_summary


class Pipeline:
    def __init__(self, cfg: Config, store: Store, *, chat=None, summarize=None, log=print):
        self.cfg = cfg
        self.store = store
        self.chat = chat  # classify's Ollama call (tests swap it)
        self.summarize = summarize or summarize_transcript
        self.log = log
        self._wake = threading.Event()
        self.current: str | None = None  # id of the note being written right now

    def process(self, row) -> Path | None:
        m = self.store.meeting(row)
        summary, model, error = "", "", ""
        if wants_summary(m, self.cfg):
            model = self.cfg.effective_summary_model
            started = time.time()
            try:
                summary = self.summarize(m, self.cfg)
                self.log(f"[pipeline] summarized '{m.title}' with {model} in {time.time() - started:.0f}s")
            except Exception as e:
                error = f"Summary with {model} failed: {e}"
                self.log(f"[pipeline] {error}")
                summary, model = "", ""
        if row["classified_by"] == "human" and row["class_name"]:
            # A person filed it: rewriting the summary never undoes that.
            c = Classification(row["class_name"], 1.0, "human", row["lecture_title"] or "",
                               json.loads(row["topics"] or "[]"))
        else:
            # Sort on our summary when we have one: it is cleaner than the raw transcript.
            basis = dataclasses.replace(m, notes_markdown=summary) if summary else m
            c = classify(basis, self.cfg, self.chat)
            if c.by in ("folder", "rules") and self.cfg.ollama_enabled and self.cfg.classes:
                # The folder or title picked the class; still let the model name the lecture and tag topics.
                described = classify_with_ollama(basis, self.cfg.classes, self.cfg, self.chat or ollama_chat)
                if described:
                    c.lecture_title = described.lecture_title or c.lecture_title
                    c.topics = described.topics
        path = self.store.finish(row, m, c, summary, model, error, keep_granola=self.cfg.keep_granola_notes)
        if path is None:
            self.log(f"[pipeline] '{m.title}' changed while it was being written; not saving the old result")
        else:
            self.log(f"[pipeline] filed '{m.title}' → {c.class_name} ({c.by} {c.confidence:.2f})")
        return path

    def run_pending(self, stop: threading.Event | None = None) -> int:
        done = 0
        while not (stop and stop.is_set()):
            row = self.store.claim_next()
            if row is None:
                break
            self.current = row["id"]
            try:
                self.process(row)
                done += 1
            except Exception as e:
                self.store.mark_failed(row["id"], str(e))
                self.log(f"[pipeline] could not file {row['id']}: {e}\n{traceback.format_exc()}")
            finally:
                self.current = None
        return done

    def wake(self) -> None:
        self._wake.set()

    def run_forever(self, stop: threading.Event) -> None:
        n = self.store.reset_working()
        if n:
            self.log(f"[pipeline] resuming {n} note(s) interrupted by a restart")
        while not stop.is_set():
            try:
                self.run_pending(stop)
            except Exception as e:
                self.log(f"[pipeline] error: {e}")
            self._wake.wait(30)
            self._wake.clear()

    def start(self, stop: threading.Event) -> threading.Thread:
        t = threading.Thread(target=self.run_forever, args=(stop,), name="pipeline", daemon=True)
        t.start()
        return t
