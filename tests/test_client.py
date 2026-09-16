"""The friend's laptop client: the durable queue, the panel actions, status and the poll loop.

Mode "ask" (the v0.2 default) queues notes for the control panel instead of popping a dialog;
mode "dialog" keeps the old native popup flow; mode "auto" shares everything.
"""

import asyncio
import json
import threading
import time
from datetime import date, datetime, timedelta, timezone
from types import SimpleNamespace

import pytest
from conftest import DEFAULT_ACCOUNT, FakeGranola, FakeOAuth

from granola_share.client import (OVERLAP_DAYS, ShareClient, atomic_write, default_status, is_weak_guess,
                                  load_state_text, looks_like_login_error, slim_account)
from granola_share.config import UNSORTED, ClientConfig
from granola_share.granola import Meeting

HEALTH = {"ok": True, "pool_name": "Fall pool", "classes": ["CS 101", "Bio 110"], "notes": 4, "version": "0.2.0"}

GUESSES = {
    "a": {"id": "a", "class_name": "CS 101", "confidence": 0.94, "classified_by": "ollama",
          "lecture_title": "Loops", "topics": ["loops", "while"]},
    "b": {"id": "b", "class_name": UNSORTED, "confidence": 0.2, "classified_by": "ollama",
          "lecture_title": "Bio lab", "topics": []},
}


class Server:
    """Fake pool server for ShareClient.post: answers /api/preview and /api/ingest, records everything."""

    def __init__(self, guesses=None, ingest_fail=None, preview_fail=(), default_class="CS 101"):
        self.guesses = dict(guesses or {})
        self.ingest_fail = ingest_fail          # error text -> every ingest raises
        self.preview_fail = set(preview_fail)   # note ids whose preview raises
        self.default_class = default_class
        self.calls: list[tuple[str, dict, dict]] = []

    def __call__(self, url: str, payload: dict, headers: dict):
        self.calls.append((url, payload, headers))
        nid = payload.get("id")
        if url.endswith("/api/preview"):
            if nid in self.preview_fail:
                raise RuntimeError("preview exploded")
            return self.guesses.get(nid)
        if self.ingest_fail:
            raise RuntimeError(self.ingest_fail)
        cls = payload.get("class_name") or (self.guesses.get(nid) or {}).get("class_name") or self.default_class
        return {"id": nid, "class_name": cls, "classified_by": "human" if payload.get("class_name") else "ollama",
                "file": f"{nid}.md", "url": f"/note/{nid}"}

    def _of(self, suffix):
        return [payload for url, payload, _ in self.calls if url.endswith(suffix)]

    @property
    def previews(self):
        return self._of("/api/preview")

    @property
    def ingests(self):
        return self._of("/api/ingest")


def make(tmp_path, mode="ask", answers=None, guesses=None, ingest_fail=None, preview_fail=(),
         health=HEALTH, account=DEFAULT_ACCOUNT, stubs=None, **cckw):
    cc = ClientConfig(home=tmp_path, server_url="http://mini:8787/", pool_key="pw", pool_name="Pool",
                      display_name="Sam", mode=mode, include_transcripts=True, **cckw)
    if stubs is None:
        stubs = [Meeting(id="a", title="CS101 lec 1", date="2026-09-10"),
                 Meeting(id="b", title="Bio lab", date="2026-09-11"),
                 Meeting(id="c", title="Standup", date="2026-09-12")]
    full = [Meeting(id=x.id, title=x.title, date=x.date, notes_markdown=f"notes {x.id}", raw={"id": x.id})
            for x in stubs]
    g = FakeGranola(stubs, full, transcript="t", account=account)
    answers = list(answers or [])
    asked, notified = [], []

    def ask(title, text, timeout=0):
        asked.append(text)
        return answers.pop(0)

    def health_fn(url, key):
        if isinstance(health, Exception):
            raise health
        return health

    server = Server(guesses if guesses is not None else GUESSES, ingest_fail, preview_fail)
    client = ShareClient(cc, g, ask=ask, notify=lambda t, x: notified.append(x), post=server,
                         log=lambda *_: None, health=health_fn)
    return SimpleNamespace(cc=cc, client=client, granola=g, server=server, asked=asked, notified=notified)


