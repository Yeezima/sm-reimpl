namespace SmContent;

/// <summary>
/// One decoded sound placed in an offline mix (<see cref="OfflineMixer"/>): interleaved float PCM at <see cref="SampleRate"/>, the delay after the post
/// (seconds), the gain (dB) and the pitch (cents) the resolved voice carries.
/// </summary>
public sealed record MixTrack(float[] Pcm, int Channels, int SampleRate, float DelaySeconds, float GainDb, float PitchCents, float Lpf = 0f);

/// <summary>
/// Offline mixer for preview renders (<c>smtool audio-render</c>): sums tracks into one interleaved float buffer at a fixed output rate. Per track:
/// gain 10^(dB/20); pitch as a playback-rate change (rate x 2^(cents/1200), linear interpolation, so the sound gets shorter and higher when pitched up, as a
/// Wwise voice pitch does); delay as a start offset; a mono source is copied to both channels of a stereo mix, a stereo source is averaged into a mono mix.
/// A voice's low-pass value (Wwise 0..100) is applied as a two-pole Butterworth at <see cref="CutoffHz"/>: an APPROXIMATION of the client's Godot bus filters (steps, 12 dB/oct, resonance 0.5).
/// Limits: no 3D attenuation, no bus effects (reverb), no looping; the sum is not normalised, <see cref="ToPcm16"/> clips.
/// </summary>
public static class OfflineMixer
{
    public const int DefaultRate = 48000;

    /// <summary>The cutoff (Hz) a Wwise LPF value stands for: the client's mapping (SoundSystem.CutoffHz, provisional): exponential from 20 kHz at 0 to 150 Hz at 100.</summary>
    public static float CutoffHz(float lpf) => 20000f * MathF.Pow(150f / 20000f, Math.Clamp(lpf, 0f, 100f) / 100f);

    /// <summary>A two-pole Butterworth low-pass (RBJ biquad, Q = 1/sqrt 2) over one channel of interleaved samples; a cutoff at or above 0.49 of the rate leaves it unchanged.</summary>
    public static float[] LowPass(float[] pcm, int channels, int rate, float cutoffHz)
    {
        if (cutoffHz >= 0.49f * rate || cutoffHz <= 0f) return pcm;
        double w = 2.0 * Math.PI * cutoffHz / rate, cos = Math.Cos(w), alpha = Math.Sin(w) / (2.0 * 0.70710678);
        double b0 = (1 - cos) / 2, b1 = 1 - cos, b2 = b0, a0 = 1 + alpha, a1 = -2 * cos, a2 = 1 - alpha;
        b0 /= a0; b1 /= a0; b2 /= a0; a1 /= a0; a2 /= a0;
        var o = new float[pcm.Length];
        for (int c = 0; c < channels; c++)
        {
            double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
            for (int i = c; i < pcm.Length; i += channels)
            {
                double x = pcm[i], y = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
                x2 = x1; x1 = x; y2 = y1; y1 = y;
                o[i] = (float)y;
            }
        }
        return o;
    }

    /// <summary>Linear gain of <paramref name="db"/> decibels.</summary>
    public static float Gain(float db) => MathF.Pow(10f, db / 20f);

    /// <summary>Playback-rate factor of <paramref name="cents"/>: 2^(cents/1200).</summary>
    public static float PitchRatio(float cents) => MathF.Pow(2f, cents / 1200f);

    /// <summary>Output frames a track covers at <paramref name="outRate"/> (delay included).</summary>
    public static int FramesOf(MixTrack t, int outRate)
    {
        if (t.Channels <= 0 || t.SampleRate <= 0 || t.Pcm.Length < t.Channels) return 0;
        double srcFrames = t.Pcm.Length / t.Channels;
        double step = (double)t.SampleRate / outRate * PitchRatio(t.PitchCents);
        return (int)Math.Round(Math.Max(0, t.DelaySeconds) * outRate) + (int)Math.Ceiling(srcFrames / step);
    }

