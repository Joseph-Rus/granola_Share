using StudyStash.Core;

namespace StudyStash.Audio;

/// <summary>
/// A pretend microphone that plays a WAV file, in real time and round again: to try recording without a microphone
/// (STUDYSTASH_MIC_FILE=speech.wav), and in tests. Nothing is heard from the room.
/// </summary>
public sealed class FileMicrophone(string wavPath) : IAudioSource
{
    readonly float[] sound = Sound.ReadWav(wavPath);
    Timer? timer;
    long at;

    public string Name => "Sound file";
    public int SampleRate => Sound.Rate;
    public int Channels => 1;
    public event Action<float[]>? Samples;
#pragma warning disable CS0067 // a file doesn't fail mid-way
    public event Action<string>? Failed;
#pragma warning restore CS0067

    public void Start()
    {
        const int block = Sound.Rate / 10;
        timer ??= new Timer(_ =>
        {
            var buf = new float[block];
            for (int i = 0; i < block; i++) buf[i] = sound.Length == 0 ? 0 : sound[(at + i) % sound.Length];
            at += block;
            Samples?.Invoke(buf);
        }, null, 0, 100);
    }

    public void Stop()
    {
        timer?.Dispose();
        timer = null;
    }

    public void Dispose() => Stop();
}
