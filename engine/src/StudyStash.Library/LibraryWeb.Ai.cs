using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using StudyStash.Core;
using StudyStash.Core.Ai;

namespace StudyStash.Library;

/// <summary>
/// /api/v2/ai/*: each engine's state, who does what, and asking a question with the engine you pick. Rewrite and
/// tool-access join this in later tasks. Test injection goes through <see cref="LibraryWebOptions.Ai"/>: with it
/// set, every action below runs through the fakes it carries, never a real account, terminal, or Ollama.
/// </summary>
public sealed partial class LibraryWeb
{
    AiJobs? aiJobs;

    AiJobs Jobs => options.Ai ?? (aiJobs ??= new AiJobs(cfg.Home, () => cfg.OllamaHost));

    async Task<AiOverview> AiOverviewAsync() =>
        (await Engines.StatusAsync(AiSettings.Load(cfg.Home), cfg, Jobs.Checks)) with { Pulling = Jobs.Pulling };

    static IResult AiJson<T>(T value) => Http.Json(JsonSerializer.SerializeToNode(value, AiRemote.Options));

    void MapAi(WebApplication app)
    {
        app.MapGet("/api/v2/ai/engines", Http.Handle(ctx => ApiAsync(ctx, async () => AiJson(await AiOverviewAsync()))));

        app.MapPost("/api/v2/ai/defaults", Http.Handle(ctx => ApiAsync(ctx, async () =>
        {
            var body = await Http.JsonBodyAsync(ctx.Request);
            string? notes = Str(body, "notes"), ask = Str(body, "ask");
            bool? fallback = body?["fallback"] is JsonValue fv && fv.TryGetValue(out bool f) ? f : null;
            foreach (string? id in new[] { notes, ask })
                if (id is not null && !Engines.Order.Contains(id)) return Http.Detail(400, $"there's no AI called {id}");
            var overview = await AiOverviewAsync();
            foreach (string? id in new[] { notes, ask })
                if (id is not null && overview.Engines.First(e => e.Id == id) is { Installed: false } row)
                    return Http.Detail(409, $"{row.Name} isn't installed on your library's computer.");
            var settings = AiSettings.Load(cfg.Home);
            if (notes is not null) settings.ByJob["notes"] = new AiChoice(notes);
            if (ask is not null)
            {
                settings.ByJob["ask"] = new AiChoice(ask);
                settings.ByJob["agent"] = new AiChoice(ask); // the chat that answers questions follows "ask"
            }
            if (fallback is not null) settings.Fallback = fallback.Value;
            settings.Save(cfg.Home);
            return AiJson(await AiOverviewAsync());
        })));

        app.MapPost("/api/v2/ai/engines/{id}/start", Http.Handle(ctx => ApiAsync(ctx, async () =>
        {
            string id = (string)ctx.Request.RouteValues["id"]!;
            if (id != "ollama") return Http.Detail(400, $"{Engines.Name(id)} doesn't start from here.");
            bool ok = await Jobs.Checks.StartOllama(cfg.OllamaHost);
            return AiJson(new AiSaid(ok ? "Ollama is starting." : "Ollama didn't start. Open the Ollama app and try again.", await AiOverviewAsync()));
        })));

        app.MapPost("/api/v2/ai/engines/{id}/download", Http.Handle(ctx => ApiAsync(ctx, async () =>
        {
            string id = (string)ctx.Request.RouteValues["id"]!;
            if (id != "ollama") return Http.Detail(400, $"{Engines.Name(id)} doesn't download a model here.");
            string model = cfg.EffectiveSummaryModel;
            _ = Jobs.DownloadAsync(model, cfg.OllamaHost); // runs in the background; AiOverview.Pulling shows it
            return AiJson(new AiSaid($"Downloading {model}…", await AiOverviewAsync()));
        })));

        app.MapPost("/api/v2/ai/engines/{id}/sign-in", Http.Handle(ctx => ApiAsync(ctx, async () =>
        {
            string id = (string)ctx.Request.RouteValues["id"]!;
            if (id == "ollama") return Http.Detail(400, "Ollama doesn't need to sign in: it's private, on this computer.");
            if (!IsLocal(ctx))
                return Http.Detail(409, $"Sign in on your library's computer: open Terminal there and run {Engines.SignInWords(id)}.");
            string said;
            try
            {
                said = Jobs.Checks.OpenSignIn(cfg.Home, AiSettings.Load(cfg.Home).Terminal, id);
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
            {
                return Http.Detail(409, e.Message);
            }
            return AiJson(new AiSaid(said, await AiOverviewAsync()));
        })));

        app.MapPost("/api/v2/ai/engines/{id}/check", Http.Handle(ctx => ApiAsync(ctx, async () =>
        {
            string id = (string)ctx.Request.RouteValues["id"]!;
            if (!Engines.Order.Contains(id)) return Http.Detail(400, $"there's no AI called {id}");
            string? model = Str(await Http.JsonBodyAsync(ctx.Request), "model");
            var (ok, why) = await Jobs.TestAsync(id, model ?? "");
            return AiJson(new AiSaid(ok ? $"{Engines.Name(id)} is ready." : why, await AiOverviewAsync()));
        })));

        app.MapPost("/api/v2/ai/engines/{id}/model", Http.Handle(ctx => ApiAsync(ctx, async () =>
        {
            string id = (string)ctx.Request.RouteValues["id"]!;
            if (!Engines.Order.Contains(id)) return Http.Detail(400, $"there's no AI called {id}");
            string? model = Str(await Http.JsonBodyAsync(ctx.Request), "model");
            if (model is null) return Http.Detail(400, "which model?");
            if (id == "ollama")
            {
                cfg.SummaryModel = model;
                Configs.Save(cfg);
            }
            else
            {
                var settings = AiSettings.Load(cfg.Home);
                settings.Models[id] = model;
                settings.Save(cfg.Home);
            }
            return AiJson(await AiOverviewAsync());
        })));

        app.MapPost("/api/v2/ai/problems/{id}/dismiss", Http.Handle(ctx => ApiAsync(ctx, async () =>
        {
            string id = (string)ctx.Request.RouteValues["id"]!;
            var settings = AiSettings.Load(cfg.Home);
            if (!settings.Dismissed.Contains(id))
            {
                settings.Dismissed.Add(id);
                settings.Save(cfg.Home);
            }
            return AiJson(await AiOverviewAsync());
        })));

        app.MapPost("/api/v2/ai/ask", Http.Handle(ctx => ApiAsync(ctx, async () =>
        {
            var body = await Http.JsonBodyAsync(ctx.Request);
            string? question = Str(body, "question");
            if (question is null) return Http.Detail(400, "what's the question?");
            var request = new AskRequest(question)
            {
                Lecture = Str(body, "lecture"), Class = Str(body, "class"), Engine = Str(body, "engine"),
                Live = Str(body, "live"), LiveTitle = Str(body, "live_title"),
            };
            try
            {
                return AiJson(await Jobs.AskAsync(request, Reader, cfg, ctx.RequestAborted));
            }
            catch (Exception e) when (e is HttpRequestException or TimeoutException or InvalidOperationException or InvalidDataException)
            {
                return Http.Detail(503, e.Message);
            }
        })));
    }
}
