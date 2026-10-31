using System.Numerics;
using SmSim;
using SmSim.Extras.Musou;

namespace SmServer;

/// <summary>
/// Test aid of Musou MU2 (ours; no gameplay): <c>SM_MUSOU_DUMMIES=N</c> in the server's environment keeps N wandering enemies in a Musou match, so the
/// network and the crowd can be seen and measured without the AI (MU1). Read only in a Musou match (the variable does nothing in any other). The dummies
/// are spawned through <see cref="MusouSim.Spawn"/> (so ids, events and the pool are the real ones) at least 20 m from every player, at most 6 a tick;
/// they walk the nav graph's links (or straight lines on the flat ground) towards a place on a ring of 2.5 to 6 m around the nearest living player, swing
/// there (wind-up, strike, recover), stagger now and then, and die after 15 to 45 s (<see cref="MusouSim.Damage"/>: a death event with a push), their
/// slot freed 3 s later. Everything follows a fixed seed. The pool has no way to move an enemy (MU1 owns the moves), so the dummies' places and states
/// are laid over the pool's copies when sent (<see cref="PoseMusouDummies"/>); the pool's own positions stay where they spawned.
/// </summary>
public sealed partial class GameServer
{
    /// <summary>The environment variable of the test aid (a count of dummies, 0..400).</summary>
    public const string MusouDummiesVariable = "SM_MUSOU_DUMMIES";

    sealed class MusouDummy
    {
        public ushort Id;
        public MusouEnemyType Type;
        public Vector3 Position;
        public float Yaw;
        public int Node = -1, Next = -1;
        public MusouEnemyState State;
        public int StateTicks;
        public uint DieAt, DiedAt;
        public float RingAngle, RingRadius;
        public uint Random;
    }

    readonly List<MusouDummy> _musouDummies = new();
    readonly Dictionary<ushort, MusouDummy> _musouDummyById = new();
    int? _musouDummyTarget;
    uint _musouDummySeed = 0x9E3779B9u;

    /// <summary>Dummies wanted (the variable, read once; 0 without it), dummies spawned and dummies killed since the start (test aid).</summary>
    public int MusouDummiesWanted => _musouDummyTarget ??= ReadDummyTarget();
    public int MusouDummiesSpawned { get; private set; }
    public int MusouDummiesKilled { get; private set; }

    /// <summary>Test aid for tests: sets the dummy count instead of the environment variable (before the first send).</summary>
    public void SetMusouDummies(int count) => _musouDummyTarget = Math.Clamp(count, 0, 400);

    int _musouDummyEvents;

    /// <summary>Test aid for tests: each tick the dummies also report this many hits and tracers (half each) through <see cref="MusouSim.AddEvent"/> (an event storm).</summary>
    public void SetMusouDummyEvents(int perTick) => _musouDummyEvents = Math.Clamp(perTick, 0, MusouSim.MaxEventsPerTick);

    static int ReadDummyTarget() =>
        int.TryParse(Environment.GetEnvironmentVariable(MusouDummiesVariable), out int n) ? Math.Clamp(n, 0, 400) : 0;

    static uint NextRandom(ref uint state)
    {
        state ^= state << 13; state ^= state >> 17; state ^= state << 5;
        return state;
    }

    static float Unit(ref uint state) => (NextRandom(ref state) & 0xFFFFFF) / (float)0x1000000;

