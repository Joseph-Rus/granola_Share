using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StudyStash.Core;
using StudyStash.Core.Ai;

namespace StudyStash.App.ViewModels;

/// <summary>One row of the "Answer with" / "Rewrite notes with" menu (<see cref="EngineMenuModel"/>): a name and
/// subtitle, whether it's checked (the library's default, or the notes' writer), whether it's the one currently
/// picked (tinted), and whether picking it is allowed at all.</summary>
public sealed partial class EngineMenuItem(string id, string name, string subtitle, bool @checked, bool enabled) : ObservableObject
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Subtitle { get; } = subtitle;
    public bool Checked { get; } = @checked;
    public bool Enabled { get; } = enabled;
    [ObservableProperty] public partial bool Selected { get; set; }
    public IRelayCommand Command { get; internal set; } = null!;
}

/// <summary>A menu of engines, shared by the Ask bar's "Answer with" and the notes' "Rewrite notes with": a header,
/// the rows, and an optional footer link. The view is one control (<c>{Mac,Win}AiEngineMenu</c>) over this.</summary>
public sealed class EngineMenuModel
{
    public string Header { get; init; } = "";
    public ObservableCollection<EngineMenuItem> Items { get; } = [];
    public string FooterText { get; init; } = "";
    public IRelayCommand? FooterCommand { get; init; }
    /// <summary>260 for "Answer with", 280 for "Rewrite notes with" (the design's Mac widths).</summary>
    public double Width { get; init; } = 260;
}

/// <summary>One question and its answer in an ask thread: what was asked, of which engine, and once it comes back,
/// the answer, its byline, a fell-back note, or why it failed. A turn never edits itself back to thinking.</summary>
public sealed partial class AiTurn : ObservableObject
{
    public string Question { get; }
    /// <summary>The engine's name at the moment this was asked (the thinking line names it, even if the answer
    /// ends up crediting a different one after a fallback).</summary>
    public string AskedName { get; }
    [ObservableProperty] public partial string? Answer { get; set; }
    [ObservableProperty] public partial string? Byline { get; set; }
    [ObservableProperty] public partial string? FellBackNote { get; set; }
    [ObservableProperty] public partial string? Failed { get; set; }
    public List<AskSource> Sources { get; set; } = [];
    public bool IsThinking => Answer is null && Failed is null;
    public string ThinkingWords => AiWords.Thinking(AskedName);

    public AiTurn(string question, string askedName)
    {
        Question = question;
        AskedName = askedName;
    }

    partial void OnAnswerChanged(string? value) => OnPropertyChanged(nameof(IsThinking));
    partial void OnFailedChanged(string? value) => OnPropertyChanged(nameof(IsThinking));
}

/// <summary>
/// The ask bar under a lecture's notes, the recorder's compact chat, and the quick panel's ask (design 16): an
/// engine picked per question (starting at the library's default and lasting for this model), a scope, and the
/// thread of turns, with an answer's byline naming who answered and when in the lecture. Reads and asks one
/// library's AI (<see cref="IAiLibrary"/>); the view is just this.
/// </summary>
public sealed partial class AiAskModel : ObservableObject
{
    readonly IAiLibrary library;

    public AiAskModel(IAiLibrary library)
    {
        this.library = library;
        foreach (var c in ScopeChoices) c.Pick = new RelayCommand(() => Scope = c.Id);
    }

    /// <summary>Set by the host: the lecture or class this ask bar is under (whichever the current scope needs).</summary>
    public string? LectureId { get; set; }
    public string? ClassName { get; set; }
    /// <summary>The recorder's chat asks with the transcript so far, not a saved lecture: the host sets this (and
    /// <see cref="LiveTitle"/>); everywhere else it's null and the scope/lecture/class above apply instead.</summary>
    public Func<string?>? Live { get; set; }
    public string LiveTitle { get; set; } = "This lecture, now";

    [ObservableProperty] public partial string Question { get; set; } = "";
    [ObservableProperty] public partial string Scope { get; set; } = "lecture";
    [ObservableProperty] public partial string Engine { get; set; } = "";
    [ObservableProperty] public partial bool MenuOpen { get; set; }
    [ObservableProperty] public partial bool Busy { get; set; }
    [ObservableProperty] public partial bool OlderLibrary { get; set; }
    [ObservableProperty] public partial bool Offline { get; set; }

    public ObservableCollection<AiTurn> Turns { get; } = [];
    public EngineMenuModel Menu { get; } = new() { Header = "Answer with", FooterText = "Change defaults in Settings" };
    public List<EngineChoice> ScopeChoices { get; } =
    [
        new("lecture", "This lecture"), new("class", "This class"), new("all", "All classes"),
    ];

