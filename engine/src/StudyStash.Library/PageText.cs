// Made by engine/tests/golden.py from the Python engine's own text: run that again rather than edit this.
// (The pages must look the same whichever engine serves them.)

namespace StudyStash.Library;

internal static class PageText
{
    public const string Css =
        "\n" +
        ":root{\n" +
        "  --canvas:#f5f5f7; --group:#fff; --sidebar:#ececef; --fill:rgba(118,118,128,.12); --fill-2:rgba(118,118,128,.07);\n" +
        "  --label:#1d1d1f; --label-2:rgba(60,60,67,.64); --label-3:rgba(60,60,67,.3); --sep:rgba(60,60,67,.17);\n" +
        "  --accent:#007aff; --accent-soft:rgba(0,122,255,.14); --sel:rgba(0,0,0,.075); --seg-on:#fff;\n" +
        "  --red:#ff3b30; --orange:#ff9500; --green:#34c759; --gray:#8e8e93;\n" +
        "  --c0:#ff3b30; --c1:#ff9500; --c2:#ffcc00; --c3:#34c759; --c4:#00c7be; --c5:#30b0c7;\n" +
        "  --c6:#32ade6; --c7:#007aff; --c8:#5856d6; --c9:#af52de; --c10:#ff2d55; --c11:#a2845e;\n" +
        "  --font:-apple-system,BlinkMacSystemFont,\"SF Pro Text\",\"Helvetica Neue\",\"Segoe UI\",Roboto,system-ui,sans-serif;\n" +
        "  --mono:ui-monospace,\"SF Mono\",SFMono-Regular,Menlo,Consolas,monospace;\n" +
        "  color-scheme:light;\n" +
        "}\n" +
        "@media (prefers-color-scheme:dark){:root{\n" +
        "  --canvas:#1c1c1e; --group:#2c2c2e; --sidebar:#232325; --fill:rgba(118,118,128,.26); --fill-2:rgba(118,118,128,.14);\n" +
        "  --label:#f5f5f7; --label-2:rgba(235,235,245,.62); --label-3:rgba(235,235,245,.28); --sep:rgba(84,84,88,.62);\n" +
        "  --accent:#0a84ff; --accent-soft:rgba(10,132,255,.24); --sel:rgba(255,255,255,.1); --seg-on:#636366;\n" +
        "  --red:#ff453a; --orange:#ff9f0a; --green:#30d158;\n" +
        "  --c0:#ff453a; --c1:#ff9f0a; --c2:#ffd60a; --c3:#30d158; --c4:#63e6e2; --c5:#40c8e0;\n" +
        "  --c6:#64d2ff; --c7:#0a84ff; --c8:#5e5ce6; --c9:#bf5af2; --c10:#ff375f; --c11:#ac8e68;\n" +
        "  color-scheme:dark;\n" +
        "}}\n" +
        "*{box-sizing:border-box}\n" +
        "html{-webkit-text-size-adjust:100%}\n" +
        "body{margin:0;background:var(--canvas);color:var(--label);font:400 15px/1.42 var(--font);letter-spacing:-.008em;\n" +
        "  -webkit-font-smoothing:antialiased}\n" +
        "a{color:var(--accent);text-decoration:none}\n" +
        "a:hover{text-decoration:underline}\n" +
        ":focus-visible{outline:3px solid color-mix(in srgb,var(--accent) 55%,transparent);outline-offset:1px;border-radius:7px}\n" +
        "[hidden]{display:none!important}\n" +
        "\n" +
        "/* the window: sidebar + content, like Notes */\n" +
        ".app{display:grid;grid-template-columns:17rem minmax(0,1fr);min-height:100vh}\n" +
        ".side{position:sticky;top:0;height:100vh;overflow-y:auto;background:var(--sidebar);border-right:1px solid var(--sep);\n" +
        "  padding:1.1rem .65rem 1rem;display:flex;flex-direction:column}\n" +
        ".brand{display:block;padding:0 .55rem;margin:0 0 .9rem;color:var(--label);font:700 1.0625rem/1.25 var(--font)}\n" +
        ".brand:hover{text-decoration:none}\n" +
        ".brand small{display:block;font-weight:400;font-size:.8125rem;color:var(--label-2);margin-top:.1rem}\n" +
        ".nav{display:flex;flex-direction:column;gap:1px}\n" +
        ".nav-head{font:600 .6875rem/1 var(--font);color:var(--label-2);padding:1.1rem .55rem .45rem}\n" +
        ".nav a{display:flex;align-items:center;gap:.55rem;padding:.36rem .55rem;border-radius:6px;color:var(--label);font-size:.875rem}\n" +
        ".nav a:hover{background:var(--fill-2);text-decoration:none}\n" +
        ".nav a[aria-current=page]{background:var(--sel);font-weight:600}\n" +
        ".nav .name{flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}\n" +
        ".nav .n{color:var(--label-2);font-size:.8125rem;font-weight:400;font-variant-numeric:tabular-nums}\n" +
        ".dot{width:.625rem;height:.625rem;border-radius:50%;background:var(--tag,var(--gray));flex:none}\n" +
        ".side-foot{margin-top:auto;padding-top:1rem}\n" +
        ".side-foot .ver{display:block;padding:.5rem .55rem 0;font-size:.75rem;color:var(--label-3)}\n" +
        ".main{min-width:0;padding:2.2rem clamp(1rem,4vw,3rem) 5rem}\n" +
        ".wrap{max-width:46rem;margin:0 auto}\n" +
        ".topbar{display:none}\n" +
        "@media (max-width:760px){\n" +
        "  .app{display:block}\n" +
        "  .side{display:none}\n" +
        "  .topbar{display:flex;position:sticky;top:0;z-index:5;align-items:center;justify-content:space-between;gap:1rem;\n" +
        "    min-height:2.9rem;padding:.4rem 1rem;background:color-mix(in srgb,var(--canvas) 82%,transparent);\n" +
        "    -webkit-backdrop-filter:saturate(180%) blur(20px);backdrop-filter:saturate(180%) blur(20px);border-bottom:1px solid var(--sep)}\n" +
        "  .topbar .t{font-weight:600;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}\n" +
        "  .main{padding:1.2rem 1rem 4rem}\n" +
        "  .only-wide{display:none!important}\n" +
        "}\n" +
        "@media (min-width:761px){.only-narrow{display:none!important}}\n" +
        "\n" +
        "/* type: Apple's scale */\n" +
        "h1{font:700 2.125rem/1.18 var(--font);letter-spacing:-.024em;margin:0 0 .35rem;text-wrap:balance}\n" +
        "h2{font:700 1.25rem/1.25 var(--font);letter-spacing:-.017em;margin:2.2rem 0 .7rem}\n" +
        ".sub{color:var(--label-2);margin:0 0 1.6rem;max-width:62ch}\n" +
        ".muted{color:var(--label-2)}\n" +
        ".small{font-size:.8125rem}\n" +
        "code{font:400 .88em var(--mono)}\n" +
        "\n" +
        "/* grouped lists */\n" +
        ".group{background:var(--group);border-radius:12px;overflow:hidden;margin:0}\n" +
        ".group-head{font:600 .8125rem/1.3 var(--font);color:var(--label-2);margin:1.9rem 1rem .5rem}\n" +
        ".group-foot{font-size:.8125rem;line-height:1.38;color:var(--label-2);margin:.5rem 1rem 0;max-width:62ch}\n" +
        "details.help{margin:.2rem 0 1rem}\n" +
        "details.help summary{cursor:pointer;color:var(--accent);font-size:.8125rem;margin:0 1rem .5rem}\n" +
        ".row{position:relative;display:flex;align-items:center;gap:.75rem;min-height:2.75rem;padding:.6rem 1rem;color:var(--label)}\n" +
        ".group>*{position:relative}\n" +
        ".group>*+*::before{content:\"\";position:absolute;top:0;left:1rem;right:0;border-top:1px solid var(--sep)}\n" +
        "a.row:hover,label.row:hover{background:var(--fill-2);text-decoration:none}\n" +
        "label.row{cursor:pointer}\n" +
        ".grow{flex:1;min-width:0}\n" +
        ".value{color:var(--label-2);text-align:right;overflow-wrap:anywhere}\n" +
        ".value.bad{color:var(--red)}\n" +
        ".value.good{color:var(--label-2)}\n" +
        ".title{font-weight:600;text-wrap:pretty}\n" +
        ".subtitle{display:flex;flex-wrap:wrap;gap:.1rem .75rem;color:var(--label-2);font-size:.8125rem;margin-top:.12rem}\n" +
        ".chev::after{content:\"\";display:block;flex:none;width:.45rem;height:.45rem;margin:0 .15rem 0 .3rem;\n" +
        "  border-top:2px solid var(--label-3);border-right:2px solid var(--label-3);transform:rotate(45deg)}\n" +
        ".row mark,.prose mark{background:color-mix(in srgb,var(--orange) 30%,transparent);color:inherit;border-radius:3px;padding:0 .1em}\n" +
        ".row .snippet{flex-basis:100%;color:var(--label-2);font-size:.8125rem;margin-top:.2rem}\n" +
        ".row.stack{flex-wrap:wrap}\n" +
        ".empty{background:var(--group);border-radius:12px;padding:2.4rem 1.4rem;text-align:center;color:var(--label-2)}\n" +
        ".empty strong{display:block;color:var(--label);font-size:1.0625rem;margin-bottom:.3rem}\n" +
        "\n" +
        "/* buttons, fields, switches */\n" +
        ".btn,button{appearance:none;font:500 .875rem/1.1 var(--font);letter-spacing:-.005em;padding:.5rem .95rem;min-height:2.1rem;\n" +
        "  border-radius:8px;border:0;background:var(--fill);color:var(--accent);cursor:pointer;display:inline-flex;\n" +
        "  align-items:center;justify-content:center;gap:.35rem;text-decoration:none}\n" +
        ".btn:hover,button:hover{background:color-mix(in srgb,var(--fill) 100%,var(--label) 6%);text-decoration:none}\n" +
        ".btn.primary,button.primary{background:var(--accent);color:#fff}\n" +
        ".btn.primary:hover,button.primary:hover{background:color-mix(in srgb,var(--accent) 88%,#000)}\n" +
        ".btn.danger,button.danger{color:var(--red)}\n" +
        "button:disabled{opacity:.4;cursor:default}\n" +
        "button.link{background:none;padding:0;min-height:0;border-radius:4px}\n" +
        ".row>button.link{padding:.2rem 0}\n" +
        "input[type=text],input[type=password],input[type=url],input[type=number],input[type=search],select,textarea{\n" +
        "  font:400 .9375rem/1.3 var(--font);color:var(--label);background:var(--group);border:1px solid var(--sep);border-radius:8px;\n" +
        "  padding:.5rem .7rem;max-width:100%;min-height:2.1rem}\n" +
        "input:focus,select:focus,textarea:focus{outline:none;border-color:var(--accent);box-shadow:0 0 0 3px var(--accent-soft)}\n" +
        "select{appearance:none;-webkit-appearance:none;padding-right:1.8rem;cursor:pointer;\n" +
        "  background:var(--group) url(\"data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 8 12'%3E%3Cpath d='M1 4.5 4 1.5 7 4.5M1 7.5 4 10.5 7 7.5' fill='none' stroke='%238E8E93' stroke-width='1.5' stroke-linecap='round' stroke-linejoin='round'/%3E%3C/svg%3E\") no-repeat right .6rem center/.5rem auto}\n" +
        ".row select,.row input[type=number]{text-align:right}\n" +
        "input.switch{appearance:none;-webkit-appearance:none;width:2.5rem;height:1.5rem;border-radius:1.5rem;margin:0;flex:none;\n" +
        "  background:var(--fill);position:relative;cursor:pointer;transition:background .18s}\n" +
        "input.switch::after{content:\"\";position:absolute;top:.125rem;left:.125rem;width:1.25rem;height:1.25rem;border-radius:50%;\n" +
        "  background:#fff;box-shadow:0 1px 3px rgba(0,0,0,.25);transition:transform .18s}\n" +
        "input.switch:checked{background:var(--accent)}\n" +
        "input.switch:checked::after{transform:translateX(1rem)}\n" +
        ".pick input{position:absolute;opacity:0;pointer-events:none}\n" +
        ".pick .tick{width:1.1rem;flex:none;display:flex;justify-content:center}\n" +
        ".pick input:checked~.tick::after{content:\"\";width:.35rem;height:.7rem;margin-top:-.2rem;border:solid var(--accent);\n" +
        "  border-width:0 2.4px 2.4px 0;transform:rotate(45deg)}\n" +
        ".pick:has(input:focus-visible){outline:3px solid color-mix(in srgb,var(--accent) 55%,transparent);outline-offset:-3px}\n" +
        ".toolbar{display:flex;flex-wrap:wrap;gap:.5rem;align-items:center}\n" +
        ".inline{display:inline}\n" +
        ".search{margin:0 0 1rem}\n" +
        ".search input{width:100%;border:0;background:var(--fill) url(\"data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 16 16'%3E%3Ccircle cx='6.5' cy='6.5' r='5' fill='none' stroke='%238E8E93' stroke-width='1.7'/%3E%3Cpath d='M10.3 10.3 14.5 14.5' stroke='%238E8E93' stroke-width='1.7' stroke-linecap='round'/%3E%3C/svg%3E\") no-repeat .6rem center/.85rem auto;\n" +
        "  padding:.45rem .7rem .45rem 1.9rem;min-height:2rem;font-size:.875rem}\n" +
        ".search input:focus{box-shadow:0 0 0 3px var(--accent-soft)}\n" +
        ".seg{display:inline-flex;gap:2px;padding:2px;border-radius:9px;background:var(--fill);max-width:100%;overflow-x:auto;margin:1.8rem 0 1rem}\n" +
        ".seg button{background:none;color:var(--label);font:500 .8125rem/1 var(--font);padding:.42rem 1rem;min-height:0;border-radius:7px;white-space:nowrap}\n" +
        ".seg button:hover{background:var(--fill-2)}\n" +
        ".seg button[aria-selected=true]{background:var(--seg-on);box-shadow:0 1px 3px rgba(0,0,0,.14),0 0 0 .5px rgba(0,0,0,.05)}\n" +
        ".spin{width:.9rem;height:.9rem;flex:none;border-radius:50%;border:2px solid var(--fill);border-top-color:var(--label-2);\n" +
        "  animation:spin .9s linear infinite}\n" +
        "@keyframes spin{to{transform:rotate(360deg)}}\n" +
        "\n" +
        "/* notices */\n" +
        ".notice{display:flex;gap:.7rem;align-items:flex-start;border-radius:12px;padding:.8rem 1rem;margin:0 0 1.2rem;\n" +
        "  background:color-mix(in srgb,var(--orange) 13%,var(--group))}\n" +
        ".notice::before{content:\"!\";flex:none;display:grid;place-items:center;width:1.15rem;height:1.15rem;margin-top:.08rem;\n" +
        "  border-radius:50%;background:var(--orange);color:#fff;font:700 .75rem/1 var(--font)}\n" +
        ".notice.bad{background:color-mix(in srgb,var(--red) 11%,var(--group))}\n" +
        ".notice.bad::before{background:var(--red)}\n" +
        ".notice.good{background:color-mix(in srgb,var(--green) 14%,var(--group))}\n" +
        ".notice.good::before{content:\"\u2713\";background:var(--green)}\n" +
        ".notice>div{flex:1;min-width:0}\n" +
        ".notice form,.notice .toolbar{margin-top:.6rem}\n" +
        "\n" +
        "/* a lecture */\n" +
        ".back{display:inline-flex;align-items:center;gap:.3rem;font-size:.9375rem;margin:0 0 .9rem}\n" +
        ".back::before{content:\"\";width:.5rem;height:.5rem;border-left:2px solid currentColor;border-bottom:2px solid currentColor;\n" +
        "  transform:rotate(45deg);margin-left:.15rem}\n" +
        ".tag{display:inline-flex;align-items:center;gap:.4rem}\n" +
        ".classpick{display:inline-flex;align-items:center;gap:.4rem;margin:0}\n" +
        ".classpick select{border:0;background-color:transparent;background-position:right 0 center;padding:0 1rem 0 0;min-height:0;\n" +
        "  color:var(--label);font-size:.875rem;field-sizing:content;max-width:24rem;text-align:left}\n" +
        ".classpick select:focus{box-shadow:none}\n" +
        ".classpick:focus-within{outline:3px solid color-mix(in srgb,var(--accent) 55%,transparent);outline-offset:3px;border-radius:6px}\n" +
        ".tag::before{content:\"\";width:.55rem;height:.55rem;border-radius:50%;background:var(--tag,var(--gray))}\n" +
        ".about{display:flex;flex-wrap:wrap;gap:.2rem 1.4rem;margin:.3rem 0 1.3rem;padding:0;color:var(--label-2);font-size:.875rem}\n" +
        ".about dt{display:none}\n" +
        ".about dd{margin:0}\n" +
        ".sheet{background:var(--group);border-radius:12px;padding:1.5rem clamp(1.1rem,4vw,2.4rem) 1.8rem}\n" +
        ".prose{font:400 1.0625rem/1.6 var(--font);letter-spacing:-.012em;max-width:68ch;overflow-wrap:break-word}\n" +
        ".prose>:first-child{margin-top:0}\n" +
        ".prose h1,.prose h2,.prose h3,.prose h4{line-height:1.25;letter-spacing:-.018em;margin:1.8rem 0 .5rem}\n" +
        ".prose h1{font-size:1.5rem;font-weight:700}.prose h2{font-size:1.25rem;font-weight:700}\n" +
        ".prose h3{font-size:1.0625rem;font-weight:600}.prose h4{font-size:1rem;font-weight:600}\n" +
        ".prose ul,.prose ol{padding-left:1.3rem}.prose li{margin:.3rem 0}\n" +
        ".prose code{background:var(--fill-2);padding:.1em .3em;border-radius:4px}\n" +
        ".prose pre{background:var(--fill-2);padding:.9rem 1rem;border-radius:10px;overflow-x:auto;line-height:1.45}\n" +
        ".prose pre code{background:none;padding:0}\n" +
        ".prose blockquote{margin:1rem 0;padding-left:1rem;border-left:3px solid var(--sep);color:var(--label-2)}\n" +
        ".prose table{border-collapse:collapse;display:block;overflow-x:auto}\n" +
        ".prose td,.prose th{border:1px solid var(--sep);padding:.35rem .6rem}\n" +
        ".prose .katex-display{overflow-x:auto;overflow-y:hidden;padding:.3rem 0}\n" +
        ".byline{font-size:.8125rem;color:var(--label-2);margin:1.6rem 0 0}\n" +
        ".transcript{font:400 1rem/1.65 var(--font);max-width:72ch;white-space:pre-wrap;overflow-wrap:break-word}\n" +
        "\n" +
        "/* settings */\n" +
        ".fields{display:grid;gap:.5rem;padding:.8rem 1rem}\n" +
        ".class-edit{display:grid;grid-template-columns:minmax(0,1.1fr) minmax(0,1fr) auto;gap:.45rem .5rem;align-items:center}\n" +
        ".class-edit .wide{grid-column:1/3}\n" +
        ".class-edit input{background:var(--fill-2);border-color:transparent}\n" +
        ".class-edit input:first-child{font-weight:600}\n" +
        ".class-edit label.remove{display:flex;align-items:center;gap:.45rem;font-size:.8125rem;color:var(--label-2)}\n" +
        "@media (max-width:560px){.class-edit{grid-template-columns:minmax(0,1fr) auto}.class-edit input:nth-child(2),.class-edit .wide{grid-column:1/2}}\n" +
        ".code{font:400 .8125rem/1.5 var(--mono);background:var(--fill-2);border-radius:8px;padding:.6rem .8rem;margin:0;\n" +
        "  white-space:pre-wrap;word-break:break-all}\n" +
        ".actions{display:flex;justify-content:flex-end;gap:.6rem;margin:1.2rem 0 0}\n" +
        "\n" +
        "/* sign in */\n" +
        ".login{min-height:100vh;display:grid;place-items:center;padding:1rem}\n" +
        ".login form{width:min(21rem,100%);display:grid;gap:.75rem;text-align:center}\n" +
        ".login .appicon{width:4rem;height:4rem;margin:0 auto .4rem;border-radius:.95rem;display:grid;place-items:center;color:#fff;\n" +
        "  font:700 1.6rem/1 var(--font);background:linear-gradient(180deg,#5ac8fa,#007aff);box-shadow:0 4px 14px rgba(0,122,255,.3)}\n" +
        ".login h1{font-size:1.375rem;margin:0}\n" +
        ".login p{margin:0}\n" +
        ".login input{width:100%;text-align:center}\n" +
        ".login .bad{color:var(--red)}\n" +
        "\n" +
        "@media (prefers-reduced-motion:reduce){*{scroll-behavior:auto!important;transition:none!important}.spin{animation-duration:2.4s}}\n";

