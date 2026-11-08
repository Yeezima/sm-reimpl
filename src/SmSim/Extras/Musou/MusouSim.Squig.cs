using System.Numerics;

namespace SmSim.Extras.Musou;

/// <summary>
/// The Bomb Squig in the pool (expansion S2; <see cref="MusouSquigRules"/>). It is dropped (it falls in <see cref="MusouEnemyState.Spawning"/>), then runs at the nearest player, AI Marine or Guard
/// squad; within <see cref="MusouSquigRules.FuseRadius"/> it lights its fuse (<see cref="MusouEnemyState.WindUp"/>) and bursts when it runs out. Any damage sets it off at once (a shot, a blast, a
/// blow; not the failsafe's removal). A player running into it kicks it (<see cref="KickSquig"/>, <see cref="MusouEnemyState.Knocked"/> while it flies). Its burst is applied by the World
/// (<c>World.TfwSquigs.cs</c>: <see cref="TakeDetonations"/>, then <see cref="Detonate"/> and <c>World.MusouOrkBlast</c>), so a chain is one Squig a tick.
/// </summary>
public sealed partial class MusouSim
{
    readonly ushort[] _fuse = new ushort[Capacity];        // ticks left on a lit fuse (0: unlit)
    readonly ushort[] _kickTicks = new ushort[Capacity];   // ticks left of a kick's flight
    readonly Vector2[] _kickDir = new Vector2[Capacity];
    readonly int[] _kicker = new int[Capacity];            // the last player who kicked it (0: none) ...
    readonly uint[] _kickTick = new uint[Capacity];        // ... and when
    readonly bool[] _blown = new bool[Capacity];           // its burst is applied (or under way): never queued again

    void SpawnedSquig(int i)
    {
        _fuse[i] = 0; _kickTicks[i] = 0; _kickDir[i] = Vector2.Zero; _kicker[i] = 0; _kickTick[i] = 0; _blown[i] = false;
    }

    /// <summary>How many of <paramref name="type"/> are alive and in play.</summary>
    public int CountAlive(MusouEnemyType type)
    {
        int n = 0;
        for (int i = 0; i < Capacity; i++) if (_used[i] && _type[i] == type && _state[i] != MusouEnemyState.Dying) n++;
        return n;
    }

    /// <summary>Ticks left on a Squig's lit fuse (0: unlit, or not a Squig).</summary>
    public int FuseTicks(ushort id) => id >= 1 && id <= Capacity && _used[id - 1] ? _fuse[id - 1] : 0;

    /// <summary>The player credited with what a Squig's burst kills when nothing else names one: the last kicker within <see cref="MusouSquigRules.KickCreditSeconds"/>, else 0.</summary>
    int KickCredit(int i) => _kicker[i] != 0 && Tick - _kickTick[i] <= Ticks(MusouSquigRules.KickCreditSeconds) ? _kicker[i] : 0;

    /// <summary>A Squig was killed (from <see cref="Damage"/>): its burst is queued for the World, credited to <paramref name="attacker"/>, else its kicker.</summary>
    void SquigDied(int i, int attacker)
    {
        if (_blown[i]) return;
        ushort id = (ushort)(i + 1);
        foreach (var d in _detonations) if (d.Id == id) return;
        _detonations.Add((id, attacker != 0 ? attacker : KickCredit(i)));
    }

    /// <summary>
    /// The World applies a queued burst: the Squig (alive or dying) is marked blown, killed if it still lives (a blast that queued it without killing it), and its burst's centre returned.
    /// False for an unknown slot, another type or one already blown.
    /// </summary>
    public bool Detonate(ushort id, int credit, out Vector3 at)
    {
        at = default;
        int i = id - 1;
        if (i < 0 || i >= Capacity || !_used[i] || _type[i] != MusouEnemyType.BombSquig || _blown[i]) return false;
        _blown[i] = true;
        if (_state[i] != MusouEnemyState.Dying) Damage(id, credit, MathF.Max(1f, _health[i]), Vector3.Zero, out _, marker: 0, cause: MusouDeathKind.OrkBlast);
        _fuse[i] = 0;
        at = _position[i] + new Vector3(0f, 0f, MusouSquigRules.BlastHeight);
        AddEvent(new MusouEvent(MusouEventKind.Telegraph, Tick, id, (ushort)Math.Clamp(credit, 0, ushort.MaxValue), MusouSquigRules.BlastCue, at));
        return true;
    }

