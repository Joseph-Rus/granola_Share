using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using StudyStash.App.Platform;
using StudyStash.App.Services;
using StudyStash.App.ViewModels;
using StudyStash.App.Views;
using StudyStash.App.Windows;
using StudyStash.Audio;
using StudyStash.Core;

namespace StudyStash.App;

/// <summary>
/// The running app: the menu bar (or tray) icon and the windows it opens, the shortcuts, and the models the windows
/// show, kept up to date from the <see cref="AppHost"/>.
/// </summary>
public static partial class Shell
{
    static App app = null!;
    static IClassicDesktopStyleApplicationLifetime life = null!;
    static AppHost host = null!;
    static TrayIcon? tray;
    static bool trayRecording;
    static readonly CancellationTokenSource stop = new();

    static readonly PanelModel panel = new();
    static readonly RecorderModel recorder = new();
    static readonly QuickModel quick = new();
    static readonly LibraryModel library = new();
    static SetupModel? setup;

    static Floating? panelWindow, recorderWindow, quickWindow;
    static Window? mainWindow, setupWindow, settingsWindow;
    static DispatcherTimer? ticker;

    /// <summary>The class Record will use, picked by hand; null follows the timetable.</summary>
    static string? chosenClass;
    static string? liveId;

    public static AppHost Host => host;

