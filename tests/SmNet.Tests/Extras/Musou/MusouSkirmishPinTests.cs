using System.Numerics;
using System.Security.Cryptography;
using SmSim;
using SmSim.Extras.Musou;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Musou;

/// <summary>
/// The Skirmish pin (Three-Front War milestone TF0, design section A): the 5-wave Musou run stays reachable (<c>--content musou --map mp_tdm_09</c>, a test aid since the
/// Three-Front War took over the mode) and must not change while the war is built beside it. The golden was captured on main 2ed1501e BEFORE any TF0 hook landed: a
/// scripted 600-tick World replay with two players on a synthetic floor with a walled gap, as SHA-256 over every tick's players, match, director, enemies and events.
/// The fixture lives here (do not change it: the golden depends on every value). A deliberate change of the Skirmish rules is the only reason to recapture it.
/// </summary>
public class MusouSkirmishPinTests(ITestOutputHelper output)
{
    // ---------------------------------------------------------------- the fixture (do not change: pinned)

    static readonly WeaponSpec Rifle = new("pin_rifle", WeaponSlot.Ranged, TriggerKind.FullAuto, 9f, 0.12f, 30, 270, 1.2f, 250f, 45f, 70f, 0.8f, 2.5f, 1f, 0.5f, 0.5f);
    static readonly WeaponSpec Pistol = new("pin_pistol", WeaponSlot.Pistol, TriggerKind.SemiAuto, 45f, 0.2f, 12, 240, 0.6f, 120f, 30f, 60f, 0.5f, 3f, 1f, 0.6f, 0.6f);
    static readonly MeleeStep Light = new(1f, 0.2f, 0.4f, 0.6f, 0.5f, 0.7f, 25f, 10f, 3.5f);

    static ClassLoadout Class(string name, Team side) =>
        new(name, 0, 1, [Light, Light, Light]) { PrimaryChoices = [0], PistolChoices = [1], Side = side, Role = "tactical" };

    static readonly SimConfig Config = new()
    {
        Weapons = [Rifle, Pistol],
        Classes = [Class("sm - tactical", Team.SpaceMarines), Class("csm - tactical", Team.Chaos)],
        RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f,
        HealthMax = 150f, ShieldMax = 50f, RespawnTime = 2f, Extras = ContentExtras.Musou,
    };

    static readonly StartArea[] Rooms =
        [new(Team.SpaceMarines, new Vector3(10f, 30f, 0f), new Vector3(6f, 6f, 3f), 0f), new(Team.Chaos, new Vector3(50f, 30f, 0f), new Vector3(6f, 6f, 3f), 0f)];
    static readonly SpawnPoint[] Markers =
        [new(new(10f, 30f, 0f), 0f), new(new(55f, 5f, 0f), 0f), new(new(55f, 55f, 0f), 0f), new(new(30f, 5f, 0f), 0f), new(new(30f, 55f, 0f), 0f)];

    // ---------------------------------------------------------------- the golden (captured on main 2ed1501e, before TF0's hooks)

    const string Golden = "55605AFF06EE5C496052981812E40B0ED62D2DA66E8C2857A8C297F92219FA2B";

    [Fact]
    public void The_skirmish_replay_is_unchanged()
    {
        var (hash, stats) = Replay();
        output.WriteLine($"replay {hash}: {stats}");
        // The script exercises what it is meant to pin (a retuned fixture that stops releasing, shooting or being struck fails here).
        Assert.True(stats.MaxAlive >= 40 && stats.Kos > 0 && stats.Strikes > 0 && stats.Tracers > 0 && stats.Hits > 0, stats.ToString());
        Assert.Equal(hash, Replay().Hash);   // deterministic within a process, or the golden would mean nothing
        Assert.Equal(Golden, hash);
    }

    internal readonly record struct ReplayStats(int MaxAlive, int Kos, int Strikes, int Tracers, int Hits, int Shots, int Wave, MusouPhase Phase, int Lives);

