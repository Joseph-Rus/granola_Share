namespace StudyStash.Core.Canvas;

/// <summary>
/// Canvas reads an AI asked for (the course scout, or a chat), carried out by the Chrome extension. The MCP tool asks
/// the library; the library queues the read here and waits while the extension, told to check often whenever
/// something is queued, fetches it with the person's own Canvas session.
/// </summary>
public sealed class AgentQueue
{
    /// <summary>After an AI's last read, the extension keeps checking often for this long: it's likely to ask again.</summary>
    public static readonly TimeSpan HotFor = TimeSpan.FromMinutes(2);

    readonly Lock gate = new();
    readonly List<CanvasJob> jobs = [];
    readonly Dictionary<string, TaskCompletionSource<CanvasResult>> waiting = [];
    DateTime last = DateTime.MinValue;
    int seq;

    /// <summary>Something is queued or was lately: the extension should check every second or two.</summary>
    public bool Hot
    {
        get
        {
            lock (gate) return jobs.Count > 0 || waiting.Count > 0 || DateTime.UtcNow - last < HotFor;
        }
    }

    /// <summary>Queue one read and wait for the extension's answer.</summary>
    public async Task<CanvasResult> FetchAsync(string url, string kind, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        string id;
        var done = new TaskCompletionSource<CanvasResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            id = $"a{++seq}";
            waiting[id] = done;
            jobs.Add(new CanvasJob(id, url, kind));
            last = DateTime.UtcNow;
        }
        try
        {
            return await done.Task.WaitAsync(timeout ?? TimeSpan.FromMinutes(2), ct);
        }
        catch (TimeoutException)
        {
            return new CanvasResult(id, 0, "", "", "", "Chrome didn't answer. Is Chrome open, with the Study Stash extension on?", "");
        }
        finally
        {
            lock (gate)
            {
                waiting.Remove(id);
                jobs.RemoveAll(j => j.Id == id);
            }
        }
    }

    public List<CanvasJob> Take(int n = 6)
    {
        lock (gate)
        {
            var out_ = jobs.Take(n).ToList();
            jobs.RemoveRange(0, out_.Count);
            return out_;
        }
    }

    /// <summary>Hand over an answer. False when it isn't one of these reads (then it's the sync's).</summary>
    public bool Answer(CanvasResult r)
    {
        if (!r.Id.StartsWith('a')) return false;
        lock (gate)
            if (waiting.TryGetValue(r.Id, out var w)) w.TrySetResult(r);
        return true;
    }
}
