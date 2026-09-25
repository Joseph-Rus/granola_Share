using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace StudyStash.Core;

/// <summary>
/// Granola's tool output: one text block that is JSON (empty accounts, account info) or a loose XML dialect
/// (accounts with notes). The XML is not always well-formed: participant lines carry raw &lt;john@acme.com&gt;
/// tokens and prose has bare "&amp;". So it is escaped first; then a strict parse; then the reply read as a run of
/// fragments; then a regex block parser. Each step gives what the Python engine's ElementTree steps give.
/// </summary>
public static partial class Granola
{
    [GeneratedRegex(@"&(?!(?:[A-Za-z][A-Za-z0-9]*|#\d+|#x[0-9A-Fa-f]+);)")]
    private static partial Regex BareAmp();

    [GeneratedRegex("<(?![A-Za-z_/?!])")]
    private static partial Regex BareLt();

    [GeneratedRegex(@"\A```[A-Za-z0-9_-]*\s*\n(.*?)\n?```\s*\z", RegexOptions.Singleline)]
    private static partial Regex Fence();

    const string TagPattern = @"<([A-Za-z_][\w.:-]*)((?:\s[^<>]*?)?)\s*(/?)>";

    [GeneratedRegex(TagPattern, RegexOptions.Singleline)]
    private static partial Regex Tag();

    [GeneratedRegex(@"\A" + TagPattern, RegexOptions.Singleline)]
    private static partial Regex TagAtStart();

    [GeneratedRegex("([A-Za-z_][\\w.:-]*)\\s*=\\s*(?:\"([^\"]*)\"|'([^']*)')", RegexOptions.Singleline)]
    private static partial Regex Attr();

    [GeneratedRegex("<access_notice>(.*?)</access_notice>", RegexOptions.Singleline)]
    private static partial Regex AccessNoticeTag();

    // Granola wraps results in advisories: an <access_notice> about plan limits, then a sentence telling the reader
    // to treat the notes as data. So a reply is a sequence of fragments, not one document: it is wrapped, and the
    // element that carries the payload is pulled out.
    static readonly string[] PayloadTags = ["meetings_data", "transcript", "folders", "meeting"];
    const string WrapRoot = "granola_response";

    /// <summary>Escape what Granola leaves raw: bare "&amp;", "&lt;email@host&gt;" tokens, and stray "&lt;".</summary>
    internal static string SanitizeXml(string text)
    {
        text = BareAmp().Replace(text, "&amp;");
        text = EmailToken().Replace(text, m => "&lt;" + m.Groups[1].Value + "&gt;");
        return BareLt().Replace(text, "&lt;");
    }

    static string CleanText(string? text) => Py.Strip(Py.Dedent(text ?? ""));

    /// <summary>ElementTree's element.tag: the name, with "{namespace}" in front when it has one.</summary>
    static string TagName(XName name) => name.NamespaceName.Length == 0 ? name.LocalName : "{" + name.NamespaceName + "}" + name.LocalName;

    /// <summary>ElementTree's element.text: the text before the first child element.</summary>
    static string LeadingText(XElement el)
    {
        var sb = new StringBuilder();
        foreach (var node in el.Nodes())
        {
            if (node is XElement) break;
            if (node is XText t) sb.Append(t.Value);
        }
        return sb.ToString();
    }

