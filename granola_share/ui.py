"""HTML for the web UI: one stylesheet, a page shell, and the small pieces pages share.

Look: an engineering pad (pale green paper, faint grid, graphite ink, ballpoint-blue actions).
Each class gets its own hue, used as a binder tab in the sidebar and a spine on every note.
"""

from __future__ import annotations

import hashlib
import html
import json
import re
from datetime import datetime
from urllib.parse import quote

import markdown as md

from .config import UNSORTED

FONTS = ("https://fonts.googleapis.com/css2?family=Barlow+Semi+Condensed:wght@500;600;700"
         "&family=Barlow:wght@400;500;600&family=Source+Serif+4:ital,opsz,wght@0,8..60,400;0,8..60,600;1,8..60,400"
         "&display=swap")
KATEX = "https://cdn.jsdelivr.net/npm/katex@0.16.22/dist"

CSS = r"""
:root{
  --paper:#e9f0e6; --sheet:#f8faf6; --grid:#d2ddce; --rule:#c3d0bf;
  --ink:#1c2620; --ink-2:#56655b; --pen:#2447a3; --pen-ink:#fff; --pen-soft:#dde5f7;
  --warn-bg:#fbefd6; --warn:#7a4a00; --bad:#a02929;
  --tab-l:.62; --tab-c:.13;
  --ui:"Barlow",system-ui,-apple-system,"Segoe UI",sans-serif;
  --head:"Barlow Semi Condensed","Arial Narrow",system-ui,sans-serif;
  --read:"Source Serif 4",Georgia,"Times New Roman",serif;
  color-scheme:light;
}
@media (prefers-color-scheme:dark){:root{
  --paper:#141915; --sheet:#1b211d; --grid:#262f29; --rule:#344038;
  --ink:#e0e7df; --ink-2:#9aa89e; --pen:#9db2f4; --pen-ink:#10162a; --pen-soft:#253052;
  --warn-bg:#3a2e14; --warn:#f0c878; --bad:#ff8f8f;
  --tab-l:.74; --tab-c:.11; color-scheme:dark;
}}
*{box-sizing:border-box}
html{-webkit-text-size-adjust:100%}
body{margin:0;background:var(--paper);color:var(--ink);font:400 16px/1.55 var(--ui);
  background-image:linear-gradient(var(--grid) 1px,transparent 1px),linear-gradient(90deg,var(--grid) 1px,transparent 1px);
  background-size:22px 22px;background-attachment:fixed}
a{color:var(--pen);text-underline-offset:.18em}
:focus-visible{outline:2px solid var(--pen);outline-offset:2px;border-radius:3px}

/* shell */
.app{display:grid;grid-template-columns:16rem minmax(0,1fr);min-height:100vh}
.side{position:sticky;top:0;height:100vh;overflow-y:auto;padding:1.4rem 0 1.4rem 1rem;border-right:1px solid var(--rule);
  display:flex;flex-direction:column;gap:1.4rem}
.brand{font:700 1.3rem/1.15 var(--head);color:var(--ink);text-decoration:none;padding-right:1rem;letter-spacing:.005em}
.brand small{display:block;font:500 .82rem/1.3 var(--ui);color:var(--ink-2);margin-top:.2rem}
.tabs-side{display:flex;flex-direction:column;gap:.2rem}
.tabs-side .group{font:500 .8rem var(--ui);color:var(--ink-2);margin:.8rem 0 .2rem .9rem}
.tab{display:flex;align-items:baseline;gap:.5rem;padding:.42rem .9rem .42rem .8rem;margin-right:-1px;
  border-left:.42rem solid var(--tab);border-radius:0 .45rem .45rem 0;color:var(--ink);text-decoration:none;
  font:600 1rem/1.25 var(--head)}
.tab span:first-child{flex:1;min-width:0;overflow-wrap:anywhere}
.tab .n{font:500 .85rem var(--ui);color:var(--ink-2);font-variant-numeric:tabular-nums}
.tab:hover{background:color-mix(in oklab,var(--sheet) 60%,transparent)}
.tab[aria-current=page]{background:var(--sheet);border-top:1px solid var(--rule);border-bottom:1px solid var(--rule)}
.tab.quiet{border-left-color:transparent;font-weight:500}
.side-foot{margin-top:auto;padding-right:1rem;font-size:.85rem;color:var(--ink-2)}
.side-foot a{color:var(--ink-2)}
.main{background:var(--sheet);min-width:0;padding:1.6rem clamp(1rem,4vw,3.2rem) 4rem}
.wrap{max-width:52rem}
@media (max-width:780px){
  .app{grid-template-columns:minmax(0,1fr);grid-template-rows:auto 1fr}
  .side{position:static;height:auto;overflow:visible;border-right:0;border-bottom:1px solid var(--rule);padding:1rem 0 .7rem 1rem;gap:.7rem}
  .tabs-side{flex-direction:row;overflow-x:auto;padding-bottom:.6rem;padding-right:1rem;scrollbar-width:thin}
  .tabs-side .group{display:none}
  .tab{white-space:nowrap;border-left:0;border-bottom:.3rem solid var(--tab);border-radius:.45rem .45rem 0 0;margin:0;padding:.4rem .7rem}
  .tab[aria-current=page]{border-top:0;border-bottom:.3rem solid var(--tab)}
  .tab.quiet{border-bottom-color:transparent}
  .side-foot{margin-top:0}
}

/* type */
h1{font:700 clamp(1.7rem,3.4vw,2.35rem)/1.08 var(--head);margin:.2rem 0 .5rem;letter-spacing:-.005em;text-wrap:balance}
h2{font:600 1.3rem/1.2 var(--head);margin:2.2rem 0 .6rem}
.lede{color:var(--ink-2);margin:0 0 1.4rem;max-width:62ch}
.muted{color:var(--ink-2)}
.small{font-size:.86rem}

/* controls */
.btn,button{font:600 .95rem/1 var(--ui);padding:.62rem .95rem;border-radius:.45rem;border:1px solid var(--rule);
  background:var(--sheet);color:var(--ink);cursor:pointer;text-decoration:none;display:inline-flex;align-items:center;gap:.4rem}
.btn:hover,button:hover{border-color:var(--ink-2)}
.btn.primary,button.primary{background:var(--pen);border-color:var(--pen);color:var(--pen-ink)}
button.danger{color:var(--bad)}
input,select,textarea{font:400 1rem/1.3 var(--ui);color:var(--ink);background:var(--sheet);border:1px solid var(--rule);
  border-radius:.45rem;padding:.55rem .7rem;max-width:100%}
input:focus,select:focus,textarea:focus{border-color:var(--pen)}
.search{display:flex;gap:.5rem;margin:0 0 1.8rem}
.search input{flex:1;min-width:0;font-size:1.05rem;padding:.7rem .9rem}
.inline{display:inline}
.row-actions{display:flex;flex-wrap:wrap;gap:.5rem;align-items:center}

/* note lists */
.notes{list-style:none;margin:0;padding:0;border-top:1px solid var(--grid)}
.note-row{border-bottom:1px solid var(--grid)}
.note-row>a{display:grid;grid-template-columns:4.2rem minmax(0,1fr);gap:.2rem 1rem;padding:.8rem .6rem .85rem .9rem;
  border-left:.3rem solid var(--tab);color:inherit;text-decoration:none}
.note-row>a:hover{background:color-mix(in oklab,var(--paper) 45%,var(--sheet))}
.note-row time{font:600 .95rem/1.35 var(--head);color:var(--ink-2);font-variant-numeric:tabular-nums;padding-top:.1rem}
.note-row .t{font:600 1.12rem/1.3 var(--head);color:var(--ink);text-wrap:pretty}
.note-row .meta{display:flex;flex-wrap:wrap;gap:.15rem .9rem;font-size:.86rem;color:var(--ink-2);margin-top:.15rem}
.note-row .cls{color:var(--ink);font-weight:500;display:inline-flex;align-items:center;gap:.35rem}
.note-row .cls::before{content:"";width:.55rem;height:.55rem;border-radius:2px;background:var(--tab)}
.note-row mark,.prose mark{background:var(--pen-soft);color:inherit;border-radius:2px;padding:0 .1em}
.snippet{grid-column:2;font-size:.9rem;color:var(--ink-2);margin-top:.25rem}
.empty{padding:2.2rem 1.2rem;border:1px dashed var(--rule);border-radius:.6rem;color:var(--ink-2);max-width:40rem}
.empty strong{color:var(--ink);display:block;font:600 1.1rem var(--head);margin-bottom:.25rem}
@media (max-width:520px){.note-row>a{grid-template-columns:1fr}.snippet{grid-column:1}}

/* queue */
.queue{border:1px solid var(--rule);border-radius:.6rem;padding:.3rem 1rem;margin:0 0 2rem;background:var(--paper)}
.queue li{display:flex;flex-wrap:wrap;justify-content:space-between;gap:.3rem 1rem;padding:.55rem 0;border-bottom:1px solid var(--grid)}
.queue li:last-child{border-bottom:0}
.queue ul{list-style:none;margin:0;padding:0}
.state{font-size:.85rem;color:var(--ink-2)}
.state.now{color:var(--pen);font-weight:600}
.state.failed{color:var(--bad);font-weight:600}

/* class page */
.class-head{border-left:.55rem solid var(--tab);padding:.2rem 0 .2rem 1rem;margin:0 0 1.4rem}
.class-head h1{margin:0}

/* note page */
.crumb{display:inline-flex;align-items:center;gap:.45rem;font:600 .95rem var(--head);text-decoration:none;color:var(--ink);
  padding:.25rem .7rem .25rem .6rem;border-radius:.4rem;background:color-mix(in oklab,var(--tab) 22%,var(--sheet))}
.crumb::before{content:"";width:.6rem;height:.6rem;border-radius:2px;background:var(--tab)}
.facts{display:flex;flex-wrap:wrap;gap:.6rem 2rem;margin:.9rem 0 1.2rem;padding:0}
.facts div{min-width:0}
.facts dt{font-size:.8rem;color:var(--ink-2)}
.facts dd{margin:0;font-weight:500}
.callout{background:var(--warn-bg);color:var(--warn);border-radius:.5rem;padding:.75rem 1rem;margin:0 0 1.2rem;max-width:62ch}
.callout form{margin-top:.5rem}
.doc-tabs{display:flex;gap:.2rem;border-bottom:1px solid var(--rule);margin:1.6rem 0 0;overflow-x:auto}
.doc-tabs button{border:0;border-bottom:3px solid transparent;border-radius:0;background:none;padding:.6rem .8rem .5rem;
  font:600 1rem var(--head);color:var(--ink-2);white-space:nowrap}
.doc-tabs button[aria-selected=true]{color:var(--ink);border-bottom-color:var(--tab)}
.doc{padding-top:.4rem}
.prose{font:400 1.08rem/1.72 var(--read);max-width:68ch;overflow-wrap:break-word}
.prose h1,.prose h2,.prose h3,.prose h4{font-family:var(--head);line-height:1.2;margin:1.8rem 0 .5rem}
.prose h1{font-size:1.5rem}.prose h2{font-size:1.32rem}.prose h3{font-size:1.14rem}.prose h4{font-size:1rem}
.prose ul,.prose ol{padding-left:1.3rem}.prose li{margin:.25rem 0}
.prose code{font-size:.88em;background:var(--paper);padding:.1em .3em;border-radius:3px}
.prose pre{background:var(--paper);padding:.9rem 1rem;border-radius:.5rem;overflow-x:auto;line-height:1.45}
.prose pre code{background:none;padding:0}
.prose blockquote{margin:1rem 0;padding-left:1rem;border-left:3px solid var(--rule);color:var(--ink-2)}
.prose table{border-collapse:collapse;display:block;overflow-x:auto}
.prose .katex-display{overflow-x:auto;overflow-y:hidden;padding:.3rem 0}
.prose td,.prose th{border:1px solid var(--rule);padding:.35rem .6rem}
.byline{font:italic 400 .95rem var(--read);color:var(--ink-2);margin-top:1.4rem}
.transcript{font:400 1rem/1.7 var(--read);max-width:72ch;white-space:pre-wrap;overflow-wrap:break-word}

/* settings + forms */
.panel{border-top:1px solid var(--rule);padding:1.2rem 0 1.6rem}
.panel h2{margin-top:0}
.field{display:grid;gap:.3rem;margin:0 0 1rem;max-width:36rem}
.field label{font-weight:600}
.field .hint{font-size:.86rem;color:var(--ink-2)}
.check{display:flex;gap:.6rem;align-items:flex-start;margin:0 0 .8rem;max-width:40rem}
.check input{margin-top:.3rem}
.classes-edit{display:grid;gap:.6rem;margin-bottom:1rem}
.classes-edit .c{display:grid;grid-template-columns:minmax(0,1fr) minmax(0,1fr) minmax(0,1.6fr) 6.5rem;gap:.5rem;align-items:center}
@media (max-width:780px){.classes-edit .c{grid-template-columns:1fr;padding:.6rem 0;border-bottom:1px solid var(--grid)}}
.code{font:400 .86rem/1.5 ui-monospace,SFMono-Regular,Menlo,Consolas,monospace;background:var(--paper);border:1px solid var(--grid);
  border-radius:.45rem;padding:.6rem .8rem;white-space:pre-wrap;word-break:break-all;margin:.3rem 0 .6rem}
.flash{background:var(--pen-soft);border-radius:.5rem;padding:.65rem 1rem;margin:0 0 1.2rem}

/* login */
.login{min-height:100vh;display:grid;place-items:center;padding:1rem}
.login form{background:var(--sheet);border:1px solid var(--rule);border-radius:.8rem;padding:2rem;width:min(24rem,100%);display:grid;gap:.9rem}
.login h1{font-size:1.7rem;margin:0}
.login .bad{color:var(--bad);margin:0}

@media (prefers-reduced-motion:reduce){*{scroll-behavior:auto!important}}
"""

