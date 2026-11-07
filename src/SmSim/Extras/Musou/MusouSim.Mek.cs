using System.Numerics;

namespace SmSim.Extras.Musou;

/// <summary>
/// The Scrap Mek in the pool (expansion S4; <see cref="MusouMekRules"/>; the battle's <see cref="MusouMeks"/> gives his orders). He never attacks and takes no token. Each step:
/// <list type="bullet">
/// <item>a player (id above 0, an AI Marine too) within <see cref="MusouMekRules.FleeRadius"/>: he runs, up the flow field (away from every target) where it leads away from that player, else straight away;</item>
/// <item>else at work (<see cref="MekWork"/>): he stands at his build, <see cref="MusouEnemyState.Building"/>;</item>
/// <item>else he walks to his order (<see cref="MekOrder"/>), or stands.</item>
/// </list>
/// The stranded failsafe leaves him alone (he hides on purpose).
/// </summary>
public sealed partial class MusouSim
{
    readonly Vector3[] _mekGoal = new Vector3[Capacity];
    readonly Vector3[] _mekWorkAt = new Vector3[Capacity];
    readonly bool[] _mekHasGoal = new bool[Capacity];
    readonly bool[] _mekWork = new bool[Capacity];
    readonly bool[] _mekFleeing = new bool[Capacity];

    /// <summary>Capture aid (ours; the server's <c>SM_MUSOU_S4_DEMO</c> only): the Meks take no damage and never run, so a capture shows every build to its end. Off in every match.</summary>
    public bool MekDemo { get; set; }

    void SpawnedMek(int i) { _mekHasGoal[i] = false; _mekWork[i] = false; _mekFleeing[i] = false; }

    bool IsMek(ushort id) => id >= 1 && id <= Capacity && _used[id - 1] && _type[id - 1] == MusouEnemyType.ScrapMek && _state[id - 1] != MusouEnemyState.Dying;

