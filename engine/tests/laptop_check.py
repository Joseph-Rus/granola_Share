"""The Python engine's own laptop code, pointed at a library the C# engine serves (CrossEngineTests runs this):

    python laptop_check.py URL PASSWORD PORT

Health, a wrong password, sending a lecture, asking whether it's filed, and the checks setup makes of a library it
just started. Prints "ok" when every answer is what a Python library would have given.
"""

import sys
import tempfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))

from granola_share import __version__, client, hostinfo, wizard  # noqa: E402
from granola_share.config import Config  # noqa: E402
from granola_share.granola import Meeting, meeting_to_dict  # noqa: E402

url, key, port = sys.argv[1], sys.argv[2], int(sys.argv[3])
auth = {"Authorization": f"Bearer {key}"}

health = client.check_server(url, key)
assert health["ok"] is True and health["pool_name"] == "Cross — engine" and health["version"] == __version__, health
assert health["classes"] == ["CS 101", "Bio 110"], health
try:
    client.check_server(url, "not the password")
    raise AssertionError("a wrong password got in")
except RuntimeError as e:
    assert str(e) == "wrong password", e

m = Meeting(id="laptop-1", title="CS101 lecture 4 — recursion", date="2026-09-14T10:00:00", owner="Sam",
            attendees=["Sam", "Zoë"], notes_markdown="# Recursion\n- base case", transcript="We start with the base case.",
            raw={"id": "laptop-1", "score": 1.5})
payload = meeting_to_dict(m)
payload["owner"] = "Sam"
sent = client.http_post(url + "/api/ingest", payload, auth)
assert sent == {"id": "laptop-1", "status": "queued", "class_name": "CS 101", "has_transcript": True}, sent

status = client.http_get_json(url + "/api/notes/laptop-1/status", auth)
assert status["id"] == "laptop-1" and status["status"] in ("queued", "working", "done") and status["path"] == "/note/laptop-1"
assert client.http_get_json(url + "/api/notes/nobody/status", auth) is None  # a lecture the library doesn't know

assert hostinfo.port_status(port, host="127.0.0.1") == "ours"  # how setup tells its own library from another app on the port
with tempfile.TemporaryDirectory() as tmp:
    cfg = Config(home=Path(tmp), pool_dir=Path(tmp), web_port=port, pool_password=key)
    assert wizard.wait_for_server(cfg, timeout=10)  # answers, and as this version
print("ok")
