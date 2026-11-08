using System.Globalization;

namespace SmContent;

/// <summary>
/// sound/louder-farther (owner, 2026-11-08: "the sound of all game sounds ... should be clearly hearable and relatively much louder from much further away"; "the game in general is too
/// quiet. make everything much louder"; both modes). An **ours** rule on top of the data's mix, client presentation only: every voice is <see cref="GainDb"/> louder, and every distance curve
/// of the banks (an attenuation's dry volume and low-pass, its end, and the <c>distance</c> RTPC) is read at the listener's distance divided by <see cref="RangeScale"/>, so a sound 30 m away
/// sounds as the data has it at 12 m. The relative levels between sounds and the curves' shapes stay the data's. Measured before the rule (smtool audio-mix): a remote bolter shot is -26 dB at the
/// voice at 5 m, -42 at 30 m and -61 at 60 m; the -60 dB culling threshold (<see cref="VoiceAudibility.VolumeThreshold"/>) is tested on the boosted level.
/// Client <c>--loudness-gain &lt;dB&gt;</c>, <c>--loudness-range &lt;x&gt;</c>, <c>--loudness-original</c> (0 dB, x1: the data's levels and ranges); <c>smtool audio-mix --loudness</c>.
/// </summary>
public readonly record struct SoundLoudness(float GainDb, float RangeScale)
{
    /// <summary>Added to every voice, dB (ours; about 2.8 times the amplitude). Refine in a playtest.</summary>
    public const float DefaultGainDb = 9f;

    /// <summary>The distance curves are read at the distance over this (ours: x2.5, a sound is heard as if 2.5 times nearer). Refine in a playtest.</summary>
    public const float DefaultRangeScale = 2.5f;

    public static SoundLoudness Default => new(DefaultGainDb, DefaultRangeScale);

    /// <summary>The data's levels and ranges (the behaviour before the rule).</summary>
    public static SoundLoudness Original => new(0f, 1f);

    /// <summary>The distance at which the data's curves are read for a listener <paramref name="distance"/> m away.</summary>
    public float CurveDistance(float distance) => RangeScale > 0f ? distance / RangeScale : distance;

    public override string ToString() => $"{GainDb:+0.#;-0.#;0} dB, range x{RangeScale:0.##}";

    /// <summary>
    /// The rule from command-line arguments: <c>--loudness-original</c> = <see cref="Original"/>; otherwise <see cref="Default"/> with <c>--loudness-gain &lt;dB&gt;</c> and
    /// <c>--loudness-range &lt;x&gt;</c> (positive) replacing its parts. A bad value is reported through <paramref name="error"/> and the default kept.
    /// </summary>
    public static SoundLoudness FromArgs(IReadOnlyList<string> args, Action<string>? error = null)
    {
        if (args.Contains("--loudness-original")) return Original;
        var rule = Default;
        for (int i = 0; i < args.Count; i++)
        {
            string? value = i + 1 < args.Count ? args[i + 1] : null;
            if (args[i] == "--loudness-gain")
            {
                if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float g) && float.IsFinite(g) && Math.Abs(g) <= 40f) rule = rule with { GainDb = g };
                else error?.Invoke($"--loudness-gain: '{value}' is not a level in dB (-40 to 40); using {rule.GainDb:0.#}");
            }
            else if (args[i] == "--loudness-range")
            {
                if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float r) && float.IsFinite(r) && r >= 0.1f && r <= 20f) rule = rule with { RangeScale = r };
                else error?.Invoke($"--loudness-range: '{value}' is not a factor (0.1 to 20); using {rule.RangeScale:0.##}");
            }
        }
        return rule;
    }

    /// <summary>
    /// A voice's mix at <paramref name="distance"/> m under this rule: <see cref="VoiceMixer.Evaluate"/> at <see cref="CurveDistance"/>, plus <see cref="GainDb"/>.
    /// </summary>
    public VoiceMix Evaluate(SoundVoice voice, SoundModifiers? modifiers, ulong emitter, double now, float? distance, IReadOnlyDictionary<uint, float>? parameters,
        RtpcVolumeCap cap = RtpcVolumeCap.Peak)
    {
        var mix = VoiceMixer.Evaluate(voice, modifiers, emitter, now, distance is float d ? CurveDistance(d) : null, parameters, cap);
        return mix with { VolumeDb = mix.VolumeDb + GainDb };
    }
}
