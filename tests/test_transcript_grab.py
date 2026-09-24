import asyncio
import json
from datetime import date, datetime

from conftest import FakeGranola
from granola_share import doctor
from granola_share.client import ShareClient
from granola_share.config import ClientConfig, save_client_config
from granola_share.granola import Meeting
from granola_share.transcript_grab import (MIN_GAP, SETTLE_LIVE, TranscriptGrabber, TranscriptStore, parse_copied,
                                           press_and_read)
from granola_share.wizard import ScriptedPrompter, client_setup

COPIED = """Meeting Title: Membranes and osmosis
Date: Sep 24
Meeting participants: alex rivera

Transcript:
Me: So we look at osmosis first. Water moves toward the side with more solute.
Them: What would you expect in pure water?"""


def test_parse_copied_splits_header_and_body():
    c = parse_copied(COPIED, today=date(2026, 9, 24))
    assert c.title == "Membranes and osmosis" and c.date == date(2026, 9, 24)
    assert c.body.startswith("Me: So we look") and c.body.endswith("pure water?")
    # Granola leaves the year out: a December note copied in January belongs to last year
    assert parse_copied(COPIED.replace("Sep 24", "Dec 30"), today=date(2027, 1, 2)).date == date(2026, 12, 30)
    assert parse_copied("Meeting Title: x\nTranscript:\n") is None


def test_store_keeps_the_best_copy_and_matches_title_and_date(tmp_path):
    store = TranscriptStore(tmp_path)
    c = parse_copied(COPIED, today=date(2026, 9, 24))
    path, changed = store.save(c, COPIED)
    assert changed and path.parent.name == "transcripts"
    assert store.save(c, COPIED) == (path, False)  # same text: nothing new
    short = COPIED.split("\nThem:")[0].replace("Water moves toward the side with more solute.", "")
    assert store.save(parse_copied(short, today=date(2026, 9, 24)), short)[1] is False  # a much shorter partial copy
    assert "What would you expect" in store.find("Membranes and osmosis", "2026-09-24T15:00:00")
    assert store.find("membranes  and OSMOSIS!", None)  # titles match loosely
    assert store.find("Membranes and osmosis", "2026-10-30") == ""  # another day, another lecture
    assert store.find("Something else", "2026-09-24") == ""


class FakePasteboard:
    def __init__(self, items, copies=None, user_copies_meanwhile=False):
        self.items, self.count, self.copies, self.meddle = items, 1, copies, user_copies_meanwhile
        self.text = None

    def pasteboardItems(self):
        return [FakeItem(d) for d in self.items]

    def changeCount(self):
        return self.count

    def stringForType_(self, t):
        if self.meddle:
            self.count += 1  # the user copies something right as we read
            self.items = [{"public.utf8-plain-text": b"users new copy"}]
        return self.text

    def clearContents(self):
        self.count += 1
        self.items = []

    def writeObjects_(self, objs):
        self.count += 1
        self.items = [o.data for o in objs]

    def app_copies(self):
        if self.copies is not None:
            self.count += 1
            self.text = self.copies
            self.items = [{"public.utf8-plain-text": self.copies.encode()}]


class FakeItem:
    def __init__(self, data=None):
        self.data = dict(data or {})

    @classmethod
    def alloc(cls):
        return cls()

    def init(self):
        return self

    def types(self):
        return list(self.data)

    def dataForType_(self, t):
        return self.data[t]

    def setData_forType_(self, d, t):
        self.data[t] = d


def test_clipboard_is_restored_exactly_and_never_read():
    mine = [{"public.utf8-plain-text": b"my own secret", "public.rtf": b"{\\rtf1 my own secret}"}]
    pb = FakePasteboard(mine, copies=COPIED)
    assert press_and_read(pb, FakeItem, pb.app_copies, sleep=lambda s: None) == COPIED
    assert pb.items == mine  # every type put back
    nothing = FakePasteboard(mine, copies=None)
    assert press_and_read(nothing, FakeItem, nothing.app_copies, timeout=0.05, sleep=lambda s: None) is None
    assert nothing.items == mine and nothing.count == 1  # untouched when the app copied nothing
    raced = FakePasteboard(mine, copies=COPIED, user_copies_meanwhile=True)
    press_and_read(raced, FakeItem, raced.app_copies, sleep=lambda s: None)
    assert raced.items == [{"public.utf8-plain-text": b"users new copy"}]  # their new copy wins


