using System;
using Godot;

namespace SmClient.Extras.Weather;

/// <summary>
/// The weather's procedural sounds (weather W2; client only, **ours**; no sample and no original file: noise filtered in code): a steady wind bed with its loop seam cross-faded, and thunder rumbles (far and near).
/// Rain's hiss stays in <see cref="WeatherRig"/>. Levels are set by the rig (spec/behavior.md "Weather (W2)": wind at most <see cref="WeatherRig.WindLoudestDb"/>, thunder at most <see cref="WeatherRig.NearThunderDb"/>).
/// </summary>
public static class WeatherSynth
{
    const int Rate = 22050;

    /// <summary>A 4 s looping wind: low-passed noise (about 150 to 700 Hz) with a slow swell, the seam cross-faded over 0.25 s so the loop does not click.</summary>
    public static AudioStreamWav Wind()
    {
        const int n = Rate * 4, fade = Rate / 4;
        var rng = new Random(0x71D0);
        var buf = new float[n + fade];
        float a = 0f, b = 0f, hp = 0f, prev = 0f;
        for (int i = 0; i < buf.Length; i++)
        {
            float white = (float)(rng.NextDouble() * 2.0 - 1.0);
            a += 0.07f * (white - a);      // two poles: no hiss, a roar
            b += 0.12f * (a - b);
            hp = 0.995f * (hp + b - prev); // no sub-bass thump
            prev = b;
            double t = (double)i / Rate;
            float swell = 0.75f + 0.25f * (float)Math.Sin(t * Math.PI * 2.0 / 4.0 * 1.0);   // exactly one swell per loop
            buf[i] = hp * swell;
        }
        var data = new float[n];
        for (int i = 0; i < n; i++)
        {
            float v = buf[i + 0];
            if (i < fade) { float w = (float)i / fade; v = buf[i] * w + buf[n + i] * (1f - w); }
            data[i] = v;
        }
        return Wav(data, 26000f, loop: true);
    }

    /// <summary>A rumble of thunder. Far: five and a half seconds, a slow swell and a long roll, only low. Near: a short broadband crack (0.12 s) then a shorter, louder roll (3.5 s).</summary>
    public static AudioStreamWav Thunder(uint seed, bool near)
    {
        double length = near ? 3.5 : 5.5;
        int n = (int)(Rate * length);
        var rng = new Random((int)(seed & 0x7FFFFFFF) | 1);
        var data = new float[n];
        float a = 0f, b = 0f, c = 0f;
        double roll = rng.NextDouble() * 6.28;
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / Rate;
            float white = (float)(rng.NextDouble() * 2.0 - 1.0);
            a += (near ? 0.10f : 0.045f) * (white - a);
            b += (near ? 0.12f : 0.06f) * (a - b);
            c += 0.4f * (white - c);
            double attack = Math.Min(1.0, t / (near ? 0.02 : 0.6));
            double decay = Math.Exp(-t * (near ? 1.1 : 0.55));
            double modulation = 0.7 + 0.3 * Math.Sin(t * (near ? 9.0 : 4.5) + roll);   // the roll
            float env = (float)(attack * decay * modulation);
            float crack = near && t < 0.12 ? c * (float)Math.Exp(-t * 40.0) * 0.8f : 0f;
            data[i] = b * 4.0f * env + crack;
        }
        // a short fade at the very end so nothing clicks
        int tail = Rate / 20;
        for (int i = 0; i < tail && i < n; i++) data[n - 1 - i] *= (float)i / tail;
        return Wav(data, near ? 24000f : 30000f, loop: false);
    }

    static AudioStreamWav Wav(float[] samples, float gain, bool loop)
    {
        var bytes = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            short s = (short)Math.Clamp((int)(samples[i] * gain), short.MinValue, short.MaxValue);
            bytes[i * 2] = (byte)(s & 0xFF);
            bytes[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
        }
        return new AudioStreamWav
        {
            Format = AudioStreamWav.FormatEnum.Format16Bits, MixRate = Rate, Stereo = false, Data = bytes,
            LoopMode = loop ? AudioStreamWav.LoopModeEnum.Forward : AudioStreamWav.LoopModeEnum.Disabled, LoopBegin = 0, LoopEnd = loop ? samples.Length : 0,
        };
    }
}
