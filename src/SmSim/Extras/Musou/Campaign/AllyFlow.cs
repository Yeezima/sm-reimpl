using System.Numerics;

namespace SmSim.Extras.Musou.Campaign;

/// <summary>
/// The allies' ways over the walking graph (pass 2's M2, design section I): a few <see cref="FlowField"/>s, each from one goal node, shared by whoever goes there. The graph never changes, so a
/// field stays good until its slot is wanted for another goal (the least recently asked goes). One field is worked on at a time, at most <see cref="ExpansionsPerTick"/> nodes a tick; until a
/// field is ready its users wait or go straight. Deterministic: the slots in order, the clock a count of requests.
/// </summary>
public sealed class AllyFlow(MusouNav nav)
{
    /// <summary>Fields kept (three points, the commander, the landing zone and a few of the players' orders; ours, about 3.2 MB each on Manufactorum).</summary>
    public const int Slots = 8;
    /// <summary>Nodes expanded a tick, as the enemies' field (<see cref="MusouRules.FlowExpansionsPerTick"/>'s default).</summary>
    public const int ExpansionsPerTick = 20_000;
    /// <summary>How far ahead along a field <see cref="Carrot"/> looks, in metres of straight line, and at most how many links.</summary>
    public const float CarrotReach = 3f;
    public const int CarrotLinks = 12;
    /// <summary>A goal is the graph's node within this far (flat) and this far in height of the place asked for.</summary>
    public const float GoalRadius = 6f, GoalHeight = 8f;

    sealed class Slot
    {
        public FlowField? Field;
        public int Goal = -1;
        public uint Used;
    }

    readonly Slot[] _slots = [new(), new(), new(), new(), new(), new(), new(), new()];
    uint _clock;

    /// <summary>The graph the fields are on.</summary>
    public MusouNav Nav => nav;

    /// <summary>The goal node of a place (-1: no node near it).</summary>
    public int GoalNode(Vector3 place) => nav.Nearest(place, GoalRadius, GoalHeight);

    /// <summary>The finished field to <paramref name="goal"/>; null while it is being worked on (the request queues it) or for no goal.</summary>
    public FlowField? Ready(int goal)
    {
        if (goal < 0 || goal >= nav.NodeCount) return null;
        Slot? oldest = null;
        foreach (var slot in _slots)
        {
            if (slot.Goal == goal)
            {
                slot.Used = ++_clock;
                return slot.Field!.Ready ? slot.Field : null;
            }
            if (oldest is null || slot.Used < oldest.Used) oldest = slot;
        }
        oldest!.Field ??= new FlowField(nav);
        oldest.Field.Reset();
        oldest.Goal = goal;
        oldest.Used = ++_clock;
        return null;
    }

    /// <summary>One tick: the first field not finished gets its nodes.</summary>
    public void Step()
    {
        foreach (var slot in _slots)
        {
            if (slot.Goal < 0 || slot.Field!.Ready) continue;
            ReadOnlySpan<int> source = [slot.Goal];
            slot.Field.Step(source, source, ExpansionsPerTick, int.MaxValue);
            return;
        }
    }

    /// <summary>A place on the way from <paramref name="from"/> to <paramref name="goal"/>, up to <see cref="CarrotReach"/> ahead along the field; null while the field is not ready, off the graph, or with no way.</summary>
    public Vector3? Carrot(Vector3 from, int goal)
    {
        if (Ready(goal) is not { } field) return null;
        int node = nav.Nearest(from, 3f, 3f);
        if (node < 0 || float.IsPositiveInfinity(field.Distance(node))) return null;
        var at = nav.Position(node);
        for (int k = 0; k < CarrotLinks; k++)
        {
            int next = field.Next(node);
            if (next < 0) break;
            node = next;
            var p = nav.Position(node);
            var way = new Vector2(p.X - from.X, p.Y - from.Y);
            if (k > 0 && !nav.Graph.CanWalk(from, way)) break;   // a straight line to it would cut the corner
            at = p;
            if (way.LengthSquared() >= CarrotReach * CarrotReach) break;
        }
        return at;
    }
}
