using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using StudyStash.Core;
using StudyStash.Core.Ai;

namespace StudyStash.Library;

/// <summary>
/// Chat with the library's AI (pages and /api/v2/chat), and History: what the AI changed, with Undo. Answers stream
/// as newline-delimited JSON: first {"chat": id}, then each event ({"kind", "text", "name", "path"}).
/// </summary>
public sealed partial class LibraryWeb
{
    Chats? chats;
    History? history;

    History LibraryHistory => history ??= new History(cfg.PoolDir);

    /// <summary>The folders (outside the library) the AI may read, from folders.json beside config.toml.</summary>
    public Func<IReadOnlyList<string>> ReadDirs => () => Folders.Load(cfg.Home).Where(f => f.Ai && !f.Private).Select(f => f.Path).Where(Directory.Exists).ToList();

    Chats LibraryChats => chats ??= new Chats(cfg.Home, cfg.PoolDir, options.Ai ?? new AiJobs(cfg.Home, () => cfg.OllamaHost), LibraryHistory, ReadDirs);

    /// <summary>Chat is in the sidebar once an AI is picked in Settings (or a chat exists).</summary>
    bool ChatOn => File.Exists(AiSettings.PathIn(cfg.Home)) || Directory.Exists(Path.Combine(cfg.Home, "chats"));

    static JsonObject EventJson(AiEvent e) => new() { ["kind"] = e.Kind, ["text"] = e.Text, ["name"] = e.Name, ["path"] = e.Path };

    /// <summary>Run one turn and stream it out.</summary>
    async Task StreamTurn(HttpContext ctx, JsonObject? body)
    {
        string message = Py.Strip(S(body?["message"]));
        if (message.Length == 0)
        {
            ctx.Response.StatusCode = 400;
            await ctx.Response.WriteAsJsonAsync(new { detail = "what's the question?" });
            return;
        }
        var chat = S(body?["chat"]) is { Length: > 0 } id ? LibraryChats.Get(id) : null;
        chat ??= LibraryChats.New(S(body?["class"]), S(body?["lecture"]));
        bool edit = body?["edit"] is JsonValue ev && ev.TryGetValue(out bool b) && b;
        ctx.Response.ContentType = "application/x-ndjson; charset=utf-8";
        ctx.Response.Headers.CacheControl = "no-store";
        async Task Line(JsonNode n)
        {
            await ctx.Response.WriteAsync(n.ToJsonString() + "\n", Encoding.UTF8, ctx.RequestAborted);
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
        }
        await Line(new JsonObject { ["chat"] = chat.Id });
        try
        {
            await foreach (var e in LibraryChats.TurnAsync(chat, message, edit, ctx.RequestAborted))
                if (e.Kind != "session") await Line(EventJson(e));
            await Line(new JsonObject { ["kind"] = "done", ["text"] = chat.Messages[^1].Text });
        }
        catch (OperationCanceledException)
        {
            // they closed the page; the chat keeps what was said
        }
    }

    static JsonObject ChatJson(Chat c, bool full) => new()
    {
        ["id"] = c.Id, ["title"] = c.Title, ["class"] = c.ClassName, ["lecture"] = c.Lecture, ["updated"] = c.Updated, ["provider"] = c.Provider,
        ["messages"] = full ? new JsonArray(c.Messages.Select(m => (JsonNode)new JsonObject
        {
            ["role"] = m.Role, ["text"] = m.Text, ["when"] = m.When, ["tools"] = new JsonArray(m.Tools.Select(t => (JsonNode)t).ToArray()),
            ["changed"] = new JsonArray(m.Changed.Select(t => (JsonNode)t).ToArray()), ["change"] = m.Change, ["failed"] = m.Failed,
        }).ToArray()) : null,
    };