    static (string Hash, ReplayStats Stats) Replay()
    {
        var mesh = new NavigationTests.Level().Floor(0f, 0f, 60f, 60f).Wall(30f, 12f, 30f, 48f).Mesh();
        var collision = new CollisionWorld(mesh, mesh);
        var w = new World(Config, collision, Markers)
        {
            Rules = new MatchRules { CountdownTime = 1f, TimeLimit = 0f, ScoreLimit = int.MaxValue / 2 }, StartAreas = Rooms,
        };
        Assert.True(w.StartMusou(NavGraph.Build(mesh, null, NavSettings.From(Config))));
        w.Spawn(1, w.AssignClass(0));
        w.Spawn(2, w.AssignClass(0));

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var enemies = new MusouEnemy[MusouSim.Capacity];
        var line = new byte[64];
        int maxAlive = 0, strikes = 0, tracers = 0, hits = 0, shots = 0;
        for (int k = 0; k < 600; k++)
        {
            var aims = new Dictionary<int, PlayerInput>();
            foreach (var (id, p) in w.Players) aims[id] = Script(k, id, p, w.Musou!);
            w.Step(id => aims[id]);

            foreach (var p in w.Players.Values.OrderBy(p => p.PlayerId))
            {
                Floats(sha, line, p.PlayerId, p.Position.X, p.Position.Y, p.Position.Z, p.Yaw, p.Health, p.Shield, p.Alive ? 1f : 0f, p.RespawnLeft);
            }
            Ints(sha, line, (int)w.State, (int)w.TicksLeft, w.ScoreOf(Team.SpaceMarines), w.ScoreOf(Team.Chaos));
            var d = w.Musou!.Director;
            Ints(sha, line, d.Wave, d.Waves, (int)d.Phase, d.BreakTicksLeft, d.EnemiesLeft, d.EnemiesAlive, d.BossId, d.Lives);
            int n = w.Musou.CopyTo(enemies);
            for (int i = 0; i < n; i++)
            {
                var e = enemies[i];
                Floats(sha, line, e.Id, (float)e.Type, (float)e.State, e.Position.X, e.Position.Y, e.Position.Z, e.Yaw, e.Health);
            }
            foreach (var e in w.Musou.Events)
            {
                Floats(sha, line, (int)e.Kind, e.Tick, e.Enemy, e.Player, e.Value, e.Vector.X, e.Vector.Y, e.Vector.Z);
                strikes += e.Kind == MusouEventKind.Strike ? 1 : 0;
                tracers += e.Kind == MusouEventKind.Tracer ? 1 : 0;
                hits += e.Kind == MusouEventKind.Hit ? 1 : 0;
            }
            maxAlive = Math.Max(maxAlive, w.Musou.Alive);
            shots += w.LastShots.Count;
        }
        var end = w.Musou!.Director;
        return (Convert.ToHexString(sha.GetHashAndReset()), new ReplayStats(maxAlive, w.MusouDirector!.Kos, strikes, tracers, hits, shots, end.Wave, end.Phase, end.Lives));
    }

    /// <summary>Each player faces its nearest enemy, strafes a little and taps Fire; every 90 ticks it strikes in melee instead.</summary>
    static PlayerInput Script(int k, int id, PlayerState me, MusouSim sim)
    {
        Span<ushort> near = stackalloc ushort[1];
        float yaw = me.Yaw;
        if (sim.Query(me.Position, 100f, near) == 1 && sim.TryGet(near[0], out var enemy))
        {
            var d = enemy.Position - me.Position;
            yaw = MathF.Atan2(-d.X, d.Y);
        }
        int t = (k + id * 13) % 90;
        float side = id % 2 == 0 ? 1f : -1f;
        var buttons = t switch
        {
            < 70 => t % 6 < 3 ? InputButtons.Fire : InputButtons.None,
            72 or 73 => InputButtons.Melee,
            _ => InputButtons.None,
        };
        return new PlayerInput(0, side * (t < 45 ? 0.6f : -0.6f), 0f, yaw, buttons);
    }

    static void Floats(IncrementalHash sha, byte[] line, params float[] values)
    {
        for (int i = 0; i < values.Length; i++) BitConverter.TryWriteBytes(line.AsSpan(i * 4), values[i]);
        sha.AppendData(line, 0, values.Length * 4);
    }

    static void Ints(IncrementalHash sha, byte[] line, params int[] values)
    {
        for (int i = 0; i < values.Length; i++) BitConverter.TryWriteBytes(line.AsSpan(i * 4), values[i]);
        sha.AppendData(line, 0, values.Length * 4);
    }
}
