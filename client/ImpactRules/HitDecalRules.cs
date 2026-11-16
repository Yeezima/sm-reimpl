namespace SmClient.ImpactRules;

/// <summary>
/// fix/hit-decals-map-only (owner, 2026-11-08): the weapon's hit container (decals, impact marks, exit wounds) is for the map. A ranged shot that hits a player leaves none
/// of it on or near him; the armour spark / flash / blood feedback is separate and stays. A hit with no player victim (the map, or a Musou enemy) is unchanged.
/// </summary>
public static class HitDecalRules
{
    public static bool DrawsContainer(SmSim.ShotHit hit, int victim) => hit == SmSim.ShotHit.World || victim <= 0;
}
