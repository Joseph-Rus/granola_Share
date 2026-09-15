import sys
from contextlib import asynccontextmanager
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))


class FakeGranola:
    """Stands in for GranolaClient: same method shapes, canned data."""

    def __init__(self, stubs, full=None, transcript=""):
        self.stubs, self.full, self.transcript = stubs, full or [], transcript
        self.calls = []

    @asynccontextmanager
    async def session(self):
        yield "session"

    async def list_meetings(self, session, since=None, limit=50):
        self.calls.append(("list", since))
        return self.stubs

    async def get_meetings(self, session, ids):
        self.calls.append(("get", ids))
        return [m for m in self.full if m.id in ids]

    async def get_transcript(self, session, mid):
        return self.transcript
