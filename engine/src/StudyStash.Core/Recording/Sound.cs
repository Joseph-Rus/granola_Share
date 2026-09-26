using System.Buffers.Binary;

namespace StudyStash.Core;

/// <summary>Where a recording's sound goes as it's recorded: a WAV file (a test gives one whose disk fills up).</summary>
public interface IWavSink : IDisposable
{
    /// <summary>Seconds written so far.</summary>
    double Seconds { get; }

    /// <summary>More 16 kHz mono sound. Throws IOException when the disk won't take it.</summary>
    void Write(ReadOnlySpan<float> samples);
}

/// <summary>
/// A recording on disk: 16-bit mono WAV. The header's sizes are brought up to date every few seconds, so a crash or a
/// dead battery leaves a file that plays up to the last flush.
/// </summary>
public sealed class WavWriter : IWavSink
{
    public const int HeaderBytes = 44;
    readonly FileStream file;
    readonly byte[] scratch = new byte[8192];
    DateTime flushedAt = DateTime.UtcNow;

    public int SampleRate { get; }
    public long Samples { get; private set; }
    public double Seconds => Samples / (double)SampleRate;
    public string Path { get; }

    /// <summary>A new file, or (append) more of one a crash cut short.</summary>
    public WavWriter(string path, int sampleRate = Sound.Rate, bool append = false)
    {
        Path = path;
        SampleRate = sampleRate;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        if (append && File.Exists(path) && new FileInfo(path).Length >= HeaderBytes)
        {
            file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            Samples = (file.Length - HeaderBytes) / 2;
            file.SetLength(HeaderBytes + Samples * 2); // an odd byte from a torn write
            file.Seek(0, SeekOrigin.End);
        }
        else
        {
            file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
            file.Write(Header(0, sampleRate));
        }
    }

    public static byte[] Header(long samples, int sampleRate)
    {
        var h = new byte[HeaderBytes];
        uint data = (uint)Math.Min(uint.MaxValue - 36, samples * 2);
        "RIFF"u8.CopyTo(h);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(4), 36 + data);
        "WAVEfmt "u8.CopyTo(h.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(20), 1); // PCM
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(22), 1); // mono
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(24), (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(28), (uint)sampleRate * 2);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(32), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(34), 16);
        "data"u8.CopyTo(h.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(40), data);
        return h;
    }

    public void Write(ReadOnlySpan<float> samples)
    {
        while (samples.Length > 0)
        {
            int n = Math.Min(samples.Length, scratch.Length / 2);
            for (int i = 0; i < n; i++)
            {
                float s = Math.Clamp(samples[i], -1f, 1f);
                BinaryPrimitives.WriteInt16LittleEndian(scratch.AsSpan(i * 2), (short)Math.Round(s * short.MaxValue));
            }
            file.Write(scratch, 0, n * 2);
            Samples += n;
            samples = samples[n..];
        }
        if ((DateTime.UtcNow - flushedAt).TotalSeconds >= 5) Flush();
    }

    public void Flush()
    {
        long at = file.Position;
        file.Seek(0, SeekOrigin.Begin);
        file.Write(Header(Samples, SampleRate));
        file.Seek(at, SeekOrigin.Begin);
        file.Flush(flushToDisk: true);
        flushedAt = DateTime.UtcNow;
    }

    /// <summary>Brings the header up to date and closes the file. The file is closed even when the disk is full
    /// (and that is still thrown).</summary>
    public void Dispose()
    {
        try
        {
            Flush();
        }
        finally
        {
            file.Dispose();
        }
    }
}

/// <summary>Sound as Whisper takes it: 16 kHz mono floats.</summary>
public static class Sound
{
    public const int Rate = 16000;

    /// <summary>Where a WAV's sound is: its rate, and where the samples start and how many bytes they fill.</summary>
    public readonly record struct WavInfo(int Rate, long DataStart, long DataBytes)
    {
        public long Samples => DataBytes / 2;
        public double Seconds => Samples / (double)Rate;
    }

