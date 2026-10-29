using System.Numerics;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// The melta's cone (M13): inside and outside the angle, partial coverage, beyond the length, the distance fall-off, several
/// targets, line of sight with a control, a lag-compensated target, and the recharging clip. A synthetic weapon shaped like
/// the install's (values typed in; no game data).
/// </summary>
public class MeltaTests(ITestOutputHelper output)
{
    internal static readonly ConeSpec Cone = new(20f, 10f, 20f, -20f, new DamageSpec(200f, 25f, 1f, 1f, 1f, 1f, false),
        [new(6.5f, 1f), new(10f, 0.5f), new(20f, 0.15f)], [new(0.15f, 0.2f), new(0.35f, 0.75f), new(0.36f, 1f), new(1f, 1f)]);
    internal static readonly WeaponSpec Melta = new("test_melta", WeaponSlot.Ranged, TriggerKind.SemiAuto, 200f, 0.33f, 1, 12, 2.5f, 250f,
        6.5f, 20f, 0.15f, 1f, 1f, 1f, 1f)
    { Delivery = DeliveryKind.Cone, Cone = Cone, Recharge = new RechargeSpec(0.5f, 0.5f) };
    static readonly WeaponSpec Sidearm = new("test_sidearm", WeaponSlot.Pistol, TriggerKind.SemiAuto, 10f, 0.2f, 10, 40, 1f, 250f, 250f, 250f, 1f, 1f, 1f, 1f, 1f);

    /// <summary>No shield and a deep health pool, so a hit's health damage is exactly 200 × its multiplier.</summary>
    static readonly SimConfig Config = new()
    {
        Weapons = [Melta, Sidearm], Classes = [new ClassLoadout("test - melta", 0, 1, []), new ClassLoadout("test - target", -1, 1, [])],
        HealthMax = 10000f, ShieldMax = 0f, HealthRegenDelay = 1e6f, MaxRewindTicks = 40,
    };

    static float Chest => (Config.BodyBottom + Config.BodyTop) / 2f;

    static World WorldWith(SimConfig config, CollisionWorld? level, params Vector3[] targets)
    {
        var spawns = new List<SpawnPoint> { new(Vector3.Zero, 0f) };
        spawns.AddRange(targets.Select(p => new SpawnPoint(p, MathF.PI)));
        var world = new World(config, level, spawns);
        world.Spawn(1, 0);
        for (int i = 1; i < spawns.Count; i++) world.Spawn(i + 1, 1);
        for (int i = 0; i < spawns.Count; i++) Assert.Equal(spawns[i].Position, world.Players[i + 1].Position);
        return world;
    }

    /// <summary>Player 1 aims at a point at chest height straight ahead (+Y) at <paramref name="ahead"/> m and fires on the first tick.</summary>
    static List<HitEvent> Fire(World world, float ahead, uint viewTick = 0, Func<int, PlayerInput>? others = null)
    {
        float pitch = MathF.Atan2(Chest - Config.EyeHeight, ahead);
        world.Step(id => id == 1 ? new PlayerInput(0, 0, 0, 0f, InputButtons.Fire, pitch, viewTick) : others?.Invoke(id) ?? new PlayerInput(0, 0, 0, MathF.PI, InputButtons.None));
        return world.LastHits.ToList();
    }

    static float Distance(float ahead, float side = 0f) => MathF.Sqrt(ahead * ahead + side * side + MathF.Pow(Config.EyeHeight - Chest, 2));

    [Fact]
    public void A_target_inside_the_cone_takes_the_full_damage_and_the_shield_part()
    {
        var hit = Assert.Single(Fire(WorldWith(Config, null, new Vector3(0, 5, 0)), 5f));
        Assert.True(hit.Cone);
        Assert.Equal((2, HitZone.Body, false, 0), (hit.Target, hit.Zone, hit.Splash, hit.Projectile));
        Assert.Equal(Distance(5f), hit.Distance, 3);
        Assert.Equal(200f, hit.HealthDamage, 3);   // under 6.5 m: ×1; fully covered: ×1
        // With a shield, the 25 shield damage comes off it first, then the 200 (shield first, as every hit).
        var shielded = Assert.Single(Fire(WorldWith(Config with { ShieldMax = 1000f }, null, new Vector3(0, 5, 0)), 5f));
        Assert.Equal((225f, 0f), (shielded.ShieldDamage, shielded.HealthDamage));
    }

