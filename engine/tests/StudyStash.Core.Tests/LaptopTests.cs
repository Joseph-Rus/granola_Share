using System.Text.Json.Nodes;

namespace StudyStash.Core.Tests;

/// <summary>Stands in for Granola: these lectures, their full notes, and a transcript for each (none for some).</summary>
public sealed class FakeGranola(List<Meeting> stubs, List<Meeting>? full = null, Func<string, string>? transcript = null) : ILectureSource
{
    public List<(string Call, object? Arg)> Calls { get; } = [];

    sealed class Session : IGranolaSession
    {
        public Task<IReadOnlyList<ToolInfo>> ListToolsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ToolInfo>>([]);
        public Task<ToolResult> CallToolAsync(string name, JsonObject args, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    static Meeting Copy(Meeting m) => m.WithNotes(m.NotesMarkdown);

    public Task<IGranolaSession> SessionAsync(CancellationToken ct = default) => Task.FromResult<IGranolaSession>(new Session());

    public Task<List<Meeting>> ListMeetingsAsync(IGranolaSession session, DateOnly? since = null, DateOnly? until = null, int limit = 50,
        int maxPages = 20, CancellationToken ct = default)
    {
        Calls.Add(("list", since));
        return Task.FromResult(stubs.Select(Copy).ToList());
    }

    public Task<List<Meeting>> GetMeetingsAsync(IGranolaSession session, IReadOnlyList<string> ids, CancellationToken ct = default)
    {
        Calls.Add(("get", ids.ToList()));
        return Task.FromResult((full ?? []).Where(m => ids.Contains(m.Id)).Select(Copy).ToList());
    }

    public Task<string> GetTranscriptAsync(IGranolaSession session, string meetingId, CancellationToken ct = default) =>
        Task.FromResult(transcript?.Invoke(meetingId) ?? "");
}

/// <summary>tests/test_client.py and test_meeting_done.py: the laptop's watcher. At most one popup when a lecture
/// finishes, and one "it's filed" notification at the end. Plus the Python engine's own record of what it did, which
/// this engine must write byte for byte.</summary>
public class LaptopTests
{
    static JsonObject G => (JsonObject)JsonNode.Parse(Golden.Text("laptop.json"))!;

    static Meeting M(string id, string title, string date, string notes = "") => new(id) { Title = title, Date = date, NotesMarkdown = notes };

    [Fact]
    public void Copied_transcripts_are_read_like_python_reads_them()
    {
        var g = G;
        var today = DateOnly.Parse(g["today"].S(), System.Globalization.CultureInfo.InvariantCulture);
        foreach (var c in g["parse"]!.AsArray())
        {
            var got = Transcripts.ParseCopied(c![0].S(), today);
            var want = c[1];
            if (want is null)
            {
                Assert.Null(got);
                continue;
            }
            Assert.NotNull(got);
            Assert.Equal((want["title"].S(), want["date"]?.GetValue<string>(), want["body"].S()),
                (got.Title, got.Date is DateOnly d ? Transcripts.Day(d) : null, got.Body));
        }
        foreach (var c in g["norm"]!.AsArray()) Assert.Equal(c![1].S(), Transcripts.NormTitle(c[0].S()));
    }

