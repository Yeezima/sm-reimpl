namespace SmContent;

/// <summary>
/// The stalker bolter's wall crater (the owner, 2026-11-02: "it should have one, that is larger than bolt pistol and bolter wall decal bullet holes"). A **deliberate change from the original**: the data's
/// <c>stalker_pattern_bolter</c> container leaves stone with no effect and elsewhere the faint husk. A world hit plays the bolter's own crater effect for the surface at <see cref="Scale"/> instead.
/// </summary>
public static class StalkerCrater
{
    /// <summary>The size against the bolter's crater (ours).</summary>
    public const float Scale = 1.6f;

    /// <summary>Whether a wargear file is the stalker bolter of either side.</summary>
    public static bool Is(string? wargear) => wargear is not null && wargear.EndsWith("mp_ranged_sniper_rifle", StringComparison.OrdinalIgnoreCase);

    /// <summary>The bolter's crater for a surface name: the metal crater on metal, else the stone one.</summary>
    public static string Effect(string surface) => surface.Equals("metal", StringComparison.OrdinalIgnoreCase) ? "bolter_dmg_metal" : "bolter_dmg_stone";
}
