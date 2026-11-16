using System.Numerics;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmSim.Extras.Warfront.Squad;

/// <summary>
/// What the squad sim needs of the world it fights in (W9's call surface, so tests run it against a fake and the real MusouSim runs it through <see cref="MusouSquadArena"/>): the enemies near a
/// place, damage to one, a line of sight and where to walk next. The squad sim never touches World or the Musou files; the Orks' side of the fight (a target id, a hit) is
/// <see cref="WarfrontSquadSim.AppendTargets"/> and <see cref="WarfrontSquadSim.Hit"/>.
/// </summary>
public interface ISquadArena
{
    /// <summary>Ticks per second.</summary>
    int TickRate { get; }

    /// <summary>Enemies alive within <paramref name="radius"/> (flat) of <paramref name="centre"/>, nearest first, written into <paramref name="into"/>; the number written.</summary>
    int QueryEnemies(Vector3 centre, float radius, Span<ushort> into);

    /// <summary>The place and class of an enemy that is alive and in play; false for a dead or unknown one.</summary>
    bool TryEnemy(ushort id, out Vector3 position, out SquadEnemyClass kind);

    /// <summary>Damage to an enemy for the squad (the owner is credited by the sim, not here). False with no effect for a dead or unknown enemy.</summary>
    bool DamageEnemy(ushort id, float amount, Vector3 push, out bool killed);

    /// <summary>Whether a shot from <paramref name="from"/> reaches <paramref name="to"/>.</summary>
    bool HasSight(Vector3 from, Vector3 to);

    /// <summary>
    /// The next place to walk to on the way from <paramref name="from"/> to <paramref name="goal"/>. <paramref name="anchor"/> is a place the whole squad shares (its owner or the ordered point):
    /// a long way is searched once for the anchor, not once per Marine. Without a graph, the goal itself.
    /// </summary>
    Vector3 Steer(Vector3 from, Vector3 goal, Vector3 anchor);
}

/// <summary>
/// The real <see cref="MusouSim"/> as the squad's arena: Query and Damage on the pool (attacker 0: no player is credited, the squad sim keeps its own kills), sight from the world's
/// <see cref="CollisionWorld"/> when given, and walking by the Guardsmen's <see cref="AllyFlow"/> ways when given (its fields are shared per anchor: at most one goal per player squad).
/// </summary>
public sealed class MusouSquadArena(MusouSim sim, AllyFlow? ways = null, Func<Vector3, Vector3, bool>? sight = null) : ISquadArena
{
    public int TickRate => sim.TickRate;

    public int QueryEnemies(Vector3 centre, float radius, Span<ushort> into) => sim.Query(centre, radius, into);

    public bool TryEnemy(ushort id, out Vector3 position, out SquadEnemyClass kind)
    {
        if (!sim.TryGet(id, out var e) || !e.Alive) { position = default; kind = SquadEnemyClass.Fodder; return false; }
        position = e.Position;
        kind = ClassOf(e.Type);
        return true;
    }

    public bool DamageEnemy(ushort id, float amount, Vector3 push, out bool killed) => sim.Damage(id, 0, amount, push, out killed);

    public bool HasSight(Vector3 from, Vector3 to) => sight is null || sight(from, to);

    public Vector3 Steer(Vector3 from, Vector3 goal, Vector3 anchor)
    {
        if (ways is null) return goal;
        if (Vector3.Distance(from, goal) <= 12f) return goal;   // close: straight on, no field needed
        int node = ways.GoalNode(anchor);
        return node >= 0 && ways.Carrot(from, node) is { } carrot ? carrot : goal;
    }

    /// <summary>Boss Orks and the Warboss are bosses; the Nobs, 'Ard Boyz, Weirdboyz, Meks, Chaos Marines and Big Shoota Nobs are elites; the rest fodder.</summary>
    public static SquadEnemyClass ClassOf(MusouEnemyType type) => type switch
    {
        MusouEnemyType.Warboss or MusouEnemyType.BossBoy or MusouEnemyType.BossShootaBoy or MusouEnemyType.BossArdBoy or MusouEnemyType.BossNob => SquadEnemyClass.Boss,
        MusouEnemyType.ArdBoy or MusouEnemyType.Nob or MusouEnemyType.ChaosMarine or MusouEnemyType.Weirdboy or MusouEnemyType.BigShootaNob or MusouEnemyType.ScrapMek => SquadEnemyClass.Elite,
        _ => SquadEnemyClass.Fodder,
    };
}