    [Fact]
    public void The_transcript_store_saves_and_finds_like_python()
    {
        var g = G;
        var store = g["store"]!;
        var today = DateOnly.Parse(g["today"].S(), System.Globalization.CultureInfo.InvariantCulture);
        using var dir = new TempDir();
        var s = new TranscriptStore(dir.Path);
        var saves = (JsonArray)JsonNode.Parse(Golden.Text("laptop.json"))!["store"]!["saved"]!;
        // golden.py's STORE_SAVES, in order
        (string Text, string? Stopped)[] inputs =
        [
            ("Meeting Title: Membranes and osmosis\nDate: Sep 24\nTranscript:\nshort", null),
            ("Meeting Title: Membranes and osmosis\nDate: Sep 24\nTranscript:\na much longer transcript of the lecture", null),
            ("Meeting Title: Membranes and osmosis\nDate: Sep 24\nTranscript:\ntiny", null),
            ("Meeting Title: \nDate: Sep 24\nTranscript:\ncopied as the recording stopped", "2026-09-24T15:58:00"),
            ("Meeting Title: Later\nDate: Sep 24\nTranscript:\nthe next recording", "2026-09-24T18:30:00"),
            ("Meeting Title: Bio: lab/2 <draft>\nDate: Sep 23\nTranscript:\nfile name with odd characters", null),
        ];
        for (int i = 0; i < inputs.Length; i++)
        {
            var (path, changed) = s.Save(Transcripts.ParseCopied(inputs[i].Text, today)!, inputs[i].Text, Transcripts.IsoTime(inputs[i].Stopped));
            Assert.Equal((saves[i]![0].S(), saves[i]![1]!.GetValue<bool>()), (Path.GetFileName(path), changed));
        }
        Assert.Equal(store["files"]!.AsArray().Select(f => f.S()), Directory.EnumerateFiles(s.Dir).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        foreach (var f in store["finds"]!.AsArray())
            Assert.Equal(f![2].S(), s.Find(f[0].S(), f[1]?.GetValue<string>()));
    }

    /// <summary>golden.py's watcher: the same lectures, answers, library and clock.</summary>
    [Fact]
    public async Task The_watcher_asks_sends_and_writes_down_what_python_does()
    {
        var w = G["watcher"]!;
        using var dir = new TempDir();
        var cc = new ClientConfig(dir.Path)
        {
            ServerUrl = "http://mini:8787/", PoolKey = "pw", PoolName = "Pool — ü", DisplayName = "Sam", Mode = "ask", IncludeTranscripts = true,
        };
        var stubs = new List<Meeting> { M("a", "CS101 lec 1", "2026-09-10"), M("b", "Bio lab — café", "2026-09-11T14:05:00"), M("c", "Standup", "2026-09-12T09:00:00Z") };
        var full = stubs.Select(m => new Meeting(m.Id)
        {
            Title = m.Title, Date = m.Date, NotesMarkdown = $"notes {m.Id}", Attendees = ["Ada"], Raw = new JsonObject { ["id"] = m.Id, ["n"] = 1 },
        }).ToList();
        var granola = new FakeGranola(stubs, full, id => id == "b" ? "" : "the transcript");
        var answers = new Queue<bool?>([true, false, null]);
        var asked = new JsonArray();
        var posted = new JsonArray();
        var notified = new JsonArray();
        var got = new JsonArray();
        var clock = new Queue<DateTimeOffset>(w["clock"]!.AsArray().Select(t => DateTimeOffset.Parse(t.S(), System.Globalization.CultureInfo.InvariantCulture)));
        var host = new LaptopHost
        {
            Ask = (title, text, yes, no, timeout) =>
            {
                asked.Add(new JsonArray(title, text, yes, no, timeout));
                return answers.Count > 0 ? answers.Dequeue() : true;
            },
            Post = (url, body, key) =>
            {
                var payload = JsonNode.Parse(body)!;
                posted.Add(new JsonArray(url, payload, "Bearer " + key));
                return Task.FromResult(payload["id"].S() == "a" ? new JsonObject { ["class_name"] = "CS 101" } : new JsonObject { ["class_name"] = null });
            },
            Get = (url, _) =>
            {
                got.Add(url);
                return Task.FromResult<JsonObject?>(url.Contains("/a/", StringComparison.Ordinal) ? new JsonObject { ["status"] = "queued" }
                    : new JsonObject { ["status"] = "done", ["class_name"] = "Bio 110", ["summary_model"] = "m" });
            },
            Notify = (title, text) => notified.Add(new JsonArray(title, text)),
            Clock = () => clock.Dequeue(),
        };
        var client = new ShareClient(cc, granola, host, log: _ => { });
        var rounds = w["rounds"]!.AsArray();
        foreach (var round in rounds)
        {
            var rep = await client.PollOnceAsync(new DateOnly(2026, 9, 1));
            var want = round!["report"]!;
            Assert.Equal((want["listed"]!.GetValue<int>(), want["considered"]!.GetValue<int>()), (rep.Listed, rep.Considered));
            Assert.Equal(want["shared"]!.AsArray().Select(x => (x![0].S(), x[1].S())), rep.Shared);
            Assert.Equal(want["skipped"]!.AsArray().Select(x => x.S()), rep.Skipped);
            Assert.Equal(want["pending"]!.AsArray().Select(x => x.S()), rep.Pending);
            Assert.Equal(want["filed"]!.AsArray().Select(x => (x![0].S(), x[1].S())), rep.Filed);
            Assert.Empty(rep.Errors);
            Assert.Equal(round["state"].S(), File.ReadAllText(cc.StatePath).ReplaceLineEndings("\n")); // byte for byte
        }
        Assert.True(JsonNode.DeepEquals(w["asked"], asked), asked.ToJsonString());
        Assert.True(JsonNode.DeepEquals(w["posted"], posted), posted.ToJsonString());
        Assert.True(JsonNode.DeepEquals(w["notified"], notified), notified.ToJsonString());
        Assert.True(JsonNode.DeepEquals(w["got"], got), got.ToJsonString());
        // and the Python engine's own record reads back here as it was
        var again = new ShareClient(cc, granola, host, log: _ => { });
        Assert.Equal(rounds[^1]!["state"].S(), PyJson.Dumps(again.State, indent: 2));
    }

    // --- tests/test_client.py ----------------------------------------------------------------------------------

    sealed class Watcher
    {
        public List<string> Asked { get; } = [];
        public List<(string Url, JsonNode Payload, string Key)> Posted { get; } = [];
        public List<string> Notified { get; } = [];
        public Queue<bool?> Answers { get; } = new();
        public Func<string, Exception?> Fail { get; set; } = _ => null;
        public ClientConfig Cc { get; }
        public ShareClient Client { get; }

        public Watcher(TempDir dir, string mode = "ask", bool failing = false)
        {
            Cc = new ClientConfig(dir.Path) { ServerUrl = "http://mini:8787/", PoolKey = "pw", PoolName = "Pool", DisplayName = "Sam", Mode = mode };
            var stubs = new List<Meeting> { M("a", "CS101 lec 1", "2026-09-10"), M("b", "Bio lab", "2026-09-11"), M("c", "Standup", "2026-09-12") };
            var full = stubs.Select(m => new Meeting(m.Id) { Title = m.Title, Date = m.Date, NotesMarkdown = $"notes {m.Id}", Raw = new JsonObject { ["id"] = m.Id } }).ToList();
            if (failing) Fail = _ => new InvalidOperationException("server down");
            var host = new LaptopHost
            {
                Ask = (_, text, _, _, _) =>
                {
                    Asked.Add(text);
                    return Answers.Count > 0 ? Answers.Dequeue() : true;
                },
                Post = (url, body, key) =>
                {
                    Posted.Add((url, JsonNode.Parse(body)!, key));
                    return Fail(url) is Exception e ? throw e : Task.FromResult(new JsonObject { ["class_name"] = "CS 101" });
                },
                Get = (_, _) => Task.FromResult<JsonObject?>(null),
                Notify = (_, text) => Notified.Add(text),
            };
            Client = new ShareClient(Cc, new FakeGranola(stubs, full, _ => "t"), host, log: _ => { });
        }

        public string Decision(string id) => Client.State["seen"]![id]!["decision"].S();
    }

    [Fact]
    public async Task Ask_mode_shares_skips_waits_and_asks_again()
    {
        using var dir = new TempDir();
        var w = new Watcher(dir);
        foreach (bool? a in new bool?[] { true, false, null }) w.Answers.Enqueue(a);
        var rep = await w.Client.PollOnceAsync();
        Assert.Equal((3, 3), (rep.Listed, rep.Considered));
        Assert.Equal([("CS101 lec 1", "CS 101")], rep.Shared);
        Assert.Equal(["Bio lab"], rep.Skipped);
        Assert.Equal(["Standup"], rep.Pending);
        Assert.Contains("CS101 lec 1", w.Asked[0]);
        Assert.Contains("Pool", w.Asked[0]);
        var (url, payload, key) = w.Posted[0];
        Assert.Equal(("http://mini:8787/api/ingest", "pw"), (url, key));
        Assert.Equal(("Sam", "notes a", "t"), (payload["owner"].S(), payload["notes_markdown"].S(), payload["transcript"].S()));
        Assert.Empty(w.Notified); // the one notification comes when the library has filed it
        Assert.Equal(("shared", "skipped", "pending"), (w.Decision("a"), w.Decision("b"), w.Decision("c")));
        Assert.NotNull(w.Client.State["last_poll"]);
        // next time: only the one without an answer is asked again
        var rep2 = await w.Client.PollOnceAsync();
        Assert.Equal(1, rep2.Considered);
        Assert.Equal([("Standup", "CS 101")], rep2.Shared);
        Assert.Equal(2, w.Posted.Count);
    }

    [Fact]
    public async Task A_lecture_granola_left_without_a_folder_is_sent_under_the_class_the_timetable_says_was_on()
    {
        using var dir = new TempDir();
        // Bio lab starts at midnight on Friday 11 Sep; the timetable has Bio 110 then.
        new Timetable { Classes = [new TimetableClass("Bio 110", ClassTime.ParseMany("Fri 0:00-1:00")!)] }.Save(dir.Path);
        var w = new Watcher(dir, "auto");
        await w.Client.PollOnceAsync();
        Assert.Equal("Bio 110", w.Posted.Single(p => p.Payload["title"].S() == "Bio lab").Payload["folder"].S());
        Assert.Equal("", w.Posted.Single(p => p.Payload["title"].S() == "CS101 lec 1").Payload["folder"].S());
    }

    [Fact]
    public async Task Auto_mode_shares_everything_without_asking()
    {
        using var dir = new TempDir();
        var w = new Watcher(dir, "auto");
        var rep = await w.Client.PollOnceAsync();
        Assert.Equal(3, rep.Shared.Count);
        Assert.Empty(w.Asked);
        Assert.Equal(3, w.Posted.Count);
    }

    [Fact]
    public async Task A_send_that_fails_keeps_the_lecture_waiting()
    {
        using var dir = new TempDir();
        var w = new Watcher(dir, "auto", failing: true);
        var rep = await w.Client.PollOnceAsync();
        Assert.Equal(3, rep.Errors.Count);
        Assert.Equal(["CS101 lec 1", "Bio lab", "Standup"], rep.Pending);
        Assert.All(new[] { "a", "b", "c" }, id => Assert.Equal("pending", w.Decision(id)));
        Assert.Equal("other", w.Client.SendProblemKind);
        Assert.Equal("Couldn't send to Pool: server down", w.Client.SendProblem);
    }

    [Fact]
    public async Task A_new_library_password_is_said_once_and_cleared_on_success()
    {
        using var dir = new TempDir();
        var w = new Watcher(dir, "auto");
        w.Fail = url => new LibraryRefusedException(401, $"Client error '401 Unauthorized' for url '{url}'");
        await w.Client.PollOnceAsync();
        await w.Client.PollOnceAsync(); // retried; still refused
        Assert.Equal("password", w.Client.SendProblemKind);
        Assert.Contains("turned down this laptop's password", w.Client.SendProblem);
        Assert.Single(w.Notified); // one notification, not one per retry
        Assert.Contains("Open Study Stash", w.Notified[0]);
        w.Fail = _ => new HttpRequestException("Connection refused");
        await w.Client.PollOnceAsync();
        Assert.Equal("unreachable", w.Client.SendProblemKind);
        Assert.Contains("awake", w.Client.SendProblem);
        Assert.Single(w.Notified);
        w.Fail = _ => null;
        var rep = await w.Client.PollOnceAsync();
        Assert.Equal(3, rep.Shared.Count);
        Assert.Null(w.Client.SendProblem);
        Assert.Null(w.Client.SendProblemKind);
    }

    [Fact]
    public async Task A_lecture_you_said_send_to_is_retried_without_asking_again()
    {
        using var dir = new TempDir();
        var w = new Watcher(dir, failing: true);
        await w.Client.PollOnceAsync();
        Assert.Equal((3, 3), (w.Asked.Count, w.Posted.Count)); // asked once each, and the library didn't take them
        w.Fail = _ => null;
        var rep = await w.Client.PollOnceAsync();
        Assert.Equal(3, w.Asked.Count); // sent now, with no second popup
        Assert.Equal(3, rep.Shared.Count);
    }

    [Fact]
    public async Task The_transcript_stays_home_when_it_is_off()
    {
        using var dir = new TempDir();
        var w = new Watcher(dir, "auto");
        w.Cc.IncludeTranscripts = false;
        await w.Client.PollOnceAsync();
        Assert.Equal("", w.Posted[0].Payload["transcript"].S());
    }

    [Fact]
    public async Task Popups_are_off_unless_this_laptop_turns_them_on()
    {
        using var dir = new TempDir();
        var cc = new ClientConfig(dir.Path) { ServerUrl = "http://mini:8787", Mode = "ask" };
        var client = new ShareClient(cc, new FakeGranola([M("a", "L", "2026-09-10")]), new LaptopHost(), log: _ => { });
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => client.PollOnceAsync());
        Assert.Equal("Asking with a popup is off for this laptop.", e.Message);
    }

