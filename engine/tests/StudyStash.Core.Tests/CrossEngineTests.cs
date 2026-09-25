using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace StudyStash.Core.Tests;

/// <summary>
/// One library, two engines: the Python engine builds a library, the C# engine reads and changes it, and the
/// Python engine checks every row and file (engine/tests/crosscheck.py). This is what lets a computer switch
/// engines without touching its notes.
/// </summary>
public class CrossEngineTests(ITestOutputHelper output)
{
    static string? RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "granola_share")) && Directory.Exists(Path.Combine(d.FullName, "engine")))
                return d.FullName;
        return null;
    }

    /// <summary>STUDYSTASH_PYTHON, or the repo's own .venv. CI sets STUDYSTASH_REQUIRE_PYTHON so it can't be skipped.</summary>
    static string? Python(string root)
    {
        string? set = Environment.GetEnvironmentVariable("STUDYSTASH_PYTHON");
        if (!string.IsNullOrEmpty(set)) return set;
        string venv = OperatingSystem.IsWindows()
            ? Path.Combine(root, ".venv", "Scripts", "python.exe") : Path.Combine(root, ".venv", "bin", "python");
        return File.Exists(venv) ? venv : null;
    }

    string Run(string python, string script, string step, string dir)
    {
        var psi = new ProcessStartInfo(python) { RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add(script);
        psi.ArgumentList.Add(step);
        psi.ArgumentList.Add(dir);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        output.WriteLine(stdout.Result + stderr);
        Assert.True(p.ExitCode == 0, $"python {step} failed:\n{stderr}");
        return stdout.Result;
    }

    static JsonNode? Column(NoteRow row, string name) => name switch
    {
        "id" => row.Id, "title" => row.Title, "date" => row.Date, "owner" => row.Owner, "attendees" => row.Attendees,
        "folder" => row.Folder, "class_name" => row.ClassName, "confidence" => row.Confidence is double d ? d : null,
        "classified_by" => row.ClassifiedBy, "lecture_title" => row.LectureTitle, "topics" => row.Topics,
        "md_path" => row.MdPath, "has_transcript" => row.HasTranscript is long l ? l : null, "raw_json" => row.RawJson,
        "first_seen" => row.FirstSeen, "updated_at" => row.UpdatedAt, "payload_json" => row.PayloadJson,
        "summary_md" => row.SummaryMd, "summary_model" => row.SummaryModel, "status" => row.Status, "error" => row.Error,
        _ => throw new ArgumentException($"a column the C# engine doesn't know: {name}"),
    };

    [Fact]
    public async Task A_python_library_opens_changes_and_goes_back_to_python()
    {
        string? root = RepoRoot();
        string? python = root is null ? null : Python(root);
        if (python is null)
        {
            Assert.True(Environment.GetEnvironmentVariable("STUDYSTASH_REQUIRE_PYTHON") != "1", "no Python engine to check against");
            output.WriteLine("skipped: no .venv with the Python engine (set STUDYSTASH_PYTHON)");
            return;
        }
        string script = Path.Combine(root!, "engine", "tests", "crosscheck.py");
        using var dir = new TempDir();
        Run(python, script, "make", dir.Path);
        var made = (JsonObject)JsonNode.Parse(File.ReadAllText(dir["python.json"]))!;

        // Everything Python wrote reads back the same: rows, lectures, lists, counts.
        var cfg = Configs.Load(made["home"].S());
        Assert.Equal("maple-otter", cfg.PoolPassword);
        using (var store = new Store(cfg.DbPath, cfg.PoolDir))
        {
            foreach (var (id, want) in made["rows"]!.AsObject())
            {
                var row = store.Get(id)!;
                foreach (var (col, value) in want!.AsObject())
                    Assert.True(JsonNode.DeepEquals(value, Column(row, col)), $"{id}.{col}: {value?.ToJsonString()} vs {Column(row, col)?.ToJsonString()}");
                var meeting = JsonNode.Parse(Granola.MeetingJson(store.Meeting(row)));
                Assert.True(JsonNode.DeepEquals(made["meetings"]![id], meeting), $"{id}: the lecture reads differently");
            }
            Assert.Equal(made["list"]!.AsArray().Select(n => n.S()), store.ListNotes().Select(r => r.Id));
            Assert.Equal(made["search"]!.AsArray().Select(n => n.S()), store.Search("membranes").Select(r => r.Id));
            Assert.Equal(made["classes"]!.AsArray().Select(c => (c![0].S(), c[1]!.GetValue<int>())), store.ClassesSummary());
            Assert.Equal(made["counts"]!.AsObject().ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<int>()), store.StatusCounts());
            Assert.Equal(made["processing"]!.AsArray().Select(n => n.S()), store.Processing().Select(r => r.Id));
            Assert.Equal("2026-09-15T00:00:00+00:00", store.GetState("last_sync"));

            // Now the C# engine does a day's work in it.
            Assert.NotNull(store.SetClass("alpha-111111", "Chem 101"));
            Assert.True(store.Delete("delta-444444"));
            store.Enqueue(new Meeting("csharp-1")
            {
                Title = "Enzymes ☕ (from C#)", Date = "Sep 20, 2026 9:05 AM", Owner = "Bo", Attendees = ["Bo", "Zoë"],
                Folder = "Biology", NotesMarkdown = "# Enzymes\n- catalysts", Transcript = "Enzymes speed reactions up.",
                Raw = (JsonObject)JsonNode.Parse("""{"id": "csharp-1", "n": 2.50, "big": 12345678901234567890, "e": 1e-7}""")!,
            });
            store.Save(new Meeting("csharp-2") { Title = "Loops", Date = "2026-09-21", NotesMarkdown = "## For\n- range" },
                new Classification("CS 101", 0.875, "ollama", "For loops", ["loops", "é"]),
                "## Overview\nLoops.\n```\n# code\n```", "big:35b", keepGranola: true);
            store.SetState("csharp", "yes");
            int filed = await new Pipeline(cfg, store, log: _ => { }).RunPendingAsync();
            Assert.Equal(2, filed); // queued-1 and csharp-1
            File.WriteAllText(dir["csharp.json"], new JsonObject
            {
                ["keep_granola"] = new JsonArray("csharp-2"),
                ["filed"] = new JsonArray("queued-1", "csharp-1", "csharp-2"),
                ["columns"] = new JsonArray(Columns(cfg.DbPath).Select(c => (JsonNode?)c).ToArray()),
            }.ToJsonString());
        }

        Assert.Equal("ok", Run(python, script, "verify", dir.Path).Trim());
    }

    static List<string> Columns(string db)
    {
        using var conn = new SqliteConnection($"Data Source={db};Pooling=False;Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(notes)";
        using var r = cmd.ExecuteReader();
        var cols = new List<string>();
        while (r.Read()) cols.Add(r.GetString(1));
        return cols;
    }

    [Fact]
    public void Json_columns_parse_in_both_directions()
    {
        // Numbers Python keeps exactly (a 20-digit int) and floats it rewrites (2.50 → 2.5) stay valid JSON here too.
        var raw = (JsonObject)JsonNode.Parse("""{"big": 12345678901234567890, "f": 2.50}""")!;
        Assert.Equal("{\"big\": 12345678901234567890, \"f\": 2.5}", PyJson.Dumps(raw));
        Assert.Equal(JsonValueKind.Object, JsonDocument.Parse(PyJson.Dumps(raw)).RootElement.ValueKind);
    }
}
