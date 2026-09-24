"""When a lecture finishes: at most one popup, and one "it's filed" notification at the end."""

import asyncio
from datetime import date, datetime, timezone

from conftest import FakeGranola
from granola_share.client import COPY_NEEDS_PERMISSION, COPY_OFF, COPY_ON, ShareClient
from granola_share.config import ClientConfig
from granola_share.granola import Meeting
from granola_share.transcript_grab import TranscriptStore, parse_copied

TITLE = "Membranes and osmosis"
COPIED = f"Meeting Title: {TITLE}\nDate: Sep 24\n\nTranscript:\nMe: osmosis and membranes, three cases."


class Harness:
    def __init__(self, tmp_path, mode="auto", copy=COPY_ON, answer="Save", ended=None, captured=COPIED):
        self.now = datetime(2026, 9, 24, 16, 0, tzinfo=timezone.utc)
        self.cc = ClientConfig(home=tmp_path, server_url="http://pool", pool_key="pw", pool_name="Lecture notes",
                               mode=mode)
        self.store = TranscriptStore(tmp_path)
        self.posted, self.notes, self.dialogs, self.captures, self.allowed = [], [], [], [], []
        self.answer, self.copy, self.ended, self.status = answer, copy, ended, {"status": "queued"}
        lecture = Meeting(id="n1", title=TITLE, date="2026-09-24T15:00:00", notes_markdown="Granola's summary")

        def choose(title, text, buttons, default=None, timeout=0):
            self.dialogs.append((text, buttons, default))
            return self.answer

        def capture_for(title):
            self.captures.append(title)
            return parse_copied(captured).body if captured else ""

        self.client = ShareClient(
            self.cc, FakeGranola([Meeting(id="n1", title=TITLE, date=lecture.date)], [lecture]),
            ask=lambda *a, **k: True, choose=choose, notify=lambda t, x: self.notes.append(x),
            post=lambda url, payload, headers: self.posted.append(payload) or {"class_name": None},
            get=lambda url, headers: self.status, log=lambda *_: None, transcripts=self.store,
            copy_state=lambda: self.copy, capture_for=capture_for, allow_copying=lambda: self.allowed.append(1),
            handled_since=lambda started: self.ended, clock=lambda: self.now)

    def poll(self):
        return asyncio.run(self.client.poll_once())


def test_copied_when_the_recording_ended_means_no_popup_at_all(tmp_path):
    h = Harness(tmp_path, ended="copied")
    h.store.save(parse_copied(COPIED, today=date(2026, 9, 24)), COPIED)
    h.poll()
    assert h.dialogs == [] and "osmosis" in h.posted[0]["transcript"]
    assert h.notes == []  # no "sent" notification: the one message is "it's filed"
    h.status = {"status": "done", "class_name": "Data Science", "summary_model": "qwen3.6:35b-a3b"}
    h.poll()
    assert h.notes == [f"“{TITLE}” is in Lecture notes under Data Science. Notes written from the transcript."]


def test_one_click_save_when_the_end_was_missed(tmp_path):
    h = Harness(tmp_path)  # the laptop didn't see the recording end (asleep, or recorded on the phone)
    h.poll()
    text, buttons, default = h.dialogs[0]
    assert buttons == ["Without transcript", "Save"] and default == "Save"
    assert TITLE in text and "opens Granola, copies the transcript, and brings you back" in text
    assert "transcript panel" not in text and "show" not in text.lower()  # nothing to learn
    assert h.captures == [TITLE] and "osmosis" in h.posted[0]["transcript"] and h.notes == []


def test_save_that_cannot_find_the_lecture_still_sends_and_says_how_to_add_it(tmp_path):
    h = Harness(tmp_path, captured=None)
    h.poll()
    assert h.posted[0]["transcript"] == "" and "Open it in Granola any time" in h.notes[0]


def test_not_now_at_the_end_of_the_recording_is_not_asked_again(tmp_path):
    h = Harness(tmp_path, ended="not now")
    h.poll()
    assert h.dialogs == [] and h.posted[0]["transcript"] == ""  # sent with Granola's summary


def test_waits_while_the_end_of_recording_popup_is_up(tmp_path):
    h = Harness(tmp_path, ended="asking")
    h.poll()
    assert h.dialogs == [] and h.posted == []
    h.ended = "saved"
    h.store.save(parse_copied(COPIED, today=date(2026, 9, 24)), COPIED)
    h.cc.mode = "ask"
    h.poll()
    assert h.dialogs == [] and "osmosis" in h.posted[0]["transcript"]  # Save already meant yes


def test_missing_permission_opens_settings_and_sends_meanwhile(tmp_path):
    h = Harness(tmp_path, copy=COPY_NEEDS_PERMISSION, answer="Allow")
    h.poll()
    text, buttons, _ = h.dialogs[0]
    assert buttons[-1] == "Allow" and "python3.12" in text
    assert h.allowed == [1] and h.captures == [] and h.posted[0]["transcript"] == ""


def test_ask_mode_adds_skip(tmp_path):
    h = Harness(tmp_path, mode="ask", answer="Skip")
    h.poll()
    assert h.dialogs[0][1] == ["Skip", "Without transcript", "Save"] and h.posted == []
    assert h.client.state["seen"]["n1"]["decision"] == "skipped"
    h2 = Harness(tmp_path / "b", answer=None)  # nobody answered: ask again next time
    h2.poll()
    h2.poll()
    assert len(h2.dialogs) == 2 and h2.posted == []


def test_no_copying_means_the_plain_flow(tmp_path):
    h = Harness(tmp_path, copy=COPY_OFF)
    h.poll()
    assert h.dialogs == [] and len(h.posted) == 1


def test_lectures_missing_a_transcript_are_wanted(tmp_path):
    h = Harness(tmp_path, answer="Without transcript")
    h.poll()
    assert h.client.missing_transcripts() == {"membranes and osmosis"}
    h.store.save(parse_copied(COPIED, today=date(2026, 9, 24)), COPIED)  # opened in Granola later
    rep = h.poll()
    assert rep.reshared == [TITLE] and h.client.missing_transcripts() == set()


def test_old_libraries_that_cannot_say_are_asked_once(tmp_path):
    h = Harness(tmp_path, answer="Without transcript")
    h.poll()
    h.status = None  # a 0.1 library answers 404
    h.poll()
    assert h.client.state["seen"]["n1"]["filed"] is None and not h.client._busy()
