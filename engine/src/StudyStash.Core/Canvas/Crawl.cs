using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace StudyStash.Core.Canvas;

/// <summary>A Canvas address for the extension to read, as JSON (the API) or as a file's bytes.</summary>
public sealed record CanvasJob(string Id, string Url, string Kind);

/// <summary>What the extension read: the HTTP status, Canvas's paging header, and the text or bytes (base64). Status
/// 401 means Chrome isn't signed in to Canvas.</summary>
public sealed record CanvasResult(string Id, int Status, string Link, string Text, string B64, string Error, string Final)
{
    public static CanvasResult From(JsonObject o)
    {
        static string S(JsonNode? v) => v is JsonValue j && j.GetValueKind() == JsonValueKind.String ? j.GetValue<string>() : "";
        int status = o["status"] is JsonValue sv && sv.GetValueKind() == JsonValueKind.Number ? (int)sv.GetValue<double>() : 0;
        return new CanvasResult(o["id"] is JsonValue iv && iv.GetValueKind() == JsonValueKind.Number ? iv.ToString() : S(o["id"]),
            status, S(o["link"]), S(o["text"]), S(o["b64"]), S(o["error"]), S(o["final"]));
    }
}

/// <summary>
/// Mirrors Canvas into each class's folder, one fetch at a time, through the Chrome extension. The library hands the
/// extension jobs; the extension fetches them with the person's own Canvas session and hands the answers back;
/// <see cref="Handle"/> files them and queues what follows (next pages, file bodies). In each class's folder:
/// <code>
/// Canvas/assignments/&lt;name&gt;/spec.md      instructions, due date, points, rubric
/// Canvas/assignments/&lt;name&gt;/feedback.md  your submission: status, score, rubric marks, comments
/// Canvas/assignments/&lt;name&gt;/submission/  the files you turned in
/// Canvas/modules/&lt;NN Module&gt;/             module files, and pages as Markdown
/// Canvas/modules.md                      the module outline, linked to the copies
/// Canvas/announcements.md                announcements, newest first
/// </code>
/// It keeps its queue in crawl.json, so a restart carries on where it was.
/// </summary>
public sealed partial class Crawl
{
    public const long MaxBytes = 40L * 1024 * 1024;
    const double InflightSeconds = 600;

    readonly string home;
    readonly Func<string, string> classDir;
    readonly Lock gate = new();
    readonly JsonObject data;

    /// <param name="classDir">A class's folder in the library (created if missing).</param>
    public Crawl(string home, Func<string, string> classDir)
    {
        this.home = home;
        this.classDir = classDir;
        data = Read() ?? [];
        foreach (string key in new[] { "jobs", "assignments" })
            if (data[key] is null) data[key] = key == "jobs" ? new JsonArray() : new JsonObject();
        foreach (string key in new[] { "inflight", "changed", "manifest" })
            if (data[key] is not JsonObject) data[key] = new JsonObject();
        if (data["errors"] is not JsonArray) data["errors"] = new JsonArray();
    }

    string StatePath => Path.Combine(home, "crawl.json");

