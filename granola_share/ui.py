"""One look for both web UIs: the pool browser (server) and the friend's control panel (client).

Everything here is plain HTML + CSS with no build step and no external assets, so it works
over Tailscale and on an offline laptop alike. `layout()` wraps a page; the small helpers
render the recurring pieces (status dots, pills, stat tiles, alerts, relative times).
"""

from __future__ import annotations

import html
from datetime import datetime, timezone

esc = html.escape

# Tones: ok = green (connected / filed), warn = amber (waiting on you), bad = clay (trouble).
TONES = ("ok", "warn", "bad", "info", "")

CSS = """
:root{
  --bg:#f6f5f2;--surface:#ffffff;--surface-2:#f0efeb;--border:#e3e1db;--border-strong:#cfccc4;
  --text:#1c1b18;--muted:#6b6862;--faint:#9a978f;--accent:#2f5fd0;--accent-ink:#ffffff;
  --ok:#2f8f5b;--ok-bg:#e6f4ec;--warn:#c27a10;--warn-bg:#fbf0dc;--bad:#b9503b;--bad-bg:#f8e6e1;
  --info:#3b6cc9;--info-bg:#e6edfb;--pill:#ecebe6;--shadow:0 1px 2px rgba(20,18,12,.06);
  --radius:10px;--radius-sm:7px;
}
@media (prefers-color-scheme:dark){:root{
  --bg:#161614;--surface:#1f1f1c;--surface-2:#262624;--border:#33322e;--border-strong:#4a4841;
  --text:#ecebe6;--muted:#a19e95;--faint:#77746c;--accent:#7ea2f0;--accent-ink:#0f1626;
  --ok:#5fc68c;--ok-bg:#1c3327;--warn:#e6a53a;--warn-bg:#3a2c12;--bad:#e2795f;--bad-bg:#3b1f18;
  --info:#8db0f5;--info-bg:#1c2740;--pill:#2e2d29;--shadow:none;
}}
*{box-sizing:border-box}
html{color-scheme:light dark}
body{margin:0;background:var(--bg);color:var(--text);font:15px/1.5 -apple-system,BlinkMacSystemFont,"Segoe UI",system-ui,sans-serif;-webkit-font-smoothing:antialiased}
a{color:var(--accent);text-decoration:none}a:hover{text-decoration:underline}
h1{font-size:1.45rem;margin:1.4rem 0 .6rem;letter-spacing:-.01em}h2{font-size:1.1rem;margin:1.6rem 0 .5rem}h3{font-size:1rem;margin:1rem 0 .4rem}
.wrap{max-width:980px;margin:0 auto;padding:0 20px 3rem}
.muted{color:var(--muted)}.faint{color:var(--faint)}.small{font-size:.86em}.mono{font-family:ui-monospace,SFMono-Regular,Menlo,monospace;font-size:.9em}
.row{display:flex;gap:.6rem;align-items:center;flex-wrap:wrap}.spread{justify-content:space-between}.grow{flex:1 1 auto}
.topbar{display:flex;align-items:center;gap:1rem;padding:.55rem 20px;border-bottom:1px solid var(--border);background:var(--surface);position:sticky;top:0;z-index:5}
.topbar .brand{font-weight:600;letter-spacing:-.01em}.topbar .brand .host{color:var(--muted);font-weight:400}
.topbar nav{display:flex;gap:1rem;margin-left:auto;flex-wrap:wrap}.topbar nav a{color:var(--muted)}.topbar nav a.active,.topbar nav a:hover{color:var(--text);text-decoration:none}
.dot{display:inline-block;width:.6em;height:.6em;border-radius:50%;background:var(--faint);vertical-align:middle;margin-right:.35em}
.dot.ok{background:var(--ok)}.dot.warn{background:var(--warn)}.dot.bad{background:var(--bad)}.dot.info{background:var(--info)}
.pill{display:inline-block;background:var(--pill);color:var(--text);border-radius:999px;padding:.05rem .6rem;font-size:.82em;white-space:nowrap;max-width:100%;overflow:hidden;text-overflow:ellipsis;vertical-align:middle}
.pill.ok{background:var(--ok-bg);color:var(--ok)}.pill.warn{background:var(--warn-bg);color:var(--warn)}.pill.bad{background:var(--bad-bg);color:var(--bad)}.pill.info{background:var(--info-bg);color:var(--info)}
.status{background:var(--surface);border:1px solid var(--border);border-radius:var(--radius);box-shadow:var(--shadow);padding:1rem 1.2rem;margin-top:1.2rem;display:flex;gap:1.5rem;align-items:center;flex-wrap:wrap}
.status .who{flex:1 1 280px;min-width:0}.status .who .id{font-size:1.15rem;font-weight:600;word-break:break-all}.status .who .meta{color:var(--muted);font-size:.9em;margin-top:.15rem}
.stats{display:flex;gap:1.4rem;flex-wrap:wrap}
.stat{text-align:center;min-width:3.6rem}.stat .n{font-size:1.7rem;font-weight:650;line-height:1.1;letter-spacing:-.02em}.stat .l{color:var(--muted);font-size:.78em;text-transform:uppercase;letter-spacing:.06em}
.stat.ok .n{color:var(--ok)}.stat.warn .n{color:var(--warn)}.stat.bad .n{color:var(--bad)}
.alert{border-radius:var(--radius-sm);padding:.7rem .9rem;margin-top:.9rem;display:flex;gap:.7rem;align-items:flex-start;border:1px solid var(--border)}
.alert .mark{font-weight:700;flex:0 0 auto;width:1.4em;height:1.4em;border-radius:50%;display:inline-flex;align-items:center;justify-content:center;font-size:.85em}
.alert.warn{background:var(--warn-bg);border-color:transparent}.alert.warn .mark{background:var(--warn);color:#fff}
.alert.bad{background:var(--bad-bg);border-color:transparent}.alert.bad .mark{background:var(--bad);color:#fff}
.alert.ok{background:var(--ok-bg);border-color:transparent}.alert.ok .mark{background:var(--ok);color:#fff}
.alert.info{background:var(--info-bg);border-color:transparent}.alert.info .mark{background:var(--info);color:#fff}
.card{background:var(--surface);border:1px solid var(--border);border-radius:var(--radius);box-shadow:var(--shadow);padding:1rem 1.2rem}
.grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(230px,1fr));gap:.8rem;margin-top:.8rem}
.card.class{display:flex;flex-direction:column;gap:.3rem;min-height:6.5rem}.card.class .name{font-weight:600;line-height:1.3}.card.class .count{font-size:1.5rem;font-weight:650;letter-spacing:-.02em}.card.class .foot{margin-top:auto;display:flex;justify-content:space-between;color:var(--muted);font-size:.85em}
.list{margin-top:.8rem;border:1px solid var(--border);border-radius:var(--radius);background:var(--surface);box-shadow:var(--shadow);overflow:hidden}
.item{display:flex;gap:1rem;align-items:center;padding:.8rem 1.1rem;border-top:1px solid var(--border)}.item:first-child{border-top:0}
.item .body{flex:1 1 auto;min-width:0}.item .title{font-weight:560;word-break:break-word}.item .meta{color:var(--muted);font-size:.88em;margin-top:.1rem;display:flex;gap:.5rem;flex-wrap:wrap;align-items:center}
.item .actions{display:flex;gap:.4rem;flex:0 0 auto;align-items:center;flex-wrap:wrap;justify-content:flex-end}
.item.dim{opacity:.72}
table{border-collapse:collapse;width:100%;background:var(--surface);border:1px solid var(--border);border-radius:var(--radius);overflow:hidden;box-shadow:var(--shadow);margin-top:.8rem}
th,td{padding:.55rem .8rem;border-top:1px solid var(--border);text-align:left;vertical-align:top}th{border-top:0;background:var(--surface-2);color:var(--muted);font-weight:600;font-size:.82em;text-transform:uppercase;letter-spacing:.05em}
tr:first-child td{border-top:0}
.btn{display:inline-flex;align-items:center;gap:.35rem;font:inherit;font-size:.9em;padding:.38rem .8rem;border-radius:var(--radius-sm);border:1px solid var(--border-strong);background:var(--surface);color:var(--text);cursor:pointer;text-decoration:none;line-height:1.2}
.btn:hover{background:var(--surface-2);text-decoration:none}.btn.primary{background:var(--accent);border-color:var(--accent);color:var(--accent-ink)}.btn.primary:hover{filter:brightness(1.07)}
.btn.quiet{border-color:transparent;background:transparent;color:var(--muted)}.btn.quiet:hover{background:var(--surface-2);color:var(--text)}
.btn.danger{color:var(--bad)}.btn.sm{padding:.22rem .55rem;font-size:.82em}
form.inline{display:inline}
input,select,textarea{font:inherit;padding:.38rem .55rem;border:1px solid var(--border-strong);border-radius:var(--radius-sm);background:var(--surface);color:var(--text);max-width:100%}
select.sm{padding:.2rem .4rem;font-size:.85em}
.section-head{display:flex;align-items:baseline;gap:.8rem;margin-top:1.8rem;flex-wrap:wrap}.section-head h2{margin:0}.section-head .actions{margin-left:auto;display:flex;gap:.4rem;flex-wrap:wrap}
.empty{color:var(--muted);padding:1.2rem;text-align:center}
.note{background:var(--surface);border:1px solid var(--border);border-radius:var(--radius);padding:1rem 1.5rem;margin-top:1rem;overflow-wrap:anywhere}
.note pre{overflow-x:auto;background:var(--surface-2);padding:.6rem .8rem;border-radius:var(--radius-sm)}.note code{font-size:.92em}.note img{max-width:100%}
.kv{display:grid;grid-template-columns:max-content 1fr;gap:.25rem 1rem;margin:.6rem 0}.kv dt{color:var(--muted)}.kv dd{margin:0;word-break:break-word}
.check{display:flex;gap:.6rem;align-items:flex-start;padding:.45rem 0;border-top:1px solid var(--border)}.check:first-child{border-top:0}
.foot{margin-top:2.5rem;color:var(--faint);font-size:.82em;display:flex;gap:1rem;flex-wrap:wrap}
@media (max-width:560px){.status{padding:.8rem 1rem}.stats{gap:1rem;width:100%;justify-content:space-around}.item{flex-wrap:wrap}.item .actions{width:100%;justify-content:flex-start}.wrap{padding:0 14px 2rem}}
"""


