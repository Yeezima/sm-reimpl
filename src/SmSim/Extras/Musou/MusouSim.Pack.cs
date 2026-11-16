using System.Numerics;

namespace SmSim.Extras.Musou;

/// <summary>
/// The crowd's packs (Musou Mode only, <see cref="MusouSim.Swarm"/> set; the owner, 2026-11-06: "make the orks stop running in straight lines in musou mode. they should pool up at their spawn point before fanning out,
/// swarming their target in a rough blobby formation"; every value ours, spec "Musou Mode: packs"). The Orks a leash group lets go together (<see cref="MusouSim.ReleaseGroup"/>, or one of it woken first) form a pack.
/// Each tick a pack's centre is the mean of its members in play and its lead point lies down the flow field from the centre, <see cref="MusouSwarmRules.PackLead"/> beyond the edge of its disc (whose area grows
/// with the head count); a member far from its target heads for its own spot in the disc moved to the lead point, its heading wandering slowly, so the pack moves as a loose blob instead of each Ork following its own
/// line of the field's cells (which squeeze into a file round every corner). Near the target (<see cref="MusouSwarmRules.SlotWithin"/>) each goes to its own place
/// round it, spread over <see cref="MusouSwarmRules.SurroundDegrees"/> each way of the side it came from, walking round the target to get there. O(1) an enemy plus O(packs) a tick; deterministic (slots in id order).
/// </summary>
public sealed partial class MusouSim
{
    /// <summary>Pack ids in use at once (ours): 1 to this less one, reused round-robin, skipping any that still has members.</summary>
    public const int MaxPacks = 512;

    readonly int[] _pack = new int[Capacity];               // its pack (0: none)
    readonly Vector2[] _packSpot = new Vector2[Capacity];   // its own spot in the pack's unit disc
    readonly float[] _wanderPhase = new float[Capacity];    // its wander's phase (radians) and rate (radians a second)
    readonly float[] _wanderRate = new float[Capacity];
    readonly float[] _slotAngle = new float[Capacity];      // its place round its target (radians, world), chosen when it came near
    readonly int[] _slotFor = new int[Capacity];            // the target that place is round (0: none chosen)
    readonly Vector3[] _packSum = new Vector3[MaxPacks];
    readonly int[] _packCount = new int[MaxPacks];
    readonly Vector3[] _packCentre = new Vector3[MaxPacks];
    readonly int[] _packMembers = new int[MaxPacks];
    readonly int[] _packNode = new int[MaxPacks];           // a member's node (the centre's fallback on the graph)
    readonly Vector2[] _packLead = new Vector2[MaxPacks];   // the lead point (valid while _packLeads)
    readonly bool[] _packLeads = new bool[MaxPacks];
    readonly Dictionary<int, int> _openPack = new();        // a leash group's pack while it lets its Orks go one by one (woken), until ReleaseGroup closes it
    int _lastPack;

    /// <summary>The pack enemy <paramref name="id"/> moves with (0: none, unknown, or the 5-wave run).</summary>
    public int PackOf(ushort id) => id >= 1 && id <= Capacity && _used[id - 1] ? _pack[id - 1] : 0;

    /// <summary>A pack's centre and members in play at the start of the last <see cref="Step"/> (0 members: none).</summary>
    public (Vector3 Centre, int Members) Pack(int pack) => pack > 0 && pack < MaxPacks ? (_packCentre[pack], _packMembers[pack]) : (Vector3.Zero, 0);

    void SpawnedPack(int i)
    {
        _pack[i] = 0;
        _slotFor[i] = 0;
        float a = MathF.Tau * Unit(Mix((uint)i * 0x27D4EB2Fu + 3u, Tick)), r = MathF.Sqrt(Unit(Mix((uint)i * 0x165667B1u + 5u, Tick)));
        _packSpot[i] = new Vector2(MathF.Cos(a), MathF.Sin(a)) * r;
        _wanderPhase[i] = MathF.Tau * Unit(Mix((uint)i * 0xD3A2646Cu + 9u, Tick));
        _wanderRate[i] = Unit(Mix((uint)i * 0xFD7046C5u + 13u, Tick));   // scaled by the rule's periods when used
    }

    /// <summary>A fresh pack id: the next round-robin one no member holds now.</summary>
    int NewPack()
    {
        for (int tries = 0; tries < MaxPacks; tries++)
        {
            _lastPack = _lastPack % (MaxPacks - 1) + 1;
            if (_packMembers[_lastPack] == 0 && !_openPack.ContainsValue(_lastPack)) break;
        }
        _packMembers[_lastPack] = 0;
        return _lastPack;
    }

    /// <summary>The pack a leash group's Orks join as they go: its open one, else a new one (kept open until <see cref="ClosePack"/>).</summary>
    int PackFor(int group)
    {
        if (!_openPack.TryGetValue(group, out int pack)) _openPack[group] = pack = NewPack();
        return pack;
    }

    void ClosePack(int group) => _openPack.Remove(group);

    /// <summary>Each tick, before the moves: every pack's centre and head count from its members in play (alive, not leashed), in slot order.</summary>
    void TallyPacks()
    {
        Array.Clear(_packSum);
        Array.Clear(_packCount);
        if (Swarm is null) return;
        for (int i = 0; i < Capacity; i++)
        {
            int p = _pack[i];
            if (p == 0 || !_used[i] || _state[i] == MusouEnemyState.Dying || _group[i] != 0) continue;
            if (_packCount[p] == 0) _packNode[p] = _node[i];
            _packSum[p] += _position[i];
            _packCount[p]++;
        }
        for (int p = 1; p < MaxPacks; p++)
        {
            _packMembers[p] = _packCount[p];
            _packLeads[p] = false;
            if (_packCount[p] == 0) continue;
            _packCentre[p] = _packSum[p] / _packCount[p];
            if (Swarm.PackLead > 0f && _packCount[p] >= 2) LeadOf(p, Swarm);
        }
    }

