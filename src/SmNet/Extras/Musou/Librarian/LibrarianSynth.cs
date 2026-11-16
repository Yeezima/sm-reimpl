using System.Text;

namespace SmNet.Extras.Musou;

/// <summary>
/// The Librarian's lightning sounds made in code (librarian-lightning, owner 2026-11-10: no downloaded recordings; "build the lightning sound from the game's own audio ... plus procedural synthesis where it
/// helps (crack transients, a sizzle from filtered noise, low thunder tails)"; **ours**): mono 16-bit PCM WAV bytes generated at run time from fixed seeds, so nothing is stored. They are laid under the
/// install's own events (<c>NetworkView.LibrarianAudio.cs</c>). Engine-free and deterministic (tested).
/// <list type="bullet">
/// <item><see cref="Crack"/>: a strike's tearing crack: a bright noise burst with micro-crackles, a darker body and a falling low thump.</item>
/// <item><see cref="Sizzle"/>: a seamless loop of an arc: band-limited noise, random crackle pops and a mains-like hum with a slow wobble.</item>
/// <item><see cref="Snap"/>: a high-voltage snap: a click and a falling zap chirp.</item>
/// <item><see cref="Thunder"/>: a thunder roll: a short crack, then low brown noise in a few swells, dying away.</item>
/// <item><see cref="Boom"/>: the Shockwave's thunderclap: a crack, a deep falling thump and a heavy roll.</item>
/// </list>
/// </summary>
public static class LibrarianSynth
{
    public const int Rate = 44100;

    /// <summary>A small repeatable random stream (xorshift).</summary>
    struct Rng
    {
        uint _s;
        public Rng(uint seed) { _s = seed * 2654435761u + 0x9E3779B9u; if (_s == 0) _s = 1; }
        public float Next() { _s ^= _s << 13; _s ^= _s >> 17; _s ^= _s << 5; return (_s & 0xFFFFFF) / 16777216f; }
        public float Signed() => Next() * 2f - 1f;
    }

    /// <summary>One-pole low-pass and high-pass coefficients for a cut-off (Hz).</summary>
    static float LowK(float hz) => 1f - MathF.Exp(-2f * MathF.PI * hz / Rate);

    /// <summary>A strike's crack, <paramref name="seconds"/> long (variation by <paramref name="seed"/>).</summary>
    public static float[] Crack(uint seed, float seconds = 0.55f)
    {
        var x = new float[(int)(seconds * Rate)];
        var r = new Rng(seed);
        float lp = 0f, body = 0f;
        float kHp = LowK(900f), kBody = LowK(550f);
        // Micro-crackles in the first 80 ms: a few short bursts.
        Span<(int At, int Len, float Amp)> rips = stackalloc (int, int, float)[10];
        for (int i = 0; i < rips.Length; i++) rips[i] = ((int)(r.Next() * 0.08f * Rate), (int)((0.0005f + r.Next() * 0.002f) * Rate), 0.4f + 0.6f * r.Next() * (1f - i / 12f));
        float phase = 0f;
        for (int i = 0; i < x.Length; i++)
        {
            float t = i / (float)Rate;
            float w = r.Signed();
            // bright tear: high-passed noise, 1 ms attack, 22 ms decay
            lp += kHp * (w - lp);
            float bright = (w - lp) * MathF.Min(1f, t / 0.001f) * MathF.Exp(-t / 0.022f);
            // body: low-passed noise, 90 ms decay
            body += kBody * (w - body);
            float dark = body * 2.2f * MathF.Exp(-t / 0.09f);
            // thump: a sine falling from 70 to 38 Hz
            float f = 38f + 32f * MathF.Exp(-t / 0.08f);
            phase += 2f * MathF.PI * f / Rate;
            float thump = MathF.Sin(phase) * 0.7f * MathF.Exp(-t / 0.13f) * MathF.Min(1f, t / 0.003f);
            float crackle = 0f;
            foreach (var (at, len, amp) in rips)
                if (i >= at && i < at + len) crackle += (w - lp) * amp * 1.6f;
            x[i] = bright * 1.4f + dark + thump + crackle;
        }
        return Finish(x, -1f, soft: 1.6f);
    }

    /// <summary>A seamless arc loop, <paramref name="seconds"/> long.</summary>
    public static float[] Sizzle(uint seed, float seconds = 2.4f)
    {
        float fade = 0.15f;
        int n = (int)(seconds * Rate), m = (int)(fade * Rate);
        var x = new float[n + m];
        var r = new Rng(seed);
        float lpA = 0f, lpB = 0f, kA = LowK(1800f), kB = LowK(7000f);
        float pop = 0f, popDecay = 0f, wobble = 0f, wobbleTarget = 0f;
        float h = 0f;
        for (int i = 0; i < x.Length; i++)
        {
            float w = r.Signed();
            lpA += kA * (w - lpA); lpB += kB * (w - lpB);
            float band = lpB - lpA;   // about 1.8 to 7 kHz
            // crackle: pops at about 55 a second (big ones 4 a second)
            if (r.Next() < 55f / Rate) { pop = 0.45f + 0.55f * r.Next(); popDecay = MathF.Exp(-1f / ((0.003f + 0.007f * r.Next()) * Rate)); }
            if (r.Next() < 4f / Rate) { pop = 1.4f; popDecay = MathF.Exp(-1f / (0.016f * Rate)); }
            float crack = band * pop * 3.2f;
            pop *= popDecay;
            // hum: 100 Hz and its harmonics, wobbling 6 to 9 times a second
            if (r.Next() < 8f / Rate) wobbleTarget = 0.6f + 0.4f * r.Next();
            wobble += (wobbleTarget - wobble) * 0.002f;
            h += 2f * MathF.PI * 100f / Rate;
            float hum = (MathF.Sin(h) + 0.5f * MathF.Sin(2f * h) + 0.33f * MathF.Sin(3f * h) + 0.25f * MathF.Sin(4f * h)) * 0.11f * wobble;
            x[i] = band * 0.6f + crack + hum;
        }
        // The tail fades into the head: a seamless loop of n samples.
        var y = new float[n];
        Array.Copy(x, y, n);
        for (int i = 0; i < m; i++) { float k = i / (float)m; y[i] = x[i] * k + x[n + i] * (1f - k); }
        return Finish(y, -3f, soft: 1.3f);
    }

