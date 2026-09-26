using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using StudyStash.Core;
using StudyStash.Core.Ai;

namespace StudyStash.Library;

/// <summary>
/// Searching files (everything in the library that isn't a lecture, and the folders Study Stash may read), and
/// choosing those folders in Settings.
/// </summary>
public sealed partial class LibraryWeb
{
    FileIndex? files;

    /// <summary>One per library; the service keeps it up to date.</summary>
    public FileIndex Files => files ??= options.Files ?? MakeFileIndex(cfg, store);

    public static FileIndex MakeFileIndex(Config cfg, Store store) => new(cfg.Home,
        () => [("Library", cfg.PoolDir, false), .. Folders.Load(cfg.Home).Where(f => Directory.Exists(f.Path)).Select(f => (f.Name, f.Path, f.Private))],
        () => store.ListNotes().Select(r => r.MdPath).OfType<string>().ToList());

    /// <summary>A link for a file search found: a page for files in a class's folder, else the file itself.</summary>
    string FileLink(FileHit h)
    {
        string lib = Path.GetFullPath(cfg.PoolDir) + Path.DirectorySeparatorChar;
        if (h.Path.StartsWith(lib, StringComparison.Ordinal))
        {
            string rel = h.Path[lib.Length..].Replace('\\', '/');
            int slash = rel.IndexOf('/');
            string? cls = slash > 0 ? cfg.ClassNames().FirstOrDefault(c => Path.GetFileName(store.ClassDir(c)) == rel[..slash]) : null;
            if (cls is not null) return $"/files/{Ui.Quote(cls, "")}/{Ui.Quote(rel[(slash + 1)..], "/")}";
        }
        int i = Folders.Load(cfg.Home).FindIndex(f => h.Path.StartsWith(Path.GetFullPath(f.Path) + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        return i >= 0 ? $"/folder/{i}/{Ui.Quote(Path.GetRelativePath(Folders.Load(cfg.Home)[i].Path, h.Path).Replace('\\', '/'), "/")}" : "#";
    }

    /// <summary>The files part of the search page (nothing when no file matches).</summary>
    string FileResults(string q)
    {
        var hits = q.Length > 0 ? Files.Search(q, 30) : [];
        if (hits.Count == 0) return "";
        return "<h2>Files</h2><div class=\"group\">" + string.Concat(hits.Select(h =>
            $"<a class=\"row\" href=\"{FileLink(h)}\"><div class=\"grow\"><div class=\"title\">{Ui.Esc(h.Title)}</div>"
            + $"<div class=\"subtitle\">{Ui.Esc(h.Root)} · {h.Snippet}</div></div></a>")) + "</div>";
    }

    void MapFiles(WebApplication app)
    {
        app.MapGet("/api/v2/files/search", (HttpContext ctx, string? q, int? limit) => Api(ctx, () => Http.Json(new JsonArray(
            Files.Search(q ?? "", Math.Clamp(limit ?? 20, 1, 60)).Select(h => (JsonNode)new JsonObject
            {
                ["root"] = h.Root, ["path"] = h.Path, ["title"] = h.Title,
                ["snippet"] = System.Net.WebUtility.HtmlDecode(h.Snippet.Replace("<mark>", "").Replace("</mark>", "")),
            }).ToArray()))));
        // A file in a readable folder: shown if it's Markdown or text, else downloaded. Private folders' files aren't served.
        app.MapGet("/folder/{i:int}/{**path}", (HttpContext ctx, int i, string path) => WithMember(ctx, role =>
        {
            var list = Folders.Load(cfg.Home);
            if (i < 0 || i >= list.Count || list[i].Private) return NotFound(role, "file");
            string root = Path.GetFullPath(list[i].Path), full = Path.GetFullPath(Path.Combine(root, Uri.UnescapeDataString(path)));
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(full)) return NotFound(role, "file");
            if (!full.EndsWith(".md", StringComparison.OrdinalIgnoreCase) && !full.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                return Results.File(full, "application/octet-stream", Path.GetFileName(full));
            var c = Context(role, back: ("/search", "Search"));
            return Show(Path.GetFileNameWithoutExtension(full), $"<p class=\"sub\">{Ui.Esc(list[i].Name)}</p><article class=\"prose\">{Ui.RenderMd(File.ReadAllText(full))}</article>", c, math: true);
        }));
        app.MapPost("/settings/folders", Http.Handle(ctx => WithMemberAsync(ctx, async role =>
        {
            var f = await Http.FormAsync(ctx.Request);
            var list = Folders.Load(cfg.Home);
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (f.Get($"folder_remove_{i}") == "1") { list.RemoveAt(i); continue; }
                if (f.ContainsKey($"folder_name_{i}"))
                    list[i] = list[i] with { Ai = f.Get($"folder_ai_{i}") == "1", Private = f.Get($"folder_private_{i}") == "1" };
            }
            string typed = Py.Strip(f.Get("folder_new"));
            if (typed.Length > 0)
            {
                string full = Path.GetFullPath(Py.ExpandUser(typed));
                if (!Directory.Exists(full)) return Http.SeeOther("/settings?folders=missing#folders");
                if (list.All(x => Path.GetFullPath(x.Path) != full)) list.Add(new ReadFolder(full, Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar))));
            }
            Folders.Save(cfg.Home, list);
            _ = Files.UpdateAsync(Console.WriteLine);
            return Http.SeeOther("/settings?folders=saved#folders");
        })));
    }

    string FoldersSettingsGroup(string? flash)
    {
        var list = Folders.Load(cfg.Home);
        string say = flash switch
        {
            "saved" => "<div class=\"notice good\"><div>Saved. New folders are searchable within a few minutes.</div></div>",
            "missing" => "<div class=\"notice\"><div>There's no folder there on this computer.</div></div>",
            _ => "",
        };
        string rows = string.Concat(list.Select((f, i) =>
            "<div class=\"row\"><div class=\"grow\">"
            + $"<div class=\"title\">{Ui.Esc(f.Name)}</div><div class=\"subtitle\">{Ui.Esc(f.Path)}</div>"
            + $"<input type=\"hidden\" name=\"folder_name_{i}\" value=\"{Ui.Esc(f.Name)}\"></div>"
            + $"<label class=\"remove\">AI reads<input class=\"switch\" type=\"checkbox\" name=\"folder_ai_{i}\" value=\"1\"{(f.Ai ? " checked" : "")}></label>"
            + $"<label class=\"remove\">Private<input class=\"switch\" type=\"checkbox\" name=\"folder_private_{i}\" value=\"1\"{(f.Private ? " checked" : "")}></label>"
            + $"<label class=\"remove\">Remove<input class=\"switch\" type=\"checkbox\" name=\"folder_remove_{i}\" value=\"1\"></label></div>"));
        return $"<div class=\"group-head\" id=\"folders\">Folders it may read</div>{say}<form method=\"post\" action=\"/settings/folders\"><div class=\"group\">"
            + rows
            + "<div class=\"row\"><input type=\"text\" name=\"folder_new\" placeholder=\"Add a folder, like ~/Documents/School\" style=\"flex:1\"></div>"
            + "</div><div class=\"actions\"><button class=\"primary\">Save folders</button></div></form>"
            + $"<p class=\"group-foot\">Search finds files in these folders, and the AI can read them when you chat. A private folder is searched "
            + $"by file name only and never shown to the AI. {Files.Files} files are searchable now.</p>";
    }
}
