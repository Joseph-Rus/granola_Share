using System.Text.Json;
using System.Text.Json.Nodes;

namespace StudyStash.Core.Ai;

/// <summary>A rewrite couldn't do what was asked, with the HTTP status the library's endpoint should answer.</summary>
public sealed class RewriteRefusedException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// One rewrite job per lecture: "Rewrite notes with" runs in the background with the engine the student picked,
/// while the lecture's current notes stay exactly as they are. When it's ready the student keeps the old notes,
/// compares, or uses the new ones — <see cref="Use"/> is the only thing that actually changes the lecture. A ready
/// (or failed, or cancelled) job is written to <c>home/rewrites</c> as it happens, so it survives a restart; one
/// still working when the library stopped comes back failed, since nothing here can pick a run back up mid-way.
/// </summary>
public sealed class Rewrites(Config cfg, Store store, AiJobs ai, Action<string>? log = null)
{
    readonly Lock gate = new();
    readonly Dictionary<string, Job> jobs = LoadAll(cfg.Home);
    readonly Action<string> log = log ?? Console.WriteLine;

    sealed class Job
    {
        public required string Engine;
        public string State = "working"; // working | ready | failed | cancelled
        public string Started = "";
        public string Error = "";
        public int Done;
        public int Parts;
        public string? DraftMarkdown;
        /// <summary>What wrote the draft, as <see cref="AiJobs.DescribeChoice"/> put it ("Claude sonnet"): kept raw
        /// so <see cref="Use"/> can save it exactly as any other note's <c>summary_model</c> would be saved.</summary>
        public string DraftModel = "";
        public string DraftAt = "";
        /// <summary>Null once the job's finished — nothing left for <see cref="Cancel"/> to stop.</summary>
        public CancellationTokenSource? Cts;
    }

    /// <summary>The same shape as <see cref="Job"/>, minus what only matters while a job is running, plus the
    /// lecture id it's for (a file's name is a hash of that, not the id itself, so nothing about it needs to survive
    /// a round trip through a file name).</summary>
    sealed record Saved(string Id, string Engine, string State, string Started, string Error, int Done, int Parts,
        string? DraftMarkdown, string DraftModel, string DraftAt);

    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    static string Dir(string home) => Path.Combine(home, "rewrites");