    void MapChat(WebApplication app)
    {
        // The app and other clients (the library password).
        app.MapPost("/api/v2/chat", async (HttpContext ctx) =>
        {
            if (RequireKey(ctx) is { } no)
            {
                await no.ExecuteAsync(ctx);
                return;
            }
            await StreamTurn(ctx, await Http.JsonBodyAsync(ctx.Request));
        });
        app.MapGet("/api/v2/chats", (HttpContext ctx) => Api(ctx, () => Http.Json(new JsonArray(LibraryChats.List().Select(c => (JsonNode)ChatJson(c, false)).ToArray()))));
        app.MapGet("/api/v2/chats/{id}", (HttpContext ctx, string id) => Api(ctx, () => LibraryChats.Get(id) is { } c ? Http.Json(ChatJson(c, true)) : Http.Detail(404, "no such chat")));
        app.MapDelete("/api/v2/chats/{id}", (HttpContext ctx, string id) => Api(ctx, () => LibraryChats.Delete(id) ? Http.Json(new JsonObject { ["deleted"] = id }) : Http.Detail(404, "no such chat")));
        app.MapGet("/api/v2/history", (HttpContext ctx) => Api(ctx, () => Http.Json(new JsonArray(LibraryHistory.Log().Select(ch => (JsonNode)new JsonObject
        {
            ["id"] = ch.Sha, ["when"] = ch.When, ["title"] = ch.Title, ["by"] = ch.By, ["files"] = new JsonArray(ch.Files.Select(f => (JsonNode)f).ToArray()),
        }).ToArray()))));
        app.MapPost("/api/v2/history/{sha}/undo", (HttpContext ctx, string sha) => Api(ctx, () =>
        {
            if (!LibraryHistory.Log(500).Any(c => c.Sha == sha)) return Http.Detail(404, "no such change");
            var (ok, why) = LibraryHistory.Undo(sha);
            return ok ? Http.Json(new JsonObject { ["undone"] = sha }) : Http.Detail(409, why);
        }));

        // The pages (the web sign-in; the header keeps other sites from posting here).
        app.MapPost("/chat/send", async (HttpContext ctx) =>
        {
            if (RoleOf(ctx) is null || ctx.Request.Headers["X-Study-Stash"] != "1")
            {
                ctx.Response.StatusCode = 403;
                return;
            }
            await StreamTurn(ctx, await Http.JsonBodyAsync(ctx.Request));
        });
        app.MapGet("/chat", (HttpContext ctx, string? @class, string? lecture) => WithMember(ctx, role => ChatPage(role, null, @class ?? "", lecture ?? "")));
        app.MapGet("/chat/{id}", (HttpContext ctx, string id) => WithMember(ctx, role => LibraryChats.Get(id) is { } c ? ChatPage(role, c, c.ClassName, c.Lecture) : NotFound(role, "chat")));
        app.MapPost("/chat/{id}/delete", (HttpContext ctx, string id) => WithMember(ctx, _ =>
        {
            LibraryChats.Delete(id);
            return Http.SeeOther("/chat");
        }));
        app.MapPost("/api/v2/terminal", Http.Handle(ctx => ApiAsync(ctx, async () =>
        {
            if (!IsLocal(ctx)) return Http.Detail(403, "Open it on the library's own computer.");
            var (ok, said) = OpenTerminal(S((await Http.JsonBodyAsync(ctx.Request))?["class"]));
            return ok ? Http.Json(new JsonObject { ["said"] = said }) : Http.Detail(409, said);
        })));
        app.MapPost("/terminal", Http.Handle(ctx => WithMemberAsync(ctx, async _ =>
        {
            string cls = (await Http.FormAsync(ctx.Request)).Get("class");
            if (!IsLocal(ctx)) return Http.SeeOther(Ui.ClassUrl(cls));
            var (ok, said) = OpenTerminal(cls);
            return Http.SeeOther(Ui.ClassUrl(cls) + "?said=" + Uri.EscapeDataString(said));
        })));
        app.MapGet("/history", (HttpContext ctx, string? undone, string? problem) => WithMember(ctx, role => HistoryPage(role, undone, problem)));
        app.MapPost("/history/{sha}/undo", (HttpContext ctx, string sha) => WithMember(ctx, _ =>
        {
            if (!LibraryHistory.Log(500).Any(c => c.Sha == sha)) return Http.SeeOther("/history");
            var (ok, why) = LibraryHistory.Undo(sha);
            return Http.SeeOther(ok ? $"/history?undone={sha}" : "/history?problem=" + Uri.EscapeDataString(why));
        }));
        MapAi(app);
    }