    /// <summary>A high-voltage snap (variation by <paramref name="seed"/>).</summary>
    public static float[] Snap(uint seed, float seconds = 0.28f)
    {
        var x = new float[(int)(seconds * Rate)];
        var r = new Rng(seed);
        float lp = 0f, k = LowK(1500f), phase = 0f;
        float top = 3200f + 1400f * r.Next(), bottom = 900f + 500f * r.Next();
        for (int i = 0; i < x.Length; i++)
        {
            float t = i / (float)Rate;
            float w = r.Signed();
            lp += k * (w - lp);
            float click = (w - lp) * MathF.Exp(-t / 0.005f) * 1.6f;
            float f = bottom + (top - bottom) * MathF.Exp(-t / 0.02f);
            phase += 2f * MathF.PI * f / Rate;
            float zap = MathF.Sin(phase) * MathF.Exp(-t / 0.035f) * 0.55f * (0.8f + 0.2f * MathF.Sin(t * 2f * MathF.PI * 60f));
            float fizz = (w - lp) * 0.25f * MathF.Exp(-t / 0.09f) * (r.Next() < 0.3f ? 1f : 0.2f);
            x[i] = click + zap + fizz;
        }
        return Finish(x, -1f, soft: 1.4f);
    }

    /// <summary>A thunder roll, <paramref name="seconds"/> long: a short crack, then low swells dying away.</summary>
    public static float[] Thunder(uint seed, float seconds = 3.4f, float crack = 0.35f)
    {
        var x = new float[(int)(seconds * Rate)];
        var r = new Rng(seed);
        float brown = 0f, lp1 = 0f, lp2 = 0f, hp = 0f, k1 = LowK(260f), k2 = LowK(180f), kHp = LowK(28f);
        Span<(float At, float Width, float Amp)> swells = stackalloc (float, float, float)[5];
        for (int i = 0; i < swells.Length; i++) swells[i] = (0.1f + r.Next() * seconds * 0.55f, 0.25f + 0.6f * r.Next(), 0.5f + 0.5f * r.Next());
        var head = Crack(seed ^ 0x5A5A5A5Au, 0.4f);
        for (int i = 0; i < x.Length; i++)
        {
            float t = i / (float)Rate;
            float w = r.Signed();
            brown = brown * 0.997f + w * 0.03f;
            lp1 += k1 * (brown - lp1); lp2 += k2 * (lp1 - lp2);
            hp += kHp * (lp2 - hp);
            float low = (lp2 - hp) * 9f;
            float env = MathF.Min(1f, t / 0.08f) * MathF.Exp(-t / (seconds * 0.33f));
            float rolls = 0.35f;
            foreach (var (at, width, amp) in swells) { float d = (t - at) / width; rolls += amp * MathF.Exp(-d * d); }
            x[i] = low * env * rolls + (i < head.Length ? head[i] * crack : 0f);
        }
        return Finish(x, -2f, soft: 1.5f);
    }

    /// <summary>The Shockwave's thunderclap: a crack, a deep falling thump and a heavy roll.</summary>
    public static float[] Boom(uint seed, float seconds = 2.8f)
    {
        var roll = Thunder(seed, seconds, crack: 0.8f);
        float phase = 0f;
        for (int i = 0; i < roll.Length; i++)
        {
            float t = i / (float)Rate;
            float f = 30f + 30f * MathF.Exp(-t / 0.15f);
            phase += 2f * MathF.PI * f / Rate;
            roll[i] = roll[i] * 0.8f + MathF.Sin(phase) * 0.9f * MathF.Exp(-t / 0.35f) * MathF.Min(1f, t / 0.004f);
        }
        return Finish(roll, -1f, soft: 1.8f);
    }

    /// <summary>A soft clip (tanh, by <paramref name="soft"/>) and a peak of <paramref name="peakDb"/> dBFS.</summary>
    static float[] Finish(float[] x, float peakDb, float soft)
    {
        float peak = 0f;
        for (int i = 0; i < x.Length; i++) { x[i] = MathF.Tanh(x[i] * soft); peak = MathF.Max(peak, MathF.Abs(x[i])); }
        float gain = peak > 1e-6f ? MathF.Pow(10f, peakDb / 20f) / peak : 1f;
        for (int i = 0; i < x.Length; i++) x[i] *= gain;
        return x;
    }

    /// <summary>The samples as a mono 16-bit PCM WAV file (what Godot's <c>AudioStreamWav.LoadFromBuffer</c> reads).</summary>
    public static byte[] Wav(float[] x)
    {
        using var ms = new MemoryStream(44 + x.Length * 2);
        using var w = new BinaryWriter(ms);
        int bytes = x.Length * 2;
        w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + bytes); w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write(Encoding.ASCII.GetBytes("data")); w.Write(bytes);
        foreach (var s in x) w.Write((short)Math.Clamp(MathF.Round(s * 32767f), -32768f, 32767f));
        w.Flush();
        return ms.ToArray();
    }
}