    // --- tests/test_meeting_done.py: at most one popup, and one "it's filed" notification -------------------------

    const string Title = "Membranes and osmosis";
    const string CopiedText = $"Meeting Title: {Title}\nDate: Sep 24\n\nTranscript:\nMe: osmosis and membranes, three cases.";

    sealed class Harness
    {
        public DateTimeOffset Now = new(2026, 9, 24, 16, 0, 0, TimeSpan.Zero);
        public ClientConfig Cc { get; }
        public TranscriptStore Store { get; }
        public List<JsonNode> Posted { get; } = [];
        public List<string> Notes { get; } = [];
        public List<(string Text, IReadOnlyList<string> Buttons, string Preferred)> Dialogs { get; } = [];
        public List<string> Captures { get; } = [];
        public int Allowed;
        public string? Answer = "Save";
        public string Copy;
        public string? Ended;
        public JsonObject? Status = new() { ["status"] = "queued" };
        public ShareClient Client { get; }

        public Harness(string home, string mode = "auto", string copy = ShareClient.CopyOn, string? answer = "Save", string? ended = null,
            string? captured = CopiedText)
        {
            (Answer, Copy, Ended) = (answer, copy, ended);
            Cc = new ClientConfig(home) { ServerUrl = "http://pool", PoolKey = "pw", PoolName = "Lecture notes", Mode = mode };
            Store = new TranscriptStore(home);
            var lecture = new Meeting("n1") { Title = Title, Date = "2026-09-24T15:00:00", NotesMarkdown = "Granola's summary" };
            var host = new LaptopHost
            {
                Ask = (_, _, _, _, _) => true,
                Choose = (_, text, buttons, preferred, _) =>
                {
                    Dialogs.Add((text, buttons, preferred));
                    return Answer;
                },
                Notify = (_, text) => Notes.Add(text),
                Post = (_, body, _) =>
                {
                    Posted.Add(JsonNode.Parse(body)!);
                    return Task.FromResult(new JsonObject { ["class_name"] = null });
                },
                Get = (_, _) => Task.FromResult(Status?.DeepClone().AsObject()),
                Clock = () => Now,
            };
            Client = new ShareClient(Cc, new FakeGranola([new Meeting("n1") { Title = Title, Date = lecture.Date }], [lecture]), host, Store, _ => { })
            {
                CopyState = () => Copy,
                CaptureFor = title =>
                {
                    Captures.Add(title);
                    return captured is null ? "" : Transcripts.ParseCopied(captured)!.Body;
                },
                AllowCopying = () => Allowed++,
                HandledSince = _ => Ended,
            };
        }

