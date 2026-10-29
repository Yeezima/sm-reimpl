using SmSim;

namespace SmNet;

/// <summary>
/// The maxima a victim's health and shield fractions are scaled by (Ork O5 / O7a): the victim's class body (<see cref="SimConfig.For"/>), never a perk's
/// (others' perks are not on the wire, so the hit report's fractions are against the class body, and a bar must use the same maxima or it reads wrong:
/// <c>Loadouts.Body</c> would apply a remote player's default perk, e.g. Iron Halo's 150 shield). One rule for the hit report and the aimed-enemy bar.
/// </summary>
public static class HitBars
{
    /// <summary>The body for a victim of <paramref name="playerClass"/> (null: the victim is not known; the config's own).</summary>
    public static SimConfig BodyOf(SimConfig config, int? playerClass) => playerClass is { } k ? config.For(k) : config;

    /// <summary>The health and shield maxima of a victim of <paramref name="playerClass"/>.</summary>
    public static (float Health, float Shield) Maxima(SimConfig config, int? playerClass)
    {
        var body = BodyOf(config, playerClass);
        return (body.HealthMax, body.ShieldMax);
    }
}
