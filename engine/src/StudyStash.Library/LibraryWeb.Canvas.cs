using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using StudyStash.Core;
using StudyStash.Core.Canvas;

namespace StudyStash.Library;

/// <summary>
/// Canvas in the library: the Chrome extension's door (it takes the extension's own key, or the library password),
/// the API the app and Claude's tools use, and the pages: Due, each class's assignments, and Canvas in Settings.
/// </summary>
public sealed partial class LibraryWeb
{
    CanvasSync? canvas;

    /// <summary>One per library: the extension and AIs' reads meet in its queue.</summary>
    public CanvasSync Canvas => canvas ??= options.Canvas ?? new CanvasSync(cfg.Home, c => store.ClassDir(c))
    {
        KnownClass = c => cfg.ClassNames().Contains(c),
    };

    /// <summary>The extension's key (X-Study-Stash-Key), or the library password like the rest of the API.</summary>
    IResult? RequireExtension(HttpContext ctx) =>
        CanvasSettings.KeyMatches(cfg.Home, ctx.Request.Headers["X-Study-Stash-Key"].ToString()) ? null : RequireKey(ctx);

    static string S(JsonNode? v) => v is JsonValue j && j.TryGetValue(out string? s) ? s : "";

    /// <summary>The most one post of the extension's answers may carry: a 40 MiB file is about 56 MB of base64, and an
    /// extension from before protocol 2 posts up to six answers together.</summary>
    const long ResultsLimit = 256L * 1024 * 1024;

