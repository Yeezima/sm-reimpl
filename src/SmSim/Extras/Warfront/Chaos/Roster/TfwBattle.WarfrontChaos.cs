using SmSim.Extras.Warfront.Chaos;

namespace SmSim.Extras.Musou.Campaign;

/// <summary>
/// The Chaos army in the battle (Chaos Warfront C2): the enemy numbers' scale (pressure, not headcount: <see cref="WarfrontChaosRoster.EnemyScale"/> of the Orks' caps) and the Ork-only spawners off
/// (the Weirdboyz, the Scrap Meks). Both return at once unless the run carries the Chaos roster.
/// </summary>
public sealed partial class TfwBattle
{
    /// <summary>Whether this battle fights the Chaos army (the run's Warfront rules carry the roster).</summary>
    public bool ChaosArmy => _enemies.Warfront?.ChaosRoster is not null;

    partial void WarfrontEnemyScale(ref float scale)
    {
        if (ChaosArmy) scale = WarfrontChaosRoster.EnemyScale;
    }

    partial void WarfrontOrkKind(MusouEnemyType kind, ref bool on)
    {
        if (ChaosArmy && !WarfrontChaosRoster.OrkSpawnerOn(kind)) on = false;
    }
}
