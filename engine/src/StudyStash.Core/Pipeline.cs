using System.Diagnostics;
using System.Net.Sockets;

namespace StudyStash.Core;

/// <summary>The notes engine didn't answer at all (not running, or not reachable): the lecture waits for it instead of
/// being filed without notes.</summary>
public sealed class EngineOfflineException(string engine, Exception inner)
    : Exception($"{engine} isn't answering: {inner.Message}", inner)
{
    /// <summary>What writes the notes ("Ollama").</summary>
    public string Engine { get; } = engine;
}

/// <summary>
/// Turns queued lectures into filed ones: writes our notes from the transcript, sorts, saves. Runs in the
/// background of the library, so an upload from the laptop returns at once even when a big model takes minutes.
/// While the notes engine isn't answering, lectures stay in the queue and get their notes when it's back.
/// </summary>
public sealed class Pipeline(Config cfg, Store store, SortChatFn? chat = null,
    Func<Meeting, Config, Task<string>>? summarize = null, Action<string>? log = null, Func<string>? notesModel = null,
    Func<string>? notesEngine = null)
{
    readonly Func<Meeting, Config, Task<string>> summarize = summarize ?? ((m, c) => Summarize.SummarizeTranscriptAsync(m, c));
    readonly Action<string> log = log ?? Console.WriteLine;
    readonly SemaphoreSlim wake = new(0, 1);
    volatile string? current;
    volatile string? engineProblem;

    public Config Cfg { get; } = cfg;

    /// <summary>The id of the lecture being written right now.</summary>
    public string? Current => current;

    /// <summary>"Ollama isn't answering on your library. New lectures wait and get their notes when it's back.", while
    /// it isn't; null once notes are written again.</summary>
    public string? EngineProblem => engineProblem;

    /// <summary>How long a pass waits before looking at the queue again: longer while the notes engine is away.</summary>
    public TimeSpan Pause => engineProblem is null ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(60);

    /// <summary>Not an answer, but no answer: nothing listening, the connection dropped, or no reply in time. A
    /// refusal with a status (a model it doesn't have) is an answer, and keeps today's handling.</summary>
    public static bool IsOffline(Exception e) => e switch
    {
        TimeoutException => true,
        HttpRequestException h => h.StatusCode is null || h.InnerException is SocketException,
        _ => false,
    };

    public async Task<string?> ProcessAsync(NoteRow row)
    {
        var m = store.Meeting(row);
        string summary = "", model = "", error = "";
        if (Summarize.WantsSummary(m, Cfg))
        {
            model = notesModel?.Invoke() ?? Cfg.EffectiveSummaryModel; // what writes the notes, as the note records it
            var watch = Stopwatch.StartNew();
            try
            {
                summary = await summarize(m, Cfg);
                log($"[pipeline] summarized '{m.Title}' with {model} in {watch.Elapsed.TotalSeconds:0}s");
            }
            catch (Exception e) when (IsOffline(e))
            {
                throw new EngineOfflineException(notesEngine?.Invoke() ?? "Ollama", e);
            }
            catch (Exception e)
            {
                error = $"Summary with {model} failed: {e.Message}";
                log($"[pipeline] {error}");
                summary = model = "";
            }
        }
        Classification c;
        if (row.ClassifiedBy == "human" && !string.IsNullOrEmpty(row.ClassName))
        {
            // A person filed it: writing the notes again never undoes that.
            c = new Classification(row.ClassName, 1.0, "human", row.LectureTitle ?? "", JsonTopics(row.Topics));
        }
        else
        {
            // Sort on our notes when we have them: they are cleaner than the raw transcript.
            var basis = summary.Length > 0 ? m.WithNotes(summary) : m;
            c = await Classify.ClassifyAsync(basis, Cfg, chat);
            if (c.By is "folder" or "rules" && Cfg.OllamaEnabled && Cfg.Classes.Count > 0)
            {
                // The folder or title picked the class; still let the model name the lecture and tag its topics.
                var described = await Classify.WithOllamaAsync(basis, Cfg.Classes, Cfg, chat);
                if (described is not null)
                {
                    if (described.LectureTitle.Length > 0) c.LectureTitle = described.LectureTitle;
                    c.Topics = described.Topics;
                }
            }
        }
        string? path = store.Finish(row, m, c, summary, model, error, keepGranola: Cfg.KeepGranolaNotes);
        log(path is null
            ? $"[pipeline] '{m.Title}' changed while it was being written; not saving the old result"
            : $"[pipeline] filed '{m.Title}' \u2192 {c.ClassName} ({c.By} {Py.FormatFixed(c.Confidence, 2)})");
        return path;
    }

    static List<string> JsonTopics(string? json) =>
        (System.Text.Json.Nodes.JsonNode.Parse(string.IsNullOrEmpty(json) ? "[]" : json) as System.Text.Json.Nodes.JsonArray
         ?? new System.Text.Json.Nodes.JsonArray()).Select(Py.Str).ToList();

    public async Task<int> RunPendingAsync(CancellationToken stop = default)
    {
        int done = 0;
        while (!stop.IsCancellationRequested)
        {
            var row = store.ClaimNext();
            if (row is null) break;
            current = row.Id;
            try
            {
                await ProcessAsync(row);
                done++;
                if (engineProblem is not null)
                {
                    engineProblem = null;
                    log("[pipeline] the notes engine is answering again");
                }
            }
            catch (EngineOfflineException e)
            {
                // Back in the queue, with the rest waiting behind it: the next pass tries again.
                store.ResetWorking();
                engineProblem = $"{e.Engine} isn't answering on your library. New lectures wait and get their notes when it's back.";
                log($"[pipeline] {e.Message}: '{row.Id}' waits");
                break;
            }
            catch (Exception e)
            {
                store.MarkFailed(row.Id, e.Message);
                log($"[pipeline] could not file {row.Id}: {e.Message}\n{e}");
            }
            finally
            {
                current = null;
            }
        }
        return done;
    }

    /// <summary>A new lecture arrived: look at the queue now instead of within 30 seconds.</summary>
    public void Wake()
    {
        try
        {
            wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // already woken
        }
    }

    public async Task RunForeverAsync(CancellationToken stop)
    {
        int n = store.ResetWorking();
        if (n > 0) log($"[pipeline] resuming {n} note(s) interrupted by a restart");
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await RunPendingAsync(stop);
            }
            catch (Exception e)
            {
                log($"[pipeline] error: {e.Message}");
            }
            try
            {
                await wake.WaitAsync(Pause, stop);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public Task Start(CancellationToken stop) => Task.Run(() => RunForeverAsync(stop), CancellationToken.None);
}