    void StepMusouDummies(MusouSim sim)
    {
        if (MusouDummiesWanted == 0) return;
        uint tick = sim.Tick;
        // A cleared pool (a new round, every player gone) took the dummies with it.
        if (_musouDummies.RemoveAll(d => !sim.TryGet(d.Id, out var e) || e.Type != d.Type) > 0)
        {
            _musouDummyById.Clear();
            foreach (var d in _musouDummies) _musouDummyById[d.Id] = d;
        }
        if (World.State != MatchState.Playing) return;
        var players = World.Players.Values.Where(p => p.Alive).Select(p => p.Position).ToList();
        if (players.Count == 0) return;
        float dt = World.Config.TickSeconds;
        var nav = sim.Nav;

        // Bodies go 3 s after the death; the dead are replaced.
        foreach (var d in _musouDummies.Where(d => d.DiedAt != 0 && tick - d.DiedAt >= 3 * World.Config.TickRate).ToList())
        {
            sim.Remove(d.Id);
            _musouDummies.Remove(d);
            _musouDummyById.Remove(d.Id);
        }
        int alive = _musouDummies.Count(d => d.DiedAt == 0);
        for (int released = 0; alive < MusouDummiesWanted && released < 6; released++, alive++)
            if (!SpawnDummy(sim, nav, players, tick)) break;

        foreach (var d in _musouDummies)
        {
            if (d.DiedAt != 0) continue;
            if (tick >= d.DieAt)
            {
                float a = Unit(ref d.Random) * MathF.Tau;
                if (sim.Damage(d.Id, 0, 100_000f, new Vector3(MathF.Cos(a), MathF.Sin(a), 0.3f), out _, cause: MusouDeathKind.Shot))
                {
                    d.DiedAt = tick; d.State = MusouEnemyState.Dying; MusouDummiesKilled++;
                }
                continue;
            }
            StepDummy(d, nav, players, dt);
        }
        for (int k = 0; k < _musouDummyEvents && _musouDummies.Count > 0; k++)
        {
            var d = _musouDummies[(int)(NextRandom(ref _musouDummySeed) % (uint)_musouDummies.Count)];
            sim.AddEvent(new MusouEvent(k % 2 == 0 ? MusouEventKind.Hit : MusouEventKind.Tracer, tick, d.Id, (ushort)(1 + k % 4), 1, d.Position));
        }
    }

    bool SpawnDummy(MusouSim sim, NavGraph? nav, List<Vector3> players, uint tick)
    {
        ref uint r = ref _musouDummySeed;
        float roll = Unit(ref r);
        var type = roll < 0.6f ? MusouEnemyType.Boy : roll < 0.8f ? MusouEnemyType.ShootaBoy : roll < 0.88f ? MusouEnemyType.ArdBoy
                 : roll < 0.93f ? MusouEnemyType.Nob : MusouEnemyType.ChaosMarine;
        Vector3 at = default;
        int node = -1;
        for (int attempt = 0; attempt < 16; attempt++)
        {
            if (nav is { NodeCount: > 0 }) { node = (int)(NextRandom(ref r) % (uint)nav.NodeCount); at = nav.Position(node); }
            else { float a = Unit(ref r) * MathF.Tau, d = 30f + 20f * Unit(ref r); at = players[0] + new Vector3(MathF.Cos(a) * d, MathF.Sin(a) * d, 0f); at.Z = 0f; }
            if (players.All(p => Vector2.Distance(new Vector2(p.X, p.Y), new Vector2(at.X, at.Y)) >= 20f)) break;
        }
        ushort id = sim.Spawn(type, at, 0f, 85f, node);
        if (id == 0) return false;
        var dummy = new MusouDummy
        {
            Id = id, Type = type, Position = at, Node = node, State = MusouEnemyState.Spawning,
            Random = NextRandom(ref r) | 1u, RingAngle = Unit(ref r) * MathF.Tau, RingRadius = 2.5f + 3.5f * Unit(ref r),
        };
        dummy.DieAt = tick + (uint)((15f + 30f * Unit(ref r)) * World.Config.TickRate);
        _musouDummies.Add(dummy);
        _musouDummyById[id] = dummy;
        MusouDummiesSpawned++;
        return true;
    }