    public const string Js =
        "\n" +
        "document.querySelectorAll('[data-tabs]').forEach(function(bar){\n" +
        "  var btns=bar.querySelectorAll('button[data-for]');\n" +
        "  function show(id){btns.forEach(function(b){var on=b.dataset.for===id;b.setAttribute('aria-selected',on);\n" +
        "    document.getElementById(b.dataset.for).hidden=!on;});}\n" +
        "  btns.forEach(function(b){b.addEventListener('click',function(){show(b.dataset.for);history.replaceState(null,'','#'+b.dataset.for);});});\n" +
        "  var want=location.hash.slice(1);show([].some.call(btns,function(b){return b.dataset.for===want;})?want:btns[0].dataset.for);\n" +
        "});\n" +
        "document.querySelectorAll('[data-copy]').forEach(function(b){b.addEventListener('click',function(){\n" +
        "  navigator.clipboard.writeText(document.getElementById(b.dataset.copy).textContent.trim()).then(function(){\n" +
        "    var t=b.textContent;b.textContent='Copied';setTimeout(function(){b.textContent=t;},1500);});});});\n" +
        "document.querySelectorAll('form[data-confirm]').forEach(function(f){f.addEventListener('submit',function(e){\n" +
        "  if(!confirm(f.dataset.confirm))e.preventDefault();});});\n" +
        "document.querySelectorAll('form[data-autosubmit]').forEach(function(f){\n" +
        "  f.querySelectorAll('.js-hide').forEach(function(b){b.hidden=true;});\n" +
        "  f.addEventListener('change',function(){f.submit();});});\n" +
        "var q=document.querySelector('[data-refresh]');\n" +
        "if(q){setTimeout(function(){location.reload();},parseInt(q.dataset.refresh,10)*1000);}\n";

