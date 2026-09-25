"""HTML for the web UI: one stylesheet, a page shell, and the small pieces pages share.

Look: Apple's. The system font (San Francisco on a Mac), the system colors in light and dark,
a Notes-style sidebar, grouped rounded lists with inset hairlines, switches, segmented controls,
and a tag color per class taken from Apple's palette.
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

KATEX = "https://cdn.jsdelivr.net/npm/katex@0.16.22/dist"
TAG_COLORS = 12  # --c0 … --c11: Apple's red, orange, yellow, green, mint, teal, cyan, blue, indigo, purple, pink, brown

_SVG_SEARCH = ("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 16 16'%3E%3Ccircle cx='6.5' "
               "cy='6.5' r='5' fill='none' stroke='%238E8E93' stroke-width='1.7'/%3E%3Cpath d='M10.3 10.3 14.5 14.5' "
               "stroke='%238E8E93' stroke-width='1.7' stroke-linecap='round'/%3E%3C/svg%3E")
_SVG_UPDOWN = ("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 8 12'%3E%3Cpath d='M1 4.5 4 "
               "1.5 7 4.5M1 7.5 4 10.5 7 7.5' fill='none' stroke='%238E8E93' stroke-width='1.5' stroke-linecap='round' "
               "stroke-linejoin='round'/%3E%3C/svg%3E")

CSS = r"""
:root{
  --canvas:#f5f5f7; --group:#fff; --sidebar:#ececef; --fill:rgba(118,118,128,.12); --fill-2:rgba(118,118,128,.07);
  --label:#1d1d1f; --label-2:rgba(60,60,67,.64); --label-3:rgba(60,60,67,.3); --sep:rgba(60,60,67,.17);
  --accent:#007aff; --accent-soft:rgba(0,122,255,.14); --sel:rgba(0,0,0,.075); --seg-on:#fff;
  --red:#ff3b30; --orange:#ff9500; --green:#34c759; --gray:#8e8e93;
  --c0:#ff3b30; --c1:#ff9500; --c2:#ffcc00; --c3:#34c759; --c4:#00c7be; --c5:#30b0c7;
  --c6:#32ade6; --c7:#007aff; --c8:#5856d6; --c9:#af52de; --c10:#ff2d55; --c11:#a2845e;
  --font:-apple-system,BlinkMacSystemFont,"SF Pro Text","Helvetica Neue","Segoe UI",Roboto,system-ui,sans-serif;
  --mono:ui-monospace,"SF Mono",SFMono-Regular,Menlo,Consolas,monospace;
  color-scheme:light;
}
@media (prefers-color-scheme:dark){:root{
  --canvas:#1c1c1e; --group:#2c2c2e; --sidebar:#232325; --fill:rgba(118,118,128,.26); --fill-2:rgba(118,118,128,.14);
  --label:#f5f5f7; --label-2:rgba(235,235,245,.62); --label-3:rgba(235,235,245,.28); --sep:rgba(84,84,88,.62);
  --accent:#0a84ff; --accent-soft:rgba(10,132,255,.24); --sel:rgba(255,255,255,.1); --seg-on:#636366;
  --red:#ff453a; --orange:#ff9f0a; --green:#30d158;
  --c0:#ff453a; --c1:#ff9f0a; --c2:#ffd60a; --c3:#30d158; --c4:#63e6e2; --c5:#40c8e0;
  --c6:#64d2ff; --c7:#0a84ff; --c8:#5e5ce6; --c9:#bf5af2; --c10:#ff375f; --c11:#ac8e68;
  color-scheme:dark;
}}
*{box-sizing:border-box}
html{-webkit-text-size-adjust:100%}
body{margin:0;background:var(--canvas);color:var(--label);font:400 15px/1.42 var(--font);letter-spacing:-.008em;
  -webkit-font-smoothing:antialiased}
a{color:var(--accent);text-decoration:none}
a:hover{text-decoration:underline}
:focus-visible{outline:3px solid color-mix(in srgb,var(--accent) 55%,transparent);outline-offset:1px;border-radius:7px}
[hidden]{display:none!important}

