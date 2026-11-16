using System.Numerics;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmSim.Extras.Warfront.Squad;

/// <summary>
/// What the squad sim needs of the world it fights in (W9's call surface, so tests run it against a fake and the real MusouSim runs it through <see cref="MusouSquadArena"/>): the enemies near a
/// place, damage to one, a line of sight, and the walking graph (is this step on it, a path, a flow field to a fixed point). The squad sim never touches World or the Musou files; the Orks' side of
/// the fight (a target id, a hit) is <see cref="WarfrontSquadSim.AppendTargets"/> and <see cref="WarfrontSquadSim.Hit"/>.
/// </summary>
public interface ISquadArena
{
    /// <summary>Ticks per second.</summary>
    int TickRate { get; }

    /// <summary>Enemies alive within <paramref name="radius"/> (flat) of <paramref name="centre"/>, nearest first, written into <paramref name="into"/>; the number written.</summary>
    int QueryEnemies(Vector3 centre, float radius, Span<ushort> into);

    /// <summary>The place and class of an enemy that is alive and in play; false for a dead or unknown one.</summary>
    bool TryEnemy(ushort id, out Vector3 position, out SquadEnemyClass kind);

    /// <summary>The Ork type of an enemy, alive or just killed (read before a blow so a kill can carry it); false when the arena does not know it (the default: a fake without types).</summary>
    bool TryEnemyType(ushort id, out MusouEnemyType type) { type = default; return false; }

    /// <summary>Damage to an enemy for the squad (the owner is credited by the sim, not here). False with no effect for a dead or unknown enemy.</summary>
    bool DamageEnemy(ushort id, float amount, Vector3 push, out bool killed);

    /// <summary>Whether a shot from <paramref name="from"/> reaches <paramref name="to"/>.</summary>
    bool HasSight(Vector3 from, Vector3 to);

    /// <summary>The walking graph's floor at or near <paramref name="p"/> (within 6 m): where a Marine can be put. False off the graph.</summary>
    bool Ground(Vector3 p, out Vector3 ground);

    /// <summary>
    /// Whether a short step from <paramref name="from"/> to <paramref name="to"/> stays on the walking graph; <paramref name="placed"/> is the place with the floor's height. Without a graph, always
    /// true and the place as given.
    /// </summary>
    bool CanStep(Vector3 from, Vector3 to, out Vector3 placed);

    /// <summary>Whether the straight line from <paramref name="from"/> to <paramref name="to"/> is floor all the way (a Marine may walk it). Without a graph, true.</summary>
    bool CanWalkLine(Vector3 from, Vector3 to);

    /// <summary>A walking path (places, walk links only) from <paramref name="from"/> to <paramref name="to"/>; null when there is none. Without a graph, just the goal.</summary>
    IReadOnlyList<Vector3>? FindPath(Vector3 from, Vector3 to);

    /// <summary>The next place on the flow field to the fixed <paramref name="point"/>; null while the field is built or off the graph. For fixed points only (an order's point), never a moving owner.</summary>
    Vector3? FlowNext(Vector3 from, Vector3 point);

    /// <summary>Once per tick: lets the arena advance its own flow fields.</summary>
    void Tick();
}

/// <summary>
/// The real <see cref="MusouSim"/> as the squad's arena: Query and Damage on the pool (attacker 0: no player is credited, the squad sim keeps its own kills), sight from the world's weapon
/// <see cref="CollisionWorld"/> when given, the sim's walking graph for steps and paths, and an <see cref="AllyFlow"/> of its own (never the Guardsmen's) for the squads' fixed points.
/// <see cref="PathsBuilt"/> and <see cref="FlowGoals"/> count the work done (the bench prints them).
/// </summary>
public sealed class MusouSquadArena : ISquadArena
{
    readonly MusouSim _sim;
    readonly NavGraph? _nav;
    readonly AllyFlow? _flow;
    readonly Func<Vector3, Vector3, bool>? _sight;
    readonly HashSet<int> _goals = [];

    public MusouSquadArena(MusouSim sim, Func<Vector3, Vector3, bool>? sight = null)
    {
        _sim = sim; _sight = sight; _nav = sim.Nav;
        if (sim.Walk is { } walk) _flow = new AllyFlow(walk);
    }

