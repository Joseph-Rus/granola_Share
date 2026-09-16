import sys
from contextlib import asynccontextmanager
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

# What Granola's get_account_info says for the fake account, already normalised
# (granola.normalize_account shape) so the client/panel can use it as-is.
DEFAULT_ACCOUNT = {
    "email": "joey@example.com",
    "workspace": "Joey's workspace",
    "workspace_id": "ws-77",
    "scopes": ["personal", "public"],
    "raw": {"email": "joey@example.com"},
}


class FakeOAuth:
    """Stands in for GranolaOAuth: records logins so `relogin()` can be asserted on."""

    def __init__(self, logged_in: bool = True, error: str | None = None):
        self.logged_in = logged_in
        self.error = error
        self.logins: list[bool] = []  # one entry per login(), the open_browser flag

    def login(self, open_browser: bool = True, log=print) -> bool:
        self.logins.append(open_browser)
        if self.error:
            raise RuntimeError(self.error)
        self.logged_in = True
        log("[fake] signed in")
        return True

    def is_logged_in(self) -> bool:
        return self.logged_in


class FakeGranola:
    """Stands in for GranolaClient: same method shapes, canned data.

    `account` is what get_account_info returns (None = "Granola would not say");
    set `account_error` to make the call blow up, as an expired session does.
    """

    def __init__(self, stubs, full=None, transcript="", account=DEFAULT_ACCOUNT, oauth=None):
        self.stubs, self.full, self.transcript = stubs, full or [], transcript
        self.account = dict(account) if isinstance(account, dict) else account
        self.account_error: str | None = None
        self.oauth = oauth if oauth is not None else FakeOAuth()
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
        self.calls.append(("transcript", mid))
        return self.transcript

    async def get_account_info(self, session):
        self.calls.append(("account", None))
        if self.account_error:
            raise RuntimeError(self.account_error)
        return self.account
