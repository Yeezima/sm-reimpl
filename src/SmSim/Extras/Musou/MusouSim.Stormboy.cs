using System.Numerics;

namespace SmSim.Extras.Musou;

/// <summary>
/// The Stormboy's own rules in the pool (expansion S3, docs/musou-expansion.md "Stormboys"; Musou Mode only; every value ours or an estimate, owner brief 2026-11-07, spec "Musou expansion: Stormboys").
/// Distances in metres, times in seconds. Hashed into the config only through <see cref="MusouSim.Version"/>. The squads' orders are the battle's (<c>Campaign/TfwBattle.Stormboys.cs</c>).
/// </summary>
public static class MusouStormboy
{
    /// <summary>The longest hop (owner brief: 25 m arcs) and the shortest worth a hop: nearer than this he walks.</summary>
    public const float HopDistance = 25f, HopMin = 8f;
    /// <summary>A hop's flight time and the arc's height above the straight line from take-off to landing (ours: about 19 m/s across, a 5 m arc).</summary>
    public const float FlightSeconds = 1.3f, ApexHeight = 5f;
    /// <summary>A normal landing shoves aside only the Orks it actually touches: those within this of the landing spot (owner brief: 1.5 m), this far (ours), with no damage.</summary>
    public const float ShoveRadius = 1.5f, ShoveDistance = 1.2f;
    /// <summary>A Stormboy under his squad's orders fights a target that comes within this (ours), and goes back to his orders once none is within <see cref="DisengageRadius"/>.</summary>
    public const float EngageRadius = 15f, DisengageRadius = 22f;
    /// <summary>A Stormboy shot down falls under this gravity (m/s², the core's 9.81) ...</summary>
    public const float Gravity = 9.81f;
    /// <summary>... and his crash is the original frag blast (owner brief and plan conflict 6: <c>sm_mp_eq_frag_grenade</c>, 200 damage, radius 10, the data's fall-off from spec "Frag grenade";
    /// Musou's own x2 radius is not used), as an Ork blast: it hurts and scatters Orks too, credited to whoever shot him down.</summary>
    public static readonly OrkBlastSpec CrashBlast = new(10f, 200f)
    {
        FallOff = [new(0f, 1f), new(2.5f, 1f), new(2.6f, 0.5f), new(5f, 0.5f), new(5.1f, 0.3f), new(7f, 0.3f), new(10f, 0.05f)],
    };
    /// <summary>The crash scatters the fodder that lives through it: thrown down up to this far at the centre, by the blast's intensity (ours).</summary>
    public const float CrashScatter = 4f;

    /// <summary>The Stormboy's cues (<see cref="MusouEventKind.Telegraph"/>'s value; above the Weirdboy's 16 to 21, room left for S2): a hop's take-off (where he lands in the vector), a landing
    /// (where, in the vector), a crash (the impact point in the vector, his killer in the event's player).</summary>
    public const byte HopCue = 32, LandCue = 33, CrashCue = 34;

    /// <summary>
    /// The hop cue's player field (2026-11-08, crowd-smooth; was 0): the hop's flight in ticks, so a client draws the exact arc between and past the sparse records of a far Stormboy (with the cue's
    /// landing and start tick). <see cref="HopTicksOf"/> reads it back; 0 (an older server) reads as the full hop's flight.
    /// </summary>
    public static ushort HopEventPlayer(int flightTicks) => (ushort)Math.Clamp(flightTicks, 1, ushort.MaxValue);

    /// <summary>The flight ticks a hop cue carries (<see cref="HopEventPlayer"/>); 0: the full hop's <see cref="FlightSeconds"/> at <paramref name="tickRate"/>.</summary>
    public static int HopTicksOf(ushort player, int tickRate) => player > 0 ? player : Math.Max(1, (int)MathF.Round(FlightSeconds * Math.Max(1, tickRate)));
}

/// <summary>A shot-down Stormboy's impact (<see cref="MusouSim.TakeCrashes"/>): the corpse, where it struck the ground, and who shot him down (0: none).</summary>
public readonly record struct MusouCrash(ushort Enemy, Vector3 Point, int Credit);