    public const string MathJs =
        "\n" +
        "if(window.renderMathInElement){document.querySelectorAll('.prose').forEach(function(el){renderMathInElement(el,{\n" +
        "  delimiters:[{left:'$$',right:'$$',display:true},{left:'$',right:'$',display:false},{left:'\\\\(',right:'\\\\)',display:false},{left:'\\\\[',right:'\\\\]',display:true}],\n" +
        "  throwOnError:false});});}\n";

    public const string Katex =
        "https://cdn.jsdelivr.net/npm/katex@0.16.22/dist";

    public const string IconLinks =
        "<link rel=\"icon\" href=\"/favicon.ico\" sizes=\"any\"><link rel=\"icon\" type=\"image/png\" href=\"/icon.png\"><link rel=\"apple-touch-icon\" href=\"/apple-touch-icon.png\">";

    public const string LibraryCsp =
        "default-src 'self'; script-src 'nonce-{nonce}' https://cdn.jsdelivr.net; style-src 'self' 'unsafe-inline' https://fonts.googleapis.com https://cdn.jsdelivr.net; font-src https://fonts.gstatic.com https://cdn.jsdelivr.net; img-src 'self' data:; form-action 'self'; frame-ancestors 'none'; base-uri 'none'";

    public const string AppCss =
        "\n" +
        ".solo{max-width:40rem;margin:0 auto;padding:2.6rem 1.1rem 5rem}\n" +
        ".solo>header{margin-bottom:1.6rem}\n" +
        ".stack{display:grid;gap:.55rem}\n" +
        ".stack input{width:100%}\n" +
        ".say{font-size:.8125rem;margin:.5rem 0 0;color:var(--label-2);min-height:1em}\n" +
        ".say:empty{display:none}\n" +
        ".say.good{color:var(--green)}\n" +
        ".say.bad{color:var(--red)}\n" +
        ".fields>p{margin:0 0 .6rem;max-width:60ch}\n" +
        ".fields>p:last-child{margin-bottom:0}\n" +
        ".step{margin:0 0 1.4rem}\n" +
        ".step-head{display:flex;align-items:center;gap:.65rem;margin:0 0 .55rem .2rem}\n" +
        ".step-head h2{margin:0;font-size:1.0625rem;font-weight:600;letter-spacing:-.01em}\n" +
        ".step-n{width:1.5rem;height:1.5rem;flex:none;border-radius:50%;display:grid;place-items:center;font:600 .8125rem/1 var(--font);\n" +
        "  color:var(--label-2);border:1.5px solid var(--label-3)}\n" +
        ".step.done .step-n{background:var(--green);border-color:var(--green);color:#fff}\n" +
        ".step.locked{opacity:.42;pointer-events:none}\n" +
        ".group>button.row{width:100%;border-radius:0;background:none;justify-content:flex-start;font:400 .9375rem/1.3 var(--font);\n" +
        "  color:var(--accent);text-align:left}\n" +
        ".group>button.row:hover{background:var(--fill-2)}\n" +
        ".group>button.row.danger{color:var(--red)}\n" +
        "details.help{margin-top:.8rem;font-size:.8125rem}\n" +
        "details.help summary{cursor:pointer;color:var(--accent)}\n" +
        "details.help p{margin:.5rem 0}\n" +
        "details.help .code{margin:.3rem 0 .5rem}\n";

