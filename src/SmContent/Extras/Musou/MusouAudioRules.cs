namespace SmContent;

/// <summary>
/// musou/audio-pass (owner, 2026-11-09, Musou Mode only; every number is **ours**, to refine by ear: spec "Musou audio pass", refine register). The rules of four sound changes the client applies
/// only when <see cref="SmSim.ContentExtras.Musou"/> is on, so Annihilation and Seize Ground play exactly as before (<see cref="Applies"/> is the one gate):
/// <list type="bullet">
/// <item>The far Ork voices of the battle ambience read as far away (<see cref="MusouAmbienceRules"/>: lower gain, high low-pass, farther ring).</item>
/// <item>The player's own character barks are never ducked, muffled, stolen or limited, and play at full volume (<c>SoundEmitter.ProtectSpeech</c>).</item>
/// <item>The bolter family's rifles fire deeper and punchier, and the first Ork a shot hits gets an explosion (the wet smack of the first pass was removed again, owner 2026-11-09).</item>
/// <item>A melee hit's sound plays through to its end and rings out longer (<c>SoundEmitter.Tail</c>).</item>
/// </list>
/// </summary>
public static class MusouAudioRules
{
    /// <summary>The single gate: the added content is Musou Mode. Everything of this pass asks it; with it false no code path of the pass runs.</summary>
    public static bool Applies(SmSim.ContentExtras extras) => extras.HasFlag(SmSim.ContentExtras.Musou);

    // ---- the bolter family ----

    /// <summary>
    /// Whether a wargear is a bolter rifle: the bolter, storm bolter, stalker bolter (the sniper rifle's wargear name) and heavy bolter, either side's (<c>sm_</c> / <c>csm_</c>, perk variants too).
    /// The bolt pistols are no rifles (owner: "bolter rifle weapons").
    /// </summary>
    public static bool IsBolterRifle(string? wargear)
    {
        if (string.IsNullOrEmpty(wargear) || wargear.Contains("pistol", StringComparison.OrdinalIgnoreCase)) return false;
        return wargear.Contains("ranged_bolter", StringComparison.OrdinalIgnoreCase) || wargear.Contains("storm_bolter", StringComparison.OrdinalIgnoreCase)
            || wargear.Contains("heavy_bolter", StringComparison.OrdinalIgnoreCase) || wargear.Contains("ranged_sniper_rifle", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The fire sound's deep copy: the weapon's own fire event again, a low copy under the shot (pitch cents, level dB, extra low-pass units so only the body of it stays). Ours.</summary>
    public const float DeepPitchCents = -800f, DeepLpf = 22f;

    /// <summary>
    /// Others' bolters (owner 2026-11-09 follow-up: "the bolter shoot sound that plays from the gun ... deeper, more powerful"): the deep copy stays lighter than the player's own, to save voices (dB). Ours.
    /// </summary>
    public const float DeepGainDb = -3f, ThumpGainDb = 3f;

    /// <summary>The thump under every shot: a one-voice blast pitched an octave down (event, cents). Ours.</summary>
    public const string ThumpEvent = "play_reactive_blast";
    public const float ThumpPitchCents = -1200f;

    /// <summary>
    /// The player's own gun (follow-up 2026-11-09): heavier. The deep copy louder (+2 dB), a second sub copy another 700 cents down under it (-1500 cents, 0 dB, low-pass +30: only the boom
    /// stays), and a louder thump (+9 dB). The layers sit on an emitter of their own (<c>SoundEmitter.ProtectGun</c>): never stolen at the voice cap, never cut by a Stop action of another
    /// event, never ducked by an explosion's Set/Reset. Ours.
    /// </summary>
    public const float OwnDeepGainDb = 2f, OwnSubPitchCents = -1500f, OwnSubGainDb = 0f, OwnSubLpf = 30f, OwnThumpGainDb = 9f;

    /// <summary>
    /// The first Ork a shot hits: <see cref="BlastDelaySeconds"/> after the original hit sound, the explosion
    /// (<see cref="BlastEvent"/> at <see cref="BlastGainDb"/> dB, its tail faded to <see cref="BlastTailDb"/> dB after the hold of <c>SoundMixPass.JumpTailHoldSeconds</c>, so it is a crack, not a grenade). Ours.
    /// </summary>
    public const string BlastEvent = "play_sm_grenade_explode_mp";
    /// <remarks>Owner 2026-11-09 follow-up: "lower the bolter damage on hit sound by 50%": half amplitude = -6 dB (smack +8 to +2, blast -9 to -15); the timing is unchanged.</remarks>
    public const float BlastDelaySeconds = 0.07f, BlastGainDb = -15f, BlastTailDb = -30f;

    // ---- the melee hit's tail ----

    /// <summary>The ring-out of a melee hit: a reverb bus (room size, damping, wet level) the hit voices pass through, after their high-pass. Ours (the data's hits last 0.2 to 0.7 s).</summary>
    public const float TailRoomSize = 0.82f, TailDamping = 0.4f, TailWet = 0.5f, TailPredelayMs = 18f;

    /// <summary>
    /// The power sword's hit (owner follow-up 2026-11-09: "revert ... exactly like the original game's power sword on hit sound effect, just louder"): no tail, no added length, the data's events and
    /// playback; only kept from being stolen at the voice cap, and +6 dB (ours, to refine by ear) on top of the mix pass's on-hit gain.
    /// </summary>
    public const float PowerSwordHitGainDb = 6f;

    public static bool IsPowerSword(string? wargear) => !string.IsNullOrEmpty(wargear) && wargear.Contains("powersword", StringComparison.OrdinalIgnoreCase);

    // ---- the far Orks (MusouAmbienceRules.Specs holds the numbers) ----

    /// <summary>Whether an ambience kind is a far Ork voice: it takes the distance treatment.</summary>
    public static bool IsFarOrk(MusouAmbienceKind kind) => kind == MusouAmbienceKind.OrkMob;
}
