using System.Numerics;
using SmNet;
using SmServer;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// feel/charge-directions (protocol 39; the owner's rules of 2026-10-29): the bull rush starts on F with W held (no sprint), W + A / W + D turn it
/// <see cref="RushSpec.SideSteer"/> (25 degrees, ours) to that side unless it locks onto a target (then along the view, steered as before), and the
/// Assault / Raptor dash attack lunges the stick's way of eight around the view (no stick: along the view). Prediction under 150 ms RTT and 5% loss.
/// Synthetic specs (RushTests' rush, JumpPackAttackTests' dash attack).
/// </summary>
public class ChargeDirectionsTests(ITestOutputHelper output)
{
    const float Deg = MathF.PI / 180f;
    const int Assault = 0, Marine = 1;
    static readonly SimConfig Config = JumpPackAttackTests.Config;
    static readonly DamageSpec Plain = new(0f, 0f, 1f, 1f, 1f, 1f, false);

    static PlayerState Fresh(int cls, float yaw = 0f) => Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, yaw, true) { Class = cls }, Config);
    static PlayerState Step(PlayerState s, InputButtons b, float mx = 0f, float my = 0f, float yaw = 0f) => Combat.StepPlayer(s, new PlayerInput(0, mx, my, yaw, b), Config, out _);
    static float Heading(Vector2 d) => MeleeLock.YawOf(d);
    static float Off(float a, float b) => MeleeLock.Delta(b, a) / Deg;   // a - b in degrees, wrapped

    [Theory]
    [InlineData(0f, 0f)]       // W
    [InlineData(-1f, 25f)]     // W + A: 25 degrees left (yaw grows to the left)
    [InlineData(1f, -25f)]     // W + D: 25 degrees right
    [InlineData(0.5f, -13.31f)] // a stick half right: (0.5 - 0.1) / 0.9 = 0.444 beyond the dead zone, atan2(0.444, 1) = 23.96 of 45 degrees, so 0.532 of 25
    [InlineData(0.1f, 0f)]     // at the dead zone's edge: nothing yet (the offset grows from 0, no step)
    public void A_or_D_with_W_turns_the_charge_by_the_side_steer_and_it_travels_that_way(float stickX, float expected)
    {
        const float view = 0.3f;
        var s = Fresh(Marine);
        for (int k = 0; k < 6; k++) s = Step(s, InputButtons.None, 0f, 1f, view);   // walking, no sprint
        var before = s;
        s = Step(s, InputButtons.Sprint | InputButtons.MeleeHeavy, stickX, 1f, view);
        Assert.Equal(RushPhase.Rush, s.Rush);
        Assert.Equal(expected, Off(s.Yaw, view), 1);
        var start = s.Position;
        for (int k = 0; k < 15; k++) s = Step(s, InputButtons.None, 0f, 0f, view + 1f);   // the view turns: the heading stays
        Assert.Equal(expected, Off(s.Yaw, view), 1);
        var travel = Heading(new Vector2(s.Position.X - start.X, s.Position.Y - start.Y));
        output.WriteLine($"stick x {stickX}: heading {Off(s.Yaw, view):F2} deg from the view, travel {Off(travel, view):F2} deg over {Vector2.Distance(new(s.Position.X, s.Position.Y), new(start.X, start.Y)):F2} m (speed before {before.Velocity.Length():F2} m/s)");
        Assert.Equal(expected, Off(travel, view), 1);
        Assert.Equal(expected * Deg, Rush.StartYaw(new PlayerInput(0, stickX, 1f, view, InputButtons.None), RushTests.Spec) - view, 3);
    }

    [Fact]
    public void The_side_steer_grows_continuously_from_the_dead_zone_edge()
    {
        // Review N1: no step at the dead zone: the offset rises from 0 as the stick leaves it and reaches 25 degrees at a keyboard's full side key.
        float At(float x) => Off(Rush.StartYaw(new PlayerInput(0, x, 1f, 0f, InputButtons.None), RushTests.Spec), 0f);
        Assert.Equal(0f, At(0.05f), 4);
        Assert.Equal(0f, At(0.1f), 4);
        Assert.InRange(-At(0.11f), 0.1f, 0.5f);   // 0.35 degrees just past the edge
        float previous = 0f;
        for (float x = 0.1f; x <= 1.0001f; x += 0.05f) { float now = -At(x); Assert.True(now >= previous - 1e-4f && now - previous < 2.5f, $"x {x}: {now} after {previous}"); previous = now; }
        Assert.Equal(25f, previous, 3);
        Assert.Equal(25f, At(-1f), 3);
    }

    [Fact]
    public void A_locked_charge_starts_along_the_view_and_steers_to_its_target_whatever_the_side_keys()
    {
        // The target stands 5 m ahead and 1.5 m to the right (17 degrees): in the charge's 45 degree cone. W + A with the lock: the charge starts along the
        // view and steers right toward the target (180 degrees a second); without the lock W + A turns it 25 degrees left.
        foreach (bool locked in new[] { true, false })
        {
            var w = new World(Config, null, [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new Vector3(1.5f, 5f, 0), MathF.PI)]) { Rules = new MatchRules { TimeLimit = 0f } };
            w.Spawn(1, Marine, team: Team.SpaceMarines);
            w.Spawn(2, Marine, team: Team.Chaos);
            uint view = w.Tick;
            w.Step(id => id == 1 ? new PlayerInput(0, -1f, 1f, 0f, InputButtons.Sprint | InputButtons.MeleeHeavy, 0f, view) { MeleeTarget = locked ? 2 : 0 } : new PlayerInput(0, 0f, 0f, MathF.PI, InputButtons.None, 0f, view));
            var me = w.Players[1];
            Assert.Equal(new Vector3(1.5f, 5f, 0f), w.Players[2].Position);
            Assert.Equal(RushPhase.Rush, me.Rush);
            float wanted = MeleeLock.Steer(0f, new Vector2(1.5f, 5f), Config) / Deg;
            output.WriteLine($"{(locked ? "locked" : "no lock")}: heading after the press tick {me.Yaw / Deg:F2} deg (steered from the view: {wanted:F2})");
            Assert.Equal(locked ? wanted : 25f, me.Yaw / Deg, 2);
            if (locked) Assert.True(me.Yaw < 0f, "steered right, toward the target");
        }
    }

    static readonly (float X, float Y, float Offset)[] Eight =
    [
        (0f, 1f, 0f), (1f, 1f, -45f), (1f, 0f, -90f), (1f, -1f, -135f), (0f, -1f, 180f), (-1f, -1f, 135f), (-1f, 0f, 90f), (-1f, 1f, 45f), (0f, 0f, 0f),
    ];

    /// <summary>A dash forward from rest, the dash attack pressed in its window with the stick at (<paramref name="x"/>, <paramref name="y"/>) and the view at <paramref name="view"/>, then the lunge to its end.</summary>
    static (PlayerState Press, PlayerState End, Vector3 From) DashAttack(float x, float y, float view)
    {
        var s = Fresh(Assault);
        s = Step(s, InputButtons.Jump, 0f, 1f);
        s = Step(s, InputButtons.None, 0f, 1f);
        while (s.EvadeTicks < 10) s = Step(s, InputButtons.None);
        Assert.True(Rush.CanDashAttack(s, Config, null));
        var from = s.Position;
        s = Step(s, InputButtons.Melee, x, y, view);
        Assert.Equal(RushPhase.DashAttack, s.Rush);
        var press = s;
        var last = s;
        for (int k = 0; k < 60 && s.Rush == RushPhase.DashAttack; k++) { last = s; s = Step(s, InputButtons.None, 0f, 0f, view); }
        return (press, last, from);
    }

    [Fact]
    public void The_dash_attack_lunges_the_sticks_way_of_eight_and_along_the_view_without_a_stick()
    {
        const float view = 0.7f;
        foreach (var (x, y, offset) in Eight)
        {
            var (press, end, from) = DashAttack(x, y, view);
            var d = new Vector2(end.Position.X - from.X, end.Position.Y - from.Y);
            output.WriteLine($"stick ({x}, {y}): heading {Off(press.Yaw, view):F1} deg, lunge travelled {d.Length():F2} m at {Off(Heading(d), view):F2} deg from the view");
            Assert.Equal(offset, MathF.Abs(offset) == 180f ? MathF.Abs(Off(press.Yaw, view)) : Off(press.Yaw, view), 2);
            float travel = Off(Heading(d), press.Yaw);
            Assert.True(MathF.Abs(travel) < 0.5f, $"stick ({x}, {y}): travel {travel:F2} deg off the heading");
            Assert.True(d.Length() > 8f, $"stick ({x}, {y}): only {d.Length():F2} m");
            Assert.Equal(offset * Deg, MeleeLock.Delta(view, Rush.DashYaw(new PlayerInput(0, x, y, view, InputButtons.None))), 3);
        }
    }

    [Fact]
    public void The_config_hash_covers_the_side_steer()
    {
        var other = Config with { Classes = [Config.Classes[0], Config.Classes[1] with { MeleeRush = RushTests.Spec with { SideSteer = 30f } }] };
        Assert.NotEqual(Config.Hash(), other.Hash());
        Assert.Equal(25f, RushTests.Spec.SideSteer);
    }

    [Theory]
    [InlineData(391)]
    [InlineData(392)]
    public void Prediction_matches_the_server_through_side_charges_and_dash_attacks_in_eight_directions_under_150ms_rtt_and_5pct_loss(int seed)
    {
        // A flat floor; the two spawn 200 m apart and never meet (hits are not predicted). The marine walks with W (no sprint) and presses F with W, W + A,
        // W + D in turn; the Assault dashes and presses its dash attack with the stick in each of the eight directions in turn.
        var v = new[] { new Vector3(-2000, -2000, 0), new Vector3(2000, -2000, 0), new Vector3(2000, 2000, 0), new Vector3(-2000, 2000, 0) };
        var mesh = new StaticMesh(v, [0, 1, 2, 0, 2, 3]);
        var collision = new CollisionWorld(mesh, mesh);
        var config = Config with
        {
            Classes = Config.Classes.Select(k => k with { Side = Team.None, MeleeRush = k.MeleeRush is { } r ? r with { Primary = Plain, Secondary = Plain } : null }).ToArray(),
            Weapons = Config.Weapons.Select(w => w with { Damage = 0f }).ToArray(),
        };
        var link = new LinkConditions(TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(10), 0.05);
        var map = new GameMap("flat", collision, [new SpawnPoint(new Vector3(-100, 0, 0), 0f), new SpawnPoint(new Vector3(100, 0, 0), 0f)], 91);
        var h = new Harness(link, seed, 0, config, new ServerOptions { PeerTimeout = TimeSpan.FromMinutes(5) }, map: map);
        int[] classes = [Assault, Marine];
        for (int i = 0; i < classes.Length; i++) h.AddClient(nonce: (uint)(100 + i), options: new ClientOptions { RequestedClass = (byte)classes[i], LoadMap = _ => new LoadedMap(collision, 91) });
        var truth = new SortedDictionary<uint, Dictionary<int, PlayerState>>();
        var predicted = classes.Select(_ => new Dictionary<uint, PlayerState>()).ToArray();
        float AssaultYaw(uint t) => (t / 100) % 4 * MathF.PI / 2f;
        PlayerInput Input(int i, uint t)
        {
            if (classes[i] == Assault)
            {
                uint cycle = t % 100;
                var (x, y, _) = Eight[(int)(t / 100 % 8)];
                var b = cycle == 0 ? InputButtons.Jump : cycle == 13 ? InputButtons.Melee : InputButtons.None;
                return new PlayerInput(t, cycle == 13 ? x : 0f, cycle < 2 ? 1f : cycle == 13 ? y : 0f, AssaultYaw(t), b);
            }
            uint phase = t % 90;
            float side = (t / 90 % 3) switch { 1 => -1f, 2 => 1f, _ => 0f };
            return new PlayerInput(t, phase == 19 ? side : 0f, phase < 20 ? 1f : 0f, 0f, phase == 19 ? InputButtons.Sprint | InputButtons.MeleeHeavy : InputButtons.None);
        }
        h.Run(TimeSpan.FromSeconds(30), Input, () =>
        {
            truth[h.Server.World.Tick] = new Dictionary<int, PlayerState>(h.Server.World.Players);
            for (int i = 0; i < classes.Length; i++)
                if (h.Clients[i].Status == ClientStatus.Connected) predicted[i][h.Clients[i].Tick] = h.Clients[i].Predicted;
        });
        var chargeOffsets = new List<float>();
        var dashOffsets = new List<float>();
        for (int i = 0; i < classes.Length; i++)
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
            foreach (var (tick, world) in truth)
            {
                if (!world.TryGetValue(id, out var p)) continue;
                if (p.Rush == RushPhase.Rush && p.RushTicks == 1) chargeOffsets.Add(MathF.Round(Off(p.Yaw, 0f)) + 0f);
                if (p.Rush == RushPhase.DashAttack && p.RushTicks == 1) dashOffsets.Add(MathF.Round(Off(p.Yaw, AssaultYaw(tick - 1))) + 0f);
            }
            int corrections = c.CorrectionErrors.Count(e => e > 1e-4f);
            output.WriteLine($"client {i}: compared {compared} ticks, {mismatches.Count} mismatches, {corrections} corrections");
            foreach (var m in mismatches.Take(3)) output.WriteLine("  " + m);
            Assert.True(compared > 300, $"client {i}: only {compared} ticks compared");
            Assert.Empty(mismatches);
        }
        output.WriteLine($"charge headings from the view: {string.Join(", ", chargeOffsets)}; dash attack headings: {string.Join(", ", dashOffsets)}");
        Assert.Superset(new HashSet<float> { 0f, 25f, -25f }, chargeOffsets.ToHashSet());
        Assert.True(dashOffsets.Select(o => o == -180f ? 180f : o).Distinct().Count() >= 6, $"only {dashOffsets.Distinct().Count()} dash attack directions");
    }

    // ---------------------------------------------------------------- the world (review S2)

    static readonly MeleeStep LightStep = new(1f, 0.2f, 0.4f, 0.6f, 0.5f, 0.7f, 10f, 5f, 3.5f);
    static readonly MeleeStep HeavyStep = new(1.2f, 0.3f, 0f, 0f, 0.8f, 0.9f, 20f, 5f, 3.5f) { PrimaryReaction = new(0f, ReactionKind.Stun) };
    static readonly WeaponSpec Rifle = new("rifle", WeaponSlot.Ranged, TriggerKind.FullAuto, 1f, 0.1f, 30, 270, 1.0f, 250f, 45f, 70f, 0.8f, 2.5f, 1f, 0.5f, 0.5f);
    static readonly WeaponSpec Braceable = Rifle with { Name = "braceable", Brace = new BraceSpec(0.1f, 1.8f) { EnterTime = 1.433f, ExitTime = 1.3f }, AlwaysReady = true };
    const int RMarine = 0, RChaos = 1, RBrace = 2, ROrk = 3;
    static readonly BodySpec OrkBody = new("ork - boy (test)", 5f, 6.5f, 4.5f, 8f, 3f, 3f, 3f, 2f, 120f, 5f, 5f, 50f, 7f, 33f);
    /// <summary>A marine and a Chaos marine with the knife-like steps, the stun strike and RushTests' rush (10 m/s, 1.2 s, contacts to 0.6 s); a braced-gun marine; an Ork Boy with its own body.</summary>
    static readonly SimConfig Rules = new()
    {
        Weapons = [Rifle, Braceable],
        Classes =
        [
            new ClassLoadout("marine", 0, -1, [LightStep]) { MeleeHeavy = [HeavyStep], MeleeRush = RushTests.Spec, Side = Team.SpaceMarines },
            new ClassLoadout("chaos", 0, -1, [LightStep]) { MeleeHeavy = [HeavyStep], MeleeRush = RushTests.Spec, Side = Team.Chaos },
            new ClassLoadout("brace", 1, -1, [LightStep]) { MeleeHeavy = [HeavyStep], MeleeRush = RushTests.Spec, Side = Team.SpaceMarines },
            new ClassLoadout("ork - boy", 0, -1, [LightStep]) { MeleeHeavy = [HeavyStep], MeleeRush = RushTests.Spec, Race = Race.Orks, Body = 0 },
        ],
        Bodies = [OrkBody],
        Extras = ContentExtras.Orks,
        RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f,
        HealthMax = 1000f, ShieldMax = 100f, HealthRegenDelay = 1e6f, ShieldRegenDelay = 1e6f,
    };

    static World Teams(SimConfig c, params (int Class, Team Team, Vector3 At)[] players)
    {
        var w = new World(c, null, players.Select(p => new SpawnPoint(p.At, 0f)).ToArray()) { Rules = new MatchRules { TimeLimit = 0f } };
        for (int i = 0; i < players.Length; i++) w.Spawn(i + 1, players[i].Class, team: players[i].Team);
        for (int i = 0; i < players.Length; i++) Assert.Equal(players[i].At, w.Players[i + 1].Position);
        return w;
    }

    [Theory]
    [InlineData(true)]    // the target 25 degrees left of the view: W + A reaches it
    [InlineData(false)]   // the target straight ahead: W + A passes it by
    public void A_W_plus_A_charge_connects_with_a_target_25_degrees_left_and_misses_one_straight_ahead(bool left)
    {
        var at = left ? new Vector3(-5f * MathF.Sin(25f * Deg), 5f * MathF.Cos(25f * Deg), 0f) : new Vector3(0f, 5f, 0f);
        var w = Teams(Rules, (RMarine, Team.SpaceMarines, Vector3.Zero), (RChaos, Team.Chaos, at));
        var rushes = new List<RushEvent>();
        var reactions = new List<ReactionEvent>();
        var phases = new List<RushPhase>();
        for (int k = 0; k < 45; k++)
        {
            int tick = k;
            uint view = w.Tick;
            // A walk with W (no sprint) for 5 ticks, then F with W + A (no lock: MeleeTarget 0), then nothing.
            w.Step(id => (id == 1
                ? new PlayerInput(0, tick == 5 ? -1f : 0f, tick <= 5 ? 1f : 0f, 0f, tick == 5 ? InputButtons.Sprint | InputButtons.MeleeHeavy : InputButtons.None)
                : new PlayerInput(0, 0f, 0f, MathF.PI, InputButtons.None)) with { ViewTick = view });
            rushes.AddRange(w.LastRushes);
            reactions.AddRange(w.LastReactions);
            phases.Add(w.Players[1].Rush);
        }
        Assert.Contains(RushPhase.Rush, phases);
        output.WriteLine($"target at {at}: {rushes.Count} contacts; charger ended at {w.Players[1].Position}, target shield {w.Players[2].Shield}");
        if (left)
        {
            var e = Assert.Single(rushes);
            Assert.Equal((1, 2, true), (e.Player, e.Target, e.Harmed));
            Assert.Contains(reactions, r => r.Target == 2 && r.Kind == ReactionKind.Stun);
        }
        else
        {
            Assert.Empty(rushes);
            Assert.Equal((100f, ReactionKind.None), (w.Players[2].Shield, w.Players[2].Reaction));
            Assert.DoesNotContain(RushPhase.Collision, phases);
        }
    }

    /// <summary>
    /// The ticks of a dash (the stick at (<paramref name="dashX"/>, <paramref name="dashY"/>) for its first two ticks, the view along +Y) and the dash attack
    /// pressed in its window with the stick at (<paramref name="x"/>, <paramref name="y"/>).
    /// </summary>
    static PlayerInput DashInput(int tick, bool press, float x, float y, float dashX = 0f, float dashY = 1f) =>
        new(0, tick <= 1 ? dashX : press ? x : 0f, tick <= 1 ? dashY : press ? y : 0f, 0f, (tick == 0 ? InputButtons.Jump : InputButtons.None) | (press ? InputButtons.Melee : InputButtons.None));

    /// <summary>Where the dash attack's press happens for an Assault at the origin dashing along +Y (a dry run on <paramref name="world"/>).</summary>
    static Vector3 PressPoint(CollisionWorld? world, float dashX = 0f, float dashY = 1f)
    {
        var s = Fresh(Assault);
        for (int k = 0; k < 60; k++)
        {
            if (Rush.CanDashAttack(s, Config, world)) return s.Position;
            s = Combat.StepPlayer(s, DashInput(k, false, 0f, 0f, dashX, dashY), Config, out _, world);
        }
        throw new InvalidOperationException("the dash attack's window never opened");
    }

    [Fact]
    public void A_backward_dash_attack_hits_a_target_behind_and_not_one_in_front()
    {
        var p = PressPoint(null);
        var behind = new Vector3(p.X, p.Y - 8f, 0f);   // 2.5 m behind the start (the lunge runs 10 m back from the press)
        var ahead = new Vector3(p.X, p.Y + 6f, 0f);
        var w = new World(Config, null, [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(behind, 0f), new SpawnPoint(ahead, MathF.PI)]);
        w.Spawn(1, Assault); w.Spawn(2, Marine); w.Spawn(3, Marine);
        Assert.Equal((Vector3.Zero, behind, ahead), (w.Players[1].Position, w.Players[2].Position, w.Players[3].Position));
        var hits = new List<HitEvent>();
        var reactions = new List<ReactionEvent>();
        bool pressed = false;
        for (int k = 0; k < 70; k++)
        {
            int tick = k;
            uint view = w.Tick;
            var me = w.Players[1];
            bool press = !pressed && Rush.CanDashAttack(me, Config, null);
            pressed |= press;
            w.Step(id => (id == 1 ? DashInput(tick, press, 0f, -1f) : new PlayerInput(0, 0f, 0f, 0f, InputButtons.None)) with { ViewTick = view });
            if (press) Assert.Equal(180f, MathF.Abs(Off(w.Players[1].Yaw, 0f)), 2);
            hits.AddRange(w.LastHits.Where(h => h.Shooter == 1));
            reactions.AddRange(w.LastReactions);
        }
        Assert.True(pressed);
        output.WriteLine($"press at {p}; hits {string.Join(", ", hits.Select(h => $"P{h.Target} {h.ShieldDamage + h.HealthDamage}"))}");
        Assert.Contains(hits, h => h.Target == 2);
        Assert.DoesNotContain(hits, h => h.Target == 3);
        Assert.Contains(reactions, r => r.Target == 2 && r.Kind == ReactionKind.Stun);
        Assert.DoesNotContain(reactions, r => r.Target == 3);
        Assert.Equal(100f, w.Players[3].Shield);
    }

    static CollisionWorld Boxes(params (Vector3 Min, Vector3 Max)[] boxes)
    {
        var v = new List<Vector3>(); var idx = new List<int>();
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) { int n = v.Count; v.AddRange([a, b, c, d]); idx.AddRange([n, n + 1, n + 2, n, n + 2, n + 3]); }
        foreach (var (min, max) in boxes)
        {
            Vector3 P(int x, int y, int z) => new(x == 0 ? min.X : max.X, y == 0 ? min.Y : max.Y, z == 0 ? min.Z : max.Z);
            Quad(P(0, 0, 0), P(1, 0, 0), P(1, 1, 0), P(0, 1, 0)); Quad(P(0, 0, 1), P(0, 1, 1), P(1, 1, 1), P(1, 0, 1));
            Quad(P(0, 0, 0), P(0, 0, 1), P(1, 0, 1), P(1, 0, 0)); Quad(P(0, 1, 0), P(1, 1, 0), P(1, 1, 1), P(0, 1, 1));
            Quad(P(0, 0, 0), P(0, 1, 0), P(0, 1, 1), P(0, 0, 1)); Quad(P(1, 0, 0), P(1, 0, 1), P(1, 1, 1), P(1, 1, 0));
        }
        var mesh = new StaticMesh(v.ToArray(), idx.ToArray());
        return new CollisionWorld(mesh, mesh);
    }

    [Theory]
    [InlineData(1f, 0f)]    // D after a forward dash: the lunge goes +X, into a wall there
    [InlineData(0f, -1f)]   // S after a dash to the right (so the wall behind is off the dash's path): the lunge goes back (-Y), into it
    public void A_sideways_or_backward_dash_attack_stops_at_a_wall(float x, float y)
    {
        var floor = (new Vector3(-100f, -100f, -1f), new Vector3(100f, 100f, 0f));
        var (dashX, dashY) = y < 0f ? (1f, 0f) : (0f, 1f);
        var p = PressPoint(Boxes(floor), dashX, dashY);
        // A slab 4 m from the press point along the lunge, wide across it.
        var wall = x > 0f ? (new Vector3(p.X + 4f, -50f, 0f), new Vector3(p.X + 5f, 50f, 6f)) : (new Vector3(-50f, p.Y - 5f, 0f), new Vector3(50f, p.Y - 4f, 6f));
        var world = Boxes(floor, wall);
        var s = Fresh(Assault);
        var phases = new List<RushPhase>();
        bool pressed = false;
        for (int k = 0; k < 70; k++)
        {
            bool press = !pressed && Rush.CanDashAttack(s, Config, world);
            pressed |= press;
            s = Combat.StepPlayer(s, DashInput(k, press, x, y, dashX, dashY), Config, out _, world);
            phases.Add(s.Rush);
            if (x > 0f) Assert.True(s.Position.X <= p.X + 4f - Config.CapsuleRadius + 0.02f, $"through the wall: {s.Position}");
            else Assert.True(s.Position.Y >= p.Y - 4f + Config.CapsuleRadius - 0.02f, $"through the wall: {s.Position}");
        }
        int first = phases.IndexOf(RushPhase.DashAttack), stop = phases.IndexOf(RushPhase.DashCollision);
        output.WriteLine($"stick ({x}, {y}): lunge from tick {first}, wall stop at tick {stop}, ended at {s.Position}");
        Assert.True(first >= 0, "the dash attack started");
        Assert.True(stop > first, "the wall stopped the lunge");
        Assert.DoesNotContain(RushPhase.DashStop, phases.Take(stop));
    }

    [Fact]
    public void W_and_F_inside_the_charge_spacing_is_lost_and_not_the_stun_strike()
    {
        var w = Teams(Rules, (RMarine, Team.SpaceMarines, Vector3.Zero), (RChaos, Team.Chaos, new Vector3(40f, 0f, 0f)));
        PlayerInput Mine(int k, InputButtons b) => new(0, 0f, 1f, 0f, b);
        w.Step(id => id == 1 ? Mine(0, InputButtons.Sprint | InputButtons.MeleeHeavy) : new PlayerInput(0, 0f, 0f, 0f, InputButtons.None));
        Assert.Equal(RushPhase.Rush, w.Players[1].Rush);
        // sim/combat-2 (item 31): W no longer ends the rush; a sideways stick (D) does, at its MoveAt (0.6 s); the 1 s spacing still runs.
        int k = 1;
        while (w.Players[1].Rush != RushPhase.None && k < 60) { w.Step(id => id == 1 ? new PlayerInput(0, 1f, 0f, 0f, InputButtons.None) : new PlayerInput(0, 0f, 0f, 0f, InputButtons.None)); k++; }
        var before = w.Players[1];
        Assert.Equal(RushPhase.None, before.Rush);
        Assert.True(before.RushLock > 1, $"spacing left {before.RushLock}");
        w.Step(id => id == 1 ? Mine(k, InputButtons.Sprint | InputButtons.MeleeHeavy) : new PlayerInput(0, 0f, 0f, 0f, InputButtons.None));
        var after = w.Players[1];
        output.WriteLine($"rush over after {k} ticks, spacing left {before.RushLock}: W + F gives {after.Rush}, melee step {after.MeleeStep}, heavy {after.MeleeHeavy}");
        // sim/combat-pass (item 8, the owner's decision of 2026-10-30): W + F is purely the charge; inside the spacing the press is lost, never the stun strike.
        Assert.Equal((RushPhase.None, 0, false), (after.Rush, after.MeleeStep, after.MeleeHeavy));
    }

    [Fact]
    public void A_mashed_F_during_a_charge_is_not_replayed_as_a_stun_strike_when_it_ends_within_the_spacing()
    {
        // With the owner's 0.3 s input buffer: F pressed again and again during the rush (W held) is kept and replayed on the tick the rush ends; the spacing
        // still runs then, so the replay is the stun strike (spec "Charge directions", row "Charge spacing and a mashed F").
        var c = Rules with { InputBufferWindow = 0.3f };
        var s = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = RMarine }, c);
        s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 1f, 0f, InputButtons.Sprint | InputButtons.MeleeHeavy), c, out _);
        Assert.Equal(RushPhase.Rush, s.Rush);
        int k = 1;
        while (s.Rush != RushPhase.None && k < 60) { s = Combat.StepPlayer(s, k % 2 == 0 ? new PlayerInput(0, 0f, 1f, 0f, InputButtons.Sprint | InputButtons.MeleeHeavy) : new PlayerInput(0, 1f, 0f, 0f, InputButtons.None), c, out _); k++; }   // sim/combat-2: W + F on the even ticks, D (which ends the rush at its stick branch) on the odd ones
        output.WriteLine($"rush over after {k} ticks: step {s.MeleeStep}, heavy {s.MeleeHeavy}, spacing left {s.RushLock}");
        // sim/combat-pass (item 8): W is held, so the replay is no stun strike either (it was one before protocol 45).
        Assert.Equal((RushPhase.None, 0, false), (s.Rush, s.MeleeStep, s.MeleeHeavy));
        Assert.True(s.RushLock > 0);
    }

    [Fact]
    public void W_and_F_in_a_reaction_or_a_brace_start_nothing()
    {
        // A stun: the presses are lost (no charge, no strike) until it ends.
        var s = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = RMarine }, Rules);
        s = Reaction.Start(s, Rules, new ReactionEntry(0f, ReactionKind.Stun), ReactionDirection.Front, Vector2.UnitY);
        for (int k = 0; k < 6; k++)
        {
            s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 1f, 0f, k % 2 == 0 ? InputButtons.Sprint | InputButtons.MeleeHeavy : InputButtons.None), Rules, out _);
            Assert.Equal((ReactionKind.Stun, RushPhase.None, 0), (s.Reaction, s.Rush, s.MeleeStep));
        }
        // The brace (World): Reload enters it; W + F in the entry and braced does nothing (the stance takes the press).
        var w = Teams(Rules, (RBrace, Team.SpaceMarines, Vector3.Zero), (RChaos, Team.Chaos, new Vector3(40f, 0f, 0f)));
        var phases = new HashSet<BracePhase>();
        for (int k = 0; k < 70; k++)
        {
            int tick = k;
            var b = tick == 0 ? InputButtons.Reload : tick % 2 == 1 ? InputButtons.MeleeHeavy : InputButtons.None;
            w.Step(id => id == 1 ? new PlayerInput(0, 0f, tick == 0 ? 0f : 1f, 0f, b) : new PlayerInput(0, 0f, 0f, 0f, InputButtons.None));
            var me = w.Players[1];
            if (tick > 0) Assert.NotEqual(BracePhase.None, me.Brace);
            Assert.Equal((RushPhase.None, 0), (me.Rush, me.MeleeStep));
            phases.Add(me.Brace);
        }
        Assert.Superset(new HashSet<BracePhase> { BracePhase.Entering, BracePhase.Braced }, phases);
    }

    [Fact]
    public void The_ork_charges_from_a_standstill_with_W_and_F()
    {
        var w = Teams(Rules, (ROrk, Team.Chaos, Vector3.Zero), (RMarine, Team.SpaceMarines, new Vector3(40f, 0f, 0f)));
        Assert.Equal((ROrk, Race.Orks), (w.Players[1].Class, Races.Of(Rules, w.Players[1].Class, w.Players[1].Team)));
        Assert.Equal(Vector3.Zero, w.Players[1].Velocity);
        w.Step(id => id == 1 ? new PlayerInput(0, 0f, 1f, 0.4f, InputButtons.Sprint | InputButtons.MeleeHeavy) : new PlayerInput(0, 0f, 0f, 0f, InputButtons.None));
        var me = w.Players[1];
        Assert.Equal((RushPhase.Rush, 0.4f), (me.Rush, me.Yaw));
        var start = me.Position;
        for (int k = 0; k < 10; k++) w.Step(id => new PlayerInput(0, 0f, 0f, id == 1 ? 1.5f : 0f, InputButtons.None));
        var d = new Vector2(w.Players[1].Position.X - start.X, w.Players[1].Position.Y - start.Y);
        output.WriteLine($"Ork charge from rest: {d.Length():F2} m in 10 ticks at {Off(MeleeLock.YawOf(d), 0.4f):F2} deg from its heading");
        Assert.Equal(10f * 10f / 30f, d.Length(), 2);   // the clip's 10 m/s, whatever the Ork body's run speed (5 m/s)
        Assert.Equal(0f, Off(MeleeLock.YawOf(d), 0.4f), 1);
    }

    // ---------------------------------------------------------------- sim/combat-2 (item 31)

    [Fact]
    public void W_held_through_the_charge_does_not_end_it_a_sideways_stick_does()
    {
        int RushTicks(float mx, float my)
        {
            var s = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = RMarine }, Rules);
            s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 1f, 0f, InputButtons.Sprint | InputButtons.MeleeHeavy), Rules, out _);
            Assert.Equal(RushPhase.Rush, s.Rush);
            int k = 1;
            while (s.Rush == RushPhase.Rush && k < 80) { s = Combat.StepPlayer(s, new PlayerInput(0, mx, my, 0f, InputButtons.None), Rules, out _); k++; }
            return k;
        }
        int withW = RushTicks(0f, 1f), withD = RushTicks(1f, 0f), withNothing = RushTicks(0f, 0f), withS = RushTicks(0f, -1f);
        output.WriteLine($"rush ticks: W {withW}, nothing {withNothing}, D {withD}, S {withS}");
        Assert.Equal(withNothing, withW);   // W is as good as no input: the charge runs to its own end
        Assert.True(withD < withW && withS < withW);
    }
}
