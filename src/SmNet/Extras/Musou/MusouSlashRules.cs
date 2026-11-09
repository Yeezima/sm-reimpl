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

    /// <summary>
    /// The tear (owner, 2026-11-09: "they should animate (open up the tear and then close the tear). it's jarring the way that they pop in and fade out"; ours, low confidence). The stroke opens along the
    /// cut from a hairline to full width over <see cref="OpenSeconds"/> (the head sweeping from the start to the end of the cut), holds, then closes over the last <see cref="CloseSeconds"/> of its
    /// <see cref="Lifetime"/> (narrowing back to a line, and only then fading out), every step eased with zero slope at both ends so nothing pops.
    /// </summary>
    public const float OpenSeconds = 0.16f, CloseSeconds = 0.45f;

    /// <summary>The stroke's total lifetime for a style whose old lifetime was <paramref name="baseSeconds"/>.</summary>
    public static float Lifetime(float baseSeconds) => baseSeconds + ExtraSeconds;

    static float Ease(float t)
    {
        t = System.Math.Clamp(t, 0f, 1f);
        return t * t * t * (t * (t * 6f - 15f) + 10f);
    }

    /// <summary>How far the tear has opened at <paramref name="age"/> (0 closed, 1 open; eased).</summary>
    public static float Opened(float age) => Ease(age / OpenSeconds);

    /// <summary>How far the tear has closed again at <paramref name="age"/> (0 not yet, 1 shut; eased) for a stroke whose old life was <paramref name="baseSeconds"/>.</summary>
    public static float Closed(float age, float baseSeconds) => Ease((age - (Lifetime(baseSeconds) - CloseSeconds)) / CloseSeconds);

    /// <summary>The tear's width as a share of the full width: opens, holds at 1, closes to a line (0 at both ends).</summary>
    public static float TearWidth(float age, float baseSeconds) => Opened(age) * (1f - Closed(age, baseSeconds));

    /// <summary>The brightness: rises over the first 60 % of the opening and falls only over the last 45 % of the closing, after the width has narrowed (0 at both ends, never a pop).</summary>
    public static float Fade(float age, float baseSeconds) => Ease(age / (OpenSeconds * 0.6f)) * (1f - Ease((Closed(age, baseSeconds) - 0.55f) / 0.45f));

    /// <summary>The head of the sweep along the cut (0 to 1.15: a little past the end so the whole cut is lit once open).</summary>
    public static float Reveal(float age, float baseSeconds) => Opened(age) * 1.15f;

    /// <summary>The slashes drawn in one frame at most (a cleave strikes up to 40 enemies; the pool is <see cref="Pool"/>).</summary>
    public const int PerFrame = 40, Pool = 320;   // 40 (was 24): a cleave strikes up to 40 (owner 2026-11-09), every one drawn; 320 (was 64, 160): the strokes live a second longer and a swing can strike 40

    /// <summary>
    /// Farther than this from the camera no slash is drawn (metres). 30 (was 60): the owner, 2026-11-10, Musou Mode only: "make it so that blue and red slashes don't appear beyond 30m". Both
    /// paths that draw a red or blue stroke gate on it: the server's melee hit (<c>NetworkView.MusouMeleeSlash</c>) and the own strike's predicted contact (<c>MusouOwnStrikeContact</c>).
    /// </summary>
    public const float Metres = 30f;

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
