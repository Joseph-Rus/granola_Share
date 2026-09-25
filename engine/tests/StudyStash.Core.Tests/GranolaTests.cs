using System.Text.Json.Nodes;

namespace StudyStash.Core.Tests;

/// <summary>
/// tests/test_granola.py: Granola's MCP output (the XML dialect, its real list_meetings schema, accounts), plus the
/// Python engine's own answers on every captured reply and a set of harder ones.
/// </summary>
public class GranolaTests
{
    static JsonArray G(string name) => (JsonArray)Golden.Granola(name)!;

    static string Fixture(int i) => G("xml")[i]![0].S(); // the replies captured live, in golden.py's order

    static string ListXml => Fixture(0);
    static string GetXml => Fixture(1);
    static string MessyXml => Fixture(2);
    static string TranscriptXml => Fixture(3);
    static string ZeroXml => Fixture(4);
    static string LiveList => Fixture(5);

    static readonly JsonObject ListSchema = (JsonObject)JsonNode.Parse("""
        {"type": "object", "properties": {
            "time_range": {"type": "string", "enum": ["this_week", "last_week", "last_30_days", "custom"], "default": "last_30_days"},
            "custom_start": {"type": "string"}, "custom_end": {"type": "string"},
            "folder_id": {"type": "string"}, "workspace_only": {"type": "boolean"}, "involvement": {"type": "string"}}}
        """)!;

    const string AccountJson = """
        {"email": "alex@example.com", "active_workspace": {"id": "ws-77", "display_name": "Alex's workspace"},
         "mcp_note_access": {"scopes": ["personal", "public"]}}
        """;

    static JsonNode? Parse(string text) => Granola.ParseToolResult(ToolResult.Text(text));

    // --- what Python answers ------------------------------------------------------------------------------------

    [Fact]
    public void Xml_reads_as_python_reads_it()
    {
        foreach (var c in G("xml"))
            Assert.Equal(Golden.Dump(c![1]), Golden.Dump(Granola.XmlToData(c[0].S())));
        foreach (var c in G("loose"))
            Assert.Equal(Golden.Dump(c![1]), Golden.Dump(Granola.LooseParse(c[0].S())));
        foreach (var c in G("access_notice"))
            Assert.Equal(c![1].S(), Granola.AccessNotice(c[0].S()));
    }

    [Fact]
    public void Tool_replies_read_as_python_reads_them()
    {
        foreach (var c in G("tool_result"))
        {
            var texts = c![0]!.AsArray().Select(t => t.S()).ToList();
            Assert.Equal(Golden.Dump(c[1]), Golden.Dump(Granola.ParseToolResult(new ToolResult(null, texts))));
        }
        foreach (var c in G("extract"))
            Assert.Equal(Golden.Dump(c![1]), Golden.Dump(new JsonArray(Granola.ExtractMeetings(c[0]).Select(m => m.DeepClone()).ToArray())));
        foreach (var c in G("transcript_text"))
            Assert.Equal(c![1].S(), Granola.TranscriptText(c[0]));
    }

    [Fact]
    public void Arguments_match_python_for_every_schema()
    {
        foreach (var c in G("build_args"))
        {
            var wants = c![1]!.AsObject().Select(kv => (kv.Key, kv.Value?.DeepClone())).ToList();
            Assert.Equal(Golden.Dump(c[2]), Golden.Dump(Granola.BuildArgs(c[0], wants)));
        }
        foreach (var c in G("list_args"))
        {
            var call = c![1]!.AsArray();
            DateOnly? Day(JsonNode? n) => n is null ? null : DateOnly.Parse(n.S(), System.Globalization.CultureInfo.InvariantCulture);
            var got = Granola.ListArgs(c[0], Day(call[0]), Day(call[1]), call[2]!.GetValue<int>(), call[3]!.GetValue<int>(), new DateOnly(2026, 2, 4));
            Assert.Equal(Golden.Dump(c[2]), Golden.Dump(got));
        }
    }

