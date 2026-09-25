"""Write lecture notes from the transcript with a local Ollama model.

Granola's own summary comes from whatever model Granola runs. This lets you pick
the model (usually a big local one) and write the notes from the raw transcript instead.
Transcripts longer than the model's context are summarized in parts, then merged.
"""

from __future__ import annotations

import re
from typing import Callable

import httpx

from .config import Config
from .granola import Meeting

CHARS_PER_TOKEN = 3.5  # rough, for English speech
# Below this (a couple of minutes of speech) there's too little to fill the notes: small models then
# pad and loop. Such lectures keep Granola's own summary.
MIN_TRANSCRIPT_CHARS = 1500
# Good notes are well under 2,000 tokens. Without a cap, a small model that starts repeating itself
# never stops: Ollama keeps shifting its context and generating (39,000 tokens seen, from llama3.2:3b).
MAX_NOTES_TOKENS = 4096

STRUCTURE = """Use exactly this structure, and skip any section the lecture has nothing for:

## Overview
Two to four sentences: what the lecture covered and how it fits the course.

## Key concepts
Bullets. Bold the term, then explain it in one or two sentences the way the lecturer did.

## Details and examples
Worked examples, derivations, formulas (LaTeX in $...$), code, and demonstrations, in the order they were taught.

## Announcements
Deadlines, exams, assignments, and readings, with dates exactly as said.

## Review questions
Three to five questions a student should be able to answer after this lecture."""

RULES = """Rules:
- Use only what is in the transcript. Never invent facts, dates, or examples.
- The transcript comes from speech recognition: fix obvious mis-hearings of technical terms, and skip filler, small talk, and audio problems.
- Write in the language of the lecture. Output Markdown only, with no preamble."""

# (cfg, model, prompt, num_ctx) -> text
ChatFn = Callable[[Config, str, str, int], str]


class RunawayOutput(RuntimeError):
    """The model didn't finish properly: it ran to the length cap, or repeated itself."""