    /// <summary>Sends a Mek to <paramref name="at"/> (null: nowhere). False for anything but a living Mek or a point that is not finite.</summary>
    public bool MekOrder(ushort id, Vector3? at)
    {
        if (!IsMek(id)) return false;
        if (at is { } p && !(float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z))) return false;
        _mekHasGoal[id - 1] = at.HasValue;
        if (at is { } goal) _mekGoal[id - 1] = goal;
        return true;
    }

    /// <summary>Puts a Mek to work at a build (facing <paramref name="at"/>), or takes him off it. False for anything but a living Mek.</summary>
    public bool MekWork(ushort id, bool working, Vector3? at = null)
    {
        if (!IsMek(id)) return false;
        _mekWork[id - 1] = working;
        if (at is { } p) _mekWorkAt[id - 1] = p;
        return true;
    }

    /// <summary>Whether a Mek ran from a player on his last step.</summary>
    public bool MekFleeing(ushort id) => IsMek(id) && _mekFleeing[id - 1];

    /// <summary>Where a Mek's order sends him (null: none).</summary>
    public Vector3? MekGoal(ushort id) => IsMek(id) && _mekHasGoal[id - 1] ? _mekGoal[id - 1] : null;

    /// <summary>One step of a Mek in play (from <see cref="Step"/>, after his spawning time).</summary>
    void StepMek(int i, ReadOnlySpan<MusouTarget> targets, Span<int> near)
    {
        var spec = Rules.Of(_type[i]);
        _target[i] = 0; _token[i] = 0; _burst[i] = 0; _aim[i] = -1;
        _headwayTick[i] = Tick; _anchor[i] = _position[i];   // he hides on purpose: never stranded
        if (_state[i] == MusouEnemyState.Stagger && _stateTicks[i] < Ticks(Rules.Combat.StaggerSeconds)) { Move(i, spec, Vector2.Zero, targets, near); return; }
        var p = _position[i];
        float nearest = MusouMekRules.FleeRadius;
        var away = Vector2.Zero;
        foreach (var t in targets)
        {
            if (!t.Alive || t.Goal || t.Id <= 0) continue;
            var d = p - t.Position;
            float reach = MathF.Sqrt(d.X * d.X + d.Y * d.Y + 9f * d.Z * d.Z);
            if (reach >= nearest) continue;
            nearest = reach;
            var flat = new Vector2(d.X, d.Y);
            away = flat.LengthSquared() > 1e-6f ? Vector2.Normalize(flat) : Forward(_yaw[i] + MathF.PI);
        }
        _mekFleeing[i] = away != Vector2.Zero && !MekDemo;
        if (_mekFleeing[i])
        {
            _mekWork[i] = false;
            if (_state[i] != MusouEnemyState.Seek) SetState(i, MusouEnemyState.Seek);
            Move(i, spec, FleeWay(i, away) * spec.Speed * MusouMekRules.FleeSpeedScale, targets, near);
            return;
        }
        if (_mekWork[i])
        {
            if (_state[i] != MusouEnemyState.Building) SetState(i, MusouEnemyState.Building);
            Face(i, new Vector2(_mekWorkAt[i].X - p.X, _mekWorkAt[i].Y - p.Y));
            _velocity[i] = Vector2.Zero;
            return;
        }
        if (_state[i] != MusouEnemyState.Seek) SetState(i, MusouEnemyState.Seek);
        var desired = Vector2.Zero;
        if (_mekHasGoal[i])
        {
            var way = new Vector2(_mekGoal[i].X - p.X, _mekGoal[i].Y - p.Y);
            float distance = way.Length();
            if (distance > 0.8f) desired = way / distance * spec.Speed;
        }
        Move(i, spec, desired, targets, near);
    }

    /// <summary>A fleeing Mek's way: to the neighbouring node farthest from every target by the flow field, when that leads away from the player (within 100 degrees), else straight away.</summary>
    Vector2 FleeWay(int i, Vector2 away)
    {
        if (Walk is not { } walk || Flow is not { Ready: true } flow || _node[i] < 0) return away;
        int node = _node[i];
        float here = flow.Distance(node), best = here;
        var pick = Vector2.Zero;
        for (int l = walk.ForwardStart(node); l < walk.ForwardEnd(node); l++)
        {
            int to = walk.ForwardTo(l);
            float d = flow.Distance(to);
            if (float.IsPositiveInfinity(d) || d <= best) continue;
            var way = new Vector2(walk.X(to) - _position[i].X, walk.Y(to) - _position[i].Y);
            if (way.LengthSquared() < 1e-6f) continue;
            way = Vector2.Normalize(way);
            if (Vector2.Dot(way, away) < -0.17f) continue;
            best = d; pick = way;
        }
        return pick != Vector2.Zero ? pick : away;
    }

    /// <summary>
    /// A Rokkit turret's rocket (expansion S4): the Rokkit Boy's projectile (<c>MusouSim.Rokkit.cs</c>: same speed, collisions, warning on the client and Ork blast), from <paramref name="muzzle"/> at
    /// <paramref name="aim"/>, its shooter the turret's Mek (so it never strikes him); a <see cref="MusouMekRules.TurretFireCue"/> (the serial in the event's enemy, the turret in its player, the aim in its
    /// vector), for the client to draw it from the turret. Its serial (0: none, a Mek not in the pool or no direction).
    /// </summary>
    public ushort LaunchTurretRocket(ushort mek, byte turret, Vector3 muzzle, Vector3 aim)
    {
        if (mek < 1 || mek > Capacity || !_used[mek - 1]) return 0;
        var way = aim - muzzle;
        float length = way.Length();
        if (!(length > 1e-3f) || !float.IsFinite(length)) return 0;
        if (++_rocketSerial == 0) _rocketSerial = 1;
        _rockets.Add(new MusouRocket(_rocketSerial, mek, muzzle, way / length * MusouRokkitRules.RocketSpeed, Tick));
        AddEvent(new MusouEvent(MusouEventKind.Telegraph, Tick, _rocketSerial, turret, MusouMekRules.TurretFireCue, aim));
        return _rocketSerial;
    }
}