    [Fact]
    public void Accounts_read_as_python_reads_them()
    {
        foreach (var c in G("account"))
        {
            var info = Granola.NormalizeAccount(c![0]);
            var got = new JsonObject
            {
                ["email"] = info.Email, ["workspace"] = info.Workspace, ["workspace_id"] = info.WorkspaceId,
                ["scopes"] = new JsonArray(info.Scopes.Select(s => (JsonNode?)s).ToArray()),
            };
            Assert.Equal(Golden.Dump(c[1]), Golden.Dump(got));
        }
        foreach (var c in G("account_label"))
        {
            var info = c![0] is JsonObject o ? new AccountInfo(o["email"]?.S() ?? "", o["workspace"]?.S() ?? "", "", [], null) : null;
            Assert.Equal(c[1].S(), Granola.AccountLabel(info));
        }
    }

    [Fact]
    public void Dedent_matches_textwrap()
    {
        foreach (var c in G("dedent"))
            Assert.Equal(c![1].S(), Py.Dedent(c[0].S()));
    }

    // --- tests/test_granola.py ------------------------------------------------------------------------------------

    [Fact]
    public void Parse_tool_result_prefers_structured()
    {
        var structured = JsonNode.Parse("""{"meetings": [{"id": "a"}]}""");
        Assert.Same(structured, Granola.ParseToolResult(new ToolResult(structured, ["ignored"])));
        Assert.Equal("""{"notes": [{"id": "x"}]}""", Golden.Dump(Parse("""{"notes": [{"id": "x"}]}""")));
        Assert.Equal("hello", Parse("hello").S());
    }

    [Fact]
    public void Extract_meetings_shapes()
    {
        Assert.Equal("[{\"id\": \"1\"}]", Golden.Dump(new JsonArray(Granola.ExtractMeetings(JsonNode.Parse("""[{"id": "1"}, "junk"]""")).Select(m => m.DeepClone()).ToArray())));
        Assert.Single(Granola.ExtractMeetings(JsonNode.Parse("""{"data": {"results": [{"id": "3"}]}}""")));
        Assert.Empty(Granola.ExtractMeetings(JsonValue.Create("text")));
    }

    [Fact]
    public void Build_args_uses_real_param_names()
    {
        var schema = JsonNode.Parse("""{"properties": {"date_from": {"type": "string"}, "limit": {"type": "integer"}, "document_ids": {"type": "string"}}}""");
        Assert.Equal("{\"date_from\": \"2026-09-01\", \"limit\": 20}", Golden.Dump(Granola.BuildArgs(schema, [("since", "2026-09-01"), ("limit", 20), ("offset", null)])));
        Assert.Equal("{\"document_ids\": \"a,b\"}", Golden.Dump(Granola.BuildArgs(schema, [("ids", new JsonArray("a", "b"))])));
        Assert.Equal("{\"meeting_ids\": [\"a\"]}", Golden.Dump(Granola.BuildArgs(JsonNode.Parse("""{"properties": {"meeting_ids": {"type": "array"}}}"""), [("ids", new JsonArray("a"))])));
        Assert.Empty(Granola.BuildArgs(null, [("since", "x")]));
    }

    [Fact]
    public void List_meetings_xml_with_unescaped_participant_emails()
    {
        var data = (JsonObject)Parse(ListXml)!;
        Assert.Equal(("Jan 27, 2026", "Feb 4, 2026", "2"), (data["from"].S(), data["to"].S(), data["count"].S()));
        Assert.Single(data["meeting"]!.AsArray()); // a single <meeting> becomes a list
        var m = Granola.NormalizeMeeting(Granola.ExtractMeetings(data).Single());
        Assert.Equal(("0dba4400-50f1-4262-9ac7-89cd27b79371", "Team sync", "2026-02-04T19:30:00"), (m.Id, m.Title, m.Date));
        Assert.Equal(["John Doe", "Jane Smith"], m.Attendees); // cleaned of "(note creator)", "from Acme", <email>
        Assert.Equal("John Doe", m.Owner); // the note creator becomes the owner
    }

    [Fact]
    public void Get_meetings_xml_keeps_markdown_in_the_summary()
    {
        var m = Granola.NormalizeMeeting(Granola.ExtractMeetings(Parse(GetXml))[0]);
        Assert.Equal("## Key Decisions\n- Approved Q1 roadmap", m.NotesMarkdown);
        Assert.Equal(("Team sync", ""), (m.Title, m.Date));
    }

