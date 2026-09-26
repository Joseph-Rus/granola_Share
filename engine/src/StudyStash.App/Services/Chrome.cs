using StudyStash.Core;

namespace StudyStash.App.Services;

/// <summary>Opens Google Chrome, a URL inside it, and Chrome's own extensions page — the three things the Canvas
/// connect flow (design 07) asks of Chrome, each through one <see cref="Machine.Run"/> call, so a computer with no
/// Chrome installed reads back as one plain sentence instead of a swallowed exception. Tests hand in their own
/// <see cref="Runner"/> instead of ever starting a real browser.</summary>
public static class Chrome
{
    public const string NotInstalled = "Study Stash reads Canvas through Google Chrome. Install it, then try again.";

    /// <summary>Opens Chrome, or a URL inside it when one is given. Null on success; <see cref="NotInstalled"/> when
    /// nothing could be started.</summary>
    public static string? Open(string? url, Runner? run = null)
    {
        var runner = run ?? Machine.Run;
        var started =
            OperatingSystem.IsMacOS() ? runner("open", url is null ? ["-a", "Google Chrome"] : ["-a", "Google Chrome", url], TimeSpan.FromSeconds(10)) :
            OperatingSystem.IsWindows() ? runner("cmd", url is null ? ["/c", "start", "chrome"] : ["/c", "start", "chrome", url], TimeSpan.FromSeconds(10)) :
            null;
        return started is null ? NotInstalled : null;
    }

    /// <summary>Opens Chrome's own <c>chrome://extensions</c> page, where "Load unpacked" picks up the folder
    /// <see cref="StudyStash.Core.Canvas.Extension"/> just wrote.</summary>
    public static string? OpenExtensions(Runner? run = null) => Open("chrome://extensions", run);
}