/// <summary>
/// Stormboys in the pool (expansion S3). A Stormboy is an ordinary melee Ork with three additions:
/// <list type="bullet">
/// <item>The hop (<see cref="Hop"/>): <see cref="MusouEnemyState.Airborne"/> for <see cref="MusouStormboy.FlightSeconds"/>, his feet along a parabola from the take-off to a landing on the
/// graph; nothing he does in the air. A normal landing shoves aside the Orks within <see cref="MusouStormboy.ShoveRadius"/>, no damage.</item>
/// <item>Orders (<see cref="Order"/>): under his squad he walks to the place it gives him, unless a target comes within <see cref="MusouStormboy.EngageRadius"/>: then he fights as any Ork
/// until none is within <see cref="MusouStormboy.DisengageRadius"/>.</item>
/// <item>Shot down (killed in the air): his body falls under gravity from where it was, and on the ground it crashes (<see cref="TakeCrashes"/>; the World sets off the Ork blast).</item>
/// </list>
/// </summary>
public sealed partial class MusouSim
{
    readonly Vector3[] _hopFrom = new Vector3[Capacity];
    readonly Vector3[] _hopTo = new Vector3[Capacity];
    readonly int[] _hopNode = new int[Capacity];
    readonly uint[] _hopStart = new uint[Capacity];
    readonly ushort[] _hopTicks = new ushort[Capacity];
    readonly bool[] _falling = new bool[Capacity];
    readonly Vector3[] _fallVelocity = new Vector3[Capacity];
    readonly float[] _fallGround = new float[Capacity];
    readonly int[] _crashCredit = new int[Capacity];
    readonly bool[] _ordered = new bool[Capacity];
    readonly bool[] _engaged = new bool[Capacity];
    readonly Vector3[] _orderAt = new Vector3[Capacity];
    readonly List<MusouCrash> _crashes = new();
    readonly ushort[] _shoveNear = new ushort[32];

    /// <summary>Normal landings so far, Orks shoved by them, and crashes (for the report and tests).</summary>
    public int Landings { get; private set; }
    public int Shoved { get; private set; }
    public int Crashes { get; private set; }

    void SpawnedStormboy(int i)
    {
        _hopTicks[i] = 0; _hopNode[i] = -1; _falling[i] = false; _crashCredit[i] = 0;
        _ordered[i] = false; _engaged[i] = false;
    }

    /// <summary>Whether the enemy is in a hop now.</summary>
    public bool IsAirborne(ushort id) => id >= 1 && id <= Capacity && _used[id - 1] && _state[id - 1] == MusouEnemyState.Airborne;

    /// <summary>Whether a shot-down Stormboy's body is still falling.</summary>
    public bool IsFalling(ushort id) => id >= 1 && id <= Capacity && _used[id - 1] && _falling[id - 1];

    /// <summary>Where a Stormboy in a hop lands, and on which node (null: not in a hop).</summary>
    public (Vector3 At, int Node)? HopLanding(ushort id) => IsAirborne(id) ? (_hopTo[id - 1], _hopNode[id - 1]) : null;

    /// <summary>Whether a Stormboy under orders is fighting a target near him this tick (he does not hop then).</summary>
    public bool Engaged(ushort id) => id >= 1 && id <= Capacity && _used[id - 1] && _engaged[id - 1];

    /// <summary>
    /// A Stormboy alive on the ground (seeking, not mid-blow) hops to <paramref name="landing"/> on <paramref name="node"/> (-1: found from it): <see cref="MusouEnemyState.Airborne"/> for
    /// <see cref="MusouStormboy.FlightSeconds"/> (scaled down for a short hop, at least half), his token given back, a <see cref="MusouStormboy.HopCue"/>. False, and nothing changed, for any other
    /// kind or state, a landing farther than <see cref="MusouStormboy.HopDistance"/> (plus a metre) or not finite, or one off the graph when there is one.
    /// </summary>
    public bool Hop(ushort id, Vector3 landing, int node = -1)
    {
        int i = id - 1;
        if (i < 0 || i >= Capacity || !_used[i] || _type[i] != MusouEnemyType.Stormboy || _state[i] != MusouEnemyState.Seek || _falling[i]) return false;
        if (!float.IsFinite(landing.X) || !float.IsFinite(landing.Y) || !float.IsFinite(landing.Z)) return false;
        var from = _position[i];
        float across = new Vector2(landing.X - from.X, landing.Y - from.Y).Length();
        if (across > MusouStormboy.HopDistance + 1f) return false;
        if (Walk is not null)
        {
            if (node < 0) node = Walk.Nearest(landing, 1f, 2.5f);
            if (node < 0) return false;
            landing = Walk.Position(node);
        }
        _hopFrom[i] = from; _hopTo[i] = landing; _hopNode[i] = node; _hopStart[i] = Tick;
        float share = Math.Clamp(across / MusouStormboy.HopDistance, 0.5f, 1f);
        _hopTicks[i] = Ticks(MusouStormboy.FlightSeconds * share);
        _token[i] = 0; _burst[i] = 0; _transit[i] = -1; _aim[i] = -1; _drive[i] = Vector2.Zero; _driveTicks[i] = 0;
        Face(i, new Vector2(landing.X - from.X, landing.Y - from.Y));
        SetState(i, MusouEnemyState.Airborne);
        AddEvent(new MusouEvent(MusouEventKind.Telegraph, Tick, id, MusouStormboy.HopEventPlayer(_hopTicks[i]), MusouStormboy.HopCue, landing));   // crowd-smooth: the flight in ticks in the player field
        return true;
    }

