"""The control panel, driven end to end over a real ShareClient with fake Granola/pool IO.

The fakes live here on purpose: the panel is the one screen the friend sees, so every test
goes through the HTTP layer (TestClient) rather than poking the renderers directly.
"""

import asyncio
import json
import re
import socket
import sys
import time
from contextlib import asynccontextmanager
from datetime import datetime, timezone
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from granola_share import panel  # noqa: E402
from granola_share.client import ShareClient  # noqa: E402
from granola_share.config import ClientConfig  # noqa: E402
from granola_share.granola import Meeting  # noqa: E402
from granola_share.panel import NO_NOTES_WARNING, UNSORTED_LABEL, create_panel  # noqa: E402

PORT = 8790
HOST = {"Host": f"127.0.0.1:{PORT}"}
ACCOUNT = {"email": "joey@example.com", "workspace": "Joey's workspace", "workspace_id": "ws-7",
           "scopes": ["personal", "public"]}
CLASSES = ["CS 101", "Bio 2"]
GUESSES = {"a": {"class_name": "CS 101", "confidence": 0.93, "lecture_title": "Lecture 1", "topics": ["loops"]},
           "b": {"class_name": "Unsorted", "confidence": 0.2, "lecture_title": "", "topics": []}}


# --- fakes ---------------------------------------------------------------

class FakeOAuth:
    def __init__(self, logged_in=True, error=None):
        self.logged_in, self.error = logged_in, error
        self.logins = []

    def login(self, open_browser=True, log=print):
        self.logins.append(open_browser)
        if self.error:
            raise RuntimeError(self.error)
        self.logged_in = True
        return True

    def is_logged_in(self):
        return self.logged_in


class FakeGranola:
    def __init__(self, stubs, full, account=ACCOUNT, oauth=None):
        self.stubs, self.full, self.account = stubs, full, account
        self.oauth = oauth or FakeOAuth()

    @asynccontextmanager
    async def session(self):
        yield "session"

    async def list_meetings(self, session, since=None, limit=50):
        return self.stubs

    async def get_meetings(self, session, ids):
        return [m for m in self.full if m.id in ids]

    async def get_transcript(self, session, mid):
        return ""

    async def get_account_info(self, session):
        return self.account


class FakePost:
    """One fake for both calls the client makes: /api/preview guesses, /api/ingest files."""

    def __init__(self, fail=False):
        self.fail = fail
        self.previews, self.ingests = [], []

    def __call__(self, url, payload, headers):
        if url.endswith("/api/preview"):
            self.previews.append(payload)
            return dict(GUESSES.get(payload["id"], {"class_name": "Unsorted", "confidence": 0.0}))
        self.ingests.append(payload)
        if self.fail:
            raise RuntimeError("server down")
        return {"id": payload["id"], "class_name": payload.get("class_name") or "CS 101",
                "url": "/note/" + payload["id"]}


def ok_health(url, key):
    return {"pool_name": "Lecture pool", "classes": list(CLASSES), "notes": 12}


def make(tmp_path, *, stubs=None, mode="ask", health=ok_health, post_fail=False, logged_in=True,
         account=ACCOUNT, poll=True):
    """A real ShareClient with its queue already filled by one poll, plus a TestClient."""
    cc = ClientConfig(home=tmp_path, server_url="http://mini:8787", pool_key="pw", pool_name="Lecture pool",
                      display_name="Sam", mode=mode, panel_port=PORT, notifications=False,
                      include_transcripts=False, poll_interval_seconds=300)
    if stubs is None:
        stubs = [Meeting(id="a", title="CS101 lecture 1", date="2026-09-10"),
                 Meeting(id="b", title="Bio lab & friends", date="2026-09-11")]
    full = [Meeting(id=m.id, title=m.title, date=m.date, notes_markdown=f"# {m.title}\n\n- point one\n- point two")
            for m in stubs]
    granola = FakeGranola(stubs, full, account=account, oauth=FakeOAuth(logged_in))
    post = FakePost(fail=post_fail)
    logs = []
    client = ShareClient(cc, granola, notify=lambda *a: None, post=post, log=logs.append, health=health)
    if poll:
        asyncio.run(client.poll_once())
    return client, TestClient(create_panel(client)), post, logs


