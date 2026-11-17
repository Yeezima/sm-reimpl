namespace SmSim;

/// <summary>
/// sim/plasma-cannon-auto-release (owner, 2026-11-17: "when the charge meter reaches the maximum as a charged shot charges, it should
/// release automatically at full charge, whether the player let go of lmb or not"; reference clip 3). The plasma cannon's charge has one
/// firing stage (the data's charge_heavy, 3 s, 1.65 s with Improved Plasma Charge): when the held charge reaches it, the shot leaves
/// by itself on the next tick. Holding on is no longer possible (before: the shot waited in the barrel, the heat running toward an overheat,
/// until the button was let go). Every mode that has the cannon (Classic PvP's Devastator and Havoc; Musou Mode's loadouts run the same
/// StepPlayer). The plasma gun and pistol are not changed (the owner named the cannon only; spec "Plasma cannon auto-release").
/// </summary>
public static class PlasmaCannonAutoRelease
{
    /// <summary>The plasma cannon by its blueprint name (<c>sm_mp_exotic_plasma_cannon</c>, <c>csm_mp_exotic_plasma_cannon</c>).</summary>
    public static bool IsPlasmaCannon(WeaponSpec w) => w.Name.Contains("plasma_cannon", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the charge held for <paramref name="chargeTime"/> seconds on <paramref name="w"/> releases itself under <paramref name="c"/>.</summary>
    public static bool Fires(SimConfig c, WeaponSpec w, ChargeSpec charge, float chargeTime) =>
        c.PlasmaCannonAutoRelease && IsPlasmaCannon(w) && Combat.ChargeStage(charge, chargeTime, firing: true) > 0;
}

public sealed partial record SimConfig
{
    /// <summary>sim/plasma-cannon-auto-release (owner 2026-11-17, protocol 84): a plasma cannon's charge fires by itself when it reaches the firing stage. False (restore switch <c>--old-cannon-hold</c> on the server and every client; config hash): the shot waits for the release. Hashed only when false.</summary>
    public bool PlasmaCannonAutoRelease { get; init; } = true;
}