    [Fact]
    public void Outside_the_angle_nothing_is_hit_and_partial_coverage_scales_the_damage()
    {
        Assert.Empty(Fire(WorldWith(Config, null, new Vector3(4f, 5f, 0)), 5f));   // 4 m aside: the cone is ~2.2 m wide there
        // 2 m aside: some of the target's sample points are inside.
        var origin = new Vector3(0, 0, Config.EyeHeight);
        var direction = Combat.AimDirection(0f, MathF.Atan2(Chest - Config.EyeHeight, 5f));
        float coverage = Cones.Coverage(Cone, origin, direction, new Vector3(2f, 5f, 0), Config);
        Assert.InRange(coverage, 0.01f, 0.99f);
        var hit = Assert.Single(Fire(WorldWith(Config, null, new Vector3(2f, 5f, 0)), 5f));
        Assert.Equal(200f * Combat.PointValue(Cone.CoverageFallOff, coverage), hit.HealthDamage, 2);
        output.WriteLine($"2 m aside at 5 m: coverage {coverage:F3}, x{Combat.PointValue(Cone.CoverageFallOff, coverage):F3}");
        // The coverage multiplier's points: below 0.35 it falls off, from 0.36 it is 1.
        Assert.Equal(0.2f, Combat.PointValue(Cone.CoverageFallOff, 0.05f));
        Assert.Equal(0.475f, Combat.PointValue(Cone.CoverageFallOff, 0.25f), 4);
        Assert.Equal(1f, Combat.PointValue(Cone.CoverageFallOff, 0.5f));
    }

    [Theory]
    [InlineData(15f, true)]
    [InlineData(19f, true)]
    [InlineData(21f, false)]   // beyond the 20 m length
    public void The_cone_ends_at_its_length_and_damage_falls_off_linearly_with_distance(float ahead, bool hits)
    {
        var events = Fire(WorldWith(Config, null, new Vector3(0, ahead, 0)), ahead);
        if (!hits) { Assert.Empty(events); return; }
        var hit = Assert.Single(events);
        float d = Distance(ahead);
        float expected = 0.5f + (0.15f - 0.5f) * ((d - 10f) / 10f);
        Assert.Equal(200f * expected, hit.HealthDamage, 2);
    }

    [Fact]
    public void Every_target_in_the_cone_is_hit()
    {
        var hits = Fire(WorldWith(Config, null, new Vector3(0, 3, 0), new Vector3(1f, 8, 0), new Vector3(-1f, 12, 0)), 8f);
        Assert.Equal([2, 3, 4], hits.Select(h => h.Target));
        Assert.All(hits, h => Assert.True(h.Cone && h.HealthDamage > 0f));
        Assert.Equal(200f, hits[0].HealthDamage, 2);   // 3 m: no fall-off
    }

