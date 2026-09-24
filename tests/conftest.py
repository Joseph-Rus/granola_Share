import sys
from contextlib import asynccontextmanager
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))


@pytest.fixture(autouse=True)
def _not_your_real_home(tmp_path_factory, monkeypatch):
    """No test may touch the real ~/Applications, /Applications, LaunchAgents, or Start Menu."""
    from granola_share import launcher

    home = tmp_path_factory.mktemp("home")
    for var in ("HOME", "USERPROFILE"):
        monkeypatch.setenv(var, str(home))
    monkeypatch.setenv("APPDATA", str(home / "AppData"))
    monkeypatch.setattr(launcher, "SYSTEM_APPS", home / "no-system-Applications")
    return home


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