    static string FileFor(string home, string id) =>
        Path.Combine(Dir(home), Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(id))) + ".json");

    static void Persist(string home, string id, Job job)
    {
        Directory.CreateDirectory(Dir(home));
        var saved = new Saved(id, job.Engine, job.State, job.Started, job.Error, job.Done, job.Parts,
            job.DraftMarkdown, job.DraftModel, job.DraftAt);
        Py.WriteText(FileFor(home, id), JsonSerializer.Serialize(saved, JsonOptions));
    }

    static void Forget(string home, string id)
    {
        string path = FileFor(home, id);
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>Every job the last run left behind. One still "working" never really finished, so it comes back
    /// failed instead of stuck forever looking busy.</summary>
    static Dictionary<string, Job> LoadAll(string home)
    {
        var result = new Dictionary<string, Job>();
        if (!Directory.Exists(Dir(home))) return result;
        foreach (string path in Directory.EnumerateFiles(Dir(home), "*.json"))
        {
            Saved? saved;
            try
            {
                saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(path), JsonOptions);
            }
            catch (JsonException)
            {
                continue; // a broken file: treat that lecture as having no rewrite job
            }
            if (saved is null) continue;
            var job = new Job
            {
                Engine = saved.Engine, State = saved.State, Started = saved.Started, Error = saved.Error,
                Done = saved.Done, Parts = saved.Parts, DraftMarkdown = saved.DraftMarkdown,
                DraftModel = saved.DraftModel, DraftAt = saved.DraftAt,
            };
            if (job.State == "working")
            {
                job.State = "failed";
                job.Error = "The library restarted before the new notes were ready.";
                Persist(home, saved.Id, job);
            }
            result[saved.Id] = job;
        }
        return result;
    }

    bool Installed(string engine) =>
        engine == "ollama" ? ai.Checks.OllamaInstalled() : ai.Checks.Which(AiProviders.Get(engine).Binary) is not null;

    static List<string> JsonTopics(string? json) =>
        (JsonNode.Parse(string.IsNullOrEmpty(json) ? "[]" : json) as JsonArray ?? []).Select(Py.Str).ToList();

    RewriteInfo Build(string id, NoteRow row, Job? job)
    {
        var current = new NotesVersion(row.SummaryMd ?? "", Engines.WhoWrote(row.SummaryModel ?? ""), row.UpdatedAt ?? "");
        if (job is null) return new RewriteInfo(id, "none") { Current = current };
        return new RewriteInfo(id, job.State)
        {
            Engine = job.Engine, EngineName = Engines.Name(job.Engine), Started = job.Started, Error = job.Error,
            Done = job.Done, Parts = job.Parts, Current = current,
            Draft = job.State == "ready" && job.DraftMarkdown is { } md
                ? new NotesVersion(md, Engines.WhoWrote(job.DraftModel), job.DraftAt) : null,
        };
    }

    NoteRow Require(string id) => store.Get(id) ?? throw new RewriteRefusedException(404, "there's no lecture with that id.");

    /// <summary>Where things stand: <c>none</c> when nobody's ever asked for a rewrite (or it was kept/used away).</summary>
    public RewriteInfo Get(string id)
    {
        var row = Require(id);
        lock (gate) return Build(id, row, jobs.GetValueOrDefault(id));
    }

    /// <summary>Start rewriting, with the old notes staying put until the student chooses. Refuses (409) when one's
    /// already running, the transcript's too short to write from, the pipeline is still writing this lecture's
    /// first notes, or the engine isn't installed; 404 for an unknown lecture.</summary>
    public RewriteInfo Start(string id, string engine)
    {
        var row = Require(id);
        lock (gate)
        {
            if (jobs.TryGetValue(id, out var running) && running.State == "working")
                throw new RewriteRefusedException(409, "a rewrite is already running for this lecture.");
            var m = store.Meeting(row);
            if (Py.Strip(m.Transcript).Length < Summarize.MinTranscriptChars)
                throw new RewriteRefusedException(409, "this lecture doesn't have enough of a transcript to rewrite from.");
            if (row.Status is Store.Queued or Store.Working)
                throw new RewriteRefusedException(409, "the library is still writing this lecture's notes.");
            if (!Installed(engine))
                throw new RewriteRefusedException(409, $"{Engines.Name(engine)} isn't installed on your library's computer.");

            var cts = new CancellationTokenSource();
            var job = new Job { Engine = engine, Started = ai.Checks.Now().ToString("o"), Cts = cts };
            jobs[id] = job;
            Persist(cfg.Home, id, job);
            _ = RunAsync(id, m, job, cts.Token);
            return Build(id, row, job);
        }
    }

    async Task RunAsync(string id, Meeting m, Job job, CancellationToken ct)
    {
        string draft = "";
        Exception? failure = null;
        try
        {
            draft = await ai.WriteNotesAsync(m, cfg, job.Engine, (done, parts) =>
            {
                lock (gate) { job.Done = done; job.Parts = parts; }
            }, ct);
        }
        catch (Exception e)
        {
            // A background job never gets another chance to answer: whatever went wrong (the engine, a bad
            // response, cancellation), it ends this job as failed or cancelled rather than faulting unseen.
            failure = e;
        }
        lock (gate)
        {
            // Keep or a fresh Start already moved this lecture on; this run's result no longer belongs anywhere.
            if (!jobs.TryGetValue(id, out var tracked) || !ReferenceEquals(tracked, job) || job.State == "cancelled")
                return;
            if (failure is OperationCanceledException)
            {
                job.State = "cancelled";
            }
            else if (failure is not null)
            {
                job.State = "failed";
                job.Error = failure.Message;
                log($"[rewrite] {Engines.Name(job.Engine)} couldn't rewrite '{m.Title}': {failure.Message}");
                ai.Record(job.Engine, false, failure.Message);
            }
            else
            {
                job.State = "ready";
                job.DraftMarkdown = draft;
                job.DraftModel = ai.DescribeChoice(job.Engine, cfg);
                job.DraftAt = ai.Checks.Now().ToString("o");
                ai.Record(job.Engine, true, "");
            }
            job.Cts = null;
            Persist(cfg.Home, id, job);
        }
    }

    /// <summary>Stop a running rewrite. Best-effort: the engine's token is cancelled, but the job is marked
    /// cancelled right away either way, so a real Ollama call with no way to stop mid-generation still ends the job
    /// here rather than leaving it looking busy forever.</summary>
    public RewriteInfo Cancel(string id)
    {
        var row = Require(id);
        lock (gate)
        {
            if (jobs.TryGetValue(id, out var job) && job.State == "working")
            {
                job.Cts?.Cancel();
                job.Cts = null;
                job.State = "cancelled";
                Persist(cfg.Home, id, job);
            }
            return Build(id, row, jobs.GetValueOrDefault(id));
        }
    }

    /// <summary>Drop the draft: the lecture's notes were never touched, so there's nothing else to undo.</summary>
    public RewriteInfo Keep(string id)
    {
        var row = Require(id);
        lock (gate)
        {
            if (jobs.Remove(id, out var job))
            {
                job.Cts?.Cancel();
                Forget(cfg.Home, id);
            }
            return Build(id, row, null);
        }
    }

    /// <summary>Save the ready draft as the lecture's notes. Refuses (409) with no ready draft, or while the
    /// pipeline has this lecture queued or working right now — checked here, not just at <see cref="Start"/>, since
    /// the two can race on the same row.</summary>
    public RewriteInfo Use(string id)
    {
        var row = Require(id);
        lock (gate)
        {
            if (!jobs.TryGetValue(id, out var job) || job.State != "ready" || job.DraftMarkdown is not { } draft)
                throw new RewriteRefusedException(409, "there are no new notes ready to use yet.");
            if (row.Status is Store.Queued or Store.Working)
                throw new RewriteRefusedException(409, "the library is writing this lecture's notes right now.");
            var m = store.Meeting(row);
            var c = new Classification(row.ClassName ?? "", row.Confidence ?? 1, row.ClassifiedBy ?? "human",
                row.LectureTitle ?? "", JsonTopics(row.Topics));
            store.Save(m, c, draft, job.DraftModel);
            jobs.Remove(id);
            Forget(cfg.Home, id);
            return Build(id, store.Get(id) ?? row, null);
        }
    }
}
