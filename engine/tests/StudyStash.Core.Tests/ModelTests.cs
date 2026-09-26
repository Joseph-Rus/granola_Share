using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using StudyStash.Audio;

namespace StudyStash.Core.Tests;

/// <summary>A pretend Hugging Face: serves one file, from where a Range asks, or not, as told.</summary>
sealed class ModelServer(byte[] file) : HttpMessageHandler
{
    /// <summary>Each request's Range start (null: the whole file).</summary>
    public List<long?> Asked { get; } = [];
    /// <summary>Answer a Range with the whole file (200), as some servers do.</summary>
    public bool IgnoreRange { get; init; }
    /// <summary>Answer a Range with 416, as a server does when the file changed.</summary>
    public bool RefuseRange { get; init; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        long? from = request.Headers.Range?.Ranges.FirstOrDefault()?.From;
        Asked.Add(from);
        if (from is not null && RefuseRange) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable));
        if (from is { } f && !IgnoreRange)
        {
            var partial = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(file, (int)f, file.Length - (int)f) };
            partial.Content.Headers.ContentRange = new ContentRangeHeaderValue(f, file.Length - 1, file.Length);
            return Task.FromResult(partial);
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(file) });
    }
}

/// <summary>Progress as it's reported, on the reporting thread (Progress&lt;T&gt; would post it elsewhere).</summary>
sealed class Seen : IProgress<DownloadProgress>
{
    public List<DownloadProgress> All { get; } = [];

    public void Report(DownloadProgress value) => All.Add(value);
}

public class ModelTests
{
    /// <summary>A small model of random bytes, and its SHA-256: a download with no network.</summary>
    static (WhisperModel Model, byte[] Bytes) Fake(int size = 3 << 20)
    {
        var bytes = new byte[size];
        new Random(7).NextBytes(bytes);
        return (new WhisperModel("test", "Whisper test", "ggml-test.bin", size, Convert.ToHexStringLower(SHA256.HashData(bytes)), "For tests."), bytes);
    }

    const string Url = "https://models.example/ggml-test.bin";

    static readonly Func<string, long?> Roomy = _ => 100L << 30;

    static Task<string> Download(TempDir dir, WhisperModel m, ModelServer server, IProgress<DownloadProgress>? progress = null,
        Func<string, long?>? free = null) =>
        ModelDownload.RunAsync(dir.Path, m, progress, default, new HttpClient(server), Url, free ?? Roomy);

    static string Part(TempDir dir, WhisperModel m) => WhisperModels.PathFor(dir.Path, m) + ".part";

    [Fact]
    public async Task A_fresh_download_reports_progress_and_ends_with_the_right_file()
    {
        using var dir = new TempDir();
        var (m, bytes) = Fake();
        var server = new ModelServer(bytes);
        var seen = new Seen();

        string path = await Download(dir, m, server, seen);

        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(WhisperModels.PathFor(dir.Path, m), path);
        Assert.False(File.Exists(Part(dir, m)));
        Assert.Equal([null], server.Asked);
        // From the first moment (nothing yet), to all of it.
        Assert.Equal(new DownloadProgress(0, m.Bytes, 0), seen.All[0]);
        Assert.Equal(m.Bytes, seen.All[^1].Done);
        Assert.True(WhisperModels.IsDownloaded(dir.Path, m));

        // Already here: nothing more is asked for.
        await Download(dir, m, server);
        Assert.Single(server.Asked);
    }

    [Fact]
    public async Task A_download_picks_up_where_it_stopped()
    {
        using var dir = new TempDir();
        var (m, bytes) = Fake();
        Directory.CreateDirectory(WhisperModels.Dir(dir.Path));
        File.WriteAllBytes(Part(dir, m), bytes[..(bytes.Length / 2)]);
        var server = new ModelServer(bytes);
        var seen = new Seen();

        await Download(dir, m, server, seen);

        Assert.Equal([bytes.Length / 2], server.Asked);
        Assert.Equal(bytes.Length / 2, seen.All[0].Done); // the half already here shows at once
        Assert.Equal(bytes, File.ReadAllBytes(WhisperModels.PathFor(dir.Path, m)));
    }

    [Fact]
    public async Task A_server_that_sends_it_all_again_starts_over_cleanly()
    {
        using var dir = new TempDir();
        var (m, bytes) = Fake();
        Directory.CreateDirectory(WhisperModels.Dir(dir.Path));
        File.WriteAllBytes(Part(dir, m), bytes[..1000]);
        var server = new ModelServer(bytes) { IgnoreRange = true };

        await Download(dir, m, server);

        Assert.Equal([1000], server.Asked);
        Assert.Equal(bytes, File.ReadAllBytes(WhisperModels.PathFor(dir.Path, m)));
    }

    [Fact]
    public async Task A_part_that_all_came_is_moved_into_place_without_asking()
    {
        using var dir = new TempDir();
        var (m, bytes) = Fake();
        Directory.CreateDirectory(WhisperModels.Dir(dir.Path));
        File.WriteAllBytes(Part(dir, m), bytes);
        var server = new ModelServer(bytes);

        await Download(dir, m, server);

        Assert.Empty(server.Asked);
        Assert.Equal(bytes, File.ReadAllBytes(WhisperModels.PathFor(dir.Path, m)));
        Assert.False(File.Exists(Part(dir, m)));
    }