    [Fact]
    public void Xml_survives_bare_ampersands_and_stray_angle_brackets()
    {
        var m = Granola.NormalizeMeeting(Granola.ExtractMeetings(Parse(MessyXml))[0]);
        Assert.Equal("Ops & Eng sync", m.Title);
        Assert.Contains("Cost < 5% of budget & falling", m.NotesMarkdown);
        Assert.Contains("Ping <john@acme.com> for the deck", m.NotesMarkdown);
    }

    [Fact]
    public void Transcript_xml()
    {
        var payload = Parse(TranscriptXml);
        Assert.Equal("0dba4400", payload!["meeting_id"].S());
        Assert.Equal("[00:00:15] John: Let's get started…", Granola.TranscriptText(payload));
        Assert.Equal("plain", Granola.TranscriptText(JsonNode.Parse("""{"transcript": "plain"}""")));
        Assert.Equal("just text", Granola.TranscriptText(JsonValue.Create("just text")));
    }

    [Fact]
    public void Empty_account_payloads_give_no_meetings_without_error()
    {
        Assert.Empty(Granola.ExtractMeetings(Parse("""{"count":0,"total_in_range":0,"date_range":{"from":"2026-08-17T00:00:00.000Z"},"meetings":[]}""")));
        var zero = Parse(ZeroXml)!;
        Assert.Equal("0", zero["count"].S());
        Assert.Empty(zero["meeting"]!.AsArray());
        Assert.Empty(Granola.ExtractMeetings(zero));
    }

    [Fact]
    public void Xml_to_data_returns_the_text_when_it_cannot_parse()
    {
        Assert.Equal("not xml at all", Granola.XmlToData("not xml at all").S());
        Assert.Equal("<broken><a>1", Granola.XmlToData("<broken><a>1").S());
    }

    [Fact]
    public void List_args_with_the_real_schema()
    {
        Assert.Equal("{\"time_range\": \"custom\", \"custom_start\": \"2026-02-01\", \"custom_end\": \"2026-02-05\"}", // end = tomorrow
            Golden.Dump(Granola.ListArgs(ListSchema, new DateOnly(2026, 2, 1), today: new DateOnly(2026, 2, 4))));
        Assert.Equal("{\"time_range\": \"last_30_days\"}", Golden.Dump(Granola.ListArgs(ListSchema)));
        Assert.Empty(Granola.ListArgs(null, new DateOnly(2026, 2, 1)));
    }

    [Fact]
    public void List_args_skips_custom_when_the_account_cannot_use_it()
    {
        // A free Granola plan offers no `custom` range; sending it is a hard validation error.
        var free = JsonNode.Parse("""{"properties": {"time_range": {"type": "string", "enum": ["this_week", "last_week", "last_30_days"]}}}""");
        Assert.Equal("{\"time_range\": \"last_30_days\"}", Golden.Dump(Granola.ListArgs(free, new DateOnly(2026, 9, 1))));
    }

    [Fact]
    public void Accounts_and_their_label()
    {
        var info = Granola.NormalizeAccount(JsonNode.Parse(AccountJson));
        Assert.Equal(("alex@example.com", "Alex's workspace", "ws-77"), (info.Email, info.Workspace, info.WorkspaceId));
        Assert.Equal(["personal", "public"], info.Scopes);
        Assert.Equal("alex@example.com · Alex's workspace", Granola.AccountLabel(info));
        Assert.Equal("not signed in", Granola.AccountLabel(null));
        Assert.Equal("not signed in", Granola.AccountLabel(Granola.NormalizeAccount(null)));
    }