    /// <summary>
    /// Mixes <paramref name="tracks"/> into <paramref name="outChannels"/> (1 or 2) interleaved channels at <paramref name="outRate"/>, with
    /// <paramref name="tailSeconds"/> of silence after the last track.
    /// </summary>
    public static float[] Mix(IReadOnlyList<MixTrack> tracks, int outRate = DefaultRate, int outChannels = 2, float tailSeconds = 0f)
    {
        if (outChannels is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(outChannels));
        if (outRate <= 0) throw new ArgumentOutOfRangeException(nameof(outRate));
        int frames = 0;
        foreach (var t in tracks) frames = Math.Max(frames, FramesOf(t, outRate));
        frames += (int)Math.Round(Math.Max(0f, tailSeconds) * outRate);
        var mix = new float[frames * outChannels];
        foreach (var t in tracks) Add(mix, outChannels, outRate, t);
        return mix;
    }

    static void Add(float[] mix, int outChannels, int outRate, MixTrack t)
    {
        if (t.Channels <= 0 || t.SampleRate <= 0 || t.Pcm.Length < t.Channels) return;
        int srcFrames = t.Pcm.Length / t.Channels;
        double step = (double)t.SampleRate / outRate * PitchRatio(t.PitchCents);
        float gain = Gain(t.GainDb);
        // The client filters after the pitch change: the cutoff in source Hz is the output cutoff over the pitch ratio.
        var src = t.Lpf > 0f ? LowPass(t.Pcm, t.Channels, t.SampleRate, CutoffHz(t.Lpf) / PitchRatio(t.PitchCents)) : t.Pcm;
        t = t with { Pcm = src };
        int start = (int)Math.Round(Math.Max(0, t.DelaySeconds) * outRate);
        int n = (int)Math.Ceiling(srcFrames / step);
        for (int i = 0; i < n; i++)
        {
            int at = start + i;
            if ((at + 1) * outChannels > mix.Length) break;
            double pos = i * step;
            int i0 = (int)pos;
            if (i0 >= srcFrames) break;
            int i1 = Math.Min(i0 + 1, srcFrames - 1);
            float f = (float)(pos - i0);
            float l = Sample(t, i0, i1, f, 0);
            float r = t.Channels >= 2 ? Sample(t, i0, i1, f, 1) : l;
            if (outChannels == 2) { mix[at * 2] += l * gain; mix[at * 2 + 1] += r * gain; }
            else mix[at] += (t.Channels >= 2 ? (l + r) * 0.5f : l) * gain;
        }
    }

    static float Sample(MixTrack t, int i0, int i1, float f, int channel)
    {
        float a = t.Pcm[i0 * t.Channels + channel], b = t.Pcm[i1 * t.Channels + channel];
        return a + (b - a) * f;
    }

    /// <summary>Largest absolute sample (linear), 0 for an empty buffer.</summary>
    public static float Peak(float[] mix)
    {
        float p = 0f;
        foreach (float s in mix) p = Math.Max(p, Math.Abs(s));
        return p;
    }

    /// <summary>16-bit PCM with hard clipping to [-1, 1].</summary>
    public static short[] ToPcm16(float[] mix)
    {
        var o = new short[mix.Length];
        for (int i = 0; i < mix.Length; i++) o[i] = (short)Math.Clamp(MathF.Round(Math.Clamp(mix[i], -1f, 1f) * 32767f), -32768f, 32767f);
        return o;
    }

    /// <summary>A canonical 44-byte-header PCM WAV of interleaved 16-bit samples.</summary>
    public static byte[] WavBytes(short[] pcm, int channels, int rate)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        int dataBytes = pcm.Length * 2;
        w.Write("RIFF"u8); w.Write(36 + dataBytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)channels); w.Write(rate);
        w.Write(rate * channels * 2); w.Write((short)(channels * 2)); w.Write((short)16);
        w.Write("data"u8); w.Write(dataBytes);
        foreach (short s in pcm) w.Write(s);
        w.Flush();
        return ms.ToArray();
    }
}