    public const string AppJs =
        "\n" +
        "function post(url, data){\n" +
        "  return fetch(url,{method:'POST',headers:{'Content-Type':'application/json','X-Study-Stash':'1'},\n" +
        "    body:JSON.stringify(data||{})}).then(function(r){return r.json().then(function(j){if(!r.ok)throw new Error(j.detail||r.statusText);return j;});});\n" +
        "}\n" +
        "function say(id, text, kind){var el=document.getElementById(id);if(el){el.textContent=text;el.className='say '+(kind||'');}}\n" +
        "document.querySelectorAll('[data-action]').forEach(function(el){\n" +
        "  el.addEventListener(el.tagName==='FORM'?'submit':'click',function(e){\n" +
        "    e.preventDefault();\n" +
        "    var data={};\n" +
        "    if(el.tagName==='FORM'){new FormData(el).forEach(function(v,k){data[k]=v;});\n" +
        "      el.querySelectorAll('input[type=checkbox]').forEach(function(c){data[c.name]=c.checked;});}\n" +
        "    if(el.dataset.confirm&&!confirm(el.dataset.confirm))return;\n" +
        "    var out=el.dataset.out;\n" +
        "    if(out)say(out, el.dataset.busy||'Working\u2026','wait');\n" +
        "    post(el.dataset.action,data).then(function(j){\n" +
        "      if(out)say(out,j.message||'Done.','good');\n" +
        "      if(j.reload!==false)setTimeout(function(){location.reload();}, j.delay||600);\n" +
        "    }).catch(function(err){if(out)say(out,err.message,'bad');});\n" +
        "  });\n" +
        "});\n" +
        "document.querySelectorAll('[data-copy]').forEach(function(b){b.addEventListener('click',function(){\n" +
        "  navigator.clipboard.writeText(document.getElementById(b.dataset.copy).textContent.trim()).then(function(){\n" +
        "    var t=b.textContent;b.textContent='Copied';setTimeout(function(){b.textContent=t;},1500);});});});\n" +
        "var watch=document.body.dataset.watch;\n" +
        "document.querySelectorAll('form[data-autosave]').forEach(function(f){\n" +
        "  f.addEventListener('change',function(){f.requestSubmit();});});\n" +
        "if(watch){var seen=null;setInterval(function(){fetch('/api/state',{headers:{'X-Study-Stash':'1'}}).then(function(r){return r.json();})\n" +
        "  .then(function(s){var key=JSON.stringify([s.signed_in,s.login.running,s.login.error,s.copy.allowed,s.watching,s.recent_key,s.problem,s.ready]);\n" +
        "    if(seen!==null&&key!==seen)location.reload();seen=key;});}, parseInt(watch,10)*1000);}\n";