def send(tc, path, token=panel.TOKEN, headers=HOST, **data):
    return tc.post(path, data={"t": token, **data}, headers=headers, follow_redirects=False)


def wait_until(fn, timeout=3.0):
    end = time.monotonic() + timeout
    while time.monotonic() < end:
        if fn():
            return True
        time.sleep(0.01)
    return bool(fn())


# --- pure helpers ---------------------------------------------------------

@pytest.mark.parametrize("host,ok", [
    ("127.0.0.1:8790", True), ("localhost:8790", True), ("127.0.0.1", True), ("localhost", True),
    ("127.0.0.1:9999", False), ("panel.evil.com:8790", False), ("192.168.1.9:8790", False), ("", False), (None, False),
])
def test_allowed_host(host, ok):
    assert panel.allowed_host(host, 8790) is ok


def test_next_check_minutes_counts_down_and_falls_back_to_the_interval():
    st = {"next_check_at": "2026-09-15T12:05:00+00:00", "poll_interval_seconds": 300}
    noon = datetime(2026, 9, 15, 12, 0, tzinfo=timezone.utc).timestamp()
    assert panel.next_check_minutes(st, noon) == 5
    assert panel.next_check_minutes(st, noon + 290) == 1  # never "0 min"
    assert panel.next_check_minutes({"poll_interval_seconds": 180}) == 3


def test_header_tone_is_green_amber_clay():
    good = {"server": {"ok": True}, "account": {"email": "a@b.c"}, "listed": 3, "counts": {"waiting": 0}}
    assert panel.header_tone(good) == "ok"
    assert panel.header_tone(good | {"counts": {"waiting": 2}}) == "warn"
    assert panel.header_tone(good | {"listed": 0}) == "warn"
    assert panel.header_tone(good | {"server": {"ok": False}}) == "bad"
    assert panel.header_tone(good | {"login_needed": True}) == "bad"


def test_guess_pill_and_class_options_default_to_unsorted():
    assert UNSORTED_LABEL in panel.guess_pill(None)
    assert UNSORTED_LABEL in panel.guess_pill({"class_name": "Unsorted"})
    assert ">CS 101</span>" in panel.guess_pill({"class_name": "CS 101", "confidence": 0.9})
    opts = panel.class_options(CLASSES, {"class_name": "Bio 2", "confidence": 0.8})
    assert '<option value="Bio 2" selected>' in opts and '<option value="CS 101">' in opts
    weak = panel.class_options(CLASSES, None)
    assert '<option value="Unsorted" selected>' in weak
    # a class the pool knows but this laptop has not seen yet is still offered
    assert '<option value="Art 9" selected>' in panel.class_options(CLASSES, {"class_name": "Art 9"})


def test_poll_script_watches_waiting_and_last_checked():
    js = panel.poll_script(2, "2026-09-15T12:00:00+00:00")
    assert "/api/state" in js and "15000" in js and "location.reload()" in js
    assert '"waiting": 2' in js and "2026-09-15T12:00:00+00:00" in js


# --- the queue page -------------------------------------------------------

def test_queue_page_shows_identity_titles_and_guesses(tmp_path):
    client, tc, post, _ = make(tmp_path)
    html = tc.get("/").text
    assert "2 notes waiting to share" in html
    assert "CS101 lecture 1" in html and "Bio lab &amp; friends" in html
    assert "joey@example.com" in html and "class=id" in html  # the email is the page's headline
    assert "workspace Joey&#x27;s workspace · pool Lecture pool · last checked just now" in html
    assert ">Found</div>" in html and ">Shared</div>" in html and ">Waiting</div>" in html
    assert 'class="stat warn"' in html  # Waiting is amber while notes wait
    assert "Sep 10" in html and "Sep 11" in html
    assert ">CS 101</span>" in html and UNSORTED_LABEL in html  # strong guess vs weak guess
    assert '<option value="CS 101" selected>CS 101</option>' in html
    assert ">Share all</button>" in html and ">Skip all</button>" in html
    assert html.count(f'value="{panel.TOKEN}"') >= 4  # every form carries the token
    assert "<h1>" not in html  # nothing competes with the email for biggest thing on the page