        public Task<ClientReport> Poll() => Client.PollOnceAsync();

        public void Copied() => Store.Save(Transcripts.ParseCopied(CopiedText, new DateOnly(2026, 9, 24))!, CopiedText);
    }

    [Fact]
    public async Task Copied_when_the_recording_ended_means_no_popup_at_all()
    {
        using var dir = new TempDir();
        var h = new Harness(dir.Path, ended: "copied");
        h.Copied();
        await h.Poll();
        Assert.Empty(h.Dialogs);
        Assert.Contains("osmosis", h.Posted[0]["transcript"].S());
        Assert.Empty(h.Notes); // no "sent" notification: the one message is "it's filed"
        h.Status = new JsonObject { ["status"] = "done", ["class_name"] = "Data Science", ["summary_model"] = "qwen3.6:35b-a3b" };
        await h.Poll();
        Assert.Equal([$"“{Title}” is in Lecture notes under Data Science. Notes written from the transcript."], h.Notes);
    }

    [Fact]
    public async Task One_click_save_when_the_end_was_missed()
    {
        using var dir = new TempDir();
        var h = new Harness(dir.Path); // the laptop didn't see the recording end (asleep, or recorded on the phone)
        await h.Poll();
        var (text, buttons, preferred) = h.Dialogs[0];
        Assert.Equal(["Without transcript", "Save"], buttons);
        Assert.Equal("Save", preferred);
        Assert.Contains(Title, text);
        Assert.Contains("opens Granola, copies the transcript, and brings you back", text);
        Assert.Equal([Title], h.Captures);
        Assert.Contains("osmosis", h.Posted[0]["transcript"].S());
        Assert.Empty(h.Notes);
    }

