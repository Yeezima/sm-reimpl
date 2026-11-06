namespace SmContent;

/// <summary>
/// The client-wide sound mix pass (sound/mix-and-reloads, owner's playtest notes 2026-11-06; all values ours, to refine by listening, spec "Sound mix pass"):
/// gun shots louder, every on-hit sound louder and through a high-pass filter (no bass), the master volume 10 % up, and the jump pack's launch kept
/// for its first burst with what follows (the long thrust layers, their decay) faded far down. <c>--sound-mix-classic</c> restores the mix before the pass.
/// </summary>
public static class SoundMixPass
{
    /// <summary>Added to every gun's fire sound (own and others'), dB. Ours (owner: "gun shoot sounds should be louder").</summary>
    public const float FireGainDb = 4f;

    /// <summary>Added to every on-hit sound (shot impacts on the level and on players, melee hits, the shooter's hit confirm), dB. Ours (owner: "all need to be louder").</summary>
    public const float HitGainDb = 4f;

    /// <summary>The on-hit sounds' high-pass cutoff, Hz, at 24 dB per octave (owner: "all on hit sound effects should have zero bass"). Ours.</summary>
    public const float HitHighPassHz = 250f;

    /// <summary>The master volume's factor (owner: "increase the global master volume by 10%"): amplitude ×1.1.</summary>
    public const float MasterFactor = 1.1f;

    /// <summary><see cref="MasterFactor"/> in dB (about +0.83).</summary>
    public static readonly float MasterBoostDb = 20f * MathF.Log10(MasterFactor);

    /// <summary>Ceiling of the limiter on the master bus, dB (ours): the boost cannot push the output past full scale.</summary>
    public const float MasterCeilingDb = -0.3f;

    /// <summary>
    /// The jump pack launch's tail (owner: "the initial sound is fine, but any echoes/reverb or extra something after that should be deeply diminished"): each voice of the
    /// launch and apex posts plays at full level for <see cref="JumpTailHoldSeconds"/>, then fades over <see cref="JumpTailFadeSeconds"/> to <see cref="JumpTailDb"/>.
    /// The launch's burst samples hold full level for about 0.6 s; its two thrust layers sound for 1.7 to 2.4 s. Ours.
    /// </summary>
    public const float JumpTailHoldSeconds = 0.5f, JumpTailFadeSeconds = 0.3f, JumpTailDb = -15f;

    /// <summary>The fire sound's offset.</summary>
    public static VoiceOffset Fire(bool classic) => classic ? VoiceOffset.None : new VoiceOffset(FireGainDb);

    /// <summary>The offset added to an on-hit post: louder and high-passed.</summary>
    public static VoiceOffset Hit(bool classic) => classic ? VoiceOffset.None : new VoiceOffset(HitGainDb, HighPass: true);

    /// <summary>The offset of a jump pack launch or apex post: the tail fade.</summary>
    public static VoiceOffset JumpTail(bool classic) => classic ? VoiceOffset.None : new VoiceOffset(TailDb: JumpTailDb);

    /// <summary>The master bus volume for the options' master level (linear 0..1).</summary>
    public static float MasterBusDb(float linear, bool classic)
    {
        float db = linear <= 0.0005f ? -80f : Math.Max(-80f, 20f * MathF.Log10(linear));
        return classic || db <= -80f ? db : db + MasterBoostDb;
    }

    /// <summary>The gain of a voice with a tail fade of <paramref name="tailDb"/> (0 = none) at <paramref name="age"/> seconds after it started, dB (0 during the hold, linear in dB over the fade).</summary>
    public static float TailGainDb(float tailDb, double age)
    {
        if (tailDb >= 0f || age <= JumpTailHoldSeconds) return 0f;
        double f = Math.Clamp((age - JumpTailHoldSeconds) / JumpTailFadeSeconds, 0.0, 1.0);
        return (float)(tailDb * f);
    }
}