def layout(title: str, body: str, *, brand: str, host: str = "", nav: list[tuple[str, str]] | None = None,
           active: str = "", head: str = "", refresh: int | None = None) -> str:
    """Full HTML document. `nav` is [(label, href)], `active` the href to highlight."""
    links = "".join(
        f'<a href="{esc(href, quote=True)}"{" class=active" if href == active else ""}>{esc(label)}</a>'
        for label, href in (nav or [])
    )
    meta_refresh = f'<meta http-equiv="refresh" content="{int(refresh)}">' if refresh else ""
    host_html = f' <span class=host>· {esc(host)}</span>' if host else ""
    return (
        "<!doctype html><html lang=en><head><meta charset=utf-8>"
        '<meta name=viewport content="width=device-width,initial-scale=1">'
        f"<title>{esc(title)} · {esc(brand)}</title>{meta_refresh}<style>{CSS}</style>{head}</head><body>"
        f'<div class=topbar><span class=brand>{esc(brand)}{host_html}</span><nav>{links}</nav></div>'
        f"<div class=wrap>{body}</div></body></html>"
    )


def dot(tone: str = "") -> str:
    return f'<span class="dot {tone}"></span>'


def pill(text: str, tone: str = "", title: str = "") -> str:
    t = f' title="{esc(title, quote=True)}"' if title else ""
    return f'<span class="pill {tone}"{t}>{esc(text)}</span>'