    [Fact]
    public async Task A_save_that_cant_find_the_lecture_still_sends_and_says_how_to_add_it()
    {
        using var dir = new TempDir();
        var h = new Harness(dir.Path, captured: null);
        await h.Poll();
        Assert.Equal("", h.Posted[0]["transcript"].S());
        Assert.Contains("Open it in Granola any time", h.Notes[0]);
    }

    [Fact]
    public async Task Not_now_at_the_end_of_the_recording_is_not_asked_again()
    {
        using var dir = new TempDir();
        var h = new Harness(dir.Path, ended: "not now");
        await h.Poll();
        Assert.Empty(h.Dialogs);
        Assert.Equal("", h.Posted[0]["transcript"].S()); // sent with Granola's summary
    }

    [Fact]
    public async Task Waits_while_the_end_of_recording_popup_is_up()
    {
        using var dir = new TempDir();
        var h = new Harness(dir.Path, ended: "asking");
        await h.Poll();
        Assert.Empty(h.Dialogs);
        Assert.Empty(h.Posted);
        h.Ended = "saved";
        h.Copied();
        h.Cc.Mode = "ask";
        await h.Poll();
        Assert.Empty(h.Dialogs);
        Assert.Contains("osmosis", h.Posted[0]["transcript"].S()); // Save already meant yes
    }