    public const string AppCsp =
        "default-src 'self'; script-src 'nonce-{nonce}'; style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; font-src https://fonts.gstatic.com; img-src 'self' data:; form-action 'self'; frame-ancestors 'none'; base-uri 'none'";

    public const string SetupCss =
        "\n" +
        ".field{display:grid;gap:.25rem;font-size:.8125rem;color:var(--label-2)}\n" +
        ".field input,.field select{font-size:.9375rem;color:var(--label)}\n" +
        ".two{display:grid;grid-template-columns:1fr 1fr;gap:.55rem}\n" +
        "@media (max-width:520px){.two{grid-template-columns:1fr}}\n" +
        "progress{width:100%;height:.45rem;margin:.6rem 0 0;accent-color:var(--accent)}\n" +
        "progress:not([value]){display:none}\n" +
        ".addr{font:500 .9375rem/1.4 ui-monospace,Menlo,Consolas,monospace;user-select:all}\n";

    public const string SetupJs =
        "\n" +
        "(function(){var seen=null;function tick(){fetch('/api/state',{headers:{'X-Study-Stash':'1'}}).then(function(r){return r.json();})\n" +
        ".then(function(s){Object.keys(s.jobs).forEach(function(k){var j=s.jobs[k];\n" +
        "  var bar=document.querySelector('progress[data-job=\"'+k+'\"]');\n" +
        "  if(bar){if(j.running&&j.total){bar.max=j.total;bar.value=j.done;}else{bar.removeAttribute('value');}}\n" +
        "  var note=document.querySelector('[data-job-note=\"'+k+'\"]');\n" +
        "  if(note&&j.note){note.textContent=j.note;note.className='say '+(j.error?'bad':j.running?'wait':'good');}});\n" +
        "  if(seen!==null&&s.key!==seen)location.reload();seen=s.key;}).catch(function(){});}\n" +
        "tick();setInterval(tick,1500);})();\n";
}