def test_a_hostile_note_title_is_escaped(tmp_path):
    stubs = [Meeting(id="x", title='<script>alert("pwn")</script>', date="2026-09-12")]
    client, tc, _, _ = make(tmp_path, stubs=stubs)
    html = tc.get("/").text
    assert "<script>alert" not in html
    assert "&lt;script&gt;alert(&quot;pwn&quot;)&lt;/script&gt;" in html


def test_share_passes_the_class_override_through_and_moves_the_note_to_history(tmp_path):
    client, tc, post, _ = make(tmp_path)
    r = send(tc, "/share/a", class_name="Bio 2")
    assert r.status_code == 303 and r.headers["location"] == "/"
    assert post.ingests[-1]["id"] == "a" and post.ingests[-1]["class_name"] == "Bio 2"
    assert post.ingests[-1]["owner"] == "Sam"
    assert [q.id for q in client.pending()] == ["b"]
    assert client.status()["counts"] == {"found": 2, "shared": 1, "skipped": 0, "waiting": 1}
    queue = tc.get("/").text
    assert "CS101 lecture 1" not in queue and "1 note waiting to share" in queue
    hist = tc.get("/history").text
    assert "CS101 lecture 1" in hist and ">Bio 2</span>" in hist
    assert 'href="http://mini:8787/note/a"' in hist and "open in pool" in hist
    assert not client.cc.queue_dir.joinpath("a.json").exists()  # body cleaned up


def test_skip_drops_the_note_without_pushing(tmp_path):
    client, tc, post, _ = make(tmp_path)
    assert send(tc, "/skip/b").status_code == 303
    assert [q.id for q in client.pending()] == ["a"]
    assert post.ingests == []
    hist = tc.get("/history").text
    assert "Bio lab &amp; friends" in hist and ">skipped</span>" in hist


def test_share_all_uses_each_guess_and_skip_all_empties_the_queue(tmp_path):
    client, tc, post, _ = make(tmp_path)
    assert send(tc, "/share-all").status_code == 303
    assert client.pending() == []
    filed = {p["id"]: p.get("class_name") for p in post.ingests}
    assert filed == {"a": "CS 101", "b": None}  # a weak guess is left for the pool to sort
    assert {r["id"] for r in client.history()} == {"a", "b"}

    client2, tc2, post2, _ = make(tmp_path / "second")
    assert send(tc2, "/skip-all").status_code == 303
    assert client2.pending() == [] and post2.ingests == []
    assert all(r["decision"] == "skipped" for r in client2.history())


def test_a_failed_share_keeps_the_note_visible_with_its_error_and_no_500(tmp_path):
    client, tc, post, logs = make(tmp_path, post_fail=True)
    r = send(tc, "/share/a")
    assert r.status_code == 303
    html = tc.get("/").text
    assert "CS101 lecture 1" in html and "share failed" in html and "server down" in html
    assert [q.decision for q in client.pending() if q.id == "a"] == ["pending"]
    assert any("share failed" in line for line in logs)
    # retry once the pool is back
    post.fail = False
    assert send(tc, "/share/a").status_code == 303
    assert [q.id for q in client.pending()] == ["b"]
    assert "share failed" not in tc.get("/").text


def test_sharing_or_skipping_a_vanished_note_redirects_instead_of_erroring(tmp_path):
    client, tc, _, logs = make(tmp_path)
    assert send(tc, "/share/gone").status_code == 303
    assert send(tc, "/skip/gone").status_code == 303
    assert sum("nothing queued with id 'gone'" in line for line in logs) == 2


def test_posts_need_the_token_and_a_loopback_host(tmp_path):
    client, tc, post, _ = make(tmp_path)
    assert send(tc, "/share/a", token="not-the-token").status_code == 403
    assert send(tc, "/share/a", headers={"Host": "panel.evil.com"}).status_code == 403
    assert send(tc, "/skip/a", headers={"Host": "127.0.0.1:1234"}).status_code == 403
    assert send(tc, "/share-all", token="").status_code == 403
    assert post.ingests == [] and [q.id for q in client.pending()] == ["b", "a"]  # newest first, untouched
    # a GET is read-only and needs neither
    assert tc.get("/").status_code == 200 and tc.get("/api/state").status_code == 200


