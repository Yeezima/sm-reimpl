namespace SmContent;

/// <summary>
/// musou/sound-ab (owner, 2026-11-10: "remove the 'drowned out sound overload' effect that happens in heavy combat"; he hears muffled / underwater, pumping / squashed, sounds cutting out
/// and a constant roar bed, even in small fights; he picks after an A/B, so **no default changes**). The systems of the client's sound that could cause it, each of which the test aid
/// <c>--musou-sound-off a,b,...</c> turns off in Musou Mode only (Annihilation and Seize Ground never read it):
/// <list type="bullet">
/// <item><see cref="Duck"/>: the data's Set/Reset volume, low-pass, pitch and mute actions aimed at a bus (every sound routed through it), e.g. <c>play_big_damage_med_explosion</c>'s low-pass and
/// volume cut on three buses for 1.5 s (the original's "deafening").</item>
/// <item><see cref="LowPass"/>: every low-pass the client renders (the filter buses: the data's node and distance low-pass, the Set/Reset ones, ours); voices play unfiltered.</item>
/// <item><see cref="HitFilter"/>: the mix pass's on-hit 250 Hz high-pass (hits play on the plain buses; their +4 dB stays).</item>
/// <item><see cref="Limiter"/>: the hard limiters on the sound bus (<c>SmSfx</c>, -0.5 dB) and on Master (-0.3 dB, the 10 % master boost's).</item>
/// <item><see cref="Steal"/>: the voice cap (48 positioned + 16 flat voices) and its stealing: the cap is raised to <see cref="LiftedCap3D"/> + <see cref="LiftedCap2D"/>.</item>
/// <item><see cref="InstLimit"/>: the data's per-sound instance limits (the crowd's posts all ask for them).</item>
/// <item><see cref="Ambience"/>: the far battle round the battlefield (musou/battle-ambience) and the older war bed beyond the walls.</item>
/// <item><see cref="CrowdVoices"/>: the near Orks' voices (grunts, barks, roars, death yells); their steps, swings and weapons stay.</item>
/// <item><see cref="Blast"/>: musou/audio-pass's explosion on the first Ork each bolter shot hits (<c>play_sm_grenade_explode_mp</c>).</item>
/// <item><see cref="BoltBlast"/>: the frag detonation sound each bolt blast plays (a bolter round blows up at every enemy it goes through, up to 2 sounds a frame; the effects stay).</item>
/// <item><see cref="AllyLayers"/>: musou/audio-pass's deep copy and thump under every other Marine's bolter shot (the allied Marines' and other players'; the own gun's layers stay).</item>
/// </list>
/// </summary>
[Flags]
public enum MusouSoundOff
{
    None = 0,
    Duck = 1 << 0,
    LowPass = 1 << 1,
    HitFilter = 1 << 2,
    Limiter = 1 << 3,
    Steal = 1 << 4,
    InstLimit = 1 << 5,
    Ambience = 1 << 6,
    CrowdVoices = 1 << 7,
    Blast = 1 << 8,
    AllyLayers = 1 << 9,
    BoltBlast = 1 << 10,
    All = Duck | LowPass | HitFilter | Limiter | Steal | InstLimit | Ambience | CrowdVoices | Blast | AllyLayers | BoltBlast,
}

/// <summary>The command-line names of <see cref="MusouSoundOff"/> and the numbers of the test aid (all ours, test aids only).</summary>
public static class MusouSoundAb
{
    /// <summary>The voice cap with <see cref="MusouSoundOff.Steal"/> (positioned, flat): high enough that a Musou fight never reaches it (the measured peak is the evidence).</summary>
    public const int LiftedCap3D = 160, LiftedCap2D = 48;

    /// <summary>The names, in the order the listening sheet uses.</summary>
    public static readonly IReadOnlyList<(string Name, MusouSoundOff Flag)> Names =
    [
        ("duck", MusouSoundOff.Duck), ("lowpass", MusouSoundOff.LowPass), ("hitfilter", MusouSoundOff.HitFilter), ("limiter", MusouSoundOff.Limiter), ("steal", MusouSoundOff.Steal),
        ("instlimit", MusouSoundOff.InstLimit), ("ambience", MusouSoundOff.Ambience), ("crowdvoices", MusouSoundOff.CrowdVoices), ("blast", MusouSoundOff.Blast), ("allylayers", MusouSoundOff.AllyLayers), ("boltblast", MusouSoundOff.BoltBlast),
    ];

    /// <summary>"duck, lowpass, ... or all".</summary>
    public static string Choices => string.Join(", ", Names.Select(n => n.Name)) + " or all";

    /// <summary>
    /// Parses a comma-separated list (<c>duck,limiter</c>; <c>all</c>; <c>none</c>; case and spaces ignored). Null or empty is <see cref="MusouSoundOff.None"/>. An unknown name is an error (the
    /// value is then None: a typo must not record a clip that claims a system was off).
    /// </summary>
    public static bool TryParse(string? text, out MusouSoundOff off, out string? error)
    {
        off = MusouSoundOff.None;
        error = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        var result = MusouSoundOff.None;
        foreach (var raw in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw.Equals("all", StringComparison.OrdinalIgnoreCase)) { result |= MusouSoundOff.All; continue; }
            if (raw.Equals("none", StringComparison.OrdinalIgnoreCase)) continue;
            var hit = Names.FirstOrDefault(n => n.Name.Equals(raw, StringComparison.OrdinalIgnoreCase));
            if (hit.Name is null) { error = $"'{raw}' is not one of {Choices}"; return false; }
            result |= hit.Flag;
        }
        off = result;
        return true;
    }

    /// <summary>The names of the systems that are off ("none" when all are on), for the log and the clip's label.</summary>
    public static string Describe(MusouSoundOff off) =>
        off == MusouSoundOff.None ? "none" : off == MusouSoundOff.All ? "all" : string.Join(",", Names.Where(n => off.HasFlag(n.Flag)).Select(n => n.Name));
}
