namespace SmContent;

/// <summary>
/// musou/audio-pass (owner, 2026-11-09, Musou Mode only; every number is **ours**, to refine by ear: spec "Musou audio pass", refine register). The rules of three sound changes the client applies
/// only when <see cref="SmSim.ContentExtras.Musou"/> is on, so Annihilation and Seize Ground play exactly as before (<see cref="Applies"/> is the one gate):
/// <list type="bullet">
/// <item>The far Ork voices of the battle ambience read as far away (<see cref="MusouAmbienceRules"/>: lower gain, high low-pass, farther ring).</item>
/// <item>The player's own character barks are never ducked, muffled, stolen or limited, and play at full volume (<c>SoundEmitter.ProtectSpeech</c>).</item>
/// <item>The bolter family's rifles fire deeper and punchier, and the first Ork a shot hits gets an explosion (the wet smack of the first pass was removed again, owner 2026-11-09).</item>
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

    /// <summary>
    /// The wet smack, back (owner 2026-11-09, fourth follow-up: "lower the volume by 40%, fall off more with distance (20% more), less punchy treble, more quiet deep lower sounds"): the data's
    /// <c>play_bolter_smack</c> at 60 % of its last level (+2 dB, i.e. 1.26 x the event: 0.6 x that = 0.755 x the event, -2.44 dB; the mix pass's +4 dB on-hit gain is added as before, but its
    /// 250 Hz high-pass is NOT: the owner wants the low body), pitched <see cref="SmackPitchCents"/> down and low-passed (<see cref="SmackLpf"/> LPF units, about 2.7 kHz) so the treble and the
    /// transient go and a quiet deep body stays, and read at <see cref="SmackFalloffScale"/> x the listener distance (the event's distance curves end at 1/1.2 of their range: 20 % more falloff
    /// at every distance). Ours.
    /// </summary>
    public const string SmackEvent = "play_bolter_smack";

    /// <summary>
    /// The smack's own event is the shooter's 2D hit confirm: the data gives it no distance curve (flat at every distance). Its falloff is the original curve of the bolter's real impact event
    /// (<c>play_bolter_hits</c>, 3D: about -23 dB near, -37 dB beyond 60 m, low-pass 1 at 30 m to 11 at 130 m), read at <see cref="SmackFalloffScale"/> x the distance (fifth follow-up 2026-11-09).
    /// </summary>
    public const string SmackCurveEvent = "play_bolter_hits";
    public static readonly float SmackGainDb = 2f + 20f * MathF.Log10(0.6f);   // about -2.44
    public const float SmackPitchCents = -600f, SmackLpf = 40f, SmackFalloffScale = 1.2f;
    /// <remarks>Owner 2026-11-09 follow-up: "lower the bolter damage on hit sound by 50%": half amplitude = -6 dB (smack +8 to +2, blast -9 to -15); the timing is unchanged.</remarks>
    public const float BlastDelaySeconds = 0.07f, BlastGainDb = -15f, BlastTailDb = -30f;

    // ---- bark themes ----

    /// <summary>The player's own barks play only after this many seconds without a melee attack (owner 2026-11-09). Ours, an exact figure from the owner.</summary>
    public const float OwnBarkMeleeQuietSeconds = 2f;

    /// <summary>
    /// The words that make a Marine bark line about Chaos, the warp or daemons (owner 2026-11-09: "none of the bark voice lines should be about chaos, the warp, or daemons. only use ork-related lines
    /// or generic lines"). Matched case-insensitively as word prefixes in a line's subtitle text; the owner named chaos, warp and daemons, the rest is ours: the same enemy by other names. "heretic"
    /// and "traitor" are included (they name Chaos's forces in the lines that use them; the owner's call whether they should stay).
    /// </summary>
    public static readonly string[] OffThemeWords =
    [
        "chaos", "warp", "daemon", "demon", "traitor", "heretic", "renegade", "nurgle", "khorne", "tzeentch", "slaanesh", "possessed", "cultist", "dark god", "ruinous", "corrupt",
    ];

    /// <summary>Whether a bark line's text is about Chaos, the warp or daemons (a word that starts with one of <see cref="OffThemeWords"/>).</summary>
    public static bool IsOffThemeLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (var w in OffThemeWords)
            for (int i = text.IndexOf(w, StringComparison.OrdinalIgnoreCase); i >= 0; i = text.IndexOf(w, i + 1, StringComparison.OrdinalIgnoreCase))
                if (i == 0 || !char.IsLetter(text[i - 1])) return true;
        return false;
    }

    /// <summary>
    /// Keeps the lines whose text is not off-theme. A line with no known text is kept (its text is unknown; no known link from a speech node to its subtitle exists in the data, so the client cannot
    /// call this on the real bank yet); when every line is off-theme the list is empty and the caller plays no bark (silence).
    /// </summary>
    public static List<T> OnTheme<T>(IEnumerable<T> lines, Func<T, string?> textOf) => lines.Where(l => !IsOffThemeLine(textOf(l))).ToList();

    // ---- the far Orks (MusouAmbienceRules.Specs holds the numbers) ----

    /// <summary>Whether an ambience kind is a far Ork voice: it takes the distance treatment.</summary>
    public static bool IsFarOrk(MusouAmbienceKind kind) => kind == MusouAmbienceKind.OrkMob;
}