    [Fact]
    public async Task Missing_permission_opens_settings_and_sends_meanwhile()
    {
        using var dir = new TempDir();
        var h = new Harness(dir.Path, copy: ShareClient.CopyNeedsPermission, answer: "Allow");
        await h.Poll();
        var (text, buttons, _) = h.Dialogs[0];
        Assert.Equal("Allow", buttons[^1]);
        Assert.Contains("python3.12", text);
        Assert.Equal(1, h.Allowed);
        Assert.Empty(h.Captures);
        Assert.Equal("", h.Posted[0]["transcript"].S());
    }

    [Fact]
    public async Task Ask_mode_adds_skip_and_no_answer_asks_again()
    {
        using var dir = new TempDir();
        var h = new Harness(dir.Path, mode: "ask", answer: "Skip");
        await h.Poll();
        Assert.Equal(["Skip", "Without transcript", "Save"], h.Dialogs[0].Buttons);
        Assert.Empty(h.Posted);
        Assert.Equal("skipped", h.Client.State["seen"]!["n1"]!["decision"].S());
        var h2 = new Harness(dir["b"], answer: null); // nobody answered: ask again next time
        await h2.Poll();
        await h2.Poll();
        Assert.Equal(2, h2.Dialogs.Count);
        Assert.Empty(h2.Posted);
    }