def test_check_now_wakes_the_watcher_and_relogin_signs_in_again(tmp_path):
    client, tc, _, _ = make(tmp_path)
    client.wake.clear()
    assert send(tc, "/check-now").status_code == 303
    assert client.wake.is_set()

    assert send(tc, "/relogin").status_code == 303
    assert wait_until(lambda: client.status()["login"]["state"] == "ok")
    assert client.granola.oauth.logins == [True]
    assert "Signed in" in client.status()["login"]["message"]
    assert "Signed in" in tc.get("/").text


def test_api_state_has_the_status_queue_and_history(tmp_path):
    client, tc, _, _ = make(tmp_path)
    send(tc, "/skip/b")
    st = tc.get("/api/state").json()
    assert st["counts"] == {"found": 2, "shared": 0, "skipped": 1, "waiting": 1}
    assert st["server"] == {"ok": True, "pool_name": "Lecture pool", "classes": CLASSES, "notes": 12,
                            "error": None, "checked_at": st["server"]["checked_at"]}
    assert st["account"]["email"] == "joey@example.com" and st["listed"] == 2
    assert st["mode"] == "ask" and st["panel_url"] == f"http://127.0.0.1:{PORT}"
    assert st["poll_interval_seconds"] == 300 and st["logged_in"] is True and st["login_needed"] is False
    assert st["last_checked"] and st["next_check_at"] and st["last_error"] is None
    assert [p["id"] for p in st["pending"]] == ["a"]
    assert st["pending"][0]["guess"]["class_name"] == "CS 101" and st["pending"][0]["decision"] == "queued"
    assert [h["id"] for h in st["history"]] == ["b"]


# --- callouts -------------------------------------------------------------

def test_zero_notes_says_so_out_loud_with_a_switch_account_button(tmp_path):
    client, tc, _, _ = make(tmp_path, stubs=[])
    html = tc.get("/").text
    assert NO_NOTES_WARNING in html
    assert "Signed in as joey@example.com" in html
    assert ">Sign in as someone else</button>" in html
    assert 'action="/relogin"' in html
    assert 'class="alert warn"' in html


def test_unreachable_server_and_a_bad_password_both_shout(tmp_path):
    def refused(url, key):
        raise RuntimeError("could not reach http://mini:8787/api/health: Connection refused")

    client, tc, _, _ = make(tmp_path, health=refused)
    html = tc.get("/").text
    assert "Cannot reach the notes pool." in html and "Connection refused" in html
    assert "Is the pool server running at" in html and "http://mini:8787" in html
    assert 'class="alert bad"' in html

    def rejected(url, key):
        raise RuntimeError("the server rejected the pool password")

    client2, tc2, _, _ = make(tmp_path / "pw", health=rejected)
    html2 = tc2.get("/").text
    assert "the server rejected the pool password" in html2
    assert "granola-share client setup" in html2


def test_not_signed_in_offers_a_sign_in_button(tmp_path):
    client, tc, _, _ = make(tmp_path, logged_in=False, account=None)
    html = tc.get("/").text
    assert "Not signed in to Granola." in html
    assert ">Sign in</button>" in html and 'action="/relogin"' in html
    assert "not signed in" in html  # the identity line says it too
    assert NO_NOTES_WARNING not in html  # one problem at a time


def test_login_in_progress_and_login_failure_are_visible(tmp_path):
    client, tc, _, _ = make(tmp_path)
    client._set_status(login={"state": "waiting", "message": "Finish signing in in the browser window…"})
    html = tc.get("/").text
    assert "Finish signing in in the browser window" in html and 'class="alert info"' in html

    client._set_status(login={"state": "error", "message": "token exchange failed"})
    html = tc.get("/").text
    assert "Signing in failed:" in html and "token exchange failed" in html
    assert ">Try again</button>" in html


def test_empty_queue_says_when_the_next_check_is(tmp_path):
    client, tc, _, _ = make(tmp_path)
    send(tc, "/skip-all")
    html = tc.get("/").text
    assert re.search(r"Nothing waiting\. Next check in \d+ min\.", html)
    assert ">Check now</button>" in html
    assert "waiting to share" not in html


def test_auto_mode_empty_queue_explains_itself(tmp_path):
    client, tc, _, _ = make(tmp_path, mode="auto")
    html = tc.get("/").text
    assert "Nothing waiting." in html and "shared without asking (mode: auto)" in html


