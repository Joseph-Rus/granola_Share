using StudyStash.Core;

namespace StudyStash.Audio;

/// <summary>
/// A pretend microphone that plays a WAV file, round again: to try recording without a microphone
/// (STUDYSTASH_MIC_FILE=speech.wav), and in tests. Nothing is heard from the room. It plays in real time, or
/// <see cref="Speed"/> times faster (STUDYSTASH_MIC_SPEED), so the self-test records minutes of lecture in seconds.
/// </summary>
public sealed class FileMicrophone(string wavPath, int speed = 1) : IAudioSource
{
    const int Block = Sound.Rate / 10; // 100 ms, as often as a sound card hands over sound
    readonly float[] sound = Sound.ReadWav(wavPath);
    readonly Lock gate = new();
    Timer? timer;
    long at;

    public string Name => "Sound file";
    public int SampleRate => Sound.Rate;
    public int Channels => 1;

    /// <summary>How many times faster than real time it plays: this many blocks every 100 ms.</summary>
    public int Speed { get; } = Math.Max(1, speed);

    public event Action<float[]>? Samples;
#pragma warning disable CS0067 // a file doesn't fail mid-way
    public event Action<string>? Failed;
#pragma warning restore CS0067

    public void Start()
    {
        lock (gate) timer ??= new Timer(_ => Tick(), null, 0, 100);
    }

    void Tick()
    {
        // One tick at a time, even when the thread pool runs late: the sound stays in order.
        lock (gate)
        {
            if (timer is null) return;
            for (int b = 0; b < Speed; b++)
            {
                var buf = new float[Block];
                for (int i = 0; i < Block; i++) buf[i] = sound.Length == 0 ? 0 : sound[(at + i) % sound.Length];
                at += Block;
                Samples?.Invoke(buf);
            }
        }
    }

    public void Stop()
    {
        lock (gate)
        {
            timer?.Dispose();
            timer = null;
        }
    }

    public void Dispose() => Stop();
}
