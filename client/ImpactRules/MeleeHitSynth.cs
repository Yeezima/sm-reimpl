using System;

namespace SmClient.ImpactRules;

/// <summary>The two procedural melee hit sounds (fx/melee-hit-sound).</summary>
public enum HitSound
{
    /// <summary>Sound A (the power axe, power sword and combat knife): a 0.64 s pink sizzle that swells over 100 ms.</summary>
    Sizzle,
    /// <summary>Sound B (the chainsword, the Tactical's and Assault's charge, the thunder hammer and the daemon maul): an instant-onset roar of about a second with a crackling tail.</summary>
    Roar,
}

/// <summary>
/// fx/melee-hit-sound (the owner's requests of 2026-11-01; **ours**, a provisional approximation: nobody has heard it yet). The procedural on-hit sounds, engine-free, built from
/// <b>measurements of two reference clips</b> (analysis numbers only; no sample of either is used or stored; rendered values against the clips' are in <c>docs/milestones.md</c>).
/// <list type="bullet">
/// <item><b>Sound A</b> (clip 1, 0.64 s): Gaussian noise (kurtosis 3.1), pink (about -3.6 dB per octave from 75 Hz to 10 kHz, falling faster above 12 kHz, no tonal ring), attack from
/// about 10 ms to -20 dB below the plateau, -10 dB at 72 ms, -3 dB at 101 ms, a plateau to 340 ms with a flutter of 4 to 19 Hz, -10 dB at 384 ms, -20 dB at 565 ms.</item>
/// <item><b>Sound B</b> (clip 2, the one isolated hit of an 11.5 s clip that otherwise holds a steady roar I did not model): noise of kurtosis 2.2, -2.2 dB per octave from 50 Hz to 3.2 kHz,
/// 10 dB less at 6.4 kHz and nothing above 10 kHz, an instant attack (10 ms from -36 to -14 dB), a plateau of about 0.55 s with a deep slow flutter (2 to 14 Hz, dips of 10 dB), then a sag to about -12 dB under the plateau by 0.9 s that holds as a crackling tail (kurtosis 24).</item>
/// </list>
/// Each call with another seed makes a fresh noise, so repeated hits are never the same sample.
/// </summary>
public static class MeleeHitSynth
{
    public const int SampleRate = 48000;
    /// <summary>The sounds' lengths (s): clip 1's 0.64 s; sound B's body and tail, 1.6 s.</summary>
    public static float Seconds(HitSound s) => s == HitSound.Sizzle ? 0.64f : 1.6f;
    /// <summary>The levels the renders are normalised to (RMS over their length, full scale 1): the clips' own over the same stretch.</summary>
    public static float TargetRms(HitSound s) => s == HitSound.Sizzle ? 0.1235f : 0.1020f;
    /// <summary>Per-hit variation (ours): pitch (the playback speed) and level, so repeats do not machine-gun.</summary>
    public const float PitchSpread = 0.07f, LevelSpreadDb = 1.5f;

    /// <summary>The envelope (seconds, dB re the plateau): linear in dB between points; silent before the first and after the last.</summary>
    static (float T, float Db)[] Envelope(HitSound s) => s == HitSound.Sizzle ? EnvA : EnvB;

    static readonly (float T, float Db)[] EnvA =
    [
        (0.000f, -60f), (0.012f, -20f), (0.050f, -16f), (0.072f, -10f), (0.101f, -3f), (0.130f, -1f), (0.200f, -2.5f), (0.300f, -2.5f), (0.340f, -3f),
        (0.365f, -3.5f), (0.400f, -6f), (0.450f, -9f), (0.500f, -12f), (0.540f, -15f), (0.580f, -18f), (0.620f, -20f), (0.640f, -40f),
    ];

    static readonly (float T, float Db)[] EnvB =
    [
        (0.000f, -40f), (0.004f, -20f), (0.010f, -1f), (0.020f, 0f), (0.550f, -1f), (0.620f, -4f), (0.700f, -8f), (0.780f, -11f), (0.900f, -12.5f), (1.200f, -13f), (1.500f, -15f), (1.580f, -18f), (1.600f, -45f),
    ];