def state_of(cc):
    return json.loads(cc.state_path.read_text())


def wait_until(fn, timeout=5.0):
    end = time.monotonic() + timeout
    while time.monotonic() < end:
        if fn():
            return True
        time.sleep(0.01)
    return False


# --- pure helpers ---------------------------------------------------------------

def test_load_state_text_accepts_v01_files_and_garbage():
    v01 = json.dumps({"seen": {"a": {"decision": "pending", "title": "Old note"}}, "last_poll": "2026-09-01T10:00:00"})
    st = load_state_text(v01)
    assert st["seen"]["a"]["decision"] == "pending" and st["last_poll"] == "2026-09-01T10:00:00"
    assert st["status"] == default_status()  # a v0.1 file has no status at all

    for junk in (None, "", "not json at all", "[1, 2, 3]", '"a string"'):
        st = load_state_text(junk)
        assert st == {"seen": {}, "last_poll": None, "status": default_status()}

    saved = json.dumps({"status": {"listed": 7, "server": {"ok": True, "pool_name": "Fall pool"},
                                   "login": {"state": "waiting", "message": "in the browser"}}})
    st = load_state_text(saved)
    assert st["status"]["listed"] == 7
    assert st["status"]["server"]["ok"] is True and st["status"]["server"]["pool_name"] == "Fall pool"
    assert st["status"]["server"]["classes"] == []  # merged onto the defaults, not replacing them
    assert st["status"]["login"] == {"state": "idle", "message": ""}  # a login never survives a restart


def test_atomic_write_creates_dirs_replaces_and_leaves_no_temp_file(tmp_path):
    path = tmp_path / "deep" / "queue" / "x.json"
    atomic_write(path, '{"v": 1}')
    atomic_write(path, '{"v": 2}')
    assert json.loads(path.read_text()) == {"v": 2}
    assert [p.name for p in path.parent.iterdir()] == ["x.json"]


def test_slim_account_and_weak_guess_and_login_error():
    assert slim_account(DEFAULT_ACCOUNT) == {"email": "joey@example.com", "workspace": "Joey's workspace",
                                             "workspace_id": "ws-77", "scopes": ["personal", "public"]}
    assert slim_account(None) is None and slim_account({}) is None
    assert slim_account({"email": "a@b.c"}) == {"email": "a@b.c", "workspace": "", "workspace_id": "", "scopes": []}
    assert is_weak_guess(None) and is_weak_guess({}) and is_weak_guess({"class_name": UNSORTED})
    assert not is_weak_guess({"class_name": "CS 101"})
    assert looks_like_login_error("RuntimeError: not logged in") and looks_like_login_error("invalid_grant")
    assert not looks_like_login_error("could not reach http://mini:8787") and not looks_like_login_error(None)


def test_since_uses_last_poll_with_overlap(tmp_path):
    env = make(tmp_path)
    assert env.client._since() == date.today() - timedelta(days=env.cc.share_lookback_days)
    env.client.state["last_poll"] = "2026-09-10T12:00:00+00:00"
    assert env.client._since() == date(2026, 9, 10) - timedelta(days=OVERLAP_DAYS)


# --- ask mode: poll queues, the panel decides ------------------------------------

