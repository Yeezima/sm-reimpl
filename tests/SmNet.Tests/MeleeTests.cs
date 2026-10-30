using System.Numerics;
using SmNet;
using SmServer;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// Melee: the combo state machine in <see cref="Combat.StepSelf(PlayerState, PlayerInput, SimConfig, out bool, out int, CollisionWorld?)"/>,
/// the world's melee hit resolution (reach, cone, height, walls, lag compensation), and exact prediction over a lossy
/// link. Synthetic timings only, placed half a tick off the 30 Hz grid so every event falls on one unambiguous tick.
/// </summary>
public class MeleeTests(ITestOutputHelper output)
{
    /// <summary>Seconds of <paramref name="ticks"/> ticks at 30 Hz.</summary>
    static float T(float ticks) => ticks / 30f;

    /// <summary>
    /// Tick n after the press (time n/30): hit at n = 7, combo window n = 13..19 (a press up to n = 19 counts), firing
    /// allowed from n = 16, movement from n = 22, attack over at n = 31.
    /// </summary>
    static MeleeStep Step(float damage, float secondary) =>
        new(T(30.5f), T(6.5f), T(12.5f), T(18.5f), T(15.5f), T(21.5f), damage, secondary, 3.5f);

    static readonly SimConfig Config = new()
    {
        MeleeSteps = [Step(10f, 5f), Step(20f, 6f), Step(30f, 7f)],
        MeleeRange = 4f, MeleeConeDegrees = 60f, MeleeVerticalReach = 1.5f,
        // Shield and health take raw damage, so ShieldDamage + HealthDamage is the damage dealt.
        ShieldBodyMultiplier = 1f, BodyMultiplier = 1f,
    };