    [Fact]
    public void List_payload_survives_access_notice_and_prose_preamble()
    {
        var data = Granola.XmlToData(LiveList);
        Assert.IsType<JsonObject>(data); // a notice and a sentence of prose must not defeat the parser
        var meetings = Granola.ExtractMeetings(data);
        Assert.Equal(2, meetings.Count);
        var first = Granola.NormalizeMeeting(meetings[0]);
        Assert.Equal("1f0c2a7e-3b4d-4e5f-8a6b-7c8d9e0f1a2b", first.Id);
        Assert.StartsWith("Cell biology", first.Title);
        Assert.Equal("2026-09-15T14:20:00", first.Date); // 2:20 PM PDT: the zone dropped, the wall time kept
        Assert.Equal("alex rivera", first.Owner); // the (note creator) participant
        Assert.Equal(["alex rivera"], first.Attendees);
        Assert.Equal("2026-09-14T08:15:00", Granola.NormalizeMeeting(meetings[1]).Date);
        Assert.Equal("Results exclude public workspace notes because of your Granola plan.", Granola.AccessNotice(LiveList));
        Assert.Equal("", Granola.AccessNotice("<meetings_data count=\"0\"></meetings_data>"));
        Assert.Equal(2, Granola.ExtractMeetings(Parse(LiveList)).Count);
    }

    [Fact]
    public void Loose_xml_keeps_single_quoted_attributes()
    {
        // The Python engine's fallback parser lost every value in single quotes; both engines keep it now.
        Assert.Equal("{\"id\": \"m1\", \"title\": \"Two\", \"b\": \"y\", \"text\": \"x &\"}",
            Golden.Dump(Granola.LooseParse("<meeting id='m1' title=\"Two\">x & <b>y</b></meeting>")));
    }

    // --- the client, against a fake MCP session --------------------------------------------------------------------

    /// <summary>An MCP session with Granola's real tool list; `answers` maps a tool's name to its reply.</summary>
    sealed class FakeSession(Dictionary<string, string> answers, Dictionary<string, JsonObject>? schemas = null, params string[] errors) : IGranolaSession
    {
        public List<(string Name, JsonObject Args)> Calls { get; } = [];

        readonly Dictionary<string, JsonObject> schemas = schemas ?? new()
        {
            ["list_meetings"] = ListSchema,
            ["get_meetings"] = (JsonObject)JsonNode.Parse("""{"properties": {"meeting_ids": {"type": "array"}}}""")!,
            ["get_meeting_transcript"] = (JsonObject)JsonNode.Parse("""{"properties": {"meeting_id": {"type": "string"}}}""")!,
            ["get_account_info"] = (JsonObject)JsonNode.Parse("""{"properties": {}}""")!,
        };

        public Task<IReadOnlyList<ToolInfo>> ListToolsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ToolInfo>>(schemas.Select(kv => new ToolInfo(kv.Key, "", kv.Value)).ToList());