def test_ask_mode_queues_with_a_guess_and_notifies_once(tmp_path):
    env = make(tmp_path, preview_fail=["c"])
    rep = asyncio.run(env.client.poll_once())

    assert rep.listed == 3 and rep.considered == 3
    assert rep.queued == ["CS101 lec 1", "Bio lab", "Standup"]
    assert rep.shared == [] and rep.skipped == [] and rep.pending == [] and rep.errors == []
    assert rep.account == slim_account(DEFAULT_ACCOUNT)
    assert env.asked == []           # no popup in ask mode
    assert env.server.ingests == []  # and nothing is pushed until a human says so
    assert [p["id"] for p in env.server.previews] == ["a", "b", "c"]
    assert env.server.previews[0]["notes_markdown"] == "notes a" and env.server.previews[0]["owner"] == "Sam"

    body = json.loads((env.cc.queue_dir / "a.json").read_text())
    assert body["id"] == "a" and body["title"] == "CS101 lec 1" and body["notes_markdown"] == "notes a"
    assert body["transcript"] == "t" and body["guess"] == GUESSES["a"] and body["queued_at"]
    assert json.loads((env.cc.queue_dir / "c.json").read_text())["guess"] is None  # preview failed: still queued

    seen = state_of(env.cc)["seen"]
    assert [seen[k]["decision"] for k in ("a", "b", "c")] == ["queued"] * 3
    assert seen["a"]["guess"]["class_name"] == "CS 101" and seen["c"]["guess"] is None
    assert env.notified == ["3 notes ready to review · http://127.0.0.1:8790"]

    # a second poll finds the same three sitting in the panel: nothing re-queued, nobody pinged again
    rep2 = asyncio.run(env.client.poll_once())
    assert rep2.listed == 3 and rep2.considered == 0 and rep2.queued == []
    assert len(env.server.previews) == 3 and env.notified == ["3 notes ready to review · http://127.0.0.1:8790"]


def test_ask_mode_notification_can_be_switched_off(tmp_path):
    env = make(tmp_path, notifications=False)
    asyncio.run(env.client.poll_once())
    assert env.notified == []


def test_pending_rows_and_queued_note_bodies(tmp_path):
    env = make(tmp_path)
    asyncio.run(env.client.poll_once())

    rows = env.client.pending()
    assert [r.id for r in rows] == ["c", "b", "a"]  # newest lecture first
    assert [r.decision for r in rows] == ["queued"] * 3
    assert rows[2].title == "CS101 lec 1" and rows[2].guess["class_name"] == "CS 101" and rows[2].queued_at
    assert rows[0].guess is None and rows[0].error is None

    m = env.client.queued_note("a")
    assert m and m.id == "a" and m.notes_markdown == "notes a" and m.transcript == "t"
    assert env.client.queued_note("nope") is None
    (env.cc.queue_dir / "b.json").write_text("{ not json")
    assert env.client.queued_note("b") is None
    # a corrupt body still shows in the queue (title/guess come from the state file), it just cannot be shared
    rows = env.client.pending()
    assert [r.id for r in rows] == ["c", "b", "a"]
    assert rows[1].title == "Bio lab" and rows[1].guess["class_name"] == UNSORTED
    with pytest.raises(KeyError):
        env.client.share("b")


def test_share_uses_the_class_override_and_moves_the_note_to_history(tmp_path):
    env = make(tmp_path)
    asyncio.run(env.client.poll_once())

    res = env.client.share("a", "Bio 110")
    assert res["class_name"] == "Bio 110"
    url, payload, headers = env.server.calls[-1]
    assert url == "http://mini:8787/api/ingest" and headers == {"Authorization": "Bearer pw"}
    assert payload["class_name"] == "Bio 110" and payload["owner"] == "Sam" and payload["notes_markdown"] == "notes a"

    assert not (env.cc.queue_dir / "a.json").exists()
    entry = state_of(env.cc)["seen"]["a"]
    assert entry["decision"] == "shared" and entry["class_name"] == "Bio 110"
    assert entry["url"] == "http://mini:8787/note/a"
    assert [r.id for r in env.client.pending()] == ["c", "b"]
    hist = env.client.history()
    assert [h["id"] for h in hist] == ["a"] and hist[0]["url"] == "http://mini:8787/note/a"

    with pytest.raises(KeyError):
        env.client.share("a")  # the body is gone, so there is nothing left to push
    with pytest.raises(KeyError):
        env.client.skip("nope")


