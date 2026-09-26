using Avalonia.Media;
using StudyStash.App.ViewModels;

namespace StudyStash.App;

/// <summary>The design's sample lectures (CS 101, BIO 110, CALC II, HIST 210): what screenshots and `--demo` show.</summary>
public static class Demo
{
    /// <summary>The design's waveform, bar by bar (heights out of 28).</summary>
    public static readonly double[] Wave = [.. new double[] { 6, 10, 16, 22, 14, 8, 12, 20, 26, 18, 10, 6, 9, 15, 24, 20, 12, 8, 14, 6 }.Select(h => h / 28)];

    /// <summary>The recorder pill's five bars (heights out of 20).</summary>
    public static readonly double[] PillWave = [.. new double[] { 8, 16, 11, 18, 7 }.Select(h => h / 20)];

    static IBrush Cs => Skin.ClassDot(0);
    static IBrush Bio => Skin.ClassDot(1);
    static IBrush Calc => Skin.ClassDot(2);
    static IBrush Hist => Skin.ClassDot(3);

    public static RecorderModel Recorder(bool paused = false, bool expanded = false)
    {
        var r = new RecorderModel { ClassName = "CS 101", ClassDot = Cs, Elapsed = "24:18", IsPaused = paused, Levels = PillWave, Expanded = expanded };
        r.Lines.Add(new HeardLine { Time = "18:05", Text = "Okay, the midterm. Recursion traces will be on it, the stack diagrams from last week." });
        r.Lines.Add(new HeardLine { Time = "19:30", Text = "Think of each call as a plate on a stack. You can only take the top one off." });
        r.Lines.Add(new HeardLine { Time = "21:52", Text = "When factorial of three calls factorial of two, the first call is paused, waiting." });
        r.Lines.Add(new HeardLine { Time = "24:12", Text = "And when we hit the base case, the frames come off one by one.", Latest = true });
        r.Chat.Add(new ChatMessage { Mine = true, Text = "What did she say is on the midterm?" });
        var a = new ChatMessage { Text = "Recursion traces and call-stack diagrams, like last week's. Big-O proofs won't be on it." };
        a.Sources.Add(new SourceChip { Label = "18:05", At = 1085 });
        a.Sources.Add(new SourceChip { Label = "18:40", At = 1120 });
        r.Chat.Add(a);
        return r;
    }

    public static QuickModel Quick(bool answer)
    {
        var q = new QuickModel();
        if (!answer)
        {
            q.Query = "call stack";
            string rec = Skin.Current == SkinKind.Mac ? "⌥⇧R" : "Ctrl+Alt+R";
            q.Rows.Add(new QuickRow { Kind = QuickKind.Header, Title = "Lectures", First = true });
            q.Rows.Add(new QuickRow { Kind = QuickKind.Lecture, Title = "Recursion and the call stack", Meta = "CS 101 · Tue 23 Sep", Dot = Cs, Selected = true });
            q.Rows.Add(new QuickRow { Kind = QuickKind.Lecture, Title = "Stack frames and scope", Meta = "CS 101 · Thu 18 Sep", Dot = Cs });
            q.Rows.Add(new QuickRow { Kind = QuickKind.Header, Title = "Passages from notes" });
            q.Rows.Add(new QuickRow { Kind = QuickKind.Passage, Title = "…each frame on the call stack keeps its own copy of n, so nothing gets overwritten…", Sub = "Recursion and the call stack · Key points", Dot = Cs });
            q.Rows.Add(new QuickRow { Kind = QuickKind.Passage, Title = "…once the base case returns, the call stack unwinds and each paused call finishes…", Sub = "Recursion and the call stack · Summary", Dot = Cs });
            q.Rows.Add(new QuickRow { Kind = QuickKind.Header, Title = "Classes" });
            q.Rows.Add(new QuickRow { Kind = QuickKind.Class, Title = "CS 101", Meta = "12 lectures", Dot = Cs });
            q.Rows.Add(new QuickRow { Kind = QuickKind.Header, Title = "Actions" });
            q.Rows.Add(new QuickRow { Kind = QuickKind.Action, Title = "Record CS 101", Meta = rec, Glyph = "mic" });
            q.Rows.Add(new QuickRow { Kind = QuickKind.Action, Title = "Open library", Glyph = "book_2" });
        }
        else
        {
            q.Query = "what's on the cs 101 midterm?";
            q.Answering = true;
            q.Answer = "Recursion traces and call-stack diagrams, from Tuesday's lecture. On Thursday she added that scope rules are fair game, but Big-O proofs aren't.";
            q.Rows.Add(new QuickRow { Kind = QuickKind.Header, Title = "Sources", First = true });
            q.Rows.Add(new QuickRow { Kind = QuickKind.Source, Title = "Recursion and the call stack", Meta = "18:05", Selected = true });
            q.Rows.Add(new QuickRow { Kind = QuickKind.Source, Title = "Stack frames and scope", Meta = "41:20" });
        }
        return q;
    }