    [Fact]
    public async Task A_whole_part_that_is_wrong_is_downloaded_again()
    {
        using var dir = new TempDir();
        var (m, bytes) = Fake();
        Directory.CreateDirectory(WhisperModels.Dir(dir.Path));
        File.WriteAllBytes(Part(dir, m), new byte[bytes.Length]);
        var server = new ModelServer(bytes);

        await Download(dir, m, server);

        Assert.Equal([null], server.Asked);
        Assert.Equal(bytes, File.ReadAllBytes(WhisperModels.PathFor(dir.Path, m)));
    }

    [Fact]
    public async Task A_server_that_refuses_the_range_starts_over()
    {
        using var dir = new TempDir();
        var (m, bytes) = Fake();
        Directory.CreateDirectory(WhisperModels.Dir(dir.Path));
        File.WriteAllBytes(Part(dir, m), bytes[..1000]);
        var server = new ModelServer(bytes) { RefuseRange = true };

        await Download(dir, m, server);

        Assert.Equal([1000, null], server.Asked);
        Assert.Equal(bytes, File.ReadAllBytes(WhisperModels.PathFor(dir.Path, m)));
    }

    [Fact]
    public async Task Wrong_bytes_are_thrown_away()
    {
        using var dir = new TempDir();
        var (m, bytes) = Fake();
        var wrong = (byte[])bytes.Clone();
        wrong[12345] ^= 0xFF;

        var e = await Assert.ThrowsAsync<InvalidDataException>(() => Download(dir, m, new ModelServer(wrong)));

        Assert.Equal("The Whisper test download came out damaged; try again.", e.Message);
        Assert.False(File.Exists(Part(dir, m)));
        Assert.False(File.Exists(WhisperModels.PathFor(dir.Path, m)));
    }

    [Fact]
    public async Task A_download_cut_short_keeps_what_came()
    {
        using var dir = new TempDir();
        var (m, bytes) = Fake();

        await Assert.ThrowsAsync<IOException>(() => Download(dir, m, new ModelServer(bytes[..(bytes.Length / 3)])));

        Assert.Equal(bytes.Length / 3, new FileInfo(Part(dir, m)).Length);
        var server = new ModelServer(bytes);
        await Download(dir, m, server);
        Assert.Equal([bytes.Length / 3], server.Asked);
        Assert.True(WhisperModels.IsDownloaded(dir.Path, m));
    }

    [Fact]
    public async Task No_room_no_download()
    {
        using var dir = new TempDir();
        var (m, bytes) = Fake();
        var server = new ModelServer(bytes);
        string? askedAbout = null;

        var e = await Assert.ThrowsAsync<NotEnoughSpaceException>(() => Download(dir, m, server, free: d =>
        {
            askedAbout = d;
            return m.Bytes; // the model fits, but not with room to spare
        }));

        Assert.Equal("Not enough space for Whisper test: it needs 203 MB free.", e.Message);
        Assert.IsAssignableFrom<IOException>(e);
        Assert.Equal(WhisperModels.Dir(dir.Path), askedAbout);
        Assert.Empty(server.Asked);

        // Half of it already here needs only the rest (and the spare room).
        File.WriteAllBytes(Part(dir, m), bytes[..(bytes.Length / 2)]);
        await Download(dir, m, server, free: _ => bytes.Length / 2 + ModelDownload.Spare);
        Assert.True(WhisperModels.IsDownloaded(dir.Path, m));

        // A disk that won't say how much room it has (a network share) isn't stopped.
        using var other = new TempDir();
        await Download(other, m, server, free: _ => null);
        Assert.True(WhisperModels.IsDownloaded(other.Path, m));
    }

    [Fact]
    public void Large_v3_unless_the_computer_has_little_memory()
    {
        Assert.Same(WhisperModels.LargeV3, WhisperModels.Recommended(16));
        Assert.Same(WhisperModels.LargeV3, WhisperModels.Recommended(8));
        Assert.Same(WhisperModels.LargeV3, WhisperModels.Recommended(null));
        Assert.Same(WhisperModels.LargeV3TurboSmall, WhisperModels.Recommended(4));
    }

    [Fact]
    public void Downloads_come_from_hugging_face_or_a_mirror()
    {
        Assert.Equal("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny.bin", WhisperModels.Tiny.UrlFrom(null));
        Assert.Equal("http://127.0.0.1:9000/models/ggml-tiny.bin", WhisperModels.Tiny.UrlFrom("http://127.0.0.1:9000/models/"));
    }

    [Fact]
    public void Progress_says_how_much_and_how_long()
    {
        var p = new DownloadProgress(1_900_000_000, 3_095_033_483, 5_000_000);
        Assert.Equal("1.9 GB of 3.1 GB", p.Amount);
        Assert.Equal("About 4 minutes left", p.Left());
        Assert.Equal("550 MB of 3.1 GB", new DownloadProgress(550_000_000, 3_095_033_483, 0).Amount);
        Assert.Null(new DownloadProgress(0, 3_095_033_483, 0).Left());
        Assert.Equal("3.1 GB", WhisperModels.LargeV3.Size);
    }
}
