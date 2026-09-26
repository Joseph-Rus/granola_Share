using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using StudyStash.Core;
using StudyStash.Core.Ai;

namespace StudyStash.Library;

/// <summary>Capture and the Inbox: jot something down; the AI files it under its class.</summary>
public sealed partial class LibraryWeb
{
    Inbox? inbox;

    public Inbox LibraryInbox => inbox ??= options.Inbox ?? new Inbox(cfg.PoolDir, () => cfg.ClassNames(), c => store.ClassDir(c),
        options.Ai ?? new AiJobs(cfg.Home, () => cfg.OllamaHost), LibraryHistory);

    void MapInbox(WebApplication app)
    {
        app.MapPost("/api/v2/capture", Http.Handle(ctx => ApiAsync(ctx, async () =>
        {
            var body = await Http.JsonBodyAsync(ctx.Request);
            string text = Py.Strip(S(body?["text"]));
            if (text.Length == 0) return Http.Detail(400, "nothing to keep");
            string path = LibraryInbox.Capture(text, S(body?["class"]) is { Length: > 0 } c ? c : null);
            return Http.Json(new JsonObject { ["saved"] = Path.GetRelativePath(cfg.PoolDir, path).Replace('\\', '/'), ["waiting"] = LibraryInbox.Waiting().Count });
        })));
        app.MapGet("/api/v2/inbox", (HttpContext ctx) => Api(ctx, () => Http.Json(new JsonArray(LibraryInbox.Waiting().Select(p => (JsonNode)new JsonObject
        {
            ["name"] = Path.GetFileNameWithoutExtension(p), ["text"] = File.ReadAllText(p),
        }).ToArray()))));
        app.MapPost("/api/v2/inbox/file", Http.Handle(ctx => ApiAsync(ctx, async () => Http.Json(new JsonObject { ["said"] = await LibraryInbox.FileAsync() }))));

        app.MapGet("/inbox", (HttpContext ctx, string? kept, string? said) => WithMember(ctx, role => InboxPage(role, kept, said)));
        app.MapPost("/inbox", Http.Handle(ctx => WithMemberAsync(ctx, async _ =>
        {
            var f = await Http.FormAsync(ctx.Request);
            string text = Py.Strip(f.Get("text"));
            if (text.Length == 0) return Http.SeeOther("/inbox");
            string cls = f.Get("class");
            LibraryInbox.Capture(text, cls.Length > 0 ? cls : null);
            return Http.SeeOther("/inbox?kept=" + (cls.Length > 0 ? Uri.EscapeDataString(cls) : "inbox"));
        })));
        app.MapPost("/inbox/file", Http.Handle(ctx => WithMemberAsync(ctx, async _ =>
            Http.SeeOther("/inbox?said=" + Uri.EscapeDataString(Py.Head(await LibraryInbox.FileAsync(), 600))))));
    }

    IResult InboxPage(string role, string? kept, string? said)
    {
        var c = Context(role, "inbox", ("/", cfg.PoolName));
        var waiting = LibraryInbox.Waiting();
        bool ready = cfg.OllamaEnabled || !AiSettings.Load(cfg.Home).Local("sort");
        string why = "Sorting needs an AI: turn one on in Settings.";
        string flash = kept switch
        {
            null => "",
            "inbox" => ready ? "<div class=\"notice good\"><div>Kept. It's filed under its class in a minute.</div></div>"
                : "<div class=\"notice good\"><div>Kept in the Inbox.</div></div>",
            _ => $"<div class=\"notice good\"><div>Kept in {Ui.Esc(kept)}'s notes.</div></div>",
        };
        if (said is not null) flash += $"<div class=\"notice\"><div>{Ui.Esc(said)}</div></div>";
        string options_ = "<option value=\"\">Let the AI file it</option>" + string.Concat(cfg.ClassNames().Select(n => $"<option value=\"{Ui.Esc(n)}\">{Ui.Esc(n)}</option>"));
        string form = "<form method=\"post\" action=\"/inbox\" class=\"stack\">"
            + "<textarea name=\"text\" rows=\"4\" placeholder=\"A thought, a question for office hours, a link, a reading note\" aria-label=\"What to keep\" style=\"width:100%;font:inherit;padding:.7rem .9rem;border-radius:.9rem\"></textarea>"
            + $"<div class=\"toolbar\" style=\"justify-content:space-between\"><select name=\"class\" aria-label=\"Class\">{options_}</select><button class=\"primary\">Keep</button></div></form>";
        string list = waiting.Count == 0 ? ""
            : "<h2>Waiting to be filed</h2><div class=\"group\">" + string.Concat(waiting.Select(p =>
                $"<div class=\"row\"><div class=\"grow\"><div class=\"title\">{Ui.Esc(Path.GetFileNameWithoutExtension(p) is { Length: > 16 } n ? n[16..].Trim() : "Note")}</div>"
                + $"<div class=\"subtitle\">{Ui.Esc(Py.Head(File.ReadAllText(p).Split("\n\n", 2).ElementAtOrDefault(1) ?? "", 160))}</div></div></div>")) + "</div>"
              + (ready ? "<form method=\"post\" action=\"/inbox/file\" class=\"actions\"><button>File them now</button></form>"
                  : $"<p class=\"group-foot\">{Ui.Esc(why)} Until then, they wait here.</p>");
        return Show("Capture", $"<h1>Capture</h1><p class=\"sub\">Jot it down now; it's filed under its class for you.</p>{flash}{form}{list}", c);
    }
}
