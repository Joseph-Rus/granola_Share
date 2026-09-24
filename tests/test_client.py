import asyncio
import json

from conftest import FakeGranola

from granola_share.client import ShareClient
from granola_share.config import ClientConfig
from granola_share.granola import Meeting


def make(tmp_path, mode="ask", answers=None, post_fail=False):
    cc = ClientConfig(home=tmp_path, server_url="http://mini:8787/", pool_key="pw", pool_name="Pool",
                      display_name="Sam", mode=mode, include_transcripts=True)
    stubs = [Meeting(id="a", title="CS101 lec 1", date="2026-09-10"),
             Meeting(id="b", title="Bio lab", date="2026-09-11"),
             Meeting(id="c", title="Standup", date="2026-09-12")]
    full = [Meeting(id=x.id, title=x.title, date=x.date, notes_markdown=f"notes {x.id}", raw={"id": x.id}) for x in stubs]
    g = FakeGranola(stubs, full, transcript="t")
    answers = list(answers or [])
    asked, posted, notified = [], [], []

    def ask(title, text, yes=None, no=None, timeout=0):
        asked.append(text)
        return answers.pop(0)

    def post(url, payload, headers):
        posted.append((url, payload, headers))
        if post_fail:
            raise RuntimeError("server down")
        return {"class_name": "CS 101"}

    client = ShareClient(cc, g, ask=ask, notify=lambda t, x: notified.append(x), post=post, log=lambda *_: None,
                         get=lambda url, headers: None)
    return cc, client, asked, posted, notified


def test_ask_mode_share_skip_pending_and_repoll(tmp_path):
    cc, client, asked, posted, notified = make(tmp_path, answers=[True, False, None])
    rep = asyncio.run(client.poll_once())
    assert rep.listed == 3 and rep.considered == 3
    assert rep.shared == [("CS101 lec 1", "CS 101")] and rep.skipped == ["Bio lab"] and rep.pending == ["Standup"]
    assert "CS101 lec 1" in asked[0] and "Pool" in asked[0]
    url, payload, headers = posted[0]
    assert url == "http://mini:8787/api/ingest" and headers == {"Authorization": "Bearer pw"}
    assert payload["owner"] == "Sam" and payload["notes_markdown"] == "notes a" and payload["transcript"] == "t"
    assert notified == []  # the one notification comes when the library has filed it
    state = json.loads(cc.state_path.read_text())
    assert state["seen"]["a"]["decision"] == "shared" and state["seen"]["b"]["decision"] == "skipped"
    assert state["seen"]["c"]["decision"] == "pending" and state["last_poll"]

    # next poll: only the pending one is asked again
    client.ask = lambda t, x, yes=None, no=None, timeout=0: (asked.append(x), True)[1]
    rep2 = asyncio.run(client.poll_once())
    assert rep2.considered == 1 and rep2.shared == [("Standup", "CS 101")]
    assert len(posted) == 2


def test_auto_mode_shares_everything_without_asking(tmp_path):
    cc, client, asked, posted, _ = make(tmp_path, mode="auto")
    rep = asyncio.run(client.poll_once())
    assert len(rep.shared) == 3 and asked == [] and len(posted) == 3


def test_push_failure_keeps_note_pending(tmp_path):
    cc, client, asked, posted, _ = make(tmp_path, mode="auto", post_fail=True)
    rep = asyncio.run(client.poll_once())
    assert len(rep.errors) == 3 and rep.pending == ["CS101 lec 1", "Bio lab", "Standup"]
    state = json.loads(cc.state_path.read_text())
    assert all(v["decision"] == "pending" for v in state["seen"].values())


def test_a_new_password_on_the_mac_mini_is_said_once_and_cleared_on_success(tmp_path):
    import httpx

    cc, client, asked, posted, notified = make(tmp_path, mode="auto")
    refused = httpx.HTTPStatusError("401", request=httpx.Request("POST", "http://mini:8787/api/ingest"),
                                    response=httpx.Response(401))
    client.post = lambda url, payload, headers: (_ for _ in ()).throw(refused)
    asyncio.run(client.poll_once())
    asyncio.run(client.poll_once())  # retried; still refused
    assert client.send_problem_kind == "password" and "turned down this laptop's password" in client.send_problem
    assert len(notified) == 1 and "Open Granola Share" in notified[0]  # one notification, not one per retry

    down = httpx.ConnectError("timed out", request=httpx.Request("POST", "http://mini:8787/api/ingest"))
    client.post = lambda url, payload, headers: (_ for _ in ()).throw(down)
    asyncio.run(client.poll_once())
    assert client.send_problem_kind == "unreachable" and "awake" in client.send_problem and len(notified) == 1

    client.post = lambda url, payload, headers: {"class_name": "CS 101"}
    rep = asyncio.run(client.poll_once())
    assert len(rep.shared) == 3 and client.send_problem is None and client.send_problem_kind is None


def test_a_lecture_you_said_send_to_is_retried_without_asking_again(tmp_path):
    cc, client, asked, posted, notified = make(tmp_path, answers=[True, True, True], post_fail=True)
    asyncio.run(client.poll_once())
    assert len(asked) == 3 and len(posted) == 3  # asked once each, and the library didn't take them
    client.post = lambda url, payload, headers: posted.append(payload) or {"class_name": "CS 101"}
    rep = asyncio.run(client.poll_once())
    assert len(asked) == 3 and len(rep.shared) == 3  # sent now, with no second popup


def test_transcript_stripped_when_disabled(tmp_path):
    cc, client, asked, posted, _ = make(tmp_path, mode="auto")
    cc.include_transcripts = False
    asyncio.run(client.poll_once())
    assert posted[0][1]["transcript"] == ""