    static PlayerState Fresh(SimConfig? c = null) => Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true), c ?? Config);
    static PlayerInput In(uint tick, InputButtons buttons, float moveY = 0f) => new(tick, 0f, moveY, 0f, buttons);

    /// <summary>Runs StepSelf over a button script (index = tick after the first), recording state and hit steps.</summary>
    sealed class Run
    {
        public readonly List<PlayerState> States = new();
        public readonly List<int> HitSteps = new();
        public readonly List<bool> Fired = new();
        public PlayerState State;
        public Run(PlayerState start) => State = start;
        public Run Tick(InputButtons buttons, float moveY = 0f, SimConfig? c = null)
        {
            State = Combat.StepSelf(State, In((uint)States.Count, buttons, moveY), c ?? Config, out bool fired, out int hit);
            States.Add(State); HitSteps.Add(hit); Fired.Add(fired);
            return this;
        }
        public Run Ticks(int n, InputButtons buttons = InputButtons.None, float moveY = 0f) { for (int i = 0; i < n; i++) Tick(buttons, moveY); return this; }
        /// <summary>Press melee on tick 0 (relative to now) and release.</summary>
        public Run Press() => Tick(InputButtons.Melee);
    }

    // ---------------------------------------------------------------- the combo state machine

    [Fact]
    public void Press_starts_step_1_at_time_0()
    {
        var r = new Run(Fresh()).Press();
        var s = r.States[0];
        Assert.Equal((1, 0f, false, true), (s.MeleeStep, s.MeleeTime, s.MeleeQueued, s.MeleeHeld));
        r.Tick(InputButtons.None);
        Assert.Equal(1, r.State.MeleeStep);
        Assert.Equal(T(1), r.State.MeleeTime, 5);
        Assert.False(r.State.MeleeHeld);
    }

    [Fact]
    public void Holding_melee_does_not_retrigger_and_hits_once()
    {
        var r = new Run(Fresh()).Ticks(90, InputButtons.Melee);
        Assert.Equal(1, r.HitSteps.Count(h => h > 0));
        Assert.Equal(31, r.States.FindIndex(s => s.MeleeStep == 0));   // over at Duration, not restarted while held
        Assert.All(r.States.Skip(31), s => Assert.Equal(0, s.MeleeStep));
        Assert.DoesNotContain(r.States, s => s.MeleeStep > 1 || s.MeleeQueued);
    }

    [Fact]
    public void Hit_flag_is_raised_exactly_once_at_HitTime()
    {
        var r = new Run(Fresh()).Press().Ticks(40);
        var hits = Enumerable.Range(0, r.HitSteps.Count).Where(i => r.HitSteps[i] > 0).ToList();
        Assert.Equal(new[] { 7 }, hits);
        Assert.Equal(1, r.HitSteps[7]);
        Assert.True(r.States[6].MeleeTime < T(6.5f) && r.States[7].MeleeTime >= T(6.5f));
    }

    [Fact]
    public void Press_inside_the_combo_window_advances_to_the_next_step_at_once()
    {
        var r = new Run(Fresh()).Press().Ticks(13).Press().Ticks(20);   // second press on tick 14
        Assert.Equal(1, r.States[13].MeleeStep);
        Assert.Equal((2, 0f, false), (r.States[14].MeleeStep, r.States[14].MeleeTime, r.States[14].MeleeQueued));
        Assert.Equal(1, r.HitSteps[7]);
        Assert.Equal(2, r.HitSteps[14 + 7]);   // step 2 hits 7 ticks after it starts
    }

    [Fact]
    public void Press_before_the_window_is_buffered_and_applies_when_it_opens()
    {
        var r = new Run(Fresh()).Press().Ticks(4).Press().Ticks(20);   // second press on tick 5
        Assert.True(r.States[5].MeleeQueued);
        Assert.All(r.States.Skip(5).Take(8), s => Assert.Equal((1, true), (s.MeleeStep, s.MeleeQueued)));   // ticks 5..12
        Assert.Equal((2, 0f, false), (r.States[13].MeleeStep, r.States[13].MeleeTime, r.States[13].MeleeQueued));
        Assert.Equal(1, r.HitSteps[7]);   // the buffered press does not disturb step 1's hit
    }

    [Fact]
    public void Last_tick_of_the_window_still_counts_and_a_later_press_does_nothing()
    {
        var late = new Run(Fresh()).Press().Ticks(18).Press().Ticks(5);   // tick 19: last tick that counts
        Assert.Equal(2, late.States[19].MeleeStep);

        var tooLate = new Run(Fresh()).Press().Ticks(19).Press().Ticks(30);   // tick 20: after ComboClose
        Assert.False(tooLate.States[20].MeleeQueued);
        Assert.DoesNotContain(tooLate.States, s => s.MeleeStep > 1);
        Assert.Equal(1, tooLate.States[30].MeleeStep);
        Assert.Equal(0, tooLate.States[31].MeleeStep);   // the attack ends at Duration
        Assert.Equal(1, tooLate.HitSteps.Count(h => h > 0));
    }

    [Fact]
    public void Combo_wraps_to_the_first_step_after_the_last()
    {
        var r = new Run(Fresh()).Press();
        for (int i = 0; i < 4; i++) r.Ticks(13).Press();   // a press every 14 ticks, each inside the window
        r.Ticks(40);
        var started = r.States.Select(s => s.MeleeStep).Where((step, i) => i == 0 || step != r.States[i - 1].MeleeStep).Where(s => s > 0).ToList();
        Assert.Equal(new[] { 1, 2, 3, 1, 2 }, started);
        Assert.Equal(new[] { 1, 2, 3, 1, 2 }, r.HitSteps.Where(h => h > 0));
    }

    [Fact]
    public void Fire_is_ignored_before_RangedAt_and_then_ends_the_attack_and_fires()
    {
        var r = new Run(Fresh()).Press().Ticks(25, InputButtons.Fire);
        int firstShot = r.Fired.IndexOf(true);
        Assert.Equal(16, firstShot);
        Assert.All(r.States.Take(16), s => Assert.Equal(Config.ClipSize, s.Clip));
        Assert.Equal(0, r.States[16].MeleeStep);
        Assert.Equal(Config.ClipSize - 1, r.States[16].Clip);
        Assert.Equal(1, r.HitSteps.Count(h => h > 0));   // the hit before the cancel still landed

        // Reload input is ignored during an attack, and a melee press cancels a reload in progress.
        var reload = new Run(Fresh() with { Clip = 5 }).Press().Ticks(5, InputButtons.Reload);
        Assert.All(reload.States, s => Assert.Equal(0f, s.ReloadLeft));
        var cancel = new Run(Fresh() with { Clip = 5 }).Tick(InputButtons.Reload).Ticks(3).Press();
        Assert.True(cancel.States[3].ReloadLeft > 0f);
        Assert.Equal((0f, 1, 5), (cancel.State.ReloadLeft, cancel.State.MeleeStep, cancel.State.Clip));
    }

    [Fact]
    public void Movement_input_is_ignored_until_MoveAt()
    {
        var r = new Run(Fresh()).Tick(InputButtons.Melee | InputButtons.Sprint, 1f).Ticks(35, InputButtons.Sprint, 1f);
        Assert.All(r.States.Take(22), s => Assert.Equal((Vector3.Zero, Vector3.Zero), (s.Position, s.Velocity)));   // ticks 0..21
        Assert.True(r.States[22].Velocity.Y > 0f);
        Assert.Equal(1, r.States[22].MeleeStep);   // moving does not end the attack
        Assert.Equal(0, r.States[31].MeleeStep);

        // Turning is not locked.
        var turn = Combat.StepSelf(Fresh(), new PlayerInput(0, 0, 1, 1.0f, InputButtons.Melee), Config, out _, out _);
        Assert.Equal(1.0f, turn.Yaw);
    }

    [Fact]
    public void Dead_players_cannot_start_an_attack_and_death_ends_one()
    {
        var dead = Combat.Killed(Fresh(), Config);
        var r = new Run(dead).Press().Ticks(10, InputButtons.Melee);
        Assert.All(r.States, s => Assert.Equal(0, s.MeleeStep));
        Assert.All(r.HitSteps, h => Assert.Equal(0, h));

        var attacking = new Run(Fresh()).Press().Ticks(3).State;
        var (killed, _, _) = Combat.ApplyDamage(attacking, 1e6f, HitZone.Body, Config);
        Assert.Equal((false, 0, 0f, false), (killed.Alive, killed.MeleeStep, killed.MeleeTime, killed.MeleeQueued));
        var respawned = Combat.Respawned(attacking with { MeleeQueued = true }, Config);
        Assert.Equal((0, 0f, false), (respawned.MeleeStep, respawned.MeleeTime, respawned.MeleeQueued));
    }

    [Fact]
    public void Without_melee_steps_melee_does_nothing()
    {
        var c = Config with { MeleeSteps = [] };
        var s = Combat.StepSelf(Fresh(c), In(0, InputButtons.Melee | InputButtons.Fire), c, out bool fired, out int hit);
        Assert.Equal((0, 0, true), (s.MeleeStep, hit, fired));
    }

    // ---------------------------------------------------------------- the world

    /// <summary>A world with players 1..n standing at <paramref name="at"/> (in order; spawn points at those spots).</summary>
    static World Place(SimConfig c, CollisionWorld? level, params Vector3[] at)
    {
        var world = new World(c, level, at.Select(p => new SpawnPoint(p, 0f)).ToArray());
        for (int i = 0; i < at.Length; i++) Assert.Equal(at[i], world.Spawn(i + 1).Position);
        return world;
    }

    /// <summary>Player 1 presses melee on the first tick facing <paramref name="yaw"/>; returns every hit over 12 ticks.</summary>
    static List<HitEvent> Swing(World world, float yaw = 0f, int ticks = 12, uint viewTick = 0)
    {
        var hits = new List<HitEvent>();
        for (int i = 0; i < ticks; i++)
        {
            world.Step(id => id == 1 ? new PlayerInput(0, 0, 0, yaw, i == 0 ? InputButtons.Melee : InputButtons.None, 0, viewTick) : new PlayerInput(0, 0, 0, 0, InputButtons.None));
            hits.AddRange(world.LastHits);
        }
        return hits;
    }

    [Fact]
    public void Melee_damage_uses_the_weapons_own_multipliers_not_the_rifles()
    {
        // The rifle's shield multiplier (0.505) stays in the config; the knife's step carries its own ×1 for the shield.
        var knife = Config with
        {
            ShieldBodyMultiplier = 0.505f,
            MeleeSteps = [Step(10f, 5f) with { ShieldMultiplier = 1f, HealthMultiplier = 1f }, Step(20f, 6f), Step(30f, 7f)],
        };
        var hits = Swing(Place(knife, null, Vector3.Zero, new(0, 2, 0)));
        var hit = Assert.Single(hits);
        Assert.Equal(10f, hit.ShieldDamage, 4);   // with the rifle's multiplier this would be 5.05
        // A step whose own multiplier halves shield damage.
        var halved = Swing(Place(knife with { MeleeSteps = [Step(10f, 5f) with { ShieldMultiplier = 0.5f }, Step(20f, 6f), Step(30f, 7f)] }, null, Vector3.Zero, new(0, 2, 0)));
        Assert.Equal(5f, Assert.Single(halved).ShieldDamage, 4);
    }

    [Fact]
    public void World_melee_hits_the_nearest_for_Damage_and_others_in_reach_for_SecondaryDamage()
    {
        var world = Place(Config, null,
            Vector3.Zero,               // 1: attacker, facing +Y
            new(0, 2, 0),               // 2: primary (nearest)
            new(1.7f, 3.5f, 0),         // 3: secondary, 26° off, 3.9 m
            new(0, -2, 0),              // 4: behind
            new(-1, 4.3f, 0),           // 5: 13° off but 4.4 m away
            new(3.6f, 1.5f, 0),         // 6: in reach, 67° off: outside the cone
            new(-3.5f, 3.5f, 0),        // 7: 45° off but 4.9 m away
            new(-1, 2.5f, 2.0f),        // 8: in front but 2 m higher
            new(1.5f, -1, 1.4f));       // 9: behind and higher
        var hits = Swing(world);
        foreach (var h in hits) output.WriteLine($"{h.Shooter} -> {h.Target}: {h.Distance:F2} m, {h.ShieldDamage + h.HealthDamage}");
        Assert.All(hits, h => Assert.True(h.Melee && h.Zone == HitZone.Body && h.Shooter == 1 && h.Tick == 8));   // pressed on tick 1, hit 7 ticks later
        Assert.Equal(new[] { 2, 3 }, hits.Select(h => h.Target));
        Assert.Equal(10f, hits[0].ShieldDamage + hits[0].HealthDamage, 4);
        Assert.Equal(5f, hits[1].ShieldDamage + hits[1].HealthDamage, 4);
        Assert.Equal(Config.ShieldMax - 10f, world.Players[2].Shield, 4);
        Assert.Equal(Config.ShieldMax - 5f, world.Players[3].Shield, 4);
        foreach (int untouched in new[] { 4, 5, 6, 7, 8, 9 }) Assert.Equal(Config.ShieldMax, world.Players[untouched].Shield);
    }

    [Theory]
    [InlineData(1.4f, true)]
    [InlineData(1.6f, false)]
    public void World_melee_respects_the_vertical_reach(float height, bool hit)
    {
        var world = Place(Config, null, Vector3.Zero, new(0, 2, height));
        Assert.Equal(hit, Swing(world).Count == 1);
    }

    [Theory]
    [InlineData(55f, true)]
    [InlineData(65f, false)]
    public void World_melee_respects_the_cone(float degrees, bool hit)
    {
        float a = degrees * MathF.PI / 180f;
        var world = Place(Config, null, Vector3.Zero, new(-MathF.Sin(a) * 3f, MathF.Cos(a) * 3f, 0));   // left of +Y
        Assert.Equal(hit, Swing(world).Count == 1);
        // Turning to face the target always hits.
        var facing = Place(Config, null, Vector3.Zero, new(-MathF.Sin(a) * 3f, MathF.Cos(a) * 3f, 0));
        Assert.Single(Swing(facing, yaw: a));
    }

    [Theory]
    [InlineData(3.9f, true)]
    [InlineData(4.1f, false)]
    public void World_melee_respects_the_range(float distance, bool hit)
    {
        var world = Place(Config, null, Vector3.Zero, new(0, distance, 0));
        Assert.Equal(hit, Swing(world).Count == 1);
    }

    static CollisionWorld Level(bool wall)
    {
        var v = new List<Vector3>(); var idx = new List<int>();
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            int i = v.Count; v.AddRange([a, b, c, d]); idx.AddRange([i, i + 1, i + 2, i, i + 2, i + 3]);
        }
        Quad(new(-20, -20, 0), new(20, -20, 0), new(20, 20, 0), new(-20, 20, 0));
        if (wall) Quad(new(-5, 1, -1), new(5, 1, -1), new(5, 1, 4), new(-5, 1, 4));   // the plane y = 1, between the two
        var mesh = new StaticMesh(v.ToArray(), idx.ToArray());
        return new CollisionWorld(mesh, mesh);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public void World_melee_is_stopped_by_a_wall(bool wall, int expected)
    {
        var world = Place(Config, Level(wall), Vector3.Zero, new(0, 2, 0));
        Assert.Equal(expected, Swing(world).Count);
    }

    [Fact]
    public void World_melee_uses_the_attackers_view_tick_lag_compensation()
    {
        // Instant acceleration to 10 m/s, so the positions are easy to reason about: x = tick / 3.
        var c = Config with { RunSpeed = 10f, StrafeSpeed = 10f, GroundTimeToMaxSpeed = 1f / 30f, MaxAcceleration = 1e5f, MaxRewindTicks = 15 };
        var positions = new Dictionary<uint, Vector3>();
        List<HitEvent> Play(Func<uint, uint> viewTick)
        {
            var world = Place(c, null, Vector3.Zero, new(0, 2, 0));
            var hits = new List<HitEvent>();
            // Player 2 strafes right (+X) from tick 1; player 1 presses melee at tick 9, so the hit lands at tick 16.
            while (world.Tick < 20)
            {
                uint tick = world.Tick + 1;
                world.Step(id => id == 2 ? new PlayerInput(0, 1, 0, 0, InputButtons.None)
                    : new PlayerInput(0, 0, 0, 0, tick == 9 ? InputButtons.Melee : InputButtons.None, 0, viewTick(tick)));
                positions[world.Tick] = world.Players[2].Position;
                hits.AddRange(world.LastHits);
            }
            return hits;
        }
        var current = Play(t => t);
        output.WriteLine($"target at tick 2: {positions[2]}, at tick 16: {positions[16]}");
        Assert.True(positions[2].X < 1f, "the target should still be in front at the view tick");
        Assert.True(positions[16].X > 4f, "the target should be out of reach by the hit tick");
        Assert.Empty(current);

        var rewound = Play(t => 2);   // the attacker saw the world at tick 2 (14 ticks back from the hit: within the cap)
        var hit = Assert.Single(rewound);
        Assert.Equal((16u, 2, true), (hit.Tick, hit.Target, hit.Melee));
        Assert.True(hit.Distance < 2.5f);

        var capped = Play(t => 0);   // view tick 0 asks for more than MaxRewindTicks: clamped to tick 1 (16 - 15)
        Assert.Single(capped);
    }

    [Fact]
    public void World_attacker_killed_before_its_hit_lands_deals_no_damage()
    {
        var c = Config with { MeleeSteps = [Step(1e4f, 1e4f), Step(1e4f, 1e4f), Step(1e4f, 1e4f)] };
        var world = new World(c, null, [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new(0, 2, 0), MathF.PI)]);
        world.Spawn(1); world.Spawn(2);
        var hits = new List<HitEvent>();
        // Player 2 presses on tick 1, player 1 on tick 4: player 2's hit lands on tick 8 and kills player 1 first.
        for (uint i = 1; i <= 15; i++)
        {
            uint tick = i;
            world.Step(id => id == 1 ? new PlayerInput(0, 0, 0, 0, tick == 4 ? InputButtons.Melee : InputButtons.None)
                : new PlayerInput(0, 0, 0, MathF.PI, tick == 1 ? InputButtons.Melee : InputButtons.None));
            hits.AddRange(world.LastHits);
        }
        var only = Assert.Single(hits);
        Assert.Equal((2, 1, true, 8u), (only.Shooter, only.Target, only.Killed, only.Tick));
        Assert.True(world.Players[2].Alive);
    }

    // ---------------------------------------------------------------- configuration and protocol

    [Fact]
    public void Config_hash_covers_every_melee_value()
    {
        var c = Config;
        ulong h = c.Hash();
        Assert.Equal(h, (c with { MeleeSteps = c.MeleeSteps.ToArray() }).Hash());   // value, not reference
        Assert.Equal(new SimConfig().Hash(), new SimConfig { MeleeSteps = SimConfig.PlaceholderMeleeSteps.ToList() }.Hash());
        Assert.NotEqual(h, (c with { MeleeRange = 4.5f }).Hash());
        Assert.NotEqual(h, (c with { MeleeConeDegrees = 45f }).Hash());
        Assert.NotEqual(h, (c with { MeleeVerticalReach = 1f }).Hash());
        Assert.NotEqual(h, (c with { MeleeSteps = c.MeleeSteps.Take(2).ToArray() }).Hash());
        var changes = new Func<MeleeStep, MeleeStep>[]
        {
            m => m with { Duration = m.Duration + 0.01f }, m => m with { HitTime = m.HitTime + 0.01f },
            m => m with { ComboOpen = m.ComboOpen + 0.01f }, m => m with { ComboClose = m.ComboClose + 0.01f },
            m => m with { RangedAt = m.RangedAt + 0.01f }, m => m with { MoveAt = m.MoveAt + 0.01f },
            m => m with { Damage = m.Damage + 1 }, m => m with { SecondaryDamage = m.SecondaryDamage + 1 }, m => m with { Lunge = m.Lunge + 1 },
        };
        var seen = new HashSet<ulong> { h };
        for (int step = 0; step < c.MeleeSteps.Count; step++)
            foreach (var change in changes)
            {
                var steps = c.MeleeSteps.ToArray();
                steps[step] = change(steps[step]);
                Assert.True(seen.Add((c with { MeleeSteps = steps }).Hash()), $"step {step + 1}: a change left the hash unchanged");
            }
    }

    [Fact]
    public void Snapshot_carries_the_melee_state()
    {
        var p = new PlayerState(3, Vector3.One, Vector3.Zero, 1f, true) { Alive = true, MeleeStep = 2, MeleeTime = 0.3f, MeleeQueued = true, MeleeHeld = false };
        var q = p with { PlayerId = 4, MeleeStep = 0, MeleeTime = 0f, MeleeQueued = false, MeleeHeld = true };
        // Protocol 5: the recipient's own player (p) carries the whole melee state; others only step and time.
        var back = Assert.IsType<Snapshot>(Protocol.Read(Protocol.Write(new Snapshot(5, 4, 1, [p, q], Self: 3))));
        Assert.Equal(new[] { p, Protocol.Slim(q) }, back.Players);
        Assert.Equal((2, 0.3f), (back.Players[0].MeleeStep, back.Players[0].MeleeTime));
        Assert.Equal(44, Protocol.Version);
        var bytes = Protocol.Write(new Snapshot(5, 4, 1, [p], Self: 3));
        // The own record's melee flag byte (4 reaction bytes follow since protocol 17, then the match block, kill count, point
        // count, hit block and shot count): a heavy buffered press (128) without a buffered press (1) is refused (64 is the heavy held flag).
        bytes[^30] = 128;
        Assert.Null(Protocol.Read(bytes));
    }

    // ---------------------------------------------------------------- prediction

    [Theory]
    [InlineData(41)]
    [InlineData(42)]
    public void Prediction_matches_the_server_while_meleeing_under_150ms_rtt_and_5pct_loss(int seed)
    {
        // Real timings in shape, but no damage, so the two clients cannot hurt each other (damage is not predicted).
        var config = Config with
        {
            MeleeSteps = [Step(0f, 0f), Step(0f, 0f), Step(0f, 0f)],
            RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f,
        };
        var link = new LinkConditions(TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(10), 0.05);
        var h = new Harness(link, seed, 2, config, new ServerOptions { PeerTimeout = TimeSpan.FromMinutes(5) });
        var truth = new SortedDictionary<uint, Dictionary<int, PlayerState>>();
        var predicted = new[] { new Dictionary<uint, PlayerState>(), new Dictionary<uint, PlayerState>() };
        PlayerInput Input(int i, uint t)
        {
            var move = Harness.Scripted(i, t);
            var buttons = move.Buttons & ~InputButtons.Jump;
            // Melee pressed for 2 ticks every 14 (combos), with pauses; fire now and then (cancels after RangedAt).
            uint phase = (t + (uint)i * 7) % 120;
            if (phase < 90 && phase % 14 < 2) buttons |= InputButtons.Melee;
            if ((t / 37 + (uint)i) % 4 == 0) buttons |= InputButtons.Fire;
            return move with { Buttons = buttons, Pitch = 1.3f };
        }
        h.Run(TimeSpan.FromSeconds(20), Input, () =>
        {
            truth[h.Server.World.Tick] = new Dictionary<int, PlayerState>(h.Server.World.Players);
            for (int i = 0; i < 2; i++)
                if (h.Clients[i].Status == ClientStatus.Connected) predicted[i][h.Clients[i].Tick] = h.Clients[i].Predicted;
        });

        for (int i = 0; i < 2; i++)
        {
            var c = h.Clients[i];
            int id = c.PlayerId, compared = 0;
            var mismatches = new List<string>();
            uint from = truth.Keys.First() + 90;
            foreach (var (tick, pred) in predicted[i])
            {
                if (tick < from || !truth.TryGetValue(tick, out var world) || !world.TryGetValue(id, out var real)) continue;
                compared++;
                if (pred != real) mismatches.Add($"tick {tick}: predicted {pred}; server {real}");
            }
            var mine = truth.Values.Where(w => w.ContainsKey(id)).Select(w => w[id]).ToList();
            int attacks = mine.Zip(mine.Skip(1)).Count(p => p.Second.MeleeStep == 1 && p.First.MeleeStep != 1);
            int combos = mine.Zip(mine.Skip(1)).Count(p => p.Second.MeleeStep > 1 && p.First.MeleeStep != p.Second.MeleeStep);
            int cancels = mine.Zip(mine.Skip(1)).Count(p => p.First.MeleeStep > 0 && p.Second.MeleeStep == 0 && p.Second.Clip < p.First.Clip);
            output.WriteLine($"client {i}: compared {compared} ticks, {mismatches.Count} mismatches; {attacks} attacks, {combos} combo steps, {cancels} fire cancels");
            foreach (var m in mismatches.Take(3)) output.WriteLine("  " + m);
            Assert.True(compared > 300, $"client {i}: only {compared} ticks compared");
            Assert.True(attacks > 5 && combos > 5 && cancels > 0, $"client {i}: too little melee");
            Assert.Empty(mismatches);
        }
    }
}