def stat(value, label: str, tone: str = "") -> str:
    return f'<div class="stat {tone}"><div class=n>{esc(str(value))}</div><div class=l>{esc(label)}</div></div>'


def alert(body_html: str, tone: str = "warn", mark: str = "!") -> str:
    """A one-sentence callout. `body_html` is trusted HTML (escape user data yourself)."""
    return f'<div class="alert {tone}"><span class=mark>{esc(mark)}</span><div>{body_html}</div></div>'


def _parse_iso(value: str | None) -> datetime | None:
    if not value:
        return None
    try:
        d = datetime.fromisoformat(str(value).replace("Z", "+00:00"))
    except ValueError:
        return None
    if d.tzinfo is None:
        d = d.replace(tzinfo=timezone.utc)
    return d


def rel_time(value: str | None, now: datetime | None = None, never: str = "never") -> str:
    """'just now', '2 min ago', '3 hours ago', 'yesterday', '4 days ago'."""
    d = _parse_iso(value)
    if d is None:
        return never
    now = now or datetime.now(timezone.utc)
    if now.tzinfo is None:
        now = now.replace(tzinfo=timezone.utc)
    s = int((now - d).total_seconds())
    if s < 0:
        s = 0
    if s < 45:
        return "just now"
    if s < 3600:
        return f"{max(1, s // 60)} min ago"
    if s < 86400:
        h = s // 3600
        return f"{h} hour{'s' if h != 1 else ''} ago"
    days = s // 86400
    if days == 1:
        return "yesterday"
    if days < 30:
        return f"{days} days ago"
    return d.strftime("%b %d, %Y")


def fmt_date(value: str | None, with_year: bool = False) -> str:
    """'Sep 11' from an ISO date/datetime, or the first 10 chars if it does not parse."""
    if not value:
        return ""
    d = _parse_iso(value)
    if d is None:
        return str(value)[:10]
    return d.strftime("%b %d, %Y" if with_year else "%b %d").replace(" 0", " ")


def plural(n: int, one: str, many: str | None = None) -> str:
    return f"{n} {one if n == 1 else (many or one + 's')}"


def render_note_markdown(text: str) -> str:
    """Note markdown → HTML, with any raw HTML in the note neutralised.

    Notes come from other people's Granola accounts, so a title or summary containing
    `<script>` must not become live markup in someone else's browser. Python-markdown has
    had no safe mode since 3.0, so we escape the source first: every `<` and `&` in the
    note is literal text, and only the HTML that markdown itself generates is real.
    """
    import markdown as md  # local import: only the pages that render a note need it

    return md.markdown(esc(text or "", quote=False), extensions=["extra", "sane_lists"])
