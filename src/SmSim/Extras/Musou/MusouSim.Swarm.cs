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

    public static MusouSwarmRules Default { get; } = new();
}

/// <summary>The crowd's steering (<see cref="MusouSwarmRules"/>), O(1) an enemy.</summary>
public sealed partial class MusouSim
{
    readonly float[] _bearing = new float[Capacity];   // its turn off the flow (radians)
    readonly bool[] _flank = new bool[Capacity];       // it takes the second-nearest target
    readonly bool[] _spread = new bool[Capacity];      // far from its target: it keeps wider apart

    /// <summary>The crowd's rules; null: the 5-wave run's way. Only <see cref="World.StartTfw"/> sets them.</summary>
    public MusouSwarmRules? Swarm { get; set; }

    void SpawnedSwarm(int i)
    {
        _bearing[i] = (2f * Unit(Mix((uint)i * 0x9E3779B1u + 11u, Tick)) - 1f) * MathF.PI / 180f;   // scaled by the rule when used
        _flank[i] = Unit(Mix((uint)i * 0xC2B2AE35u + 7u, Tick)) < (Swarm?.FlankShare ?? 0f);
        _spread[i] = false;
    }

    static Vector2 Turned(Vector2 v, float radians)
    {
        float sin = MathF.Sin(radians), cos = MathF.Cos(radians);
        return new Vector2(v.X * cos - v.Y * sin, v.X * sin + v.Y * cos);
    }

    float Bearing(int i, MusouSwarmRules swarm) => _bearing[i] * swarm.BearingDegrees;

    /// <summary>Whether the walk from enemy <paramref name="i"/> to its target is about the straight line (no wall between; off the field: yes).</summary>
    bool WayIsStraight(int i, float distance, MusouSwarmRules swarm)
    {
        if (Flow is not { Ready: true } || _node[i] < 0) return true;
        return Flow.Distance(_node[i]) <= distance * swarm.StraightRatio + swarm.StraightSlack;
    }

    /// <summary>
    /// The way of an enemy with no token: near and with a straight way, to its own slot round the target (it waits there with <paramref name="hold"/>), else down the flow, turned by its bearing while far. A blocked
    /// enemy follows the plain flow for a while (<see cref="Move"/>), so a turn into a wall never holds it there.
    /// </summary>
    Vector2 SwarmWay(int i, int t, in MusouTarget target, Vector3 d, float speed, bool slot, bool hold, MusouSwarmRules swarm)
    {
        var flat = new Vector2(d.X, d.Y);
        float distance = flat.Length();
        bool free = _flowTicks[i] == 0;
        if (slot && free && distance > 1e-4f && distance <= swarm.SlotWithin + target.Radius && MathF.Abs(d.Z) < 1.5f && WayIsStraight(i, distance, swarm))
        {
            float ring = target.Radius + swarm.SlotMin + (swarm.SlotMax - swarm.SlotMin) * Unit(Mix((uint)i * 0x85EBCA6Bu, 0x51u));
            var toward = flat / distance;
            if (hold && distance <= ring + 0.5f) { Face(i, toward); return Vector2.Zero; }
            var way = flat + Turned(-toward, Bearing(i, swarm)) * ring;   // from here to the slot, on its own side of the target
            float length = way.Length();
            return length > 1e-4f ? way / length * speed : Vector2.Zero;
        }
        var flow = Approach(i, t, d, speed);
        return free && distance > swarm.FanOutBeyond ? Turned(flow, Bearing(i, swarm)) : flow;
    }

    /// <summary>The target an enemy that flanks takes at a look: the second-nearest that is no mere place, within reach; -1: none.</summary>
    int Flank(int i, int best, int second, float secondScore, ReadOnlySpan<MusouTarget> targets)
    {
        if (Swarm is not { } swarm || !_flank[i] || second < 0 || targets[second].Goal || !(secondScore <= swarm.FlankReach * swarm.FlankReach)) return -1;
        return second != best ? second : -1;
    }
}