class FakeUI:
    def __init__(self):
        self.allowed, self.front, self.rows, self.idle, self.copies, self.prompts = True, True, "rows-1", 30.0, [], 0
        self.text = COPIED
        self.rec, self.title, self.front_bundle, self.events = False, "Membranes and osmosis", "com.microsoft.VSCode", []

    def recording(self):
        return self.rec

    def note_title(self):
        return self.title

    def open_panel(self):
        self.events.append("open panel")
        self.rows = "rows-panel"
        return True

    def close_panel(self):
        self.events.append("close panel")
        self.rows = None

    def open_note(self, title):
        self.events.append(f"open {title}")
        return title == self.title

    def front_app(self):
        return self.front_bundle

    def activate(self, bundle):
        self.events.append(f"back to {bundle}")
        self.front = False

    def bring_to_front(self):
        self.events.append("bring Granola forward")
        self.front = True

    def trusted(self, prompt=False):
        self.prompts += prompt
        return self.allowed

    def frontmost(self):
        return self.front

    def rows_fingerprint(self):
        return self.rows

    def idle_seconds(self):
        return self.idle

    def copy_transcript(self):
        self.copies.append(self.rows)
        return self.text


def grabber(tmp_path, ui, ask=None):
    clock = {"t": 1000.0}
    got = []
    g = TranscriptGrabber(tmp_path, ui, on_new=got.append, log=lambda s: None, clock=lambda: clock["t"],
                          ask_save=ask, sleep=lambda s: None, now=lambda: datetime(2026, 9, 24, 15, 50),
                          allowed_now=lambda: False, restart=lambda: None)
    return g, clock, got


def test_grabber_copies_an_opened_transcript_once(tmp_path):
    ui = FakeUI()
    g, clock, got = grabber(tmp_path, ui)
    assert g.tick() == "settling"  # just opened
    clock["t"] += 4
    assert g.tick() == "copied" and len(got) == 1 and got[0].title.startswith("Membranes")
    clock["t"] += 60
    assert g.tick() == "up to date" and len(ui.copies) == 1  # nothing changed: no more clipboard use
    assert json.loads(g.status_path.read_text())["last_copy"]["chars"] > 50


def test_grabber_waits_for_a_live_recording_to_settle_and_for_a_pause_in_typing(tmp_path):
    ui = FakeUI()
    g, clock, got = grabber(tmp_path, ui)
    for i in range(5):  # new lines keep arriving while recording
        ui.rows = f"rows-{i}"
        assert g.tick() in ("settling", "waiting")
        clock["t"] += 4
    assert ui.copies == []
    clock["t"] += SETTLE_LIVE  # recording stopped: rows stay put
    ui.idle = 0.5  # but the user is typing
    assert g.tick() == "waiting"
    ui.idle = 5
    assert g.tick() == "copied" and ui.copies == ["rows-4"]
    ui.rows = "rows-5"  # they scroll; a different view shortly after
    clock["t"] += 4
    assert g.tick() in ("settling", "waiting") and len(ui.copies) == 1  # MIN_GAP holds it back
    clock["t"] += MIN_GAP
    assert g.tick() == "copied" and len(got) == 1  # same transcript text: nothing new passed on


def test_grabber_only_acts_with_granola_in_front_and_panel_open(tmp_path):
    ui = FakeUI()
    g, clock, got = grabber(tmp_path, ui)
    ui.front = False
    assert g.tick() == "background"
    ui.front, ui.rows = True, None
    assert g.tick() == "panel closed" and ui.copies == []


def test_grabber_waits_for_permission_and_asks_only_when_told(tmp_path):
    ui = FakeUI()
    ui.allowed = False
    opened = []
    g = TranscriptGrabber(tmp_path, ui, log=lambda s: None, open_url=opened.append, allowed_now=lambda: False)
    assert g.tick() == "not allowed" and ui.prompts == 0 and ui.copies == []  # no surprise dialogs
    assert json.loads(g.status_path.read_text())["trusted"] is False
    assert g.copy_state() == "needs permission"
    g.request_permission()  # the user clicked Allow
    assert ui.prompts == 1 and opened == ["x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility"]
    ui.allowed = True
    assert g.copy_state() == "on" and g.tick() == "settling"


