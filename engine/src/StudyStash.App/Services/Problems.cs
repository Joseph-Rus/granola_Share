using StudyStash.Audio;
using StudyStash.Core;

namespace StudyStash.App.Services;

/// <summary>Which problem the app is showing (so a button or command knows what to do about it).</summary>
public enum ProblemKind
{
    DiskFull,
    MicDenied,
    NoMic,
    WhisperFailed,
    DownloadFailed,
    NoModel,
    Downloading,
    LibraryStopped,
    WrongPassword,
    Unreachable,
    NotSetUp,
}

/// <summary>One thing the student might need to know or do, in plain words: a title, why, and (if there's something
/// to click) a label for it.</summary>
public sealed record AppProblem(ProblemKind Kind, string Title, string Detail, string ActionLabel)
{
    public bool HasAction => ActionLabel.Length > 0;
}

/// <summary>What <see cref="Problems"/> needs to know to decide what's wrong: <see cref="AppHost"/>'s own read-only
/// state, so a test can hand it a fake without driving the recorder, Whisper or a real library to get there.</summary>
public interface IProblemSource
{
    string? RecorderProblem { get; }
    MicAccess MicAccess();
    string? WhisperProblem { get; }
    string? DownloadProblem { get; }
    bool ModelReady { get; }
    DownloadProgress? Downloading { get; }
    AppRole Role { get; }
    LibraryServiceState? LocalLibraryState { get; }
    string? LocalLibraryFailure { get; }
    LibraryState Library { get; }
}

/// <summary>
/// The one place that decides what's wrong right now, in priority order: whichever stops a lecture from being
/// recorded or written down first, then whatever keeps the library from being reached. The dropdown, the recorder,
/// toasts, the library window and Settings all read this instead of re-deriving it themselves.
/// </summary>
public static class Problems
{
    /// <summary>Strips "Whisper couldn't start: " off <see cref="AppHost.WhisperProblem"/> so the detail doesn't repeat
    /// the title.</summary>
    static string WithoutPrefix(string text, string prefix) =>
        text.StartsWith(prefix, StringComparison.Ordinal) ? text[prefix.Length..].Trim() : text;

    public static AppProblem? For(IProblemSource host)
    {
        string device = OperatingSystem.IsMacOS() ? "Mac" : "PC";

        if (host.RecorderProblem == RecordingWords.DiskFull)
            return new(ProblemKind.DiskFull, "Your disk is full", "Free some space to record.", "");

        var mic = host.MicAccess();
        if (mic is MicAccess.Denied or MicAccess.Restricted)
            return OperatingSystem.IsMacOS()
                ? new(ProblemKind.MicDenied, "Study Stash can't use the microphone",
                    "Turn it on in System Settings → Privacy & Security → Microphone.", "Open System Settings")
                : new(ProblemKind.MicDenied, "Study Stash can't use the microphone", RecordingWords.CantHearWindows, "Open Settings");

        if (host.RecorderProblem == RecordingWords.MicStopped)
            return new(ProblemKind.NoMic, "No microphone", "Plug one in, or check the sound settings.", "");

        if (host.WhisperProblem is { } whisper)
            return new(ProblemKind.WhisperFailed, "Whisper couldn't start", WithoutPrefix(whisper, "Whisper couldn't start: "), "Download again");

        if (host.DownloadProblem is { } download)
            return new(ProblemKind.DownloadFailed, "The download stopped", download, "Try again");

        if (!host.ModelReady && host.Downloading is null)
            return new(ProblemKind.NoModel, "Download the transcription model", "Recording starts once it's here, about 3 GB.", "Download");

        if (!host.ModelReady && host.Downloading is { } d)
            return new(ProblemKind.Downloading, "Downloading the transcription model",
                $"{d.Amount}" + (d.Left() is { } left ? $" · {left}" : ""), "");

        if (host.Role != AppRole.Laptop && host.LocalLibraryState is LibraryServiceState.Stopped or LibraryServiceState.Failed or LibraryServiceState.PortTaken)
            return new(ProblemKind.LibraryStopped, "Your library isn't running",
                host.LocalLibraryFailure ?? "Start it to keep recording lectures.", "Start it");

        if (host.Library == LibraryState.WrongPassword)
            return new(ProblemKind.WrongPassword, "Library password changed", "Type the new one in Settings to keep sending lectures.", "Settings");

        if (host.Library == LibraryState.Unreachable)
            return new(ProblemKind.Unreachable, "Can't reach your library", $"Lectures you record wait on this {device} and go when it's back.", "");

        if (host.Library == LibraryState.NotSetUp)
            return new(ProblemKind.NotSetUp, "No library yet", "Connect to your library in Settings.", "Settings");

        return null;
    }
}
