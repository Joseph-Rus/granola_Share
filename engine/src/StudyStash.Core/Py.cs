using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace StudyStash.Core;

/// <summary>
/// Python behaviour the Python engine relied on. Both engines share one data folder, so they must strip,
/// split, round, and name files the same way, down to the byte.
/// </summary>
public static class Py
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // --- strings -----------------------------------------------------------------------------------

    /// <summary>str.isspace(): .NET's whitespace plus the four ASCII separators Python also counts.</summary>
    public static bool IsSpace(char c) => char.IsWhiteSpace(c) || c is >= '\x1c' and <= '\x1f';

    public static string Strip(string? s) => RStrip(LStrip(s ?? ""));

    public static string LStrip(string s)
    {
        int i = 0;
        while (i < s.Length && IsSpace(s[i])) i++;
        return s[i..];
    }

    public static string RStrip(string s)
    {
        int j = s.Length;
        while (j > 0 && IsSpace(s[j - 1])) j--;
        return s[..j];
    }

    static bool IsLineBreak(char c) =>
        c is '\n' or '\r' or '\v' or '\f' or '\x1c' or '\x1d' or '\x1e' or '\x85' or '\u2028' or '\u2029';

    /// <summary>str.splitlines(): every line break Python knows, and no empty line after a final break.</summary>
    public static List<string> SplitLines(string s)
    {
        var lines = new List<string>();
        int start = 0, i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (!IsLineBreak(c))
            {
                i++;
                continue;
            }
            lines.Add(s[start..i]);
            i += c == '\r' && i + 1 < s.Length && s[i + 1] == '\n' ? 2 : 1;
            start = i;
        }
        if (start < s.Length) lines.Add(s[start..]);
        return lines;
    }

    /// <summary>s[:n], without cutting an emoji (a surrogate pair) in half.</summary>
    public static string Head(string s, int n)
    {
        if (s.Length <= n) return s;
        if (n > 0 && char.IsHighSurrogate(s[n - 1])) n--;
        return s[..n];
    }

    /// <summary>s[-n:], without cutting an emoji in half.</summary>
    public static string Tail(string s, int n)
    {
        if (s.Length <= n) return s;
        int start = s.Length - n;
        if (char.IsLowSurrogate(s[start]) && start > 0) start--;
        return s[start..];
    }

    static readonly Regex WhitespaceOnly = new("^[ \t]+$", RegexOptions.Multiline);
    static readonly Regex LeadingWhitespace = new("(^[ \t]*)(?:[^ \t\n])", RegexOptions.Multiline);

    /// <summary>textwrap.dedent: remove the indentation every line shares; blank lines become empty.</summary>
    public static string Dedent(string text)
    {
        string? margin = null;
        text = WhitespaceOnly.Replace(text, "");
        foreach (Match m in LeadingWhitespace.Matches(text))
        {
            string indent = m.Groups[1].Value;
            if (margin is null) margin = indent;
            else if (indent.StartsWith(margin, StringComparison.Ordinal)) { }
            else if (margin.StartsWith(indent, StringComparison.Ordinal)) margin = indent;
            else
            {
                int i = 0;
                while (i < margin.Length && i < indent.Length && margin[i] == indent[i]) i++;
                margin = margin[..i];
            }
        }
        return string.IsNullOrEmpty(margin) ? text : Regex.Replace(text, "(?m)^" + Regex.Escape(margin), "");
    }

    /// <summary>
    /// urllib.parse.quote_plus: what Python's urlencode does to each key and value. Letters, digits and "_.-~"
    /// stay, a space becomes "+", and everything else becomes %XX of its UTF-8 bytes.
    /// </summary>
    public static string QuotePlus(string s)
    {
        var sb = new StringBuilder();
        foreach (byte b in Encoding.UTF8.GetBytes(s))
        {
            char c = (char)b;
            if (char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-' or '~') sb.Append(c);
            else if (c == ' ') sb.Append('+');
            else sb.Append('%').Append(b.ToString("X2", Inv));
        }
        return sb.ToString();
    }

    /// <summary>urllib.parse.urlencode(dict).</summary>
    public static string UrlEncode(IEnumerable<(string Key, string Value)> pairs) =>
        string.Join("&", pairs.Select(p => QuotePlus(p.Key) + "=" + QuotePlus(p.Value)));

    /// <summary>urllib.parse.parse_qs(q) with the first value of each key: blank values are dropped, as there.</summary>
    public static Dictionary<string, string> ParseQs(string query)
    {
        var result = new Dictionary<string, string>();
        foreach (string part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq < 0) continue;
            string key = Unquote(part[..eq]), value = Unquote(part[(eq + 1)..]);
            if (value.Length > 0) result.TryAdd(key, value);
        }
        return result;
        static string Unquote(string s) => Uri.UnescapeDataString(s.Replace('+', ' '));
    }

    /// <summary>datetime.now(timezone.utc).isoformat(): microseconds, unless there are none.</summary>
    public static string IsoNowUtc()
    {
        var now = DateTime.UtcNow;
        long micro = now.Ticks / 10 % 1_000_000;
        return now.ToString("yyyy-MM-dd'T'HH:mm:ss", Inv) + (micro != 0 ? "." + micro.ToString("000000", Inv) : "") + "+00:00";
    }

    /// <summary>str.lower(): .NET's own casing, plus the one unconditional full mapping Python applies that .NET leaves
    /// alone ("İ" becomes "i" and a combining dot).</summary>
    public static string Lower(string s) => s.Replace("\u0130", "i\u0307").ToLowerInvariant();

    /// <summary>subprocess.list2cmdline: one Windows command line, quoted the way the C runtime reads it back.</summary>
    public static string List2CmdLine(IEnumerable<string> args)
    {
        var result = new StringBuilder();
        foreach (string arg in args)
        {
            if (result.Length > 0) result.Append(' ');
            bool quote = arg.Contains(' ') || arg.Contains('\t') || arg.Length == 0;
            if (quote) result.Append('"');
            int backslashes = 0;
            foreach (char c in arg)
            {
                if (c == '\\')
                {
                    backslashes++;
                }
                else if (c == '"')
                {
                    result.Append('\\', backslashes * 2).Append("\\\"");
                    backslashes = 0;
                }
                else
                {
                    result.Append('\\', backslashes).Append(c);
                    backslashes = 0;
                }
            }
            result.Append('\\', backslashes);
            if (quote) result.Append('\\', backslashes).Append('"');
        }
        return result.ToString();
    }

    /// <summary>time.time(): seconds since 1970, as a float.</summary>
    public static double Time() => (DateTime.UtcNow - DateTime.UnixEpoch).Ticks / (double)TimeSpan.TicksPerSecond;

    /// <summary>
    /// json.loads: a key given twice keeps its last value (System.Text.Json refuses the object), and numbers keep
    /// exactly the digits they were sent with.
    /// </summary>
    public static JsonNode? JsonLoads(string text)
    {
        using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 512 });
        return Build(doc.RootElement);

        static JsonNode? Build(JsonElement e)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    var o = new JsonObject();
                    foreach (var p in e.EnumerateObject()) o[p.Name] = Build(p.Value);
                    return o;
                case JsonValueKind.Array:
                    var a = new JsonArray();
                    foreach (var item in e.EnumerateArray()) a.Add(Build(item));
                    return a;
                case JsonValueKind.Null:
                    return null;
                default:
                    return JsonValue.Create(e.Clone());
            }
        }
    }

    // --- numbers -----------------------------------------------------------------------------------

    /// <summary>repr(float): the shortest digits that read back the same, laid out the way Python does.</summary>
    public static string FloatRepr(double d)
    {
        if (double.IsNaN(d)) return "nan";
        if (double.IsInfinity(d)) return d > 0 ? "inf" : "-inf";
        var (negative, digits, point) = ShortestDigits(d);
        string sign = negative ? "-" : "";
        if (digits == "0") return sign + "0.0";
        if (point > -4 && point <= 16)
        {
            if (point <= 0) return sign + "0." + new string('0', -point) + digits;
            if (point >= digits.Length) return sign + digits + new string('0', point - digits.Length) + ".0";
            return sign + digits[..point] + "." + digits[point..];
        }
        int exp = point - 1;
        string mantissa = digits.Length > 1 ? digits[..1] + "." + digits[1..] : digits;
        return sign + mantissa + "e" + (exp < 0 ? "-" : "+") + Math.Abs(exp).ToString("00", Inv);
    }

    /// <summary>The value is 0.DIGITS × 10^point.</summary>
    static (bool Negative, string Digits, int Point) ShortestDigits(double d)
    {
        string r = d.ToString("R", Inv); // shortest round-trip, e.g. "0.6", "1E-05", "10000000000000000"
        bool negative = r.StartsWith('-');
        if (negative) r = r[1..];
        int exp = 0;
        int e = r.IndexOfAny(['E', 'e']);
        if (e >= 0)
        {
            exp = int.Parse(r[(e + 1)..], Inv);
            r = r[..e];
        }
        int dot = r.IndexOf('.');
        string whole = dot >= 0 ? r[..dot] : r, frac = dot >= 0 ? r[(dot + 1)..] : "";
        string all = whole + frac;
        int point = whole.Length + exp;
        int lead = 0;
        while (lead < all.Length - 1 && all[lead] == '0') lead++;
        all = all[lead..];
        point -= lead;
        all = all.TrimEnd('0');
        if (all.Length == 0) return (negative, "0", 1);
        return (negative, all, point);
    }

    /// <summary>f"{d:.{places}f}": the exact binary value rounded half to even, as Python does.</summary>
    public static string FormatFixed(double d, int places)
    {
        if (double.IsNaN(d)) return "nan";
        if (double.IsInfinity(d)) return d > 0 ? "inf" : "-inf";
        long bits = BitConverter.DoubleToInt64Bits(d);
        bool negative = bits < 0;
        int rawExp = (int)((bits >> 52) & 0x7FF);
        long frac = bits & 0xFFFFFFFFFFFFFL;
        BigInteger mant = rawExp == 0 ? frac : frac | (1L << 52);
        int exp = (rawExp == 0 ? 1 : rawExp) - 1075; // value = mant × 2^exp
        BigInteger num = mant * BigInteger.Pow(10, places);
        BigInteger q;
        if (exp >= 0)
        {
            q = num << exp;
        }
        else
        {
            BigInteger den = BigInteger.One << -exp;
            q = BigInteger.DivRem(num, den, out BigInteger rem);
            int half = (rem * 2).CompareTo(den);
            if (half > 0 || (half == 0 && !q.IsEven)) q += 1;
        }
        string s = q.ToString(Inv).PadLeft(places + 1, '0');
        string text = places > 0 ? s[..^places] + "." + s[^places..] : s;
        return negative ? "-" + text : text;
    }

    // --- JSON values, the way Python sees what json.loads returned ---------------------------------------

    /// <summary>Python truthiness: None, False, 0, "", [], and {} are false.</summary>
    public static bool Truthy(JsonNode? v) => v switch
    {
        null => false,
        JsonObject o => o.Count > 0,
        JsonArray a => a.Count > 0,
        JsonValue val => val.GetValueKind() switch
        {
            JsonValueKind.String => val.GetValue<string>().Length > 0,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => NumberValue(val) != 0,
            _ => false,
        },
        _ => false,
    };

    /// <summary>A JSON string's text, or null when the value is not a string.</summary>
    public static string? AsString(JsonNode? v) =>
        v is JsonValue val && val.GetValueKind() == JsonValueKind.String ? val.GetValue<string>() : null;

    /// <summary>str(value) in Python.</summary>
    public static string Str(JsonNode? v) => AsString(v) ?? Repr(v);

    /// <summary>repr(value) in Python, for what json.loads returns.</summary>
    public static string Repr(JsonNode? v)
    {
        switch (v)
        {
            case null:
                return "None";
            case JsonObject o:
                return "{" + string.Join(", ", o.Select(kv => StrRepr(kv.Key) + ": " + Repr(kv.Value))) + "}";
            case JsonArray a:
                return "[" + string.Join(", ", a.Select(Repr)) + "]";
            case JsonValue val:
                switch (val.GetValueKind())
                {
                    case JsonValueKind.String: return StrRepr(val.GetValue<string>());
                    case JsonValueKind.True: return "True";
                    case JsonValueKind.False: return "False";
                    case JsonValueKind.Number:
                        string raw = NumberText(val);
                        return IsFloatText(raw) ? FloatRepr(NumberValue(val)) : IntText(raw);
                }
                break;
        }
        return v.ToJsonString();
    }

    internal static string NumberText(JsonValue val) =>
        val.TryGetValue(out JsonElement el) ? el.GetRawText() : val.ToJsonString();

    internal static bool IsFloatText(string raw) => raw.IndexOfAny(['.', 'e', 'E']) >= 0;

    internal static string IntText(string raw) => raw == "-0" ? "0" : raw;

    public static double NumberValue(JsonValue val) =>
        double.Parse(NumberText(val), NumberStyles.Float, Inv);

    /// <summary>repr(str): single quotes unless the text has one and no double quote.</summary>
    public static string StrRepr(string s)
    {
        char quote = s.Contains('\'') && !s.Contains('"') ? '"' : '\'';
        var sb = new StringBuilder().Append(quote);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == quote || c == '\\') sb.Append('\\').Append(c);
            else if (c == '\t') sb.Append("\\t");
            else if (c == '\n') sb.Append("\\n");
            else if (c == '\r') sb.Append("\\r");
            else if (c < ' ' || c == '\x7f') sb.Append("\\x").Append(((int)c).ToString("x2", Inv));
            else if (c < '\x7f') sb.Append(c);
            else if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                var rune = new Rune(c, s[i + 1]);
                if (Printable(Rune.GetUnicodeCategory(rune))) sb.Append(c).Append(s[i + 1]);
                else sb.Append("\\U").Append(rune.Value.ToString("x8", Inv));
                i++;
            }
            else if (Printable(CharUnicodeInfo.GetUnicodeCategory(c))) sb.Append(c);
            else if (c <= '\xff') sb.Append("\\x").Append(((int)c).ToString("x2", Inv));
            else sb.Append("\\u").Append(((int)c).ToString("x4", Inv));
        }
        return sb.Append(quote).ToString();
    }

    static bool Printable(UnicodeCategory cat) => cat is not (UnicodeCategory.Control or UnicodeCategory.Format
        or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned
        or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator or UnicodeCategory.SpaceSeparator);

    // --- files ---------------------------------------------------------------------------------------

    /// <summary>Path.read_text(encoding="utf-8"): any line ending reads as "\n", as Python's text mode does.</summary>
    public static string ReadText(string path)
    {
        string text = new UTF8Encoding(false).GetString(File.ReadAllBytes(path));
        return text.Replace("\r\n", "\n").Replace('\r', '\n');
    }

    /// <summary>Path.write_text(encoding="utf-8"): Windows gets "\r\n" line endings, as Python writes them there.</summary>
    public static void WriteText(string path, string text)
    {
        if (OperatingSystem.IsWindows()) text = text.Replace("\n", "\r\n");
        File.WriteAllBytes(path, new UTF8Encoding(false).GetBytes(text));
    }

    /// <summary>os.chmod(path, 0o600): only you can read it. Windows has no such bits, as in Python.</summary>
    public static void OwnerOnly(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    // --- paths, spelled the way pathlib spells them ----------------------------------------------------

    /// <summary>The home folder Path.expanduser() uses: HOME, or USERPROFILE on Windows.</summary>
    public static string UserHome()
    {
        string? home = Environment.GetEnvironmentVariable(OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME");
        if (string.IsNullOrEmpty(home) && OperatingSystem.IsWindows())
        {
            string drive = Environment.GetEnvironmentVariable("HOMEDRIVE") ?? "";
            string rest = Environment.GetEnvironmentVariable("HOMEPATH") ?? "";
            home = drive + rest;
        }
        return string.IsNullOrEmpty(home) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : home;
    }

    /// <summary>Path(p).expanduser(): a leading "~" becomes your home folder.</summary>
    public static string ExpandUser(string p)
    {
        bool sep(char c) => c == '/' || (OperatingSystem.IsWindows() && c == '\\');
        if (p == "~" || (p.Length > 1 && p[0] == '~' && sep(p[1]))) p = UserHome() + p[1..];
        return NormPath(p);
    }

    /// <summary>str(Path(p)): no doubled or trailing separators, no "." parts, and backslashes on Windows.</summary>
    public static string NormPath(string p) => OperatingSystem.IsWindows() ? NormWindows(p) : NormPosix(p);

    internal static string NormPosix(string p)
    {
        if (p.Length == 0) return ".";
        string root = p.StartsWith("//") && !p.StartsWith("///") ? "//" : p.StartsWith('/') ? "/" : "";
        string rest = string.Join("/", p.Split('/').Where(s => s.Length > 0 && s != "."));
        string result = root + rest;
        return result.Length == 0 ? "." : result;
    }

    internal static string NormWindows(string p)
    {
        p = p.Replace('/', '\\');
        string drive = "";
        if (p.Length >= 2 && p[1] == ':' && char.IsAsciiLetter(p[0]))
        {
            drive = p[..2];
            p = p[2..];
        }
        else if (p.StartsWith(@"\\") && !p.StartsWith(@"\\\"))
        {
            // \\server\share is the drive of a network path
            string[] unc = p[2..].Split('\\', 3);
            if (unc.Length >= 2 && unc[0].Length > 0 && unc[1].Length > 0)
            {
                drive = @"\\" + unc[0] + "\\" + unc[1];
                p = unc.Length == 3 ? "\\" + unc[2] : "\\";
            }
        }
        string root = p.StartsWith('\\') ? "\\" : "";
        string rest = string.Join("\\", p.Split('\\').Where(s => s.Length > 0 && s != "."));
        string result = drive + root + rest;
        return result.Length == 0 ? "." : result;
    }

    /// <summary>Path.parent, spelled as pathlib would.</summary>
    public static string Parent(string path)
    {
        string p = NormPath(path);
        string? parent = Path.GetDirectoryName(p);
        return string.IsNullOrEmpty(parent) ? (Path.IsPathRooted(p) ? p : ".") : NormPath(parent);
    }

    /// <summary>Two spellings of one file: Windows and macOS don't tell capitals apart in file names.</summary>
    public static bool SamePath(string a, string b)
    {
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), comparison);
    }
}