def test_share_without_override_lets_the_server_decide(tmp_path):
    env = make(tmp_path)
    asyncio.run(env.client.poll_once())
    env.client.share("b")
    assert "class_name" not in env.server.ingests[-1]
    assert state_of(env.cc)["seen"]["b"]["class_name"] == UNSORTED


def test_skip_and_skip_all_clear_the_queue(tmp_path):
    env = make(tmp_path)
    asyncio.run(env.client.poll_once())

    entry = env.client.skip("a")
    assert entry["decision"] == "skipped" and entry["title"] == "CS101 lec 1"
    assert not (env.cc.queue_dir / "a.json").exists() and env.server.ingests == []

    assert env.client.skip_all() == 2
    assert env.client.pending() == [] and list(env.cc.queue_dir.iterdir()) == []
    assert {h["id"]: h["decision"] for h in env.client.history()} == {"a": "skipped", "b": "skipped", "c": "skipped"}
    assert env.client.status()["counts"] == {"found": 3, "shared": 0, "skipped": 3, "waiting": 0}


def test_share_all_only_overrides_strong_guesses(tmp_path):
    env = make(tmp_path)  # a: CS 101 (strong), b: Unsorted (weak), c: no guess
    asyncio.run(env.client.poll_once())

    out = env.client.share_all()
    assert sorted(out["shared"]) == ["a", "b", "c"] and out["errors"] == []
    by_id = {p["id"]: p for p in env.server.ingests}
    assert by_id["a"]["class_name"] == "CS 101"
    assert "class_name" not in by_id["b"] and "class_name" not in by_id["c"]
    assert env.client.pending() == [] and len(env.client.history()) == 3
    assert list(env.cc.queue_dir.iterdir()) == []


def test_share_failure_leaves_the_note_retryable_with_its_error(tmp_path):
    env = make(tmp_path, ingest_fail="server down")
    asyncio.run(env.client.poll_once())

    with pytest.raises(RuntimeError):
        env.client.share("a", "CS 101")
    entry = state_of(env.cc)["seen"]["a"]
    assert entry["decision"] == "pending" and "server down" in entry["error"]
    assert (env.cc.queue_dir / "a.json").exists()  # the body stays so Share can be pressed again
    row = next(r for r in env.client.pending() if r.id == "a")
    assert row.decision == "pending" and "server down" in row.error
    assert env.client.status()["counts"]["waiting"] == 3

    out = env.client.share_all()
    assert out["shared"] == [] and sorted(out["errors"]) == ["a: server down", "b: server down", "c: server down"]

    env.server.ingest_fail = None  # the server comes back
    env.client.share("a", "CS 101")
    entry = state_of(env.cc)["seen"]["a"]
    assert entry["decision"] == "shared" and "error" not in entry


# --- status ---------------------------------------------------------------------

def test_status_reports_server_account_counts_and_login_need(tmp_path):
    env = make(tmp_path)
    asyncio.run(env.client.poll_once())
    env.client.share("a", "CS 101")
    env.client.skip("b")

    st = env.client.status()
    assert st["server"] == {"ok": True, "pool_name": "Fall pool", "classes": ["CS 101", "Bio 110"],
                            "notes": 4, "error": None, "checked_at": st["server"]["checked_at"]}
    assert st["account"] == slim_account(DEFAULT_ACCOUNT)
    assert st["listed"] == 3 and st["since"] == (date.today() - timedelta(days=7)).isoformat()
    assert st["counts"] == {"found": 3, "shared": 1, "skipped": 1, "waiting": 1}
    assert st["mode"] == "ask" and st["panel_url"] == "http://127.0.0.1:8790"
    assert st["pool_name"] == "Pool" and st["server_url"] == "http://mini:8787/"
    assert st["last_error"] is None and st["login"] == {"state": "idle", "message": ""}
    expected = datetime.fromisoformat(st["last_checked"]) + timedelta(seconds=env.cc.poll_interval_seconds)
    assert st["next_check_at"] == expected.isoformat(timespec="seconds")
    assert st["logged_in"] is True and st["login_needed"] is False

    env.granola.oauth.logged_in = False
    assert env.client.status()["login_needed"] is True

    env.granola.oauth = None  # an old granola client cannot tell us
    st = env.client.status()
    assert st["logged_in"] is None and st["login_needed"] is False


