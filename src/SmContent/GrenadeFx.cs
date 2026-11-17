namespace SmContent;

/// <summary>
/// The look of a frag grenade's blast that is not in the effect files (grenade-effects, owner 2026-11-17: "same visual appearance ... explosion sound and effects"; protocol 83). Engine-free; tested
/// (<c>GrenadeFxTests</c>).
/// <para>
/// Camera effect: the frag blueprints' (<c>pp_grenade_multiplayer</c>, <c>_upgraded</c>, <c>_chaos</c>, <c>_chaos_upgraded</c>; the same four blocks) <c>camera_effect</c> command, **original data**:
/// duration 0.07 s, ease out 0.5, blur power 0.5, attenuation from 5 m to 10 m, a shake of 0.05 horizontal and 0.1 vertical at 12 Hz (pitch, yaw and roll 0). The owner asked for the game's
/// shake in place of ours (card G4, "Game's"). What the file does not say (**ours / estimate**): the shake amplitudes are metres of camera offset, ease out is a fraction of the duration, the
/// attenuation is linear, and the blur is not drawn.
/// </para>
/// <para>
/// Smoke: the owner's no-smoke rule (2026-10-29) stands for every effect except the frag blasts, which draw the data's smoke and dust again (card G1, "Smoke on frags only"; the reference clips show it).
/// </para>
/// </summary>
public static class GrenadeFx
{
    /// <summary>Seconds the camera effect lasts (original data: <c>duration</c>).</summary>
    public const float Duration = 0.07f;
    /// <summary>Share of the duration over which the shake fades out at its end (original data: <c>ease_out</c> 0.5; read as a fraction: ours / estimate).</summary>
    public const float EaseOut = 0.5f;
    /// <summary>Shake amplitudes (original data: horizontal 0.05, vertical 0.1; read as metres of camera offset: ours / estimate).</summary>
    public const float Horizontal = 0.05f, Vertical = 0.1f;
    /// <summary>Shake frequency in Hz (original data).</summary>
    public const float Frequency = 12f;
    /// <summary>Full strength up to this distance in metres, none from <see cref="AttenuationEnd"/> (original data: attenuation_start_distance 5, attenuation_end_distance 10; linear between: ours / estimate).</summary>
    public const float AttenuationStart = 5f, AttenuationEnd = 10f;

    /// <summary>The share of the shake at <paramref name="distance"/> metres from the blast: 1 within <see cref="AttenuationStart"/>, 0 from <see cref="AttenuationEnd"/>, linear between.</summary>
    public static float Strength(float distance) =>
        !(distance > AttenuationStart) ? 1f : distance >= AttenuationEnd ? 0f : (AttenuationEnd - distance) / (AttenuationEnd - AttenuationStart);

    /// <summary>The camera offset (right, up) in metres <paramref name="age"/> seconds after the blast at the given <paramref name="strength"/>; zero once <see cref="Duration"/> has passed.</summary>
    public static (float Right, float Up) Offset(float age, float strength)
    {
        if (age < 0f || age >= Duration || strength <= 0f) return (0f, 0f);
        float fadeFrom = Duration * (1f - EaseOut);
        float envelope = age <= fadeFrom ? 1f : (Duration - age) / (Duration - fadeFrom);
        float wave = MathF.Sin(2f * MathF.PI * Frequency * age) * envelope * strength;
        return (Horizontal * wave, Vertical * wave);
    }

    /// <summary>The share of the frag blast's smoke and dust sprites that is drawn (ours / estimate: the data's full cloud is far thicker than the reference clips' thin grey column; a first guess to check against them).</summary>
    public const float SmokeShare = 0.3f;

    /// <summary>The blast effects that draw their smoke and dust although the no-smoke rule is on: the frag grenades' (loyalist and Chaos); not the krak grenade's or anyone else's.</summary>
    public static bool KeepsSmoke(string effect) =>
        effect.Equals("frag_grenade_explosion", StringComparison.OrdinalIgnoreCase) || effect.Equals("chaos_frag_grenade_explosion", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a thrown item's wargear name is a frag grenade (<c>sm_mp_eq_frag_grenade</c>, <c>csm_mp_eq_frag_grenade</c>).</summary>
    public static bool IsFrag(string wargear) => wargear.Contains("frag_grenade", StringComparison.OrdinalIgnoreCase);
}