JS = r"""
document.querySelectorAll('[data-tabs]').forEach(function(bar){
  var btns=bar.querySelectorAll('button[data-for]');
  function show(id){btns.forEach(function(b){var on=b.dataset.for===id;b.setAttribute('aria-selected',on);
    document.getElementById(b.dataset.for).hidden=!on;});}
  btns.forEach(function(b){b.addEventListener('click',function(){show(b.dataset.for);history.replaceState(null,'','#'+b.dataset.for);});});
  var want=location.hash.slice(1);show([].some.call(btns,function(b){return b.dataset.for===want;})?want:btns[0].dataset.for);
});
document.querySelectorAll('[data-copy]').forEach(function(b){b.addEventListener('click',function(){
  navigator.clipboard.writeText(document.getElementById(b.dataset.copy).textContent.trim()).then(function(){
    var t=b.textContent;b.textContent='Copied';setTimeout(function(){b.textContent=t;},1500);});});});
document.querySelectorAll('form[data-confirm]').forEach(function(f){f.addEventListener('submit',function(e){
  if(!confirm(f.dataset.confirm))e.preventDefault();});});
var q=document.querySelector('[data-refresh]');
if(q){setTimeout(function(){location.reload();},parseInt(q.dataset.refresh,10)*1000);}
"""