def test_switching_python_on_restarts_the_service_so_it_takes_effect(tmp_path):
    # macOS keeps telling a running process "not allowed" after the switch is turned on.
    ui = FakeUI()
    ui.allowed = False
    clock, fresh, restarts = {"t": 0.0}, {"yes": False}, []
    g = TranscriptGrabber(tmp_path, ui, log=lambda s: None, open_url=lambda u: None, clock=lambda: clock["t"],
                          allowed_now=lambda: fresh["yes"], restart=lambda: restarts.append(1))
    g.request_permission()
    assert g.tick() == "not allowed"
    fresh["yes"] = True  # the user turned python3.12 on in System Settings
    clock["t"] += 3
    assert g.tick() == "not allowed"  # a fresh process is asked every few seconds, not every tick
    clock["t"] += 3
    assert g.tick() == "restarting" and restarts == [1]
    # Started again. If macOS still says no, it doesn't restart over and over.
    again = TranscriptGrabber(tmp_path, ui, log=lambda s: None, allowed_now=lambda: True,
                              restart=lambda: restarts.append(2))
    assert again.tick() == "not allowed" and restarts == [1]


def test_permission_switched_on_without_clicking_allow_is_noticed_too(tmp_path):
    ui = FakeUI()
    ui.allowed = False
    clock, restarts = {"t": 0.0}, []
    g = TranscriptGrabber(tmp_path, ui, log=lambda s: None, clock=lambda: clock["t"], allowed_now=lambda: True,
                          restart=lambda: restarts.append(1))
    g.fresh_checked = 0.0  # it just looked
    clock["t"] = 60
    assert g.tick() == "not allowed"  # only every 5 minutes when nobody clicked Allow
    clock["t"] = 301
    assert g.tick() == "restarting" and restarts == [1]


def end_recording(g, ui, clock):
    ui.rec = True
    assert g.tick() != "recording ended"
    ui.rec = False
    assert g.tick() == "recording ended"  # the microphone just went quiet
    clock["t"] += 9  # Granola writes the last lines
    return g.tick()


def test_stopping_in_granola_copies_with_no_clicks_from_the_user(tmp_path):
    ui = FakeUI()
    ui.rows = None  # the transcript panel is closed: it's opened, copied, and closed again
    asked = []
    g, clock, got = grabber(tmp_path, ui, ask=lambda title: asked.append(title))
    assert end_recording(g, ui, clock) == "copied after recording"
    assert ui.events == ["open panel", "close panel"] and asked == [] and len(got) == 1
    assert g.handled_since(datetime(2026, 9, 24, 15, 0)) == "copied"
    saved = [p for p in (tmp_path / "transcripts").glob("*.json") if p.name != "status.json"]
    assert len(saved) == 1 and "15:50" in saved[0].read_text()  # stop time kept, for matching by time


def test_stopping_elsewhere_asks_once_and_save_brings_you_back(tmp_path):
    ui = FakeUI()
    ui.front, ui.rows = False, None  # stopped from the menu bar; Granola behind VS Code
    asked = []
    g, clock, got = grabber(tmp_path, ui, ask=lambda title: asked.append(title) or True)
    assert end_recording(g, ui, clock) == "copied after asking"
    assert asked == ["Membranes and osmosis"]
    assert ui.events == ["bring Granola forward", "open panel", "close panel", "back to com.microsoft.VSCode"]
    assert g.handled_since(datetime(2026, 9, 24, 15, 0)) == "saved" and len(got) == 1


def test_not_now_is_remembered(tmp_path):
    ui = FakeUI()
    ui.front = False
    g, clock, got = grabber(tmp_path, ui, ask=lambda title: False)
    assert end_recording(g, ui, clock) == "not now" and ui.copies == []
    assert g.handled_since(datetime(2026, 9, 24, 15, 0)) == "not now"
    assert g.handled_since(datetime(2026, 9, 24, 17, 0)) is None  # a later lecture is its own thing


def test_capture_for_a_lecture_opens_it_and_refuses_the_wrong_one(tmp_path):
    ui = FakeUI()
    ui.front = False
    g, clock, got = grabber(tmp_path, ui)
    assert "osmosis" in g.capture_for("Membranes and osmosis")
    assert ui.events[:2] == ["bring Granola forward", "open Membranes and osmosis"]
    assert ui.events[-1] == "back to com.microsoft.VSCode"
    ui.events.clear()
    assert g.capture_for("Some other lecture") == ""  # can't find it: nothing copied, you're still put back
    assert ui.events[-1] == "back to com.microsoft.VSCode"


