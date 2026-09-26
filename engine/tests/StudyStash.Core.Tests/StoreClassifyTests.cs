using System.Text.Json;
using System.Text.Json.Nodes;

namespace StudyStash.Core.Tests;

/// <summary>tests/test_store_classify.py, plus the note files and prompts compared with Python's own.</summary>
public class StoreClassifyTests
{
    static Config CfgFor(TempDir dir) => new(dir["home"], dir["pool"])
    {
        Classes =
        [
            new ClassDef("CS 101", ["cs101", "intro programming"], "Intro to programming"),
            new ClassDef("Bio 110", ["bio110", "biology"]),
        ],
    };

    static List<ClassDef> GoldenClasses() => Golden.Cases("classes")
        .Select(c => new ClassDef(c![0].S(), c[1]!.AsArray().Select(a => a.S()).ToList(), c[2].S())).ToList();

    static Meeting GoldenMeeting() => Granola.MeetingFromJson(Golden.Case("meeting"));

    static SortChatFn Answer(string json) => (_, _, _) => Task.FromResult(json);

    [Fact]
    public void Slugify_strips_bad_chars()
    {
        Assert.Equal("Lec 3 Loops while", Notes.Slugify("Lec 3: \"Loops\" / while?"));
        foreach (var c in Golden.Cases("slugify"))
            Assert.Equal(c![2].S(), Notes.Slugify(c[0].S(), c[1]!.GetValue<int>()));
        // A date that isn't in the input is "today" when the golden file was made: the clock's, not the code's.
        foreach (var c in Golden.Cases("date_prefix"))
            if (c![0].S().Contains(c[1].S(), StringComparison.Ordinal)) Assert.Equal(c[1].S(), Notes.DatePrefix(c[0].S()));
        Assert.Equal(DateTime.UtcNow.ToString("yyyy-MM-dd"), Notes.DatePrefix(""));
    }

    [Fact]
    public void Rules_folder_then_title()
    {
        using var dir = new TempDir();
        var cfg = CfgFor(dir);
        var c = Classify.ByRules(new Meeting("1") { Title = "Random meeting", Folder = "Biology" }, cfg.Classes);
        Assert.Equal(("Bio 110", "folder"), (c!.ClassName, c.By));
        var c2 = Classify.ByRules(new Meeting("2") { Title = "CS101 lecture 4 - recursion" }, cfg.Classes);
        Assert.Equal(("CS 101", "rules"), (c2!.ClassName, c2.By));
        Assert.Null(Classify.ByRules(new Meeting("3") { Title = "Dentist" }, cfg.Classes));
    }

    [Fact]
    public void Rules_and_names_match_python()
    {
        foreach (var c in Golden.Cases("norm"))
            Assert.Equal(c![1].S(), Classify.Norm(c[0].S()));
        var classes = GoldenClasses();
        foreach (var c in Golden.Cases("rules"))
        {
            var got = Classify.ByRules(Granola.MeetingFromJson(c![0]), classes);
            if (c[1] is null)
            {
                Assert.Null(got);
                continue;
            }
            var want = c[1]!.AsArray();
            Assert.Equal((want[0].S(), want[1]!.GetValue<double>(), want[2].S(), want[3].S()),
                (got!.ClassName, got.Confidence, got.By, got.LectureTitle));
        }
    }

    [Fact]
    public async Task Ollama_path_with_fake_chat()
    {
        using var dir = new TempDir();
        var cfg = CfgFor(dir);
        var m = new Meeting("1") { Title = "Lecture", NotesMarkdown = "Today we covered mitosis and the cell cycle." };
        var c = await Classify.ClassifyAsync(m, cfg, (_, prompt, schema) =>
        {
            Assert.Contains("Bio 110", prompt);
            Assert.Contains("mitosis", prompt);
            Assert.Equal(Configs.Unsorted, schema["properties"]!["class_name"]!["enum"]!.AsArray()[^1].S());
            return Task.FromResult("""{"class_name": "Bio 110", "confidence": 0.9, "lecture_title": "Mitosis", "topics": ["mitosis", "cell cycle"]}""");
        });
        Assert.Equal(("Bio 110", "ollama", "Mitosis"), (c.ClassName, c.By, c.LectureTitle));
        var low = Answer("""{"class_name": "Bio 110", "confidence": 0.2, "lecture_title": "x", "topics": []}""");
        Assert.Equal(Configs.Unsorted, (await Classify.ClassifyAsync(m, cfg, low)).ClassName);
        var bad = Answer("not json");
        Assert.Null(await Classify.WithOllamaAsync(m, cfg.Classes, cfg, bad));
        Assert.Equal("none", (await Classify.ClassifyAsync(m, cfg, bad)).By);
    }