/* the window: sidebar + content, like Notes */
.app{display:grid;grid-template-columns:17rem minmax(0,1fr);min-height:100vh}
.side{position:sticky;top:0;height:100vh;overflow-y:auto;background:var(--sidebar);border-right:1px solid var(--sep);
  padding:1.1rem .65rem 1rem;display:flex;flex-direction:column}
.brand{display:block;padding:0 .55rem;margin:0 0 .9rem;color:var(--label);font:700 1.0625rem/1.25 var(--font)}
.brand:hover{text-decoration:none}
.brand small{display:block;font-weight:400;font-size:.8125rem;color:var(--label-2);margin-top:.1rem}
.nav{display:flex;flex-direction:column;gap:1px}
.nav-head{font:600 .6875rem/1 var(--font);color:var(--label-2);padding:1.1rem .55rem .45rem}
.nav a{display:flex;align-items:center;gap:.55rem;padding:.36rem .55rem;border-radius:6px;color:var(--label);font-size:.875rem}
.nav a:hover{background:var(--fill-2);text-decoration:none}
.nav a[aria-current=page]{background:var(--sel);font-weight:600}
.nav .name{flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.nav .n{color:var(--label-2);font-size:.8125rem;font-weight:400;font-variant-numeric:tabular-nums}
.dot{width:.625rem;height:.625rem;border-radius:50%;background:var(--tag,var(--gray));flex:none}
.side-foot{margin-top:auto;padding-top:1rem}
.side-foot .ver{display:block;padding:.5rem .55rem 0;font-size:.75rem;color:var(--label-3)}
.main{min-width:0;padding:2.2rem clamp(1rem,4vw,3rem) 5rem}
.wrap{max-width:46rem;margin:0 auto}
.topbar{display:none}
@media (max-width:760px){
  .app{display:block}
  .side{display:none}
  .topbar{display:flex;position:sticky;top:0;z-index:5;align-items:center;justify-content:space-between;gap:1rem;
    min-height:2.9rem;padding:.4rem 1rem;background:color-mix(in srgb,var(--canvas) 82%,transparent);
    -webkit-backdrop-filter:saturate(180%) blur(20px);backdrop-filter:saturate(180%) blur(20px);border-bottom:1px solid var(--sep)}
  .topbar .t{font-weight:600;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
  .main{padding:1.2rem 1rem 4rem}
  .only-wide{display:none!important}
}
@media (min-width:761px){.only-narrow{display:none!important}}

/* type: Apple's scale */
h1{font:700 2.125rem/1.18 var(--font);letter-spacing:-.024em;margin:0 0 .35rem;text-wrap:balance}
h2{font:700 1.25rem/1.25 var(--font);letter-spacing:-.017em;margin:2.2rem 0 .7rem}
.sub{color:var(--label-2);margin:0 0 1.6rem;max-width:62ch}
.muted{color:var(--label-2)}
.small{font-size:.8125rem}
code{font:400 .88em var(--mono)}

/* grouped lists */
.group{background:var(--group);border-radius:12px;overflow:hidden;margin:0}
.group-head{font:600 .8125rem/1.3 var(--font);color:var(--label-2);margin:1.9rem 1rem .5rem}
.group-foot{font-size:.8125rem;line-height:1.38;color:var(--label-2);margin:.5rem 1rem 0;max-width:62ch}
details.help{margin:.2rem 0 1rem}
details.help summary{cursor:pointer;color:var(--accent);font-size:.8125rem;margin:0 1rem .5rem}
.row{position:relative;display:flex;align-items:center;gap:.75rem;min-height:2.75rem;padding:.6rem 1rem;color:var(--label)}
.group>*{position:relative}
.group>*+*::before{content:"";position:absolute;top:0;left:1rem;right:0;border-top:1px solid var(--sep)}
a.row:hover,label.row:hover{background:var(--fill-2);text-decoration:none}
label.row{cursor:pointer}
.grow{flex:1;min-width:0}
.value{color:var(--label-2);text-align:right;overflow-wrap:anywhere}
.value.bad{color:var(--red)}
.value.good{color:var(--label-2)}
.title{font-weight:600;text-wrap:pretty}
.subtitle{display:flex;flex-wrap:wrap;gap:.1rem .75rem;color:var(--label-2);font-size:.8125rem;margin-top:.12rem}
.chev::after{content:"";display:block;flex:none;width:.45rem;height:.45rem;margin:0 .15rem 0 .3rem;
  border-top:2px solid var(--label-3);border-right:2px solid var(--label-3);transform:rotate(45deg)}
.row mark,.prose mark{background:color-mix(in srgb,var(--orange) 30%,transparent);color:inherit;border-radius:3px;padding:0 .1em}
.row .snippet{flex-basis:100%;color:var(--label-2);font-size:.8125rem;margin-top:.2rem}
.row.stack{flex-wrap:wrap}
.empty{background:var(--group);border-radius:12px;padding:2.4rem 1.4rem;text-align:center;color:var(--label-2)}
.empty strong{display:block;color:var(--label);font-size:1.0625rem;margin-bottom:.3rem}

/* buttons, fields, switches */
.btn,button{appearance:none;font:500 .875rem/1.1 var(--font);letter-spacing:-.005em;padding:.5rem .95rem;min-height:2.1rem;
  border-radius:8px;border:0;background:var(--fill);color:var(--accent);cursor:pointer;display:inline-flex;
  align-items:center;justify-content:center;gap:.35rem;text-decoration:none}
.btn:hover,button:hover{background:color-mix(in srgb,var(--fill) 100%,var(--label) 6%);text-decoration:none}
.btn.primary,button.primary{background:var(--accent);color:#fff}
.btn.primary:hover,button.primary:hover{background:color-mix(in srgb,var(--accent) 88%,#000)}
.btn.danger,button.danger{color:var(--red)}
button:disabled{opacity:.4;cursor:default}
button.link{background:none;padding:0;min-height:0;border-radius:4px}
.row>button.link{padding:.2rem 0}
input[type=text],input[type=password],input[type=url],input[type=number],input[type=search],select,textarea{
  font:400 .9375rem/1.3 var(--font);color:var(--label);background:var(--group);border:1px solid var(--sep);border-radius:8px;
  padding:.5rem .7rem;max-width:100%;min-height:2.1rem}
input:focus,select:focus,textarea:focus{outline:none;border-color:var(--accent);box-shadow:0 0 0 3px var(--accent-soft)}
select{appearance:none;-webkit-appearance:none;padding-right:1.8rem;cursor:pointer;
  background:var(--group) url("__UPDOWN__") no-repeat right .6rem center/.5rem auto}
.row select,.row input[type=number]{text-align:right}
input.switch{appearance:none;-webkit-appearance:none;width:2.5rem;height:1.5rem;border-radius:1.5rem;margin:0;flex:none;
  background:var(--fill);position:relative;cursor:pointer;transition:background .18s}
input.switch::after{content:"";position:absolute;top:.125rem;left:.125rem;width:1.25rem;height:1.25rem;border-radius:50%;
  background:#fff;box-shadow:0 1px 3px rgba(0,0,0,.25);transition:transform .18s}
input.switch:checked{background:var(--accent)}
input.switch:checked::after{transform:translateX(1rem)}
.pick input{position:absolute;opacity:0;pointer-events:none}
.pick .tick{width:1.1rem;flex:none;display:flex;justify-content:center}
.pick input:checked~.tick::after{content:"";width:.35rem;height:.7rem;margin-top:-.2rem;border:solid var(--accent);
  border-width:0 2.4px 2.4px 0;transform:rotate(45deg)}
.pick:has(input:focus-visible){outline:3px solid color-mix(in srgb,var(--accent) 55%,transparent);outline-offset:-3px}
.toolbar{display:flex;flex-wrap:wrap;gap:.5rem;align-items:center}
.inline{display:inline}
.search{margin:0 0 1rem}
.search input{width:100%;border:0;background:var(--fill) url("__SEARCH__") no-repeat .6rem center/.85rem auto;
  padding:.45rem .7rem .45rem 1.9rem;min-height:2rem;font-size:.875rem}
.search input:focus{box-shadow:0 0 0 3px var(--accent-soft)}
.seg{display:inline-flex;gap:2px;padding:2px;border-radius:9px;background:var(--fill);max-width:100%;overflow-x:auto;margin:1.8rem 0 1rem}
.seg button{background:none;color:var(--label);font:500 .8125rem/1 var(--font);padding:.42rem 1rem;min-height:0;border-radius:7px;white-space:nowrap}
.seg button:hover{background:var(--fill-2)}
.seg button[aria-selected=true]{background:var(--seg-on);box-shadow:0 1px 3px rgba(0,0,0,.14),0 0 0 .5px rgba(0,0,0,.05)}
.spin{width:.9rem;height:.9rem;flex:none;border-radius:50%;border:2px solid var(--fill);border-top-color:var(--label-2);
  animation:spin .9s linear infinite}
@keyframes spin{to{transform:rotate(360deg)}}

/* notices */
.notice{display:flex;gap:.7rem;align-items:flex-start;border-radius:12px;padding:.8rem 1rem;margin:0 0 1.2rem;
  background:color-mix(in srgb,var(--orange) 13%,var(--group))}
.notice::before{content:"!";flex:none;display:grid;place-items:center;width:1.15rem;height:1.15rem;margin-top:.08rem;
  border-radius:50%;background:var(--orange);color:#fff;font:700 .75rem/1 var(--font)}
.notice.bad{background:color-mix(in srgb,var(--red) 11%,var(--group))}
.notice.bad::before{background:var(--red)}
.notice.good{background:color-mix(in srgb,var(--green) 14%,var(--group))}
.notice.good::before{content:"✓";background:var(--green)}
.notice>div{flex:1;min-width:0}
.notice form,.notice .toolbar{margin-top:.6rem}

/* a lecture */
.back{display:inline-flex;align-items:center;gap:.3rem;font-size:.9375rem;margin:0 0 .9rem}
.back::before{content:"";width:.5rem;height:.5rem;border-left:2px solid currentColor;border-bottom:2px solid currentColor;
  transform:rotate(45deg);margin-left:.15rem}
.tag{display:inline-flex;align-items:center;gap:.4rem}
.classpick{display:inline-flex;align-items:center;gap:.4rem;margin:0}
.classpick select{border:0;background-color:transparent;background-position:right 0 center;padding:0 1rem 0 0;min-height:0;
  color:var(--label);font-size:.875rem;field-sizing:content;max-width:24rem;text-align:left}
.classpick select:focus{box-shadow:none}
.classpick:focus-within{outline:3px solid color-mix(in srgb,var(--accent) 55%,transparent);outline-offset:3px;border-radius:6px}
.tag::before{content:"";width:.55rem;height:.55rem;border-radius:50%;background:var(--tag,var(--gray))}
.about{display:flex;flex-wrap:wrap;gap:.2rem 1.4rem;margin:.3rem 0 1.3rem;padding:0;color:var(--label-2);font-size:.875rem}
.about dt{display:none}
.about dd{margin:0}
.sheet{background:var(--group);border-radius:12px;padding:1.5rem clamp(1.1rem,4vw,2.4rem) 1.8rem}
.prose{font:400 1.0625rem/1.6 var(--font);letter-spacing:-.012em;max-width:68ch;overflow-wrap:break-word}
.prose>:first-child{margin-top:0}
.prose h1,.prose h2,.prose h3,.prose h4{line-height:1.25;letter-spacing:-.018em;margin:1.8rem 0 .5rem}
.prose h1{font-size:1.5rem;font-weight:700}.prose h2{font-size:1.25rem;font-weight:700}
.prose h3{font-size:1.0625rem;font-weight:600}.prose h4{font-size:1rem;font-weight:600}
.prose ul,.prose ol{padding-left:1.3rem}.prose li{margin:.3rem 0}
.prose code{background:var(--fill-2);padding:.1em .3em;border-radius:4px}
.prose pre{background:var(--fill-2);padding:.9rem 1rem;border-radius:10px;overflow-x:auto;line-height:1.45}
.prose pre code{background:none;padding:0}
.prose blockquote{margin:1rem 0;padding-left:1rem;border-left:3px solid var(--sep);color:var(--label-2)}
.prose table{border-collapse:collapse;display:block;overflow-x:auto}
.prose td,.prose th{border:1px solid var(--sep);padding:.35rem .6rem}
.prose .katex-display{overflow-x:auto;overflow-y:hidden;padding:.3rem 0}
.byline{font-size:.8125rem;color:var(--label-2);margin:1.6rem 0 0}
.transcript{font:400 1rem/1.65 var(--font);max-width:72ch;white-space:pre-wrap;overflow-wrap:break-word}

/* settings */
.fields{display:grid;gap:.5rem;padding:.8rem 1rem}
.class-edit{display:grid;grid-template-columns:minmax(0,1.1fr) minmax(0,1fr) auto;gap:.45rem .5rem;align-items:center}
.class-edit .wide{grid-column:1/3}
.class-edit input{background:var(--fill-2);border-color:transparent}
.class-edit input:first-child{font-weight:600}
.class-edit label.remove{display:flex;align-items:center;gap:.45rem;font-size:.8125rem;color:var(--label-2)}
@media (max-width:560px){.class-edit{grid-template-columns:minmax(0,1fr) auto}.class-edit input:nth-child(2),.class-edit .wide{grid-column:1/2}}
.code{font:400 .8125rem/1.5 var(--mono);background:var(--fill-2);border-radius:8px;padding:.6rem .8rem;margin:0;
  white-space:pre-wrap;word-break:break-all}
.actions{display:flex;justify-content:flex-end;gap:.6rem;margin:1.2rem 0 0}

/* sign in */
.login{min-height:100vh;display:grid;place-items:center;padding:1rem}
.login form{width:min(21rem,100%);display:grid;gap:.75rem;text-align:center}
.login .appicon{width:4rem;height:4rem;margin:0 auto .4rem;border-radius:.95rem;display:grid;place-items:center;color:#fff;
  font:700 1.6rem/1 var(--font);background:linear-gradient(180deg,#5ac8fa,#007aff);box-shadow:0 4px 14px rgba(0,122,255,.3)}
.login h1{font-size:1.375rem;margin:0}
.login p{margin:0}
.login input{width:100%;text-align:center}
.login .bad{color:var(--red)}

@media (prefers-reduced-motion:reduce){*{scroll-behavior:auto!important;transition:none!important}.spin{animation-duration:2.4s}}
""".replace("__UPDOWN__", _SVG_UPDOWN).replace("__SEARCH__", _SVG_SEARCH)

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
document.querySelectorAll('form[data-autosubmit]').forEach(function(f){
  f.querySelectorAll('.js-hide').forEach(function(b){b.hidden=true;});
  f.addEventListener('change',function(){f.submit();});});
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
    """A stable tag color per class (0-11, Apple's palette); Unsorted gets none (gray)."""
    if not name or name == UNSORTED:
        return None
    return int(hashlib.sha1(name.encode()).hexdigest()[:4], 16) % TAG_COLORS


def hue_style(name: str | None) -> str:
    h = hue(name)
    return "--tag:var(--gray)" if h is None else f"--tag:var(--c{h})"


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
            f'<meta name="viewport" content="width=device-width,initial-scale=1,viewport-fit=cover">'
            f'<meta name="color-scheme" content="light dark">{ICON_LINKS}'
            f'<title>{esc(title)}</title>{extra}<style nonce="{nonce}">{CSS}</style></head>')


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

    def item(href, label, n=None, key=None, style=None):
        current = ' aria-current="page"' if key is not None and key == cur else ""  # Log out has no key
        dot = f'<span class="dot" style="{style}"></span>' if style is not None else ""
        count = f'<span class="n">{n}</span>' if n is not None else ""
        return f'<a href="{href}"{current}>{dot}<span class="name">{esc(label)}</span>{count}</a>'

    items = [item("/", "Recent", key="home")]
    if ctx.get("processing"):
        items.append(item("/#queue", "Being written", ctx["processing"], key="queue"))
    items.append('<div class="nav-head">Classes</div>')
    for name, n in classes:
        if name != UNSORTED:
            items.append(item(class_url(name), name, n, key=f"class:{name}", style=hue_style(name)))
    items.append(item("/unsorted", "Unsorted", dict(classes).get(UNSORTED, 0), key="unsorted", style=hue_style(UNSORTED)))
    foot = []
    if ctx.get("admin"):
        foot.append(item("/settings", "Settings", key="settings"))
    if ctx.get("password"):
        foot.append(item("/logout", "Log out"))
    total = ctx["total"]
    return (f'<aside class="side"><a class="brand" href="/">{esc(ctx["pool_name"])}'
            f'<small>{total} lecture{"s" if total != 1 else ""}</small></a>'
            f'<form class="search" action="/search" role="search"><input type="search" name="q" '
            f'value="{esc(ctx.get("q", ""))}" placeholder="Search" aria-label="Search lectures"></form>'
            f'<nav class="nav" aria-label="Classes">{"".join(items)}</nav>'
            f'<div class="side-foot nav">{"".join(foot)}<span class="ver">Version {esc(ctx.get("version", ""))}</span></div>'
            f'</aside>')


def topbar(ctx: dict) -> str:
    """Phones: the sidebar folds away; a back button and the page's name take its place."""
    back = ctx.get("back")
    left = (f'<a class="back" style="margin:0" href="{back[0]}">{esc(back[1])}</a>' if back
            else "<span></span>")  # home: the large title below names the library, as on iOS
    right = '<a href="/settings">Settings</a>' if ctx.get("admin") and ctx.get("current") != "settings" else ""
    return f'<header class="topbar">{left}{right}</header>'


def page(title: str, body: str, ctx: dict, *, math: bool = False) -> str:
    nonce = ctx["nonce"]
    return (head(f"{title} · {ctx['pool_name']}" if title != ctx["pool_name"] else title, nonce, math)
            + f'<body><div class="app">{sidebar(ctx)}{topbar(ctx)}<main class="main"><div class="wrap">{body}</div></main></div>'
            + scripts(nonce, math) + "</body></html>")


# The Study Stash icon on every page: the browser tab, the taskbar button of an Edge or Chrome app window
# (that's the Windows app), and an iPhone's home screen.
ICON_LINKS = ('<link rel="icon" href="/favicon.ico" sizes="any"><link rel="icon" type="image/png" href="/icon.png">'
              '<link rel="apple-touch-icon" href="/apple-touch-icon.png">')
ICON_FILES = {"/favicon.ico": ("study-stash.ico", "image/x-icon"), "/icon.png": ("icon.png", "image/png"),
              "/apple-touch-icon.png": ("apple-touch-icon.png", "image/png")}


def add_icon_routes(app) -> None:
    """Serve the icon files; they need no sign-in."""
    from pathlib import Path

    from fastapi import Response

    assets = Path(__file__).resolve().parent / "assets"

    def route(name: str, kind: str):
        def icon():
            try:
                data = (assets / name).read_bytes()
            except OSError:
                return Response(status_code=404)
            return Response(data, media_type=kind, headers={"Cache-Control": "public, max-age=86400"})
        return icon

    for path, (name, kind) in ICON_FILES.items():
        app.get(path, include_in_schema=False)(route(name, kind))


def bare_page(title: str, body: str, nonce: str) -> str:
    return head(title, nonce) + f"<body>{body}{scripts(nonce)}</body></html>"


# --- shared pieces ------------------------------------------------------------

def search_box(q: str = "") -> str:
    return (f'<form class="search" action="/search" role="search"><input type="search" name="q" value="{esc(q)}" '
            f'placeholder="Search titles, summaries, and transcripts" aria-label="Search lectures"></form>')


def note_row(r, snippet: str = "") -> str:
    topics = ", ".join(json.loads(r["topics"] or "[]")[:3])
    about = [f'<time datetime="{esc((r["date"] or "")[:10])}">{esc(short_date(r["date"]))}</time>',
             f'<span class="tag" style="{hue_style(r["class_name"])}">{esc(r["class_name"])}</span>']
    if topics:
        about.append(f"<span>{esc(topics)}</span>")
    snip = f'<div class="snippet">{snippet}</div>' if snippet else ""
    return (f'<a class="row chev" href="/note/{quote(r["id"], safe="")}"><div class="grow">'
            f'<div class="title">{esc(r["lecture_title"] or r["title"])}</div>'
            f'<div class="subtitle">{"".join(about)}</div>{snip}</div></a>')


def note_list(rows, empty_title: str, empty_text: str, snippets: dict | None = None) -> str:
    if not rows:
        return f'<div class="empty"><strong>{esc(empty_title)}</strong>{esc(empty_text)}</div>'
    snippets = snippets or {}
    return '<div class="group">' + "".join(note_row(r, snippets.get(r["id"], "")) for r in rows) + "</div>"


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
