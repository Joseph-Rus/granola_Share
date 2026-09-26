using StudyStash.Audio;

namespace StudyStash.App.Services;

/// <summary>
/// The transcription model the environment asks for, for tests, the self-test and trying the app out.
/// STUDYSTASH_WHISPER_MODEL names a model to use and download ("tiny", "large-v3"), or a model file to use as it is
/// (nothing downloads); STUDYSTASH_MODEL_FILE is another name for the file; STUDYSTASH_MODEL_URL is where models
/// download from instead of Hugging Face (<c>{url}/{file}</c>). With none of them, it's the model picked in Settings,
/// from Hugging Face.
/// </summary>
public sealed record ModelSetting(WhisperModel? Model = null, string? File = null, string? Mirror = null)
{
    /// <summary>The environment asks for nothing.</summary>
    public static readonly ModelSetting None = new();

    /// <summary>What this process's environment asks for.</summary>
    public static ModelSetting FromEnvironment() => FromEnvironment(Environment.GetEnvironmentVariable, System.IO.File.Exists);

    /// <summary>What an environment asks for: <paramref name="env"/> reads a variable, <paramref name="exists"/> says
    /// whether a file is there (a path to nothing is ignored).</summary>
    public static ModelSetting FromEnvironment(Func<string, string?> env, Func<string, bool> exists)
    {
        string? Get(string name) => env(name)?.Trim() is { Length: > 0 } v ? v : null;
        WhisperModel? model = null;
        string? file = null;
        if (Get("STUDYSTASH_WHISPER_MODEL") is { } named)
        {
            if (WhisperModels.Find(named) is { } m) model = m;
            else if (exists(named)) file = named;
        }
        if (model is null && file is null && Get("STUDYSTASH_MODEL_FILE") is { } alias && exists(alias)) file = alias;
        // A file named like one of the models is that model (ggml-tiny.bin is Whisper tiny), so the app says its name.
        if (file is not null) model = WhisperModels.All.FirstOrDefault(m => m.File == Path.GetFileName(file));
        return new ModelSetting(model, file, Get("STUDYSTASH_MODEL_URL")?.TrimEnd('/'));
    }

    /// <summary>Where <paramref name="m"/> downloads from.</summary>
    public string UrlFor(WhisperModel m) => m.UrlFrom(Mirror);
}