    [Fact]
    public async Task Odd_model_answers_fall_back_instead_of_failing()
    {
        using var dir = new TempDir();
        var cfg = CfgFor(dir);
        var m = new Meeting("1") { Title = "Lecture" };
        Assert.Null(await Classify.WithOllamaAsync(m, cfg.Classes, cfg, Answer("[1]")));
        Assert.Null(await Classify.WithOllamaAsync(m, cfg.Classes, cfg, Answer("""{"class_name": "Art 1", "confidence": 1}""")));
        Assert.Null(await Classify.WithOllamaAsync(m, cfg.Classes, cfg, (_, _, _) => throw new HttpRequestException("down")));
        var sloppy = await Classify.WithOllamaAsync(m, cfg.Classes, cfg,
            Answer("""{"class_name": "CS 101", "confidence": "0.8", "lecture_title": "", "topics": [1, "b", "c", "d", "e", "f", "g", "h", "i"]}"""));
        Assert.Equal(("CS 101", 0.8, "Lecture"), (sloppy!.ClassName, sloppy.Confidence, sloppy.LectureTitle));
        Assert.Equal(["1", "b", "c", "d", "e", "f", "g", "h"], sloppy.Topics);
        var tooSure = await Classify.WithOllamaAsync(m, cfg.Classes, cfg, Answer("""{"class_name": "CS 101", "confidence": 7}"""));
        Assert.Equal(1.0, tooSure!.Confidence);
    }

    [Fact]
    public void The_prompts_are_word_for_word_the_python_ones()
    {
        var prompts = Golden.Case("prompts");
        var m = GoldenMeeting();
        var classes = GoldenClasses();
        var bare = new Meeting("n") { Title = "T", Date = "2026-09-01" };
        Assert.Equal(prompts["sort"].S(), Classify.BuildPrompt(m, classes));
        Assert.Equal(prompts["sort_bare"].S(), Classify.BuildPrompt(bare, classes[..1]));
        Assert.Equal(prompts["whole"].S(), Summarize.WholePrompt(m, "TRANSCRIPT"));
        Assert.Equal(prompts["whole_bare"].S(), Summarize.WholePrompt(bare, "T"));
        Assert.Equal(prompts["part"].S(), Summarize.PartPrompt(m, "PART", 2, 3));
        Assert.Equal(prompts["condense"].S(), Summarize.CondensePrompt(m, ["a", "b"]));
        Assert.Equal(prompts["merge"].S(), Summarize.MergePrompt(m, ["a", "b", "c"]));
        Assert.True(JsonNode.DeepEquals(prompts["schema"], Classify.Schema(classes.Select(c => c.Name))));
    }

    [Fact]
    public void Note_files_match_python_byte_for_byte()
    {
        var m = GoldenMeeting();
        var c = new Classification("CS 101", 0.875, "ollama", "Recursion and trees", ["recursion", "trees", "base case"]);
        Assert.Equal(Golden.Text("note-notes.md"), Notes.Render(m, c));
        string summary = "## Overview\nWe met recursion.\n\n```\n## not a heading\n```\n\n#### Deep\n###### Six";
        Assert.Equal(Golden.Text("note-summary.md"), Notes.Render(m, c, summary, "big:35b", keepGranola: true));
        Assert.Equal(Golden.Text("note-bare.md"), Notes.Render(new Meeting("x") { Title = "Untitled" },
            new Classification("Unsorted", 0.125, "none")));
    }

    [Fact]
    public void Markdown_helpers_match_python()
    {
        foreach (var c in Golden.Cases("demote"))
            Assert.Equal(c![1].S(), Notes.DemoteHeadings(c[0].S()));
        foreach (var c in Golden.Cases("md_section"))
            Assert.Equal(c![2].S(), Notes.Section(c[0].S(), c[1].S()));
    }

    [Fact]
    public void Store_save_move_and_listing()
    {
        using var dir = new TempDir();
        var cfg = CfgFor(dir);
        using var store = new Store(cfg.DbPath, cfg.PoolDir);
        var m = new Meeting("abc123")
        {
            Title = "Lec 1: Intro", Date = "2026-09-10T09:00:00Z", Owner = "Alex", NotesMarkdown = "# Hello\nworld",
            Raw = new JsonObject { ["id"] = "abc123", ["title"] = "Lec 1: Intro", ["date"] = "2026-09-10T09:00:00Z", ["notes"] = "# Hello\nworld" },
        };
        string path = store.Save(m, new Classification("CS 101", 0.9, "ollama", "Intro", ["hello"]));
        Assert.True(File.Exists(path));
        Assert.Equal("CS 101", Path.GetFileName(Path.GetDirectoryName(path)));
        Assert.Equal("2026-09-10 Lec 1 Intro.md", Path.GetFileName(path));
        string text = Py.ReadText(path);
        Assert.StartsWith("---", text);
        Assert.Contains("## Notes", text);
        Assert.Contains("world", text);
        Assert.Equal(["abc123"], store.KnownIds());
        Assert.Equal([("CS 101", 1)], store.ClassesSummary());

        // saving again keeps one file
        store.Save(m, new Classification("CS 101", 0.9, "ollama", "Intro", ["hello"]));
        Assert.Single(Directory.GetFileSystemEntries(Path.Combine(cfg.PoolDir, "CS 101")));

        // a person moving it moves the file and removes the empty folder
        string? moved = store.SetClass("abc123", "Bio 110");
        Assert.Equal("Bio 110", Path.GetFileName(Path.GetDirectoryName(moved)));
        Assert.False(File.Exists(path));
        Assert.False(Directory.Exists(Path.Combine(cfg.PoolDir, "CS 101")));
        var row = store.Get("abc123")!;
        Assert.Equal(("Bio 110", "human"), (row.ClassName, row.ClassifiedBy));

        Assert.Null(store.GetState("last_sync"));
        store.SetState("last_sync", "2026-09-15T00:00:00+00:00");
        Assert.Equal("2026-09-15T00:00:00+00:00", store.GetState("last_sync"));
    }