    /// <summary>
    /// Pack <paramref name="p"/>'s lead point: down the flow field from the node at its centre (else a member's), along the field's next nodes for the disc's radius plus the lead, stopping at a drop or a source;
    /// none without a field. Off the graph (the flat test ground): towards the members' nearest target is not known here, so none either.
    /// </summary>
    void LeadOf(int p, MusouSwarmRules swarm)
    {
        if (Walk is null || Flow is not { Ready: true }) return;
        var centre = _packCentre[p];
        int node = Walk.Nearest(centre, 3f, 2.5f);
        if (node < 0) node = _packNode[p];
        if (node < 0 || float.IsPositiveInfinity(Flow.Distance(node))) return;
        float want = MathF.Sqrt(_packCount[p] * swarm.PackAreaPerOrk / MathF.PI) + swarm.PackLead, gone = 0f;
        var at = new Vector2(Walk.X(node), Walk.Y(node));
        var start = new Vector2(centre.X, centre.Y);
        gone = Vector2.Distance(start, at);
        for (int step = 0; step < 96 && gone < want; step++)
        {
            int next = Flow.Next(node);
            if (next < 0 || !Walk.Adjacent(node, next) || MathF.Abs(Walk.Z(next) - Walk.Z(node)) > 1f) break;
            var to = new Vector2(Walk.X(next), Walk.Y(next));
            gone += Vector2.Distance(at, to);
            at = to; node = next;
        }
        if (Vector2.DistanceSquared(at, start) < 1f) return;   // at the field's end: the members' own ways
        _packLead[p] = at;
        _packLeads[p] = true;
    }

    /// <summary>The slow wander of an enemy's heading (radians, deterministic in the tick).</summary>
    float Wander(int i, MusouSwarmRules swarm)
    {
        float period = swarm.WanderPeriodMin + (swarm.WanderPeriodMax - swarm.WanderPeriodMin) * _wanderRate[i];
        float t = Tick / (float)Math.Max(1, TickRate);
        return swarm.WanderDegrees * MathF.PI / 180f * MathF.Sin(_wanderPhase[i] + MathF.Tau * t / MathF.Max(0.5f, period));
    }

    /// <summary>
    /// The way of a pack's member far from its target: to its own spot in the pack's disc round the lead point, its heading wandering, at its speed scaled by how far that spot is (the front waits, the back catches up).
    /// <paramref name="flow"/> (the field's way at its speed, turned by its wander) instead for a member of no pack or a pack with no lead, a straggler beyond <see cref="MusouSwarmRules.PackLooseBeyond"/> of the disc,
    /// one on another level than the centre, and one following the plain flow round a wall.
    /// </summary>
    Vector2 PackWay(int i, Vector2 flow, float speed, MusouSwarmRules swarm)
    {
        float wander = Wander(i, swarm);
        var way = Turned(flow, wander);
        int p = _pack[i];
        if (p == 0 || !_packLeads[p] || _flowTicks[i] != 0) return way;
        var centre = _packCentre[p];
        var here = new Vector2(_position[i].X, _position[i].Y);
        float radius = MathF.Sqrt(_packMembers[p] * swarm.PackAreaPerOrk / MathF.PI);
        if (MathF.Abs(centre.Z - _position[i].Z) > swarm.PackLevel || Vector2.Distance(here, new Vector2(centre.X, centre.Y)) > radius + swarm.PackLooseBeyond) return way;
        var error = _packLead[p] + _packSpot[i] * radius - here;
        float off = error.Length();
        if (off < 1e-3f) return Vector2.Zero;
        float lead = radius + swarm.PackLead;
        return Turned(error / off, wander) * speed * Math.Clamp(off / lead, swarm.PackLeastSpeed, swarm.PackMostSpeed);
    }

    /// <summary>
    /// The way to an enemy's own place round its target (<paramref name="flat"/> from it to the target, <paramref name="distance"/> its length): the place's angle is chosen the first time it comes near this target,
    /// its bearing times <see cref="MusouSwarmRules.SurroundDegrees"/> off the side it came from; while the place is more than <see cref="MusouSwarmRules.OrbitStepDegrees"/> round from where it stands it walks
    /// round the target at its distance, else in to the ring. <paramref name="settled"/>: at the ring and about at its place.
    /// </summary>
    Vector2 SurroundWay(int i, in MusouTarget target, Vector2 flat, float distance, float ring, float speed, MusouSwarmRules swarm, out bool settled)
    {
        var from = -flat / distance;   // from the target to the enemy
        float now = MathF.Atan2(from.Y, from.X);
        if (_slotFor[i] != target.Id)
        {
            _slotFor[i] = target.Id;
            _slotAngle[i] = now + _bearing[i] * swarm.SurroundDegrees;
        }
        float diff = MathF.IEEERemainder(_slotAngle[i] - now, MathF.Tau);
        float step = swarm.OrbitStepDegrees * MathF.PI / 180f;
        bool round = MathF.Abs(diff) > step;
        float at = now + Math.Clamp(diff, -step, step), r = round ? MathF.Max(ring, distance - 1f) : ring;
        settled = !round && distance <= ring + 0.5f && MathF.Abs(diff) < step * 0.5f;
        var place = new Vector2(target.Position.X, target.Position.Y) + new Vector2(MathF.Cos(at), MathF.Sin(at)) * r;
        var way = place - new Vector2(_position[i].X, _position[i].Y);
        float length = way.Length();
        return length > 1e-4f ? way / length * speed : Vector2.Zero;
    }
}
