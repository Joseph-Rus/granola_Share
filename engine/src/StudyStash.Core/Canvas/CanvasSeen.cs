using System.Text.Json;
using System.Text.Json.Serialization;

namespace StudyStash.Core.Canvas;

/// <summary>Which announcements the student has opened in Study Stash (Canvas's own read state is separate: an
/// announcement can be read on Canvas but never opened here, or the other way round). Kept in
/// <c>home/canvas_seen.json</c>, class → announcement ids.</summary>
public static class CanvasSeen
{
    static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    static readonly Lock Gate = new();

    static string PathIn(string home) => Path.Combine(home, "canvas_seen.json");

    static Dictionary<string, List<long>> Load(string home)
    {
        try
        {
            return File.Exists(PathIn(home))
                ? JsonSerializer.Deserialize<Dictionary<string, List<long>>>(File.ReadAllText(PathIn(home)), Options) ?? []
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    static void Save(string home, Dictionary<string, List<long>> all)
    {
        string tmp = PathIn(home) + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(all, Options));
        File.Move(tmp, PathIn(home), overwrite: true);
    }

    /// <summary>The announcement ids opened in Study Stash for this class.</summary>
    public static HashSet<long> For(string home, string cls) => Load(home).GetValueOrDefault(cls, []).ToHashSet();

    /// <summary>Mark these announcements opened in Study Stash.</summary>
    public static void Mark(string home, string cls, IEnumerable<long> ids)
    {
        lock (Gate)
        {
            var all = Load(home);
            var mine = (all.TryGetValue(cls, out var have) ? have : all[cls] = []).ToHashSet();
            mine.UnionWith(ids);
            all[cls] = mine.Order().ToList();
            Save(home, all);
        }
    }
}
