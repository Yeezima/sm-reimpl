using System.Numerics;

namespace SmSim.Extras.Musou.Campaign;

/// <summary>
/// The allied pair's goal (<see cref="AllyRules"/>; owner 2026-11-08: "they roam the map together attacking and defending"): they act on their own, with no command. They defend the secured position
/// the Orks press hardest (the Orks near it as they stand, an Ork army on its way to it or attacking it with all its men) once that pressure reaches <see cref="AllyRules.DefendAt"/>; otherwise they
/// attack the contested position they are at (kept until it is secured), else the nearest position not secured, then the HQ; with the battle won, the landing zone.
/// </summary>
public sealed partial class TfwBattle
{
    /// <summary>The pair's goal from <paramref name="from"/> (the leader's feet), keeping <paramref name="current"/> while it still holds: a position's index, <see cref="Positions"/> the HQ, -1 the landing zone.</summary>
    public int AllyGoal(Vector3 from, int current)
    {
        if (Won) return -1;
        Span<ushort> near = stackalloc ushort[AllyRules.ThreatMost];
        int defend = -1;
        float worst = 0f;
        for (int k = 0; k < Positions; k++)
        {
            if (!Imperial(k)) continue;
            float threat = _enemies.Query(_layout.Positions[k], AllyRules.ThreatRadius, near, AllyRules.ThreatRadius);
            foreach (var a in _armies)
                if (a.Side == TfwArmySide.Ork && a.Goal == k && a.Order is TfwArmyOrder.March or TfwArmyOrder.Attack or TfwArmyOrder.Hold) threat += a.FarCount;   // its real Orks are already in the Query
            float need = k == current ? AllyRules.DefendAt * 0.5f : AllyRules.DefendAt;
            if (threat >= need && threat > worst) { worst = threat; defend = k; }
        }
        if (defend >= 0) return defend;
        if (current is >= 0 && current < Positions && !Imperial(current)) return current;
        int open = Nearest(from);
        return open >= 0 ? open : Positions;
    }

    /// <summary>The place of a goal <see cref="AllyGoal"/> gave, on the walking graph's ground.</summary>
    public Vector3 AllyPlace(int goal) => goal < 0 ? _layout.LandingZone : goal < Positions ? Ground(_layout.Positions[goal], _layout.Positions[goal]) : _layout.Hq;
}
