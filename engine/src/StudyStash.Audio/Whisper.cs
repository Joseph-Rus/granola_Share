using System.Runtime.InteropServices;
using System.Security.Cryptography;
using StudyStash.Core;
using Whisper.net;

namespace StudyStash.Audio;

/// <summary>A Whisper model Study Stash can download (whisper.cpp's files on Hugging Face).</summary>
public sealed record WhisperModel(string Id, string Name, string File, long Bytes, string Sha256, string About)
{
    public string Url => $"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/{File}";

    /// <summary>"3.1 GB", "550 MB".</summary>
    public string Size => Bytes >= 1_000_000_000 ? $"{Bytes / 1e9:0.0} GB" : $"{Bytes / 1e6:0} MB";
}

public static class WhisperModels
{
    public static readonly WhisperModel LargeV3 = new("large-v3", "Whisper large-v3", "ggml-large-v3.bin", 3095033483,
        "64d182b440b98d5203c4f9bd541544d84c605196c4f7b845dfa11fb23594d1e2", "The most accurate. Best on a Mac with Apple silicon or a PC with a graphics card.");
    public static readonly WhisperModel LargeV3Turbo = new("large-v3-turbo", "Whisper large-v3 turbo", "ggml-large-v3-turbo.bin", 1624555275,
        "1fc70f774d38eb169993ac391eea357ef47c88757ef72ee5943879b7e8e2bc69", "Nearly as accurate as large-v3 and several times faster.");
    public static readonly WhisperModel LargeV3TurboSmall = new("large-v3-turbo-q5", "Whisper large-v3 turbo (compact)", "ggml-large-v3-turbo-q5_0.bin", 574041195,
        "394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2", "large-v3 turbo in a third of the space, for computers without a graphics card.");
    /// <summary>For tests: small and fast, not good enough for lectures.</summary>
    public static readonly WhisperModel Tiny = new("tiny", "Whisper tiny", "ggml-tiny.bin", 77691713,
        "be07e048e1e599ad46341c8d2a135645097a538221678b7acdd1b1919c6e1b21", "For trying things out.");

    public static readonly IReadOnlyList<WhisperModel> All = [LargeV3, LargeV3Turbo, LargeV3TurboSmall, Tiny];

    public static WhisperModel? Find(string id) => All.FirstOrDefault(m => m.Id == id);

    /// <summary>What to download on this computer: large-v3 where there's a GPU for it (a Mac with Apple silicon), large-v3
    /// turbo elsewhere, and the compact turbo on a computer with little memory.</summary>
    public static WhisperModel Recommended(string system, Architecture arch, double? ramGb)
    {
        if (ramGb is < 7) return LargeV3TurboSmall;
        if (system == "Darwin" && arch == Architecture.Arm64) return LargeV3;
        return LargeV3Turbo;
    }

    public static string Dir(string home) => Path.Combine(home, "models");

    public static string PathFor(string home, WhisperModel m) => Path.Combine(Dir(home), m.File);

    public static bool IsDownloaded(string home, WhisperModel m)
    {
        var f = new FileInfo(PathFor(home, m));
        return f.Exists && f.Length == m.Bytes;
    }
}

/// <summary>How far a download has got: bytes so far, of how many, and how fast (bytes a second, lately).</summary>
public sealed record DownloadProgress(long Done, long Total, double BytesPerSecond)
{
    public double Fraction => Total <= 0 ? 0 : Math.Clamp(Done / (double)Total, 0, 1);

    /// <summary>"About 4 minutes left", once there's a rate to go on.</summary>
    public string? Left()
    {
        if (BytesPerSecond <= 0 || Done >= Total) return null;
        double s = (Total - Done) / BytesPerSecond;
        return s < 60 ? "Less than a minute left" : s < 90 ? "About a minute left" : $"About {Math.Round(s / 60)} minutes left";
    }
}