    public static void Start(App application, IClassicDesktopStyleApplicationLifetime desktop)
    {
        app = application;
        life = desktop;
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        string home = Program.Home;
        Func<IAudioSource>? mic = Environment.GetEnvironmentVariable("STUDYSTASH_MIC_FILE") is { Length: > 0 } wav && File.Exists(wav)
            ? () => new FileMicrophone(wav) : null;
        host = new AppHost(home, mic, laptop: new LaptopHost(), log: Program.Log);
        Skin.UseTheme(ColourThemes.Find(host.Settings.Theme));
        host.Changed += () => Dispatcher.UIThread.Post(Refresh);
        host.Heard += (l, lines) => Dispatcher.UIThread.Post(() => AddHeard(l, lines));
        host.Filed += l => Dispatcher.UIThread.Post(() => Toast($"Filed in {(l.FiledClass.Length > 0 ? l.FiledClass : "your library")}",
            l.FiledTitle.Length > 0 ? l.FiledTitle : "The notes are written.", "Open note", () => OpenLecture(l.Id)));
        host.Problem += why => Dispatcher.UIThread.Post(() => Toast("Recording paused", why, null, null));
        Wire();
        host.Start();
        MakeTray();
        if (host.Settings.Shortcuts && !Hotkeys.Register(OnShortcut)) Program.Log("[app] the shortcuts are taken by another app");
        Desktop.Listen(home, OnHandOff, stop.Token);
        ticker = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => Tick());
        ticker.Start();
        Refresh();
        desktop.ShutdownRequested += (_, _) => Quit();
        if (!host.Settings.SetupDone) ShowSetup();
        else if (!Program.Background) ShowLibrary();
        if (SelfTest.Dir is not null) SelfTest.Run();
    }

    /// <summary>The windows, for the self-test.</summary>
    public static class Windows
    {
        public static Window? Setup => setupWindow;
        public static Window? Main => mainWindow;
        public static Window? Panel => panelWindow;
        public static Window? Quick => quickWindow;
        public static Window? Recorder => recorderWindow;
        public static Window? Settings => settingsWindow;
        public static void TogglePanel() => Shell.TogglePanel();
        public static void ToggleQuick() => Shell.ToggleQuick();
        public static void Record() => ToggleRecording();
        public static void StopRecording() => Shell.StopRecording();
        public static void ShowRecorder(bool expanded) => Shell.ShowRecorder(expanded);
    }

    static void OnHandOff(string message)
    {
        if (message == "record") ToggleRecording();
        else if (!host.Settings.SetupDone) ShowSetup();
        else ShowLibrary();
    }

    static void OnShortcut(Shortcut s)
    {
        if (s == Shortcut.Quick) ToggleQuick();
        else ToggleRecording();
    }

    public static void Quit()
    {
        stop.Cancel();
        host.Dispose();
        tray?.Dispose();
        life.Shutdown();
    }

    // --- the tray ---------------------------------------------------------------------------------------------------

    /// <summary>The icon: the waveform glyph (a template image on a Mac, which the menu bar tints), with a red dot
    /// while recording.</summary>
    static WindowIcon TrayImage(bool recording)
    {
        const int size = 44;
        var bmp = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        using (var ctx = bmp.CreateDrawingContext())
        {
            IBrush ink = OperatingSystem.IsMacOS() ? Brushes.Black : Application.Current?.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Light ? Brushes.Black : Brushes.White;
            if (Controls.Icon.Find("graphic_eq", false) is { } g)
                using (ctx.PushTransform(Matrix.CreateScale(size / 24.0, size / 24.0)))
                    ctx.DrawGeometry(ink, null, g);
            if (recording) ctx.DrawEllipse(new SolidColorBrush(Color.Parse("#E5484D")), null, new Point(size - 9, size - 9), 8, 8);
        }
        var stream = new MemoryStream();
        bmp.Save(stream, PngBitmapEncoderOptions.Default);
        stream.Position = 0;
        return new WindowIcon(stream);
    }

    static void MakeTray()
    {
        tray = new TrayIcon { Icon = TrayImage(false), ToolTipText = "Study Stash", IsVisible = true };
        if (OperatingSystem.IsMacOS()) MacOSProperties.SetIsTemplateIcon(tray, true);
        tray.Clicked += (_, _) => TogglePanel();
        var menu = new NativeMenu();
        void Item(string title, Action act)
        {
            var i = new NativeMenuItem(title);
            i.Click += (_, _) => act();
            menu.Add(i);
        }
        // Windows opens the flyout on a click; this menu is the right click (and a Mac's fallback).
        Item("Record", ToggleRecording);
        Item("Search notes and lectures", ToggleQuick);
        Item("Open Study Stash", ShowLibrary);
        Item("Settings…", ShowSettings);
        menu.Add(new NativeMenuItemSeparator());
        Item("Quit Study Stash", Quit);
        if (OperatingSystem.IsWindows()) tray.Menu = menu;
        TrayIcon.SetIcons(app, new TrayIcons { tray });
    }

    // --- what the buttons do ---------------------------------------------------------------------------------------

    static void Wire()
    {
        panel.OnRecord = ToggleRecording;
        panel.OnStop = () => StopRecording();
        panel.OnPause = TogglePause;
        panel.OnShowRecorder = () => ShowRecorder(expanded: true);
        panel.OnSearch = () =>
        {
            panelWindow?.Hide();
            ToggleQuick();
        };
        panel.OnOpenApp = () =>
        {
            panelWindow?.Hide();
            ShowLibrary();
        };
        panel.OnSwitchClass = PickClass;
        panel.OnOpenLecture = item =>
        {
            panelWindow?.Hide();
            if (item.Id == liveId) ShowRecorder(expanded: true);
            else OpenLecture(item.Id);
        };

        recorder.OnPause = TogglePause;
        recorder.OnStop = () => StopRecording();
        recorder.OnExpand = expanded => PlaceRecorder();
        recorder.OnAsk = AskLive;
        recorder.OnPlay = chip => Play(chip.LectureId ?? liveId, chip.At);

        quick.OnQuery = q => _ = SearchAsync(q);
        quick.OnAsk = AskQuick;
        quick.OnOpen = OpenQuickRow;
        quick.OnClose = () => quickWindow?.Hide();

        library.OnCloseAnswer = () => chatId = null;
        library.OnClass = c => _ = c.IsDue ? ShowDueAsync() : ShowClassAsync(c.Name);
        library.OnLecture = l => _ = ShowLectureAsync(l.Id);
        library.OnAsk = AskLibrary;
        library.OnSearch = ToggleQuick;
        library.OnSettings = ShowSettings;
        library.OnMove = MoveLecture;
        library.OnExport = () => _ = ExportAsync();
        library.OnScope = CycleScope;
        library.OnMore = MoreMenu;
        library.OnSource = chip => Play(chip.LectureId, chip.At);
    }

    // --- recording ----------------------------------------------------------------------------------------------------

    static string RecordClass() => chosenClass ?? host.ClassNow()?.Name ?? "";

    static void ToggleRecording()
    {
        if (host.Recorder.Current is not null)
        {
            StopRecording();
            return;
        }
        if (!host.ModelReady)
        {
            Toast("The model isn't downloaded yet", host.Downloading is not null ? "It's downloading: Record works once it's done." : "Download it in Settings → Recording.",
                "Settings", ShowSettings);
            return;
        }
        try
        {
            var l = host.StartRecording(RecordClass());
            liveId = l.Id;
            recorder.Lines.Clear();
            recorder.Chat.Clear();
            recorder.Waiting = "What's said shows here a few seconds after it's said.";
            panelWindow?.Hide();
            ShowRecorder(expanded: false);
        }
        catch (Exception e) when (e is InvalidOperationException or PlatformNotSupportedException)
        {
            Toast("Couldn't start recording", e.Message, null, null);
        }
        Refresh();
    }

    static void TogglePause()
    {
        if (host.Recorder.Current is not { } l) return;
        if (l.State == LectureState.Paused) host.Resume();
        else host.Pause();
        Refresh();
    }

    static void StopRecording()
    {
        var l = host.StopRecording();
        chosenClass = null;
        recorderWindow?.Hide();
        if (l is not null) Toast("Recording saved", "Study Stash is writing it down; the library files it and writes your notes.", null, null);
        Refresh();
    }

    static void PickClass()
    {
        var menu = new ContextMenu();
        var classes = host.Classes();
        foreach (var (name, color, _) in classes)
        {
            var item = new MenuItem { Header = name, Icon = new Avalonia.Controls.Shapes.Ellipse { Width = 8, Height = 8, Fill = Skin.ClassDot(color) } };
            item.Click += (_, _) =>
            {
                chosenClass = name;
                Refresh();
            };
            menu.Items.Add(item);
        }
        if (classes.Count > 0) menu.Items.Add(new Separator());
        var sort = new MenuItem { Header = "Let the library sort it" };
        sort.Click += (_, _) =>
        {
            chosenClass = "";
            Refresh();
        };
        menu.Items.Add(sort);
        var follow = new MenuItem { Header = "Follow my timetable", IsEnabled = chosenClass is not null };
        follow.Click += (_, _) =>
        {
            chosenClass = null;
            Refresh();
        };
        menu.Items.Add(follow);
        if (panelWindow?.Content is Control c) menu.Open(c);
    }

    static void AddHeard(Lecture l, IReadOnlyList<Spoken> lines)
    {
        if (l.Id != liveId) return;
        foreach (var old in recorder.Lines.Where(x => x.Latest).ToList())
            recorder.Lines[recorder.Lines.IndexOf(old)] = new HeardLine { Time = old.Time, Text = old.Text };
        for (int i = 0; i < lines.Count; i++)
            recorder.Lines.Add(new HeardLine { Time = TimedText.Clock(lines[i].Start), Text = lines[i].Text, Latest = i == lines.Count - 1 });
        while (recorder.Lines.Count > 200) recorder.Lines.RemoveAt(0);
        panel.LastLine = $"“…{Trim(lines[^1].Text, 90)}”";
    }

    static string Trim(string s, int n) => s.Length <= n ? s : s[..n].TrimEnd() + "…";

    static async Task AskLive(string question)
    {
        recorder.Chat.Add(new ChatMessage { Mine = true, Text = question });
        var answer = new ChatMessage { Thinking = true };
        recorder.Chat.Add(answer);
        while (recorder.Chat.Count > 6) recorder.Chat.RemoveAt(0);
        var live = host.Recorder.Current;
        await Answer(answer, lib => lib.AskAsync(question, className: null, live: live?.Transcript(), liveTitle: $"{(live?.ClassName is { Length: > 0 } c ? c : "This lecture")}, now"));
    }

    static async Task Answer(ChatMessage into, Func<RemoteLibrary, Task<JsonObject>> ask)
    {
        if (host.Remote() is not { } lib)
        {
            into.Thinking = false;
            into.Text = "Connect to your library first (Settings → Library).";
            return;
        }
        try
        {
            var r = await ask(lib);
            into.Text = r["answer"]?.GetValue<string>() ?? "";
            foreach (var s in (r["sources"] as JsonArray ?? []).OfType<JsonObject>())
                if (s["at"] is JsonValue v && v.TryGetValue(out double at))
                    into.Sources.Add(new SourceChip { Label = TimedText.Clock(at), At = at, LectureId = s["id"]?.GetValue<string>() });
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or LibraryRefusedException)
        {
            into.Text = e is LibraryRefusedException r ? r.Message : "Your library didn't answer. Is it on?";
        }
        into.Thinking = false;
    }

    // --- the windows -----------------------------------------------------------------------------------------------

    static Control PanelView() => Skin.Current == SkinKind.Mac ? new MacPanel { DataContext = panel } : new WinPanel { DataContext = panel };

    static void TogglePanel()
    {
        if (panelWindow?.IsVisible == true)
        {
            panelWindow.Hide();
            return;
        }
        panelWindow ??= new Floating { Content = PanelView(), CloseOnDeactivate = true, Title = "Study Stash" };
        Refresh();
        var (area, scale) = panelWindow.WorkArea(Floating.Pointer());
        var size = panelWindow.Measured(scale);
        int room = (int)(Floating.ShadowRoom * scale);
        if (OperatingSystem.IsMacOS())
        {
            // Below the menu bar, under the icon that was clicked.
            int x = (Floating.Pointer()?.X is int px ? (int)(px * scale) : area.Right - size.Width) - (int)(24 * scale) - room;
            panelWindow.Position = new PixelPoint(Math.Clamp(x, area.X, area.Right - size.Width), area.Y + (int)(6 * scale) - room);
        }
        else
        {
            // 12 px above the tray, like Quick Settings.
            panelWindow.Position = new PixelPoint(area.Right - size.Width - (int)(12 * scale) + room, area.Bottom - size.Height - (int)(12 * scale) + room);
        }
        panelWindow.Show();
        panelWindow.Activate();
        Desktop.Activate();
    }

    static void ShowRecorder(bool expanded)
    {
        recorder.Expanded = expanded;
        recorderWindow ??= MakeRecorderWindow();
        PlaceRecorder();
        recorderWindow.Show();
    }

    static Floating MakeRecorderWindow()
    {
        var view = Skin.Current == SkinKind.Mac ? (Control)new MacRecorder { DataContext = recorder } : new WinRecorder { DataContext = recorder };
        var w = new Floating { Content = view, Title = "Study Stash recorder" };
        // Drag it anywhere by its background; it remembers where.
        view.PointerPressed += (_, e) =>
        {
            if (e.Source is TextBox || !e.GetCurrentPoint(view).Properties.IsLeftButtonPressed) return;
            w.BeginMoveDrag(e);
        };
        w.PositionChanged += (_, _) =>
        {
            if (!w.IsVisible) return;
            var (_, scale) = w.WorkArea(w.Position);
            var size = w.Measured(scale);
            host.Settings.RecorderX = w.Position.X + size.Width;
            host.Settings.RecorderY = w.Position.Y;
        };
        w.Closing += (_, e) =>
        {
            e.Cancel = true;
            w.Hide();
        };
        return w;
    }

    /// <summary>The recorder keeps its top right corner where you left it (a corner by default) as it grows and shrinks.</summary>
    static void PlaceRecorder()
    {
        if (recorderWindow is null) return;
        var (area, scale) = recorderWindow.WorkArea();
        var size = recorderWindow.Measured(scale);
        int room = (int)(Floating.ShadowRoom * scale);
        int right = host.Settings.RecorderX is double rx ? (int)rx : area.Right - (int)(16 * scale) + room;
        int top = host.Settings.RecorderY is double ry ? (int)ry
            : OperatingSystem.IsMacOS() ? area.Y + (int)(10 * scale) - room : area.Bottom - size.Height - (int)(12 * scale) + room;
        int x = Math.Clamp(right - size.Width, area.X - room, area.Right - size.Width + room);
        int y = Math.Clamp(top, area.Y - room, area.Bottom - size.Height + room);
        recorderWindow.Position = new PixelPoint(x, y);
    }

    static void ToggleQuick()
    {
        if (quickWindow?.IsVisible == true)
        {
            quickWindow.Hide();
            return;
        }
        var view = quickWindow?.Content;
        if (quickWindow is null)
        {
            view = Skin.Current == SkinKind.Mac ? new MacQuick { DataContext = quick } : new WinQuick { DataContext = quick };
            quickWindow = new Floating { Content = view, CloseOnDeactivate = true, Title = "Study Stash search" };
        }
        quick.Answering = false;
        quick.Query = "";
        _ = SearchAsync("");
        var (area, scale) = quickWindow.WorkArea(Floating.Pointer());
        var size = quickWindow.Measured(scale);
        quickWindow.Position = new PixelPoint(area.X + (area.Width - size.Width) / 2, area.Y + area.Height / 5 - (int)(Floating.ShadowRoom * scale));
        quickWindow.Show();
        quickWindow.Activate();
        Desktop.Activate();
        if (view is MacQuick mq) mq.FocusQuery();
        else if (view is WinQuick wq) wq.FocusQuery();
    }

    static Window MainWindow()
    {
        if (mainWindow is not null) return mainWindow;
        var w = new Window
        {
            Title = "Study Stash", Width = 1280, Height = 800, MinWidth = 900, MinHeight = 560, WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ExtendClientAreaToDecorationsHint = true, ExtendClientAreaTitleBarHeightHint = Skin.Current == SkinKind.Mac ? 52 : 48,
        };
        Look.Apply(w);
        if (Skin.Current == SkinKind.Mac)
        {
            w.Content = new MacLibrary { DataContext = library };
        }
        else
        {
            w.TransparencyLevelHint = [WindowTransparencyLevel.Mica, WindowTransparencyLevel.None];
            w.Background = Brushes.Transparent;
            w.Content = new WinLibrary { DataContext = library };
            w.Opened += (_, _) => MicaIfAvailable(w);
        }
        w.Closing += (_, e) =>
        {
            e.Cancel = true;
            w.Hide();
            Desktop.ShowInDock(false);
        };
        return mainWindow = w;
    }

    /// <summary>Windows 11's Mica shows through where the design has its Mica color; elsewhere the color stands in.</summary>
    static void MicaIfAvailable(Window w)
    {
        if (w.ActualTransparencyLevel == WindowTransparencyLevel.Mica) w.Resources["Mica"] = Brushes.Transparent;
    }

    public static void ShowLibrary()
    {
        if (!host.Settings.SetupDone)
        {
            ShowSetup();
            return;
        }
        var w = MainWindow();
        library.DrawChrome = false;
        Desktop.ShowInDock(true);
        w.Show();
        w.Activate();
        Desktop.Activate();
        _ = LoadLibraryAsync();
    }

    public static void ShowSetup()
    {
        if (setupWindow is { IsVisible: true })
        {
            setupWindow.Activate();
            return;
        }
        setup = Setup.Make(host);
        var view = Skin.Current == SkinKind.Mac ? (Control)new MacSetup { DataContext = setup, DrawChrome = false } : new WinSetup { DataContext = setup, DrawChrome = false };
        var w = new Window
        {
            Title = "Set up Study Stash", Width = 720, Height = 480, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterScreen, Content = view,
            ExtendClientAreaToDecorationsHint = true, ExtendClientAreaTitleBarHeightHint = Skin.Current == SkinKind.Mac ? 48 : 32,
        };
        Look.Apply(w);
        if (Skin.Current == SkinKind.Win)
        {
            w.TransparencyLevelHint = [WindowTransparencyLevel.Mica, WindowTransparencyLevel.None];
            w.Background = Brushes.Transparent;
            w.Opened += (_, _) => MicaIfAvailable(w);
        }
        setup.OnFinish = () =>
        {
            host.Save(s => s.SetupDone = true);
            w.Close();
            if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) Desktop.StartAtLogin(true, host.Home);
            ShowLibrary();
        };
        w.Closed += (_, _) =>
        {
            setupWindow = null;
            Desktop.ShowInDock(mainWindow?.IsVisible == true);
        };
        setupWindow = w;
        Desktop.ShowInDock(true);
        w.Show();
        w.Activate();
        Desktop.Activate();
    }

    public static void ShowSettings()
    {
        if (settingsWindow is { IsVisible: true })
        {
            settingsWindow.Activate();
            return;
        }
        var model = SettingsModel.Make(host);
        var w = new Window
        {
            Title = "Study Stash settings", Width = 900, Height = Skin.Current == SkinKind.Mac ? 780 : 860, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = new SettingsView { DataContext = model, DrawChrome = false },
            ExtendClientAreaToDecorationsHint = true, ExtendClientAreaTitleBarHeightHint = Skin.Current == SkinKind.Mac ? 52 : 32,
        };
        Look.Apply(w);
        if (Skin.Current == SkinKind.Win)
        {
            w.TransparencyLevelHint = [WindowTransparencyLevel.Mica, WindowTransparencyLevel.None];
            w.Background = Brushes.Transparent;
            w.Opened += (_, _) => MicaIfAvailable(w);
        }
        w.Closed += (_, _) =>
        {
            settingsWindow = null;
            model.Dispose();
            Desktop.ShowInDock(mainWindow?.IsVisible == true);
        };
        settingsWindow = w;
        Desktop.ShowInDock(true);
        w.Show();
        w.Activate();
        Desktop.Activate();
    }

    /// <summary>A notification in the design's look: top right on a Mac, above the tray on Windows. It goes by itself.</summary>
    public static void Toast(string title, string text, string? action, Action? run)
    {
        var view = new ToastView { Title = title, Text = text, ActionLabel = action };
        var w = new Floating { Content = view, Title = title, ShowActivated = false };
        view.Acted += () =>
        {
            w.Close();
            run?.Invoke();
        };
        view.Dismissed += w.Close;
        var (area, scale) = w.WorkArea();
        var size = w.Measured(scale);
        int room = (int)(Floating.ShadowRoom * scale);
        w.Position = OperatingSystem.IsMacOS()
            ? new PixelPoint(area.Right - size.Width - (int)(12 * scale) + room, area.Y + (int)(12 * scale) - room)
            : new PixelPoint(area.Right - size.Width - (int)(12 * scale) + room, area.Bottom - size.Height - (int)(12 * scale) + room);
        w.Show();
        DispatcherTimer.RunOnce(() =>
        {
            if (w.IsVisible && !view.IsPointerOver) w.Close();
        }, TimeSpan.FromSeconds(7));
    }

    // --- keeping it all up to date ------------------------------------------------------------------------------------

    static void Tick()
    {
        var live = host.Recorder.Current;
        if (live is null) return;
        string elapsed = TimedText.Clock(host.Recorder.Elapsed);
        var levels = host.Recorder.Levels();
        panel.Elapsed = recorder.Elapsed = elapsed;
        panel.Levels = recorder.Levels = levels;
    }

    static void Refresh()
    {
        var live = host.Recorder.Current;
        liveId = live?.Id;
        bool recording = live is not null;
        panel.IsRecording = recording;
        panel.IsPaused = recorder.IsPaused = live?.State == LectureState.Paused;
        string cls = recording ? live!.ClassName : RecordClass();
        panel.ClassName = recorder.ClassName = cls;
        int color = host.ColorOf(cls);
        panel.ClassDot = recorder.ClassDot = color >= 0 ? Skin.ClassDot(color) : Brushes.Gray;
        var now = host.ClassNow();
        panel.Hint = recording ? null
            : chosenClass is { Length: > 0 } ? "Picked by you"
            : chosenClass is "" ? "The library will sort it"
            : now is not null ? $"From your timetable · {now.Time.Describe()}"
            : host.Timetable.Next(DateTime.Now) is { } next ? $"No class on now · next, {next.Class.Name} {next.Class.Time.Describe()}" : null;
        panel.CanRecord = host.ModelReady || recording;
        var (status, good) = host.Status();
        panel.Status = status;
        panel.StatusGood = good;
        library.Status = host.Library switch
        {
            LibraryState.Connected => "Library connected",
            LibraryState.Unreachable => "Can't reach your library",
            LibraryState.WrongPassword => "Library password changed",
            _ => "No library yet",
        };
        library.StatusGood = host.Library == LibraryState.Connected;
        RefreshRecent();
        if (trayRecording != recording && tray is not null)
        {
            trayRecording = recording;
            tray.Icon = TrayImage(recording);
        }
        if (OperatingSystem.IsWindows() && mainWindow?.TryGetPlatformHandle()?.Handle is IntPtr hwnd)
        {
            var busy = host.Lectures.All().FirstOrDefault(l => l.State == LectureState.Transcribing);
            Desktop.TaskbarProgress(hwnd, busy?.Progress);
        }
        if (setup is not null) Setup.Refresh(setup, host);
    }

    /// <summary>The dropdown's recent lectures: this laptop's (where each is on its way), newest first.</summary>
    static void RefreshRecent()
    {
        var items = host.Lectures.All().Where(l => l.Id != liveId).Take(4).Select(l =>
        {
            int color = host.ColorOf(l.FiledClass.Length > 0 ? l.FiledClass : l.ClassName);
            return new LectureItem
            {
                Id = l.Id,
                Title = l.FiledTitle.Length > 0 ? l.FiledTitle : l.ClassName.Length > 0 ? $"{l.ClassName} lecture" : "Lecture",
                Detail = l.State switch
                {
                    LectureState.Transcribing => $"Transcribing {Math.Round(l.Progress * 100)}%",
                    LectureState.Sending => host.Library == LibraryState.Connected ? "Sending…" : "Waiting for your library",
                    LectureState.Writing => "Writing notes…",
                    LectureState.Filed => $"Filed in {(l.FiledClass.Length > 0 ? l.FiledClass : "your library")}",
                    LectureState.Failed => l.Error.Length > 0 ? l.Error : "Something went wrong",
                    _ => "Recording…",
                },
                Progress = l.State == LectureState.Transcribing ? l.Progress : null,
                Busy = l.State == LectureState.Writing,
                Problem = l.State == LectureState.Failed,
                Time = When(l.StartedAt.LocalDateTime),
                Dot = color >= 0 ? Skin.ClassDot(color) : Brushes.Gray,
            };
        }).ToList();
        bool same = items.Count == panel.Recent.Count && items.Zip(panel.Recent).All(p => p.First.Id == p.Second.Id && p.First.Detail == p.Second.Detail
            && p.First.Title == p.Second.Title && p.First.Progress == p.Second.Progress);
        if (same) return;
        panel.Recent.Clear();
        foreach (var i in items) panel.Recent.Add(i);
    }

    /// <summary>"11:40" today, "Mon" this week, "23 Sep" before.</summary>
    public static string When(DateTime t)
    {
        var today = DateTime.Today;
        if (t.Date == today) return t.ToString("H:mm", CultureInfo.InvariantCulture);
        if (t.Date > today.AddDays(-6)) return t.ToString("ddd", CultureInfo.InvariantCulture);
        return t.ToString("d MMM", CultureInfo.InvariantCulture);
    }

    // --- audio ---------------------------------------------------------------------------------------------------------

    /// <summary>Play the recording from a moment, when this laptop still has it; otherwise open the lecture's
    /// transcript there.</summary>
    static void Play(string? lectureId, double at)
    {
        if (lectureId is not null && File.Exists(host.Lectures.AudioPath(lectureId)))
        {
            Player.Play(host.Lectures.AudioPath(lectureId), Math.Max(0, at - 2));
            return;
        }
        if (lectureId is not null) OpenLecture(lectureId, transcript: true);
    }
}
