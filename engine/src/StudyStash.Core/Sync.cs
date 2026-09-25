using System.Globalization;
using System.Text.Json.Nodes;

namespace StudyStash.Core;

public sealed class SyncReport
{
    public int Listed { get; set; }
    public int New { get; set; }
    public List<string> Queued { get; } = []; // titles handed to the pipeline
    public List<string> Errors { get; } = [];
}

/// <summary>The library's own sync (server_sync): ask Granola for new lectures and queue them for the pipeline.</summary>
public static class Sync
{
    public const int FirstRunLookbackDays = 30; // the free plan only shows 30 days anyway
    public const int OverlapDays = 2;

    /// <summary>Two days before the last sync, or 30 days back the first time.</summary>
    public static DateOnly Since(Store store)
    {
        string? last = store.GetState("last_sync");
        if (last is not null)
            return DateOnly.FromDateTime(DateTimeOffset.Parse(last, CultureInfo.InvariantCulture).DateTime).AddDays(-OverlapDays);
        return DateOnly.FromDateTime(DateTime.Today).AddDays(-FirstRunLookbackDays);
    }

    public static async Task<SyncReport> SyncOnceAsync(Config cfg, ILectureSource client, Store store, Action<string>? log = null,
        Action? onQueued = null, CancellationToken ct = default)
    {
        log ??= Console.WriteLine;
        var report = new SyncReport();
        var since = Since(store);
        await using (var session = await client.SessionAsync(ct))
        {
            var stubs = await client.ListMeetingsAsync(session, since, ct: ct);
            report.Listed = stubs.Count;
            var known = store.KnownIds();
            var newIds = stubs.Where(m => !known.Contains(m.Id)).Select(m => m.Id).ToList();
            report.New = newIds.Count;
            log($"[sync] {stubs.Count} meetings since {since:yyyy-MM-dd}, {newIds.Count} new");
            if (newIds.Count == 0)
            {
                store.SetState("last_sync", Py.IsoNowUtc());
                return report;
            }
            var stubById = new Dictionary<string, Meeting>();
            foreach (var m in stubs) stubById[m.Id] = m;
            var full = new List<Meeting>();
            try
            {
                full = await client.GetMeetingsAsync(session, newIds, ct);
            }
            catch (Exception e) when (!ct.IsCancellationRequested) // the list's own copy still has the basics
            {
                report.Errors.Add($"get_meetings: {e.Message}");
                log($"[sync] get_meetings failed, using list data: {e.Message}");
            }
            var fullById = new Dictionary<string, Meeting>();
            foreach (var m in full) fullById[m.Id] = m;
            foreach (string id in newIds)
            {
                var m = fullById.GetValueOrDefault(id) ?? stubById[id];
                if (m.NotesMarkdown.Length == 0 && stubById.TryGetValue(id, out var stub)) m.NotesMarkdown = stub.NotesMarkdown;
                if (cfg.IncludeTranscripts && m.Transcript.Length == 0) m.Transcript = await client.GetTranscriptAsync(session, id, ct);
                try
                {
                    store.Enqueue(m);
                    report.Queued.Add(m.Title);
                    log($"[sync] queued '{m.Title}'");
                }
                catch (Exception e)
                {
                    report.Errors.Add($"{id}: {e.Message}");
                    log($"[sync] failed on {id}: {e.Message}\n{e}");
                }
            }
            DumpDebug(cfg, full.Count > 0 ? full : stubById.Values.ToList());
        }
        if (report.Queued.Count > 0) onQueued?.Invoke();
        store.SetState("last_sync", Py.IsoNowUtc());
        return report;
    }

    /// <summary>Keep the last raw lectures, so the field mapping can be fixed if Granola changes its output.</summary>
    static void DumpDebug(Config cfg, List<Meeting> meetings)
    {
        try
        {
            Directory.CreateDirectory(cfg.DebugDir);
            var raws = new JsonArray(meetings.Take(5).Select(m => m.Raw.DeepClone()).ToArray());
            Py.WriteText(Path.Combine(cfg.DebugDir, "last_meetings.json"), Py.Head(PyJson.Dumps(raws, indent: 2), 500_000));
        }
        catch (Exception)
        {
            // only a debugging aid
        }
    }

    /// <summary>Sync, then wait poll_interval_seconds, until stopped. A failed round is logged and tried again.</summary>
    public static async Task RunLoopAsync(Config cfg, ILectureSource client, Store store, Action<string>? log = null,
        Action? onQueued = null, CancellationToken stop = default)
    {
        log ??= Console.WriteLine;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await SyncOnceAsync(cfg, client, store, log, onQueued, stop);
            }
            catch (Exception e) when (!stop.IsCancellationRequested)
            {
                log($"[sync] error: {e.Message}");
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(cfg.PollIntervalSeconds), stop);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