    JsonObject? Read()
    {
        try
        {
            return File.Exists(StatePath) ? JsonNode.Parse(File.ReadAllText(StatePath)) as JsonObject : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    void Save()
    {
        string tmp = StatePath + ".tmp";
        File.WriteAllText(tmp, data.ToJsonString());
        File.Move(tmp, StatePath, overwrite: true);
    }

    JsonArray Jobs => (JsonArray)data["jobs"]!;
    JsonObject Inflight => (JsonObject)data["inflight"]!;
    JsonObject Manifest => (JsonObject)data["manifest"]!;
    JsonObject Changed => (JsonObject)data["changed"]!;
    JsonArray Errors => (JsonArray)data["errors"]!;

    static bool Flag(JsonNode? v) => v is JsonValue j && j.GetValueKind() == JsonValueKind.True;
    static string S(JsonNode? v) => v is JsonValue j && j.GetValueKind() == JsonValueKind.String ? j.GetValue<string>() : "";
    // Numbers parsed from JSON and numbers set here (ints) read the same way.
    static double? D(JsonNode? v) => v is JsonValue j && j.GetValueKind() == JsonValueKind.Number
        ? double.Parse(j.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture) : null;

    /// <summary>A sync is running.</summary>
    public bool Active { get { lock (gate) return Flag(data["active"]); } }
    /// <summary>A sync finished and its results are waiting for <see cref="TakeFinished"/>.</summary>
    public bool Ready { get { lock (gate) return Flag(data["ready"]); } }
    /// <summary>Jobs still to do, and being done.</summary>
    public (int Waiting, int Inflight) Left { get { lock (gate) return (Jobs.Count, Inflight.Count); } }

    /// <summary>The extension was sent to Canvas's sign-in page: the sync stopped. Reading clears it.</summary>
    public bool TakeSignedOut()
    {
        lock (gate)
        {
            if (!Flag(data["signed_out"])) return false;
            data["signed_out"] = false;
            Save();
            return true;
        }
    }

    void Add(string url, string kind, JsonObject tag)
    {
        int seq = (int)(D(data["seq"]) ?? 0) + 1;
        data["seq"] = seq;
        Jobs.Add(new JsonObject { ["id"] = seq.ToString(CultureInfo.InvariantCulture), ["url"] = url, ["kind"] = kind, ["tag"] = tag });
    }

    static JsonObject Tag(string type, string cls, params (string Key, string Value)[] more)
    {
        var t = new JsonObject { ["type"] = type, ["class"] = cls };
        foreach (var (k, v) in more) t[k] = v;
        return t;
    }

    /// <summary>Start a sync of these classes (class → Canvas course id). False if one is already running.</summary>
    public bool Start(string canvasUrl, IReadOnlyDictionary<string, long> courses, DateTime today)
    {
        lock (gate)
        {
            if (Flag(data["active"])) return false;
            data["jobs"] = new JsonArray();
            data["inflight"] = new JsonObject();
            data["assignments"] = new JsonObject();
            data["changed"] = new JsonObject();
            data["errors"] = new JsonArray();
            data["active"] = true;
            data["ready"] = false;
            data["signed_out"] = false;
            data["started"] = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture);
            data["base"] = canvasUrl;
            // Announcements from the start of this term: August for fall, January otherwise.
            string since = today.Month >= 8 ? $"{today.Year}-08-01" : $"{today.Year}-01-01";
            foreach (var (cls, id) in courses)
            {
                string api = $"{canvasUrl}/api/v1/courses/{id}";
                Add($"{api}/assignments?include[]=submission&per_page=100&order_by=due_at", "json", Tag("assignments", cls));
                Add($"{api}/students/submissions?student_ids[]=self&include[]=submission_comments&include[]=rubric_assessment&include[]=assignment&per_page=100",
                    "json", Tag("submissions", cls));
                Add($"{api}/modules?include[]=items&per_page=100", "json", Tag("modules", cls));
                Add($"{canvasUrl}/api/v1/announcements?context_codes[]=course_{id}&start_date={since}&per_page=50", "json", Tag("announcements", cls));
            }
            Save();
            return true;
        }
    }

    /// <summary>Up to <paramref name="n"/> jobs for the extension. Jobs it took and never answered (Chrome closed) go
    /// back in the queue after ten minutes.</summary>
    public List<CanvasJob> Next(int n = 6)
    {
        lock (gate)
        {
            double t = Py.Time();
            foreach (var (id, rec) in Inflight.ToList())
                if (t - (D(rec?["t"]) ?? 0) > InflightSeconds)
                {
                    Jobs.Add(rec!["job"]!.DeepClone());
                    Inflight.Remove(id);
                }
            var take = Jobs.Take(n).Select(j => j!.AsObject()).ToList();
            foreach (var j in take)
            {
                Jobs.Remove(j);
                Inflight[S(j["id"])] = new JsonObject { ["job"] = j, ["t"] = t };
            }
            if (take.Count > 0) Save();
            return take.Select(j => new CanvasJob(S(j["id"]), S(j["url"]), S(j["kind"]))).ToList();
        }
    }

    /// <summary>File one answer. False when it isn't one of this crawl's jobs.</summary>
    public bool Handle(CanvasResult r)
    {
        lock (gate)
        {
            if (Inflight[r.Id] is not JsonObject rec) return false;
            Inflight.Remove(r.Id);
            var job = rec["job"]!.AsObject();
            var tag = job["tag"]!.AsObject();
            try
            {
                if (r.Status == 401)
                {
                    data["signed_out"] = true;
                    data["jobs"] = new JsonArray();
                    data["inflight"] = new JsonObject();
                }
                else if (r.Error.Length > 0 || r.Status >= 400)
                {
                    if (r.Status is not (403 or 404)) Errors.Add($"{S(tag["type"])}: {(r.Error.Length > 0 ? r.Error : r.Status)}");
                }
                else Dispatch(job, tag, r);
            }
            catch (Exception e) when (e is JsonException or IOException or InvalidOperationException or FormatException or UnauthorizedAccessException)
            {
                Errors.Add($"{S(tag["type"])}: {e.Message}"); // one odd item never stops the sync
            }
            if (Flag(data["active"]) && Jobs.Count == 0 && Inflight.Count == 0)
            {
                data["active"] = false;
                data["ready"] = !Flag(data["signed_out"]);
            }
            Save();
            return true;
        }
    }

    /// <summary>A finished sync's results, once: every assignment (class → Canvas's JSON) and the files that changed
    /// (class → paths inside the class folder). Null while it's still running.</summary>
    public (Dictionary<string, List<JsonObject>> Assignments, Dictionary<string, List<string>> Changed, List<string> Errors)? TakeFinished()
    {
        lock (gate)
        {
            if (!Flag(data["ready"])) return null;
            var assignments = ((JsonObject)data["assignments"]!).ToDictionary(kv => kv.Key, kv => (kv.Value as JsonArray ?? []).OfType<JsonObject>().Select(o => (JsonObject)o.DeepClone()).ToList());
            var changed = Changed.ToDictionary(kv => kv.Key, kv => (kv.Value as JsonArray ?? []).Select(S).Distinct().ToList());
            var errors = Errors.Select(S).ToList();
            data["ready"] = false;
            data["assignments"] = new JsonObject();
            data["changed"] = new JsonObject();
            Save();
            return (assignments, changed, errors);
        }
    }

    // --- what each answer becomes ---------------------------------------------------------------------------------

    [GeneratedRegex("<([^>]+)>;\\s*rel=\"next\"")]
    private static partial Regex NextLink();

    void Dispatch(JsonObject job, JsonObject tag, CanvasResult r)
    {
        string type = S(tag["type"]);
        if (S(job["kind"]) == "bytes")
        {
            FileBytes(tag, Convert.FromBase64String(r.B64));
            return;
        }
        var body = JsonNode.Parse(r.Text.Length > 0 ? r.Text : "null");
        if (body is JsonArray && NextLink().Match(r.Link) is { Success: true } m) Add(m.Groups[1].Value, "json", (JsonObject)tag.DeepClone());
        string cls = S(tag["class"]);
        switch (type)
        {
            case "assignments": AssignmentsPage(cls, body as JsonArray ?? []); break;
            case "submissions": Submissions(cls, body as JsonArray ?? []); break;
            case "modules": Modules(cls, body as JsonArray ?? []); break;
            case "announcements": Announcements(cls, body as JsonArray ?? []); break;
            case "file_meta": if (body is JsonObject meta) WantFile(cls, meta, S(tag["dir"])); break;
            case "page": if (body is JsonObject page) Page(cls, page, S(tag["dir"])); break;
        }
    }

    string CanvasDir(string cls) => Path.Combine(classDir(cls), "Canvas");

    /// <summary>Write a file if it's new or different, and note it as changed.</summary>
    void Write(string cls, string path, byte[] content)
    {
        Directory.CreateDirectory(Py.Parent(path));
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(content)) return;
        File.WriteAllBytes(path, content);
        if (Changed[cls] is not JsonArray list) Changed[cls] = list = [];
        list.Add(Path.GetRelativePath(classDir(cls), path).Replace('\\', '/'));
    }

