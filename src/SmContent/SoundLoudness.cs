using System.Globalization;

namespace SmContent;

/// <summary>
/// sound/louder-farther (owner, 2026-11-08: "the sound of all game sounds ... should be clearly hearable and relatively much louder from much further away"; "the game in general is too
/// quiet. make everything much louder"; both modes). An **ours** rule on top of the data's mix, client presentation only: every voice is <see cref="GainDb"/> louder, and every distance curve
/// of the banks (an attenuation's dry volume and low-pass, its end, and the <c>distance</c> RTPC) is read at the listener's distance divided by <see cref="RangeScale"/>, so a sound 30 m away
/// sounds as the data has it at 12 m. The relative levels between sounds and the curves' shapes stay the data's. Measured before the rule (smtool audio-mix): a remote bolter shot is -26 dB at the
/// voice at 5 m, -42 at 30 m and -61 at 60 m; the -60 dB culling threshold (<see cref="VoiceAudibility.VolumeThreshold"/>) is tested on the boosted level.
/// Then (owner, 2026-11-08, second note: "sounds should scale down to 20% of their normal sound when turned down by distance"): the dry volume curve never takes a voice more than
/// <see cref="FloorFraction"/> of its amplitude below its level at 0 m (<see cref="FloorDb"/>, about -14 dB), and a positioned voice past its curve's end is no longer cut: it plays at the floor.
/// Client <c>--loudness-gain &lt;dB&gt;</c>, <c>--loudness-range &lt;x&gt;</c>, <c>--loudness-floor &lt;fraction&gt;</c> (0 = no floor), <c>--loudness-original</c> (0 dB, x1, no floor: the data's
/// levels and ranges); <c>smtool audio-mix --loudness</c>.
/// </summary>
public readonly record struct SoundLoudness(float GainDb, float RangeScale, float FloorFraction = 0f, float MeleeHitGainDb = 0f)
{
    /// <summary>Added to every voice, dB (ours; about 2.8 times the amplitude). Refine in a playtest.</summary>
    public const float DefaultGainDb = 9f;

    /// <summary>The distance curves are read at the distance over this (ours: x2.5, a sound is heard as if 2.5 times nearer). Refine in a playtest.</summary>
    public const float DefaultRangeScale = 2.5f;

    /// <summary>The share of a voice's amplitude at 0 m that distance can take it down to (the owner's 20 %, read as amplitude: -14 dB). Refine in a playtest.</summary>
    public const float DefaultFloorFraction = 0.2f;

    /// <summary>
    /// Added to every melee weapon's on-hit sound on top of <see cref="GainDb"/> (owner, 2026-11-08: "raise melee weapon on hit sounds by 2X"): 2x the amplitude, +6.02 dB. All modes; swings' data
    /// hit events on players and on Musou enemies, the Musou enemies' melee hits, the generated hit sound; not the charge's body smash. <c>--melee-hit-gain &lt;dB&gt;</c>.
    /// </summary>
    public static readonly float DefaultMeleeHitGainDb = 20f * MathF.Log10(2f);

    public static SoundLoudness Default => new(DefaultGainDb, DefaultRangeScale, DefaultFloorFraction, DefaultMeleeHitGainDb);

    /// <summary>The data's levels and ranges (the behaviour before the rule).</summary>
    public static SoundLoudness Original => new(0f, 1f, 0f, 0f);

    /// <summary>The post offset of a melee weapon's on-hit sound (<see cref="MeleeHitGainDb"/>).</summary>
    public VoiceOffset MeleeHit => new(MeleeHitGainDb);

    /// <summary>
    /// For a sound played outside the banks' curves (a Godot player): inverse distance, full within <paramref name="unitSize"/> times <see cref="RangeScale"/>, never under the floor; dB.
    /// </summary>
    public float InverseDistanceDb(float distance, float unitSize)
    {
        float unit = unitSize * RangeScale;
        float db = distance <= unit || unit <= 0f ? 0f : 20f * MathF.Log10(unit / distance);
        return Floored ? MathF.Max(db, FloorDb) : db;
    }

    /// <summary>The floor in dB below the curve's value at 0 m (negative infinity: none).</summary>
    public float FloorDb => FloorFraction > 0f ? 20f * MathF.Log10(FloorFraction) : float.NegativeInfinity;

    /// <summary>Whether distance has a floor (then nothing positioned is cut at its curve's end).</summary>
    public bool Floored => FloorFraction > 0f;

    /// <summary>The distance at which the data's curves are read for a listener <paramref name="distance"/> m away.</summary>
    public float CurveDistance(float distance) => RangeScale > 0f ? distance / RangeScale : distance;

    /// <summary>The curve end to test audibility against: the data's, or 0 (none) with a floor.</summary>
    public float MaxDistanceOf(SmFormats.Audio.WwiseAttenuation? attenuation) => Floored ? 0f : attenuation?.MaxDistance ?? 0f;

    public override string ToString() => $"{GainDb:+0.#;-0.#;0} dB, range x{RangeScale:0.##}, floor {(Floored ? $"{FloorFraction * 100f:0.#} % ({FloorDb:0.#} dB)" : "none")}, melee hits {MeleeHitGainDb:+0.#;-0.#;0} dB";

    /// <summary>
    /// The rule from command-line arguments: <c>--loudness-original</c> = <see cref="Original"/>; otherwise <see cref="Default"/> with <c>--loudness-gain &lt;dB&gt;</c>,
    /// <c>--loudness-range &lt;x&gt;</c> (positive) and <c>--loudness-floor &lt;fraction&gt;</c> (0 to 1) replacing its parts. A bad value is reported through <paramref name="error"/> and the default kept.
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
            else if (args[i] == "--melee-hit-gain")
            {
                if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float m) && float.IsFinite(m) && Math.Abs(m) <= 40f) rule = rule with { MeleeHitGainDb = m };
                else error?.Invoke($"--melee-hit-gain: '{value}' is not a level in dB (-40 to 40); using {rule.MeleeHitGainDb:0.#}");
            }
            else if (args[i] == "--loudness-floor")
            {
                if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) && float.IsFinite(f) && f >= 0f && f <= 1f) rule = rule with { FloorFraction = f };
                else error?.Invoke($"--loudness-floor: '{value}' is not a fraction (0 to 1); using {rule.FloorFraction:0.##}");
            }
        }
        return rule;
    }

    /// <summary>
    /// A voice's mix at <paramref name="distance"/> m under this rule: <see cref="VoiceMixer.Evaluate"/> at <see cref="CurveDistance"/>, the dry volume curve held at or above
    /// its value at 0 m plus <see cref="FloorDb"/>, plus <see cref="GainDb"/>.
    /// </summary>
    public VoiceMix Evaluate(SoundVoice voice, SoundModifiers? modifiers, ulong emitter, double now, float? distance, IReadOnlyDictionary<uint, float>? parameters,
        RtpcVolumeCap cap = RtpcVolumeCap.Peak)
    {
        float? curveDistance = distance is float d ? CurveDistance(d) : null;
        var mix = VoiceMixer.Evaluate(voice, modifiers, emitter, now, curveDistance, parameters, cap);
        float lift = 0f;
        if (Floored && curveDistance is float cd && voice.Attenuation is { } att)
        {
            float at = att.VolumeAt(cd), floor = att.VolumeAt(0f) + FloorDb;
            if (at < floor) lift = floor - at;
        }
        return mix with { VolumeDb = mix.VolumeDb + lift + GainDb };
    }
}
