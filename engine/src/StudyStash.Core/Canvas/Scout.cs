using System.Globalization;
using StudyStash.Core.Ai;

namespace StudyStash.Core.Canvas;

/// <summary>
/// The course scout: an AI that explores one class on Canvas, because every instructor lays Canvas out differently.
/// The sync mirrors the standard pieces; the scout finds the rest (the syllabus page, files outside modules, links
/// to Box or Google Drive), saves what's missing into the class's folder, and writes Canvas/canvas-recipe.md: a
/// short map of where this instructor puts things, which later scouts and chats follow. One class at a time.
/// </summary>
public sealed class Scout(string home, Func<string, string> classDir, AiJobs ai, Action<string>? log = null)
{
    readonly Action<string> log = log ?? Console.WriteLine;
    readonly Queue<string> queue = new();
    readonly Lock gate = new();
    string? running;

    /// <summary>The class being explored now, if any.</summary>
    public string? Running => running;
    public IReadOnlyList<string> Waiting { get { lock (gate) return queue.ToList(); } }

    public static string Prompt(string cls, long courseId, bool recipeExists) =>
        $"""
        AUTO MODE: Study Stash is running you unattended; nobody can answer questions.
        You are the Canvas scout for the class "{cls}" (Canvas course id {courseId}). Your working folder is that class's folder in the
        user's Study Stash library. Its lectures are the .md files here; Canvas/ is what Study Stash already mirrors on every sync —
        look at what's there first, and never write over any of it: assignments/*/spec.md, feedback.md, submission/, files/ (instructions,
        your grade and feedback, what you handed in); modules.md and modules/NN Module/ (the outline, and each module's own files and
        pages); syllabus.md; pages/*.md and pages/files/ (the syllabus and pages outside modules); files/<folder>/<file> (the course's
        Files area, when Canvas lets you see it); announcements.md; quizzes/*.md and discussions/*.md (the ungraded ones).
        Your job is everything that mirror misses, because every instructor lays Canvas out differently. With the canvas tools
        (canvas_api, canvas_page, canvas_download), explore: links inside pages, announcements and assignment instructions that point
        outside Canvas (Box, Google Drive, OneDrive, YouTube, zyBooks, GitHub, a course website); module items that are external links
        or tools your mirror couldn't reach; anything an instructor keeps off Canvas entirely.
        1. Canvas/canvas-recipe.md {(recipeExists ? "exists: follow it, then update it with anything that changed" : "doesn't exist yet: write it")}: a short, specific
           map of where this instructor puts slides, readings, assignment instructions, solutions, grades and announcements; naming
           patterns; what lives outside Canvas (with links) and can't be downloaded; and exactly how to check for new material next time.
        2. A module item that links outside Canvas (Box, Drive, OneDrive) and isn't saved yet: download it with canvas_download into
           that module's own folder (Canvas/modules/<the module's folder>/), named exactly after the item's title — an item titled
           "Tracing worksheet" is saved as "Tracing worksheet.pdf" — that's how the app knows it's been saved and can say "Saved from Box".
        3. Other course material that isn't here yet (slides, handouts, readings, starter files, study guides) with canvas_download,
           save_to "{cls}/Canvas/files/<the instructor's own folder or module name>/<file>". Skip videos, files already here, and anything over 40 MB.
        4. Save a useful text-only page the mirror doesn't already save (a policy page linked from an announcement, say) as Markdown
           under Canvas/pages/.
        Never change the lecture notes, or any file the sync writes (everything listed above): write only canvas-recipe.md and new
        files under Canvas/modules/, Canvas/files/ or Canvas/pages/ that the mirror hasn't already created.
        Finish with up to 8 lines: the recipe in one line, what you downloaded, and what lives outside Canvas.
        """;

    /// <summary>Queue classes to explore. Starts working through them unless it already is.</summary>
    public void Queue(params string[] classes)
    {
        lock (gate)
        {
            foreach (string c in classes)
                if (c != running && !queue.Contains(c)) queue.Enqueue(c);
            if (running is not null || queue.Count == 0) return;
            running = queue.Dequeue();
        }
        _ = Task.Run(RunAsync);
    }

    async Task RunAsync()
    {
        while (running is string cls)
        {
            try
            {
                await ExploreAsync(cls);
            }
            catch (Exception e)
            {
                log($"[scout] {cls}: {e.Message}");
            }
            lock (gate) running = queue.Count > 0 ? queue.Dequeue() : null;
        }
    }

    async Task ExploreAsync(string cls)
    {
        var settings = CanvasSettings.Load(home);
        if (!settings.Courses.TryGetValue(cls, out long id)) return;
        var (ready, why) = ai.AgentReady();
        if (!ready)
        {
            Report(cls, false, why, 0);
            return;
        }
        string dir = classDir(cls), recipe = Path.Combine(dir, "Canvas", "canvas-recipe.md");
        Directory.CreateDirectory(Path.Combine(dir, "Canvas"));
        var before = Snapshot(dir);
        log($"[scout] exploring {cls} on Canvas");
        string text = "", final = "", error = "";
        await foreach (var e in ai.AgentAsync(Prompt(cls, id, File.Exists(recipe)), dir, write: true))
        {
            if (e.Kind == "text") text += e.Text;
            else if (e.Kind == "final") final = e.Text;
            else if (e.Kind == "error") error = e.Text;
        }
        int files = Snapshot(dir).Count(kv => !before.TryGetValue(kv.Key, out var t) || t != kv.Value);
        string said = Py.Strip(final.Length > 0 ? final : text);
        Report(cls, error.Length == 0, error.Length > 0 ? error : said, files);
        log($"[scout] {cls}: {(error.Length == 0 ? "done" : "failed: " + Py.Head(error, 200))}, {files} file(s)");
    }

    void Report(string cls, bool ok, string text, int files) =>
        CanvasSettings.Update(home, s => s.Scouts[cls] = new ScoutReport(ok, Py.Tail(text, 1500), DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture), files));

    static Dictionary<string, DateTime> Snapshot(string dir) =>
        Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToDictionary(f => f, File.GetLastWriteTimeUtc) : [];
}