    public const string Notes = """
        ## Summary
        A recursive function solves a problem by calling itself on a smaller version of it. Each call gets its own frame on the call stack, which holds that call's arguments and local variables. The calls pause in order until a base case returns, then the stack unwinds and each paused call finishes its work.

        ## Key points
        - Every recursive function needs a base case that returns without calling itself.
        - Each call waits for the one it made, so the most recent call finishes first.
        - Without a base case the stack keeps growing until it overflows.
        - Tracing factorial(3) frame by frame is the skill the midterm tests.

        ## Definitions
        - **Call stack**: the memory a program uses to track calls that haven't finished yet.
        - **Stack frame**: one entry on the stack: a call's arguments, locals and where to return.
        - **Base case**: the input a recursive function answers directly.

        ## Questions to review
        1. Draw the stack for factorial(4) at its deepest point.
        2. What happens if the base case is n == 1 and you call factorial(0)?
        """;

    public static LibraryModel Library()
    {
        var m = new LibraryModel { ClassTitle = "CS 101", ClassCount = "12 lectures", Status = "Library connected", DrawChrome = true };
        m.Classes.Add(new ClassItem { Name = "CS 101", Dot = Cs, Count = 12, Selected = true });
        m.Classes.Add(new ClassItem { Name = "BIO 110", Dot = Bio, Count = 9 });
        m.Classes.Add(new ClassItem { Name = "CALC II", Dot = Calc, Count = 11 });
        m.Classes.Add(new ClassItem { Name = "HIST 210", Dot = Hist, Count = 7 });
        m.Unsorted = new ClassItem { Name = "Unsorted", IsUnsorted = true, Count = 2 };
        var week = new LectureGroup { Label = "This week", First = true };
        week.Items.Add(new LectureCard { Title = "Recursion and the call stack", Meta = "Tue 23 Sep · 1 h 12 min", Summary = "A recursive function solves a problem by calling itself on a smaller version of it.", Selected = true });
        week.Items.Add(new LectureCard { Title = "Stack frames and scope", Meta = "Thu 18 Sep · 1 h 14 min", Summary = "Where a variable lives decides who can see it, and for how long." });
        week.Items.Add(new LectureCard { Title = "Functions as values", Meta = "Tue 16 Sep · 1 h 10 min", Summary = "Passing a function to another function, and why map and filter work." });
        var last = new LectureGroup { Label = "Last week" };
        last.Items.Add(new LectureCard { Title = "Loops and invariants", Meta = "Thu 11 Sep · 1 h 13 min", Summary = "An invariant is a statement that stays true on every pass through a loop." });
        last.Items.Add(new LectureCard { Title = "Tracing small programs", Meta = "Tue 9 Sep · 1 h 08 min", Summary = "Reading code line by line and writing down every value as it changes.", Last = true });
        m.Groups.Add(week);
        m.Groups.Add(last);
        m.Note = new NoteModel
        {
            ClassName = "CS 101", Dot = Cs, Meta = "CS 101 · Tuesday 23 September · 1 h 12 min", Title = "Recursion and the call stack", Markdown = Notes,
        };
        return m;
    }

    public static SetupModel Setup(SkinKind skin)
    {
        var m = SetupModel.For(skin);
        if (skin == SkinKind.Mac)
        {
            m.Go(SetupStep.Model);
            m.ModelSize = "3 GB";
            m.ModelProgress = 0.62;
            m.ModelDone = "1.9 GB of 3.1 GB";
            m.ModelLeft = "About 4 minutes left";
        }
        else
        {
            m.Go(SetupStep.Taskbar);
        }
        return m;
    }

    public static PanelModel Panel(bool recording)
    {
        var p = new PanelModel
        {
            ClassName = "CS 101", ClassDot = Cs, Hint = "From your timetable · Tue 10:00–11:15", Status = "Library connected · Model ready",
            IsRecording = recording, Elapsed = "24:18", Levels = Wave, LastLine = "“…and when we hit the base case, the frames come off one by one.”",
        };
        if (!recording)
        {
            p.Recent.Add(new LectureItem { Title = "Membranes and osmosis", Detail = "Transcribing 42%", Time = "11:40", Dot = Bio, Progress = 0.42 });
            p.Recent.Add(new LectureItem { Title = "Series convergence tests", Detail = "Writing notes…", Time = "10:50", Dot = Calc, Busy = true });
            p.Recent.Add(new LectureItem { Title = "Recursion and the call stack", Detail = "Filed in CS 101", Time = "9:02", Dot = Cs, Selected = true });
            p.Recent.Add(new LectureItem { Title = "The Treaty of Versailles", Detail = "Filed in HIST 210", Time = "Mon", Dot = Hist });
        }
        else
        {
            p.Recent.Add(new LectureItem { Title = "The Treaty of Versailles", Detail = "Filed in HIST 210", Time = "Mon", Dot = Hist });
            p.Recent.Add(new LectureItem { Title = "Cell transport, part 1", Detail = "Filed in BIO 110", Time = "Fri", Dot = Bio });
        }
        return p;
    }
}
