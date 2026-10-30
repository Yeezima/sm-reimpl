namespace SmContent;

/// <summary>
/// The armour regeneration sound's level (owner decision 2026-10-31: "the armor regen sound should falloff past a certain distance. call it 20 feet roughly.
/// also make it about 50% of the current volume"). The own player's regen is half the amplitude (-6 dB); another player's regen, should one be posted (the data
/// makes it the local player's only, so none is today), fades out smoothly to silence at <see cref="RangeMeters"/> (20 ft). Client switch <c>--regen-sound-old</c>
/// restores the earlier level (no change, no falloff).
/// </summary>
public static class RegenSoundLevel
{
    /// <summary>Half the amplitude, in dB (owner: "about 50% of the current volume").</summary>
    public const float OwnGainDb = -6.0206f;

    /// <summary>20 feet in metres (owner: "roughly"): the distance at which another player's regen sound is inaudible.</summary>
    public const float RangeMeters = 6.096f;

    /// <summary>The gain (dB) to add to the regen voice: <paramref name="own"/> the fixed half volume; otherwise half volume times a smoothstep fade to zero at the range.</summary>
    public static float GainDb(bool own, float distanceMeters)
    {
        if (own) return OwnGainDb;
        float x = Math.Clamp(distanceMeters / RangeMeters, 0f, 1f);
        float fade = 1f - x * x * (3f - 2f * x);
        return fade <= 0.0001f ? -80f : OwnGainDb + 20f * MathF.Log10(fade);
    }
}
