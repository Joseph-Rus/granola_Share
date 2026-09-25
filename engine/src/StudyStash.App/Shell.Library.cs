using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using StudyStash.App.Services;
using StudyStash.App.ViewModels;
using StudyStash.Core;

namespace StudyStash.App;

/// <summary>The shell's library side: the full app's columns, search, asking, and moving lectures.</summary>
public static partial class Shell
{
    static string? openClass;
    static string? openLecture;
    static int searchTurn;

    static string S(JsonNode? n) => n is JsonValue v && v.TryGetValue(out string? s) ? s ?? "" : "";

    static DateTimeOffset? Date(string? s) =>
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d) ? d : null;

    static IBrush DotFor(string? className)
    {
        int c = host.ColorOf(className ?? "");
        return c >= 0 ? Skin.ClassDot(c) : Brushes.Gray;
    }

    /// <summary>"Tue 23 Sep · 1 h 12 min".</summary>
    static string CardMeta(JsonObject l)
    {
        string day = Date(S(l["date"]))?.LocalDateTime.ToString("ddd d MMM", CultureInfo.InvariantCulture) ?? "";
        return l["seconds"] is JsonValue v && v.TryGetValue(out double s) ? $"{day} · {TimedText.Length(s)}" : day;
    }

    static async Task LoadLibraryAsync()
    {
        await host.CheckLibraryAsync();
        library.Classes.Clear();
        foreach (var (name, color, count) in host.Classes())
            library.Classes.Add(new ClassItem { Name = name, Dot = Skin.ClassDot(color), Count = count });
        await AddDueAsync();
        int unsorted = host.Overview?["unsorted"]?.GetValue<int>() ?? 0;
        library.Unsorted = unsorted > 0 ? new ClassItem { Name = Configs.Unsorted, IsUnsorted = true, Count = unsorted } : null;
        if (host.Library != LibraryState.Connected || host.OlderLibrary)
        {
            library.Groups.Clear();
            library.ClassTitle = "";
            library.ClassCount = "";
            library.Note = null;
            library.Empty = host.OlderLibrary ? "Your library runs an older Study Stash. It files your lectures, but update it to browse, search and ask here."
                : host.Library switch
            {
                LibraryState.NotSetUp => "Connect to your library in Settings to see your lectures here.",
                LibraryState.WrongPassword => "Your library's password changed. Sign in again in Settings.",
                _ => "Can't reach your library. Lectures you record wait on this computer until it's back.",
            };
            return;
        }
        if (dueOpen && library.Classes.FirstOrDefault(c => c.IsDue) is not null)
        {
            await ShowDueAsync();
            return;
        }
        string pick = openClass ?? library.Classes.FirstOrDefault(c => c.Count > 0 && !c.IsDue)?.Name ?? library.Classes.FirstOrDefault(c => !c.IsDue)?.Name ?? Configs.Unsorted;
        await ShowClassAsync(pick);
    }

    static async Task ShowClassAsync(string name)
    {
        openClass = name;
        dueOpen = false;
        foreach (var c in library.Classes) c.Selected = c.Name == name && !c.IsDue;
        if (library.Unsorted is { } u) u.Selected = name == Configs.Unsorted;
        library.ClassTitle = name;
        if (host.Remote() is not { } lib) return;
        JsonArray list;
        try
        {
            list = await lib.LecturesAsync(name, 200, null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or LibraryRefusedException)
        {
            library.Empty = "Can't reach your library right now.";
            return;
        }
        library.ClassCount = $"{list.Count} lecture{(list.Count == 1 ? "" : "s")}";
        library.Groups.Clear();
        var lectures = list.OfType<JsonObject>().ToList();
        library.Empty = lectures.Count == 0 ? $"No lectures in {name} yet. Record one and it lands here." : null;
        var today = DateTime.Today;
        var weekStart = today.AddDays(-(((int)today.DayOfWeek + 6) % 7)); // Monday
        string GroupOf(JsonObject l)
        {
            var d = Date(S(l["date"]))?.LocalDateTime.Date ?? today;
            if (d >= weekStart) return "This week";
            if (d >= weekStart.AddDays(-7)) return "Last week";
            return d.ToString("MMMM yyyy", CultureInfo.InvariantCulture) is var m && d.Year == today.Year ? d.ToString("MMMM", CultureInfo.InvariantCulture) : m;
        }
        // With Canvas: what's still to hand in for this class comes first.
        var todo = linkedClasses.Contains(name) ? await AssignmentCardsAsync(lib, name, null) : [];
        if (todo.Count > 0)
        {
            var due = new LectureGroup { Label = "To hand in", First = true };
            foreach (var card in todo) due.Items.Add(card);
            library.Groups.Add(due);
            library.Empty = null;
        }
        foreach (var g in lectures.GroupBy(GroupOf))
        {
            var group = new LectureGroup { Label = g.Key, First = library.Groups.Count == 0 };
            var items = g.ToList();
            for (int i = 0; i < items.Count; i++)
            {
                var l = items[i];
                bool writing = S(l["status"]) is "queued" or "working";
                group.Items.Add(new LectureCard
                {
                    Id = S(l["id"]), Title = S(l["title"]), Meta = CardMeta(l), Summary = writing ? "Writing notes…" : S(l["summary"]),
                    Last = i == items.Count - 1 && ReferenceEquals(g.Key, lectures.GroupBy(GroupOf).Last().Key),
                });
            }
            library.Groups.Add(group);
        }
        string? pick = openLecture is not null && lectures.Any(l => S(l["id"]) == openLecture) ? openLecture : lectures.Select(l => S(l["id"])).FirstOrDefault();
        if (pick is not null) await ShowLectureAsync(pick);
        else library.Note = null;
    }

    static async Task ShowLectureAsync(string id, bool transcript = false)
    {
        openLecture = id;
        foreach (var g in library.Groups)
            foreach (var c in g.Items) c.Selected = c.Id == id;
        if (host.Remote() is not { } lib) return;
        if (assignments.TryGetValue(id, out var asg))
        {
            await ShowAssignmentAsync(lib, id, asg);
            return;
        }
        JsonObject? l;
        try
        {
            l = await lib.LectureAsync(id);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or LibraryRefusedException)
        {
            return;
        }
        if (l is null) return;
        var date = Date(S(l["date"]))?.LocalDateTime;
        string meta = string.Join(" · ", new[]
        {
            S(l["class"]), date?.ToString("dddd d MMMM", CultureInfo.InvariantCulture) ?? "",
            l["seconds"] is JsonValue v && v.TryGetValue(out double s) ? TimedText.Length(s) : "",
        }.Where(x => x.Length > 0));
        string status = S(l["status"]);
        string notes = S(l["notes"]);
        var note = new NoteModel
        {
            Id = id, ClassName = S(l["class"]), Dot = DotFor(S(l["class"])), Meta = meta, Title = S(l["title"]), Markdown = notes,
            Pending = status is "queued" or "working" ? "The library is writing the notes for this lecture. They show here when they're done."
                : notes.Length == 0 ? S(l["error"]) is { Length: > 0 } err ? err : "There are no notes for this lecture." : null,
            ShowTranscript = transcript,
        };
        foreach (var line in TimedText.HasTimes(S(l["transcript"])) ? TimedText.Parse(S(l["transcript"]))
                     : S(l["transcript"]) is { Length: > 0 } plain ? [new Spoken(0, 0, plain)] : [])
            note.Transcript.Add(new HeardLine { Time = TimedText.HasTimes(S(l["transcript"])) ? TimedText.Clock(line.Start) : "", Text = line.Text });
        library.Note = note;
        library.Scope = "This lecture";
    }

    // --- Canvas: what's due, and each assignment's instructions and feedback --------------------------------------

    static bool dueOpen;
    /// <summary>For the self-test: whether "Due" is in the sidebar, and showing it.</summary>
    public static bool HasDue => library.Classes.Any(c => c.IsDue);
    public static Task ShowDuePublic() => ShowDueAsync();
    static HashSet<string> linkedClasses = [];
    static readonly Dictionary<string, JsonObject> assignments = [];

    /// <summary>With Canvas set up, "Due" heads the sidebar with how many are due within a week.</summary>
    static async Task AddDueAsync()
    {
        linkedClasses = [];
        if (host.Remote() is not { } lib || host.Library != LibraryState.Connected) return;
        try
        {
            if (await lib.CanvasSettingsAsync(HttpMethod.Get) is not { } c || S(c["url"]).Length == 0 || c["courses"] is not JsonObject courses || courses.Count == 0) return;
            linkedClasses = courses.Select(kv => kv.Key).ToHashSet();
            int soon = (await lib.AssignmentsAsync(null, 7)).Count;
            library.Classes.Insert(0, new ClassItem { Name = "Due", IsDue = true, Dot = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#E5484D")), Count = soon, Selected = dueOpen });
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or LibraryRefusedException)
        {
        }
    }

    static string StatusWords(string status) => status switch
    {
        "missing" => "Missing", "past due" => "Past due", "open" => "To do", "graded" => "Graded", "submitted" => "Submitted",
        "late" => "Submitted late", "excused" => "Excused", _ => "Nothing to hand in",
    };

    static string DueWords(string due) => Core.Canvas.Assignments.Say(due, DateTime.Now);

    /// <summary>Cards for what's still to hand in: one class's, or (with <paramref name="days"/>) every class's.</summary>
    static async Task<List<LectureCard>> AssignmentCardsAsync(RemoteLibrary lib, string? className, int? days)
    {
        JsonArray list;
        try
        {
            list = await lib.AssignmentsAsync(className, days ?? 30);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or LibraryRefusedException)
        {
            return [];
        }
        var cards = new List<LectureCard>();
        var rows = list.OfType<JsonObject>().ToList();
        for (int i = 0; i < rows.Count; i++)
        {
            var a = rows[i];
            string id = $"asg:{S(a["class"])}:{a["id"]}";
            assignments[id] = a;
            cards.Add(new LectureCard
            {
                Id = id, Title = S(a["name"]), Meta = (className is null ? S(a["class"]) + " · " : "") + DueWords(S(a["due"])),
                Summary = StatusWords(S(a["status"])) + (a["points"] is JsonValue p && p.TryGetValue(out double pts) ? $" · {pts:0.##} points" : ""),
                Last = i == rows.Count - 1,
            });
        }
        return cards;
    }

    static async Task ShowDueAsync()
    {
        dueOpen = true;
        foreach (var c in library.Classes) c.Selected = c.IsDue;
        if (library.Unsorted is { } u) u.Selected = false;
        library.ClassTitle = "Due";
        if (host.Remote() is not { } lib) return;
        var cards = await AssignmentCardsAsync(lib, null, 30);
        library.ClassCount = cards.Count == 1 ? "1 to hand in" : $"{cards.Count} to hand in";
        library.Groups.Clear();
        library.Empty = cards.Count == 0 ? "Nothing due in the next month." : null;
        var now = DateTime.Now;
        DateTime When(LectureCard c) => S(assignments[c.Id]["due"]) is { Length: > 0 } d ? DateTime.Parse(d, CultureInfo.InvariantCulture) : DateTime.MaxValue;
        foreach (var (label, test) in new (string, Func<DateTime, bool>)[]
                 {
                     ("Overdue", d => d < now), ("Next 7 days", d => d >= now && d < now.Date.AddDays(8)),
                     ("Later", d => d >= now.Date.AddDays(8) && d != DateTime.MaxValue), ("No due date", d => d == DateTime.MaxValue),
                 })
        {
            var these = cards.Where(c => test(When(c))).ToList();
            if (these.Count == 0) continue;
            var g = new LectureGroup { Label = label, First = library.Groups.Count == 0 };
            foreach (var c in these) g.Items.Add(new LectureCard { Id = c.Id, Title = c.Title, Meta = c.Meta, Summary = c.Summary, Last = c == these[^1] });
            library.Groups.Add(g);
        }
        if (library.Groups.FirstOrDefault()?.Items.FirstOrDefault() is { } first) await ShowLectureAsync(first.Id);
        else library.Note = null;
    }

    static string WithoutFrontMatter(string text) =>
        text.StartsWith("---\n", StringComparison.Ordinal) && text.IndexOf("\n---\n", 4, StringComparison.Ordinal) is int end and > 0 ? text[(end + 5)..] : text;

    static async Task ShowAssignmentAsync(RemoteLibrary lib, string id, JsonObject a)
    {
        string cls = S(a["class"]), folder = S(a["folder"]);
        string spec = "", feedback = "";
        try
        {
            if (folder.Length > 0)
            {
                spec = WithoutFrontMatter(await lib.FileTextAsync(cls, folder + "/spec.md") ?? "");
                feedback = WithoutFrontMatter(await lib.FileTextAsync(cls, folder + "/feedback.md") ?? "");
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or LibraryRefusedException)
        {
        }
        // The spec starts with its own title: the page shows it above already.
        string body = System.Text.RegularExpressions.Regex.Replace(spec, "^# .*\n+", "");
        if (feedback.Length > 0) body += "\n\n## My submission\n\n" + System.Text.RegularExpressions.Regex.Replace(feedback, "^# .*\n+", "");
        library.Note = new NoteModel
        {
            Id = id, ClassName = cls, Dot = DotFor(cls), Title = S(a["name"]),
            Meta = string.Join(" · ", new[] { cls, DueWords(S(a["due"])), StatusWords(S(a["status"])) }.Where(x => x.Length > 0)),
            Markdown = body.Trim(),
            Pending = body.Trim().Length == 0 ? "Canvas hasn't been read for this assignment yet." : null,
        };
        library.Scope = "This class";
    }

    static void OpenLecture(string id, bool transcript = false)
    {
        ShowLibrary();
        _ = OpenLectureAsync(id, transcript);
    }

    static async Task OpenLectureAsync(string id, bool transcript)
    {
        if (host.Remote() is not { } lib) return;
        try
        {
            if (await lib.LectureAsync(id) is { } l && S(l["class"]) is { Length: > 0 } cls && cls != openClass)
            {
                openLecture = id;
                await ShowClassAsync(cls);
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or LibraryRefusedException)
        {
            return;
        }
        await ShowLectureAsync(id, transcript);
    }

    static void CycleScope()
    {
        library.Scope = library.Scope switch
        {
            "This lecture" => "This class",
            "This class" => "All classes",
            _ => library.Note is null ? "This class" : "This lecture",
        };
    }

    static async Task AskLibrary(string question, string scope)
    {
        library.AskedQuestion = question;
        library.Answer = null;
        library.Sources.Clear();
        library.Thinking = true;
        var into = new ChatMessage();
        await Answer(into, lib => lib.AskAsync(question,
            lectureId: scope == "This lecture" ? library.Note?.Id : null,
            className: scope == "This class" ? openClass : null));
        library.Thinking = false;
        library.Answer = into.Text;
        foreach (var s in into.Sources) library.Sources.Add(s);
    }

    static void MoveLecture()
    {
        if (library.Note is not { } note || mainWindow?.Content is not Control anchor) return;
        var menu = new ContextMenu();
        foreach (var (name, color, _) in host.Classes().Where(c => c.Name != note.ClassName).Append((Configs.Unsorted, -1, 0)))
        {
            var item = new MenuItem { Header = name };
            if (color >= 0) item.Icon = new Avalonia.Controls.Shapes.Ellipse { Width = 8, Height = 8, Fill = Skin.ClassDot(color) };
            item.Click += async (_, _) =>
            {
                if (host.Remote() is not { } lib) return;
                try
                {
                    await lib.MoveAsync(note.Id, name);
                    openLecture = note.Id;
                    await LoadLibraryAsync();
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException or LibraryRefusedException)
                {
                    Toast("Couldn't move it", e.Message, null, null);
                }
            };
            menu.Items.Add(item);
        }
        menu.Open(anchor);
    }

    static async Task ExportAsync()
    {
        if (library.Note is not { } note || mainWindow is null) return;
        var file = await mainWindow.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export notes", SuggestedFileName = Notes.Slugify(note.Title) + ".md", DefaultExtension = "md",
            FileTypeChoices = [new FilePickerFileType("Markdown") { Patterns = ["*.md"] }],
        });
        if (file is null) return;
        string text = $"# {note.Title}\n\n{note.Meta}\n\n{note.Markdown}\n";
        if (note.Transcript.Count > 0) text += "\n## Transcript\n\n" + string.Join("\n", note.Transcript.Select(t => t.Time.Length > 0 ? $"[{t.Time}] {t.Text}" : t.Text)) + "\n";
        await using var stream = await file.OpenWriteAsync();
        await using var w = new StreamWriter(stream);
        await w.WriteAsync(text);
    }

    // --- the quick panel ---------------------------------------------------------------------------------------------

    static async Task SearchAsync(string query)
    {
        int turn = ++searchTurn;
        await Task.Delay(120); // wait for the typing to pause
        if (turn != searchTurn || quick.Answering) return;
        var rows = new List<QuickRow>();
        string rec = Skin.Current == SkinKind.Mac ? "⌥⇧R" : "Ctrl+Alt+R";
        string cls = RecordClass();
        void Actions()
        {
            rows.Add(new QuickRow { Kind = QuickKind.Header, Title = "Actions", First = rows.Count == 0 });
            rows.Add(host.Recorder.Current is null
                ? new QuickRow { Kind = QuickKind.Action, Title = cls.Length > 0 ? $"Record {cls}" : "Record", Meta = rec, Glyph = "mic", Run = () => { quickWindow?.Hide(); ToggleRecording(); } }
                : new QuickRow { Kind = QuickKind.Action, Title = "Stop recording", Meta = rec, Glyph = "stop", Run = () => { quickWindow?.Hide(); StopRecording(); } });
            rows.Add(new QuickRow { Kind = QuickKind.Action, Title = "Open library", Glyph = "book_2", Run = () => { quickWindow?.Hide(); ShowLibrary(); } });
        }
        quick.Note = null;
        if (query.Trim().Length > 0 && host.Remote() is { } lib)
        {
            try
            {
                var r = await lib.SearchAsync(query, null, 6);
                if (turn != searchTurn) return;
                var lectures = (r["lectures"] as JsonArray ?? []).OfType<JsonObject>().Take(4).ToList();
                var passages = (r["passages"] as JsonArray ?? []).OfType<JsonObject>().Take(4).ToList();
                var classes = (r["classes"] as JsonArray ?? []).OfType<JsonObject>().Take(3).ToList();
                if (lectures.Count > 0)
                {
                    rows.Add(new QuickRow { Kind = QuickKind.Header, Title = "Lectures", First = rows.Count == 0 });
                    foreach (var l in lectures)
                        rows.Add(new QuickRow
                        {
                            Kind = QuickKind.Lecture, Title = S(l["title"]), Dot = DotFor(S(l["class"])), LectureId = S(l["id"]),
                            Meta = $"{S(l["class"])} · {Date(S(l["date"]))?.LocalDateTime.ToString("ddd d MMM", CultureInfo.InvariantCulture)}",
                        });
                }
                if (passages.Count > 0)
                {
                    rows.Add(new QuickRow { Kind = QuickKind.Header, Title = "Passages from notes", First = rows.Count == 0 });
                    foreach (var p in passages)
                    {
                        string where = p["at"] is JsonValue v && v.TryGetValue(out double at) ? TimedText.Clock(at) : S(p["section"]) is { Length: > 0 } sec ? sec : "Transcript";
                        rows.Add(new QuickRow
                        {
                            Kind = QuickKind.Passage, Title = "…" + Excerpt(S(p["text"]), query) + "…", Sub = $"{S(p["title"])} · {where}", Dot = DotFor(S(p["class"])),
                            LectureId = S(p["id"]), At = p["at"] is JsonValue va && va.TryGetValue(out double a2) ? a2 : null,
                        });
                    }
                }
                if (classes.Count > 0)
                {
                    rows.Add(new QuickRow { Kind = QuickKind.Header, Title = "Classes", First = rows.Count == 0 });
                    foreach (var c in classes)
                    {
                        int n = c["lectures"]?.GetValue<int>() ?? 0;
                        rows.Add(new QuickRow { Kind = QuickKind.Class, Title = S(c["name"]), ClassName = S(c["name"]), Dot = Skin.ClassDot(c["color"]?.GetValue<int>() ?? 0), Meta = $"{n} lecture{(n == 1 ? "" : "s")}" });
                    }
                }
                if (rows.Count == 0) quick.Note = $"Nothing matches “{query.Trim()}”. {(Skin.Current == SkinKind.Mac ? "⌘↩" : "Ctrl+Enter")} asks your notes instead.";
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or LibraryRefusedException)
            {
                quick.Note = "Can't reach your library to search it.";
            }
        }
        Actions();
        quick.Rows.Clear();
        foreach (var r in rows) quick.Rows.Add(r);
        quick.SelectFirst();
    }

    /// <summary>A passage cut down to the part round what was searched for.</summary>
    static string Excerpt(string text, string query)
    {
        text = text.Replace('\n', ' ').Trim();
        int at = Controls.Marked.Find(text, query).FirstOrDefault() is { Length: > 0 } f ? f.Start : 0;
        int from = Math.Max(0, at - 40);
        if (from > 0) from = text.IndexOf(' ', from) is int sp and >= 0 && sp < at ? sp + 1 : from;
        string cut = text[from..];
        return cut.Length > 110 ? cut[..110].TrimEnd() : cut;
    }

    static async Task AskQuick(string question)
    {
        quick.Answering = true;
        quick.Thinking = true;
        quick.Answer = "";
        quick.Rows.Clear();
        quick.Note = null;
        var into = new ChatMessage();
        await Answer(into, lib => lib.AskAsync(question));
        quick.Thinking = false;
        quick.Answer = into.Text;
        if (host.Remote() is { } lib && into.Sources.Count > 0)
        {
            quick.Rows.Add(new QuickRow { Kind = QuickKind.Header, Title = "Sources", First = true });
            foreach (var s in into.Sources)
            {
                string title = s.LectureId ?? "";
                try
                {
                    if (s.LectureId is { } id && await lib.LectureAsync(id) is { } l) title = S(l["title"]);
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException or LibraryRefusedException)
                {
                }
                quick.Rows.Add(new QuickRow { Kind = QuickKind.Source, Title = title, Meta = s.Label, LectureId = s.LectureId, At = s.At });
            }
            quick.SelectFirst();
        }
    }

    static void OpenQuickRow(QuickRow row)
    {
        quickWindow?.Hide();
        switch (row.Kind)
        {
            case QuickKind.Class when row.ClassName is { } c:
                openClass = c;
                openLecture = null;
                ShowLibrary();
                break;
            case QuickKind.Source or QuickKind.Passage when row.LectureId is { } id && row.At is double at:
                if (File.Exists(host.Lectures.AudioPath(id))) Play(id, at);
                OpenLecture(id, transcript: row.Kind == QuickKind.Source);
                break;
            case QuickKind.Lecture or QuickKind.Passage or QuickKind.Source when row.LectureId is { } id:
                OpenLecture(id);
                break;
        }
    }
}
