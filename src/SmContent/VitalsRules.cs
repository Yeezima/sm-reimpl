namespace SmContent;

/// <summary>
/// Which frame of the HUD movie's bar clips (<c>Hud.gfx</c> <c>unifiedhealth_mc</c>, ui/hud-2) shows a fraction of the health and of the armour. <c>healthbar_mc</c> has 100 frames (0 full, 99 empty);
/// <c>ironhalo_mc</c> (the cyan armour bar) has 100: frames 0 to 14 are a flash (the medallion's starburst shrinking in), frame 15 the whole bar, frames 16 to 99 the bar cut shorter (with its white end
/// cap) down to the starburst alone. The mapping is an <b>interpretation</b> of the frames (the script that sets them is not read), linear in the fraction.
/// </summary>
public static class VitalsRules
{
    /// <summary>The health clip's last frame (empty).</summary>
    public const int HealthEmpty = 99;

    /// <summary>The armour clip's whole-bar frame, its starburst-only frame, and the flash's last frame.</summary>
    public const int ArmourFull = 15, ArmourEmpty = 99, FlashLast = 14;

    /// <summary>The armour bar's frame for a fraction of the armour: the whole bar at full, then the cut bar by the fraction, the starburst alone at 0 or when dead.</summary>
    public static int ArmourFrame(float fraction, bool alive)
    {
        if (!alive || !float.IsFinite(fraction) || fraction <= 0.001f) return ArmourEmpty;
        if (fraction >= 0.999f) return ArmourFull;
        return Math.Clamp(ArmourFull + 1 + (int)MathF.Round((1f - fraction) * (ArmourEmpty - ArmourFull - 1)), ArmourFull + 1, ArmourEmpty);
    }

    /// <summary>
    /// The plain armour clip <c>lightdamagebar_mc</c> (the bar without the Iron Halo perk, ui/hud-bar-colours): frame 0 the whole bar, frames 1 to 98 the bar cut by its mask, whose right edge moves
    /// 3.62 movie px a frame from the whole length (about 369 px, measured on the rendered frames: 102 frames' worth), frame 99 no mask (the lines bare, white: drawn as nothing), frames 100 to 109 the
    /// whole bar fading from cream to white (a flash whose trigger is script: not played).
    /// </summary>
    public const int PlainFull = 0, PlainEmpty = 99, PlainLastCut = 98;

    /// <summary>Movie frames of the plain clip's mask for the whole bar length (369 px at 3.62 px a frame).</summary>
    public const float PlainFramesPerBar = 102f;

    /// <summary>The plain armour bar's frame for a fraction of the armour: the whole bar at full, the mask's cut proportional to the fraction, <see cref="PlainEmpty"/> at 0 or when dead.</summary>
    public static int PlainArmourFrame(float fraction, bool alive)
    {
        if (!alive || !float.IsFinite(fraction) || fraction <= 0.001f) return PlainEmpty;
        if (fraction >= 0.999f) return PlainFull;
        return Math.Clamp((int)MathF.Round((1f - fraction) * PlainFramesPerBar), PlainFull + 1, PlainLastCut);
    }

    /// <summary>The health bar's frame for a fraction of the health: frame 0 full, frame 99 empty (also when dead).</summary>
    public static int HealthFrame(float fraction, bool alive)
    {
        if (!alive) return HealthEmpty;
        float f = float.IsFinite(fraction) ? Math.Clamp(fraction, 0f, 1f) : 0f;
        return Math.Clamp((int)MathF.Round((1f - f) * HealthEmpty), 0, HealthEmpty);
    }
}
