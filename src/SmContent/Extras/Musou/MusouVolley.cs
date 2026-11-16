using System.Numerics;
using SmSim.Extras.Musou;

namespace SmContent.Extras.Musou;

/// <summary>One gun Ork as the client draws it this frame: id, kind, state, feet and facing (radians, the simulation's: forward is (-sin yaw, cos yaw)).</summary>
public readonly record struct MusouVolleyOrk(ushort Id, MusouEnemyType Type, MusouEnemyState State, Vector3 Position, float Yaw);

/// <summary>One cosmetic round due now: the Ork that fires it and where it ends (always beside its target: a miss).</summary>
public readonly record struct MusouVolleyRound(ushort Enemy, Vector3 End);

/// <summary>
/// The client's own volleys (musou-net-budget, the owner 2026-11-09: "maximize client sided rendering/drawing effects to make the battlefield feel real and intense and alive";
/// added content, all values ours and estimates for the refine register). The simulation lets only a few Orks shoot at one target at a time (8 a player, 10 a Guard squad: its shooter
/// tokens); every other gun Ork that the client draws standing in range of a target fires bursts of its own here, drawn and heard like the server's rounds. They are misses only and
/// exist on this client alone: no damage, no bytes, no server time, and two clients see different ones.
/// </summary>
public static class MusouVolleyRules
{
    /// <summary>Kinds that join the volleys (the Big Shoota Nob always fires on the server; the Rokkit Boy's rockets are real projectiles).</summary>
    public static bool Fires(MusouEnemyType type) => type is MusouEnemyType.ShootaBoy or MusouEnemyType.BossShootaBoy;

    /// <summary>Metres from the camera within which an Ork joins: the guns' tracer reach (past that no effect of his round is drawn).</summary>
    public const float CameraMetres = MusouOrkGunRules.TracerMetres;
    /// <summary>The target band (m, flat): a little wider than the Shoota's 12-20 m, so the crowd behind the shooters joins in.</summary>
    public const float MinMetres = 8f, MaxMetres = 30f;
    /// <summary>Most height between the Ork and his target (m).</summary>
    public const float MaxRise = 4f;
    /// <summary>A target must lie within this many degrees of the Ork's facing (he does not turn: the shot must look aimed).</summary>
    public const float ConeDegrees = 50f;
    /// <summary>Most Orks in the volleys at once, the nearest the camera first.</summary>
    public const int MaxShooters = 48;
    /// <summary>Seconds between choosing the shooters again.</summary>
    public const float ChooseEverySeconds = 0.5f;
    /// <summary>An Ork that fired a server round within this many seconds is left to the server.</summary>
    public const float RealQuietSeconds = 2f;
    /// <summary>Each Ork's burst period is the Shoota's period times 1 to this (the crowd's bursts do not line up).</summary>
    public const float PeriodSpread = 1.4f;
    /// <summary>Most rounds a frame (a long frame does not dump a second of fire at once).</summary>
    public const int MaxRoundsPerFrame = 24;
    /// <summary>A miss passes this far beside the target (m), as the simulation's misses do.</summary>
    public const float MissNear = 0.6f, MissFar = 1.4f;
}

/// <summary>
/// Plans the client's volleys frame by frame (engine-free, so the rules are tested): <see cref="Step"/> with the drawn gun Orks, the possible targets (the players and the Guardsmen, at
/// their feet) and the camera; it returns the rounds due this frame. Line of sight is asked of <c>clear</c> once per burst (null: always clear).
/// </summary>
public sealed class MusouVolleyPlanner
{
    sealed class Gunner { public float NextBurst; public int RoundsLeft; public float NextRound; public Vector3 Target; public uint Salt; public bool Chosen; }

    readonly Dictionary<ushort, Gunner> _gunners = new();
    readonly Dictionary<ushort, float> _realAt = new();
    readonly List<(float Distance, int Index)> _order = new();
    readonly List<MusouVolleyRound> _rounds = new();
    readonly List<ushort> _gone = new();
    float _now, _chooseAt;

    /// <summary>Counts since the start: bursts begun, rounds given, bursts not begun for want of a line of sight.</summary>
    public long Bursts { get; private set; }
    public long Rounds { get; private set; }
    public long Blocked { get; private set; }
    /// <summary>Orks in the volleys now.</summary>
    public int Shooters { get; private set; }

    /// <summary>The server's round from <paramref name="enemy"/> was just drawn: he is left to the server a while.</summary>
    public void RealRound(ushort enemy) => _realAt[enemy] = _now;

    public IReadOnlyList<MusouVolleyRound> Step(float delta, IReadOnlyList<MusouVolleyOrk> orks, IReadOnlyList<Vector3> targets, Vector3 camera,
        Func<Vector3, Vector3, bool>? clear = null, MusouRules? rules = null)
    {
        rules ??= MusouRules.Default;
        _now += MathF.Max(0f, delta);
        _rounds.Clear();
        if (_now >= _chooseAt)
        {
            _chooseAt = _now + MusouVolleyRules.ChooseEverySeconds;
            Choose(orks, targets, camera);
        }
        for (int k = 0; k < orks.Count && _rounds.Count < MusouVolleyRules.MaxRoundsPerFrame; k++)
        {
            var o = orks[k];
            if (!_gunners.TryGetValue(o.Id, out var g) || !g.Chosen) continue;
            if (!Ready(o) || Quiet(o.Id)) { g.RoundsLeft = 0; continue; }
            var spec = rules.Of(o.Type);
            if (g.RoundsLeft == 0 && _now >= g.NextBurst)
            {
                var target = Nearest(o, targets);
                if (target is not { } t) continue;
                var muzzle = o.Position + new Vector3(0f, 0f, 1.4f);
                var chest = t + new Vector3(0f, 0f, 1.2f);
                g.NextBurst = _now + spec.RangedPeriod * (1f + (MusouVolleyRules.PeriodSpread - 1f) * Unit(Mix(o.Id, ++g.Salt)));
                if (clear is not null && !clear(muzzle, chest)) { Blocked++; continue; }
                g.Target = t;
                g.RoundsLeft = Math.Clamp(spec.RangedRounds, 1, 8);
                g.NextRound = _now;
                Bursts++;
            }
            while (g.RoundsLeft > 0 && _now >= g.NextRound && _rounds.Count < MusouVolleyRules.MaxRoundsPerFrame)
            {
                _rounds.Add(new MusouVolleyRound(o.Id, Miss(o.Position, g.Target, Mix(o.Id, ++g.Salt))));
                g.RoundsLeft--;
                g.NextRound += MathF.Max(0.03f, spec.RangedRoundInterval);
                Rounds++;
            }
        }
        return _rounds;
    }

