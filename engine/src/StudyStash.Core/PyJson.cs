using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StudyStash.Core;

/// <summary>
/// json.dumps with Python's defaults: ASCII only, ", " and ": " between items. Note files, their front
/// matter, and the database's JSON columns come out exactly as the Python engine wrote them.
/// </summary>
public static class PyJson
{
    public static string Dumps(JsonNode? node)
    {
        var sb = new StringBuilder();
        Write(sb, node);
        return sb.ToString();
    }

    public static string Dumps(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        WriteString(sb, s);
        return sb.ToString();
    }

    public static string Dumps(IEnumerable<string> items)
    {
        var sb = new StringBuilder("[");
        bool first = true;
        foreach (string item in items)
        {
            if (!first) sb.Append(", ");
            WriteString(sb, item);
            first = false;
        }
        return sb.Append(']').ToString();
    }

    /// <summary>json.dumps(value, indent=n): one item per line, "," then a line break, ": " after keys.</summary>
    public static string Dumps(JsonNode? node, int indent)
    {
        var sb = new StringBuilder();
        WriteIndented(sb, node, indent, 0);
        return sb.ToString();
    }

    static void WriteIndented(StringBuilder sb, JsonNode? node, int indent, int level)
    {
        string inner = "\n" + new string(' ', indent * (level + 1)), outer = "\n" + new string(' ', indent * level);
        switch (node)
        {
            case JsonObject { Count: > 0 } o:
                sb.Append('{');
                bool first = true;
                foreach (var (key, value) in o)
                {
                    sb.Append(first ? "" : ",").Append(inner);
                    WriteString(sb, key);
                    sb.Append(": ");
                    WriteIndented(sb, value, indent, level + 1);
                    first = false;
                }
                sb.Append(outer).Append('}');
                return;
            case JsonArray { Count: > 0 } a:
                sb.Append('[');
                for (int i = 0; i < a.Count; i++)
                {
                    sb.Append(i > 0 ? "," : "").Append(inner);
                    WriteIndented(sb, a[i], indent, level + 1);
                }
                sb.Append(outer).Append(']');
                return;
            default:
                Write(sb, node);
                return;
        }
    }

    /// <summary>A JSON object from fields already written as JSON, in this order.</summary>
    public static string Object(IEnumerable<(string Key, string Json)> fields) =>
        "{" + string.Join(", ", fields.Select(f => Dumps(f.Key) + ": " + f.Json)) + "}";

    static void Write(StringBuilder sb, JsonNode? node)
    {
        switch (node)
        {
            case null:
                sb.Append("null");
                return;
            case JsonObject o:
            {
                sb.Append('{');
                bool first = true;
                foreach (var (key, value) in o)
                {
                    if (!first) sb.Append(", ");
                    WriteString(sb, key);
                    sb.Append(": ");
                    Write(sb, value);
                    first = false;
                }
                sb.Append('}');
                return;
            }
            case JsonArray a:
            {
                sb.Append('[');
                for (int i = 0; i < a.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    Write(sb, a[i]);
                }
                sb.Append(']');
                return;
            }
            case JsonValue v:
                switch (v.GetValueKind())
                {
                    case JsonValueKind.String:
                        WriteString(sb, v.GetValue<string>());
                        return;
                    case JsonValueKind.True:
                        sb.Append("true");
                        return;
                    case JsonValueKind.False:
                        sb.Append("false");
                        return;
                    case JsonValueKind.Number:
                        sb.Append(Number(v));
                        return;
                    case JsonValueKind.Null:
                        sb.Append("null");
                        return;
                }
                break;
        }
        sb.Append(node.ToJsonString());
    }

    /// <summary>Python reads "1.50" as a float and writes it back as "1.5"; whole numbers stay as written.</summary>
    static string Number(JsonValue v)
    {
        string raw = Py.NumberText(v);
        if (!Py.IsFloatText(raw)) return Py.IntText(raw);
        double d = double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture);
        if (double.IsNaN(d)) return "NaN";
        if (double.IsInfinity(d)) return d > 0 ? "Infinity" : "-Infinity";
        return Py.FloatRepr(d);
    }

    internal static void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < ' ' || c > '~') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }
}