    /// <summary>A 16-bit mono WAV's layout. Other tools put more chunks before the sound.</summary>
    public static WavInfo Wav(string path)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var head = new byte[12];
        if (f.Read(head, 0, 12) < 12 || !head.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !head.AsSpan(8, 4).SequenceEqual("WAVE"u8))
            throw new InvalidDataException($"{path} is not a WAV file");
        int rate = 0;
        var chunk = new byte[8];
        while (f.Read(chunk, 0, 8) == 8)
        {
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan(4));
            if (chunk.AsSpan(0, 4).SequenceEqual("fmt "u8))
            {
                var fmt = new byte[Math.Max(16, (int)size)];
                f.ReadExactly(fmt, 0, (int)size);
                if (BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(0)) != 1 || BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(2)) != 1
                    || BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(14)) != 16)
                    throw new InvalidDataException($"{path} isn't 16-bit mono PCM");
                rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(fmt.AsSpan(4));
                if (size % 2 == 1) f.Seek(1, SeekOrigin.Current);
            }
            else if (chunk.AsSpan(0, 4).SequenceEqual("data"u8))
            {
                if (rate == 0) break;
                long start = f.Position, rest = f.Length - start;
                // Our own recordings (sound straight after the 44-byte header) run to the end of the file: their
                // header lags behind. Another tool's file may have more chunks after the sound, so its size holds.
                long bytes = start == WavWriter.HeaderBytes || size == 0 || size > rest ? rest : size;
                return new WavInfo(rate, start, bytes - bytes % 2);
            }
            else
            {
                f.Seek(size + size % 2, SeekOrigin.Current);
            }
        }
        throw new InvalidDataException($"{path} has no sound in it");
    }

    /// <summary>A 16-bit mono WAV's samples as floats in [-1, 1].</summary>
    public static float[] ReadWav(string path, long startSample = 0, long count = -1)
    {
        var info = Wav(path);
        startSample = Math.Clamp(startSample, 0, info.Samples);
        long n = count < 0 ? info.Samples - startSample : Math.Min(count, info.Samples - startSample);
        var bytes = new byte[n * 2];
        using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            f.Seek(info.DataStart + startSample * 2, SeekOrigin.Begin);
            f.ReadExactly(bytes);
        }
        var result = new float[n];
        for (long i = 0; i < n; i++) result[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan((int)(i * 2))) / (float)short.MaxValue;
        return result;
    }

    public static double WavSeconds(string path) => Wav(path).Seconds;

    /// <summary>Loudness of a stretch as dBFS (0 is full scale; -100 for silence).</summary>
    public static double Db(ReadOnlySpan<float> samples)
    {
        if (samples.Length == 0) return -100;
        double sum = 0;
        foreach (float s in samples) sum += s * s;
        double rms = Math.Sqrt(sum / samples.Length);
        return rms <= 1e-5 ? -100 : 20 * Math.Log10(rms);
    }

    /// <summary>A level bar's height, 0 to 1: -60 dBFS and below is flat, full scale is full.</summary>
    public static double Level(double db) => Math.Clamp((db + 60) / 60, 0, 1);
}

/// <summary>
/// Turns what a microphone gives (any rate, any number of channels, interleaved) into 16 kHz mono. The channels are
/// averaged, then a windowed-sinc filter takes out what 16 kHz can't hold and picks the new samples, so speech keeps
/// its consonants and nothing folds back as hiss.
/// </summary>
public sealed class Resampler
{
    readonly int channels;
    readonly double step; // input samples per output sample
    readonly double cutoff; // cycles per input sample
    readonly int half; // the filter reaches this many input samples each side
    readonly List<float> buf = [];
    long bufStart; // the input index of buf[0]
    double pos; // the input position of the next output sample

    public int InRate { get; }
    public int OutRate { get; }

    public Resampler(int inRate, int channels, int outRate = Sound.Rate)
    {
        if (inRate <= 0 || channels <= 0) throw new ArgumentOutOfRangeException(nameof(inRate), "a microphone needs a rate and a channel");
        InRate = inRate;
        OutRate = outRate;
        this.channels = channels;
        step = inRate / (double)outRate;
        cutoff = Math.Min(1.0, outRate / (double)inRate) * 0.475;
        half = (int)Math.Ceiling(10 / (2 * cutoff));
    }

    double Kernel(double x)
    {
        double ax = Math.Abs(x);
        if (ax >= half) return 0;
        double sinc = ax < 1e-9 ? 1 : Math.Sin(2 * Math.PI * cutoff * x) / (Math.PI * x) / (2 * cutoff);
        double w = 0.42 + 0.5 * Math.Cos(Math.PI * x / half) + 0.08 * Math.Cos(2 * Math.PI * x / half); // Blackman
        return 2 * cutoff * sinc * w;
    }