MATH_JS = r"""
if(window.renderMathInElement){document.querySelectorAll('.prose').forEach(function(el){renderMathInElement(el,{
  delimiters:[{left:'$$',right:'$$',display:true},{left:'$',right:'$',display:false},{left:'\\(',right:'\\)',display:false},{left:'\\[',right:'\\]',display:true}],
  throwOnError:false});});}
"""


def esc(s) -> str:
    return html.escape(str(s if s is not None else ""), quote=True)


def hue(name: str | None) -> int | None:
    """A stable hue per class name; Unsorted gets none (a neutral tab)."""
    if not name or name == UNSORTED:
        return None
    return int(hashlib.sha1(name.encode()).hexdigest()[:4], 16) % 360


def hue_style(name: str | None) -> str:
    h = hue(name)
    return "--tab:var(--rule)" if h is None else f"--tab:oklch(var(--tab-l) var(--tab-c) {h})"


def class_url(name: str) -> str:
    return "/class/" + quote(name, safe="")


def _parse(value: str | None) -> datetime | None:
    try:
        return datetime.fromisoformat((value or "")[:19]) if value else None
    except ValueError:
        return None


def short_date(value: str | None) -> str:
    d = _parse(value)
    return f"{d:%b} {d.day}" if d else (value or "")[:10]


