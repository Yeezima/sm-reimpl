using System.Numerics;

namespace SmSim.Extras.Musou;

/// <summary>
/// Orks holding a place (Musou Mode only, <see cref="MusouSim.Swarm"/> set; the owner, 2026-11-08: "orks pile up all looking at the center of a point when capturing it - they should spread out a bit and look
/// around"; every value ours, spec "Musou Mode: Orks at a point"). An Ork retaking a secured point (a <see cref="MusouTarget.Goal"/> with a <see cref="MusouTarget.Ring"/>) takes its own spot across the capture
/// ring, picked once (by its slot: an even spread over the ring by the R2 sequence, pulled in toward the centre where the spot is off the walk graph), walks straight to it once near and stands there; one that
/// cannot walk straight to it stands where it is if that is inside the ring, else comes down the flow as before. A standing Ork (on its spot, or a leashed one at its post) looks round: its own heading off outward,
/// a slow glance every few seconds, and toward the nearest threat in range. O(1) an Ork (O(targets) for the threat); no path search. Capture is untouched: every spot lies inside the ring.
/// </summary>
public sealed partial class MusouSim
{
    readonly Vector3[] _goalSpot = new Vector3[Capacity];   // its spot at the goal _goalSpotFor
    readonly int[] _goalSpotFor = new int[Capacity];        // the goal that spot is at (0: none picked)
    readonly bool[] _atSpot = new bool[Capacity];           // standing on it
    readonly bool[] _spotBlocked = new bool[Capacity];      // its straight walk there met a wall: it stands where it got to
    readonly bool[] _spotWalk = new bool[Capacity];         // it walked straight for its spot last tick
    readonly bool[] _lookSet = new bool[Capacity];          // its standing heading is chosen (cleared when it walks)
    readonly float[] _lookBase = new float[Capacity];       // that heading (yaw)

    void ForgetHold(int i)
    {
        _goalSpotFor[i] = 0; _atSpot[i] = false; _spotBlocked[i] = false; _spotWalk[i] = false; _lookSet[i] = false;
    }

    /// <summary>Test aid: enemy <paramref name="id"/>'s spot at the goal it is retaking (null: none picked).</summary>
    public Vector3? GoalSpotOf(ushort id) => id >= 1 && id <= Capacity && _used[id - 1] && _goalSpotFor[id - 1] != 0 ? _goalSpot[id - 1] : null;

    /// <summary>
    /// The way of enemy <paramref name="i"/> at a goal with a ring: to its own spot (zero standing on it, looking round); null where the old way stands (no ring, far, another level, or blocked outside the ring).
    /// </summary>
    Vector2? GoalSpotWay(int i, in MusouTarget goal, Vector3 d, float speed, ReadOnlySpan<MusouTarget> targets, MusouSwarmRules swarm)
    {
        if (goal.Ring <= 0f) return null;
        var flat = new Vector2(d.X, d.Y);
        float distance = flat.Length();
        if (_goalSpotFor[i] != goal.Id) PickGoalSpot(i, goal, swarm);
        bool walked = _spotWalk[i];
        _spotWalk[i] = false;
        if (distance > goal.Ring + swarm.GoalSpotNear || MathF.Abs(d.Z) > 1.5f) { _atSpot[i] = false; return null; }
        var outward = distance > 1e-4f ? -flat / distance : Vector2.Zero;
        if (_spotBlocked[i]) { LookAround(i, targets, outward, threats: true, swarm); return Vector2.Zero; }
        var to = new Vector2(_goalSpot[i].X - _position[i].X, _goalSpot[i].Y - _position[i].Y);
        float off = to.Length();
        _atSpot[i] = _atSpot[i] ? off <= swarm.GoalSpotLeave : off <= swarm.GoalSpotSettle;
        if (_atSpot[i]) { LookAround(i, targets, outward, threats: true, swarm); return Vector2.Zero; }
        if (_flowTicks[i] != 0)
        {
            // Its straight walk met a wall (Move sets the flow spell): inside the ring it stands where it got to, outside it follows the flow in as before.
            if (walked && distance < goal.Ring * swarm.GoalSpotOuter) { _spotBlocked[i] = true; _goalSpot[i] = _position[i]; return Vector2.Zero; }
            return null;
        }
        _spotWalk[i] = true;
        return off > 1e-4f ? to / off * speed : Vector2.Zero;
    }

