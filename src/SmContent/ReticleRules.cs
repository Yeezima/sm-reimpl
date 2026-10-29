using SmSim;

namespace SmContent;

/// <summary>
/// One weapon family's reticle in the HUD movie (ui/hud-fidelity): the instance names, inside the movie's reticle container <see cref="ReticleRules.Container"/>, of the idle
/// reticle and of the one shown with an enemy under it (<see cref="Active"/>); <see cref="Spread"/> names the nested sprite whose frames carry the spread ticks
/// (labels <c>min_aim</c>, <c>min_normal</c>, <c>max_normal</c>, <c>max_aim</c>), when the family has one.
/// </summary>
public sealed record ReticleSpec(string Family, string Idle, string? Active, bool Spread);

/// <summary>
/// Which of the original reticles a weapon shows and which frame of its spread clip (ui/hud-fidelity; owner, 2026-10-29: "crosshairs identical to the original"). The names are the
/// movie's (<c>Hud.gfx</c> sprite placed as <c>Reticule_Ingame_0_mc</c>: <c>boltpistol_mc</c>, <c>idle_mc</c> / <c>enemy_mc</c> ...); which weapon type picks which name is an
/// <b>interpretation</b> of the names (the code that picks them is in the executable). The stalker bolter and the lascannon show <b>no</b> reticle outside the scope (owner's frame of the
/// original; the movie does have a faint <c>stalker_idle_mc</c>, not used); scoped, the scope overlay carries its own (<c>ScopeArt</c>).
/// </summary>
public static class ReticleRules
{
    /// <summary>The reticle container's instance name on the movie's root (placed at 639.2, 356.85 of the 1280 x 720 stage).</summary>
    public const string Container = "Reticule_Ingame_0_mc";

    /// <summary>The hit marker inside the container (10 frames, added, 75 % alpha).</summary>
    public const string HitMarker = "hit_mc";

    /// <summary>The frame rate of the movie's clips.</summary>
    public const double FramesPerSecond = 30.0;

    static readonly ReticleSpec Bolter = new("bolter", "idle_mc", "enemy_mc", true);
    static readonly ReticleSpec BoltPistol = new("bolt_pistol", "boltpistol_mc", "boltpistol_active_mc", true);
    static readonly ReticleSpec StormBolter = new("storm_bolter", "storm_bolter_mc", "storm_bolter_active_mc", true);
    static readonly ReticleSpec HeavyBolter = new("heavy_bolter", "heavybidle_mc", "heavybtarget_mc", false);
    static readonly ReticleSpec PlasmaGun = new("plasma_gun", "plasmaidle_mc", "plasmatarget_mc", false);
    static readonly ReticleSpec PlasmaPistol = new("plasma_pistol", "plasmapistol_mc", "plasmapistol_active_mc", false);
    static readonly ReticleSpec PlasmaCannon = new("plasma_cannon", "plasma_cannon_mc", "plasma_cannon_active_mc", false);
    static readonly ReticleSpec Melta = new("melta", "melta_mc", "melta_active_mc", false);
    static readonly ReticleSpec Launcher = new("grenade_launcher", "grenade_neutral_mc", "grenade_active_mc", false);

    /// <summary>Every family (for warming and checks).</summary>
    public static readonly IReadOnlyList<ReticleSpec> All = [Bolter, BoltPistol, StormBolter, HeavyBolter, PlasmaGun, PlasmaPistol, PlasmaCannon, Melta, Launcher];

    /// <summary>
    /// The reticle of a ranged weapon by its wargear file and <c>ui_data/weapon_type</c> (the name decides when the type is missing); null for the scoped weapons (no reticle
    /// outside the scope) and for anything else (no reticle: the HUD draws none).
    /// </summary>
    public static ReticleSpec? For(string wargear, string? weaponType)
    {
        string type = (weaponType is { Length: > 0 } ? weaponType : HudRules.TypeFromName(wargear)).ToLowerInvariant();
        return type switch
        {
            "bolter" => Bolter,
            "bolt_pistol" => BoltPistol,
            "storm_bolter" => StormBolter,
            "heavy_bolter" => HeavyBolter,
            "plasma_gun" => PlasmaGun,
            "plasma_pistol" => PlasmaPistol,
            "plasma_cannon" => PlasmaCannon,
            "melta" => Melta,
            "grenade_launcher" => Launcher,
            _ => null,   // stalker, laz_cannon / lascannon (owner: none unscoped), melee, unknown
        };
    }

    /// <summary>
    /// The frame range of a spread clip for a stance, from its labels: aiming from <c>min_aim</c> to <c>max_aim</c> (else <c>max_normal</c>), hip from <c>min_normal</c> (else
    /// <c>min_aim</c>) to <c>max_normal</c> (else <c>max_aim</c>); missing labels fall back to the clip's ends. <b>Interpretation</b> of the label names.
    /// </summary>
    public static (int From, int To) SpreadRange(IReadOnlyDictionary<string, int> labels, int frameCount, bool aiming)
    {
        int last = Math.Max(0, frameCount - 1);
        int L(string name, int fallback) => labels.TryGetValue(name, out int f) ? Math.Clamp(f, 0, last) : fallback;
        if (aiming) return (L("min_aim", 0), L("max_aim", L("max_normal", last)));
        return (L("min_normal", L("min_aim", 0)), L("max_normal", L("max_aim", last)));
    }

    /// <summary>The frame for a spread fraction (0 rested, 1 the stance's widest) in a range.</summary>
    public static int SpreadFrame((int From, int To) range, float fraction)
    {
        float f = float.IsFinite(fraction) ? Math.Clamp(fraction, 0f, 1f) : 0f;
        return (int)MathF.Round(range.From + (range.To - range.From) * f);
    }

    /// <summary>
    /// How far the next shot's scatter is between the stance's narrowest and widest (0..1, <b>ours</b>): the horizontal half-width now against the smallest initial half-width and the
    /// largest maximum of the weapon's sets for that stance over the three movement bands (so walking and running open the reticle, and shooting blooms it). 0 without spread.
    /// </summary>
    public static float SpreadFraction(WeaponSpec? weapon, PlayerState s, float speedScale = 1f, int stage = 0)
    {
        if (weapon?.Spread is not { } spec) return 0f;
        var stances = spec.For(stage);
        var stance = s.Aiming ? SpreadStance.Aiming : SpreadStance.Standing;
        float lo = float.MaxValue, hi = float.MinValue;
        foreach (var band in new[] { SpreadMovement.Idle, SpreadMovement.Walking, SpreadMovement.Running })
            if (stances.SetFor(stance, band)?.Scatter is { } sc)
            {
                lo = MathF.Min(lo, sc.InitialHorizontal);
                hi = MathF.Max(hi, MathF.Max(sc.MaxHorizontal, sc.InitialHorizontal));
            }
        if (lo == float.MaxValue || hi - lo < 1e-5f) return 0f;
        var (h, _) = Spread.HalfWidths(weapon, s, stage, speedScale);
        return Math.Clamp((h - lo) / (hi - lo), 0f, 1f);
    }
}
