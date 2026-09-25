using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StudyStash.Core;

/// <summary>One weekly meeting of a class: Tuesdays, 10:00 to 11:15.</summary>
public sealed record ClassTime(DayOfWeek Day, TimeOnly Start, TimeOnly End)
{
    static readonly string[] Days = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];

    /// <summary>"Tue 10:00–11:15".</summary>
    public string Describe() => $"{Days[(int)Day]} {Start.ToString("H:mm", CultureInfo.InvariantCulture)}–{End.ToString("H:mm", CultureInfo.InvariantCulture)}";
}

/// <summary>A class in your timetable, and when it meets.</summary>
public sealed record TimetableClass(string Name, List<ClassTime> Times);

/// <summary>What Record means right now: the class on, or about to start, and its time.</summary>
public sealed record ClassNow(string Name, ClassTime Time);

/// <summary>
/// When your classes meet, kept on the laptop (timetable.json): Record picks the class on now, so a lecture is
/// filed where it belongs without asking. The classes themselves are the library's.
/// </summary>
public sealed class Timetable
{
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public List<TimetableClass> Classes { get; set; } = [];

    public static string PathIn(string home) => Path.Combine(home, "timetable.json");

    public static Timetable Load(string home)
    {
        try
        {
            return JsonSerializer.Deserialize<Timetable>(File.ReadAllText(PathIn(home)), Json) ?? new Timetable();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new Timetable();
        }
    }

    public void Save(string home)
    {
        Directory.CreateDirectory(home);
        string path = PathIn(home), tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>The class on at this moment: from <paramref name="early"/> before it starts until it ends. If two
    /// overlap, the one that started last (you walked into the second).</summary>
    public ClassNow? Now(DateTime local, TimeSpan? early = null)
    {
        var lead = early ?? TimeSpan.FromMinutes(10);
        ClassNow? best = null;
        DateTime bestStart = DateTime.MinValue;
        foreach (var c in Classes)
        {
            foreach (var t in c.Times)
            {
                if (t.Day != local.DayOfWeek) continue;
                DateTime start = local.Date + t.Start.ToTimeSpan(), end = local.Date + t.End.ToTimeSpan();
                if (local >= start - lead && local <= end && start > bestStart)
                {
                    best = new ClassNow(c.Name, t);
                    bestStart = start;
                }
            }
        }
        return best;
    }

    /// <summary>The next class to start after this moment, within a week.</summary>
    public (ClassNow Class, DateTime Starts)? Next(DateTime local)
    {
        (ClassNow, DateTime)? best = null;
        foreach (var c in Classes)
        {
            foreach (var t in c.Times)
            {
                int days = ((int)t.Day - (int)local.DayOfWeek + 7) % 7;
                DateTime start = local.Date.AddDays(days) + t.Start.ToTimeSpan();
                if (start <= local) start = start.AddDays(7);
                if (best is null || start < best.Value.Item2) best = (new ClassNow(c.Name, t), start);
            }
        }
        return best;
    }

    /// <summary>Keep the timetable to the library's classes: a class renamed or removed there drops out here.</summary>
    public bool KeepOnly(IReadOnlyCollection<string> classNames)
    {
        int before = Classes.Count;
        Classes = Classes.Where(c => classNames.Contains(c.Name)).ToList();
        return Classes.Count != before;
    }
}

/// <summary>Each class's dot: the same color on every screen and computer, from the class's place in the library.</summary>
public static class ClassColors
{
    /// <summary>OKLCH hues at one lightness and chroma, so no class shouts: blue, green, violet, amber, then the rest.</summary>
    public static readonly (double L, double C, double H)[] Palette =
    [
        (0.62, 0.14, 250), (0.64, 0.14, 155), (0.60, 0.14, 295), (0.70, 0.13, 70),
        (0.63, 0.14, 20), (0.66, 0.12, 200), (0.62, 0.14, 330), (0.66, 0.13, 120),
    ];

    public static (double L, double C, double H) For(int index) => Palette[((index % Palette.Length) + Palette.Length) % Palette.Length];

    /// <summary>OKLCH to sRGB, clipped: "#4F86D9".</summary>
    public static string Hex((double L, double C, double H) c)
    {
        double hr = c.H * Math.PI / 180, a = c.C * Math.Cos(hr), b = c.C * Math.Sin(hr);
        double l_ = c.L + 0.3963377774 * a + 0.2158037573 * b;
        double m_ = c.L - 0.1055613458 * a - 0.0638541728 * b;
        double s_ = c.L - 0.0894841775 * a - 1.2914855480 * b;
        double l = l_ * l_ * l_, m = m_ * m_ * m_, s = s_ * s_ * s_;
        double r = 4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s;
        double g = -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s;
        double bl = -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s;
        static int Gamma(double x)
        {
            x = Math.Clamp(x, 0, 1);
            double v = x <= 0.0031308 ? 12.92 * x : 1.055 * Math.Pow(x, 1 / 2.4) - 0.055;
            return (int)Math.Round(v * 255);
        }
        return string.Create(CultureInfo.InvariantCulture, $"#{Gamma(r):X2}{Gamma(g):X2}{Gamma(bl):X2}");
    }
}
