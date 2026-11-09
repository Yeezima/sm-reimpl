namespace SmNet.Extras.Musou;

/// <summary>The slash colour of a melee weapon: red for the chain weapons and the knife, blue for the power weapons.</summary>
public enum MusouSlashColour : byte { Red, Blue }

/// <summary>
/// Musou Mode's melee slash and the small screen shake of the player's own melee hits (owner, 2026-11-08, Musou Mode: "bring back the slashing visual effect that would appear over enemies
/// that were hit by player melee attacks. make the effect red for chain weapons and the combat knife, blue for power weapons. add a bit of screen shake"). Every value is ours
/// (kind: ours, confidence: low; the colours and amounts are the owner's words turned into numbers, to refine by playtest). Added content, Musou only: classic PvP never reaches this.
/// </summary>
public static class MusouSlashRules
{
    /// <summary>Seconds a slash lasts (the dark gore's slash lasted 0.26 s) and the stroke's length and thickness range (metres).</summary>
    public const float Seconds = 0.3f, LengthMin = 1.5f, LengthMax = 2.2f, Width = 0.34f;

    /// <summary>
    /// Seconds added to every stroke's lifetime (owner, 2026-11-09, Musou Mode: "make the blue and red slash strokes persist for a bit longer before more smoothly disappearing. add 1 second to
    /// their lifetime before they expire"). The sweep-in and the bright hold keep the old timing; the extra second is the fade-out's tail, eased (<see cref="Fade"/>) so it never pops.
    /// </summary>
    public const float ExtraSeconds = 1f;

    /// <summary>Where the fade starts, as a share of the stroke's old lifetime (the old fade began at 0.45 of it).</summary>
    public const float FadeStart = 0.45f;

    /// <summary>The stroke's total lifetime for a style whose old lifetime was <paramref name="baseSeconds"/>.</summary>
    public static float Lifetime(float baseSeconds) => baseSeconds + ExtraSeconds;

    /// <summary>The stroke's brightness at <paramref name="age"/> seconds: 1 until <see cref="FadeStart"/> of the old lifetime, then a smootherstep down to 0 at the new end (no pop, zero slope at both ends).</summary>
    public static float Fade(float age, float baseSeconds)
    {
        float from = baseSeconds * FadeStart, to = Lifetime(baseSeconds);
        float t = System.Math.Clamp((age - from) / (to - from), 0f, 1f);
        return 1f - t * t * t * (t * (t * 6f - 15f) + 10f);
    }

    /// <summary>The sweep-in's progress at <paramref name="age"/>: over 0.4 of the old lifetime, as before the extra second.</summary>
    public static float Reveal(float age, float baseSeconds) => System.Math.Clamp(age / (baseSeconds * 0.4f), 0.05f, 1f) * 1.15f;

    /// <summary>The slashes drawn in one frame at most (a cleave strikes up to 20 enemies; the pool is <see cref="Pool"/>).</summary>
    public const int PerFrame = 24, Pool = 160;   // 160 (was 64): the strokes live a second longer, so more are alive at once

    /// <summary>Farther than this from the camera no slash is drawn (metres).</summary>
    public const float Metres = 60f;

    /// <summary>
    /// The colour for a melee wargear name (case-insensitive). Chain weapons (chainsword, chain axe, gold chainsword) and the combat knife are red; power weapons (power sword, power axe, thunder
    /// hammer, power mace and maul) are blue. A weapon that fits neither, and a hit whose attacker is unknown (a retainer: the chainsword), is red.
    /// </summary>
    public static MusouSlashColour ColourOf(string? wargear)
    {
        if (string.IsNullOrEmpty(wargear)) return MusouSlashColour.Red;
        string n = wargear.ToLowerInvariant();
        if (n.Contains("chain") || n.Contains("knife")) return MusouSlashColour.Red;
        if (n.Contains("power") || n.Contains("thunder") || n.Contains("hammer") || n.Contains("mace") || n.Contains("maul")) return MusouSlashColour.Blue;
        return MusouSlashColour.Red;
    }

    /// <summary>The colour as linear RGB (a little over 1 so the glow reads in the light).</summary>
    public static (float R, float G, float B) Rgb(MusouSlashColour c) => c == MusouSlashColour.Blue ? (0.25f, 0.55f, 1.6f) : (1.6f, 0.1f, 0.06f);

    /// <summary>The own melee hit's camera shake (radians, the same unit as the weapon kicks): the first enemy a swing hits, and each further one.</summary>
    public const float ShakeFirst = 0.008f, ShakeExtra = 0.001f;

    /// <summary>The most one swing shakes the camera by in all (radians), however many enemies it strikes: a swing that hits 20 does not stack into a big shake.</summary>
    public const float ShakeCap = 0.014f;

    /// <summary>Seconds without an own melee hit after which the next one starts a new swing's shake budget.</summary>
    public const double ShakeSwingGap = 0.45;

    /// <summary>The shake of the next hit of a swing that has shaken <paramref name="soFar"/> radians over <paramref name="hitsSoFar"/> hits, times <paramref name="scale"/> (0 = off); never past the cap.</summary>
    public static float Shake(int hitsSoFar, float soFar, float scale)
    {
        if (!(scale > 0f)) return 0f;
        float want = (hitsSoFar <= 0 ? ShakeFirst : ShakeExtra) * scale;
        return System.Math.Max(0f, System.Math.Min(want, ShakeCap * scale - soFar));
    }
}