    /// <summary>The 16 kHz samples this much input makes (some come out with the next call: the filter looks ahead).</summary>
    public float[] Process(ReadOnlySpan<float> interleaved)
    {
        int frames = interleaved.Length / channels;
        if (InRate == OutRate)
        {
            var same = new float[frames];
            for (int i = 0; i < frames; i++) same[i] = Mix(interleaved, i);
            return same;
        }
        for (int i = 0; i < frames; i++) buf.Add(Mix(interleaved, i));
        return Drain(bufStart + buf.Count);
    }

    /// <summary>The last samples, once the recording has stopped.</summary>
    public float[] Flush()
    {
        if (InRate == OutRate) return [];
        long end = bufStart + buf.Count;
        for (int i = 0; i < half + 1; i++) buf.Add(0);
        return Drain(end + half + 1, until: end);
    }

    float Mix(ReadOnlySpan<float> interleaved, int frame)
    {
        if (channels == 1) return interleaved[frame];
        float sum = 0;
        for (int c = 0; c < channels; c++) sum += interleaved[frame * channels + c];
        return sum / channels;
    }

    float[] Drain(long available, long? until = null)
    {
        var output = new List<float>();
        while (pos + half < available && (until is null || pos < until))
        {
            long lo = (long)Math.Ceiling(pos - half), hi = (long)Math.Floor(pos + half);
            double acc = 0;
            for (long k = lo; k <= hi; k++)
            {
                long at = k - bufStart;
                if (at < 0 || at >= buf.Count) continue;
                acc += buf[(int)at] * Kernel(pos - k);
            }
            output.Add((float)acc);
            pos += step;
        }
        long keepFrom = (long)Math.Floor(pos) - half - 1;
        int drop = (int)Math.Clamp(keepFrom - bufStart, 0, buf.Count);
        if (drop > 0)
        {
            buf.RemoveRange(0, drop);
            bufStart += drop;
        }
        return [.. output];
    }
}

/// <summary>A piece of a recording for Whisper: where it starts (in samples at 16 kHz) and its sound.</summary>
public sealed record Chunk(long Start, float[] Samples)
{
    public double StartSeconds => Start / (double)Sound.Rate;
    public double EndSeconds => (Start + Samples.Length) / (double)Sound.Rate;

    /// <summary>Nobody spoke: the loudest third of a second stays below the floor. Whisper makes words up out of
    /// silence ("Thank you."), so these aren't transcribed.</summary>
    public bool Silent(double floorDb = -48)
    {
        const int window = Sound.Rate / 3;
        for (int i = 0; i + window <= Samples.Length; i += window / 2)
            if (Sound.Db(Samples.AsSpan(i, window)) > floorDb) return false;
        return Samples.Length < window ? Sound.Db(Samples) <= floorDb : true;
    }
}

/// <summary>
/// Where to cut a recording into pieces Whisper takes in one go (it reads 30 seconds at a time): at the quietest
/// moment between <c>min</c> and <c>max</c> seconds in, so no word is split.
/// </summary>
public static class Segmenter
{
    const int Frame = Sound.Rate / 50; // 20 ms
    const int Smooth = 15; // frames: a 300 ms stretch of quiet

    /// <summary>How many samples of <paramref name="window"/> (what's left of a recording, from where Whisper got
    /// to) make the next piece. 0 means wait: a lecture still recording hasn't a full piece yet. Once it has
    /// stopped (<paramref name="final"/>), the last piece is whatever is left.</summary>
    public static int CutLength(ReadOnlySpan<float> window, bool final, double minSeconds = 20, double maxSeconds = 29.5)
    {
        int max = (int)(maxSeconds * Sound.Rate);
        if (window.Length < max) return final ? window.Length : 0;
        int from = (int)(minSeconds * Sound.Rate) / Frame, to = max / Frame;
        var energy = new double[to];
        for (int f = 0; f < to; f++)
        {
            double sum = 0;
            for (int i = f * Frame; i < (f + 1) * Frame; i++) sum += window[i] * window[i];
            energy[f] = sum;
        }
        int best = to - 1;
        double bestSum = double.MaxValue;
        for (int f = Math.Max(from, Smooth / 2); f + Smooth / 2 < to; f++)
        {
            double sum = 0;
            for (int j = f - Smooth / 2; j <= f + Smooth / 2; j++) sum += energy[j];
            if (sum < bestSum)
            {
                bestSum = sum;
                best = f;
            }
        }
        return best * Frame + Frame / 2;
    }
}