    /// <summary>Puts a Stormboy under his squad's orders: walk to <paramref name="at"/> unless a target is near (<see cref="MusouStormboy.EngageRadius"/>). False for any other kind or a dead one.</summary>
    public bool Order(ushort id, Vector3 at)
    {
        int i = id - 1;
        if (i < 0 || i >= Capacity || !_used[i] || _type[i] != MusouEnemyType.Stormboy || _state[i] == MusouEnemyState.Dying || !float.IsFinite(at.X) || !float.IsFinite(at.Y) || !float.IsFinite(at.Z)) return false;
        if (!_ordered[i] || Vector2.Distance(new(_orderAt[i].X, _orderAt[i].Y), new(at.X, at.Y)) > MusouMoveRules.OrderLeave) _orderSettled[i] = false;   // a new place: he goes again (MusouSim.Movement.cs)
        _ordered[i] = true; _orderAt[i] = at;
        return true;
    }

    /// <summary>Where a Stormboy's orders send him (null: none).</summary>
    public Vector3? OrderOf(ushort id) => id >= 1 && id <= Capacity && _used[id - 1] && _ordered[id - 1] ? _orderAt[id - 1] : null;

    /// <summary>The arc at <paramref name="t"/> (0 to 1) of slot <paramref name="i"/>'s hop: the straight line plus a parabola of <see cref="MusouStormboy.ApexHeight"/>.</summary>
    Vector3 ArcAt(int i, float t) => Vector3.Lerp(_hopFrom[i], _hopTo[i], t) + new Vector3(0f, 0f, 4f * MusouStormboy.ApexHeight * t * (1f - t));

    /// <summary>One tick of a hop: along the arc; at its end he stands on the landing node, seeking, and shoves aside the Orks he lands on.</summary>
    void StepAirborne(int i)
    {
        _headwayTick[i] = Tick; _anchor[i] = _position[i];
        float t = Math.Clamp((Tick - _hopStart[i]) / (float)Math.Max(1, (int)_hopTicks[i]), 0f, 1f);
        var was = _position[i];
        if (t < 1f)
        {
            _position[i] = ArcAt(i, t);
            _velocity[i] = new Vector2(_position[i].X - was.X, _position[i].Y - was.Y) / Dt;
            return;
        }
        _position[i] = _hopTo[i]; _node[i] = _hopNode[i]; _velocity[i] = Vector2.Zero;
        _anchor[i] = _position[i]; _best[i] = float.PositiveInfinity;
        SetState(i, MusouEnemyState.Seek);
        Landings++;
        AddEvent(new MusouEvent(MusouEventKind.Telegraph, Tick, (ushort)(i + 1), 0, MusouStormboy.LandCue, _position[i]));
        ShoveOnLanding(i);
    }

    /// <summary>A normal landing: every other Ork alive on the ground within <see cref="MusouStormboy.ShoveRadius"/> is pushed <see cref="MusouStormboy.ShoveDistance"/> straight away, no damage.</summary>
    void ShoveOnLanding(int i)
    {
        int n = Query(_position[i], MusouStormboy.ShoveRadius, _shoveNear, verticalReach: 1.5f);
        for (int k = 0; k < n; k++)
        {
            int j = _shoveNear[k] - 1;
            if (j == i || !Ork(_type[j]) || _state[j] is MusouEnemyState.Airborne or MusouEnemyState.Spawning || _type[j] == MusouEnemyType.Warboss || MusouBossOrks.Is(_type[j])) continue;
            var away = new Vector2(_position[j].X - _position[i].X, _position[j].Y - _position[i].Y);
            away = away.LengthSquared() > 1e-6f ? Vector2.Normalize(away) : Forward(_yaw[i] + (j - i) * 2.399963f);
            Knock(j, away, MusouStormboy.ShoveDistance);
            Shoved++;
        }
    }