    /// <summary>The envelope in dB at <paramref name="t"/> seconds (re the plateau).</summary>
    public static float EnvelopeDb(HitSound sound, float t)
    {
        var env = Envelope(sound);
        if (t <= env[0].T) return env[0].Db;
        for (int i = 1; i < env.Length; i++)
            if (t <= env[i].T)
            {
                float k = (t - env[i - 1].T) / (env[i].T - env[i - 1].T);
                return env[i - 1].Db + (env[i].Db - env[i - 1].Db) * k;
            }
        return env[^1].Db;
    }

    /// <summary>One rendition (mono, <see cref="SampleRate"/>): shaped noise with a flutter and the envelope, at <see cref="TargetRms"/>.</summary>
    public static float[] Render(HitSound sound, int seed)
    {
        bool a = sound == HitSound.Sizzle;
        int n = (int)(Seconds(sound) * SampleRate);
        var rng = new Random(seed);
        var x = new float[n];
        // Gaussian white noise (Box-Muller): Kellet's pink filter, mixed with some white for B's flatter middle; low-passed (A: one pole at 10 kHz; B: four at 7 kHz); 40 Hz high-pass.
        double b0 = 0, b1 = 0, b2 = 0, b3 = 0, b4 = 0, b5 = 0, b6 = 0;
        int poles = 2;
        var lp = new double[8];
        double lpA = 1.0 - Math.Exp(-2.0 * Math.PI * (a ? 9000.0 : WhiteCut) / SampleRate);
        double hpIn = 0, hpOut = 0, hpA = Math.Exp(-2.0 * Math.PI * 40.0 / SampleRate);
        double white = a ? 0.0 : WhiteMix;
        for (int i = 0; i < n; i++)
        {
            double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
            double w = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
            b0 = 0.99886 * b0 + w * 0.0555179; b1 = 0.99332 * b1 + w * 0.0750759; b2 = 0.96900 * b2 + w * 0.1538520;
            b3 = 0.86650 * b3 + w * 0.3104856; b4 = 0.55000 * b4 + w * 0.5329522; b5 = -0.7616 * b5 - w * 0.0168980;
            double v = b0 + b1 + b2 + b3 + b4 + b5 + b6 + w * 0.5362; b6 = w * 0.115926;
            v += white * w;
            for (int p = 0; p < poles; p++) { lp[p] += lpA * (v - lp[p]); v = lp[p]; }
            double hp = hpA * (hpOut + v - hpIn); hpIn = v; hpOut = hp;
            x[i] = (float)hp;
        }
        // B: a steep low-pass at 9 kHz (the clip has nothing above it), a 255-tap windowed sinc.
        if (!a) x = BrickLowPass(x, 9000.0);
        // The flutter (random phases) and the envelope. A: modest (the noise itself is uneven); B: deep and slow, with 10 dB dips.
        var freqs = a ? new[] { 4.0, 8.0, 19.0 } : new[] { 2.0, 5.0, 7.0, 14.0 };
        var weights = a ? new[] { 0.5, 0.35, 0.15 } : new[] { 0.35, 0.3, 0.25, 0.1 };
        var phase = new double[freqs.Length];
        for (int k = 0; k < phase.Length; k++) phase[k] = rng.NextDouble() * 6.2832;
        float depth = a ? 0.3f : FlutterDepthB;
        double sum = 0;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)SampleRate;
            double f = 0;
            for (int k = 0; k < freqs.Length; k++) f += weights[k] * Math.Sin(6.2832 * freqs[k] * t + phase[k]);
            float flutter = MathF.Max(0.2f, 1f + depth * (float)f);
            x[i] *= MathF.Pow(10f, EnvelopeDb(sound, t) / 20f) * flutter;
            sum += x[i] * x[i];
        }
        // B's crackle: sparse clicks (a few a second at the start of the tail, fewer later) laid over the tail from 0.95 s.
        if (!a)
        {
            int clicks = 0;
            for (int i = (int)(0.6f * SampleRate); i < n - 200; i++)
            {
                float t = i / (float)SampleRate;
                double rate = 40.0 * Math.Exp(-(t - 0.6) * 1.3);   // per second
                if (rng.NextDouble() < rate / SampleRate)
                {
                    clicks++;
                    float amp = (float)(0.06 + 0.12 * rng.NextDouble()) * (rng.Next(2) == 0 ? 1f : -1f);
                    float ring = 0.35f + (float)rng.NextDouble() * 0.5f;
                    for (int j = 0; j < 160; j++) x[i + j] += amp * MathF.Exp(-j / (24f * ring)) * MathF.Sin(j * (0.55f + 0.5f * (float)rng.NextDouble()));
                }
            }
            sum = 0; for (int i = 0; i < n; i++) sum += x[i] * x[i];
        }
        // Scaled to the target level, through a soft knee (the clips' crest factor is about 5.6, plain Gaussian noise's 7); the gain is refined once for the knee's loss.
        float gain = (float)(TargetRms(sound) / Math.Sqrt(sum / n));
        for (int pass = 0; pass < 3; pass++)
        {
            double q = 0;
            for (int i = 0; i < n; i++) { float y = KneeOf(sound) * MathF.Tanh(x[i] * gain / KneeOf(sound)); q += y * y; }
            gain *= (float)(TargetRms(sound) / Math.Sqrt(q / n));
        }
        for (int i = 0; i < n; i++) x[i] = KneeOf(sound) * MathF.Tanh(x[i] * gain / KneeOf(sound));
        return x;
    }

    static float KneeOf(HitSound s) => s == HitSound.Sizzle ? 0.85f : 0.62f;

    static float[] BrickLowPass(float[] x, double cutoffHz)
    {
        const int taps = 255; int m = taps / 2;
        var h = new double[taps]; double fc = cutoffHz / SampleRate, sum = 0;
        for (int i = 0; i < taps; i++)
        {
            int k = i - m;
            double sinc = k == 0 ? 2 * fc : Math.Sin(2 * Math.PI * fc * k) / (Math.PI * k);
            double win = 0.42 - 0.5 * Math.Cos(2 * Math.PI * i / (taps - 1)) + 0.08 * Math.Cos(4 * Math.PI * i / (taps - 1));
            h[i] = sinc * win; sum += h[i];
        }
        var y = new float[x.Length];
        for (int i = 0; i < x.Length; i++)
        {
            double acc = 0;
            for (int j = 0; j < taps; j++) { int k = i + m - j; if (k >= 0 && k < x.Length) acc += h[j] * x[k]; }
            y[i] = (float)(acc / sum);
        }
        return y;
    }

    /// <summary>Tuning of sound B (ours, set against the clip's measurements): the white share of the noise, the low-pass cut (Hz) and the flutter's depth.</summary>
    public const double WhiteMix = 3.5, WhiteCut = 11500;
    public const float FlutterDepthB = 0.55f;

    /// <summary>16-bit PCM of a render, clipped at full scale.</summary>
    public static short[] ToPcm16(float[] x)
    {
        var s = new short[x.Length];
        for (int i = 0; i < x.Length; i++) s[i] = (short)Math.Clamp((int)MathF.Round(x[i] * 32767f), -32767, 32767);
        return s;
    }

    /// <summary>The playback speed and the level (dB) of the <paramref name="hit"/>-th hit (deterministic from a hash of it, within the spreads).</summary>
    public static (float Pitch, float Db) Variation(uint hit)
    {
        uint h = hit * 2654435761u; h ^= h >> 15; h *= 2246822519u; h ^= h >> 13;
        float a = (h & 0xFFFF) / 65535f, b = ((h >> 16) & 0xFFFF) / 65535f;
        return (1f + (a * 2f - 1f) * PitchSpread, (b * 2f - 1f) * LevelSpreadDb);
    }

    /// <summary>Which procedural sound replaces a melee wargear file's on-hit sound, or null (it keeps its own): A for the power axe and power sword; B for the thunder hammer and daemon maul; both races. The chainsword, combat knife and every charge keep their original sounds (hotfix 2026-11-01).</summary>
    public static HitSound? Replaces(string? wargear)
    {
        if (wargear is null) return null;
        if (wargear.Contains("thunder_hammer", StringComparison.OrdinalIgnoreCase) || wargear.Contains("power_mace", StringComparison.OrdinalIgnoreCase)
            ) return HitSound.Roar;
        if (wargear.Contains("power_axe", StringComparison.OrdinalIgnoreCase) || wargear.Contains("powersword", StringComparison.OrdinalIgnoreCase)) return HitSound.Sizzle;
        return null;
    }
}