def long_date(value: str | None) -> str:
    d = _parse(value)
    if not d:
        return value or ""
    out = f"{d:%a} {d:%b} {d.day}, {d.year}"
    if len(value or "") > 10:
        out += f", {d.hour % 12 or 12}:{d:%M} {'AM' if d.hour < 12 else 'PM'}"
    return out


# --- Markdown -----------------------------------------------------------------

_MATH_RE = re.compile(r"\$\$(.+?)\$\$|(?<![\\$\w])\$(?=\S)([^$\n]+?)(?<=\S)\$(?![\w$])", re.DOTALL)
_BAD_URL_RE = re.compile(r'(href|src)="\s*(?:javascript|vbscript|data):[^"]*"', re.IGNORECASE)


def render_md(text: str) -> str:
    """Markdown to HTML with raw HTML disabled and math left intact for KaTeX."""
    maths: list[str] = []

    def stash(m: re.Match) -> str:
        maths.append(m.group(0))
        return f"\x00M{len(maths) - 1}\x00"

    text = _MATH_RE.sub(stash, text or "")
    conv = md.Markdown(extensions=["extra", "sane_lists"])
    # Note text comes from Granola and your model's output: never let it inject HTML or scripts.
    conv.preprocessors.deregister("html_block")
    conv.inlinePatterns.deregister("html")
    out = _BAD_URL_RE.sub(r'\1="#"', conv.convert(text))
    return re.sub(r"\x00M(\d+)\x00", lambda m: esc(maths[int(m.group(1))]), out)