def test_status_records_an_unreachable_server_without_raising(tmp_path):
    env = make(tmp_path, health=RuntimeError("the server rejected the pool password"))
    asyncio.run(env.client.poll_once())
    server = env.client.status()["server"]
    assert server["ok"] is False and "rejected the pool password" in server["error"] and server["checked_at"]


def test_poll_failure_records_last_error_and_asks_for_a_login(tmp_path):
    env = make(tmp_path)

    async def boom(session, since=None, limit=50):
        raise RuntimeError("not logged in: no refresh token")

    env.granola.list_meetings = boom
    with pytest.raises(RuntimeError):
        asyncio.run(env.client.poll_once())
    st = env.client.status()
    assert "no refresh token" in st["last_error"] and st["login_needed"] is True and st["last_checked"]


def test_zero_notes_is_said_out_loud(tmp_path):
    lines = []
    env = make(tmp_path, stubs=[])
    env.client.log = lines.append
    rep = asyncio.run(env.client.poll_once())
    assert rep.listed == 0 and rep.queued == [] and env.notified == []
    assert any("signed in as joey@example.com · Joey's workspace" in ln and "0 notes found since" in ln
               for ln in lines)


def test_account_info_failure_is_survivable(tmp_path):
    env = make(tmp_path)
    env.granola.account_error = "session expired"
    rep = asyncio.run(env.client.poll_once())
    assert rep.account is None and rep.considered == 3
    assert env.client.status()["account"] is None


def test_client_without_get_account_info_still_polls(tmp_path):
    env = make(tmp_path)
    env.granola.get_account_info = None  # a v0.1 granola client has no such method
    rep = asyncio.run(env.client.poll_once())
    assert rep.account is None and len(rep.queued) == 3


# --- auto and dialog modes --------------------------------------------------------

def test_auto_mode_shares_everything_without_asking(tmp_path):
    env = make(tmp_path, mode="auto")
    rep = asyncio.run(env.client.poll_once())
    assert len(rep.shared) == 3 and env.asked == [] and len(env.server.ingests) == 3
    assert env.server.previews == [] and rep.queued == []
    assert env.notified == ["Shared “CS101 lec 1” → CS 101", "Shared “Bio lab” → Unsorted",
                            "Shared “Standup” → CS 101"]
    assert all(v["decision"] == "shared" for v in state_of(env.cc)["seen"].values())


def test_push_failure_keeps_note_pending_and_retryable(tmp_path):
    env = make(tmp_path, mode="auto", ingest_fail="server down")
    rep = asyncio.run(env.client.poll_once())
    assert len(rep.errors) == 3 and rep.pending == ["CS101 lec 1", "Bio lab", "Standup"]
    state = state_of(env.cc)
    assert all(v["decision"] == "pending" for v in state["seen"].values())
    assert sorted(p.name for p in env.cc.queue_dir.iterdir()) == ["a.json", "b.json", "c.json"]
    assert [r.decision for r in env.client.pending()] == ["pending"] * 3


def test_transcript_stripped_when_disabled(tmp_path):
    env = make(tmp_path, mode="auto")
    env.cc.include_transcripts = False
    asyncio.run(env.client.poll_once())
    assert env.server.ingests[0]["transcript"] == ""