    static CollisionWorld Wall(float y)
    {
        var v = new List<Vector3>(); var idx = new List<int>();
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) { int n = v.Count; v.AddRange([a, b, c, d]); idx.AddRange([n, n + 1, n + 2, n, n + 2, n + 3]); }
        Quad(new(-60, -60, 0), new(60, -60, 0), new(60, 60, 0), new(-60, 60, 0));
        Quad(new(-60, y, -1), new(60, y, -1), new(60, y, 20), new(-60, y, 20));
        var mesh = new StaticMesh(v.ToArray(), idx.ToArray());
        return new CollisionWorld(mesh, mesh);
    }

    [Fact]
    public void A_wall_between_shooter_and_target_blocks_the_cone()
    {
        Assert.Empty(Fire(WorldWith(Config, Wall(6f), new Vector3(0, 10, 0)), 10f));
        Assert.Single(Fire(WorldWith(Config, Wall(12f), new Vector3(0, 10, 0)), 10f));   // control: the wall behind the target
        Assert.Equal([2], Fire(WorldWith(Config, Wall(6f), new Vector3(0, 4, 0), new Vector3(0.5f, 10, 0)), 10f).Select(h => h.Target));   // in front of it: hit
    }

    [Fact]
    public void The_cone_hits_targets_where_the_shooter_saw_them()
    {
        // The target runs sideways out of the cone; the shooter aims where it stood 30 ticks ago.
        PlayerInput Runner(int id) => new(0, -1f, 0, MathF.PI, InputButtons.Sprint);   // facing −Y, stick left: moves +X
        World Run(out uint before)
        {
            var world = WorldWith(Config, null, new Vector3(0, 6, 0));
            before = world.Tick;
            for (int k = 0; k < 30; k++) world.Step(id => id == 1 ? new PlayerInput(0, 0, 0, 0f, InputButtons.None) : Runner(id));
            Assert.True(world.Players[2].Position.X > 4f, $"{world.Players[2].Position}");
            return world;
        }
        var rewound = Run(out uint seen);
        Assert.Single(Fire(rewound, 6f, viewTick: seen, others: Runner));
        var current = Run(out _);
        Assert.Empty(Fire(current, 6f, viewTick: current.Tick + 1, others: Runner));   // control: no rewind
    }

    [Fact]
    public void The_clip_recharges_one_round_2_5_s_after_a_shot_from_the_reserve_and_ignores_reload()
    {
        var s = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = 0 }, Config);
        Assert.Equal((1, 12), (s.Clip, s.Reserve));
        s = Combat.StepPlayer(s, new PlayerInput(0, 0, 0, 0, InputButtons.Fire), Config, out var e);
        Assert.True(e.Fired);
        Assert.Equal(0, s.Clip);
        int back = -1;
        for (int k = 1; k <= 90; k++)
        {
            // Reload presses change nothing; the trigger stays held from the shot (semi-auto: no new press edge).
            var buttons = InputButtons.Fire | (k % 3 == 0 ? InputButtons.Reload : InputButtons.None);
            s = Combat.StepPlayer(s, new PlayerInput(0, 0, 0, 0, buttons), Config, out e);
            if (back < 0 && s.Clip == 1) back = k;
            if (k > 1 && back < 0) Assert.True(s.ReloadLeft > 0f, $"tick {k}");
        }
        Assert.Equal(76, back);   // starts the tick after the shot, 2.5 s = 75 ticks
        Assert.Equal(11, s.Reserve);
        // Semi-auto: the trigger must be released; then it fires again.
        s = Combat.StepPlayer(s, new PlayerInput(0, 0, 0, 0, InputButtons.None), Config, out _);
        s = Combat.StepPlayer(s, new PlayerInput(0, 0, 0, 0, InputButtons.Fire), Config, out e);
        Assert.True(e.Fired);
        // No reserve: no recharge.
        var dry = s with { Reserve = 0 };
        for (int k = 0; k < 100; k++) dry = Combat.StepPlayer(dry, new PlayerInput(0, 0, 0, 0, InputButtons.None), Config, out _);
        Assert.Equal((0, 0f), (dry.Clip, dry.ReloadLeft));
        // A switch drops a recharge under way (as a reload); back in hand it starts over.
        var sw = s;
        for (int k = 0; k < 30; k++) sw = Combat.StepPlayer(sw, new PlayerInput(0, 0, 0, 0, InputButtons.None), Config, out _);
        sw = Combat.StepPlayer(sw, new PlayerInput(0, 0, 0, 0, InputButtons.Switch), Config, out _);
        Assert.Equal((WeaponSlot.Pistol, 0f), (sw.Slot, sw.ReloadLeft));
    }

    [Fact]
    public void The_shooter_is_never_hit_and_the_cone_is_wide_at_the_muzzle_but_not_behind()
    {
        // Beside the shooter, half a metre ahead and 1.2 m aside: inside the ~1.8 m radius the cone has at the muzzle.
        var origin = new Vector3(0, 0, Config.EyeHeight);
        var direction = Combat.AimDirection(0f, 0f);
        Assert.True(Cones.Coverage(Cone, origin, direction, new Vector3(1.2f, 0.5f, 0), Config) > 0f);
        // (One target per world: the spawn rule keeps a free point 2 m from others while one exists.)
        foreach (var (at, hit) in new[] { (new Vector3(1.2f, 0.5f, 0), true), (new Vector3(0, -2f, 0), false) })
        {
            var world = WorldWith(Config, null, at);
            world.Step(id => id == 1 ? new PlayerInput(0, 0, 0, 0f, InputButtons.Fire) : new PlayerInput(0, 0, 0, MathF.PI, InputButtons.None));
            Assert.Equal(hit ? [2] : [], world.LastHits.Select(h => h.Target));   // never the shooter (1); nothing behind it
        }
    }

    [Fact]
    public void A_shot_interrupts_a_recharge_when_the_clip_still_holds_a_round()
    {
        var config = Config with { Weapons = [Melta with { ClipSize = 2 }, Sidearm] };
        var s = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = 0 }, config);
        Assert.Equal(2, s.Clip);
        s = Combat.StepPlayer(s, new PlayerInput(0, 0, 0, 0, InputButtons.Fire), config, out var e);
        Assert.True(e.Fired);
        for (int k = 0; k < 15; k++) s = Combat.StepPlayer(s, new PlayerInput(0, 0, 0, 0, InputButtons.None), config, out _);
        Assert.Equal(1, s.Clip);
        Assert.True(s.ReloadLeft > 0f);   // recharging the second round
        s = Combat.StepPlayer(s, new PlayerInput(0, 0, 0, 0, InputButtons.Fire), config, out e);
        Assert.True(e.Fired);
        Assert.Equal((0, 0f, 12), (s.Clip, s.ReloadLeft, s.Reserve));   // the round under way is lost, nothing taken from the reserve
        s = Combat.StepPlayer(s, new PlayerInput(0, 0, 0, 0, InputButtons.None), config, out _);
        Assert.Equal(2.5f, s.ReloadLeft, 4);   // the next starts over with its delay
    }

    [Theory]
    [InlineData(91)]
    [InlineData(92)]
    public void Prediction_matches_the_server_with_the_melta_under_150ms_rtt_and_5pct_loss(int seed)
    {
        var config = Config with
        {
            Weapons = [Melta with { ReserveSize = 3000, Cone = Cone with { Damage = Cone.Damage with { Damage = 0f, ShieldDamage = 0f } } }, Sidearm with { Damage = 0f, ReserveSize = 3000 }],
            RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f,
        };
        var link = new LinkConditions(TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(10), 0.05);
        var h = new Harness(link, seed, 0, config, new SmServer.ServerOptions { PeerTimeout = TimeSpan.FromMinutes(5) });
        for (int i = 0; i < 2; i++) h.AddClient(nonce: (uint)(100 + i), options: new ClientOptions { RequestedClass = 0 });
        var truth = new SortedDictionary<uint, Dictionary<int, PlayerState>>();
        var predicted = Enumerable.Range(0, 2).Select(_ => new Dictionary<uint, PlayerState>()).ToArray();
        PlayerInput Input(int i, uint t)
        {
            var move = Harness.Scripted(i, t);
            uint phase = (t + (uint)i * 17) % 150;
            var buttons = move.Buttons & ~InputButtons.Jump;
            if (phase % 9 < 3) buttons |= InputButtons.Fire;                 // presses during and after the recharge
            if (phase is 40 or 41) buttons |= InputButtons.Reload;          // ignored
            if (phase is 100 or 101 or 130 or 131) buttons |= InputButtons.Switch;
            return move with { Buttons = buttons };
        }
        h.Run(TimeSpan.FromSeconds(25), Input, () =>
        {
            truth[h.Server.World.Tick] = new Dictionary<int, PlayerState>(h.Server.World.Players);
            for (int i = 0; i < 2; i++)
                if (h.Clients[i].Status == ClientStatus.Connected) predicted[i][h.Clients[i].Tick] = h.Clients[i].Predicted;
        });
        for (int i = 0; i < 2; i++)
        {
            int id = h.Clients[i].PlayerId, compared = 0;
            var mismatches = new List<string>();
            uint from = truth.Keys.First() + 90;
            foreach (var (tick, pred) in predicted[i])
            {
                if (tick < from || !truth.TryGetValue(tick, out var world) || !world.TryGetValue(id, out var real)) continue;
                compared++;
                if (pred != real) mismatches.Add($"tick {tick}: predicted {pred}; server {real}");
            }
            var mine = truth.Values.Where(w => w.ContainsKey(id)).Select(w => w[id]).ToList();
            int shots = mine.Zip(mine.Skip(1)).Count(p => p.First.Slot == WeaponSlot.Ranged && p.Second.Slot == WeaponSlot.Ranged && p.Second.Clip < p.First.Clip);
            output.WriteLine($"client {i}: compared {compared}, {mismatches.Count} mismatches, {shots} melta shots");
            foreach (var m in mismatches.Take(3)) output.WriteLine("  " + m);
            Assert.True(compared > 300);
            Assert.True(shots >= 5, $"only {shots} shots");
            Assert.Empty(mismatches);
        }
    }

    [Fact]
    public void A_raycast_weapon_is_unaffected_by_cone_code()
    {
        // Control: the sidearm (a ray) at 5 m ahead hits once, without the cone flag.
        var world = WorldWith(Config, null, new Vector3(0, 5, 0));
        world.Step(id => id == 1 ? new PlayerInput(0, 0, 0, 0f, InputButtons.Switch) : new PlayerInput(0, 0, 0, MathF.PI, InputButtons.None));
        for (int k = 0; k < 20; k++) world.Step(id => new PlayerInput(0, 0, 0, id == 1 ? 0f : MathF.PI, InputButtons.None));
        var hit = Assert.Single(Fire(world, 5f));
        Assert.False(hit.Cone);
        Assert.Equal(10f, hit.HealthDamage, 3);
    }
}