    /// <summary>The shooters: gun Orks standing (Seek) near the camera with a target in the band and the cone, not lately firing for the server, the nearest the camera first.</summary>
    void Choose(IReadOnlyList<MusouVolleyOrk> orks, IReadOnlyList<Vector3> targets, Vector3 camera)
    {
        _order.Clear();
        for (int k = 0; k < orks.Count; k++)
        {
            var o = orks[k];
            if (!MusouVolleyRules.Fires(o.Type) || !Ready(o) || Quiet(o.Id)) continue;
            float d = Vector3.Distance(o.Position, camera);
            if (!(d <= MusouVolleyRules.CameraMetres) || Nearest(o, targets) is null) continue;
            _order.Add((d, k));
        }
        _order.Sort((a, b) => a.Distance != b.Distance ? a.Distance.CompareTo(b.Distance) : orks[a.Index].Id.CompareTo(orks[b.Index].Id));
        foreach (var g in _gunners.Values) g.Chosen = false;
        int n = Math.Min(_order.Count, MusouVolleyRules.MaxShooters);
        for (int k = 0; k < n; k++)
        {
            var o = orks[_order[k].Index];
            if (!_gunners.TryGetValue(o.Id, out var g))
                _gunners[o.Id] = g = new Gunner { NextBurst = _now + MusouRules.Default.Of(o.Type).RangedPeriod * Unit(Mix(o.Id, 0x5EEDu)), Salt = o.Id };   // a phase of his own
            g.Chosen = true;
        }
        Shooters = n;
        _gone.Clear();
        foreach (var (id, g) in _gunners) if (!g.Chosen) _gone.Add(id);   // a burst cut short is dropped: kept, it would fire its stale rounds at once if he (or an Ork given his id) were chosen again
        foreach (var id in _gone) _gunners.Remove(id);
        _gone.Clear();
        foreach (var (id, at) in _realAt) if (_now - at > MusouVolleyRules.RealQuietSeconds) _gone.Add(id);
        foreach (var id in _gone) _realAt.Remove(id);
    }

    static bool Ready(in MusouVolleyOrk o) => o.State == MusouEnemyState.Seek;

    bool Quiet(ushort id) => _realAt.TryGetValue(id, out var at) && _now - at <= MusouVolleyRules.RealQuietSeconds;

    /// <summary>The nearest target in the band, the height and the cone; null: none.</summary>
    static Vector3? Nearest(in MusouVolleyOrk o, IReadOnlyList<Vector3> targets)
    {
        var facing = new Vector2(-MathF.Sin(o.Yaw), MathF.Cos(o.Yaw));   // MusouSim.Forward
        float cos = MathF.Cos(MusouVolleyRules.ConeDegrees * MathF.PI / 180f);
        Vector3? best = null;
        float bestD = float.MaxValue;
        for (int k = 0; k < targets.Count; k++)
        {
            var t = targets[k];
            var flat = new Vector2(t.X - o.Position.X, t.Y - o.Position.Y);
            float d = flat.Length();
            if (d < MusouVolleyRules.MinMetres || d > MusouVolleyRules.MaxMetres || MathF.Abs(t.Z - o.Position.Z) > MusouVolleyRules.MaxRise || d >= bestD) continue;
            if (Vector2.Dot(flat / d, facing) < cos) continue;
            best = t; bestD = d;
        }
        return best;
    }

    /// <summary>A miss beside the target's chest: 0.6 to 1.4 m to the left or right, a little high or low (the simulation's miss).</summary>
    static Vector3 Miss(Vector3 from, Vector3 target, uint roll)
    {
        var chest = target + new Vector3(0f, 0f, 1.2f);
        var side = Vector2.Normalize(new Vector2(-(chest.Y - from.Y), chest.X - from.X) + new Vector2(1e-4f, 0f));
        float off = (MusouVolleyRules.MissNear + (MusouVolleyRules.MissFar - MusouVolleyRules.MissNear) * Unit(Mix(roll, 1u))) * ((roll & 1u) == 0u ? 1f : -1f);
        return chest + new Vector3(side * off, (Unit(Mix(roll, 2u)) - 0.5f) * 0.8f);
    }

    static uint Mix(uint a, uint b)
    {
        uint h = a * 0x9E3779B1u ^ (b + 0x7F4A7C15u + (a << 6) + (a >> 2));
        h ^= h >> 16; h *= 0x85EBCA6Bu; h ^= h >> 13; h *= 0xC2B2AE35u; h ^= h >> 16;
        return h;
    }

    static float Unit(uint h) => (h >> 8) / 16777216f;
}
