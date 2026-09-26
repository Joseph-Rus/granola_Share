using System.Text.RegularExpressions;

namespace StudyStash.Core.Canvas;

/// <summary>
/// A guess at which Canvas course a class that isn't linked yet probably is ("CS 101" ~ "COMP 101" by their shared
/// number; "CALC II" ~ "Calculus II" by a shared word), so Settings can offer it as a suggestion. Never links
/// anything by itself: the student always picks.
/// </summary>
public static partial class CourseMatch
{
    [GeneratedRegex("[A-Za-z]+")]
    private static partial Regex WordPattern();
    [GeneratedRegex("[0-9]+")]
    private static partial Regex DigitPattern();

    static List<string> Words(string s) => WordPattern().Matches(s).Select(m => m.Value).ToList();
    static List<string> Digits(string s) => DigitPattern().Matches(s).Select(m => m.Value).ToList();

    /// <summary>Two words are "the same" for matching when one is a prefix of the other ("CALC" of "Calculus"), so
    /// abbreviations count; a one-letter word only matches itself.</summary>
    static bool WordMatch(string a, string b)
    {
        int n = Math.Min(a.Length, b.Length);
        return n >= 2 && string.Equals(a[..n], b[..n], StringComparison.OrdinalIgnoreCase);
    }

    static int Score(string className, string code, string name)
    {
        var classWords = Words(className);
        var classDigits = Digits(className);
        var otherWords = Words(code).Concat(Words(name)).ToList();
        var otherDigits = Digits(code).Concat(Digits(name)).ToList();
        int score = 0;
        if (classDigits.Count > 0 && classDigits.Any(otherDigits.Contains)) score += 2;
        if (classWords.Any(w => otherWords.Any(o => WordMatch(w, o)))) score += 1;
        return score;
    }

    /// <summary>The best-guessed course id and name for a class not yet linked to one, or null when nothing scores.
    /// A suggestion only: Settings shows it, the student links it (or doesn't).</summary>
    public static (string Id, string Name)? Suggest(string className, IReadOnlyDictionary<string, string> available, IReadOnlyDictionary<string, CourseInfo> info)
    {
        string CodeOf(string id) => info.TryGetValue(id, out var c) ? c.Code : "";
        return available
            .Select(kv => (Id: kv.Key, Name: kv.Value, Score: Score(className, CodeOf(kv.Key), kv.Value)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score).ThenBy(x => x.Id, StringComparer.Ordinal)
            .Select(x => ((string, string)?)(x.Id, x.Name))
            .FirstOrDefault();
    }
}