# --- page shell ---------------------------------------------------------------

def head(title: str, nonce: str, math: bool = False) -> str:
    extra = f'<link rel="stylesheet" href="{KATEX}/katex.min.css">' if math else ""
    return (f'<!doctype html><html lang="en"><head><meta charset="utf-8">'
            f'<meta name="viewport" content="width=device-width,initial-scale=1">'
            f'<title>{esc(title)}</title><link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>'
            f'<link rel="stylesheet" href="{FONTS}">{extra}<style nonce="{nonce}">{CSS}</style></head>')


def scripts(nonce: str, math: bool = False) -> str:
    out = f'<script nonce="{nonce}">{JS}</script>'
    if math:
        out += (f'<script nonce="{nonce}" src="{KATEX}/katex.min.js"></script>'
                f'<script nonce="{nonce}" src="{KATEX}/contrib/auto-render.min.js"></script>'
                f'<script nonce="{nonce}">{MATH_JS}</script>')
    return out


def sidebar(ctx: dict) -> str:
    cur = ctx.get("current")
    classes = ctx["classes"]  # [(name, count)] from the store, plus configured classes with 0

    def tab(href, label, n=None, key=None, style="", cls="tab"):
        current = ' aria-current="page"' if key == cur else ""
        count = f'<span class="n">{n}</span>' if n is not None else ""
        st = f' style="{style}"' if style else ""
        return f'<a class="{cls}" href="{href}"{st}{current}><span>{esc(label)}</span>{count}</a>'

    items = [tab("/", "Recent", key="home", cls="tab quiet")]
    items.append('<div class="group">Classes</div>')
    for name, n in classes:
        if name != UNSORTED:
            items.append(tab(class_url(name), name, n, key=f"class:{name}", style=hue_style(name)))
    unsorted = dict(classes).get(UNSORTED, 0)
    items.append('<div class="group">To do</div>')
    items.append(tab("/unsorted", "Unsorted", unsorted, key="unsorted", style=hue_style(UNSORTED)))
    if ctx.get("processing"):
        items.append(tab("/#queue", "Being written", ctx["processing"], key="queue", cls="tab quiet"))
    foot = []
    if ctx.get("admin"):
        foot.append('<a href="/settings">Settings</a>')
    if ctx.get("password"):
        foot.append('<a href="/logout">Log out</a>')
    foot.append(f'<span>v{esc(ctx.get("version", ""))}</span>')
    return (f'<aside class="side"><a class="brand" href="/">{esc(ctx["pool_name"])}'
            f'<small>{ctx["total"]} lecture{"s" if ctx["total"] != 1 else ""}</small></a>'
            f'<nav class="tabs-side" aria-label="Classes">{"".join(items)}</nav>'
            f'<div class="side-foot row-actions">{" ".join(foot)}</div></aside>')


