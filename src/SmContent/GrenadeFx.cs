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

    /// <summary>The camera effect's blur power (original data: <c>blur_power</c> 0.5; its unit is not stated).</summary>
    public const float BlurPower = 0.5f;
    /// <summary>The widest blur radius in pixels at a 720-pixel-high screen for a blur power of 1 (ours / estimate: the data gives no unit; 0.5 draws a 6 px blur, scaled with the screen height).</summary>
    public const float BlurPixels720 = 12f;

    /// <summary>The share of the effect still running <paramref name="age"/> seconds after the blast: 1 until the ease out begins, then down to 0 at <see cref="Duration"/>; 0 outside it.</summary>
    public static float Envelope(float age)
    {
        if (age < 0f || age >= Duration) return 0f;
        float fadeFrom = Duration * (1f - EaseOut);
        return age <= fadeFrom ? 1f : (Duration - age) / (Duration - fadeFrom);
    }

    /// <summary>The blur radius in pixels on a screen <paramref name="screenHeight"/> pixels high, <paramref name="age"/> seconds after a blast at <paramref name="strength"/> (see <see cref="Strength"/>).</summary>
    public static float BlurRadius(float age, float strength, float screenHeight) =>
        BlurPower * BlurPixels720 * (screenHeight / 720f) * strength * Envelope(age);

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

/// <summary>
/// The orange in-flight grenade marker (grenade-effects, owner 2026-11-17: "Yes, match the video"; spec "Grenade in-flight marker"). **Original art**: the canister (Decorator.gfx sprite 29, a 30-frame
/// glow pulse) and the chevron (sprite 23, 15 frames); everything else is measured from the four reference clips (**ours / estimate**): it shows for the thrower's own frag grenade (and, since the owner's "also enemy grenades near me", an enemy's within its blast radius + 2 m of the player; a teammate's shows none) from the
/// release until it first lands, on a ring round the screen centre at the grenade's direction as seen from the camera (the release at the right hand: 0 degrees; the throw ahead: 90 degrees), with
/// a thin arc outside the canister and the chevron beyond. Sizes are stage pixels of the 1280 x 720 stage. Whether other players' grenades show it is not in the clips (only own throws were filmed).
/// </summary>
public static class GrenadeRules
{
    /// <summary>The ring the canister sits on, in stage pixels from the centre (the clips: 0.19 of the screen height).</summary>
    public const float RingRadius = 138f;
    /// <summary>The canister's scale over its 16 x 32 art (the clips: about 5 % of the screen height tall).</summary>
    public const float IconScale = 1.9f;
    /// <summary>The arc's distance outside the canister, its half angle in radians (about 25 degrees) and its width in stage pixels.</summary>
    public const float ArcOffset = 26f, ArcHalfAngle = 0.43f, ArcWidth = 3.5f;
    /// <summary>The chevron's distance outside the canister and its scale.</summary>
    public const float CaretOffset = 60f, CaretScale = 1.0f;
    /// <summary>Seconds the marker keeps fading after the grenade lands (the clips: gone within a frame or two).</summary>
    public const float LandedFade = 0.1f;
    /// <summary>An enemy's grenade shows the marker while it is within its blast radius plus this many metres of the player (owner 2026-11-17: "enemy grenades near me"; the margin ours / estimate).</summary>
    public const float NearMargin = 2f;
    /// <summary>The thrower of a grenade is inferred as the player nearest to it within this many metres when it first shows, who was in a throw within <see cref="ThrowerWindow"/> seconds (ours / estimate: the snapshot names no owner).</summary>
    public const float ThrowerReach = 4f, ThrowerWindow = 1.2f;

    /// <summary>Whether a grenade <paramref name="distance"/> metres from the player, with a blast of <paramref name="radius"/> metres, is near enough to show its marker.</summary>
    public static bool Near(float distance, float radius) => radius > 0f && distance <= radius + NearMargin;

    /// <summary>A marker that has not landed after this many seconds is dropped (a grenade lost in flight).</summary>
    public const float MaxAge = 1.5f;

    /// <summary>The direction on the screen (radians, 0 right, counter-clockwise up) of a point <paramref name="right"/> metres to the camera's right and <paramref name="forward"/> metres ahead of the player.</summary>
    public static float Angle(float right, float forward) => MathF.Atan2(forward, right);

    /// <summary>The share of the arc still drawn <paramref name="age"/> seconds after the release: all of it, then fading out first (the clips show the arc gone while the canister is still there).</summary>
    public static float ArcFadeShare(float age) => Math.Clamp(1f - (age - 0.18f) / 0.15f, 0f, 1f);

    /// <summary>The marker's fade: 1 in flight, down to 0 over <see cref="LandedFade"/> after the landing; 0 past <see cref="MaxAge"/>.</summary>
    public static float Fade(float age, float? sinceLanded) =>
        age > MaxAge ? 0f : sinceLanded is { } l ? Math.Clamp(1f - l / LandedFade, 0f, 1f) : 1f;
}
