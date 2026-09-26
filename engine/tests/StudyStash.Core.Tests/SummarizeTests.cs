using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace StudyStash.Core.Tests;

/// <summary>Study notes from the transcript, the pipeline, and the runaway-model guards.</summary>
public class SummarizeTests
{
    const string Line = "The derivative measures how fast a function changes at a point.\n";

    static string Lines(int n) => string.Concat(Enumerable.Repeat(Line, n));

    static Config CfgFor(TempDir dir) => new(dir["home"], dir["pool"])
    {
        OllamaModel = "small:1b", SummaryModel = "big:35b",
        Classes = [new ClassDef("Calc 1", ["math 151"]), new ClassDef("Bio 110")],
    };

    static SortChatFn Sort(string json) => (_, _, _) => Task.FromResult(json);

    static Func<Meeting, Config, Task<string>> Notes(string text) => (_, _) => Task.FromResult(text);

    static readonly Action<string> Quiet = _ => { };

    [Fact]
    public void Split_transcript_respects_budget_and_keeps_text()
    {
        string text = Lines(200);
        var parts = Summarize.SplitTranscript(text, 1000);
        Assert.True(parts.Count > 1 && parts.All(p => p.Length <= 1000));
        Assert.Equal(text.Replace("\n", ""), string.Concat(parts).Replace("\n", ""));
        // one giant line with no breaks still splits, at sentence ends
        string one = string.Concat(Enumerable.Repeat("Limits come first. ", 300)).Trim();
        parts = Summarize.SplitTranscript(one, 500);
        Assert.True(parts.All(p => p.Length <= 500));
        Assert.EndsWith("first.", parts[0]);
    }

    [Fact]
    public void Splitting_matches_python()
    {
        foreach (var c in Golden.Cases("split"))
            Assert.Equal(c![2]!.AsArray().Select(p => p.S()), Summarize.SplitTranscript(c[0].S(), c[1]!.GetValue<int>()));
        foreach (var c in Golden.Cases("budget"))
            Assert.Equal(c![1]!.GetValue<int>(), Summarize.TranscriptBudget(c[0]!.GetValue<int>()));
    }

    [Fact]
    public void Clean_output_strips_think_and_fences()
    {
        Assert.Equal("## Overview\nx", Summarize.CleanOutput("<think>hmm</think>\n```markdown\n## Overview\nx\n```"));
        Assert.Equal("## A", Summarize.CleanOutput("  ## A  "));
        foreach (var c in Golden.Cases("clean"))
        {
            var want = c![1]!.AsObject();
            string? input = c[0]?.GetValue<string>();
            if (want.ContainsKey("ok")) Assert.Equal(want["ok"].S(), Summarize.CleanOutput(input));
            else Assert.Throws<RunawayOutputException>(() => Summarize.CleanOutput(input));
        }
        foreach (var c in Golden.Cases("repetitive"))
            Assert.Equal(c![1]!.GetValue<bool>(), Summarize.Repetitive(c[0].S()));
    }

    [Fact]
    public async Task Short_transcript_is_one_call_with_chosen_model()
    {
        using var dir = new TempDir();
        var cfg = CfgFor(dir);
        var calls = new List<(string Model, int Ctx, string Prompt)>();
        var m = new Meeting("1") { Title = "Calc lecture 3", Date = "2026-09-20T09:00", Transcript = Lines(40) };
        string output = await Summarize.SummarizeTranscriptAsync(m, cfg, (_, model, prompt, ctx) =>
        {
            calls.Add((model, ctx, prompt));
            return Task.FromResult("## Overview\nDerivatives.");
        }, (_, _) => Task.FromResult<int?>(131072));
        Assert.Equal("## Overview\nDerivatives.", output);
        Assert.Single(calls);
        Assert.Equal(("big:35b", 32768), (calls[0].Model, calls[0].Ctx)); // capped at max_context
        Assert.Contains("## Key points", calls[0].Prompt);
        Assert.Contains("Calc lecture 3", calls[0].Prompt);
        Assert.Contains("derivative", calls[0].Prompt);
    }