def page(title: str, body: str, ctx: dict, *, math: bool = False) -> str:
    nonce = ctx["nonce"]
    return (head(f"{title} · {ctx['pool_name']}" if title != ctx["pool_name"] else title, nonce, math)
            + f'<body><div class="app">{sidebar(ctx)}<main class="main"><div class="wrap">{body}</div></main></div>'
            + scripts(nonce, math) + "</body></html>")


def bare_page(title: str, body: str, nonce: str) -> str:
    return head(title, nonce) + f"<body>{body}{scripts(nonce)}</body></html>"


# --- shared pieces ------------------------------------------------------------

def search_box(q: str = "") -> str:
    return (f'<form class="search" action="/search" role="search"><input type="search" name="q" value="{esc(q)}" '
            f'placeholder="Search titles, summaries, and transcripts" aria-label="Search notes">'
            f'<button class="primary">Search</button></form>')


def note_row(r, snippet: str = "") -> str:
    topics = ", ".join(json.loads(r["topics"] or "[]")[:4])
    meta = [f'<span class="cls">{esc(r["class_name"])}</span>']
    if topics:
        meta.append(f"<span>{esc(topics)}</span>")
    snip = f'<div class="snippet">{snippet}</div>' if snippet else ""
    return (f'<li class="note-row" style="{hue_style(r["class_name"])}"><a href="/note/{quote(r["id"], safe="")}">'
            f'<time datetime="{esc((r["date"] or "")[:10])}">{esc(short_date(r["date"]))}</time>'
            f'<div><div class="t">{esc(r["lecture_title"] or r["title"])}</div><div class="meta">{"".join(meta)}</div></div>'
            f"{snip}</a></li>")


def note_list(rows, empty_title: str, empty_text: str, snippets: dict | None = None) -> str:
    if not rows:
        return f'<div class="empty"><strong>{esc(empty_title)}</strong>{esc(empty_text)}</div>'
    snippets = snippets or {}
    return '<ul class="notes">' + "".join(note_row(r, snippets.get(r["id"], "")) for r in rows) + "</ul>"


def snippet(text: str, q: str, width: int = 90) -> str:
    """A bit of text around the first match, with the match highlighted."""
    i = (text or "").lower().find(q.lower())
    if i < 0 or not q:
        return ""
    start, end = max(0, i - width), min(len(text), i + len(q) + width)
    before, hit, after = text[start:i], text[i:i + len(q)], text[i + len(q):end]
    clean = lambda s: re.sub(r"\s+", " ", re.sub(r"[#*_`>|]", "", s))  # noqa: E731
    return (("…" if start else "") + esc(clean(before)) + f"<mark>{esc(hit)}</mark>" + esc(clean(after))
            + ("…" if end < len(text) else ""))