/// <summary>
/// Downloads a model into the models folder: to a .part file that a later try picks up from (a laptop closes
/// mid-download), checked against its SHA-256 before it's used.
/// </summary>
public static class ModelDownload
{
    static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    public static async Task<string> RunAsync(string home, WhisperModel m, IProgress<DownloadProgress>? progress = null,
        CancellationToken stop = default, HttpClient? http = null, string? url = null)
    {
        string path = WhisperModels.PathFor(home, m), part = path + ".part";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (WhisperModels.IsDownloaded(home, m)) return path;
        long have = System.IO.File.Exists(part) ? new FileInfo(part).Length : 0;
        if (have > m.Bytes) have = 0;
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        if (have > 0)
        {
            await using var existing = System.IO.File.OpenRead(part);
            var buf = new byte[1 << 20];
            int n;
            while ((n = await existing.ReadAsync(buf, stop)) > 0) sha.AppendData(buf, 0, n);
        }
        using var request = new HttpRequestMessage(HttpMethod.Get, url ?? m.Url);
        if (have > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);
        using var response = await (http ?? Http).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stop);
        if (have > 0 && response.StatusCode != System.Net.HttpStatusCode.PartialContent)
        {
            // The server sent the whole file again: start over.
            have = 0;
            sha.GetHashAndReset();
        }
        response.EnsureSuccessStatusCode();
        await using (var body = await response.Content.ReadAsStreamAsync(stop))
        await using (var file = new FileStream(part, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            var buf = new byte[1 << 20];
            long done = have, windowStart = have;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            double rate = 0;
            int n;
            while ((n = await body.ReadAsync(buf, stop)) > 0)
            {
                await file.WriteAsync(buf.AsMemory(0, n), stop);
                sha.AppendData(buf, 0, n);
                done += n;
                if (clock.Elapsed.TotalSeconds >= 1)
                {
                    double now = (done - windowStart) / clock.Elapsed.TotalSeconds;
                    rate = rate <= 0 ? now : rate * 0.7 + now * 0.3;
                    windowStart = done;
                    clock.Restart();
                    progress?.Report(new DownloadProgress(done, m.Bytes, rate));
                }
            }
            progress?.Report(new DownloadProgress(done, m.Bytes, rate));
        }
        string got = Convert.ToHexStringLower(sha.GetHashAndReset());
        if (new FileInfo(part).Length != m.Bytes || got != m.Sha256)
        {
            System.IO.File.Delete(part);
            throw new InvalidDataException($"The {m.Name} download came out damaged; try again.");
        }
        System.IO.File.Move(part, path, overwrite: true);
        return path;
    }
}

/// <summary>Whisper (whisper.cpp) on this computer: Metal on Apple silicon, Vulkan on a Windows graphics card, the
/// processor elsewhere.</summary>
public sealed class WhisperTranscriber : ITranscriber
{
    readonly WhisperFactory factory;
    readonly string fixedLanguage;

    /// <param name="language">"" or "auto" finds each lecture's language; "en" and so on fixes it.</param>
    public WhisperTranscriber(string modelPath, string language = "")
    {
        factory = WhisperFactory.FromPath(modelPath);
        fixedLanguage = language is "auto" ? "" : language;
    }

    /// <summary>Which of whisper.cpp's builds loaded (it says whether the GPU is in use).</summary>
    public static string Runtime => WhisperFactory.GetRuntimeInfo() ?? "";

    public async Task<Transcription> TranscribeAsync(float[] samples, string prompt, string language, CancellationToken stop)
    {
        string lang = fixedLanguage.Length > 0 ? fixedLanguage : language.Length > 0 ? language : "auto";
        var builder = factory.CreateBuilder()
            .WithLanguage(lang)
            .WithThreads(Math.Clamp(Environment.ProcessorCount / 2, 1, 8))
            .WithNoSpeechThreshold(0.6f);
        if (prompt.Length > 0) builder = builder.WithPrompt(prompt);
        await using var processor = builder.Build();
        var segments = new List<Spoken>();
        string found = "";
        await foreach (var s in processor.ProcessAsync(samples, stop))
        {
            segments.Add(new Spoken(s.Start.TotalSeconds, s.End.TotalSeconds, s.Text));
            if (found.Length == 0 && !string.IsNullOrEmpty(s.Language)) found = s.Language;
        }
        return new Transcription(segments, lang == "auto" ? found : lang);
    }

    public void Dispose() => factory.Dispose();
}