        public Task<ToolResult> CallToolAsync(string name, JsonObject args, CancellationToken ct = default)
        {
            Calls.Add((name, args));
            return Task.FromResult(errors.Contains(name) ? new ToolResult(null, ["boom"], true) : ToolResult.Text(answers.GetValueOrDefault(name, "")));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    static GranolaClient ClientFor(FakeSession session) => new(_ => Task.FromResult<IGranolaSession>(session));

    [Fact]
    public async Task Client_sends_a_custom_range_and_parses_the_xml()
    {
        var session = new FakeSession(new() { ["list_meetings"] = ListXml });
        var meetings = await ClientFor(session).ListMeetingsAsync(session, new DateOnly(2026, 1, 27));
        var (name, args) = session.Calls[0];
        Assert.Equal("list_meetings", name);
        Assert.Equal(("custom", "2026-01-27"), (args["time_range"].S(), args["custom_start"].S()));
        Assert.False(string.IsNullOrEmpty(args["custom_end"].S()));
        Assert.Single(session.Calls); // the real schema has no paging, so there's no loop
        Assert.Equal(["Team sync"], meetings.Select(m => m.Title));
        Assert.Equal(["John Doe", "Jane Smith"], meetings[0].Attendees);
    }

    [Fact]
    public async Task Client_handles_the_empty_json_payload()
    {
        var session = new FakeSession(new() { ["list_meetings"] = """{"count":0,"meetings":[]}""" });
        Assert.Empty(await ClientFor(session).ListMeetingsAsync(session));
        Assert.Equal("{\"time_range\": \"last_30_days\"}", Golden.Dump(session.Calls[0].Args));
    }

    [Fact]
    public async Task Client_get_meetings_and_transcript()
    {
        var session = new FakeSession(new() { ["get_meetings"] = GetXml, ["get_meeting_transcript"] = TranscriptXml });
        var client = ClientFor(session);
        var full = await client.GetMeetingsAsync(session, ["0dba4400-50f1-4262-9ac7-89cd27b79371"]);
        Assert.Equal("{\"meeting_ids\": [\"0dba4400-50f1-4262-9ac7-89cd27b79371\"]}", Golden.Dump(session.Calls[0].Args));
        Assert.StartsWith("## Key Decisions", full[0].NotesMarkdown);
        Assert.StartsWith("[00:00:15] John:", await client.GetTranscriptAsync(session, "0dba4400"));
    }

    [Fact]
    public async Task Client_get_account_info()
    {
        var session = new FakeSession(new() { ["get_account_info"] = AccountJson });
        var info = await ClientFor(session).GetAccountInfoAsync(session);
        Assert.Equal(("alex@example.com", "Alex's workspace"), (info!.Email, info.Workspace));
        // a tool that errors, and a server without the tool at all, both mean "we cannot say"
        var broken = new FakeSession(new() { ["get_account_info"] = "{}" }, errors: "get_account_info");
        Assert.Null(await ClientFor(broken).GetAccountInfoAsync(broken));
        var missing = new FakeSession([], new() { ["list_meetings"] = ListSchema });
        Assert.Null(await ClientFor(missing).GetAccountInfoAsync(missing));
    }

    [Fact]
    public async Task A_failing_tool_says_what_failed_and_a_missing_one_lists_what_there_is()
    {
        var session = new FakeSession([], errors: "list_meetings");
        var e = await Assert.ThrowsAsync<GranolaToolException>(() => ClientFor(session).ListMeetingsAsync(session));
        Assert.Equal("list_meetings failed: boom", e.Message);
        var bare = new FakeSession([], new() { ["whoami"] = new JsonObject() });
        var missing = await Assert.ThrowsAsync<GranolaToolException>(() => ClientFor(bare).GetMeetingsAsync(bare, ["x"]));
        Assert.Equal("no get_meetings tool on server; tools are ['whoami']", missing.Message);
        Assert.Equal("", await ClientFor(bare).GetTranscriptAsync(bare, "x")); // a free plan has no transcript tool
    }

    [Fact]
    public async Task A_schema_with_paging_is_read_page_by_page()
    {
        var pages = new Queue<string>(["[{\"id\": \"1\"}, {\"id\": \"2\"}]", "[{\"id\": \"2\"}, {\"id\": \"3\"}]", "[{\"id\": \"4\"}]"]);
        var legacy = (JsonObject)JsonNode.Parse("""{"properties": {"date_from": {}, "limit": {}, "offset": {}}}""")!;
        var session = new PagedSession(pages, legacy);
        var meetings = await new GranolaClient(_ => Task.FromResult<IGranolaSession>(session)).ListMeetingsAsync(session, new DateOnly(2026, 9, 1), limit: 2);
        Assert.Equal(["1", "2", "3", "4"], meetings.Select(m => m.Id));
        Assert.Equal(["{\"date_from\": \"2026-09-01\", \"limit\": 2}", "{\"date_from\": \"2026-09-01\", \"limit\": 2, \"offset\": 2}",
            "{\"date_from\": \"2026-09-01\", \"limit\": 2, \"offset\": 4}"], session.Calls.Select(Golden.Dump));
    }

    sealed class PagedSession(Queue<string> pages, JsonObject schema) : IGranolaSession
    {
        public List<JsonObject> Calls { get; } = [];

        public Task<IReadOnlyList<ToolInfo>> ListToolsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ToolInfo>>([new ToolInfo("list_meetings", "", schema)]);

        public Task<ToolResult> CallToolAsync(string name, JsonObject args, CancellationToken ct = default)
        {
            Calls.Add(args);
            return Task.FromResult(ToolResult.Text(pages.Dequeue()));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
