using System;

namespace SmClient.ImpactRules;

/// <summary>The two procedural melee hit sounds (fx/melee-hit-sound).</summary>
public enum HitSound
{
    /// <summary>Sound A (the power axe and power sword): a short deep impact of 0.5 s.</summary>
    Sizzle,
    /// <summary>Sound B (the thunder hammer and daemon maul): a heavier, lower impact of 0.8 s.</summary>
    Roar,
}

/// <summary>
/// fx/melee-hit-sound (the owner's requests of 2026-11-01 and 2026-11-02; **ours**, a provisional approximation). The procedural on-hit sounds, engine-free. The first version, shaped noise measured from two reference
/// clips, was "too scratchy/fuzzy" and "late" (the owner, 2026-11-02): it is replaced by an impact with nothing above about 3 kHz and its peak in the first milliseconds:
/// <list type="bullet">
/// <item>a sine falling fast to a deep note (sound A about 62 Hz, sound B about 46 Hz), with its second harmonic so that small speakers carry it too;</item>
/// <item>a low rumble: pink noise low-passed at a few hundred Hz, four poles;</item>
/// <item>a click of 12 ms low-passed at 1.8 kHz: the contact, no hiss.</item>
/// </list>
/// Each call with another seed makes a fresh noise, so repeated hits are never the same sample.
/// </summary>
public static class MeleeHitSynth
{
    public const int SampleRate = 48000;
    /// <summary>The sounds' lengths (s).</summary>
    public static float Seconds(HitSound s) => s == HitSound.Sizzle ? 0.5f : 0.8f;
    /// <summary>The levels the renders are normalised to (RMS over their length, full scale 1).</summary>
    public static float TargetRms(HitSound s) => s == HitSound.Sizzle ? 0.14f : 0.13f;
    /// <summary>Per-hit variation (ours): pitch (the playback speed) and level, so repeats do not machine-gun.</summary>
    public const float PitchSpread = 0.07f, LevelSpreadDb = 1.5f;

    /// <summary>One sound's recipe (ours): the sine's start and end notes (Hz), its fall and decay (s), the rumble's cut (Hz) and decay (s).</summary>
    readonly record struct Recipe(double NoteFrom, double NoteTo, double Fall, double Decay, double RumbleCut, double RumbleDecay);

    static Recipe RecipeOf(HitSound s) => s == HitSound.Sizzle ? new(150, 62, 0.03, 0.15, 420, 0.12) : new(120, 46, 0.04, 0.26, 300, 0.20);

    /// <summary>One rendition (mono, <see cref="SampleRate"/>) at <see cref="TargetRms"/>.</summary>
    public static float[] Render(HitSound sound, int seed)
    {
        var r = RecipeOf(sound);
        int n = (int)(Seconds(sound) * SampleRate);
        var rng = new Random(seed);
        var x = new float[n];
        double b0 = 0, b1 = 0, b2 = 0, phase = rng.NextDouble() * 0.4;
        var rumble = new double[4];
        double click = 0, clickPole = 0;
        double rumbleA = 1.0 - Math.Exp(-2.0 * Math.PI * r.RumbleCut / SampleRate), clickA = 1.0 - Math.Exp(-2.0 * Math.PI * 1800.0 / SampleRate);
        double sum = 0;
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)SampleRate;
            double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
            double w = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
            b0 = 0.99765 * b0 + w * 0.0990460; b1 = 0.96300 * b1 + w * 0.2965164; b2 = 0.57000 * b2 + w * 1.0526913;
            double v = b0 + b1 + b2 + w * 0.1848;
            for (int p = 0; p < rumble.Length; p++) { rumble[p] += rumbleA * (v - rumble[p]); v = rumble[p]; }
            click += clickA * (w - click); clickPole += clickA * (click - clickPole);

            double note = r.NoteTo + (r.NoteFrom - r.NoteTo) * Math.Exp(-t / r.Fall);
            phase += 2.0 * Math.PI * note / SampleRate;
            double tone = (Math.Sin(phase) + 0.35 * Math.Sin(2.0 * phase)) * Math.Exp(-t / r.Decay);
            double y = tone + 0.9 * v * Math.Exp(-t / r.RumbleDecay) + 0.3 * clickPole * Math.Exp(-t / 0.012);
            y *= Math.Min(1.0, t / 0.0008) * Math.Min(1.0, (Seconds(sound) - t) / 0.04);   // no click at either end
            x[i] = (float)y;
            sum += y * y;
        }
        // Scaled to the target level through a soft knee; the gain is refined for the knee's loss.
        float gain = (float)(TargetRms(sound) / Math.Sqrt(sum / n));
        for (int pass = 0; pass < 3; pass++)
        {
            double q = 0;
            for (int i = 0; i < n; i++) { float y = Knee * MathF.Tanh(x[i] * gain / Knee); q += y * y; }
            gain *= (float)(TargetRms(sound) / Math.Sqrt(q / n));
        }
        for (int i = 0; i < n; i++) x[i] = Knee * MathF.Tanh(x[i] * gain / Knee);
        return x;
    }

    const float Knee = 0.9f;

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
