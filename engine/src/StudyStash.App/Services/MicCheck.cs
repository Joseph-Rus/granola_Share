using System.Runtime.InteropServices;
using StudyStash.Audio;
using StudyStash.Core;

namespace StudyStash.App.Services;

/// <summary>
/// Setup's microphone step: opens a microphone (through whatever <see cref="Open"/> is given — never the real one in
/// a test) and counts its loudness in 50 ms blocks, so the step's waveform can show the student Study Stash hearing
/// them. Pure and thread-safe: nothing here touches a view model, so a caller (Setup, Shell) copies <see cref="Heard"/>
/// and <see cref="Levels"/> across on its own schedule, the way <see cref="Recorder"/> is read for its waveform.
/// </summary>
public sealed class MicCheck : IDisposable
{
    public const int Bars = 24;
    const int BlockSamples = Sound.Rate / 20; // 50 ms

    readonly Lock gate = new();
    readonly double[] levels = new double[Bars];
    readonly List<float> buf = [];
    int at;
    volatile bool heard;
    IAudioSource? mic;

    /// <summary>A level has passed the "Study Stash hears you" mark since the microphone opened.</summary>
    public bool Heard => heard;

    /// <summary>Opens the microphone and starts counting; does nothing if one is already open.</summary>
    public void Open(Func<IAudioSource> open)
    {
        if (mic is not null) return;
        lock (gate)
        {
            Array.Clear(levels);
            buf.Clear();
            at = 0;
        }
        heard = false;
        var m = open();
        m.Samples += OnSamples;
        m.Start();
        mic = m;
    }

    void OnSamples(float[] samples)
    {
        lock (gate)
        {
            foreach (float s in samples)
            {
                buf.Add(s);
                if (buf.Count < BlockSamples) continue;
                double level = Sound.Level(Sound.Db(CollectionsMarshal.AsSpan(buf)));
                levels[at] = level;
                at = (at + 1) % Bars;
                buf.Clear();
                if (level > 0.25) heard = true;
            }
        }
    }

    /// <summary>The last bars, oldest first, 0 to 1: for the waveform.</summary>
    public double[] Levels()
    {
        lock (gate)
        {
            var result = new double[Bars];
            for (int i = 0; i < Bars; i++) result[i] = levels[(at + i) % Bars];
            return result;
        }
    }

    /// <summary>Closes the microphone, if one is open. Safe to call more than once.</summary>
    public void Close()
    {
        if (mic is null) return;
        var m = mic;
        mic = null;
        m.Samples -= OnSamples;
        try
        {
            m.Stop();
        }
        catch (Exception e) when (e is InvalidOperationException or IOException)
        {
        }
        m.Dispose();
    }

    public void Dispose() => Close();
}
