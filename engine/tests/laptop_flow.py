"""The Python engine's own laptop watcher, sending to a library the C# engine serves (CrossEngineTests runs this):

    python laptop_flow.py URL PASSWORD HOME

Two lectures from a stand-in Granola, sent as the real watcher sends them, then checked on until the library has
filed both. Prints what it sent, what was filed, and what it told the person, as JSON.
"""

import asyncio
import json
import sys
import time
from contextlib import asynccontextmanager
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))

from granola_share.client import ShareClient  # noqa: E402
from granola_share.config import ClientConfig  # noqa: E402
from granola_share.granola import Meeting  # noqa: E402

url, key, home = sys.argv[1], sys.argv[2], Path(sys.argv[3])

LECTURES = [Meeting(id="py-1", title="CS101 lecture 5 — loops", date="2026-09-15T10:00:00", folder="CS 101",
                    notes_markdown="# Loops\n- for and while", transcript="Today: for loops, then while loops.",
                    raw={"id": "py-1"}),
            Meeting(id="py-2", title="Bio lab: osmosis", date="2026-09-16T14:00:00", notes_markdown="Membranes.",
                    raw={"id": "py-2"})]


class Granola:
    @asynccontextmanager
    async def session(self):
        yield "session"

    async def list_meetings(self, session, since=None, limit=50):
        return LECTURES

    async def get_meetings(self, session, ids):
        return [m for m in LECTURES if m.id in ids]

    async def get_transcript(self, session, mid):
        return ""


cc = ClientConfig(home=home, server_url=url, pool_key=key, pool_name="Cross — engine", display_name="Sam", mode="auto")
told = []
client = ShareClient(cc, Granola(), notify=lambda title, text: told.append(text), log=lambda s: None)
rep = asyncio.run(client.poll_once())
shared, filed = [list(s) for s in rep.shared], [list(f) for f in rep.filed]  # a quick library files them straight away
deadline = time.time() + 30
while len(filed) < len(LECTURES) and time.time() < deadline:
    time.sleep(0.5)
    filed += [list(f) for f in asyncio.run(client.poll_once()).filed]
print(json.dumps({"shared": shared, "filed": filed, "told": told, "errors": rep.errors}))
