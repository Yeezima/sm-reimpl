namespace SmContent;

/// <summary>
/// Install transitions (2026-11-12, pass 2 batch 1; engine-free, tested): which fall and which jump-pack landing a marine's melee weapon picks. The graphs choose on <c>BOOL_Thunderhammer</c> /
/// <c>BOOL_Chainsword</c>; ours reads the melee wargear (through <see cref="AnimationAlias"/>, so Musou's spear counts as the hammer, whose alias carries "thunder_hammer"). The switch is <c>--no-extra-transitions</c>.
/// </summary>
public static class MeleeFallChoice
{
    /// <summary>True for the thunder hammer and the daemon maul, by the animation alias of the wargear (the spear included).</summary>
    public static bool IsHammer(string? aliasedWargear) =>
        aliasedWargear is not null && (aliasedWargear.Contains("thunder_hammer", StringComparison.OrdinalIgnoreCase) || aliasedWargear.Contains("power_mace", StringComparison.OrdinalIgnoreCase));

    /// <summary>The melee fall family of a wargear (already aliased): Hammer, Chainsword, else Rifle; Rifle whenever <paramref name="extraTransitionsOff"/> or there is no wargear.</summary>
    public static FallFamily Family(string? aliasedWargear, bool extraTransitionsOff)
    {
        if (extraTransitionsOff || aliasedWargear is null) return FallFamily.Rifle;
        if (IsHammer(aliasedWargear)) return FallFamily.Hammer;
        return aliasedWargear.Contains("chainsword", StringComparison.OrdinalIgnoreCase) ? FallFamily.Chainsword : FallFamily.Rifle;
    }

    /// <summary>The landing clip to use: the hammer's when the wargear is a hammer, switch on and the hammer's clip exists; else the plain one.</summary>
    public static string? Landing(string? aliasedWargear, bool extraTransitionsOff, string? plain, string? hammer) =>
        !extraTransitionsOff && IsHammer(aliasedWargear) ? hammer ?? plain : plain;
}