    void MapCanvas(WebApplication app)
    {
        // An updated Study Stash brings the extension folder it made up to date; Chrome's copy reloads itself from it.
        try
        {
            if (Extension.Refresh(Extension.Folder(cfg.Home))) Console.WriteLine($"[canvas] the Chrome extension's folder is now version {Extension.Version()}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"[canvas] couldn't update the Chrome extension's folder: {e.Message}");
        }

        // The extension. It says its version (v) and protocol (p); one from before protocol 2 sends no p.
        app.MapGet("/api/v2/canvas/work", (HttpContext ctx, int? force, string? v, int? p) =>
        {
            if (RequireExtension(ctx) is { } no) return no;
            var w = Canvas.Work(force is 1, v, p ?? 1);
            return Http.Json(new JsonObject
            {
                ["jobs"] = new JsonArray(w.Jobs.Select(j => (JsonNode)new JsonObject { ["id"] = j.Id, ["url"] = j.Url, ["kind"] = j.Kind }).ToArray()),
                ["hot"] = w.Hot, ["ext"] = w.Ext, ["p"] = Extension.Protocol,
            });
        });
        app.MapPost("/api/v2/canvas/results", Http.Handle(async ctx =>
        {
            if (RequireExtension(ctx) is { } no) return no;
            // Files come as base64 inside JSON: past Kestrel's 30 MB default for a big one.
            if (ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = ResultsLimit;
            var body = await Http.JsonBodyAsync(ctx.Request);
            Canvas.Results((body?["results"] as JsonArray ?? []).OfType<JsonObject>().Select(CanvasResult.From).ToList());
            return Http.Json(new JsonObject { ["ok"] = true });
        }));
        app.MapGet("/api/v2/canvas/status", (HttpContext ctx) =>
        {
            if (RequireExtension(ctx) is { } no) return no;
            var s = Canvas.Settings;
            var (waiting, inflight) = Canvas.Crawl.Left;
            return Http.Json(new JsonObject
            {
                ["synced"] = s.LastSync, ["error"] = s.Error,
                ["busy"] = Canvas.Crawl.Active ? $"Syncing Canvas… {waiting + inflight} left" : "",
            });
        });

        // The app, and Claude's tools.
        app.MapGet("/api/v2/canvas", (HttpContext ctx) => Api(ctx, () => Http.Json(CanvasJson())));
        app.MapPost("/api/v2/canvas", Http.Handle(ctx => ApiAsync(ctx, async () =>
        {
            var body = await Http.JsonBodyAsync(ctx.Request);
            if (S(body?["url"]) is { Length: > 0 } typed && CanvasSettings.CleanUrl(typed) is null) return Http.Detail(400, "That isn't a web address.");
            if (body?["courses"] is JsonObject courses && courses.Any(kv => !cfg.ClassNames().Contains(kv.Key)))
                return Http.Detail(400, "Link Canvas courses to classes this library has.");
            CanvasSettings.Update(cfg.Home, s =>
            {
                if (S(body?["url"]) is { Length: > 0 } u) s.Url = CanvasSettings.CleanUrl(u)!;
                if (body?["courses"] is JsonObject cs)
                    foreach (var (cls, id) in cs)
                    {
                        if (id is JsonValue n && n.TryGetValue(out long cid) && cid > 0) s.Courses[cls] = cid;
                        else s.Courses.Remove(cls);
                    }
                if (body?["sync"] is JsonValue sv && sv.TryGetValue(out bool now) && now) s.SyncNow = true;
                if (body?["poll_minutes"] is JsonValue pv && pv.TryGetValue(out double pm)) s.PollMinutes = Math.Clamp((int)pm, 15, 1440);
                if (body?["dismiss_update"] is JsonValue dv && dv.TryGetValue(out bool dismiss) && dismiss && s.ExtensionUpdate is { } noted)
                    s.ExtensionUpdate = noted with { Dismissed = true };
            });
            return Http.Json(CanvasJson());
        })));
        app.MapGet("/api/v2/canvas/state", (HttpContext ctx) => Api(ctx, () => Http.Json(CanvasView.State(Canvas, Canvas.Clock()))));
        app.MapGet("/api/v2/canvas/classes", (HttpContext ctx) => Api(ctx, () =>
            Http.Json(CanvasView.Classes(cfg.ClassNames(), Canvas.Settings, cfg.Home, ScoutOf, Canvas.Clock()))));
        app.MapGet("/api/v2/canvas/due", (HttpContext ctx) => Api(ctx, () =>
        {
            var s = Canvas.Settings;
            return Http.Json(CanvasView.Due(Assignments.Load(cfg.Home), s.LastDone, Canvas.Clock(), Canvas.Zone, Canvas.Crawl.AssignmentFolder));
        }));
        app.MapGet("/api/v2/canvas/assignments", (HttpContext ctx, string? @class) => Api(ctx, () =>
        {
            if (@class is null || !cfg.ClassNames().Contains(@class)) return Http.Detail(400, "unknown class");
            var mine = Assignments.Load(cfg.Home).Where(a => a.ClassName == @class).ToList();
            return Http.Json(CanvasView.ForClass(@class, mine, id => Canvas.Crawl.AssignmentFolder(@class, id)));
        }));
        app.MapGet("/api/v2/canvas/assignment", (HttpContext ctx, string? @class, long? id) => Api(ctx, () =>
        {
            if (@class is null || id is null || !cfg.ClassNames().Contains(@class)) return Http.Detail(404, "no such assignment");
            var found = CanvasView.Assignment(CourseIndex.Load(cfg.Home, @class), id.Value, Canvas.Clock(), Canvas.Crawl.AssignmentFolder(@class, id.Value));
            return found is null ? Http.Detail(404, "no such assignment") : Http.Json(found);
        }));
        app.MapGet("/api/v2/canvas/modules", (HttpContext ctx, string? @class) => Api(ctx, () =>
            @class is null || !cfg.ClassNames().Contains(@class) ? Http.Detail(400, "unknown class") : Http.Json(CanvasView.Modules(CourseIndex.Load(cfg.Home, @class)))));
        app.MapGet("/api/v2/canvas/files", (HttpContext ctx, string? @class) => Api(ctx, () =>
            @class is null || !cfg.ClassNames().Contains(@class) ? Http.Detail(400, "unknown class") : Http.Json(CanvasView.Files(CourseIndex.Load(cfg.Home, @class)))));
        app.MapGet("/api/v2/canvas/pages", (HttpContext ctx, string? @class) => Api(ctx, () =>
            @class is null || !cfg.ClassNames().Contains(@class) ? Http.Detail(400, "unknown class") : Http.Json(CanvasView.Pages(CourseIndex.Load(cfg.Home, @class)))));
        app.MapGet("/api/v2/canvas/announcements", (HttpContext ctx, string? @class) => Api(ctx, () =>
        {
            if (@class is null || !cfg.ClassNames().Contains(@class)) return Http.Detail(400, "unknown class");
            return Http.Json(CanvasView.Announcements(CourseIndex.Load(cfg.Home, @class), CanvasSeen.For(cfg.Home, @class)));
        }));
        app.MapPost("/api/v2/canvas/announcements/seen", Http.Handle(ctx => ApiAsync(ctx, async () =>
        {
            var body = await Http.JsonBodyAsync(ctx.Request);
            string cls = S(body?["class"]);
            if (cls.Length == 0 || !cfg.ClassNames().Contains(cls)) return Http.Detail(400, "unknown class");
            var ids = (body?["ids"] as JsonArray ?? []).Select(n => n is JsonValue v && v.TryGetValue(out long id) ? id : (long?)null).OfType<long>();
            CanvasSeen.Mark(cfg.Home, cls, ids);
            return Http.Json(new JsonObject { ["ok"] = true });
        })));
        app.MapGet("/api/v2/canvas/notifications", (HttpContext ctx, long? after) => Api(ctx, () =>
        {
            var soonNow = Canvas.Clock();
            CanvasNotifications.EnsureDueSoon(cfg.Home, Assignments.Upcoming(Assignments.Load(cfg.Home), TimeZoneInfo.ConvertTime(soonNow, Canvas.Zone).DateTime, 1), soonNow, Canvas.Zone);
            var (last, items) = CanvasNotifications.Since(cfg.Home, after ?? 0);
            return Http.Json(CanvasView.Notifications(last, items));
        }));
        app.MapPost("/api/v2/canvas/notifications/seen", Http.Handle(ctx => ApiAsync(ctx, async () =>
        {
            var body = await Http.JsonBodyAsync(ctx.Request);
            if (body?["up_to"] is not JsonValue v || !v.TryGetValue(out long upTo)) return Http.Detail(400, "up_to is required");
            CanvasNotifications.MarkSeen(cfg.Home, upTo);
            return Http.Json(new JsonObject { ["ok"] = true });
        })));
        app.MapGet("/api/v2/files/raw", (HttpContext ctx, string? @class, string? path) => Api(ctx, () =>
        {
            if (@class is null || path is null || !cfg.ClassNames().Contains(@class)) return Http.Detail(404, "no such file");
            string root = Path.GetFullPath(store.ClassDir(@class)), full = Path.GetFullPath(Path.Combine(root, path));
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(full)) return Http.Detail(404, "no such file");
            return Results.File(full, "application/octet-stream", Path.GetFileName(full));
        }));
        app.MapGet("/api/v2/canvas/extension", (HttpContext ctx) => Api(ctx, () => Http.Json(new JsonObject
        {
            ["key"] = CanvasSettings.ExtensionKey(cfg.Home), ["canvas"] = Canvas.Settings.Url, ["version"] = Extension.Version(),
        })));
        app.MapPost("/api/v2/canvas/courses", Http.Handle(ctx => ApiAsync(ctx, async () => Http.Json(await FindCoursesAsync()))));
        app.MapPost("/api/v2/canvas/fetch", Http.Handle(ctx => ApiAsync(ctx, async () =>
        {
            var body = await Http.JsonBodyAsync(ctx.Request);
            string kind = S(body?["kind"]) is "text" or "bytes" ? S(body?["kind"]) : "json";
            return Http.Json(await Canvas.FetchAsync(S(body?["url"]), kind, S(body?["save_to"]), ctx.RequestAborted));
        })));
        app.MapGet("/api/v2/canvas/agent-courses", (HttpContext ctx) => Api(ctx, () => Http.Json(Canvas.Courses())));
        app.MapGet("/api/v2/assignments", (HttpContext ctx, string? @class, int? days) => Api(ctx, () =>
        {
            var all = Assignments.Load(cfg.Home);
            var list = days is int d ? Assignments.Upcoming(all, DateTime.Now, d, @class) : all.Where(a => @class is null || a.ClassName == @class).ToList();
            return Http.Json(new JsonArray(list.Select(a => (JsonNode)AssignmentJson(a)).ToArray()));
        }));

        app.MapGet("/api/v2/files", (HttpContext ctx, string? @class, string? path) => Api(ctx, () =>
        {
            if (@class is null || path is null || !cfg.ClassNames().Contains(@class)) return Http.Detail(404, "no such file");
            string root = Path.GetFullPath(store.ClassDir(@class)), full = Path.GetFullPath(Path.Combine(root, path));
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(full)
                || new FileInfo(full).Length > 2_000_000 || !(full.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || full.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)))
                return Http.Detail(404, "no such file");
            return Http.Json(new JsonObject { ["text"] = File.ReadAllText(full) });
        }));

