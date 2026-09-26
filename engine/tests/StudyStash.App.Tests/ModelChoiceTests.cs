using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using Avalonia.Headless.XUnit;
using StudyStash.App.Services;
using StudyStash.App.ViewModels;
using StudyStash.Audio;

namespace StudyStash.App.Tests;

/// <summary>A pretend model mirror: serves files by name, can fail the first requests (no internet), and can hold a
/// file's request open until it's cancelled (a big download under way).</summary>
sealed class Mirror : HttpMessageHandler
{
    public Dictionary<string, byte[]> Files { get; } = [];
    /// <summary>Requests for these never answer until they're cancelled.</summary>
    public HashSet<string> Hold { get; } = [];
    public int FailFirst { get; set; }
    public TaskCompletionSource Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    int asked;

    public int Asked => Volatile.Read(ref asked);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref asked);
        string name = request.RequestUri!.Segments[^1];
        if (FailFirst > 0)
        {
            FailFirst--;
            throw new HttpRequestException("No route to host");
        }
        if (Hold.Contains(name))
        {
            Held.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            finally
            {
                Released.TrySetResult();
            }
        }
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Files[name]) };
    }
}

public class ModelChoiceTests
{
    static readonly TimeSpan Soon = TimeSpan.FromSeconds(10);

    static (WhisperModel Model, byte[] Bytes) Fake(string id)
    {
        var bytes = new byte[2 << 20];
        new Random(id.Length + id[^1]).NextBytes(bytes);
        return (new WhisperModel(id, $"Whisper {id}", $"ggml-{id}.bin", bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)), "For tests."), bytes);
    }

    const string MirrorUrl = "https://mirror.example/models";

    static AppHost Host(TempHome home, Mirror mirror, WhisperModel? model = null) =>
        new(home.Path, () => throw new InvalidOperationException("no microphone in this test"), log: _ => { }, loginItems: new CountingLoginItems(),
            models: new ModelSetting(model, Mirror: MirrorUrl), http: new HttpClient(mirror));

    static async Task Until(Func<bool> done)
    {
        var took = Stopwatch.StartNew();
        while (!done() && took.Elapsed < Soon) await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.True(done(), "it didn't happen in time");
    }

    [Fact]
    public void The_environment_names_a_model_a_file_or_a_mirror()
    {
        using var home = new TempHome();
        string tiny = home["ggml-tiny.bin"], other = home["mine.bin"];
        File.WriteAllBytes(tiny, [1]);
        File.WriteAllBytes(other, [1]);
        ModelSetting From(params (string Name, string Value)[] vars) =>
            ModelSetting.FromEnvironment(n => vars.FirstOrDefault(v => v.Name == n).Value, File.Exists);

        Assert.Equal(ModelSetting.None, From());
        Assert.Equal(new ModelSetting(WhisperModels.Tiny), From(("STUDYSTASH_WHISPER_MODEL", "tiny")));
        Assert.Equal(new ModelSetting(WhisperModels.LargeV3Turbo), From(("STUDYSTASH_WHISPER_MODEL", " large-v3-turbo ")));
        // A path: used as it is. Named like one of the models, it's that model.
        Assert.Equal(new ModelSetting(WhisperModels.Tiny, tiny), From(("STUDYSTASH_WHISPER_MODEL", tiny)));
        Assert.Equal(new ModelSetting(null, other), From(("STUDYSTASH_WHISPER_MODEL", other)));
        Assert.Equal(ModelSetting.None, From(("STUDYSTASH_WHISPER_MODEL", home["missing.bin"])));
        // STUDYSTASH_MODEL_FILE is another name for the path; STUDYSTASH_WHISPER_MODEL wins.
        Assert.Equal(new ModelSetting(null, other), From(("STUDYSTASH_MODEL_FILE", other)));
        Assert.Equal(new ModelSetting(WhisperModels.Tiny), From(("STUDYSTASH_WHISPER_MODEL", "tiny"), ("STUDYSTASH_MODEL_FILE", other)));

        var mirror = From(("STUDYSTASH_WHISPER_MODEL", "tiny"), ("STUDYSTASH_MODEL_URL", "http://127.0.0.1:8123/models/"));
        Assert.Equal("http://127.0.0.1:8123/models", mirror.Mirror);
        Assert.Equal("http://127.0.0.1:8123/models/ggml-tiny.bin", mirror.UrlFor(WhisperModels.Tiny));
        Assert.Equal(WhisperModels.LargeV3.Url, ModelSetting.None.UrlFor(WhisperModels.LargeV3));
    }

    [Fact]
    public async Task A_model_file_given_is_used_and_nothing_downloads()
    {
        using var home = new TempHome();
        string file = home["ggml-tiny.bin"];
        File.WriteAllBytes(file, [1]);
        var mirror = new Mirror();
        using var host = new AppHost(home.Path, log: _ => { }, loginItems: new CountingLoginItems(), models: new ModelSetting(WhisperModels.Tiny, file),
            http: new HttpClient(mirror));

        Assert.True(host.ModelReady);
        Assert.Same(WhisperModels.Tiny, host.Model);
        Assert.Equal(file, host.ModelFile);
        await host.DownloadModelAsync();
        await host.RedownloadModel();
        Assert.Equal(0, mirror.Asked);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task Picking_another_model_stops_the_first_download()
    {
        using var home = new TempHome();
        var (a, aBytes) = Fake("a");
        var (b, bBytes) = Fake("b");
        var mirror = new Mirror { Files = { [a.File] = aBytes, [b.File] = bBytes }, Hold = { a.File } };
        using var host = Host(home, mirror);

        var first = host.DownloadModelAsync(a);
        await mirror.Held.Task.WaitAsync(Soon, TestContext.Current.CancellationToken);
        Assert.Same(a, host.DownloadingModel);
        Assert.Equal("Downloading Whisper a: 0 MB of 2 MB.", SettingsModel.ModelWords(host));
        // The same one again is the same download.
        Assert.Same(first, host.DownloadModelAsync(a));

        var second = host.DownloadModelAsync(b);
        await Task.WhenAll(first, second).WaitAsync(Soon, TestContext.Current.CancellationToken);

        await mirror.Released.Task.WaitAsync(Soon, TestContext.Current.CancellationToken);
        Assert.False(WhisperModels.IsDownloaded(home.Path, a));
        Assert.Equal(bBytes, File.ReadAllBytes(WhisperModels.PathFor(home.Path, b)));
        Assert.Null(host.Downloading);
        Assert.Null(host.DownloadingModel);
        Assert.Null(host.DownloadProblem);
    }

    [Fact]
    public async Task A_download_with_no_internet_says_so_and_Try_again_goes_now()
    {
        using var home = new TempHome();
        var (m, bytes) = Fake("c");
        var mirror = new Mirror { Files = { [m.File] = bytes }, FailFirst = 1 };
        using var host = Host(home, mirror, m);
        var setup = SetupModel.For(SkinKind.Mac);

        var download = host.DownloadModelAsync();
        await Until(() => host.DownloadProblem is not null);
        Assert.Equal(AppHost.DownloadStopped, host.DownloadProblem);
        Assert.Equal(AppHost.DownloadStopped, SettingsModel.ModelWords(host));
        Setup.Refresh(setup, host);
        Assert.Equal(AppHost.DownloadStopped, setup.ModelProblem);
        Assert.False(download.IsCompleted); // waiting 30 seconds to try again

        // Try again: now, not in 30 seconds.
        Assert.Same(download, host.DownloadModelAsync());
        await download.WaitAsync(Soon, TestContext.Current.CancellationToken);
        Assert.True(host.ModelReady);
        Assert.Null(host.DownloadProblem);
        Assert.Equal(2, mirror.Asked);
        Setup.Refresh(setup, host);
        Assert.Equal("Whisper c is ready", setup.ModelDone);
        Assert.Equal("Whisper c is ready.", SettingsModel.ModelWords(host));
    }

    [Fact]
    public void A_download_that_stopped_tries_again_less_and_less_often()
    {
        Assert.Equal([30, 60, 120, 300, 300, 300], Enumerable.Range(0, 6).Select(n => AppHost.RetryAfter(n).TotalSeconds));
    }

    [Fact]
    public async Task Starting_picks_up_the_download_once_setup_is_done()
    {
        using var home = new TempHome();
        var (m, bytes) = Fake("d");
        var mirror = new Mirror { Files = { [m.File] = bytes } };
        using (var before = Host(home, mirror, m))
        {
            before.Start();
            await Task.Delay(300, TestContext.Current.CancellationToken);
            Assert.Equal(0, mirror.Asked); // setup comes first
        }

        new AppSettings { SetupDone = true }.Save(home.Path);
        Directory.CreateDirectory(WhisperModels.Dir(home.Path));
        File.WriteAllBytes(WhisperModels.PathFor(home.Path, m) + ".part", bytes[..1000]);
        using var host = Host(home, mirror, m);
        host.Start();
        await Until(() => host.ModelReady);
        Assert.Equal(bytes, File.ReadAllBytes(WhisperModels.PathFor(home.Path, m)));
    }

    [Fact]
    public async Task Download_again_replaces_a_model_whisper_couldnt_start_with()
    {
        using var home = new TempHome();
        var (m, bytes) = Fake("e");
        var mirror = new Mirror { Files = { [m.File] = bytes } };
        using var host = Host(home, mirror, m);
        Directory.CreateDirectory(WhisperModels.Dir(home.Path));
        File.WriteAllBytes(WhisperModels.PathFor(home.Path, m), new byte[bytes.Length]); // the right size, but damaged
        Assert.True(host.ModelReady);

        await host.RedownloadModel().WaitAsync(Soon, TestContext.Current.CancellationToken);

        Assert.Equal(bytes, File.ReadAllBytes(WhisperModels.PathFor(home.Path, m)));
        Assert.Null(host.DownloadProblem);
    }

    [AvaloniaFact]
    public void Settings_lists_whisper_tiny_only_when_its_the_one_in_use()
    {
        using var home = new TempHome();
        using (var host = new AppHost(home.Path, log: _ => { }, loginItems: new CountingLoginItems(), models: ModelSetting.None))
        using (var settings = SettingsModel.Make(host))
        {
            Assert.DoesNotContain(settings.Models, c => c.Model == WhisperModels.Tiny);
            Assert.Contains(settings.Models, c => c.Model == WhisperModels.LargeV3Turbo);
        }
        using (var host = new AppHost(home.Path, log: _ => { }, loginItems: new CountingLoginItems(), models: new ModelSetting(WhisperModels.Tiny)))
        using (var settings = SettingsModel.Make(host))
            Assert.Single(settings.Models, c => c.Model == WhisperModels.Tiny && c.Chosen);
    }
}