    /// <summary>Enemy <paramref name="i"/>'s spot at <paramref name="goal"/>: an area-even place in the ring by its slot; pulled in toward the centre while it lies off the graph.</summary>
    void PickGoalSpot(int i, in MusouTarget goal, MusouSwarmRules swarm)
    {
        _goalSpotFor[i] = goal.Id; _atSpot[i] = false; _spotBlocked[i] = false;
        // The R2 sequence by slot (a band's slots run on, so its spots fill the ring evenly), turned by the goal's own hash.
        float angle = MathF.Tau * (float)((i * 0.7548776662466927 + Unit(Mix((uint)goal.Id, 0x73706F74u))) % 1.0);
        float inner = swarm.GoalSpotInner, outer = swarm.GoalSpotOuter, u = (float)((0.5 + i * 0.5698402909980532) % 1.0);
        float share = MathF.Sqrt(inner * inner + (outer * outer - inner * inner) * u);
        var dir = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
        var centre = goal.Position;
        for (int attempt = 0; attempt < 4; attempt++, share *= 0.6f)
        {
            var spot = centre + new Vector3(dir * share * goal.Ring, 0f);
            if (Walk is null) { _goalSpot[i] = spot; return; }
            int node = Walk.NodeIn(Walk.ColumnOf(spot.X, spot.Y), spot.Z, 2.5f);   // its own cell walkable (no nearby cell's stand-in: a spot past a ledge stays off)
            if (node >= 0) { _goalSpot[i] = new Vector3(spot.X, spot.Y, Walk.Z(node)); return; }
        }
        _goalSpot[i] = centre;
    }

    /// <summary>
    /// A standing enemy's look (<paramref name="outward"/>: from the place's centre; zero: its facing when it stopped): toward the nearest threat within <see cref="MusouSwarmRules.LookThreatRange"/>
    /// (<paramref name="threats"/>), else its own heading off outward and, every few seconds, a glance off that; turned toward at <see cref="MusouSwarmRules.LookTurnDegrees"/> a second.
    /// </summary>
    void LookAround(int i, ReadOnlySpan<MusouTarget> targets, Vector2 outward, bool threats, MusouSwarmRules swarm)
    {
        if (MusouBossOrks.Is(_type[i]) || _state[i] != MusouEnemyState.Seek) return;
        float rad = MathF.PI / 180f;
        if (!_lookSet[i])
        {
            float own = (2f * Unit(Mix((uint)i * 0x9E3779B1u + 23u, 0x6C6F6F6Bu)) - 1f) * swarm.LookSpreadDegrees * rad;
            _lookBase[i] = (outward != Vector2.Zero ? MathF.Atan2(-outward.X, outward.Y) : _yaw[i]) + own;
            _lookSet[i] = true;
        }
        float want;
        int threat = -1;
        if (threats)
        {
            float best = swarm.LookThreatRange * swarm.LookThreatRange;
            for (int t = 0; t < targets.Length; t++)
            {
                if (!targets[t].Alive || targets[t].Goal) continue;
                var d = targets[t].Position - _position[i];
                if (MathF.Abs(d.Z) > 3f) continue;
                float dd = d.X * d.X + d.Y * d.Y;
                if (dd < best) { best = dd; threat = t; }
            }
        }
        if (threat >= 0) want = MathF.Atan2(-(targets[threat].Position.X - _position[i].X), targets[threat].Position.Y - _position[i].Y);
        else
        {
            float span = swarm.LookPeriodMin + (swarm.LookPeriodMax - swarm.LookPeriodMin) * Unit(Mix((uint)i * 0x27D4EB2Fu + 29u, 0x7065726Fu));
            uint period = Ticks(span), phase = Mix((uint)i, 0x70686173u) % period;
            uint epoch = (Tick + phase) / period;
            want = _lookBase[i] + (2f * Unit(Mix((uint)i * 0x165667B1u + epoch, 0x676C616Eu)) - 1f) * swarm.LookSwingDegrees * rad;
        }
        float diff = MathF.IEEERemainder(want - _yaw[i], MathF.Tau), max = swarm.LookTurnDegrees * rad * Dt;
        _yaw[i] = MathF.IEEERemainder(_yaw[i] + Math.Clamp(diff, -max, max), MathF.Tau);
    }
}