def ollama_generate(cfg: Config, model: str, prompt: str, num_ctx: int) -> str:
    body = {
        "model": model,
        "messages": [{"role": "user", "content": prompt}],
        "stream": False,
        "think": False,
        # A mild repeat penalty: stronger ones mangle Markdown, whose bullets legitimately repeat.
        "options": {"temperature": 0.2, "num_ctx": num_ctx, "num_predict": min(MAX_NOTES_TOKENS, num_ctx // 2),
                    "repeat_penalty": 1.1, "repeat_last_n": 256},
    }
    # A long lecture on a big model, or a slow computer, takes minutes; fine, this runs in the background.
    # Output is capped, so this only has to cover reading the transcript plus 4,096 tokens.
    r = httpx.post(f"{cfg.ollama_host.rstrip('/')}/api/chat", json=body, timeout=httpx.Timeout(900, connect=10))
    r.raise_for_status()
    data = r.json()
    if data.get("done_reason") == "length":
        raise RunawayOutput(f"{model} kept writing past {body['options']['num_predict']} tokens without finishing "
                            "(small models sometimes loop), so Granola's notes stay")
    return data["message"]["content"]


def repetitive(text: str, times: int = 5) -> bool:
    """The same line over and over: a model stuck in a loop, not notes."""
    lines = [ln.strip() for ln in text.splitlines() if len(ln.strip()) >= 12]
    if not lines:
        return False
    counts: dict[str, int] = {}
    for ln in lines:
        counts[ln] = counts.get(ln, 0) + 1
    return max(counts.values()) >= times or (len(lines) >= 20 and len(counts) < len(lines) / 2)


def model_context(cfg: Config, model: str) -> int | None:
    """The model's native context length, from Ollama's /api/show."""
    try:
        r = httpx.post(f"{cfg.ollama_host.rstrip('/')}/api/show", json={"model": model}, timeout=10)
        r.raise_for_status()
        for key, value in (r.json().get("model_info") or {}).items():
            if key.endswith(".context_length"):
                return int(value)
    except Exception:
        pass
    return None


def context_size(cfg: Config, model: str, show=model_context) -> int:
    cap = max(4096, cfg.summary_max_context)
    native = show(cfg, model)
    return min(cap, native) if native else cap


def transcript_budget(ctx: int) -> int:
    """Characters of transcript that fit in one call, leaving room for instructions and the answer."""
    reserved = min(4096, ctx // 3)
    return int((ctx - reserved) * CHARS_PER_TOKEN)


def wants_summary(m: Meeting, cfg: Config) -> bool:
    return cfg.ollama_enabled and cfg.summary_enabled and len(m.transcript.strip()) >= MIN_TRANSCRIPT_CHARS


def clean_output(text: str) -> str:
    text = re.sub(r"<think>.*?</think>", "", text or "", flags=re.DOTALL).strip()
    fence = re.fullmatch(r"```(?:markdown|md)?\s*\n(.*)\n```", text, flags=re.DOTALL)
    text = (fence.group(1) if fence else text).strip()
    if repetitive(text):
        raise RunawayOutput("the model repeated itself instead of writing notes, so Granola's notes stay")
    return text


def _pieces(line: str, max_chars: int) -> list[str]:
    """Break one over-long line at sentence ends, then at spaces, then anywhere."""
    if len(line) <= max_chars:
        return [line]
    out, cur = [], ""
    for sentence in re.split(r"(?<=[.!?])\s+", line):
        while len(sentence) > max_chars:
            cut = sentence.rfind(" ", 0, max_chars)
            cut = cut if cut > max_chars // 2 else max_chars
            head, sentence = sentence[:cut], sentence[cut:].lstrip()
            if cur:
                out.append(cur)
                cur = ""
            out.append(head)
        if cur and len(cur) + 1 + len(sentence) > max_chars:
            out.append(cur)
            cur = ""
        cur = f"{cur} {sentence}" if cur else sentence
    if cur:
        out.append(cur)
    return out


def split_transcript(text: str, max_chars: int) -> list[str]:
    """Split on line breaks into parts of at most max_chars."""
    parts, cur, size = [], [], 0
    for raw in text.splitlines():
        for line in _pieces(raw, max_chars):
            if cur and size + len(line) + 1 > max_chars:
                parts.append("\n".join(cur))
                cur, size = [], 0
            cur.append(line)
            size += len(line) + 1
    if cur:
        parts.append("\n".join(cur))
    return [p for p in parts if p.strip()]


def _header(m: Meeting) -> str:
    lines = [f"Lecture: {m.title}", f"Date: {m.date[:10]}"]
    if m.folder:
        lines.append(f"Granola folder: {m.folder}")
    return "\n".join(lines)


def whole_prompt(m: Meeting, transcript: str) -> str:
    return (f"You are an expert note-taker for university lectures. Write study notes for this lecture "
            f"from its transcript.\n\n{STRUCTURE}\n\n{RULES}\n\n{_header(m)}\n\nTranscript:\n{transcript}")


def part_prompt(m: Meeting, part: str, i: int, n: int) -> str:
    return (f"You are taking notes on part {i} of {n} of a lecture transcript. Write detailed Markdown bullet "
            f"notes for this part only: every concept, definition, example, formula, and announcement, in order. "
            f"They will be merged with the other parts later, so write no introduction or conclusion.\n\n"
            f"{RULES}\n\n{_header(m)}\n\nTranscript part {i} of {n}:\n{part}")


def condense_prompt(m: Meeting, notes: list[str]) -> str:
    joined = "\n\n".join(f"### Notes {i}\n{n}" for i, n in enumerate(notes, 1))
    return (f"Combine these notes on consecutive parts of one lecture into one set of detailed Markdown bullet "
            f"notes. Remove repetition but keep every distinct fact.\n\n{RULES}\n\n{_header(m)}\n\n{joined}")


def merge_prompt(m: Meeting, notes: list[str]) -> str:
    joined = "\n\n".join(f"### Part {i}\n{n}" for i, n in enumerate(notes, 1))
    return (f"You are an expert note-taker for university lectures. Below are notes on consecutive parts of one "
            f"lecture. Merge them into one set of study notes, removing repetition and keeping every distinct "
            f"fact.\n\n{STRUCTURE}\n\n{RULES}\n\n{_header(m)}\n\n{joined}")


def summarize_transcript(m: Meeting, cfg: Config, chat: ChatFn | None = None, show=None) -> str:
    """Our own study notes for a lecture, written from its transcript."""
    chat = chat or ollama_generate
    model = cfg.effective_summary_model
    ctx = context_size(cfg, model, show or model_context)
    budget = transcript_budget(ctx)
    text = m.transcript.strip()
    if len(text) <= budget:
        return clean_output(chat(cfg, model, whole_prompt(m, text), ctx))
    parts = split_transcript(text, budget)
    notes = [clean_output(chat(cfg, model, part_prompt(m, p, i, len(parts)), ctx)) for i, p in enumerate(parts, 1)]
    # Very long lectures: fold pairs of part-notes together until the merge fits in one call.
    while len(notes) > 1 and sum(len(n) for n in notes) > budget:
        notes = [clean_output(chat(cfg, model, condense_prompt(m, notes[i:i + 2]), ctx)) if i + 1 < len(notes)
                 else notes[i] for i in range(0, len(notes), 2)]
    return clean_output(chat(cfg, model, merge_prompt(m, notes), ctx))
