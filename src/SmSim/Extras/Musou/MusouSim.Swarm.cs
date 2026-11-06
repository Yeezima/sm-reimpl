using System.Numerics;

namespace SmSim.Extras.Musou;

/// <summary>
/// How a war's Orks move and fight as a crowd (pass 2's M3, design sections B and I; every value ours, spec "Musou Mode: the Orks"). Off (<see cref="MusouSim.Swarm"/> null) the enemies move as the 5-wave
/// run always has, which <c>MusouSkirmishPinTests</c> holds. A change here raises <c>TfwRules.Version</c>.
/// </summary>
public sealed record MusouSwarmRules
{
    /// <summary>Each enemy keeps a bearing of up to this much each way off the flow, its own for life.</summary>
    public float BearingDegrees { get; init; } = 35f;
    /// <summary>It turns the flow by its bearing while its target is farther than this.</summary>
    public float FanOutBeyond { get; init; } = 12f;
    /// <summary>Within this of its target, on the same level and with the way there about straight, it heads for its own slot round the target.</summary>
    public float SlotWithin { get; init; } = 25f;
    /// <summary>A slot lies this far from the target's edge, each enemy its own distance; it waits there for a token.</summary>
    public float SlotMin { get; init; } = 2f;
    public float SlotMax { get; init; } = 5f;
    /// <summary>"About straight": the walk to the target is at most this many times the straight line, plus <see cref="StraightSlack"/> metres.</summary>
    public float StraightRatio { get; init; } = 1.3f;
    public float StraightSlack { get; init; } = 2f;
    /// <summary>Beyond <see cref="SpreadBeyond"/> of its target an enemy keeps this many times its radius from others.</summary>
    public float SpreadScale { get; init; } = 1.6f;
    public float SpreadBeyond { get; init; } = 10f;
    /// <summary>This share of the enemies takes the second-nearest target, within <see cref="FlankReach"/>.</summary>
    public float FlankShare { get; init; } = 0.3f;
    public float FlankReach { get; init; } = 45f;
    /// <summary>Attack tokens on one player and on one Guard squad.</summary>
    public int MeleeTokensPlayer { get; init; } = 6;
    public int ShooterTokensPlayer { get; init; } = 8;
    public int MeleeTokensSquad { get; init; } = 12;
    public int ShooterTokensSquad { get; init; } = 10;
    /// <summary>An enemy stops this far short of a place it goes to (<see cref="MusouTarget.Goal"/>).</summary>
    public float GoalStand { get; init; } = 3f;

    // Packs (MusouSim.Pack.cs; the owner, 2026-11-06: "pool up at their spawn point before fanning out, swarming their target in a rough blobby formation"; every value ours, an estimate).
    /// <summary>A far enemy's heading wanders up to this much each way off the flow, over its own period of <see cref="WanderPeriodMin"/> to <see cref="WanderPeriodMax"/> seconds.</summary>
    public float WanderDegrees { get; init; } = 25f;
    public float WanderPeriodMin { get; init; } = 3f;
    public float WanderPeriodMax { get; init; } = 7f;
    /// <summary>A pack's disc holds this much ground an Ork (m²): 16 Orks 2.3 m round, 200 about 11 m, 400 about 16 m.</summary>
    public float PackAreaPerOrk { get; init; } = 2f;
    /// <summary>
    /// A member heads for its spot in the disc round a point this far beyond the disc's edge down the flow from the pack's centre (0: no pack steering, each Ork down the field on its own), at its speed times its
    /// distance to that spot over the lead, between <see cref="PackLeastSpeed"/> and <see cref="PackMostSpeed"/> (the front waits, the back catches up).
    /// </summary>
    public float PackLead { get; init; } = 6f;
    public float PackLeastSpeed { get; init; } = 0.6f;
    public float PackMostSpeed { get; init; } = 1.3f;
    /// <summary>No pull for a member more than this beyond the disc's edge (a straggler), or more than <see cref="PackLevel"/> above or below its centre.</summary>
    public float PackLooseBeyond { get; init; } = 12f;
    public float PackLevel { get; init; } = 2f;
    /// <summary>Near its target an enemy takes a place up to this far round each way from the side it came from (its bearing's share), walking round at most <see cref="OrbitStepDegrees"/> ahead at a time.</summary>
    public float SurroundDegrees { get; init; } = 160f;
    public float OrbitStepDegrees { get; init; } = 50f;

    public static MusouSwarmRules Default { get; } = new();
}

/// <summary>The crowd's steering (<see cref="MusouSwarmRules"/>), O(1) an enemy.</summary>
public sealed partial class MusouSim
{
    readonly float[] _bearing = new float[Capacity];   // its turn off the flow (radians)
    readonly bool[] _flank = new bool[Capacity];       // it takes the second-nearest target
    readonly bool[] _spread = new bool[Capacity];      // far from its target: it keeps wider apart

    /// <summary>
    /// Places a ranged Ork holding its band keeps out of: a capture position being taken (centre, flat radius, vertical reach). A shooter holds 12 m or more from its target, so with the target on a point's centre the idle
    /// shooters stood just inside the point's 13.5 to 15 m ring and kept it Clearing for as long as they lived, while a player had to hunt each one down. Only a war fills it (<see cref="TfwBattle"/>); empty, nothing changes.
    /// </summary>
    public List<(Vector3 Centre, float Radius, float Reach)> KeepOut { get; } = new();