    void StepDummy(MusouDummy d, NavGraph? nav, List<Vector3> players, float dt)
    {
        int rate = World.Config.TickRate;
        d.StateTicks++;
        var nearest = players[0];
        foreach (var p in players) if (Vector3.DistanceSquared(p, d.Position) < Vector3.DistanceSquared(nearest, d.Position)) nearest = p;
        var goal = nearest + new Vector3(MathF.Cos(d.RingAngle), MathF.Sin(d.RingAngle), 0f) * d.RingRadius;
        var toPlayer = new Vector2(nearest.X - d.Position.X, nearest.Y - d.Position.Y);
        float toGoal = Vector2.Distance(new Vector2(goal.X, goal.Y), new Vector2(d.Position.X, d.Position.Y));

        switch (d.State)
        {
            case MusouEnemyState.Spawning:
                if (d.StateTicks >= rate / 2) Enter(d, MusouEnemyState.Seek);
                return;
            case MusouEnemyState.WindUp:
                if (d.StateTicks >= rate / 2) Enter(d, MusouEnemyState.Strike);
                Face(d, toPlayer);
                return;
            case MusouEnemyState.Strike:
                if (d.StateTicks >= rate / 5) Enter(d, MusouEnemyState.Recover);
                return;
            case MusouEnemyState.Recover:
                if (d.StateTicks >= (int)(rate * 0.4f)) Enter(d, MusouEnemyState.Seek);
                return;
            case MusouEnemyState.Stagger:
                if (d.StateTicks >= rate) Enter(d, MusouEnemyState.Seek);
                return;
        }
        // Seek: a stagger now and then (about once in 20 s), a swing once at the ring.
        if (NextRandom(ref d.Random) % (uint)(20 * rate) == 0) { Enter(d, MusouEnemyState.Stagger); return; }
        if (toGoal < 0.6f)
        {
            Face(d, toPlayer);
            if (NextRandom(ref d.Random) % (uint)rate == 0) Enter(d, MusouEnemyState.WindUp);
            return;
        }
        float speed = (d.Type switch { MusouEnemyType.Nob or MusouEnemyType.ArdBoy => 3.2f, MusouEnemyType.ChaosMarine => 3.6f, _ => 4.2f }) * dt;
        Vector3 target = goal;
        if (nav is { NodeCount: > 0 } && d.Node >= 0 && toGoal > 3f)
        {
            if (d.Next < 0)
            {
                // Mostly the link that gets closest to the goal, sometimes any (a wander); a dead end keeps the node.
                int best = -1; float bestDistance = float.MaxValue; int count = 0, pick = -1;
                uint any = NextRandom(ref d.Random);
                foreach (var (to, _, kind) in nav.Links(d.Node))
                {
                    if (kind != NavLinkKind.Walk) continue;
                    float dist = Vector3.DistanceSquared(nav.Position(to), goal);
                    if (dist < bestDistance) { bestDistance = dist; best = to; }
                    if (any % (uint)(++count) == 0) pick = to;
                }
                d.Next = (NextRandom(ref d.Random) & 3) == 0 && pick >= 0 ? pick : best;
                if (d.Next < 0) target = d.Position;
            }
            if (d.Next >= 0) target = nav.Position(d.Next);
        }
        var step = new Vector2(target.X - d.Position.X, target.Y - d.Position.Y);
        float length = step.Length();
        if (length > 1e-4f)
        {
            float move = MathF.Min(speed, length);
            var dir = step / length;
            d.Position += new Vector3(dir * move, 0f);
            float rise = target.Z - d.Position.Z;   // the floor of the node it walks to (on the flat ground: the player's)
            d.Position.Z += MathF.Sign(rise) * MathF.Min(MathF.Abs(rise), MathF.Max(speed, 0.05f));
            Face(d, dir);
        }
        if (d.Next >= 0 && length <= speed + 1e-3f) { d.Node = d.Next; d.Next = -1; }
    }

    static void Enter(MusouDummy d, MusouEnemyState state) { d.State = state; d.StateTicks = 0; }

    static void Face(MusouDummy d, Vector2 direction)
    {
        if (direction.LengthSquared() > 1e-6f) d.Yaw = MathF.Atan2(-direction.X, direction.Y);   // the game's yaw: 0 faces +Y, forward (-sin, cos)
    }

    /// <summary>The dummies' own places, facings and states laid over the pool's copies (the dying keep the pool's state).</summary>
    void PoseMusouDummies(Span<MusouEnemy> enemies)
    {
        if (_musouDummyById.Count == 0) return;
        for (int i = 0; i < enemies.Length; i++)
        {
            if (!_musouDummyById.TryGetValue(enemies[i].Id, out var d)) continue;
            var e = enemies[i];
            enemies[i] = e with { Position = d.Position, Yaw = d.Yaw, State = e.State == MusouEnemyState.Dying ? e.State : d.State };
        }
    }
}
