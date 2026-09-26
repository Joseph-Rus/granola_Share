using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace StudyStash.Core;

/// <summary>Which class a note belongs in, and who decided: folder | rules | ollama | none | human.</summary>
public sealed class Classification(string className, double confidence, string by, string lectureTitle = "",
    List<string>? topics = null)
{
    public string ClassName { get; set; } = className;
    public double Confidence { get; set; } = confidence;
    public string By { get; set; } = by;
    public string LectureTitle { get; set; } = lectureTitle;
    public List<string>? Topics { get; set; } = topics;
}

/// <summary>A row of the notes table. Every column can be empty: databases from 0.1 lack the newer ones' values.</summary>
public sealed record NoteRow
{
    public required string Id { get; init; }
    public string? Title { get; init; }
    public string? Date { get; init; }
    public string? Owner { get; init; }
    public string? Attendees { get; init; }
    public string? Folder { get; init; }
    public string? ClassName { get; init; }
    public double? Confidence { get; init; }
    public string? ClassifiedBy { get; init; }
    public string? LectureTitle { get; init; }
    public string? Topics { get; init; }
    public string? MdPath { get; init; }
    public long? HasTranscript { get; init; }
    public string? RawJson { get; init; }
    public string? FirstSeen { get; init; }
    public string? UpdatedAt { get; init; }
    public string? PayloadJson { get; init; }
    public string? SummaryMd { get; init; }
    public string? SummaryModel { get; init; }
    public string? Status { get; init; }
    public string? Error { get; init; }
}

/// <summary>How a note file looks: the Markdown the library writes for each lecture.</summary>
public static partial class Notes
{
    [GeneratedRegex("[\\\\/:*?\"<>|\\x00-\\x1f]")]
    private static partial Regex BadFileChars();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\A([0-9]{4}-[0-9]{2}-[0-9]{2})")]
    private static partial Regex IsoDay();

    [GeneratedRegex(@"\A#{1,5}\s")]
    private static partial Regex Heading();

    public static string Slugify(string? text, int maxLen = 80)
    {
        string t = Py.Strip(BadFileChars().Replace(text ?? "", " "));
        t = Spaces().Replace(t, " ");
        return Py.Strip(Py.Head(t.Length > 0 ? t : "untitled", maxLen));
    }

    /// <summary>The note's day for its file name, or today's (UTC) when its date has none.</summary>
    public static string DatePrefix(string? date)
    {
        var m = IsoDay().Match(date ?? "");
        return m.Success ? m.Groups[1].Value : DateTime.UtcNow.ToString("yyyy-MM-dd");
    }

    /// <summary>Push every Markdown heading down one level, so a summary nests under "## Summary".</summary>
    public static string DemoteHeadings(string text)
    {
        var output = new List<string>();
        bool fenced = false;
        foreach (string raw in Py.SplitLines(text))
        {
            string line = raw;
            if (Py.LStrip(line).StartsWith("```", StringComparison.Ordinal)) fenced = !fenced;
            if (!fenced && Heading().IsMatch(line)) line = "#" + line;
            output.Add(line);
        }
        return string.Join("\n", output);
    }

    public static string Render(Meeting m, Classification c, string summaryMd = "", string summaryModel = "")
    {
        var topicList = c.Topics ?? [];
        string topics = string.Join(", ", topicList);
        string lectureTitle = c.LectureTitle.Length > 0 ? c.LectureTitle : m.Title;
        bool summarized = Py.Strip(summaryMd).Length > 0;
        var lines = new List<string>
        {
            "---",
            $"title: {PyJson.Dumps(m.Title)}",
            $"lecture_title: {PyJson.Dumps(lectureTitle)}",
            $"class: {PyJson.Dumps(c.ClassName)}",
            $"date: {PyJson.Dumps(m.Date)}",
            $"source: {PyJson.Dumps(m.Owner)}",
            $"id: {PyJson.Dumps(m.Id)}",
            $"folder: {PyJson.Dumps(m.Folder)}",
            $"attendees: {PyJson.Dumps(m.Attendees)}",
            $"topics: {PyJson.Dumps(topicList)}",
            $"classified_by: {c.By} ({Py.FormatFixed(c.Confidence, 2)})",
            $"summary_by: {PyJson.Dumps(summarized ? summaryModel : "")}",
            "---",
            "",
            $"# {lectureTitle}",
            "",
            $"*{m.Date}*  \u00b7  class: **{c.ClassName}**" + (topics.Length > 0 ? $"  \u00b7  topics: {topics}" : ""),
            "",
        };
        if (summarized)
        {
            lines.AddRange(["## Summary", "", DemoteHeadings(Py.Strip(summaryMd)), "",
                $"_Written by {summaryModel} from the transcript._", ""]);
        }
        else
        {
            string notes = Py.Strip(m.NotesMarkdown);
            lines.AddRange(["## Notes", "", notes.Length > 0 ? notes : "_(no AI notes returned)_", ""]);
        }
        if (Py.Strip(m.PrivateNotes).Length > 0) lines.AddRange(["## Private notes", "", Py.Strip(m.PrivateNotes), ""]);
        if (Py.Strip(m.Transcript).Length > 0) lines.AddRange(["## Transcript", "", Py.Strip(m.Transcript), ""]);
        return string.Join("\n", lines);
    }