    /// <summary>A strict parse, as ElementTree's fromstring: one root element, no DTDs, or null.</summary>
    static XElement? ParseXml(string text)
    {
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using var reader = XmlReader.Create(new StringReader(text), settings);
            return XDocument.Load(reader, LoadOptions.PreserveWhitespace).Root;
        }
        catch (XmlException)
        {
            return null;
        }
    }

    /// <summary>A repeated tag becomes a list, in document order, keeping the key where it first appeared.</summary>
    static void AddChild(JsonObject data, string key, JsonNode value)
    {
        if (!data.TryGetPropertyValue(key, out var existing))
        {
            data[key] = value;
            return;
        }
        if (existing is JsonArray list)
        {
            list.Add(value);
            return;
        }
        data[key] = null; // lets go of the old value, so it can move into the list
        data[key] = new JsonArray(existing, value);
    }

    static void SetDefault(JsonObject data, string key, string value)
    {
        if (!data.ContainsKey(key)) data[key] = value;
    }

    static JsonNode ElementToData(XElement el)
    {
        var data = new JsonObject();
        foreach (var a in el.Attributes())
            if (!a.IsNamespaceDeclaration) data[TagName(a.Name)] = a.Value;
        var children = el.Elements().ToList();
        string text = CleanText(LeadingText(el));
        if (children.Count == 0)
        {
            if (data.Count == 0) return JsonValue.Create(text);
            if (text.Length > 0) SetDefault(data, "text", text);
            return data;
        }
        foreach (var child in children) AddChild(data, TagName(child.Name), ElementToData(child));
        if (text.Length > 0) SetDefault(data, "text", text);
        return data;
    }

    static string AfterDeclaration(string text)
    {
        int end = text.IndexOf("?>", StringComparison.Ordinal);
        return end < 0 ? text : text[(end + 2)..];
    }

    /// <summary>
    /// A regex block parser for XML the strict parser refuses even after escaping. It handles the shallow shapes
    /// Granola uses: a root element with attributes, child elements (a repeated tag becomes a list), and text.
    /// Returns null when there is no root element to find.
    /// </summary>
    internal static JsonNode? LooseParse(string text)
    {
        text = Py.Strip(text);
        if (text.StartsWith("<?", StringComparison.Ordinal)) text = Py.Strip(AfterDeclaration(text));
        var m = TagAtStart().Match(text);
        if (!m.Success) return null;
        string tag = m.Groups[1].Value, attrText = m.Groups[2].Value;
        var attrs = new JsonObject();
        foreach (Match a in Attr().Matches(attrText))
            attrs[a.Groups[1].Value] = WebUtility.HtmlDecode(a.Groups[2].Success ? a.Groups[2].Value : a.Groups[3].Value);
        if (m.Groups[3].Value.Length > 0) return attrs;
        int end = text.LastIndexOf("</" + tag + ">", StringComparison.Ordinal);
        if (end < 0) return null;
        int start = m.Index + m.Length;
        string inner = end >= start ? text[start..end] : "";
        var data = (JsonObject)attrs.DeepClone();
        var looseText = new StringBuilder();
        bool foundChild = false;
        int pos = 0;
        while (true)
        {
            var cm = Tag().Match(inner, pos);
            if (!cm.Success)
            {
                looseText.Append(inner, pos, inner.Length - pos);
                break;
            }
            looseText.Append(inner, pos, cm.Index - pos);
            string ctag = cm.Groups[1].Value;
            string childSrc;
            int next;
            if (cm.Groups[3].Value.Length > 0)
            {
                childSrc = cm.Value;
                next = cm.Index + cm.Length;
            }
            else
            {
                int cend = inner.IndexOf("</" + ctag + ">", cm.Index + cm.Length, StringComparison.Ordinal);
                if (cend < 0) // an unclosed tag such as "<inaudible>" in prose: keep it as text
                {
                    looseText.Append(cm.Value);
                    pos = cm.Index + cm.Length;
                    continue;
                }
                next = cend + ctag.Length + 3;
                childSrc = inner[cm.Index..next];
            }
            var value = LooseParse(childSrc);
            if (value is null)
            {
                looseText.Append(childSrc);
                pos = next;
                continue;
            }
            foundChild = true;
            AddChild(data, ctag, value);
            pos = next;
        }
        string free = CleanText(WebUtility.HtmlDecode(looseText.ToString()));
        if (!foundChild && attrs.Count == 0) return JsonValue.Create(free);
        if (free.Length > 0) SetDefault(data, "text", free);
        return data;
    }

    /// <summary>&lt;meetings_data&gt; always holds a "meeting" list, even of one or none.</summary>
    static JsonNode NormaliseRoot(string tag, JsonNode data)
    {
        if (data is JsonObject o && tag == "meetings_data")
        {
            var meetings = o["meeting"];
            if (meetings is null) o["meeting"] = new JsonArray();
            else if (meetings is not JsonArray)
            {
                o["meeting"] = null;
                o["meeting"] = new JsonArray(meetings);
            }
        }
        return data;
    }

    /// <summary>
    /// Granola's XML-ish tool output as plain data: an element is an object of its attributes and children, a
    /// repeated child tag a list, a text-only leaf its stripped text. Hopeless input comes back as the text.
    /// </summary>
    public static JsonNode XmlToData(string text)
    {
        string src = Py.Strip(text);
        if (!src.StartsWith('<')) return JsonValue.Create(text);
        var root = ParseXml(SanitizeXml(src));
        if (root is not null) return NormaliseRoot(TagName(root.Name), ElementToData(root));
        var fragments = ParseFragments(src);
        if (fragments is var (tag, data)) return NormaliseRoot(tag, data);
        var loose = LooseParse(src);
        if (loose is null) return JsonValue.Create(text);
        var m = TagAtStart().Match(src.StartsWith("<?", StringComparison.Ordinal) ? Py.Strip(AfterDeclaration(src)) : src);
        return NormaliseRoot(m.Success ? m.Groups[1].Value : "", loose);
    }

    static string StripFences(string blob)
    {
        var m = Fence().Match(blob);
        return m.Success ? Py.Strip(m.Groups[1].Value) : blob;
    }

    /// <summary>The XML part of a text block: all of it, or what follows one line of preamble.</summary>
    static string? XmlCandidate(string blob)
    {
        if (blob.StartsWith('<')) return blob;
        int nl = blob.IndexOf('\n');
        if (nl < 0) return null;
        string first = blob[..nl], rest = Py.Strip(blob[(nl + 1)..]);
        return !first.Contains('<') && rest.StartsWith('<') ? rest : null;
    }

    static XElement? RichestChild(XElement root) =>
        root.Elements().Where(c => c.Name.LocalName is not ("access_notice" or "notice" or "warning") || c.Name.NamespaceName.Length > 0)
            .Select(c => (Element: c, Size: 1 + c.Descendants().Count()))
            .Aggregate(((XElement?)null, -1), (best, c) => c.Size > best.Item2 ? (c.Element, c.Size) : best).Item1;

    /// <summary>The element holding the data: a known payload tag anywhere, else the biggest child.</summary>
    static XElement? FindPayloadElement(XElement root)
    {
        foreach (string tag in PayloadTags)
            if (root.Descendants(tag).FirstOrDefault() is XElement el) return el;
        return RichestChild(root);
    }

    /// <summary>The plan-limit sentence Granola puts first, if any: worth repeating to the person.</summary>
    public static string AccessNotice(string? text)
    {
        var m = AccessNoticeTag().Match(text ?? "");
        return m.Success ? CleanText(m.Groups[1].Value) : "";
    }

    /// <summary>A blob of notices, prose and XML, parsed by wrapping it in one made-up root element.</summary>
    static (string Tag, JsonNode Data)? ParseFragments(string src)
    {
        var wrapped = ParseXml($"<{WrapRoot}>{SanitizeXml(src)}</{WrapRoot}>");
        if (wrapped is null) return null;
        var el = FindPayloadElement(wrapped);
        return el is null ? null : (TagName(el.Name), ElementToData(el));
    }
}
