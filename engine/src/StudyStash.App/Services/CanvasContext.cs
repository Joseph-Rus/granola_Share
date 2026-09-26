using Avalonia.Media;
using StudyStash.Core;

namespace StudyStash.App.Services;

/// <summary>"Now", in the zone the student reads times in. Real code reads the system clock; tests fix it to the
/// design's moment so no assertion depends on when it runs.</summary>
public sealed record CanvasClock(Func<DateTimeOffset> Now, TimeZoneInfo Zone);

/// <summary>Everything a Canvas view model does to the machine, as delegates: real code launches Chrome and the
/// Finder/Explorer; tests just record what was asked for and launch nothing.</summary>
public sealed record CanvasActions(
    Action<string> OpenUrl,
    Action<string> OpenInChrome,
    Action OpenChrome,
    Action OpenChromeExtensions,
    Action<string> RevealFolder,
    Action<string> OpenFile,
    Func<string, string, string> PrepareExtension);

/// <summary>What every Canvas view model reads: the library's Canvas client (null before the laptop is paired with
/// one), the clock, a class's dot colour, and the actions above. Tests build one whole so nothing a view model does
/// ever depends on the real machine or the real time.</summary>
public sealed record CanvasContext(CanvasClient? Client, CanvasClock Clock, Func<string, IBrush> DotOf, CanvasActions Actions, string Home)
{
    /// <summary>The real context: a client built from the app's own library connection (null when the laptop isn't
    /// connected to one), the system clock, and actions that really launch Chrome and reveal files.</summary>
    public static CanvasContext For(AppHost host)
    {
        var cc = host.Client();
        var client = cc.ServerUrl.Length > 0 ? new CanvasClient(cc.ServerUrl, cc.PoolKey) : null;
        var actions = new CanvasActions(
            OpenUrl: url => Dialogs.OpenUrl(url),
            OpenInChrome: OpenInChrome,
            OpenChrome: () => OpenInChrome(null),
            OpenChromeExtensions: () => OpenInChrome("chrome://extensions"),
            RevealFolder: dir => Machine.Open(dir),
            OpenFile: path => Machine.Open(path),
            PrepareExtension: (key, canvasUrl) => Core.Canvas.Extension.Prepare(Core.Canvas.Extension.Folder(host.Home), cc.ServerUrl, key, canvasUrl));
        return new CanvasContext(client, new CanvasClock(() => DateTimeOffset.Now, TimeZoneInfo.Local), cls => Skin.ClassDot(host.ColorOf(cls)), actions, host.Home);
    }

    static void OpenInChrome(string? url)
    {
        try
        {
            if (OperatingSystem.IsMacOS()) Machine.Run("open", url is null ? ["-a", "Google Chrome"] : ["-a", "Google Chrome", url], TimeSpan.FromSeconds(10));
            else if (OperatingSystem.IsWindows()) Machine.Run("cmd", url is null ? ["/c", "start", "chrome"] : ["/c", "start", "chrome", url], TimeSpan.FromSeconds(10));
            else if (url is not null) Dialogs.OpenUrl(url);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
    }
}