def test_opening_a_lecture_that_is_missing_its_transcript_is_enough(tmp_path):
    ui = FakeUI()
    ui.rows = None  # panel closed
    g, clock, got = grabber(tmp_path, ui)
    assert g.tick() == "panel closed"  # not wanted: leave it alone
    g.wanted = lambda: {"membranes and osmosis"}
    clock["t"] += 30
    assert g.tick() == "copied" and ui.events == ["open panel", "close panel"]
    assert g.tick() == "panel closed"  # once is enough


def make_client(tmp_path, posted, full):
    cc = ClientConfig(home=tmp_path, server_url="http://pool", pool_key="pw", pool_name="Pool", mode="auto",
                      display_name="Alex")
    store = TranscriptStore(tmp_path)
    post = lambda url, payload, headers: posted.append(payload) or {"class_name": "Data Science"}
    client = ShareClient(cc, FakeGranola([Meeting(id=m.id, title=m.title, date=m.date) for m in full], full),
                         ask=lambda *a, **k: True, notify=lambda *a: None, post=post, log=lambda *_: None,
                         transcripts=store, get=lambda url, headers: None)
    return client, store


def test_client_sends_the_copied_transcript_and_resends_when_one_arrives_later(tmp_path):
    lecture = Meeting(id="n1", title="Membranes and osmosis", date="2026-09-24T15:00:00",
                      notes_markdown="Granola's summary")
    posted = []
    client, store = make_client(tmp_path, posted, [lecture])
    rep = asyncio.run(client.poll_once())
    assert len(rep.shared) == 1 and posted[0]["transcript"] == ""  # free plan, nothing copied yet
    assert client.state["seen"]["n1"]["transcript_chars"] == 0

    store.save(parse_copied(COPIED, today=date(2026, 9, 24)), COPIED)  # the user opened it in Granola
    rep = asyncio.run(client.poll_once())
    assert rep.reshared == ["Membranes and osmosis"] and "osmosis" in posted[1]["transcript"]
    assert posted[1]["notes_markdown"] == "Granola's summary" and client.state["seen"]["n1"]["transcript_chars"] > 50
    assert asyncio.run(client.poll_once()).reshared == [] and len(posted) == 2  # only once


def test_client_attaches_a_copy_made_before_the_note_finished(tmp_path):
    TranscriptStore(tmp_path).save(parse_copied(COPIED, today=date(2026, 9, 24)), COPIED)
    posted = []
    client, _ = make_client(tmp_path, posted, [Meeting(id="n2", title="Membranes and osmosis",
                                                        date="2026-09-24T15:00:00")])
    asyncio.run(client.poll_once())
    assert "osmosis" in posted[0]["transcript"]


def test_setup_asks_about_copying_on_a_mac(tmp_path):
    io = ScriptedPrompter(["http://mini:8787", "pw", "Sam", True, True, False, True, False])
    client_setup(tmp_path, io, check_server=lambda u, k: {"pool_name": "P"}, do_login=lambda c: None,
                 is_logged_in=lambda c: False, install_autostart=lambda r, h: "p", share_now=lambda c, log: None,
                 service_status=lambda r: "running", cleanup=lambda h, log: False, mac=True)
    out = "\n".join(io.output)
    assert "Copy transcript" in out and "python3.12" in out and "Accessibility" in out


def test_doctor_reports_copying(tmp_path):
    cc = ClientConfig(home=tmp_path, server_url="http://mini:8787", pool_key="pw", copy_transcripts=True)
    save_client_config(cc)
    assert doctor.copy_check(cc, False, system="Linux") is None
    assert doctor.copy_check(cc, False, system="Darwin").state == doctor.WARN  # watcher hasn't run yet
    (tmp_path / "transcripts").mkdir()
    (tmp_path / "transcripts" / "status.json").write_text(json.dumps({"trusted": False}))
    c = doctor.copy_check(cc, False, system="Darwin")
    assert c.state == doctor.FAIL and "python3.12" in c.fix
    (tmp_path / "transcripts" / "status.json").write_text(json.dumps(
        {"trusted": True, "last_copy": {"title": "Membranes", "chars": 8181, "at": "2026-09-24T15:40:00"}}))
    assert "Membranes" in doctor.copy_check(cc, False, system="Darwin").detail
    cc.copy_transcripts = False  # the default: fine, and it says how to turn it on
    off = doctor.copy_check(cc, False, system="Darwin")
    assert off.state == doctor.OK and "off (the default)" in off.detail and "read the note" in off.fix