    void Write(string cls, string path, string text) => Write(cls, path, new UTF8Encoding(false).GetBytes(text));

    [GeneratedRegex("[\\\\/:*?\"<>|\\x00-\\x1f]+")]
    private static partial Regex Unsafe();

    /// <summary>A name that works as a file name everywhere.</summary>
    public static string SafeName(string name, int limit = 120)
    {
        string n = Unsafe().Replace(Py.Strip(name), "-").Trim(' ', '.', '-');
        if (n.Length == 0) n = "untitled";
        return n[..Math.Min(n.Length, limit)].TrimEnd(' ', '.', '-');
    }

    /// <summary>An assignment's folder: the one already made for its Canvas id, else a new one named after it.</summary>
    string AssignmentDir(string cls, JsonObject a)
    {
        string key = $"asgdir:{cls}:{D(a["id"]):0}";
        if (S(Manifest[key]) is { Length: > 0 } known) return Path.Combine(classDir(cls), known);
        string root = Path.Combine(CanvasDir(cls), "assignments"), dir = Path.Combine(root, SafeName(S(a["name"]), 80));
        // Two assignments with the same name (or one renamed on Canvas) get their own folders.
        bool Taken(string d) => File.Exists(Path.Combine(d, "spec.md"))
            && !File.ReadAllText(Path.Combine(d, "spec.md")).Contains($"canvas_id: {Num(D(a["id"]))}\n", StringComparison.Ordinal);
        for (int i = 2; Taken(dir); i++) dir = Path.Combine(root, SafeName(S(a["name"]), 76) + $" {i}");
        Manifest[key] = Path.GetRelativePath(classDir(cls), dir);
        return dir;
    }