    /// <summary>
    /// A player runs into a Squig: it is flung <see cref="MusouSquigRules.KickDistance"/> along <paramref name="direction"/> and an unlit fuse is set to <see cref="MusouSquigRules.KickFuseSeconds"/>;
    /// <paramref name="player"/> is credited for <see cref="MusouSquigRules.KickCreditSeconds"/>. False for an unknown, dying, falling or already flying Squig, or another type.
    /// </summary>
    public bool KickSquig(ushort id, int player, Vector2 direction)
    {
        int i = id - 1;
        if (i < 0 || i >= Capacity || !_used[i] || _type[i] != MusouEnemyType.BombSquig || _blown[i]) return false;
        if (_state[i] is MusouEnemyState.Dying or MusouEnemyState.Spawning || _kickTicks[i] > 0) return false;
        if (!(direction.LengthSquared() > 1e-6f) || !float.IsFinite(direction.X) || !float.IsFinite(direction.Y)) return false;
        _kickDir[i] = Vector2.Normalize(direction);
        _kickTicks[i] = Ticks(MusouSquigRules.KickDistance / MusouSquigRules.KickSpeed);
        _kicker[i] = player; _kickTick[i] = Tick;
        if (_fuse[i] == 0) LightFuse(i, MusouSquigRules.KickFuseSeconds);
        SetState(i, MusouEnemyState.Knocked);
        Face(i, _kickDir[i]);
        AddEvent(new MusouEvent(MusouEventKind.Telegraph, Tick, id, (ushort)Math.Clamp(player, 0, ushort.MaxValue), MusouSquigRules.KickCue, _position[i] + new Vector3(_kickDir[i] * MusouSquigRules.KickDistance, 0f)));   // where it would land (a position: the wire quantises the vector in the box)
        return true;
    }

    void LightFuse(int i, float seconds)
    {
        _fuse[i] = Ticks(seconds);
        AddEvent(new MusouEvent(MusouEventKind.Telegraph, Tick, (ushort)(i + 1), _fuse[i], MusouSquigRules.FuseCue, _position[i]));
    }

    /// <summary>
    /// One tick of a Squig (from <see cref="Step"/>, for every Squig not dying): its fall, its fuse, a kick's flight, else the run at the nearest target and the fuse lit near one.
    /// <paramref name="fight"/> false: no fuse is lit (one already lit still burns).
    /// </summary>
    void StepSquig(int i, ReadOnlySpan<MusouTarget> targets, Span<int> near, bool fight)
    {
        var spec = Rules.Of(_type[i]);
        _headwayTick[i] = Tick; _anchor[i] = _position[i];   // the failsafe leaves it alone: it bursts in the end (LifeSeconds)
        _token[i] = 0; _burst[i] = 0;
        if (_state[i] == MusouEnemyState.Spawning)
        {
            if (_stateTicks[i] >= Ticks(MusouSquigRules.FallSeconds)) SetState(i, MusouEnemyState.Seek);
            return;
        }
        if (_fuse[i] > 0 && --_fuse[i] == 0)
        {
            Damage((ushort)(i + 1), KickCredit(i), MathF.Max(1f, _health[i]), Vector3.Zero, out _, marker: 0, cause: MusouDeathKind.OrkBlast);   // SquigDied queues the burst
            return;
        }
        if (_kickTicks[i] > 0)
        {
            _kickTicks[i]--;
            FlyKicked(i);
            if (_kickTicks[i] == 0) SetState(i, _fuse[i] > 0 ? MusouEnemyState.WindUp : MusouEnemyState.Seek);
            return;
        }
        if (_fuse[i] == 0 && _stateTicks[i] >= Ticks(MusouSquigRules.LifeSeconds) && _state[i] == MusouEnemyState.Seek) { LightFuse(i, MusouSquigRules.FuseSeconds); SetState(i, MusouEnemyState.WindUp); }

        // The nearest living target (players, AI Marines, Guard squads at their men; never a goal).
        int best = -1; float bestScore = float.MaxValue, bestReach = float.MaxValue, bestDz = 0f;
        for (int t = 0; t < targets.Length; t++)
        {
            var target = targets[t];
            if (!target.Alive || target.Goal) continue;
            var d = target.Position - _position[i];
            float reach = MathF.Max(0f, new Vector2(d.X, d.Y).Length() - MathF.Max(0f, target.Radius));
            float score = reach * reach + 9f * d.Z * d.Z;
            if (score < bestScore) { bestScore = score; best = t; bestReach = reach; bestDz = d.Z; }
        }
        _target[i] = best >= 0 ? TargetKey(targets[best].Id) : 0;
        if (best < 0) { Move(i, spec, Vector2.Zero, targets, near); return; }
        if (fight && _fuse[i] == 0 && bestReach <= MusouSquigRules.FuseRadius && MathF.Abs(bestDz) <= MusouSquigRules.FuseVertical)
        {
            LightFuse(i, MusouSquigRules.FuseSeconds);
            SetState(i, MusouEnemyState.WindUp);
        }
        _prefer[i] = true;   // walked to directly within MusouTargetRules.PreferRange (the flow field leads to players alone)
        _aim[i] = -1;
        var way = Approach(i, best, targets[best].Position - _position[i], spec.Speed);
        Move(i, spec, way, targets, near);
    }

    /// <summary>A kick's flight this tick: straight along the kick at <see cref="MusouSquigRules.KickSpeed"/>, on the graph (a wall ends the flight where it is).</summary>
    void FlyKicked(int i)
    {
        var p = _position[i];
        var step = _kickDir[i] * MusouSquigRules.KickSpeed * Dt;
        if (Walk is { } walk && _node[i] >= 0)
        {
            int node = walk.MoveTo(_node[i], p.X + step.X, p.Y + step.Y, -1);
            if (node < 0) { _kickTicks[i] = 0; _velocity[i] = Vector2.Zero; return; }
            _node[i] = node;
            _position[i] = new Vector3(p.X + step.X, p.Y + step.Y, walk.Z(node));
        }
        else _position[i] = new Vector3(p.X + step.X, p.Y + step.Y, p.Z);
        _velocity[i] = step / Dt;
    }
}