    /// <summary>The body of "## heading" in a note file Render wrote (how 0.1 rows are read back).</summary>
    public static string Section(string text, string heading)
    {
        var m = Regex.Match(text, "^## " + Regex.Escape(heading) + @"\n\n(.*?)(?=^## |\z)",
            RegexOptions.Singleline | RegexOptions.Multiline);
        return m.Success ? Py.Strip(m.Groups[1].Value) : "";
    }
}

/// <summary>
/// The SQLite index (state.db) and the Markdown folder tree. A shared lecture arrives with its transcript and is
/// queued; the pipeline writes notes from the transcript, sorts it into a class, and saves it here.
/// </summary>
public sealed class Store : IDisposable
{
    public const string Queued = "queued", Working = "working", Done = "done", Failed = "failed";

    // A note is in the library once it has a file, even while it is being written again.
    const string Filed = "md_path IS NOT NULL AND class_name IS NOT NULL";

    const string Schema = """

        CREATE TABLE IF NOT EXISTS notes (
            id TEXT PRIMARY KEY,
            title TEXT,
            date TEXT,
            owner TEXT,
            attendees TEXT,
            folder TEXT,
            class_name TEXT,
            confidence REAL,
            classified_by TEXT,
            lecture_title TEXT,
            topics TEXT,
            md_path TEXT,
            has_transcript INTEGER DEFAULT 0,
            raw_json TEXT,
            first_seen TEXT,
            updated_at TEXT
        );
        CREATE TABLE IF NOT EXISTS sync_state (key TEXT PRIMARY KEY, value TEXT);

        """;

    // Columns added after 0.1.0. Databases from older versions get them when opened.
    static readonly (string Name, string Decl)[] Migrations =
    [
        ("payload_json", "TEXT"), // the whole shared lecture, so it can be processed again
        ("summary_md", "TEXT"), // our notes, written from the transcript
        ("summary_model", "TEXT"),
        ("status", "TEXT DEFAULT 'done'"), // queued | working | done | failed
        ("error", "TEXT"),
    ];

    public string DbPath { get; }
    public string PoolDir { get; }

    // The web server and the pipeline share one connection: one caller at a time. (C#'s lock is re-entrant.)
    readonly Lock gate = new();
    readonly SqliteConnection conn;

