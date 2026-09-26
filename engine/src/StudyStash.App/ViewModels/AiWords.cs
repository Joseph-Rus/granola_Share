using System.Globalization;

namespace StudyStash.App.ViewModels;

/// <summary>
/// Every piece of copy the AI screens derive rather than take literally from the design: an engine's state as a
/// word, which colour its dot is, what its row's button says, the icon for each engine, and the times the design
/// writes as "Tue 11:52", "Used 10:40" or "3:00 PM". Pure functions, so every AI view model reads from here instead
/// of repeating a switch.
/// </summary>
public static class AiWords
{
    /// <summary>An engine's icon (memory/terminal/code/auto_awesome), by id.</summary>
    public static string EngineIcon(string id) => id switch
    {
        "ollama" => "memory",
        "claude" => "terminal",
        "codex" => "code",
        "gemini" => "auto_awesome",
        _ => "memory",
    };

    /// <summary>The word a state shows next to its dot: ready Ready · unchecked Installed · not_signed_in Not
    /// signed in · not_running Not running · model_missing Needs its model · limited Limit reached ·
    /// not_installed Not installed · failed Didn't work.</summary>
    public static string EngineStateWords(string state) => state switch
    {
        "ready" => "Ready",
        "unchecked" => "Installed",
        "not_signed_in" => "Not signed in",
        "not_running" => "Not running",
        "model_missing" => "Needs its model",
        "limited" => "Limit reached",
        "not_installed" => "Not installed",
        "failed" => "Didn't work",
        _ => state,
    };

    public static bool StateIsGood(string state) => state == "ready";
    public static bool StateIsWarn(string state) => state is "not_signed_in" or "not_running" or "model_missing" or "limited" or "failed";
    public static bool StateIsMuted(string state) => state is "unchecked" or "not_installed";

    /// <summary>The row's button: ready → Options · unchecked → Check · not_signed_in → Sign in (tinted) ·
    /// not_running → Start · model_missing → Download · not_installed → Get it · limited/failed → Options.</summary>
    public static string RowActionWords(string state) => state switch
    {
        "unchecked" => "Check",
        "not_signed_in" => "Sign in",
        "not_running" => "Start",
        "model_missing" => "Download",
        "not_installed" => "Get it",
        _ => "Options",
    };

    /// <summary>Only "Sign in" is the tinted, accent-coloured button; every other row action is a plain pill.</summary>
    public static bool RowActionPrimary(string state) => state == "not_signed_in";

    /// <summary>Whether the row's button opens the Options menu (its models, and Check it works) instead of acting
    /// straight away.</summary>
    public static bool NeedsOptions(string state) => state is "ready" or "limited" or "failed";

    /// <summary>The AI engines pane's row subtitle: Ollama always says nothing leaves it; a ready CLI says it's
    /// signed in; every other CLI just says where it runs.</summary>
    public static string EngineAbout(string id, string state) =>
        id == "ollama" ? "Runs on your library. Nothing leaves it."
        : state == "ready" ? "Runs on your library. Signed in."
        : "Runs on your library.";

    /// <summary>The library setup step's row subtitle: phrased for the computer you're sitting at, since in setup
    /// the library is this computer.</summary>
    public static string SetupAbout(string id, string state) => id == "ollama"
        ? "Private. Runs here, nothing leaves this computer."
        : state switch
        {
            "ready" => "Signed in on this computer.",
            "not_signed_in" => "Sign in first.",
            "unchecked" => "Installed on this computer.",
            _ => "",
        };

    /// <summary>"Written by Ollama · Tue 11:52": the notes' byline (engine name, then the design's "ddd H:mm").</summary>
    public static string Byline(string engineName, DateTime at) => $"{engineName} · {at:ddd H:mm}";

    /// <summary>A connected tool's last-used words: "Used 10:40" today, "Used Tue" this week, else "Used 12 Sep".</summary>
    public static string UsedWords(DateTime at, DateTime now)
    {
        if (at.Date == now.Date) return $"Used {at:H:mm}";
        if (now - at < TimeSpan.FromDays(7)) return $"Used {at:ddd}";
        return $"Used {at:d MMM}";
    }

    /// <summary>A usage limit's "3:00 PM", the design's h:mm tt.</summary>
    public static string UntilClock(DateTime until) => until.ToString("h:mm tt", CultureInfo.InvariantCulture);

    /// <summary>Parses the ISO-ish time `AiOverview`/`AiProblemInfo` carry ("Until"), or null when it's empty or
    /// unreadable.</summary>
    public static DateTime? ParseUntil(string iso) =>
        DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) ? t : null;

    public const string OlderLibraryWords = "Your library runs an older Study Stash: update it to pick engines here.";
}