    /// <summary>A sight test from a <see cref="CollisionWorld"/>: the weapon rays' first hit is not nearer than the target (as the Guardsmen's).</summary>
    public static Func<Vector3, Vector3, bool> SightOf(CollisionWorld world) => (from, to) =>
    {
        var eye = from + new Vector3(0f, 0f, 1.5f); var aim = to + new Vector3(0f, 0f, 1.2f);
        var ray = aim - eye; float length = ray.Length();
        if (length < 1e-3f) return true;
        return !(world.Weapons.Raycast(eye, ray / length, length) is { } wall && wall.Distance < length - 0.1f);
    };

    /// <summary>Paths searched so far.</summary>
    public int PathsBuilt { get; private set; }
    /// <summary>Distinct flow-field goal nodes asked for so far (a field is built per new goal).</summary>
    public int FlowGoals => _goals.Count;

    public int TickRate => _sim.TickRate;

    public int QueryEnemies(Vector3 centre, float radius, Span<ushort> into) => _sim.Query(centre, radius, into);

    public bool TryEnemy(ushort id, out Vector3 position, out SquadEnemyClass kind)
    {
        if (!_sim.TryGet(id, out var e) || !e.Alive) { position = default; kind = SquadEnemyClass.Fodder; return false; }
        position = e.Position;
        kind = ClassOf(e.Type);
        return true;
    }

    public bool TryEnemyType(ushort id, out MusouEnemyType type)
    {
        if (_sim.TryGet(id, out var e)) { type = e.Type; return true; }
        type = default; return false;
    }

    public bool DamageEnemy(ushort id, float amount, Vector3 push, out bool killed) => _sim.Damage(id, 0, amount, push, out killed);

    public bool HasSight(Vector3 from, Vector3 to) => _sight is null || _sight(from, to);

    public bool Ground(Vector3 p, out Vector3 ground)
    {
        ground = p;
        if (_nav is null) return true;
        int n = _nav.Nearest(p, 6f, 4f);
        if (n < 0) return false;
        ground = _nav.Position(n);
        return true;
    }

    public bool CanStep(Vector3 from, Vector3 to, out Vector3 placed)
    {
        placed = to;
        if (_nav is null) return true;
        if (!_nav.CanWalk(from, new Vector2(to.X - from.X, to.Y - from.Y))) return false;
        float reach = _nav.Settings.CellSize * 1.5f;
        int n = _nav.Nearest(new Vector3(to.X, to.Y, from.Z), reach, 1.5f);
        if (n < 0) return false;
        placed = new Vector3(to.X, to.Y, _nav.Position(n).Z);
        return true;
    }

    public bool CanWalkLine(Vector3 from, Vector3 to) => _nav is null || _nav.CanWalk(from, new Vector2(to.X - from.X, to.Y - from.Y));

    public IReadOnlyList<Vector3>? FindPath(Vector3 from, Vector3 to)
    {
        if (_nav is null) return [to];
        PathsBuilt++;
        if (_nav.FindPath(from, to, 10f, SquadRules.SearchNodes) is not { Partial: false } path) return null;   // a partial path (the goal not reached, or past the node cap) is "no way" for a follow
        var points = new List<Vector3>(path.Waypoints.Count);
        foreach (var w in path.Waypoints)
        {
            if (w.Kind != NavLinkKind.Walk && points.Count > 0) break;   // a jump or a drop is not a Marine's step: the path ends there
            points.Add(w.Position);
        }
        return points.Count > 0 ? points : null;
    }

    public Vector3? FlowNext(Vector3 from, Vector3 point)
    {
        if (_flow is null) return null;
        int goal = _flow.GoalNode(point);
        if (goal < 0) return null;
        _goals.Add(goal);
        return _flow.Carrot(from, goal);
    }

    public void Tick() => _flow?.Step();

    /// <summary>Boss Orks and the Warboss are bosses; the Nobs, 'Ard Boyz, Weirdboyz, Meks, Chaos Marines and Big Shoota Nobs are elites; the rest fodder.</summary>
    public static SquadEnemyClass ClassOf(MusouEnemyType type) => type switch
    {
        MusouEnemyType.Warboss or MusouEnemyType.BossBoy or MusouEnemyType.BossShootaBoy or MusouEnemyType.BossArdBoy or MusouEnemyType.BossNob => SquadEnemyClass.Boss,
        MusouEnemyType.ArdBoy or MusouEnemyType.Nob or MusouEnemyType.ChaosMarine or MusouEnemyType.Weirdboy or MusouEnemyType.BigShootaNob or MusouEnemyType.ScrapMek => SquadEnemyClass.Elite,
        _ => SquadEnemyClass.Fodder,
    };
}