    public Store(string dbPath, string poolDir)
    {
        DbPath = Py.NormPath(dbPath);
        PoolDir = Py.NormPath(poolDir);
        Directory.CreateDirectory(Py.Parent(DbPath));
        Directory.CreateDirectory(PoolDir);
        conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Pooling = false }.ToString());
        conn.Open();
        Exec(Schema);
        Migrate();
        HasPassages = CreatePassages();
    }

    public void Dispose() => conn.Dispose();

    void Migrate()
    {
        var cols = new HashSet<string>();
        using (var cmd = Command("PRAGMA table_info(notes)"))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) cols.Add(r.GetString(r.GetOrdinal("name")));
        foreach (var (name, decl) in Migrations)
            if (!cols.Contains(name)) Exec($"ALTER TABLE notes ADD COLUMN {name} {decl}");
        Exec("CREATE INDEX IF NOT EXISTS notes_status ON notes(status)");
    }

    // --- SQL helpers: "?" placeholders, as in the Python engine ------------------------------------------------

    SqliteCommand Command(string sql, params object?[] args)
    {
        var cmd = conn.CreateCommand();
        var text = new System.Text.StringBuilder();
        int n = 0;
        foreach (char ch in sql)
        {
            if (ch != '?')
            {
                text.Append(ch);
                continue;
            }
            text.Append("$p").Append(n);
            cmd.Parameters.AddWithValue("$p" + n, args[n] ?? DBNull.Value);
            n++;
        }
        cmd.CommandText = text.ToString();
        return cmd;
    }

    int Exec(string sql, params object?[] args)
    {
        using var cmd = Command(sql, args);
        return cmd.ExecuteNonQuery();
    }

    List<NoteRow> Rows(string sql, params object?[] args)
    {
        using var cmd = Command(sql, args);
        using var r = cmd.ExecuteReader();
        var ordinals = Enumerable.Range(0, r.FieldCount).ToDictionary(r.GetName, i => i);
        string? S(string col) => ordinals.TryGetValue(col, out int i) && !r.IsDBNull(i) ? Convert.ToString(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture) : null;
        double? F(string col) => ordinals.TryGetValue(col, out int i) && !r.IsDBNull(i) ? Convert.ToDouble(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture) : null;
        long? L(string col) => ordinals.TryGetValue(col, out int i) && !r.IsDBNull(i) ? Convert.ToInt64(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture) : null;
        var rows = new List<NoteRow>();
        while (r.Read())
        {
            rows.Add(new NoteRow
            {
                Id = S("id") ?? "",
                Title = S("title"), Date = S("date"), Owner = S("owner"), Attendees = S("attendees"), Folder = S("folder"),
                ClassName = S("class_name"), Confidence = F("confidence"), ClassifiedBy = S("classified_by"),
                LectureTitle = S("lecture_title"), Topics = S("topics"), MdPath = S("md_path"),
                HasTranscript = L("has_transcript"), RawJson = S("raw_json"), FirstSeen = S("first_seen"),
                UpdatedAt = S("updated_at"), PayloadJson = S("payload_json"), SummaryMd = S("summary_md"),
                SummaryModel = S("summary_model"), Status = S("status"), Error = S("error"),
            });
        }
        return rows;
    }

    /// <summary>datetime.now(timezone.utc).isoformat(timespec="seconds").</summary>
    static string Now() => DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'+00:00'", System.Globalization.CultureInfo.InvariantCulture);

    static string Like(string q) => "%" + q.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";

    static List<string> JsonList(string? json) =>
        (JsonNode.Parse(json is null or "" ? "[]" : json) as JsonArray ?? new JsonArray()).Select(Py.Str).ToList();

    // --- sync state ----------------------------------------------------------------------------------------------

    public string? GetState(string key)
    {
        lock (gate)
        {
            using var cmd = Command("SELECT value FROM sync_state WHERE key=?", key);
            return cmd.ExecuteScalar() is object v and not DBNull ? Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) : null;
        }
    }

    public void SetState(string key, string value)
    {
        lock (gate)
            Exec("INSERT INTO sync_state(key,value) VALUES(?,?) ON CONFLICT(key) DO UPDATE SET value=excluded.value", key, value);
    }

    // --- the processing queue ------------------------------------------------------------------------------------

    /// <summary>Accept a shared lecture. The pipeline picks it up and files it.</summary>
    public void Enqueue(Meeting m)
    {
        lock (gate)
        {
            string now = Now();
            Exec("""
                INSERT INTO notes(id,title,date,owner,attendees,folder,has_transcript,raw_json,payload_json,
                    status,error,first_seen,updated_at)
                   VALUES(?,?,?,?,?,?,?,?,?,?,NULL,?,?)
                   ON CONFLICT(id) DO UPDATE SET title=excluded.title, date=excluded.date, owner=excluded.owner,
                    attendees=excluded.attendees, folder=excluded.folder, has_transcript=excluded.has_transcript,
                    raw_json=excluded.raw_json, payload_json=excluded.payload_json, status=excluded.status,
                    error=NULL, updated_at=excluded.updated_at
                """,
                m.Id, m.Title, m.Date, m.Owner, PyJson.Dumps(m.Attendees), m.Folder,
                Py.Strip(m.Transcript).Length > 0 ? 1L : 0L, Py.Head(PyJson.Dumps(m.Raw), 200_000),
                Wire.MeetingJson(m), Queued, now, now);
        }
    }

    public NoteRow? ClaimNext()
    {
        lock (gate)
        {
            string? id;
            using (var cmd = Command("SELECT id FROM notes WHERE status=? ORDER BY updated_at, first_seen LIMIT 1", Queued))
                id = cmd.ExecuteScalar() as string;
            if (id is null) return null;
            Exec("UPDATE notes SET status=? WHERE id=?", Working, id);
            return Get(id);
        }
    }

    /// <summary>After a restart, anything that was mid-processing goes back in the queue.</summary>
    public int ResetWorking()
    {
        lock (gate) return Exec("UPDATE notes SET status=? WHERE status=?", Queued, Working);
    }

    public bool Requeue(string noteId)
    {
        lock (gate) return Exec("UPDATE notes SET status=?, updated_at=? WHERE id=?", Queued, Now(), noteId) > 0;
    }

    public int RequeueAll()
    {
        lock (gate) return Exec("UPDATE notes SET status=?, updated_at=? WHERE status IN (?, ?)", Queued, Now(), Done, Failed);
    }

    public void MarkFailed(string noteId, string error)
    {
        // Only if still ours: a re-share or re-queue meanwhile means it runs again instead.
        lock (gate)
            Exec("UPDATE notes SET status=?, error=?, updated_at=? WHERE id=? AND status=?",
                Failed, Py.Head(error, 2000), Now(), noteId, Working);
    }

    /// <summary>
    /// Save the pipeline's result for a row it claimed, unless the note changed meanwhile. The pipeline works for
    /// minutes on a snapshot: if the note was deleted, re-shared, or re-queued since, the result is stale and is
    /// dropped (a re-queued note simply runs again). If a person moved the note meanwhile, their class wins.
    /// </summary>
    public string? Finish(NoteRow claimed, Meeting m, Classification c, string summaryMd = "", string summaryModel = "",
        string error = "")
    {
        lock (gate)
        {
            var row = Get(claimed.Id);
            if (row is null || row.Status != Working || row.UpdatedAt != claimed.UpdatedAt) return null;
            if (row.ClassifiedBy == "human" && !string.IsNullOrEmpty(row.ClassName))
                c = new Classification(row.ClassName, 1.0, "human", c.LectureTitle, c.Topics);
            return Save(m, c, summaryMd, summaryModel, error);
        }
    }

    public Dictionary<string, int> StatusCounts()
    {
        lock (gate)
        {
            var counts = new Dictionary<string, int>();
            using var cmd = Command("SELECT status, COUNT(*) AS n FROM notes GROUP BY status");
            using var r = cmd.ExecuteReader();
            while (r.Read()) counts[r.IsDBNull(0) || r.GetString(0) == "" ? Done : r.GetString(0)] = r.GetInt32(1);
            return counts;
        }
    }

    /// <summary>Notes not filed yet: waiting, being written right now, or stuck with an error.</summary>
    public List<NoteRow> Processing()
    {
        lock (gate) return Rows("SELECT * FROM notes WHERE status IN (?, ?, ?) ORDER BY updated_at", Working, Queued, Failed);
    }

    // --- notes -----------------------------------------------------------------------------------------------------

    public HashSet<string> KnownIds()
    {
        lock (gate)
        {
            var ids = new HashSet<string>();
            using var cmd = Command("SELECT id FROM notes");
            using var r = cmd.ExecuteReader();
            while (r.Read()) ids.Add(r.GetString(0));
            return ids;
        }
    }

    public string ClassDir(string? className)
    {
        lock (gate)
        {
            string d = Path.Combine(PoolDir, Notes.Slugify(string.IsNullOrEmpty(className) ? Configs.Unsorted : className, 60));
            Directory.CreateDirectory(d);
            return d;
        }
    }

    string TargetPath(Meeting m, string className)
    {
        string name = $"{Notes.DatePrefix(m.Date)} {Notes.Slugify(m.Title)}";
        string path = Path.Combine(ClassDir(className), name + ".md");
        if (File.Exists(path))
        {
            using var cmd = Command("SELECT id FROM notes WHERE md_path=?", path);
            if (cmd.ExecuteScalar() is string other && other != m.Id)
                path = Path.Combine(ClassDir(className), $"{name} [{Py.Tail(m.Id, 6)}].md");
        }
        return path;
    }

    /// <summary>Write the Markdown file and mark the note done.</summary>
    public string Save(Meeting m, Classification c, string summaryMd = "", string summaryModel = "", string error = "")
    {
        lock (gate)
        {
            string path = TargetPath(m, c.ClassName);
            Py.WriteText(path, Notes.Render(m, c, summaryMd, summaryModel));
            string now = Now();
            string? oldPath = null, firstSeen = null;
            bool known = false;
            using (var cmd = Command("SELECT md_path, first_seen FROM notes WHERE id=?", m.Id))
            using (var r = cmd.ExecuteReader())
            {
                if (r.Read())
                {
                    known = true;
                    oldPath = r.IsDBNull(0) ? null : r.GetString(0);
                    firstSeen = r.IsDBNull(1) ? null : r.GetString(1);
                }
            }
            // The Python engine compared spellings only; a class renamed "bio 110" → "Bio 110" on a Mac is the
            // same file, which that comparison would delete right after writing it.
            if (!string.IsNullOrEmpty(oldPath) && oldPath != path && File.Exists(oldPath) && !Py.SamePath(oldPath, path))
                File.Delete(oldPath);
            Exec("""
                INSERT INTO notes(id,title,date,owner,attendees,folder,class_name,confidence,classified_by,
                    lecture_title,topics,md_path,has_transcript,raw_json,payload_json,summary_md,summary_model,
                    status,error,first_seen,updated_at)
                   VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)
                   ON CONFLICT(id) DO UPDATE SET title=excluded.title, date=excluded.date, owner=excluded.owner,
                    attendees=excluded.attendees, folder=excluded.folder, class_name=excluded.class_name,
                    confidence=excluded.confidence, classified_by=excluded.classified_by,
                    lecture_title=excluded.lecture_title, topics=excluded.topics, md_path=excluded.md_path,
                    has_transcript=excluded.has_transcript, raw_json=excluded.raw_json,
                    payload_json=excluded.payload_json, summary_md=excluded.summary_md,
                    summary_model=excluded.summary_model, status=excluded.status, error=excluded.error,
                    updated_at=excluded.updated_at
                """,
                m.Id, m.Title, m.Date, m.Owner, PyJson.Dumps(m.Attendees), m.Folder, c.ClassName, c.Confidence,
                c.By, c.LectureTitle.Length > 0 ? c.LectureTitle : m.Title, PyJson.Dumps(c.Topics ?? []), path,
                Py.Strip(m.Transcript).Length > 0 ? 1L : 0L, Py.Head(PyJson.Dumps(m.Raw), 200_000), Wire.MeetingJson(m),
                summaryMd.Length > 0 ? summaryMd : null, summaryModel.Length > 0 ? summaryModel : null, Done,
                error.Length > 0 ? error : null, known ? firstSeen : now, now);
            Index(m.Id, summaryMd.Length > 0 ? summaryMd : m.NotesMarkdown, m.Transcript, now);
            return path;
        }
    }

    /// <summary>The whole shared lecture behind a row (rebuilt from its columns and Markdown file for rows from 0.1).</summary>
    public Meeting Meeting(NoteRow row)
    {
        lock (gate)
        {
            if (!string.IsNullOrEmpty(row.PayloadJson)) return Wire.MeetingFromJson(JsonNode.Parse(row.PayloadJson));
            var raw = JsonNode.Parse(string.IsNullOrEmpty(row.RawJson) ? "{}" : row.RawJson) as JsonObject ?? new JsonObject();
            var m = new Meeting(row.Id) { Raw = raw };
            if (!string.IsNullOrEmpty(row.Title)) m.Title = row.Title;
            if (!string.IsNullOrEmpty(row.Date)) m.Date = row.Date;
            if (!string.IsNullOrEmpty(row.Owner)) m.Owner = row.Owner;
            var attendees = JsonList(row.Attendees);
            if (attendees.Count > 0) m.Attendees = attendees;
            if (!string.IsNullOrEmpty(row.Folder)) m.Folder = row.Folder;
            if (!string.IsNullOrEmpty(row.MdPath) && File.Exists(row.MdPath))
            {
                string text = Py.ReadText(row.MdPath);
                m.NotesMarkdown = Notes.Section(text, "Notes");
                m.PrivateNotes = Notes.Section(text, "Private notes");
                m.Transcript = Notes.Section(text, "Transcript");
            }
            return m;
        }
    }

    // --- passages: search and Ask ---------------------------------------------------------------------------------

    /// <summary>False when this SQLite has no full-text search (FTS5): search then goes by title and notes only.</summary>
    public bool HasPassages { get; }

    bool CreatePassages()
    {
        try
        {
            Exec("CREATE VIRTUAL TABLE IF NOT EXISTS passages USING fts5(note_id UNINDEXED, kind UNINDEXED, section UNINDEXED, "
                + "start UNINDEXED, text, tokenize='porter unicode61')");
            Exec("CREATE TABLE IF NOT EXISTS passage_notes (note_id TEXT PRIMARY KEY, updated_at TEXT)");
            return true;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    void Index(string noteId, string notes, string transcript, string updatedAt)
    {
        if (!HasPassages) return;
        Unindex(noteId);
        foreach (var p in Passages.FromNotes(noteId, notes).Concat(Passages.FromTranscript(noteId, transcript)))
            Exec("INSERT INTO passages(note_id, kind, section, start, text) VALUES(?,?,?,?,?)", p.NoteId, p.Kind, p.Section, p.Start, p.Text);
        Exec("INSERT INTO passage_notes(note_id, updated_at) VALUES(?,?) ON CONFLICT(note_id) DO UPDATE SET updated_at=excluded.updated_at", noteId, updatedAt);
    }

    void Unindex(string noteId)
    {
        if (!HasPassages) return;
        Exec("DELETE FROM passages WHERE note_id=?", noteId);
        Exec("DELETE FROM passage_notes WHERE note_id=?", noteId);
    }

    /// <summary>Index the filed lectures the index hasn't seen, or has seen an older version of (lectures filed by the
    /// Python engine, or before this version). Returns how many.</summary>
    public int IndexMissing()
    {
        if (!HasPassages) return 0;
        lock (gate)
        {
            var stale = Rows($"SELECT n.* FROM notes n LEFT JOIN passage_notes p ON p.note_id = n.id WHERE {Filed} "
                + "AND (p.updated_at IS NULL OR p.updated_at != n.updated_at)");
            foreach (var row in stale)
            {
                var m = Meeting(row);
                Index(row.Id, string.IsNullOrEmpty(row.SummaryMd) ? m.NotesMarkdown : row.SummaryMd, m.Transcript, row.UpdatedAt ?? "");
            }
            return stale.Count;
        }
    }

    /// <summary>The best passages for an FTS5 query (see <see cref="Passages"/>), filed lectures only.</summary>
    public List<PassageHit> SearchPassages(string match, string? className = null, string? noteId = null, int limit = 20)
    {
        if (!HasPassages) return [];
        lock (gate)
        {
            string sql = "SELECT passages.note_id, passages.kind, passages.section, passages.start, passages.text, bm25(passages) AS rank "
                + $"FROM passages JOIN notes ON notes.id = passages.note_id WHERE passages MATCH ? AND {Filed}";
            var args = new List<object?> { match };
            if (className is not null)
            {
                sql += " AND notes.class_name=?";
                args.Add(className);
            }
            if (noteId is not null)
            {
                sql += " AND passages.note_id=?";
                args.Add(noteId);
            }
            sql += $" ORDER BY rank LIMIT {Math.Clamp(limit, 1, 200)}";
            var found = new List<(Passage, double)>();
            try
            {
                using var cmd = Command(sql, [.. args]);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    found.Add((new Passage(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? "" : r.GetString(2),
                        r.IsDBNull(3) ? null : r.GetDouble(3), r.GetString(4)), r.GetDouble(5)));
            }
            catch (SqliteException)
            {
                return []; // a query FTS5 can't read
            }
            var notes = new Dictionary<string, NoteRow?>();
            var hits = new List<PassageHit>();
            foreach (var (p, rank) in found)
            {
                if (!notes.TryGetValue(p.NoteId, out var n)) notes[p.NoteId] = n = Get(p.NoteId);
                if (n is not null) hits.Add(new PassageHit(n, p, rank));
            }
            return hits;
        }
    }

    /// <summary>A lecture's own passages, in order: what Ask reads for a question about one lecture.</summary>
    public List<Passage> PassagesOf(string noteId)
    {
        if (!HasPassages) return [];
        lock (gate)
        {
            var result = new List<Passage>();
            using var cmd = Command("SELECT note_id, kind, section, start, text FROM passages WHERE note_id=? ORDER BY rowid", noteId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                result.Add(new Passage(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? "" : r.GetString(2), r.IsDBNull(3) ? null : r.GetDouble(3), r.GetString(4)));
            return result;
        }
    }

    public NoteRow? Get(string noteId)
    {
        lock (gate) return Rows("SELECT * FROM notes WHERE id=?", noteId).FirstOrDefault();
    }

    public List<NoteRow> ListNotes(string? className = null, int? limit = null)
    {
        lock (gate)
        {
            string sql = $"SELECT * FROM notes WHERE {Filed}";
            var args = new List<object?>();
            if (className is not null)
            {
                sql += " AND class_name=?";
                args.Add(className);
            }
            sql += " ORDER BY date DESC, title";
            if (limit is > 0) sql += $" LIMIT {limit.Value}";
            return Rows(sql, [.. args]);
        }
    }

    public List<NoteRow> Search(string q, int limit = 200)
    {
        lock (gate)
        {
            string like = Like(Py.Strip(q));
            string[] fields = ["title", "lecture_title", "topics", "summary_md", "owner", "class_name", "payload_json"];
            string where = string.Join(" OR ", fields.Select(f => $"{f} LIKE ? ESCAPE '\\'"));
            return Rows($"SELECT * FROM notes WHERE {Filed} AND ({where}) ORDER BY date DESC LIMIT {limit}",
                [.. fields.Select(_ => (object?)like)]);
        }
    }

    public List<(string ClassName, int Count)> ClassesSummary()
    {
        lock (gate)
        {
            var result = new List<(string, int)>();
            using var cmd = Command($"SELECT class_name, COUNT(*) AS n FROM notes WHERE {Filed} GROUP BY class_name ORDER BY class_name");
            using var r = cmd.ExecuteReader();
            while (r.Read()) result.Add((r.GetString(0), r.GetInt32(1)));
            return result;
        }
    }

    public string? SetClass(string noteId, string className, string by = "human")
    {
        lock (gate)
        {
            var row = Get(noteId);
            if (row is null) return null;
            if (row.Status is Queued or Working)
            {
                // The pipeline writes the file when it's done and keeps this choice (see Finish).
                Exec("UPDATE notes SET class_name=?, classified_by=?, confidence=1.0 WHERE id=?", className, by, noteId);
                return string.IsNullOrEmpty(row.MdPath) ? null : row.MdPath;
            }
            string? old = string.IsNullOrEmpty(row.MdPath) ? null : row.MdPath;
            var m = Meeting(row);
            var c = new Classification(className, 1.0, by, row.LectureTitle ?? "", JsonList(row.Topics));
            string newPath = Save(m, c, row.SummaryMd ?? "", row.SummaryModel ?? "", row.Error ?? "");
            DropEmptyDir(old, newPath);
            return newPath;
        }
    }

    public bool Delete(string noteId)
    {
        lock (gate)
        {
            var row = Get(noteId);
            if (row is null) return false;
            string? path = string.IsNullOrEmpty(row.MdPath) ? null : row.MdPath;
            if (path is not null && File.Exists(path)) File.Delete(path);
            Exec("DELETE FROM notes WHERE id=?", noteId);
            Unindex(noteId);
            DropEmptyDir(path, null);
            return true;
        }
    }

    void DropEmptyDir(string? old, string? @new)
    {
        if (old is null) return;
        string dir = Py.Parent(old);
        if (@new is not null && Py.SamePath(dir, Py.Parent(@new))) return;
        if (!Directory.Exists(dir) || Py.SamePath(dir, PoolDir) || Directory.EnumerateFileSystemEntries(dir).Any()) return;
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
