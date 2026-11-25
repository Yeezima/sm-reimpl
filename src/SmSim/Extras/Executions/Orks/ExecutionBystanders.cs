using System.Numerics;
using SmSim.Extras.Musou;

namespace SmSim.Extras.Executions;

/// <summary>
/// Executions X4: who counts as a bystander of a Warfront execution in a horde (lead 40 / owner answer 2026-11-25; the plan's X4 step 2; <b>ours</b>): the bodies the slide must not pass
/// through are the other living players (humans and AI Marines) and the enemies a player's body meets under Musou's own rule (<see cref="MusouSolids.Blocks"/>: the Warboss and the Boss Orks;
/// MusouSim version 31, "a player meets only the Warboss and the Boss Orks"). Regular Orks do not block a player's movement in Warfront, so they do not block the slide: otherwise a crowd
/// would refuse nearly every execution (the slide needs two capsule radii of clearance from each bystander). Pure: no world state.
/// </summary>
public static class ExecutionBystanders
{
    /// <summary>Whether an enemy of <paramref name="type"/> counts as a bystander (player-solid).</summary>
    public static bool Counts(MusouEnemyType type) => MusouSolids.Blocks(type);

    /// <summary>
    /// The feet of the bystanders of <paramref name="executor"/>'s execution at <paramref name="at"/>: every other living player in <paramref name="players"/> and every enemy of
    /// <paramref name="enemies"/> that <see cref="Counts"/>, within <paramref name="reach"/> metres (3D) of <paramref name="at"/>; players first (in the given order), then the enemies.
    /// </summary>
    public static List<Vector3> Warfront(int executor, IReadOnlyList<(int Id, PlayerState State)> players, IReadOnlyList<(MusouEnemyType Type, Vector3 At)> enemies, Vector3 at, float reach)
    {
        var list = new List<Vector3>();
        float reach2 = reach * reach;
        foreach (var (id, p) in players)
            if (id != executor && p.Alive && Vector3.DistanceSquared(p.Position, at) <= reach2) list.Add(p.Position);
        foreach (var (type, where) in enemies)
            if (Counts(type) && Vector3.DistanceSquared(where, at) <= reach2) list.Add(where);
        return list;
    }
}