    IResult ChatPage(string role, Chat? chat, string cls, string lecture)
    {
        var c = Context(role, "chat", ("/", cfg.PoolName));
        var agent = AiSettings.Load(cfg.Home).For("agent");
        string who = AiProviders.Get(agent.Provider).Name;
        var (ready, why) = (options.Ai ?? new AiJobs(cfg.Home)).AgentReady();
        var sb = new StringBuilder();
        string scope = lecture.Length > 0 ? "this lecture" : cls.Length > 0 ? cls : "all your classes";
        sb.Append($"<h1>{(chat is null ? "Chat" : Ui.Esc(chat.Title))}</h1><p class=\"sub\">With {Ui.Esc(who)}, about {Ui.Esc(scope)}. "
            + "<a href=\"/history\">What the AI changed</a></p>");
        if (!ready) sb.Append($"<div class=\"notice\"><div>{Ui.Esc(why)} Pick another AI in <a href=\"/settings\">Settings</a>.</div></div>");
        if (chat is null)
        {
            var recent = LibraryChats.List(20);
            if (recent.Count > 0)
                sb.Append("<h2>Recent chats</h2><div class=\"group\">" + string.Concat(recent.Select(r =>
                    $"<a class=\"row\" href=\"/chat/{r.Id}\"><div class=\"grow\"><div class=\"title\">{Ui.Esc(r.Title)}</div>"
                    + $"<div class=\"subtitle\">{Ui.Esc(r.ClassName.Length > 0 ? r.ClassName + " · " : "")}{Ui.Esc(History.Ago(r.Updated, DateTime.Now))} · {r.Messages.Count / 2} question{(r.Messages.Count / 2 == 1 ? "" : "s")}</div></div></a>")) + "</div>");
        }
        sb.Append("<div class=\"chat\" id=\"thread\">");
        foreach (var m in chat?.Messages ?? [])
        {
            if (m.Role == "user")
            {
                sb.Append($"<div class=\"msg me\">{Ui.Esc(m.Text).ReplaceLineEndings("<br>")}</div>");
                continue;
            }
            string used = m.Tools.Count > 0 ? $"<details class=\"used\"><summary>Looked at {m.Tools.Count} thing{(m.Tools.Count == 1 ? "" : "s")}</summary><ul>{string.Concat(m.Tools.Select(t => $"<li>{Ui.Esc(Py.Head(t, 140))}</li>"))}</ul></details>" : "";
            string changed = m.Changed.Count > 0
                ? $"<div class=\"notice good\"><div>Changed {string.Join(", ", m.Changed.Select(f => Ui.Esc(f)))}"
                  + (m.Change.Length > 0 ? $"<form method=\"post\" action=\"/history/{Ui.Esc(m.Change)}/undo\" data-confirm=\"Undo this change?\" style=\"display:inline\"> <button class=\"link\">Undo</button></form>" : "") + "</div></div>"
                : "";
            sb.Append($"<div class=\"msg ai{(m.Failed ? " failed" : "")}\"><article class=\"prose\">{Ui.RenderMd(m.Text)}</article>{used}{changed}</div>");
        }
        sb.Append("</div>");
        sb.Append($"<form class=\"asker\" id=\"asker\" data-chat=\"{Ui.Esc(chat?.Id ?? "")}\" data-class=\"{Ui.Esc(cls)}\" data-lecture=\"{Ui.Esc(lecture)}\">"
            + $"<textarea name=\"message\" rows=\"2\" placeholder=\"{(chat is null ? "Ask anything about your classes" : "Ask a follow-up")}\" aria-label=\"Message\"></textarea>"
            + "<div class=\"toolbar\" style=\"justify-content:space-between\"><label class=\"row\" style=\"padding:0;gap:.5rem\">"
            + "<input class=\"switch\" type=\"checkbox\" name=\"edit\" value=\"1\"><span>Let it write files (you can undo)</span></label>"
            + "<button class=\"primary\">Send</button></div></form>");
        if (chat is not null)
            sb.Append($"<form method=\"post\" action=\"/chat/{chat.Id}/delete\" data-confirm=\"Delete this chat?\" style=\"margin-top:2rem\"><button class=\"danger\">Delete chat</button></form>");
        sb.Append($"<style>{ChatCss}</style><script nonce=\"{c.Nonce}\">{ChatJs}</script>");
        return Show(chat?.Title ?? "Chat", sb.ToString(), c, math: true);
    }