    [Fact]
    public async Task No_copying_means_the_plain_flow()
    {
        using var dir = new TempDir();
        var h = new Harness(dir.Path, copy: ShareClient.CopyOff);
        await h.Poll();
        Assert.Empty(h.Dialogs);
        Assert.Single(h.Posted);
    }

    [Fact]
    public async Task Lectures_missing_a_transcript_are_wanted_and_sent_again_once_copied()
    {
        using var dir = new TempDir();
        var h = new Harness(dir.Path, answer: "Without transcript");
        await h.Poll();
        Assert.Equal(["membranes and osmosis"], h.Client.MissingTranscripts());
        h.Copied(); // opened in Granola later
        var rep = await h.Poll();
        Assert.Equal([Title], rep.Reshared);
        Assert.Empty(h.Client.MissingTranscripts());
        Assert.True(h.Client.State["seen"]!["n1"]!["resent"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Old_libraries_that_cant_say_are_asked_once()
    {
        using var dir = new TempDir();
        var h = new Harness(dir.Path, answer: "Without transcript");
        await h.Poll();
        Assert.True(h.Client.Busy());
        h.Status = null; // a 0.1 library answers 404
        await h.Poll();
        Assert.Null(h.Client.State["seen"]!["n1"]!["filed"]);
        Assert.False(h.Client.Busy());
    }

    [Fact]
    public async Task The_loop_checks_again_when_woken_and_stops_when_asked()
    {
        using var dir = new TempDir();
        var w = new Watcher(dir, "auto");
        w.Cc.PollIntervalSeconds = 3600;
        using var stop = new CancellationTokenSource();
        var loop = w.Client.RunLoopAsync(stop.Token);
        for (int i = 0; i < 100 && w.Posted.Count < 3; i++) await Task.Delay(20);
        Assert.Equal(3, w.Posted.Count);
        await stop.CancelAsync();
        await loop.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(w.Client.LastError);
    }

    [Fact]
    public async Task The_library_answers_the_laptop_the_way_httpx_did()
    {
        var library = new FakeOllama((path, _) => path switch
        {
            "/api/ingest" => JsonNode.Parse("""{"ok": true, "class_name": "CS 101"}""")!,
            "/api/notes/a%2Fb/status" => JsonNode.Parse("""{"status": "done"}""")!,
            _ => (System.Net.HttpStatusCode.NotFound, """{"detail": "Not Found"}"""),
        });
        Assert.Equal("CS 101", (await LibraryHttp.PostAsync("http://mini:8787/api/ingest", "{}", "pw", library.Client()))["class_name"].S());
        Assert.Equal("done", (await LibraryHttp.GetAsync("http://mini:8787/api/notes/a%2Fb/status", "pw", library.Client()))!["status"].S());
        Assert.Null(await LibraryHttp.GetAsync("http://mini:8787/api/notes/x/status", "pw", library.Client())); // a library too old to say
        var refused = new FakeOllama((_, _) => (System.Net.HttpStatusCode.Unauthorized, """{"detail": "bad pool password"}"""));
        var e = await Assert.ThrowsAsync<LibraryRefusedException>(() => LibraryHttp.PostAsync("http://mini:8787/api/ingest", "{}", "x", refused.Client()));
        Assert.Equal(401, e.Status);
        Assert.Equal("Client error '401 Unauthorized' for url 'http://mini:8787/api/ingest'\n"
            + "For more information check: https://developer.mozilla.org/en-US/docs/Web/HTTP/Status/401", e.Message);
    }
}
