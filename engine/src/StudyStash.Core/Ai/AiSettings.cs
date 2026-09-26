using System.Text.Json;
using System.Text.Json.Nodes;

namespace StudyStash.Core.Ai;

/// <summary>Which AI does one kind of work, and with which of its models ("" is its default).</summary>
public sealed record AiChoice(string Provider, string Model = "");

/// <summary>
/// Which AI does what, kept in ai.json beside config.toml (so config.toml stays as the Python engine writes it).
/// One AI is picked for everything, and each kind of work may use another:
/// <list type="bullet">
/// <item><c>notes</c>: writing study notes from a transcript;</item>
/// <item><c>sort</c>: filing a lecture under its class;</item>
/// <item><c>ask</c>: answering questions from your notes;</item>
/// <item><c>agent</c>: work that reads around and uses tools (Canvas, the chat that can change notes).</item>
/// </list>
/// With no ai.json, everything is the library's Ollama model, as before.
/// </summary>
public sealed class AiSettings
{
    public static readonly string[] Jobs = ["notes", "sort", "ask", "agent"];

    public string Provider { get; set; } = "ollama";
    /// <summary>Per kind of work: its own provider and model, when it isn't the one above.</summary>
    public Dictionary<string, AiChoice> ByJob { get; set; } = [];
    /// <summary>Per provider: the model to use when a job doesn't name one.</summary>
    public Dictionary<string, string> Models { get; set; } = [];
    /// <summary>Per provider: "works", or what went wrong the last time it was tried.</summary>
    public Dictionary<string, string> Tests { get; set; } = [];
    /// <summary>The terminal "Open in Claude Code" uses (ghostty, terminal, iterm, warp).</summary>
    public string Terminal { get; set; } = "ghostty";
    /// <summary>Whether a question goes to Ollama when the engine asked (or picked as a default) can't answer.</summary>
    public bool Fallback { get; set; } = true;
    /// <summary>Per provider: until when its usage limit lasts (ISO 8601), while still in the future.</summary>
    public Dictionary<string, string> Limits { get; set; } = [];
    /// <summary>Problem ids the student has closed, hidden until the problem changes (a new usage limit, say).</summary>
    public List<string> Dismissed { get; set; } = [];

    public static string PathIn(string home) => System.IO.Path.Combine(home, "ai.json");

    /// <summary>What does this kind of work.</summary>
    public AiChoice For(string job)
    {
        if (ByJob.TryGetValue(job, out var c) && c.Provider.Length > 0)
            return c.Model.Length > 0 ? c : c with { Model = Models.GetValueOrDefault(c.Provider, "") };
        return new AiChoice(Provider, Models.GetValueOrDefault(Provider, ""));
    }

    /// <summary>True when a job runs on the library's own Ollama, the way the engine always has.</summary>
    public bool Local(string job) => For(job).Provider == "ollama";

    static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static AiSettings Load(string home)
    {
        string path = PathIn(home);
        if (!File.Exists(path)) return new AiSettings();
        try
        {
            return JsonSerializer.Deserialize<AiSettings>(File.ReadAllText(path), Options) ?? new AiSettings();
        }
        catch (JsonException)
        {
            return new AiSettings();
        }
    }

    public void Save(string home)
    {
        Directory.CreateDirectory(home);
        Py.WriteText(PathIn(home), JsonSerializer.Serialize(this, Options) + "\n");
    }

    /// <summary>As the API shows it: the choice, each job's, and every provider with whether it's here.</summary>
    public JsonObject ToJson()
    {
        var jobs = new JsonObject();
        foreach (string j in Jobs)
        {
            var c = For(j);
            jobs[j] = new JsonObject { ["provider"] = c.Provider, ["model"] = c.Model, ["own"] = ByJob.ContainsKey(j) };
        }
        var providers = new JsonArray();
        foreach (var p in AiProviders.All())
            providers.Add(new JsonObject
            {
                ["id"] = p.Id, ["name"] = p.Name, ["installed"] = p.Available(), ["site"] = p.Site,
                ["test"] = Tests.GetValueOrDefault(p.Id, ""),
                ["models"] = new JsonArray(p.Models.Select(m => (JsonNode)new JsonObject { ["id"] = m.Id, ["label"] = m.Label }).ToArray()),
            });
        return new JsonObject { ["provider"] = Provider, ["jobs"] = jobs, ["providers"] = providers };
    }
}