    const string ChatCss =
        ".chat{display:flex;flex-direction:column;gap:1rem;margin:1.2rem 0}"
        + ".msg.me{align-self:flex-end;max-width:80%;background:var(--fill,rgba(127,127,127,.14));padding:.6rem .9rem;border-radius:1rem}"
        + ".msg.ai .note{margin:0}.msg.ai.failed{color:var(--red)}"
        + ".msg .live{white-space:pre-wrap}"
        + ".used{font-size:.8125rem;color:var(--label-2);margin-top:.4rem}.used ul{margin:.3rem 0 0;padding-left:1.1rem}"
        + ".asker{position:sticky;bottom:0;padding:.8rem 0 1rem;background:var(--bg,Canvas)}"
        + ".asker textarea{width:100%;resize:vertical;font:inherit;padding:.7rem .9rem;border-radius:.9rem}"
        + ".asker .toolbar{margin-top:.5rem}";

    // Sends the message, shows the answer as it comes, then reloads the chat so the answer is shown as a page.
    const string ChatJs =
        "(function(){var f=document.getElementById('asker');if(!f)return;var t=document.getElementById('thread');\n"
        + "var ta=f.querySelector('textarea');ta.addEventListener('keydown',function(e){if(e.key==='Enter'&&!e.shiftKey){e.preventDefault();f.requestSubmit();}});\n"
        + "f.addEventListener('submit',async function(e){e.preventDefault();var msg=ta.value.trim();if(!msg)return;ta.value='';\n"
        + "var me=document.createElement('div');me.className='msg me';me.textContent=msg;t.appendChild(me);\n"
        + "var ai=document.createElement('div');ai.className='msg ai';var live=document.createElement('div');live.className='live';live.textContent='Thinking…';ai.appendChild(live);\n"
        + "var used=document.createElement('div');used.className='used';ai.appendChild(used);t.appendChild(ai);ai.scrollIntoView({block:'end'});\n"
        + "f.querySelector('button').disabled=true;var id=f.dataset.chat,text='',n=0;\n"
        + "try{var r=await fetch('/chat/send',{method:'POST',headers:{'Content-Type':'application/json','X-Study-Stash':'1'},\n"
        + "body:JSON.stringify({message:msg,chat:id,'class':f.dataset['class'],lecture:f.dataset.lecture,edit:f.edit.checked})});\n"
        + "var rd=r.body.getReader(),dec=new TextDecoder(),buf='';\n"
        + "for(;;){var x=await rd.read();if(x.done)break;buf+=dec.decode(x.value,{stream:true});var lines=buf.split('\\n');buf=lines.pop();\n"
        + "lines.forEach(function(l){if(!l)return;var ev=JSON.parse(l);if(ev.chat){id=ev.chat;return;}\n"
        + "if(ev.kind==='text'){text+=ev.text;live.textContent=text;}else if(ev.kind==='tool'){n++;used.textContent='Looked at '+n+(n===1?' thing':' things')+'…';}\n"
        + "else if(ev.kind==='error'){live.textContent=ev.text;ai.classList.add('failed');}});}\n"
        + "}catch(err){live.textContent='The library stopped answering.';}\n"
        + "location.href='/chat/'+id;});})();";