def test_dialog_mode_share_skip_timeout_and_repoll(tmp_path):
    env = make(tmp_path, mode="dialog", answers=[True, False, None])
    rep = asyncio.run(env.client.poll_once())
    assert rep.listed == 3 and rep.considered == 3
    assert rep.shared == [("CS101 lec 1", "CS 101")] and rep.skipped == ["Bio lab"] and rep.pending == ["Standup"]
    assert "CS101 lec 1" in env.asked[0] and "Pool" in env.asked[0]
    assert env.server.previews == []  # the popup flow never asks the server for a guess
    url, payload, headers = env.server.calls[0]
    assert url == "http://mini:8787/api/ingest" and headers == {"Authorization": "Bearer pw"}
    assert payload["owner"] == "Sam" and payload["notes_markdown"] == "notes a" and payload["transcript"] == "t"
    assert env.notified == ["Shared “CS101 lec 1” → CS 101"]
    state = state_of(env.cc)
    assert state["seen"]["a"]["decision"] == "shared" and state["seen"]["b"]["decision"] == "skipped"
    assert state["seen"]["c"]["decision"] == "pending" and state["last_poll"]
    # a dialog that timed out has no body on disk, so the panel has nothing to retry
    assert env.client.pending() == []

    # next poll: only the timed-out one is asked again
    env.client.ask = lambda t, x, timeout=0: (env.asked.append(x), True)[1]
    rep2 = asyncio.run(env.client.poll_once())
    assert rep2.considered == 1 and rep2.shared == [("Standup", "CS 101")]
    assert len(env.server.ingests) == 2


# --- loop, wake and relogin --------------------------------------------------------

def test_run_loop_wakes_early_on_request_poll_and_honours_stop(tmp_path):
    env = make(tmp_path, mode="auto", stubs=[], poll_interval_seconds=30)
    polls = []
    original = env.client.poll_once

    async def counted(since=None):
        polls.append(since)
        return await original(since)

    env.client.poll_once = counted
    stop = threading.Event()
    thread = threading.Thread(target=env.client.run_loop, kwargs={"stop": stop}, daemon=True)
    thread.start()
    try:
        assert wait_until(lambda: len(polls) == 1), "the loop should poll straight away"
        time.sleep(0.05)
        assert len(polls) == 1, "and then wait, not spin"
        env.client.request_poll()
        assert wait_until(lambda: len(polls) == 2), "request_poll should wake the loop well before 30s"
    finally:
        stop.set()
        thread.join(timeout=5)
    assert not thread.is_alive()


def test_run_loop_survives_a_failing_poll(tmp_path):
    env = make(tmp_path, mode="auto", stubs=[], poll_interval_seconds=30)
    lines, polls = [], []
    env.client.log = lines.append

    async def boom(since=None):
        polls.append(since)
        raise RuntimeError("granola exploded")

    env.client.poll_once = boom
    stop = threading.Event()
    thread = threading.Thread(target=env.client.run_loop, kwargs={"stop": stop}, daemon=True)
    thread.start()
    try:
        assert wait_until(lambda: polls and any("granola exploded" in ln for ln in lines))
    finally:
        stop.set()
        thread.join(timeout=5)
    assert not thread.is_alive()


def test_relogin_signs_in_in_the_background_and_asks_for_a_poll(tmp_path):
    env = make(tmp_path)
    env.granola.oauth = FakeOAuth(logged_in=False)
    thread = env.client.relogin(open_browser=False)
    thread.join(timeout=5)
    assert env.granola.oauth.logins == [False]
    assert env.client.status()["login"] == {"state": "ok", "message": "Signed in. Checking for notes…"}
    assert env.client.wake.is_set()  # the loop picks the new account up immediately


def test_relogin_reports_a_failed_or_missing_login(tmp_path):
    env = make(tmp_path)
    env.granola.oauth = FakeOAuth(error="the browser never came back")
    env.client.relogin().join(timeout=5)
    login = env.client.status()["login"]
    assert login["state"] == "error" and "never came back" in login["message"]

    env2 = make(tmp_path / "other")
    env2.granola.oauth = None
    env2.client.relogin().join(timeout=5)
    assert env2.client.status()["login"] == {"state": "error", "message": "this client has no OAuth helper"}