    void AssignmentsPage(string cls, JsonArray list)
    {
        if (data["assignments"]![cls] is not JsonArray all) data["assignments"]![cls] = all = [];
        foreach (var a in list.OfType<JsonObject>())
        {
            all.Add(a.DeepClone());
            if (!Assignments.Published(a)) continue;
            Write(cls, Path.Combine(AssignmentDir(cls, a), "spec.md"), SpecMd(cls, a));
        }
    }

    static string Num(double? d) => (d ?? 0).ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>"Tue 30 Sep 2026, 11:59 PM" in this computer's time.</summary>
    public static string LocalTime(string iso) =>
        DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)
            ? t.ToLocalTime().ToString("ddd d MMM yyyy, h:mm tt", CultureInfo.InvariantCulture) : "";

    public static string SpecMd(string cls, JsonObject a)
    {
        var sb = new StringBuilder();
        sb.Append("---\n")
            .Append("title: ").Append(JsonSerializer.Serialize(S(a["name"]))).Append('\n')
            .Append("class: ").Append(cls).Append('\n')
            .Append("canvas_id: ").Append(Num(D(a["id"]))).Append('\n')
            .Append("due: ").Append(LocalTime(S(a["due_at"])) is { Length: > 0 } due ? due : "none").Append('\n')
            .Append("points: ").Append(Num(D(a["points_possible"]))).Append('\n')
            .Append("submission_types: ").Append(string.Join(", ", (a["submission_types"] as JsonArray ?? []).Select(S))).Append('\n')
            .Append("url: ").Append(S(a["html_url"])).Append('\n')
            .Append("generated_by: study-stash (from Canvas; rewritten when Canvas changes)\n---\n\n")
            .Append("# ").Append(S(a["name"])).Append("\n\n");
        var facts = new List<string> { S(a["due_at"]).Length > 0 ? $"**Due** {LocalTime(S(a["due_at"]))}" : "**No due date**", $"**{Num(D(a["points_possible"]))} points**" };
        if (S(a["lock_at"]).Length > 0) facts.Add($"closes {LocalTime(S(a["lock_at"]))}");
        sb.Append(string.Join(" · ", facts)).Append("\n\n");
        string text = HtmlText.ToMarkdown(S(a["description"]));
        sb.Append(text.Length > 0 ? text : "_No instructions on Canvas._").Append('\n');
        if (a["rubric"] is JsonArray rubric && rubric.Count > 0)
        {
            sb.Append("\n## Rubric\n\n| Criterion | Points | Levels |\n|---|---|---|\n");
            foreach (var r in rubric.OfType<JsonObject>())
            {
                string levels = string.Join("; ", (r["ratings"] as JsonArray ?? []).OfType<JsonObject>().Select(x => $"{S(x["description"])} ({Num(D(x["points"]))})"));
                string desc = S(r["description"]) + (S(r["long_description"]) is { Length: > 0 } ld ? " (" + ld.ReplaceLineEndings(" ") + ")" : "");
                sb.Append("| ").Append(desc.Replace('|', '/')).Append(" | ").Append(Num(D(r["points"]))).Append(" | ").Append(levels.Replace('|', '/')).Append(" |\n");
            }
        }
        return sb.ToString();
    }

    void Submissions(string cls, JsonArray list)
    {
        foreach (var s in list.OfType<JsonObject>())
        {
            if (s["assignment"] is not JsonObject a) continue;
            if (S(s["submitted_at"]).Length == 0 && D(s["score"]) is null && (s["submission_comments"] as JsonArray ?? []).Count == 0) continue;
            string dir = AssignmentDir(cls, a);
            Write(cls, Path.Combine(dir, "feedback.md"), FeedbackMd(a, s));
            foreach (var att in (s["attachments"] as JsonArray ?? []).OfType<JsonObject>())
                WantFile(cls, att, Path.Combine(dir, "submission"));
        }
    }

    public static string FeedbackMd(JsonObject a, JsonObject s)
    {
        double? pts = D(a["points_possible"]), score = D(s["score"]);
        string state = Flag(s["excused"]) ? "excused" : Flag(s["missing"]) ? "missing" : Flag(s["late"]) ? "late" : S(s["workflow_state"]);
        var sb = new StringBuilder();
        sb.Append("---\ncanvas_id: ").Append(Num(D(a["id"]))).Append("\ngenerated_by: study-stash (from Canvas)\n---\n\n")
            .Append("# ").Append(S(a["name"])).Append(": my submission\n\n")
            .Append("- **Score:** ").Append(score is double sc ? $"{Num(sc)}/{Num(pts)}" : "not graded yet");
        if (S(s["grade"]) is { Length: > 0 } g && g != Num(score)) sb.Append($" (grade {g})");
        sb.Append("\n- **Status:** ").Append(state);
        if (S(s["submitted_at"]).Length > 0) sb.Append(", submitted ").Append(LocalTime(S(s["submitted_at"])));
        sb.Append("\n- **Attempt:** ").Append(Num(D(s["attempt"]))).Append('\n');
        if (D(s["points_deducted"]) is double off && off > 0) sb.Append("- **Late penalty:** −").Append(Num(off)).Append('\n');
        var files = (s["attachments"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        if (files.Count > 0)
            sb.Append("- **Files:** ").Append(string.Join(", ", files.Select(f => $"[{S(f["display_name"])}](submission/{Uri.EscapeDataString(SafeName(S(f["display_name"])))})"))).Append('\n');
        if (S(s["body"]) is { Length: > 0 } body) sb.Append("\n## What I submitted\n\n").Append(HtmlText.ToMarkdown(body)).Append('\n');
        if (s["rubric_assessment"] is JsonObject ra && ra.Count > 0)
        {
            var names = (a["rubric"] as JsonArray ?? []).OfType<JsonObject>().ToDictionary(r => S(r["id"]), r => S(r["description"]));
            sb.Append("\n## Rubric marks\n\n");
            foreach (var (rid, mark) in ra)
                sb.Append("- ").Append(names.GetValueOrDefault(rid, rid)).Append(": ").Append(D(mark?["points"]) is double p ? Num(p) : "none")
                    .Append(S(mark?["comments"]) is { Length: > 0 } c ? " (" + c + ")" : "").Append('\n');
        }
        var comments = (s["submission_comments"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        if (comments.Count > 0)
        {
            sb.Append("\n## Comments\n\n");
            foreach (var c in comments)
                sb.Append("- **").Append(S(c["author_name"])).Append("** (").Append(LocalTime(S(c["created_at"]))).Append("): ")
                    .Append(Py.Strip(S(c["comment"])).ReplaceLineEndings(" ")).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Queue a file's bytes, unless this version is already here, it's audio or video, or it's too big.</summary>
    void WantFile(string cls, JsonObject meta, string destDir, string? name = null)
    {
        string type = S(meta["content-type"]);
        if (Flag(meta["locked_for_user"]) || (D(meta["size"]) ?? 0) > MaxBytes || type.StartsWith("video/", StringComparison.Ordinal)
            || type.StartsWith("audio/", StringComparison.Ordinal) || S(meta["url"]).Length == 0) return;
        string path = Path.Combine(destDir, SafeName(name ?? (S(meta["display_name"]) is { Length: > 0 } dn ? dn : S(meta["filename"]) is { Length: > 0 } fn ? fn : Num(D(meta["id"])))));
        string key = $"file:{Num(D(meta["id"]))}:{Path.GetRelativePath(classDir(cls), path)}";
        if (S(Manifest[key]) == S(meta["updated_at"]) && File.Exists(path)) return;
        Add(S(meta["url"]), "bytes", Tag("file_bytes", cls, ("path", path), ("key", key), ("updated", S(meta["updated_at"]))));
    }

    void FileBytes(JsonObject tag, byte[] body)
    {
        Write(S(tag["class"]), S(tag["path"]), body);
        Manifest[S(tag["key"])] = S(tag["updated"]);
    }

    void Modules(string cls, JsonArray list)
    {
        string root = CanvasDir(cls);
        var outline = new StringBuilder($"# {cls}: Canvas modules\n\n_From Canvas; the local copies are linked. Rewritten on every sync._\n\n");
        foreach (var mod in list.OfType<JsonObject>().OrderBy(m => D(m["position"]) ?? 0))
        {
            string folder = Path.Combine(root, "modules", $"{D(mod["position"]) ?? 0:00} {SafeName(S(mod["name"]), 80)}");
            outline.Append("## ").Append(S(mod["name"])).Append("\n\n");
            foreach (var it in (mod["items"] as JsonArray ?? []).OfType<JsonObject>())
            {
                string kind = S(it["type"]), title = S(it["title"]), indent = new(' ', 2 * (int)(D(it["indent"]) ?? 0));
                string Link(string file) => Path.GetRelativePath(root, Path.Combine(folder, file)).Replace('\\', '/').Replace(" ", "%20");
                switch (kind)
                {
                    case "SubHeader":
                        outline.Append(indent).Append("- **").Append(title).Append("**\n");
                        break;
                    case "File":
                        Add(S(it["url"]), "json", Tag("file_meta", cls, ("dir", folder)));
                        outline.Append(indent).Append($"- [{title}]({Link(SafeName(title))}) (file)\n");
                        break;
                    case "Page":
                        Add(S(it["url"]), "json", Tag("page", cls, ("dir", folder)));
                        outline.Append(indent).Append($"- [{title}]({Link(SafeName(title) + ".md")})\n");
                        break;
                    case "ExternalUrl" or "ExternalTool":
                        outline.Append(indent).Append($"- [{title}]({(S(it["external_url"]) is { Length: > 0 } ext ? ext : S(it["html_url"]))}) (link)\n");
                        break;
                    default:
                        outline.Append(indent).Append($"- {title} ({(kind.Length > 0 ? kind.ToLowerInvariant() : "item")}, on Canvas: {S(it["html_url"])})\n");
                        break;
                }
            }
            outline.Append('\n');
        }
        Write(cls, Path.Combine(root, "modules.md"), outline.ToString().TrimEnd() + "\n");
    }

    void Page(string cls, JsonObject page, string dir)
    {
        if (Flag(page["locked_for_user"])) return;
        string text = $"# {S(page["title"])}\n\n_From Canvas ({S(page["html_url"])}), updated {LocalTime(S(page["updated_at"]))}._\n\n{HtmlText.ToMarkdown(S(page["body"]))}\n";
        Write(cls, Path.Combine(dir, SafeName(S(page["title"]) is { Length: > 0 } t ? t : "page") + ".md"), text);
    }

    void Announcements(string cls, JsonArray list)
    {
        if (list.Count == 0) return;
        var sb = new StringBuilder($"# {cls}: announcements\n\n_From Canvas, newest first._\n\n");
        foreach (var a in list.OfType<JsonObject>().OrderByDescending(x => S(x["posted_at"]), StringComparer.Ordinal))
            sb.Append("## ").Append(S(a["title"])).Append("\n_").Append(LocalTime(S(a["posted_at"]))).Append(" · ")
                .Append(S(a["author"]?["display_name"])).Append("_\n\n").Append(HtmlText.ToMarkdown(S(a["message"]))).Append("\n\n");
        Write(cls, Path.Combine(CanvasDir(cls), "announcements.md"), sb.ToString().TrimEnd() + "\n");
    }
}