# --- note preview, history, log -------------------------------------------

def test_note_page_renders_the_markdown_body(tmp_path):
    client, tc, _, _ = make(tmp_path)
    html = tc.get("/note/a").text
    assert "<li>point one</li>" in html and "<li>point two</li>" in html
    assert "Sep 10, 2026" in html and ">CS 101</span>" in html
    assert 'action="/share/a"' in html and ">Share</button>" in html
    assert tc.get("/note/nope").status_code == 404
    send(tc, "/skip/a")
    assert tc.get("/note/a").status_code == 404  # gone from the queue, gone from the preview


def test_log_page_shows_the_tail_and_says_when_there_is_none(tmp_path):
    client, tc, _, _ = make(tmp_path)
    assert "No log yet" in tc.get("/log").text

    log = client.cc.log_dir / "client.log"
    log.parent.mkdir(parents=True, exist_ok=True)
    log.write_text("\n".join(f"[client] line {i} <b>" for i in range(1, 251)) + "\n")
    html = tc.get("/log").text
    assert "Last 200 lines of" in html
    assert "line 250" in html and "line 51" in html and "line 49" not in html
    assert "&lt;b&gt;" in html and "<b>" not in html


def test_log_tail_survives_a_missing_file(tmp_path):
    assert panel.log_tail(tmp_path / "nope.log") == []
    p = tmp_path / "some.log"
    p.write_text("one\ntwo\nthree\n")
    assert panel.log_tail(p, 2) == ["two", "three"]


def test_history_page_without_history(tmp_path):
    client, tc, _, _ = make(tmp_path)
    assert "Nothing shared or skipped yet." in tc.get("/history").text


def test_pages_carry_the_nav_and_the_15_second_poll(tmp_path):
    client, tc, _, _ = make(tmp_path)
    for path in ("/", "/history"):
        html = tc.get(path).text
        assert 'href="/history"' in html and 'href="/log"' in html and 'href="http://mini:8787"' in html
        assert f"localhost:{PORT}" in html
        assert "/api/state" in html and "15000" in html


# --- serving --------------------------------------------------------------

def test_serve_in_thread_gives_up_quietly_when_the_port_is_taken(tmp_path):
    client, _, _, logs = make(tmp_path, poll=False)
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        s.listen(1)
        taken = s.getsockname()[1]
        assert panel.serve_in_thread(client, port=taken) is None
    assert any(f"port {taken} is already in use" in line for line in logs)


def test_serve_in_thread_refuses_to_leave_loopback(tmp_path):
    client, _, _, logs = make(tmp_path, poll=False)
    assert panel.serve_in_thread(client, host="0.0.0.0", port=8791) is None
    assert any("localhost only" in line for line in logs)


def test_state_file_stays_valid_json_through_panel_actions(tmp_path):
    client, tc, _, _ = make(tmp_path)
    send(tc, "/share/a", class_name="CS 101")
    send(tc, "/skip/b")
    state = json.loads(client.cc.state_path.read_text())
    assert state["seen"]["a"]["decision"] == "shared" and state["seen"]["a"]["class_name"] == "CS 101"
    assert state["seen"]["b"]["decision"] == "skipped"
    assert list(client.cc.queue_dir.glob("*.json")) == []


def test_note_body_html_is_escaped_not_executed(tmp_path):
    """A note body carrying markup must never become live HTML in the panel.

    Granola's scopes include notes other people shared with you, and this page holds the
    token that can share the whole queue — so script in a note must stay text.
    """
    stubs = [Meeting(id="x", title="Shared lecture", date="2026-09-12")]
    client, tc, _, _ = make(tmp_path, stubs=stubs, poll=False)
    client.granola.full = [Meeting(id="x", title="Shared lecture", date="2026-09-12",
                                   notes_markdown="# Heading\n\n<script>fetch('/share-all')</script>\n\n"
                                                  "<b>bold</b> and **real bold**")]
    asyncio.run(client.poll_once())
    html = tc.get("/note/x").text
    assert "<script>fetch(" not in html
    assert "&lt;script&gt;" in html and "&lt;b&gt;bold&lt;/b&gt;" in html
    # markdown the panel generates itself still renders
    assert "<h1>Heading</h1>" in html and "<strong>real bold</strong>" in html