    [Fact]
    public void Render_includes_transcript_and_private_notes()
    {
        string output = Notes.Render(new Meeting("1") { Title = "T", Date = "2026-01-01", NotesMarkdown = "n", PrivateNotes = "p", Transcript = "t" },
            new Classification("CS 101", 1.0, "folder"));
        Assert.Contains("## Private notes", output);
        Assert.Contains("## Transcript", output);
    }

    [Fact]
    public void Two_lectures_with_one_title_get_two_files()
    {
        using var dir = new TempDir();
        using var store = new Store(dir["state.db"], dir["pool"]);
        var c = new Classification("CS 101", 0.9, "rules");
        string a = store.Save(new Meeting("first-aaaaaa") { Title = "Lecture", Date = "2026-09-01" }, c);
        string b = store.Save(new Meeting("second-bbbbbb") { Title = "Lecture", Date = "2026-09-01" }, c);
        Assert.Equal("2026-09-01 Lecture.md", Path.GetFileName(a));
        Assert.Equal("2026-09-01 Lecture [bbbbbb].md", Path.GetFileName(b));
        Assert.True(File.Exists(a) && File.Exists(b));
    }

    [Fact]
    public void Search_lists_and_counts()
    {
        using var dir = new TempDir();
        using var store = new Store(dir["state.db"], dir["pool"]);
        store.Save(new Meeting("1") { Title = "Mitosis", Date = "2026-09-02" }, new Classification("Bio 110", 1, "rules", "", ["cells"]));
        store.Save(new Meeting("2") { Title = "Loops", Date = "2026-09-03" }, new Classification("CS 101", 1, "rules"));
        store.Save(new Meeting("3") { Title = "50%_off sale", Date = "2026-09-01" }, new Classification("CS 101", 1, "rules"));
        store.Enqueue(new Meeting("4") { Title = "Queued mitosis" });
        Assert.Equal(["2", "1", "3"], store.ListNotes().Select(r => r.Id));
        Assert.Equal(["2", "3"], store.ListNotes("CS 101").Select(r => r.Id));
        Assert.Equal(["2"], store.ListNotes(limit: 1).Select(r => r.Id));
        Assert.Equal(["1"], store.Search("  CELLS ").Select(r => r.Id));
        Assert.Equal(["3"], store.Search("%_").Select(r => r.Id)); // % and _ are text, not wildcards
        Assert.Equal(new Dictionary<string, int> { ["done"] = 3, ["queued"] = 1 }, store.StatusCounts());
        Assert.Equal(["4"], store.Processing().Select(r => r.Id));
        Assert.True(store.Delete("1"));
        Assert.False(store.Delete("1"));
        Assert.False(Directory.Exists(Path.Combine(dir["pool"], "Bio 110")));
    }

    [Fact]
    public void Renaming_a_class_only_by_capitals_keeps_the_file()
    {
        // On a Mac or Windows "bio 110" and "Bio 110" are one folder: the Python engine deleted the file it had just
        // written there. This one keeps it.
        using var dir = new TempDir();
        using var store = new Store(dir["state.db"], dir["pool"]);
        store.Save(new Meeting("1") { Title = "Cells", Date = "2026-09-02" }, new Classification("bio 110", 1, "rules"));
        string? moved = store.SetClass("1", "Bio 110");
        Assert.True(File.Exists(moved), "the note file is still there");
        Assert.Equal("Bio 110", store.Get("1")!.ClassName);
    }

    [Fact]
    public void The_json_columns_are_what_python_writes()
    {
        using var dir = new TempDir();
        using var store = new Store(dir["state.db"], dir["pool"]);
        var m = GoldenMeeting();
        store.Save(m, new Classification("CS 101", 0.9, "rules", "", ["é", "b"]));
        var row = store.Get(m.Id)!;
        var fromPython = Golden.Cases("from_dict")[^1]![1]!["ok"].S();
        Assert.Equal(fromPython, row.PayloadJson);
        Assert.Equal("[\"\\u00e9\", \"b\"]", row.Topics);
        Assert.Equal("[\"Sam\", \"Dr. Ada Lovelace\"]", row.Attendees);
        Assert.Equal(PyJson.Dumps(m.Raw), row.RawJson);
        Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\+00:00$", row.UpdatedAt);
        foreach (var c in Golden.Cases("like"))
            Assert.Equal(c![1].S(), typeof(Store).GetMethod("Like", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .Invoke(null, [c[0].S()]));
        Assert.Equal(JsonValueKind.Array, JsonDocument.Parse(row.Topics!).RootElement.ValueKind);
    }
}