    public string EngineName => Menu.Items.FirstOrDefault(i => i.Id == Engine)?.Name ?? Engine;
    public string ScopeName => ScopeChoices.FirstOrDefault(c => c.Id == Scope)?.Name ?? Scope;
    public string Placeholder => AiWords.AskPlaceholder(Scope);
    public AiTurn? Latest => Turns.Count > 0 ? Turns[^1] : null;
    public bool HasLatest => Latest is not null;

    /// <summary>Opens Settings → AI engines (the "Change defaults" footer link).</summary>
    public Action? OpenSettings { get; set; }
    /// <summary>A turn's answer, tapped: jump to its first timed source.</summary>
    public Action<AskSource>? OnSource { get; set; }
    /// <summary>Closes the "Answer with" popup once an engine is picked; the view wires this to the real flyout.</summary>
    public Action? CloseMenu { get; set; }

    partial void OnEngineChanged(string value)
    {
        foreach (var i in Menu.Items) i.Selected = i.Id == value;
        OnPropertyChanged(nameof(EngineName));
    }

    partial void OnScopeChanged(string value)
    {
        OnPropertyChanged(nameof(ScopeName));
        OnPropertyChanged(nameof(Placeholder));
    }

    [RelayCommand]
    void ToggleMenu() => MenuOpen = !MenuOpen;

    [RelayCommand]
    public Task Refresh() => Load();

    /// <summary>Reads the library's AI once and builds the "Answer with" menu: called when the ask bar opens.</summary>
    public async Task Load()
    {
        AiOverview? overview;
        try
        {
            overview = await library.EnginesAsync();
        }
        catch
        {
            Offline = true;
            return;
        }
        Apply(overview);
    }

    void Apply(AiOverview? overview)
    {
        if (overview is null)
        {
            OlderLibrary = true;
            Offline = false;
            return;
        }
        OlderLibrary = false;
        Offline = false;

        // The default first (checked), then the rest in the library's own order; an engine nobody has installed
        // never reaches this menu.
        var ordered = overview.Engines.Where(e => e.Id == overview.Ask)
            .Concat(overview.Engines.Where(e => e.Id != overview.Ask))
            .Where(e => e.State != "not_installed");

        Menu.Items.Clear();
        foreach (var e in ordered)
        {
            bool isDefault = e.Id == overview.Ask;
            var item = new EngineMenuItem(e.Id, e.Name, AiWords.AskEngineSubtitle(e.Id, e.State, isDefault), isDefault, AiWords.EngineUsable(e.State));
            item.Command = new RelayCommand(() => { Engine = item.Id; CloseMenu?.Invoke(); }, () => item.Enabled);
            Menu.Items.Add(item);
        }
        if (Engine.Length == 0 || Menu.Items.All(i => i.Id != Engine)) Engine = overview.Ask;
        foreach (var i in Menu.Items) i.Selected = i.Id == Engine;
        OnPropertyChanged(nameof(EngineName));
    }

    [RelayCommand]
    async Task Ask()
    {
        string q = Question.Trim();
        if (q.Length == 0 || Busy) return;
        Question = "";
        var turn = new AiTurn(q, EngineName);
        Turns.Add(turn);
        OnPropertyChanged(nameof(Latest));
        OnPropertyChanged(nameof(HasLatest));
        Busy = true;
        try
        {
            string? live = Live?.Invoke();
            var request = new AskRequest(q)
            {
                Engine = Engine,
                Live = live,
                LiveTitle = live is not null ? LiveTitle : null,
                Lecture = live is null && Scope == "lecture" ? LectureId : null,
                Class = live is null && Scope == "class" ? ClassName : null,
            };
            AskReply? reply = await library.AskAsync(request);
            if (reply is null)
            {
                turn.Failed = AiWords.OlderLibraryWords;
                OlderLibrary = true;
                return;
            }
            turn.Answer = reply.Answer;
            turn.Sources = reply.Sources;
            turn.Byline = AiWords.AskByline(reply.EngineName, reply.Sources);
            if (reply.FellBack) turn.FellBackNote = AiWords.FellBackNote(reply.EngineName, reply.Why);
        }
        catch (LibraryRefusedException ex)
        {
            turn.Failed = ex.Message;
        }
        catch
        {
            turn.Failed = "Your library isn't answering. Check it's on and connected.";
            Offline = true;
        }
        finally
        {
            Busy = false;
            OnPropertyChanged(nameof(Latest));
        }
    }

    [RelayCommand]
    void OpenTurnSource(AiTurn turn)
    {
        if (turn.Sources.FirstOrDefault() is { } s) OnSource?.Invoke(s);
    }
}