    [Fact]
    public async Task Long_transcript_is_split_then_merged()
    {
        using var dir = new TempDir();
        var cfg = CfgFor(dir);
        cfg.SummaryMaxContext = 8192;
        var prompts = new List<string>();
        int budget = Summarize.TranscriptBudget(8192);
        var m = new Meeting("1") { Title = "Long", Transcript = Lines(budget / Line.Length * 3) };
        string output = await Summarize.SummarizeTranscriptAsync(m, cfg, (_, _, prompt, _) =>
        {
            prompts.Add(prompt);
            return Task.FromResult(prompt.Contains("Merge them into one set of study notes") ? "## Overview\nmerged" : "- notes");
        }, (_, _) => Task.FromResult<int?>(null));
        Assert.Equal("## Overview\nmerged", output);
        Assert.True(prompts.Count(p => p.Contains("Transcript part")) >= 3);
        Assert.Contains("Below are notes on consecutive parts", prompts[^1]);
    }

    [Fact]
    public async Task Very_long_notes_are_folded_in_pairs_before_the_merge()
    {
        using var dir = new TempDir();
        var cfg = CfgFor(dir);
        cfg.SummaryMaxContext = 8192;
        int budget = Summarize.TranscriptBudget(8192);
        var prompts = new List<string>();
        var m = new Meeting("1") { Title = "Huge", Transcript = Lines(budget / Line.Length * 5) };
        await Summarize.SummarizeTranscriptAsync(m, cfg, (_, _, prompt, _) =>
        {
            prompts.Add(prompt);
            // every part's notes are a third of the budget: five of them don't fit one merge
            return Task.FromResult(prompt.StartsWith("Combine these notes") ? "- short" : string.Join("\n", Enumerable.Range(0, budget / 3 / 20).Select(i => $"- fact {i:00000} ok")));
        }, (_, _) => Task.FromResult<int?>(null));
        Assert.Contains(prompts, p => p.StartsWith("Combine these notes"));
        Assert.StartsWith("You are an expert note-taker", prompts[^1]);
    }

    [Fact]
    public void Wants_summary_needs_transcript_and_ai()
    {
        using var dir = new TempDir();
        var cfg = CfgFor(dir);
        Assert.True(Summarize.WantsSummary(new Meeting("1") { Transcript = Lines(40) }, cfg));
        Assert.False(Summarize.WantsSummary(new Meeting("1") { Transcript = "hi" }, cfg));
        cfg.SummaryEnabled = false;
        Assert.False(Summarize.WantsSummary(new Meeting("1") { Transcript = Lines(40) }, cfg));
    }

    [Fact]
    public async Task Pipeline_uses_our_summary_and_sorts_on_it()
    {
        using var dir = new TempDir();
        var cfg = CfgFor(dir);
        using var store = new Store(cfg.DbPath, cfg.PoolDir);
        string? seen = null;
        var p = new Pipeline(cfg, store, (_, prompt, _) =>
        {
            seen = prompt;
            return Task.FromResult("""{"class_name": "Calc 1", "confidence": 0.9, "lecture_title": "Derivatives", "topics": ["derivatives"]}""");
        }, Notes("## Overview\nOur own notes on derivatives."), Quiet);
        store.Enqueue(new Meeting("m1") { Title = "Lecture", Owner = "Sam", NotesMarkdown = "The notes it came with", Transcript = Lines(40) });
        Assert.Equal(1, await p.RunPendingAsync());
        var row = store.Get("m1")!;
        Assert.StartsWith("## Overview", row.SummaryMd);
        Assert.Equal("big:35b", row.SummaryModel);
        Assert.Equal("Calc 1", row.ClassName);
        Assert.Contains("Our own notes", seen);
        string text = Py.ReadText(row.MdPath!);
        Assert.Contains("### Overview", text);
        Assert.Contains("Written by big:35b", text);
        Assert.DoesNotContain("The notes it came with", text); // our notes replace them
        Assert.Contains("## Transcript", text);
    }