    /// <summary>From <see cref="Damage"/>: a Stormboy killed in a hop falls from where he is, with the arc's velocity, to the ground under the arc (his crash credited to <paramref name="attacker"/>).</summary>
    void BeginFall(int i, int attacker)
    {
        float span = Math.Max(1, (int)_hopTicks[i]);
        float t = Math.Clamp((Tick - _hopStart[i]) / span, 0f, 1f);
        var ahead = ArcAt(i, Math.Min(1f, t + 1f / span));
        _fallVelocity[i] = (ahead - ArcAt(i, t)) / Dt;
        _fallGround[i] = _hopFrom[i].Z + (_hopTo[i].Z - _hopFrom[i].Z) * t;
        if (Walk is not null && Walk.Nearest(_position[i] with { Z = _fallGround[i] }, 3f, 6f) is var below and >= 0) _fallGround[i] = Walk.Z(below);
        _falling[i] = true;
        _crashCredit[i] = attacker;
    }

    /// <summary>One tick of a shot-down Stormboy's fall: gravity until his feet reach the ground, then the crash (his body's dying time starts then).</summary>
    void StepFall(int i)
    {
        _stateTicks[i] = 0;
        _fallVelocity[i] -= new Vector3(0f, 0f, MusouStormboy.Gravity * Dt);
        var p = _position[i] + _fallVelocity[i] * Dt;
        if (p.Z > _fallGround[i]) { _position[i] = p; return; }
        _position[i] = p with { Z = _fallGround[i] };
        _falling[i] = false;
        Crashes++;
        _crashes.Add(new MusouCrash((ushort)(i + 1), _position[i], _crashCredit[i]));
        AddEvent(new MusouEvent(MusouEventKind.Telegraph, Tick, (ushort)(i + 1), (ushort)Math.Clamp(_crashCredit[i], 0, ushort.MaxValue), MusouStormboy.CrashCue, _position[i]));
    }

    /// <summary>The crashes since the last call (in the order they struck), moved into <paramref name="into"/>; the number moved. The World sets off each as an Ork blast.</summary>
    public int TakeCrashes(List<MusouCrash> into)
    {
        into.AddRange(_crashes);
        int n = _crashes.Count;
        _crashes.Clear();
        return n;
    }

    /// <summary>
    /// From <see cref="Step"/>: a Stormboy under orders with no target within <see cref="MusouStormboy.EngageRadius"/> (<see cref="MusouStormboy.DisengageRadius"/> once engaged) walks to his
    /// place and does nothing else (true: his tick is done). False when he is engaged, mid-blow or not under orders: he goes on as any Ork.
    /// </summary>
    bool StepOrdered(int i, ReadOnlySpan<MusouTarget> targets, Span<int> near, MusouTypeSpec spec)
    {
        if (!_ordered[i]) return false;
        float r = _engaged[i] ? MusouStormboy.DisengageRadius : MusouStormboy.EngageRadius, r2 = r * r;
        bool close = false;
        var p = _position[i];
        foreach (var t in targets)
        {
            if (!t.Alive || t.Goal) continue;
            var d = t.Position - p;
            float reach = MathF.Max(0f, MathF.Sqrt(d.X * d.X + d.Y * d.Y + 9f * d.Z * d.Z) - t.Radius);
            if (reach * reach <= r2) { close = true; break; }
        }
        _engaged[i] = close;
        if (close || _state[i] != MusouEnemyState.Seek) return false;
        _target[i] = 0; _token[i] = 0; _burst[i] = 0; _aim[i] = -1;
        var way = new Vector2(_orderAt[i].X - p.X, _orderAt[i].Y - p.Y);
        Move(i, spec, OrderedWay(i, way, spec.Speed), targets, near);   // MusouSim.Movement.cs: he stands at his place until pushed well off it
        _headwayTick[i] = Tick; _anchor[i] = _position[i];
        return true;
    }
}
