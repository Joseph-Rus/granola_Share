using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StudyStash.App.Services;

namespace StudyStash.App.ViewModels;

/// <summary>Which of Canvas's eight moments (design 08) is showing right now. "Updated" isn't a status the library
/// sends: it's "connected" with an extension update still to tell the student about.</summary>
public enum CanvasStateKind
{
    NotSetUp,
    NoExtension,
    ChromeAway,
    SignedOut,
    Syncing,
    Connected,
    Updated,
    Error,
}

/// <summary>Which colour a state reads as: Windows picks its InfoBar by this; the Mac picks its icon colour (Info
/// reads as <c>Fg2</c>, Error as <c>Accent</c> — the design has no separate Mac "error" colour).</summary>
public enum CanvasTone
{
    Info,
    Ok,
    Warn,
    Error,
}

/// <summary>
/// One Canvas state (design 08): its title, text, icon and button, shaped from <see cref="CanvasApi.State"/>. Shown
/// standing alone (e.g. atop the Due list) or, when connected or syncing, as Settings' compact header line
/// (<see cref="IsCompact"/>/<see cref="CompactLine"/>). <see cref="Act"/> dispatches its button the way each state's
/// does: Connect and Show me how call back to the host (they open the T5 connect flow); the rest talk to the
/// library directly. <see cref="Close"/> only hides the card for the session — nothing is saved.
/// </summary>
public sealed partial class CanvasStatusModel(CanvasContext context) : ObservableObject
{
    CanvasApi.State state = new();

    [NotifyPropertyChangedFor(nameof(IsSyncing), nameof(IsCompact))]
    [ObservableProperty]
    public partial CanvasStateKind Kind { get; set; }

    [ObservableProperty] public partial string Title { get; set; } = "";
    [ObservableProperty] public partial string Text { get; set; } = "";
    [ObservableProperty] public partial string Glyph { get; set; } = "";

    [NotifyPropertyChangedFor(nameof(IsInfo), nameof(IsOk), nameof(IsWarn), nameof(IsError))]
    [ObservableProperty]
    public partial CanvasTone Tone { get; set; }

    [NotifyPropertyChangedFor(nameof(HasAction), nameof(ShowWinButton))]
    [ObservableProperty]
    public partial string? ActionLabel { get; set; }

    [ObservableProperty] public partial bool IsPrimary { get; set; }

    [NotifyPropertyChangedFor(nameof(ShowWinButton))]
    [ObservableProperty]
    public partial bool IsPlain { get; set; }

    [ObservableProperty] public partial bool CanClose { get; set; }
    [ObservableProperty] public partial bool IsVisible { get; set; } = true;

    [NotifyPropertyChangedFor(nameof(HasProgress))]
    [ObservableProperty]
    public partial double? Progress { get; set; }

    [ObservableProperty] public partial string CompactLine { get; set; } = "";

    public bool IsSyncing => Kind == CanvasStateKind.Syncing;
    /// <summary>Connected or syncing show as Settings' one-line header instead of the full card.</summary>
    public bool IsCompact => Kind is CanvasStateKind.Connected or CanvasStateKind.Syncing;
    public bool IsInfo => Tone == CanvasTone.Info;
    public bool IsOk => Tone == CanvasTone.Ok;
    public bool IsWarn => Tone == CanvasTone.Warn;
    public bool IsError => Tone == CanvasTone.Error;
    public bool HasAction => !string.IsNullOrEmpty(ActionLabel);
    public bool HasProgress => Progress is not null;
    /// <summary>Windows shows a button for every state but "Updated" (the design gives that one no button, and no
    /// close — the extension notice clears itself next time Canvas syncs).</summary>
    public bool ShowWinButton => HasAction && !IsPlain;

    /// <summary>Shapes the card from the library's state: the words from <see cref="CanvasWords.Describe"/>, the
    /// icon/tone/button from design 08's table.</summary>
    public void Show(CanvasApi.State s)
    {
        state = s;
        var zone = context.Clock.Zone;
        var words = CanvasWords.Describe(s, zone);
        Title = words.Title;
        Text = words.Text;
        bool updated = s.Status == "connected" && s.Extension?.Updated is not null;
        Kind = s.Status switch
        {
            "not_set_up" => CanvasStateKind.NotSetUp,
            "no_extension" => CanvasStateKind.NoExtension,
            "chrome_away" => CanvasStateKind.ChromeAway,
            "signed_out" => CanvasStateKind.SignedOut,
            "syncing" => CanvasStateKind.Syncing,
            "error" => CanvasStateKind.Error,
            _ => updated ? CanvasStateKind.Updated : CanvasStateKind.Connected,
        };
        (Glyph, Tone, ActionLabel, IsPrimary, IsPlain, CanClose) = Kind switch
        {
            CanvasStateKind.NotSetUp => ("link_off", CanvasTone.Info, "Connect", true, false, true),
            CanvasStateKind.NoExtension => ("extension", CanvasTone.Warn, "Show me how", true, false, true),
            CanvasStateKind.ChromeAway => ("schedule", CanvasTone.Warn, "Open Chrome", false, false, true),
            CanvasStateKind.SignedOut => ("lock", CanvasTone.Warn, "Open Canvas", false, false, true),
            CanvasStateKind.Syncing => ("", CanvasTone.Info, null, false, false, true),
            CanvasStateKind.Connected => ("check_circle", CanvasTone.Ok, "Sync now", false, false, false),
            CanvasStateKind.Updated => ("new_releases", CanvasTone.Info, "Dismiss", false, true, false),
            CanvasStateKind.Error => ("error", CanvasTone.Error, "Try now", false, false, true),
            _ => ("", CanvasTone.Info, null, false, false, false),
        };
        Progress = Kind == CanvasStateKind.Syncing && s.Syncing is { Total: > 0 } sy ? (double)(sy.Total - sy.Left) / sy.Total : null;
        CompactLine = Kind == CanvasStateKind.Connected ? CanvasWords.ConnectedSummary(s.LastSync, zone) : Title;
        IsVisible = true;
    }

    /// <summary>Connect opens Settings' Connection flow; Show me how opens the same flow at its Chrome step (both
    /// T5's connect control — this task only calls back to whichever host holds it).</summary>
    public Action? OnConnect { get; set; }
    public Action? OnShowMeHow { get; set; }

    [RelayCommand]
    async Task Act()
    {
        switch (Kind)
        {
            case CanvasStateKind.NotSetUp:
                OnConnect?.Invoke();
                break;
            case CanvasStateKind.NoExtension:
                OnShowMeHow?.Invoke();
                break;
            case CanvasStateKind.ChromeAway:
                context.Actions.OpenChrome();
                break;
            case CanvasStateKind.SignedOut:
                context.Actions.OpenInChrome(state.Url);
                break;
            case CanvasStateKind.Connected:
            case CanvasStateKind.Error:
                if (context.Client is { } syncClient) await syncClient.SaveAsync(sync: true);
                break;
            case CanvasStateKind.Updated:
                if (context.Client is { } dismissClient) await dismissClient.SaveAsync(dismissUpdate: true);
                break;
        }
    }

    [RelayCommand]
    void Close() => IsVisible = false;
}