        app.MapPost("/api/v2/canvas/scout", Http.Handle(ctx => ApiAsync(ctx, async () =>
        {
            string cls = S((await Http.JsonBodyAsync(ctx.Request))?["class"]);
            if (!Canvas.Settings.Courses.ContainsKey(cls)) return Http.Detail(400, "Link that class to Canvas first.");
            if (options.Scout is not { } scout) return Http.Detail(503, "Exploring needs the library's service.");
            scout.Queue(cls);
            return Http.Json(CanvasJson());
        })));
        app.MapPost("/settings/canvas/scout", Http.Handle(ctx => WithMemberAsync(ctx, async _ =>
        {
            string cls = (await Http.FormAsync(ctx.Request)).Get("class");
            if (Canvas.Settings.Courses.ContainsKey(cls)) options.Scout?.Queue(cls);
            return Http.SeeOther("/settings?canvas=scout#canvas");
        })));

        // The pages.
        app.MapGet("/due", (HttpContext ctx) => WithMember(ctx, DuePage));
        app.MapGet("/files/{cls}/{**path}", (HttpContext ctx, string cls, string path) => WithMember(ctx, role => FilePage(role, RouteName(cls), path)));
        app.MapPost("/settings/canvas", Http.Handle(SaveCanvas));
        app.MapPost("/settings/canvas/find", Http.Handle(ctx => WithMemberAsync(ctx, async _ =>
        {
            var found = await FindCoursesAsync();
            return Http.SeeOther("/settings?canvas=" + (found["error"] is null ? "found" : "unreachable") + "#canvas");
        })));
        app.MapPost("/settings/canvas/extension", Http.Handle(ctx => WithMemberAsync(ctx, _ =>
        {
            PrepareExtensionHere();
            return Task.FromResult(Http.SeeOther("/settings?canvas=extension#canvas"));
        })));
    }

    /// <summary>The extension's folder on this computer, pointing at this library.</summary>
    string PrepareExtensionHere() =>
        Extension.Prepare(Extension.Folder(cfg.Home), $"http://127.0.0.1:{cfg.WebPort}", CanvasSettings.ExtensionKey(cfg.Home), Canvas.Settings.Url);

    /// <summary>Ask Canvas (through the extension) which courses the person is in, and keep the list (with each
    /// course's code and term, for <see cref="CourseMatch"/>) for Settings. Follows every page.</summary>
    async Task<JsonObject> FindCoursesAsync()
    {
        if (!Canvas.Settings.On) return new JsonObject { ["error"] = "Add your school's Canvas address first." };
        var found = new Dictionary<string, string>();
        var info = new Dictionary<string, CourseInfo>();
        string? next = "/api/v1/courses?enrollment_state=active&include[]=term&per_page=100";
        for (int page = 0; page < 20 && next is not null; page++)
        {
            var r = await Canvas.FetchAsync(next, "json");
            if (r["error"] is not null) return page == 0 ? r : CanvasJson();
            foreach (var c in (JsonNode.Parse(S(r["json"])) as JsonArray ?? []).OfType<JsonObject>())
                if (c["id"] is JsonValue id && S(c["name"]) is { Length: > 0 } name)
                {
                    string sid = id.ToJsonString();
                    found[sid] = name;
                    info[sid] = new CourseInfo(S(c["course_code"]), name, S(c["term"]?["name"]));
                }
            next = r["next_page"] is JsonValue nv ? S(nv) : null;
        }
        CanvasSettings.Update(cfg.Home, s => { s.Available = found; s.CourseInfo = info; });
        return CanvasJson();
    }

    /// <summary>Where the course scout stands on a class, for <c>/api/v2/canvas/classes</c>.</summary>
    ScoutState ScoutOf(string cls)
    {
        var s = Canvas.Settings;
        if (options.Scout?.Running == cls) return new ScoutState("exploring", 0, "", "");
        if (options.Scout?.Waiting.Contains(cls) == true) return new ScoutState("waiting", 0, "", "");
        return s.Scouts.TryGetValue(cls, out var r) ? new ScoutState(r.Ok ? "done" : "failed", r.Files, r.When, r.Report) : new ScoutState("never", 0, "", "");
    }

    JsonObject AssignmentJson(Assignment a) => new()
    {
        ["class"] = a.ClassName, ["id"] = a.Id, ["name"] = a.Name, ["due"] = a.Due, ["points"] = a.Points, ["status"] = a.Status,
        ["score"] = a.Score, ["submitted"] = a.Submitted, ["url"] = a.Url, ["done"] = a.Done,
        ["folder"] = Canvas.Crawl.AssignmentFolder(a.ClassName, a.Id),
        ["label"] = Assignments.Label(a.Status, a.Late), ["score_text"] = Assignments.ScoreText(a), ["grade"] = a.Grade,
        ["late"] = a.Late, ["missing"] = a.Missing, ["excused"] = a.Excused, ["graded_at"] = a.GradedAt, ["kind"] = a.Kind,
        ["due_at"] = a.DueAt, ["comments"] = a.Comments ?? 0,
    };

    JsonObject CanvasJson()
    {
        var s = Canvas.Settings;
        var courses = new JsonObject();
        foreach (var (cls, id) in s.Courses) courses[cls] = id;
        var available = new JsonObject();
        foreach (var (id, name) in s.Available) available[id] = name;
        var (waiting, inflight) = Canvas.Crawl.Left;
        var courseInfo = new JsonObject();
        foreach (var (id, info) in s.CourseInfo) courseInfo[id] = new JsonObject { ["code"] = info.Code, ["name"] = info.Name, ["term"] = info.Term };
        return new JsonObject
        {
            ["url"] = s.Url, ["courses"] = courses, ["available"] = available, ["last_sync"] = s.LastSync, ["error"] = s.Error,
            ["needs_login"] = s.NeedsLogin, ["extension_seen"] = s.ExtensionSeen, ["extension_version"] = s.ExtensionVersion,
            ["extension_latest"] = Extension.Version(), ["extension_outdated"] = s.ExtensionOutdated,
            ["extension_update"] = s.ExtensionUpdate is { Dismissed: false } up ? new JsonObject { ["from"] = up.From, ["to"] = up.To, ["at"] = up.At } : null,
            ["state"] = CanvasView.State(Canvas, Canvas.Clock()), ["course_info"] = courseInfo,
            ["syncing"] = Canvas.Crawl.Active, ["left"] = waiting + inflight,
            ["exploring"] = options.Scout?.Running, ["scouts"] = new JsonObject(s.Scouts.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)new JsonObject
            {
                ["ok"] = kv.Value.Ok, ["report"] = kv.Value.Report, ["when"] = kv.Value.When, ["files"] = kv.Value.Files,
            }))), ["changes"] = new JsonArray(s.Changes.Select(c => (JsonNode)c).ToArray()),
            ["last_changes"] = new JsonArray(s.LastChanges.Select(c => (JsonNode)new JsonObject
            {
                ["kind"] = c.Kind, ["class"] = c.Class, ["name"] = c.Name, ["text"] = c.Text, ["assignment_id"] = c.AssignmentId,
                ["announcement_id"] = c.AnnouncementId,
            }).ToArray()),
        };
    }

    // --- pages -------------------------------------------------------------------------------------------------

    /// <summary>Canvas has linked classes: the Due page and the assignment sections show.</summary>
    bool CanvasOn => CanvasSettings.PathIn(cfg.Home) is var p && File.Exists(p) && Canvas.Settings is { On: true, Courses.Count: > 0 };

    static readonly Dictionary<string, (string Label, string Color)> StatusLook = new()
    {
        ["missing"] = ("Missing", "var(--red)"), ["past due"] = ("Past due", "var(--red)"), ["open"] = ("To do", "var(--label-3)"),
        ["graded"] = ("Graded", "var(--green)"), ["submitted"] = ("Submitted", "var(--green)"), ["late"] = ("Submitted late", "var(--orange)"),
        ["excused"] = ("Excused", "var(--label-3)"), ["no submission"] = ("Nothing to hand in", "var(--label-3)"),
    };

    string AssignmentRow(Assignment a, bool showClass)
    {
        var (label, color) = StatusLook.GetValueOrDefault(a.Status, (a.Status, "var(--label-3)"));
        string? folder = Canvas.Crawl.AssignmentFolder(a.ClassName, a.Id);
        string title = Ui.Esc(a.Name);
        title = folder is not null ? $"<a href=\"/files/{Ui.Quote(a.ClassName, "")}/{Ui.Quote(folder, "/")}/spec.md\">{title}</a>"
            : a.Url.Length > 0 ? $"<a href=\"{Ui.Esc(a.Url)}\">{title}</a>" : title;
        string score = a.Score is double sc ? $" · {sc.ToString("0.##", CultureInfo.InvariantCulture)}/{(a.Points ?? 0).ToString("0.##", CultureInfo.InvariantCulture)}" : "";
        string where = showClass ? $"<span class=\"tag\" style=\"{Ui.HueStyle(a.ClassName)}\">{Ui.Esc(a.ClassName)}</span> " : "";
        return $"<div class=\"row\"><span class=\"dot\" style=\"--tag:{color}\"></span><div class=\"grow\"><div class=\"title\">{title}</div>"
            + $"<div class=\"subtitle\">{where}{Ui.Esc(Assignments.Say(a.Due, DateTime.Now))} · {Ui.Esc(label)}{score}</div></div></div>";
    }

    string AssignmentGroups(List<Assignment> items, bool showClass)
    {
        var now = DateTime.Now;
        DateTime Due(Assignment a) => a.Due.Length > 0 ? DateTime.Parse(a.Due, CultureInfo.InvariantCulture) : DateTime.MaxValue;
        var groups = new (string Title, Func<Assignment, bool> In)[]
        {
            ("Overdue", a => Due(a) < now),
            ("Next 7 days", a => Due(a) >= now && Due(a) < now.Date.AddDays(8)),
            ("Later", a => Due(a) >= now.Date.AddDays(8) && Due(a) != DateTime.MaxValue),
            ("No due date", a => Due(a) == DateTime.MaxValue),
        };
        return string.Concat(groups.Select(g => items.Where(g.In).ToList() is { Count: > 0 } rows
            ? $"<h2>{g.Title}</h2><div class=\"group\">{string.Concat(rows.Select(a => AssignmentRow(a, showClass)))}</div>" : ""));
    }

    IResult DuePage(string role)
    {
        var c = Context(role, "due", ("/", cfg.PoolName));
        var s = Canvas.Settings;
        var upcoming = Assignments.Upcoming(Assignments.Load(cfg.Home), DateTime.Now, 30);
        string state = s.NeedsLogin || s.Error.Length > 0 ? $"<div class=\"notice\"><div>{Ui.Esc(s.Error)}</div></div>" : "";
        string synced = DateTimeOffset.TryParse(s.LastSync, out var t) ? $"Synced from Canvas {Ui.Esc(t.LocalDateTime.ToString("ddd d MMM, h:mm tt", CultureInfo.InvariantCulture))}." : "Not synced yet.";
        string body = $"<h1>Due</h1><p class=\"sub\">{synced}</p>{state}"
            + (upcoming.Count > 0 ? AssignmentGroups(upcoming, showClass: true)
                : "<div class=\"empty\"><strong>Nothing due</strong>Nothing to hand in for the next month.</div>");
        if (s.Changes.Count > 0)
            body += $"<h2>Last changes on Canvas</h2><div class=\"group\">{string.Concat(s.Changes.Take(12).Select(ch => $"<div class=\"row\"><div class=\"grow\">{Ui.Esc(ch)}</div></div>"))}</div>";
        return Show("Due", body, c);
    }

    /// <summary>A class's assignments and Canvas material, for its page (empty when Canvas doesn't know the class).</summary>
    string ClassCanvas(string name)
    {
        if (!CanvasOn || !Canvas.Settings.Courses.ContainsKey(name)) return "";
        var mine = Assignments.Load(cfg.Home).Where(a => a.ClassName == name).ToList();
        var todo = Assignments.Upcoming(mine, DateTime.Now, 30);
        int done = mine.Count(a => a.Done);
        string cq = Ui.Quote(name, "");
        var links = new List<string>();
        foreach (var (file, label) in new[] { ("Canvas/modules.md", "Modules"), ("Canvas/announcements.md", "Announcements"), ("Canvas/canvas-recipe.md", "How this class uses Canvas") })
            if (File.Exists(Path.Combine(store.ClassDir(name), file))) links.Add($"<a class=\"btn\" href=\"/files/{cq}/{Ui.Quote(file, "/")}\">{label}</a>");
        string todoHtml = todo.Count > 0 ? AssignmentGroups(todo, showClass: false) : "";
        string doneHtml = done > 0
            ? $"<details class=\"help\"><summary>{done} handed in</summary><div class=\"group\">{string.Concat(mine.Where(a => a.Done).OrderByDescending(a => a.Due, StringComparer.Ordinal).Select(a => AssignmentRow(a, false)))}</div></details>"
            : "";
        return $"<section class=\"assignments\">{(links.Count > 0 ? $"<div class=\"toolbar\" style=\"margin:0 0 1rem\">{string.Concat(links)}</div>" : "")}{todoHtml}{doneHtml}</section>";
    }

    /// <summary>A file in a class's folder: Markdown is shown as a page, anything else is downloaded.</summary>
    IResult FilePage(string role, string cls, string path)
    {
        if (!cfg.ClassNames().Contains(cls)) return NotFound(role, "class");
        string root = Path.GetFullPath(store.ClassDir(cls)), full = Path.GetFullPath(Path.Combine(root, Uri.UnescapeDataString(path)));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(full)) return NotFound(role, "file");
        if (!full.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            return Results.File(full, "application/octet-stream", Path.GetFileName(full));
        var c = Context(role, $"class:{cls}", (Ui.ClassUrl(cls), cls));
        string text = File.ReadAllText(full);
        // An assignment's instructions and your submission link to each other, and to Canvas.
        string bar = "";
        if (Path.GetFileName(full) is "spec.md" or "feedback.md")
        {
            string here = Path.GetDirectoryName(full)!, rel = Ui.Quote(cls, "") + "/" + Ui.Quote(Path.GetRelativePath(root, here).Replace('\\', '/'), "/");
            var links = new List<string>();
            if (Path.GetFileName(full) == "feedback.md") links.Add($"<a class=\"btn\" href=\"/files/{rel}/spec.md\">Instructions</a>");
            else if (File.Exists(Path.Combine(here, "feedback.md"))) links.Add($"<a class=\"btn\" href=\"/files/{rel}/feedback.md\">My submission and feedback</a>");
            string spec = File.Exists(Path.Combine(here, "spec.md")) ? File.ReadAllText(Path.Combine(here, "spec.md")) : "";
            if (System.Text.RegularExpressions.Regex.Match(spec, "^url: (https?://\\S+)$", System.Text.RegularExpressions.RegexOptions.Multiline) is { Success: true } m)
                links.Add($"<a class=\"btn\" href=\"{Ui.Esc(m.Groups[1].Value)}\">Open in Canvas</a>");
            bar = $"<div class=\"toolbar\" style=\"margin:0 0 1.2rem\">{string.Concat(links)}</div>";
        }
        if (text.StartsWith("---\n", StringComparison.Ordinal) && text.IndexOf("\n---\n", 4, StringComparison.Ordinal) is int end and > 0) text = text[(end + 5)..];
        // Links between the mirrored files are relative: point them at this same page.
        string dir = Path.GetRelativePath(root, Path.GetDirectoryName(full)!).Replace('\\', '/');
        string html = Ui.RenderMd(text).Replace("href=\"", "href=\"\u0001").Replace("href=\"\u0001http", "href=\"http").Replace("href=\"\u0001#", "href=\"#")
            .Replace("href=\"\u0001mailto", "href=\"mailto").Replace("\u0001", $"/files/{Ui.Quote(cls, "")}/{(dir == "." ? "" : Ui.Quote(dir, "/") + "/")}");
        string title = text.Split('\n').FirstOrDefault(l => l.StartsWith("# ", StringComparison.Ordinal))?[2..].Trim() is { Length: > 0 } h ? h : Path.GetFileNameWithoutExtension(full);
        return Show(title, $"{bar}<article class=\"prose\">{html}</article>", c, math: true);
    }

    string CanvasSettingsGroup(string? flash)
    {
        var s = Canvas.Settings;
        string say = flash switch
        {
            "found" => "<div class=\"notice good\"><div>Found your Canvas courses. Pick one for each class.</div></div>",
            "unreachable" => "<div class=\"notice\"><div>Chrome didn't answer. Set up the extension below, keep Chrome open and signed in to Canvas, then try again.</div></div>",
            "extension" => "<div class=\"notice good\"><div>The extension's folder is ready. Load it in Chrome as below.</div></div>",
            "saved" => "<div class=\"notice good\"><div>Saved. Canvas syncs within a minute while Chrome is open.</div></div>",
            "scout" => "<div class=\"notice good\"><div>Exploring. It takes a few minutes; what it finds lands in the class's Canvas folder.</div></div>",
            _ => "",
        };
        if (s.Error.Length > 0) say += $"<div class=\"notice\"><div>{Ui.Esc(s.Error)}</div></div>";
        string CourseSelect(string cls)
        {
            long have = s.Courses.GetValueOrDefault(cls);
            if (s.Available.Count == 0)
                return $"<input type=\"number\" name=\"canvas_{Ui.Esc(cls)}\" value=\"{(have > 0 ? have.ToString(CultureInfo.InvariantCulture) : "")}\" placeholder=\"Canvas course id\" style=\"width:9rem\">";
            var opts = "<option value=\"\">Not on Canvas</option>" + string.Concat(s.Available.OrderBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase).Select(kv =>
                $"<option value=\"{Ui.Esc(kv.Key)}\"{(kv.Key == have.ToString(CultureInfo.InvariantCulture) ? " selected" : "")}>{Ui.Esc(kv.Value)}</option>"));
            if (have > 0 && !s.Available.ContainsKey(have.ToString(CultureInfo.InvariantCulture))) opts += $"<option value=\"{have}\" selected>Course {have}</option>";
            return $"<select name=\"canvas_{Ui.Esc(cls)}\">{opts}</select>";
        }
        string rows = string.Concat(cfg.ClassNames().Select(cls => $"<div class=\"row\"><span class=\"grow\">{Ui.Esc(cls)}</span>{CourseSelect(cls)}</div>"));
        // The course scout: what it found per class, and a way to explore again.
        string scouts = "";
        if (options.Scout is { } scout && s.Courses.Count > 0)
        {
            var items = s.Courses.Keys.Select(cls =>
            {
                string state = scout.Running == cls ? "Exploring now…" : scout.Waiting.Contains(cls) ? "Waiting its turn"
                    : s.Scouts.TryGetValue(cls, out var r) ? (r.Ok ? $"Explored {Ui.Esc(DateTimeOffset.TryParse(r.When, out var w) ? w.LocalDateTime.ToString("d MMM", CultureInfo.InvariantCulture) : "")}, {r.Files} file(s)" : "Didn't finish: " + Ui.Esc(Py.Head(r.Report, 140)))
                    : "Not explored yet";
                string report = s.Scouts.TryGetValue(cls, out var rr) && rr.Ok && rr.Report.Length > 0 ? $"<div class=\"subtitle\">{Ui.Esc(Py.Head(rr.Report, 300))}</div>" : "";
                return $"<form class=\"row\" method=\"post\" action=\"/settings/canvas/scout\"><input type=\"hidden\" name=\"class\" value=\"{Ui.Esc(cls)}\">"
                    + $"<div class=\"grow\"><div class=\"title\">{Ui.Esc(cls)}</div><div class=\"subtitle\">{state}</div>{report}</div><button>Explore</button></form>";
            });
            scouts = "<div class=\"group-head\">Explore each class with AI</div><div class=\"group\">" + string.Concat(items) + "</div>"
                + "<p class=\"group-foot\">Every instructor lays Canvas out differently. The AI looks through a class's Canvas (the syllabus, files "
                + "outside modules, links to Box or Google Drive), saves what the sync misses, and writes a short guide to where things are.</p>";
        }
        string synced = DateTimeOffset.TryParse(s.LastSync, out var t) ? t.LocalDateTime.ToString("ddd d MMM, h:mm tt", CultureInfo.InvariantCulture) : "Never";
        string seen = DateTimeOffset.TryParse(s.ExtensionSeen, out var e) && DateTimeOffset.Now - e < TimeSpan.FromMinutes(5) ? "Connected" : s.ExtensionSeen.Length > 0 ? "Not lately (is Chrome open?)" : "Not set up";
        string folder = Extension.Folder(cfg.Home);
        return $"<div class=\"group-head\" id=\"canvas\">Canvas</div>{say}<form method=\"post\" action=\"/settings/canvas\"><div class=\"group\">"
            + $"<div class=\"row\"><label class=\"grow\" for=\"canvas_url\">Your school's Canvas</label><input id=\"canvas_url\" name=\"canvas_url\" type=\"text\" value=\"{Ui.Esc(s.Url)}\" placeholder=\"school.instructure.com\" style=\"width:16rem\"></div>"
            + rows
            + $"<div class=\"row\"><span class=\"grow\">Chrome extension</span><span class=\"value\">{seen}</span></div>"
            + $"<div class=\"row\"><span class=\"grow\">Last sync</span><span class=\"value\">{Ui.Esc(synced)}</span></div>"
            + "</div><div class=\"actions\"><button class=\"primary\">Save and sync</button>"
            + "<button formaction=\"/settings/canvas/find\">Find my courses</button></div></form>"
            + "<p class=\"group-foot\">Canvas is read through Chrome with your own sign-in, so no Canvas token is needed. It only reads: "
            + "assignments and instructions, your submissions and feedback, modules, files and announcements, into each class's Canvas folder.</p>"
            + scouts
            + "<details class=\"help\"><summary>Set up the Chrome extension on this computer</summary><div class=\"group\">"
            + "<form class=\"row\" method=\"post\" action=\"/settings/canvas/extension\"><span class=\"grow\">1. Make the extension's folder</span><button>Make it</button></form>"
            + "<div class=\"row\"><span class=\"grow\">2. In Chrome, open chrome://extensions and turn on Developer mode</span></div>"
            + $"<div class=\"row\"><span class=\"grow\">3. Click Load unpacked and pick <code>{Ui.Esc(folder)}</code></span></div>"
            + "</div><p class=\"group-foot\">On a laptop that reaches this library from elsewhere, set it up from the Study Stash app's Settings instead.</p></details>";
    }

    async Task<IResult> SaveCanvas(HttpContext ctx) => await WithMemberAsync(ctx, async _ =>
    {
        var f = await Http.FormAsync(ctx.Request);
        string typed = Py.Strip(f.Get("canvas_url"));
        CanvasSettings.Update(cfg.Home, s =>
        {
            if (typed.Length == 0) s.Url = "";
            else if (CanvasSettings.CleanUrl(typed) is string u) s.Url = u;
            foreach (string cls in cfg.ClassNames())
            {
                if (!f.ContainsKey("canvas_" + cls)) continue;
                if (long.TryParse(Py.Strip(f.Get("canvas_" + cls)), out long id) && id > 0) s.Courses[cls] = id;
                else s.Courses.Remove(cls);
            }
            s.SyncNow = true;
        });
        if (Directory.Exists(Extension.Folder(cfg.Home))) PrepareExtensionHere(); // its Canvas address may have changed
        return Http.SeeOther("/settings?canvas=saved#canvas");
    });
}