    [Fact]
    public async Task Pipeline_keeps_the_notes_a_lecture_came_with_when_the_model_fails()
    {
        using var dir = new TempDir();
        var cfg = CfgFor(dir);
        cfg.Classes = [];
        using var store = new Store(cfg.DbPath, cfg.PoolDir);
        var p = new Pipeline(cfg, store, summarize: (_, _) => throw new InvalidOperationException("model 'big:35b' not found"), log: Quiet);
        store.Enqueue(new Meeting("m2") { Title = "Lecture", NotesMarkdown = "Notes typed in class", Transcript = Lines(40) });
        await p.RunPendingAsync();
        var row = store.Get("m2")!;
        Assert.Equal(("done", Configs.Unsorted, (string?)null), (row.Status, row.ClassName, row.SummaryMd));
        Assert.Contains("not found", row.Error);
        Assert.Contains("Notes typed in class", Py.ReadText(row.MdPath!));
    }

    [Fact]
    public async Task Pipeline_marks_failed_instead_of_looping()
    {
        using var dir = new TempDir();
        var cfg = CfgFor(dir);
        cfg.OllamaEnabled = false;
        using var store = new Store(cfg.DbPath, cfg.PoolDir);
        var p = new Pipeline(cfg, store, log: Quiet);
        store.Enqueue(new Meeting("m3") { Title = "Lecture" });
        using (var conn = new SqliteConnection($"Data Source={cfg.DbPath};Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE notes SET payload_json='not json' WHERE id='m3'";
            cmd.ExecuteNonQuery();
        }
        Assert.Equal(0, await p.RunPendingAsync());
        Assert.Equal("failed", store.Get("m3")!.Status);
        Assert.Equal(["m3"], store.Processing().Select(r => r.Id));
    }

    [Fact]
    public void Restart_resumes_interrupted_work()
    {
        using var dir = new TempDir();
        var cfg = CfgFor(dir);
        using var store = new Store(cfg.DbPath, cfg.PoolDir);
        store.Enqueue(new Meeting("m4") { Title = "Lecture" });
        Assert.Equal("m4", store.ClaimNext()!.Id);
        Assert.Null(store.ClaimNext());
        Assert.Equal(1, store.ResetWorking());
        Assert.Equal("queued", store.Get("m4")!.Status);
    }

    [Fact]
    public async Task Database_from_0_1_is_upgraded_and_readable()
    {
        // Libraries created by 0.1 have no payload column: they must open, list, and be processed again.
        using var dir = new TempDir();
        var cfg = CfgFor(dir);
        cfg.OllamaEnabled = false;
        Directory.CreateDirectory(cfg.Home);
        Directory.CreateDirectory(Path.Combine(cfg.PoolDir, "Bio 110"));
        string md = Path.Combine(cfg.PoolDir, "Bio 110", "2026-09-01 Cells.md");
        File.WriteAllText(md, "---\ntitle: \"Cells\"\n---\n\n# Cells\n\n## Notes\n\nmembranes\n\n## Transcript\n\nosmosis talk\n");
        using (var old = new SqliteConnection($"Data Source={cfg.DbPath};Pooling=False"))
        {
            old.Open();
            using var cmd = old.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE notes (id TEXT PRIMARY KEY, title TEXT, date TEXT, owner TEXT, attendees TEXT,
                    folder TEXT, class_name TEXT, confidence REAL, classified_by TEXT, lecture_title TEXT, topics TEXT,
                    md_path TEXT, has_transcript INTEGER DEFAULT 0, raw_json TEXT, first_seen TEXT, updated_at TEXT);
                CREATE TABLE sync_state (key TEXT PRIMARY KEY, value TEXT);
                INSERT INTO notes VALUES ('n1','Cells','2026-09-01','Sam','[]','','Bio 110',1,'human','Cells','[]',$md,1,
                    '{"id": "n1", "summary": "membranes"}','2026-09-01','2026-09-01');
                """;
            cmd.Parameters.AddWithValue("$md", md);
            cmd.ExecuteNonQuery();
        }
        using var store = new Store(cfg.DbPath, cfg.PoolDir);
        Assert.Equal(["n1"], store.ListNotes().Select(r => r.Id));
        var m = store.Meeting(store.Get("n1")!);
        Assert.Equal(("Sam", "membranes", "osmosis talk"), (m.Owner, m.NotesMarkdown, m.Transcript));
        Assert.Equal(1, store.RequeueAll());
        await new Pipeline(cfg, store, log: Quiet).RunPendingAsync();
        var row = store.Get("n1")!;
        Assert.Equal("done", row.Status);
        Assert.False(string.IsNullOrEmpty(row.PayloadJson));
        Assert.Contains("osmosis talk", Py.ReadText(row.MdPath!));
        Assert.Equal(("Bio 110", "human"), (row.ClassName, row.ClassifiedBy)); // a person's filing survives
    }

    [Fact]
    public async Task Rules_pick_the_class_but_the_model_still_names_the_lecture()
    {
        using var dir = new TempDir();
        var cfg = CfgFor(dir);
        using var store = new Store(cfg.DbPath, cfg.PoolDir);
        var p = new Pipeline(cfg, store, Sort("""{"class_name": "Bio 110", "confidence": 0.9, "lecture_title": "Limits and continuity", "topics": ["limits"]}"""),
            Notes(""), Quiet);
        store.Enqueue(new Meeting("r1") { Title = "Math 151 lecture 2", NotesMarkdown = "limits" });
        await p.RunPendingAsync();
        var row = store.Get("r1")!;
        Assert.Equal(("Calc 1", "rules"), (row.ClassName, row.ClassifiedBy)); // the rule still decides the class
        Assert.Equal("Limits and continuity", row.LectureTitle);
        Assert.Equal("[\"limits\"]", row.Topics);
    }

    /// <summary>A pipeline whose summarizer runs `during(store)` mid-summary, like a person clicking in the web page.</summary>
    static (Store, Pipeline) RacingPipeline(TempDir dir, Action<Store> during)
    {
        var cfg = CfgFor(dir);
        cfg.Classes = [new ClassDef("Calc 1"), new ClassDef("Bio 110")];
        var store = new Store(cfg.DbPath, cfg.PoolDir);
        var p = new Pipeline(cfg, store, Sort("""{"class_name": "Calc 1", "confidence": 0.9, "lecture_title": "t", "topics": []}"""),
            (_, _) =>
            {
                during(store);
                return Task.FromResult("## Overview\nnew summary");
            }, Quiet);
        store.Enqueue(new Meeting("r") { Title = "Lecture", NotesMarkdown = "old", Transcript = Lines(40) });
        return (store, p);
    }

    [Fact]
    public async Task Delete_while_summarizing_is_not_undone()
    {
        using var dir = new TempDir();
        var (store, p) = RacingPipeline(dir, st => st.Delete("r"));
        using (store)
        {
            await p.RunPendingAsync();
            Assert.Null(store.Get("r"));
            Assert.Empty(Directory.GetFiles(dir["pool"], "*.md", SearchOption.AllDirectories));
        }
    }

    [Fact]
    public async Task Move_while_summarizing_keeps_the_persons_class_and_the_summary()
    {
        using var dir = new TempDir();
        var (store, p) = RacingPipeline(dir, st => st.SetClass("r", "Bio 110"));
        using (store)
        {
            await p.RunPendingAsync();
            var row = store.Get("r")!;
            Assert.Equal(("Bio 110", "human"), (row.ClassName, row.ClassifiedBy));
            Assert.Equal(("## Overview\nnew summary", "done"), (row.SummaryMd, row.Status));
        }
    }

    [Fact]
    public async Task Reshare_while_summarizing_runs_again_with_the_new_copy()
    {
        using var dir = new TempDir();
        bool shared = false;
        var (store, p) = RacingPipeline(dir, st =>
        {
            if (shared) return;
            shared = true; // the lecture is edited and sent again, mid-summary
            st.Enqueue(new Meeting("r") { Title = "Lecture (edited)", NotesMarkdown = "newer", Transcript = Lines(40) });
        });
        using (store)
        {
            Assert.Equal(2, await p.RunPendingAsync()); // the stale result is dropped, the new copy is processed
            var row = store.Get("r")!;
            Assert.Equal(("Lecture (edited)", "done"), (row.Title, row.Status));
        }
    }

    [Fact]
    public async Task Notes_are_capped_and_a_runaway_model_is_caught()
    {
        // A small model once looped for 39,000 tokens on a short transcript (Ollama shifts its context and keeps
        // going). Notes are capped at MaxNotesTokens, and hitting the cap or repeating lines fails the summary, so
        // the lecture keeps its transcript (and any notes it came with) instead of the loop.
        var cfg = new Config(".", ".");
        string reason = "stop";
        var ollama = new FakeOllama((_, _) => new JsonObject
        {
            ["message"] = new JsonObject { ["content"] = "## Overview\nfine" }, ["done_reason"] = reason,
        });
        Assert.Equal("## Overview\nfine", await Summarize.OllamaGenerateAsync(cfg, "llama3.2:3b", "notes please", 32768, ollama.Client()));
        var options = ollama.Sent[0].Body["options"]!;
        Assert.Equal(Summarize.MaxNotesTokens, options["num_predict"]!.GetValue<int>());
        Assert.True(options["repeat_penalty"]!.GetValue<double>() > 1);
        Assert.Equal("/api/chat", ollama.Sent[0].Path);
        Assert.Equal(1024, await Predict(2048)); // a small context: half of it

        reason = "length";
        var e = await Assert.ThrowsAsync<RunawayOutputException>(
            () => Summarize.OllamaGenerateAsync(cfg, "llama3.2:3b", "notes please", 32768, ollama.Client()));
        Assert.Contains("kept writing", e.Message);
        string loop = "## Key concepts\n" + string.Concat(Enumerable.Repeat("- **Osmosis** is water moving across a membrane.\n", 8));
        Assert.Contains("repeated itself", Assert.Throws<RunawayOutputException>(() => Summarize.CleanOutput(loop)).Message);
        Assert.StartsWith("## Overview", Summarize.CleanOutput("## Overview\nOne.\n\n## Key concepts\n- **A** is a.\n- **B** is b."));
        Assert.False(Summarize.WantsSummary(new Meeting("1") { Transcript = string.Concat(Enumerable.Repeat("Short lecture. ", 50)) }, cfg)); // 750 chars

        async Task<int> Predict(int ctx)
        {
            reason = "stop";
            ollama.Sent.Clear();
            await Summarize.OllamaGenerateAsync(cfg, "m", "p", ctx, ollama.Client());
            return ollama.Sent[0].Body["options"]!["num_predict"]!.GetValue<int>();
        }
    }

    [Fact]
    public async Task Ollama_saying_no_reads_as_a_sentence()
    {
        var cfg = new Config(".", ".");
        var ollama = new FakeOllama((_, _) => (System.Net.HttpStatusCode.NotFound, """{"error":"model 'big:35b' not found"}"""));
        var e = await Assert.ThrowsAsync<HttpRequestException>(
            () => Summarize.OllamaGenerateAsync(cfg, "big:35b", "p", 8192, ollama.Client()));
        Assert.Equal("Ollama answered 404: model 'big:35b' not found", e.Message);
    }
}
