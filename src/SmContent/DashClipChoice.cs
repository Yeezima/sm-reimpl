namespace SmContent;

/// <summary>The jump-pack dash's clip family: the plain set of <c>SM_Base_MP</c> or the thunder hammer's of <c>MP_SM_MEL_ThunderHammer</c> (<see cref="EvadeTuning.HammerGraph"/>).</summary>
public enum DashFamily { Plain, Hammer }

/// <summary>
/// Install transitions, pass 2 batch 2 (2026-11-25; engine-free, tested): the clips a jump-pack marine's air dash shows. <see cref="EvadeTuning"/> reads the dash from one graph (the
/// plain <c>SM_Base_MP</c> set, which the simulation keeps); a hammer or daemon maul in the marine's hands picks the hammer graph's own flight and landing clips, of the same lengths and
/// landing branch times, so the client poses them at the simulation's times with no stretch. The choice reads the melee wargear by its animation alias (<see cref="MeleeFallChoice.IsHammer"/>;
/// Musou's spear counts as the hammer, as for the falls). The switch is <c>--no-extra-transitions</c>.
/// </summary>
public static class DashClipChoice
{
    /// <summary>The dash family of a wargear (already aliased): Hammer for the thunder hammer and the daemon maul; Plain otherwise, without a wargear, or whenever <paramref name="extraTransitionsOff"/>.</summary>
    public static DashFamily Family(string? aliasedWargear, bool extraTransitionsOff) =>
        !extraTransitionsOff && MeleeFallChoice.IsHammer(aliasedWargear) ? DashFamily.Hammer : DashFamily.Plain;

    /// <summary>
    /// The family's name for a plain dash clip the simulation plays: <c>SM_JMP_Dash_</c> + F/B/L/R → <c>SM_JMP_TH_Dash_</c> + the same letter; <c>SM_JMP_Dash_Landing_Shallow_</c> + B/L/R
    /// → <c>SM_JMP_TH_Dash_Landing_</c> + the same letter, and the forward landing (<c>_01</c> or <c>_F</c>) → <c>..._F</c>. Null for the plain family or any other clip (a roll, a hop, a
    /// diagonal plays the forward or back clip and so maps through it).
    /// </summary>
    public static string? HammerClip(string clip)
    {
        if (clip.StartsWith(EvadeTuning.DashLandingPrefix, StringComparison.OrdinalIgnoreCase))
        {
            string suffix = clip[EvadeTuning.DashLandingPrefix.Length..];
            string? letter = suffix.ToUpperInvariant() switch { "01" or "F" => "F", "B" => "B", "L" => "L", "R" => "R", _ => null };
            return letter is null ? null : EvadeTuning.HammerDashLandingPrefix + letter;
        }
        if (clip.StartsWith(EvadeTuning.DashClipPrefix, StringComparison.OrdinalIgnoreCase) && clip.Length == EvadeTuning.DashClipPrefix.Length + 1
            && "FBLR".Contains(char.ToUpperInvariant(clip[^1])))
            return EvadeTuning.HammerDashClipPrefix + char.ToUpperInvariant(clip[^1]);
        return null;
    }

    /// <summary>
    /// The clip to pose for the simulation's dash clip <paramref name="clip"/>: the hammer's when the family is <see cref="DashFamily.Hammer"/> and <paramref name="rigHas"/> finds it;
    /// else <paramref name="clip"/> itself.
    /// </summary>
    public static string Clip(string clip, DashFamily family, Func<string, bool> rigHas) =>
        family == DashFamily.Hammer && HammerClip(clip) is { } hammer && rigHas(hammer) ? hammer : clip;
}
