using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace StudyStash.Core;

/// <summary>One lecture, as the laptop sends it and the library keeps it.</summary>
public sealed class Meeting(string id)
{
    public string Id { get; set; } = id;
    public string Title { get; set; } = "";
    public string Date { get; set; } = "";
    public string Owner { get; set; } = "";
    public List<string> Attendees { get; set; } = [];
    public string Folder { get; set; } = "";
    public string NotesMarkdown { get; set; } = "";
    public string PrivateNotes { get; set; } = "";
    public string Transcript { get; set; } = "";
    public JsonObject Raw { get; set; } = new();

    /// <summary>dataclasses.replace(m, notes_markdown=...): a copy with other notes.</summary>
    public Meeting WithNotes(string notesMarkdown) => new(Id)
    {
        Title = Title, Date = Date, Owner = Owner, Attendees = Attendees, Folder = Folder,
        NotesMarkdown = notesMarkdown, PrivateNotes = PrivateNotes, Transcript = Transcript, Raw = Raw,
    };
}

/// <summary>A lecture from ingest had no id, or was not an object. Python raised ValueError here.</summary>
public sealed class PayloadException(string message) : Exception(message);

/// <summary>The wire format between the laptop and the library: one lecture as JSON, sent to /api/ingest and kept in
/// each row's payload_json.</summary>
public static class Wire
{
    /// <summary>Attendees sent as one string, "Alex, Sam; Kim": the names between the commas, semicolons and lines.</summary>
    static List<string> AsNameList(string s) => Regex.Split(s, "[,;\n]").Select(Py.Strip).Where(x => x.Length > 0).ToList();

    /// <summary>json.dumps(meeting_to_dict(m)): what the laptop sends, and what the library keeps in payload_json.</summary>
    public static string MeetingJson(Meeting m) => PyJson.Object([
        ("id", PyJson.Dumps(m.Id)),
        ("title", PyJson.Dumps(m.Title)),
        ("date", PyJson.Dumps(m.Date)),
        ("owner", PyJson.Dumps(m.Owner)),
        ("attendees", PyJson.Dumps(m.Attendees)),
        ("folder", PyJson.Dumps(m.Folder)),
        ("notes_markdown", PyJson.Dumps(m.NotesMarkdown)),
        ("private_notes", PyJson.Dumps(m.PrivateNotes)),
        ("transcript", PyJson.Dumps(m.Transcript)),
        ("raw", PyJson.Dumps(m.Raw)),
    ]);

    /// <summary>The lecture a payload describes. Missing fields are empty (the title "Untitled"); without an id it's refused.</summary>
    public static Meeting MeetingFromJson(JsonNode? node)
    {
        if (node is not JsonObject d || !d.TryGetPropertyValue("id", out var idNode) || !Py.Truthy(idNode)
            || Py.Strip(Py.Str(idNode)).Length == 0)
            throw new PayloadException("payload needs a non-empty 'id'");
        string Field(string key, string fallback = "") =>
            d.TryGetPropertyValue(key, out var v) && Py.Truthy(v) ? Py.Str(v) : fallback;
        d.TryGetPropertyValue("attendees", out var attendees);
        List<string> names = !Py.Truthy(attendees) ? []
            : Py.AsString(attendees) is string s ? AsNameList(s)
            : attendees is JsonArray a ? a.Select(Py.Str).ToList()
            : attendees is JsonObject o ? o.Select(kv => kv.Key).ToList()
            : throw new PayloadException("attendees must be a list of names");
        var raw = d.TryGetPropertyValue("raw", out var r) && r is JsonObject ro ? (JsonObject)ro.DeepClone() : new JsonObject();
        return new Meeting(Py.Str(idNode))
        {
            Title = Field("title", "Untitled"),
            Date = Field("date"),
            Owner = Field("owner"),
            Attendees = names,
            Folder = Field("folder"),
            NotesMarkdown = Field("notes_markdown"),
            PrivateNotes = Field("private_notes"),
            Transcript = Field("transcript"),
            Raw = raw,
        };
    }
}