    /// <summary>The way out of a keep-out place for the ranged Ork <paramref name="i"/> that would hold where it stands (zero when it stands outside every one, or in the 5-wave run).</summary>
    Vector2 KeepOutWay(int i, float speed)
    {
        if (Swarm is null || KeepOut.Count == 0) return Vector2.Zero;
        foreach (var (centre, radius, reach) in KeepOut)
        {
            float dx = _position[i].X - centre.X, dy = _position[i].Y - centre.Y;
            if (MathF.Abs(_position[i].Z - centre.Z) > reach) continue;
            float flat = MathF.Sqrt(dx * dx + dy * dy);
            if (flat >= radius + 1f) continue;
            return flat > 1e-3f ? new Vector2(dx, dy) / flat * speed : Vector2.UnitX * speed;
        }
        return Vector2.Zero;
    }

    /// <summary>The crowd's rules; null: the 5-wave run's way. Only <see cref="World.StartTfw"/> sets them.</summary>
    public MusouSwarmRules? Swarm { get; set; }

    void SpawnedSwarm(int i)
    {
        _bearing[i] = (2f * Unit(Mix((uint)i * 0x9E3779B1u + 11u, Tick)) - 1f) * MathF.PI / 180f;   // scaled by the rule when used
        _flank[i] = Unit(Mix((uint)i * 0xC2B2AE35u + 7u, Tick)) < (Swarm?.FlankShare ?? 0f);
        _spread[i] = false;
        SpawnedPack(i);
    }

    static Vector2 Turned(Vector2 v, float radians)
    {
        float sin = MathF.Sin(radians), cos = MathF.Cos(radians);
        return new Vector2(v.X * cos - v.Y * sin, v.X * sin + v.Y * cos);
    }

    float Bearing(int i, MusouSwarmRules swarm) => _bearing[i] * swarm.BearingDegrees;

    /// <summary>Whether the walk from enemy <paramref name="i"/> to its target is about the straight line (no wall between; off the field: yes).</summary>
    /// <summary>How long (s) a Boss Ork that found a wall in its straight line keeps to the flow field (ours; about 6 m at its pace, past the corner).</summary>
    public const float BossNotStraightSeconds = 1.5f;

    bool WayIsStraight(int i, float distance, MusouSwarmRules swarm, Vector3 targetAt)
    {
        if (Flow is not { Ready: true } || _node[i] < 0) return true;
        if (!(Flow.Distance(_node[i]) <= distance * swarm.StraightRatio + swarm.StraightSlack)) return false;
        // A Boss Ork (a handful at most) also walks the line on the graph: by the lengths alone the way read "straight" on one node at a wall's corner and "round" on the next, and it stepped to and fro there for good,
        // facing one way and the other, and past the corner it headed for the target through the wall (night/ork-movement, 2026-11-06). The crowd keeps the lengths alone.
        if (MusouBossOrks.Is(_type[i]) && Walk is { } walk && walk.Nearest(targetAt, 2f, 2.5f) is var goal and >= 0 && !walk.Graph.StraightWalk(_node[i], goal))
        {
            _flowTicks[i] = Math.Max(_flowTicks[i], Ticks(BossNotStraightSeconds));   // and it keeps to the flow for a spell, so the verdict does not flip back at the next cell
            return false;
        }
        return true;
    }

    /// <summary>
    /// The way of an enemy with no token: near and with a straight way, to its own place round the target (it waits there with <paramref name="hold"/>), else down the flow: a pack's member with its wander and its
    /// pull to its spot in the pack (<see cref="PackWay"/>), one in no pack turned by its bearing and its wander, while far. A blocked
    /// enemy follows the plain flow for a while (<see cref="Move"/>), so a turn into a wall never holds it there.
    /// </summary>
    Vector2 SwarmWay(int i, int t, in MusouTarget target, Vector3 d, float speed, bool slot, bool hold, MusouSwarmRules swarm)
    {
        var flat = new Vector2(d.X, d.Y);
        float distance = flat.Length();
        bool free = _flowTicks[i] == 0;
        if (slot && free && distance > 1e-4f && distance <= swarm.SlotWithin + target.Radius && MathF.Abs(d.Z) < 1.5f && WayIsStraight(i, distance, swarm, target.Position))
        {
            float ring = target.Radius + swarm.SlotMin + (swarm.SlotMax - swarm.SlotMin) * Unit(Mix((uint)i * 0x85EBCA6Bu, 0x51u));
            var way = SurroundWay(i, target, flat, distance, ring, speed, swarm, out bool settled);   // MusouSim.Pack.cs: its own place round the target, walking round to it
            if (hold && settled) { Face(i, flat / distance); return Vector2.Zero; }
            return way;
        }
        var flow = Approach(i, t, d, speed);
        if (!free || distance <= swarm.FanOutBeyond || flow == Vector2.Zero) return flow;
        return _pack[i] != 0 ? PackWay(i, flow, speed, swarm) : Turned(flow, Bearing(i, swarm) + Wander(i, swarm));
    }

    /// <summary>The target an enemy that flanks takes at a look: the second-nearest that is no mere place, within reach; -1: none.</summary>
    int Flank(int i, int best, int second, float secondScore, ReadOnlySpan<MusouTarget> targets)
    {
        if (Swarm is not { } swarm || !_flank[i] || second < 0 || targets[second].Goal || !(secondScore <= swarm.FlankReach * swarm.FlankReach)) return -1;
        return second != best ? second : -1;
    }
}