    /// <summary>Open a terminal with the agent AI in a class's folder (or the library's), on this computer only.</summary>
    (bool Ok, string Said) OpenTerminal(string? cls)
    {
        try
        {
            string folder = cls is { Length: > 0 } && cfg.ClassNames().Contains(cls) ? store.ClassDir(cls) : cfg.PoolDir;
            var ai = AiSettings.Load(cfg.Home);
            string name = Terminal.Open(cfg.Home, folder, cfg.PoolDir, cls is { Length: > 0 } ? cls : null, ai.Terminal, ai.For("agent").Provider,
                (options.Ai ?? new AiJobs(cfg.Home)).McpCommand);
            return (true, $"Opened in {name}.");
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return (false, e.Message);
        }
    }

    /// <summary>Files in a class's folder that aren't its lectures (a study guide the AI wrote, your own notes), for
    /// its page. Empty when there are none.</summary>
    string ClassFiles(string name)
    {
        string dir = store.ClassDir(name);
        var lectures = store.ListNotes(name).Select(r => r.MdPath).Where(p => !string.IsNullOrEmpty(p)).Select(p => Path.GetFullPath(p!)).ToHashSet();
        string notes = Path.Combine(dir, "Notes");
        var files = Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly)
            .Concat(Directory.Exists(notes) ? Directory.EnumerateFiles(notes, "*", SearchOption.AllDirectories) : [])
            .Where(f => !lectures.Contains(Path.GetFullPath(f)) && !Path.GetFileName(f).StartsWith('.'))
            .OrderByDescending(File.GetLastWriteTime).ToList();
        if (files.Count == 0) return "";
        string cq = Ui.Quote(name, "");
        return "<h2>Files</h2><div class=\"group\">" + string.Concat(files.Select(f =>
            $"<a class=\"row\" href=\"/files/{cq}/{Ui.Quote(Path.GetRelativePath(dir, f).Replace('\\', '/'), "/")}\"><div class=\"grow\"><div class=\"title\">{Ui.Esc(Path.GetFileNameWithoutExtension(f))}</div>"
            + $"<div class=\"subtitle\">{Ui.Esc(File.GetLastWriteTime(f).ToString("ddd d MMM, h:mm tt", CultureInfo.InvariantCulture))}</div></div></a>")) + "</div>";
    }

    IResult HistoryPage(string role, string? undone, string? problem)
    {
        var c = Context(role, "chat", ("/chat", "Chat"));
        var log = LibraryHistory.Log();
        var sb = new StringBuilder("<h1>What the AI changed</h1><p class=\"sub\">Each change the AI made to your library, newest first. Undo puts the files back as they were.</p>");
        if (undone is not null) sb.Append("<div class=\"notice good\"><div>Undone.</div></div>");
        if (problem is not null) sb.Append($"<div class=\"notice\"><div>{Ui.Esc(problem)}</div></div>");
        if (!LibraryHistory.Available) sb.Append("<div class=\"notice\"><div>Git isn't on this computer, so changes can't be undone. Install it (on a Mac: xcode-select --install).</div></div>");
        if (log.Count == 0) sb.Append("<div class=\"empty\"><strong>Nothing yet</strong>When you let the AI write files in a chat, what it changed shows here.</div>");
        else
            sb.Append("<div class=\"group\">" + string.Concat(log.Select(ch =>
                $"<div class=\"row\"><div class=\"grow\"><div class=\"title\">{Ui.Esc(ch.Title)}</div>"
                + $"<div class=\"subtitle\">{Ui.Esc(ch.By)} · {Ui.Esc(History.Ago(ch.When, DateTime.Now))} · {Ui.Esc(string.Join(", ", ch.Files.Take(4)))}{(ch.Files.Count > 4 ? $" and {ch.Files.Count - 4} more" : "")}</div></div>"
                + (ch.By == "undo" ? "" : $"<form method=\"post\" action=\"/history/{Ui.Esc(ch.Sha)}/undo\" data-confirm=\"Undo this change?\"><button>Undo</button></form>") + "</div>")) + "</div>");
        return Show("History", sb.ToString(), c);
    }
}
