using System.Text;
using System.Text.RegularExpressions;

namespace StudyStash.Core;

/// <summary>A piece of a lecture search and Ask can point at: a bullet or paragraph of its notes (under a heading),
/// or a stretch of its transcript (with the time it starts).</summary>
public sealed record Passage(string NoteId, string Kind, string Section, double? Start, string Text)
{
    public const string NotesKind = "notes", TranscriptKind = "transcript";
}

/// <summary>A passage that matched, with the lecture it's from.</summary>
public sealed record PassageHit(NoteRow Note, Passage Passage, double Rank);

/// <summary>Cutting a lecture into passages, and turning what someone typed into a full-text query.</summary>
public static partial class Passages
{
    const int Target = 420; // characters: a few sentences, enough to answer from

    [GeneratedRegex(@"^#{1,6}\s+(.+?)\s*#*\s*$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^\s*(?:[-*+]|\d+[.)])\s+")]
    private static partial Regex ListItem();

    public static List<Passage> FromNotes(string noteId, string markdown)
    {
        var result = new List<Passage>();
        string section = "";
        var para = new StringBuilder();
        void Flush()
        {
            string t = para.ToString().Trim();
            para.Clear();
            if (t.Length > 0) result.Add(new Passage(noteId, Passage.NotesKind, section, null, t));
        }
        foreach (string raw in markdown.ReplaceLineEndings("\n").Split('\n'))
        {
            string line = raw.TrimEnd();
            var h = Heading().Match(line.Trim());
            if (h.Success)
            {
                Flush();
                section = h.Groups[1].Value.Trim();
                continue;
            }
            if (line.Trim().Length == 0)
            {
                Flush();
                continue;
            }
            if (ListItem().IsMatch(line))
            {
                Flush();
                para.Append(ListItem().Replace(line, "", 1).Trim());
                Flush();
                continue;
            }
            if (para.Length > 0) para.Append(' ');
            para.Append(line.Trim());
            if (para.Length > Target * 2) Flush();
        }
        Flush();
        return result;
    }

    /// <summary>A transcript in stretches of about a minute; a timed one's stretches start where a line does.</summary>
    public static List<Passage> FromTranscript(string noteId, string transcript)
    {
        var result = new List<Passage>();
        if (TimedText.HasTimes(transcript))
        {
            var text = new StringBuilder();
            double? start = null;
            foreach (var s in TimedText.Parse(transcript))
            {
                start ??= s.Start;
                if (text.Length > 0) text.Append(' ');
                text.Append(s.Text);
                if (text.Length >= Target || s.End - start >= 75)
                {
                    result.Add(new Passage(noteId, Passage.TranscriptKind, "", start, text.ToString()));
                    text.Clear();
                    start = null;
                }
            }
            if (text.Length > 0) result.Add(new Passage(noteId, Passage.TranscriptKind, "", start, text.ToString()));
            return result;
        }
        string plain = Regex.Replace(transcript, @"\s+", " ").Trim();
        for (int at = 0; at < plain.Length;)
        {
            int end = Math.Min(plain.Length, at + Target);
            if (end < plain.Length)
            {
                int stop = plain.LastIndexOfAny(['.', '?', '!'], end - 1, end - at);
                if (stop > at + Target / 2) end = stop + 1;
            }
            result.Add(new Passage(noteId, Passage.TranscriptKind, "", null, plain[at..end].Trim()));
            at = end;
        }
        return result;
    }

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex Word();

    static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "and", "or", "of", "to", "in", "on", "for", "is", "are", "was", "were", "be", "it", "this",
        "that", "what", "which", "who", "how", "why", "when", "where", "did", "do", "does", "she", "he", "they", "we",
        "you", "i", "me", "my", "our", "about", "with", "as", "at", "by", "from", "say", "said", "tell", "there",
    };

    public static List<string> Words(string text) => Word().Matches(text).Select(m => m.Value.ToLowerInvariant()).ToList();

    /// <summary>A search box's words for FTS5: every word must appear, the last may be half typed.</summary>
    public static string? AllWords(string query)
    {
        var words = Words(query);
        if (words.Count == 0) return null;
        return string.Join(" ", words.Select((w, i) => i == words.Count - 1 ? $"\"{w}\"*" : $"\"{w}\""));
    }

    /// <summary>A question's words for FTS5: any of the ones that mean something; the ranking does the rest.</summary>
    public static string? AnyWords(string question)
    {
        var words = Words(question).Where(w => !Stop.Contains(w) && w.Length > 1).Distinct().ToList();
        if (words.Count == 0) words = Words(question).Distinct().ToList();
        return words.Count == 0 ? null : string.Join(" OR ", words.Select(w => $"\"{w}\""));
    }
}
