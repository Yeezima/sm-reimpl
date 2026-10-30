using System.Numerics;
using SmNet;
using SmServer;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// M44 (the owner's playtest of 2026-10-28): the Assault / Raptor small hop is the jump-pack dash (a tap; a hold still jumps), in eight
/// directions; the ground pound's landing prediction; the pound's stun in its whole radius; client prediction against the server.
/// Synthetic specs shaped like the install's values (the dash clip: about 12 m along and 1.7 m up, back down at 0.733 s).
/// </summary>
public class JumpPackDashTests(ITestOutputHelper output)
{
    const int Assault = 0, Tactical = 1;
    const float Dt = 1f / 30f;

    /// <summary>A flight shaped like <c>SM_JMP_Dash_F</c>: 16.4 m/s along, a parabola peaking at 1.7 m at 0.367 s, on below the take-off after 0.733 s; 1.3 s.</summary>
    static EvadeMove Flight(string name, Vector2 axis)
    {
        const int samples = 40;   // 0 .. 1.3 s at 30 per s
        var path = new Vector2[samples]; var height = new float[samples];
        for (int k = 0; k < samples; k++)
        {
            float t = k * Dt, u = t / 0.7333f;
            path[k] = axis * (16.37f * t);
            height[k] = 4f * 1.7f * u * (1f - u);
        }
        var landing = new EvadeMove(name + "_land", Enumerable.Range(0, 31).Select(k => axis * (3f * MathF.Sin(k / 30f * MathF.PI / 2f))).ToArray(),
            1f, 0.25f, 0.38f, 1f, 1f, 0.5f) { JumpAt = 0f };
        return new EvadeMove(name, path, (samples - 1) * Dt, (samples - 1) * Dt, (samples - 1) * Dt, (samples - 1) * Dt, (samples - 1) * Dt, (samples - 1) * Dt)
        { Height = height, Flight = true, Landing = landing };
    }

    internal static readonly EvadeSpec Dash = new("jump_evade", Flight("F", Vector2.UnitY), Flight("B", -Vector2.UnitY), Flight("L", -Vector2.UnitX), Flight("R", Vector2.UnitX))
    {
        MinInterval = 0.8f, WallHitAngle = 25f, AnimationSet = "jump_evade", Camera = "evade_jumppack_default_mp", Diagonals = true, Dash = true,
        ToJumpAt = 5f / 30f, TargetableModeTime = 1.44f, CollisionModeTime = 2.03f,
    };

    /// <summary>The M14 pack with the M44 capacity (two full jumps: 2 × 55).</summary>
    static readonly JumpPackSpec Pack = JumpPackTests.Pack with { MaxPower = 110f };

    internal static readonly SimConfig Config = JumpPackTests.Config with
    {
        Classes = [JumpPackTests.Config.Classes[0] with { Evade = 0 }, JumpPackTests.Config.Classes[1]],
        JumpPacks = [Pack],
        Evades = [Dash],
    };

    static PlayerState Fresh(SimConfig? c = null, int cls = Assault, Vector3 at = default, float yaw = 0f) =>
        Combat.Respawned(new PlayerState(1, at, Vector3.Zero, yaw, true) { Class = cls }, c ?? Config);

    static SelfEvents Step(ref PlayerState s, InputButtons b, float mx = 0f, float my = 0f, float yaw = 0f, float pitch = 0f, CollisionWorld? world = null, SimConfig? c = null)
    {
        s = Combat.StepPlayer(s, new PlayerInput(0, mx, my, yaw, b, pitch), c ?? Config, out var e, world);
        return e;
    }

    static CollisionWorld Mesh(params (Vector3 A, Vector3 B, Vector3 C, Vector3 D)[] quads)
    {
        var v = new List<Vector3>(); var idx = new List<int>();
        foreach (var (a, b, c, d) in quads) { int n = v.Count; v.AddRange([a, b, c, d]); idx.AddRange([n, n + 1, n + 2, n, n + 2, n + 3]); }
        var mesh = new StaticMesh(v.ToArray(), idx.ToArray());
        return new CollisionWorld(mesh, mesh);
    }

    static (Vector3, Vector3, Vector3, Vector3) Floor(float z, float x0 = -300, float x1 = 300, float y0 = -300, float y1 = 300) =>
        (new(x0, y0, z), new(x1, y0, z), new(x1, y1, z), new(x0, y1, z));

    // ---------------------------------------------------------------- the tap is the dash

    [Theory]
    [InlineData(0f, 1f, EvadeDirection.Forward)]       // W: north (facing)
    [InlineData(0f, -1f, EvadeDirection.Back)]         // S: south
    [InlineData(-1f, 0f, EvadeDirection.Left)]         // A: west
    [InlineData(1f, 0f, EvadeDirection.Right)]         // D: east
    [InlineData(1f, 1f, EvadeDirection.ForwardRight)]  // W+D: north-east
    [InlineData(-1f, 1f, EvadeDirection.ForwardLeft)]  // W+A: north-west
    [InlineData(1f, -1f, EvadeDirection.BackRight)]    // S+D: south-east
    [InlineData(-1f, -1f, EvadeDirection.BackLeft)]    // A+S: south-west
    public void A_tap_dashes_in_eight_directions_relative_to_the_facing(float mx, float my, EvadeDirection expected)
    {
        const float yaw = 0.7f;
        var s = Fresh(yaw: yaw);
        var start = s.Position;
        Step(ref s, InputButtons.Jump, mx, my, yaw);   // press: the take-off waits for the decision
        Assert.Equal(JumpPhase.TakeOff, s.Jump);
        Step(ref s, InputButtons.None, mx, my, yaw);   // released before 0.167 s: the dash
        Assert.True(Evade.Evading(s));
        Assert.Equal(expected, s.EvadeDirection);
        Assert.Equal(JumpPhase.None, s.Jump);
        for (int k = 0; k < 8; k++) Step(ref s, InputButtons.None, 0f, 0f, yaw);   // stick released: the flight keeps its line
        var moved = new Vector2(s.Position.X - start.X, s.Position.Y - start.Y);
        var forward = new Vector2(-MathF.Sin(yaw), MathF.Cos(yaw));
        var right = new Vector2(forward.Y, -forward.X);
        var wanted = Vector2.Normalize(right * mx + forward * my);
        output.WriteLine($"{expected}: moved {moved}, wanted direction {wanted}, body yaw {s.Yaw:F3}");
        Assert.True(Vector2.Dot(Vector2.Normalize(moved), wanted) > 0.995f, $"moved {moved}, wanted {wanted}");
        Assert.True(moved.Length() > 4f);
    }

    [Fact]
    public void The_dash_is_mostly_horizontal_with_a_small_rise_and_lands_into_its_landing_part()
    {
        var s = Fresh();
        Step(ref s, InputButtons.Jump, 0f, 1f);
        float apex = 0f; int flight = 0;
        Vector3 landedAt = default;
        for (int k = 0; k < 60; k++)
        {
            var e = Step(ref s, InputButtons.None, 0f, k < 1 ? 1f : 0f);
            Assert.Equal(JumpEventKind.None, e.Jump);   // never a lift-off
            Assert.Equal(JumpPhase.None, s.Jump);
            apex = MathF.Max(apex, s.Position.Z);
            if (!s.EvadeLanded) flight++;
            else { landedAt = s.Position; break; }
        }
        output.WriteLine($"apex {apex:F2} m, landed after {flight} ticks at {landedAt}");
        Assert.True(s.EvadeLanded);
        Assert.Equal(1, s.EvadeTicks);   // the landing part starts at tick 1 (clip time 0)
        Assert.InRange(apex, 1.6f, 1.75f);
        Assert.InRange(landedAt.Y, 11.5f, 12.5f);
        Assert.True(landedAt.Y / apex > 6f);   // mostly horizontal
        Assert.Equal(0f, landedAt.Z, 3);
        Assert.Equal(110f, s.JumpPower);   // a dash costs no power (the data's jump evade has none)
        // The landing slides on the clip and returns control at its BRANCH_Run (0.25 s) to the stick.
        float y = s.Position.Y;
        Step(ref s, InputButtons.None);
        Assert.True(s.Position.Y > y && s.Grounded && s.EvadeLanded);
        for (int k = 0; k < 7; k++) Step(ref s, InputButtons.None);
        Step(ref s, InputButtons.None, 1f, 0f);   // 0.27 s into the landing: the stick ends it
        Assert.False(Evade.Evading(s));
        Assert.False(s.EvadeLanded);
    }

    [Fact]
    public void Holding_past_the_decision_jumps_as_before_and_spends_the_power_then()
    {
        var s = Fresh();
        Step(ref s, InputButtons.Jump);
        for (int k = 1; k < 5; k++) { Step(ref s, InputButtons.Jump); Assert.Equal((JumpPhase.TakeOff, 110f), (s.Jump, s.JumpPower)); }
        Step(ref s, InputButtons.Jump);   // 5 ticks = 0.167 s: committed, a zero-to-0.167 s hold costs 35
        Assert.Equal((JumpPhase.TakeOff, 75f), (s.Jump, s.JumpPower));
        var e = Step(ref s, InputButtons.Jump);   // 0.2 s: lift-off
        Assert.Equal(JumpEventKind.Launch, e.Jump);
        Assert.False(Evade.Evading(s));
        for (int k = 0; k < 12; k++) Step(ref s, InputButtons.Jump);
        Assert.Equal(110f - 55f, s.JumpPower, 3);   // a full hold: 55
        // A release one tick before the decision is still a dash.
        var t = Fresh();
        for (int k = 0; k < 4; k++) Step(ref t, InputButtons.Jump);
        Step(ref t, InputButtons.None);
        Assert.True(Evade.Evading(t));
        Assert.Equal(110f, t.JumpPower);
    }

    [Fact]
    public void A_press_during_the_flight_is_lost_and_one_in_the_landing_part_starts_a_take_off()
    {
        var s = Fresh();
        Step(ref s, InputButtons.Jump);
        Step(ref s, InputButtons.None);
        Step(ref s, InputButtons.Jump);   // in the flight: nothing
        Assert.Equal(JumpPhase.None, s.Jump);
        Assert.True(Evade.Evading(s));
        for (int k = 0; k < 60 && !s.EvadeLanded; k++) Step(ref s, InputButtons.None);
        Assert.True(s.EvadeLanded);
        Step(ref s, InputButtons.Jump);   // BRANCH_Evade_to_Jump from 0 in the landing
        Assert.Equal(JumpPhase.TakeOff, s.Jump);
        Assert.False(Evade.Evading(s));
    }

    [Fact]
    public void The_dash_respects_the_spawn_room_blockers_and_walls()
    {
        // A wall 4 m ahead: the dash stops at it (a head-on wall ends the evade).
        var wall = Mesh(Floor(0f), (new(-50, 4f, -1), new(50, 4f, -1), new(50, 4f, 10), new(-50, 4f, 10)));
        var s = Fresh(at: new Vector3(0, 0, 0));
        Step(ref s, InputButtons.Jump, world: wall);
        Step(ref s, InputButtons.None, world: wall);
        for (int k = 0; k < 40; k++) Step(ref s, InputButtons.None, world: wall);
        output.WriteLine($"after the wall: {s.Position}, evading {Evade.Evading(s)}");
        Assert.True(s.Position.Y < 4f - Config.CapsuleRadius + 0.05f);
    }

    [Fact]
    public void A_dash_off_a_ledge_follows_the_clip_down_and_ends_in_a_fall_at_the_flights_end()
    {
        // A 1 m wide ledge at 20 m: the flight goes on below the take-off and, not grounded by 1.3 s, ends; gravity takes over.
        var world = Mesh(Floor(20f, -5, 5, -5, 1), Floor(0f));
        var s = Fresh(at: new Vector3(0, 0, 20f));
        Step(ref s, InputButtons.Jump, world: world);
        Step(ref s, InputButtons.None, world: world);
        int ticks = 0;
        while (Evade.Evading(s) && !s.EvadeLanded && ticks < 80) { Step(ref s, InputButtons.None, world: world); ticks++; }
        output.WriteLine($"flight ended after {ticks} ticks at {s.Position}, landed {s.EvadeLanded}");
        Assert.False(s.EvadeLanded);
        Assert.False(Evade.Evading(s));
        Assert.InRange(ticks, 37, 39);
        for (int k = 0; k < 200 && !s.Grounded; k++) Step(ref s, InputButtons.None, world: world);
        Assert.True(s.Grounded);
        Assert.Equal(0f, s.Position.Z, 2);
    }

    // ---------------------------------------------------------------- the pound's landing prediction

    [Theory]
    [InlineData(0f, 0f)]       // level aim: the dive leans 50° from straight down
    [InlineData(-1.4f, 0.3f)]  // nearly straight down
    [InlineData(-0.4f, 2.0f)]  // another heading
    public void The_landing_prediction_is_the_impact_the_simulation_finds(float pitch, float yaw)
    {
        // Floor at 0 with a 3 m block ahead: the predicted point is where the dive really lands, on the floor or the block.
        var world = Mesh(Floor(0f), Floor(3f, -20, 20, 10, 30), (new(-20, 10, 0), new(20, 10, 0), new(20, 10, 3), new(-20, 10, 3)));
        var s = Fresh();
        Step(ref s, InputButtons.Jump, world: world);
        for (int k = 0; k < 20; k++) Step(ref s, InputButtons.Jump, world: world);   // a full jump, still rising
        Assert.True(JumpPack.InFlight(s.Jump));
        Assert.True(JumpPack.CanPound(s, Pack, Config));
        var predicted = JumpPack.PoundLanding(s, yaw, pitch, Pack, Config, world);
        Assert.NotNull(predicted);
        JumpEventKind e = JumpEventKind.None;
        Step(ref s, InputButtons.Melee, yaw: yaw, pitch: pitch, world: world);
        Assert.Equal(JumpPhase.Dive, s.Jump);
        for (int k = 0; k < 300 && e != JumpEventKind.Impact; k++) e = Step(ref s, InputButtons.None, yaw: yaw, pitch: pitch, world: world).Jump;
        output.WriteLine($"pitch {pitch}, yaw {yaw}: predicted {predicted}, impact {s.Position}");
        Assert.Equal(JumpEventKind.Impact, e);
        Assert.True(Vector3.Distance(predicted!.Value, s.Position) < 1e-3f);
    }

    [Fact]
    public void The_prediction_is_offered_only_while_a_pound_can_start_and_is_null_over_the_void()
    {
        var s = Fresh();
        Assert.False(JumpPack.CanPound(s, Pack, Config));   // on the ground
        Step(ref s, InputButtons.Jump);
        for (int k = 0; k < 4; k++) Step(ref s, InputButtons.Jump);
        Assert.False(JumpPack.CanPound(s, Pack, Config));   // the take-off
        Assert.False(JumpPack.CanPound(s, Pack with { PoundEnabled = false }, Config));
        // A small platform at 0 under a void: diving away from it finds no ground within the look-ahead.
        var world = Mesh(Floor(0f, -2, 2, -2, 2));
        var air = Fresh(at: new Vector3(0, 0, 0)) with { Position = new Vector3(0, 0, 10f), Grounded = false, Jump = JumpPhase.Fall, JumpTicks = 30 };
        Assert.True(JumpPack.CanPound(air, Pack, Config));
        Assert.NotNull(JumpPack.PoundLanding(air, 0f, -MathF.PI / 2f, Pack, Config, world, 120));
        Assert.Null(JumpPack.PoundLanding(air, 0f, 0f, Pack, Config, world, 120));
    }

    // ---------------------------------------------------------------- the pound stuns in its whole radius (owner's request)

    [Fact]
    public void A_pound_whose_blast_stuns_from_intensity_zero_stuns_every_enemy_it_damages_but_not_beyond_its_radius()
    {
        var pound = JumpPackTests.PoundBlast with { Reactions = [new ReactionEntry(0f, ReactionKind.Stun)] };
        var c = Config with { JumpPacks = [Pack with { Pound = pound }] };
        var world = new World(c, null, [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new Vector3(6.5f, 0, 0), 0f), new SpawnPoint(new Vector3(0, -6.5f, 0), 0f), new SpawnPoint(new Vector3(-2f, 0, 0), 0f)]);
        world.Spawn(1, Assault);
        for (int id = 2; id <= 4; id++) world.Spawn(id, Tactical);
        output.WriteLine(string.Join(", ", world.Players.Values.Select(p => $"P{p.PlayerId} {p.Team} at {p.Position}")));
        var reactions = new List<ReactionEvent>();
        bool pounded = false, impact = false;
        for (int k = 0; k < 300 && !impact; k++)
        {
            var p = world.Players[1];
            var b = k < 8 ? InputButtons.Jump : !pounded && p.Jump == JumpPhase.Hang ? InputButtons.Melee : InputButtons.None;
            if (b == InputButtons.Melee) pounded = true;
            world.Step(id => id == 1 ? new PlayerInput(0, 0, 0, 0f, b, -1.55f) : new PlayerInput(0, 0, 0, 0f, InputButtons.None));
            reactions.AddRange(world.LastReactions);
            impact = world.LastJumps.Any(j => j.Kind == JumpEventKind.Impact);
            foreach (var j in world.LastJumps) output.WriteLine($"k {k}: {j.Kind} at {j.Position}, {j.Victims} victims");
        }
        Assert.True(impact);
        foreach (var r in reactions) output.WriteLine($"reaction {r.Kind} on P{r.Target}");
        bool Enemy(int id) => world.Harms(1, id);   // the friendly-fire rule (no match rules here: everyone)
        // about 6.8 m: intensity about 0.15, which the data's entries (stun from 0.81, interrupt from 0.8) left without a reaction.
        if (Enemy(2)) Assert.Contains(reactions, r => r.Target == 2 && r.Kind == ReactionKind.Stun);
        else Assert.DoesNotContain(reactions, r => r.Target == 2);
        Assert.DoesNotContain(reactions, r => r.Target == 3);   // about 8.5 m from the impact (it lands 2 m ahead): outside the 7 m radius
        Assert.DoesNotContain(reactions, r => r.Target == 1);   // the pounder
        if (Enemy(4)) Assert.Contains(reactions, r => r.Target == 4 && r.Kind == ReactionKind.Stun);
        Assert.True(Enemy(2) || Enemy(4), "the test needs an enemy in the blast");
    }

    [Fact]
    public void The_dash_flight_has_hyper_armour_and_its_landing_part_has_none()
    {
        var s = Fresh();
        Step(ref s, InputButtons.Jump);
        Step(ref s, InputButtons.None);
        Assert.True(Evade.HyperArmour(s, Config));
        Assert.False(Reaction.CanReact(s, Config));
        for (int k = 0; k < 60 && !s.EvadeLanded; k++) Step(ref s, InputButtons.None);
        Assert.True(s.EvadeLanded);
        Assert.False(Evade.HyperArmour(s, Config));
    }

    // ---------------------------------------------------------------- determinism and the wire

    [Theory]
    [InlineData(441)]
    [InlineData(442)]
    public void Prediction_matches_the_server_through_dashes_jumps_and_pounds_under_150ms_rtt_and_5pct_loss(int seed)
    {
        static SplashSpec Harmless(SplashSpec s) => s with { Damage = s.Damage with { Damage = 0f, ShieldDamage = 0f }, Reactions = [] };
        var config = Config with
        {
            JumpPacks = [Pack with { Pound = Harmless(Pack.Pound!), Landings = Pack.Landings.Select(l => l with { Blast = Harmless(l.Blast) }).ToArray() }],
            Weapons = Config.Weapons.Select(w => w with { Damage = 0f, ReserveSize = 5000 }).ToArray(),
            Classes = Config.Classes.Select(c => c with { Melee = c.Melee.Select(m => m with { Damage = 0f, SecondaryDamage = 0f }).ToArray() }).ToArray(),
            RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f,
        };
        var map = Mesh(Floor(0f), Floor(2.5f, 8, 20, -20, 20), (new(8, -20, 0), new(8, 20, 0), new(8, 20, 2.5f), new(8, -20, 2.5f)));
        var link = new LinkConditions(TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(10), 0.05);
        var game = new GameMap("dash_test", map, [new SpawnPoint(new Vector3(0, 0, 0), 0f), new SpawnPoint(new Vector3(-10, 5, 0), 1f)], 78);
        var h = new Harness(link, seed, 0, config, new ServerOptions { PeerTimeout = TimeSpan.FromMinutes(5) }, map: game);
        for (int i = 0; i < 2; i++) h.AddClient(nonce: (uint)(400 + i), options: new ClientOptions { RequestedClass = Assault, LoadMap = _ => new LoadedMap(map, 78) });
        int n = h.Clients.Count;
        var truth = new SortedDictionary<uint, Dictionary<int, PlayerState>>();
        var predicted = Enumerable.Range(0, n).Select(_ => new Dictionary<uint, PlayerState>()).ToArray();
        PlayerInput Input(int i, uint t)
        {
            var move = Harness.Scripted(i, t);
            var buttons = move.Buttons & ~(InputButtons.Jump | InputButtons.Sprint | InputButtons.Melee);
            uint phase = (t + (uint)i * 13) % 120;
            if (phase < 2) buttons |= InputButtons.Jump;                                   // a tap: the dash
            if (phase is >= 60 and < 72) buttons |= InputButtons.Jump;                     // a full hold: the jump
            if (phase is 85 or 86) buttons |= InputButtons.Melee;                          // the pound
            if (phase is 30 or 31) buttons |= InputButtons.Jump;                           // a press in the dash's landing (or flight)
            float mx = phase % 40 < 20 ? -1f : 1f, my = phase % 3 == 0 ? -1f : 1f;         // the eight directions come and go
            return move with { Buttons = buttons, MoveX = mx, MoveY = my, Pitch = -0.5f };
        }
        h.Run(TimeSpan.FromSeconds(30), Input, () =>
        {
            truth[h.Server.World.Tick] = new Dictionary<int, PlayerState>(h.Server.World.Players);
            for (int i = 0; i < n; i++)
                if (h.Clients[i].Status == ClientStatus.Connected) predicted[i][h.Clients[i].Tick] = h.Clients[i].Predicted;
        });
        for (int i = 0; i < n; i++)
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
            int dashes = mine.Zip(mine.Skip(1)).Count(p => p.First.EvadeTicks == 0 && p.Second.EvadeTicks > 0);
            int landings = mine.Zip(mine.Skip(1)).Count(p => !p.First.EvadeLanded && p.Second.EvadeLanded);
            int launches = mine.Zip(mine.Skip(1)).Count(p => p.First.Jump == JumpPhase.TakeOff && p.Second.Jump == JumpPhase.Ascent);
            int pounds = mine.Zip(mine.Skip(1)).Count(p => p.First.Jump != JumpPhase.Dive && p.Second.Jump == JumpPhase.Dive);
            output.WriteLine($"client {i}: compared {compared} ticks, {mismatches.Count} mismatches; {dashes} dashes ({landings} landed), {launches} lift-offs, {pounds} pounds");
            foreach (var m in mismatches.Take(3)) output.WriteLine("  " + m);
            Assert.True(compared > 300, $"client {i}: only {compared} ticks compared");
            Assert.True(dashes >= 6, $"client {i}: only {dashes} dashes");
            Assert.True(landings >= 3, $"client {i}: only {landings} dash landings");
            Assert.True(launches >= 4, $"client {i}: only {launches} lift-offs");
            Assert.True(pounds >= 2, $"client {i}: only {pounds} pounds");
            Assert.Empty(mismatches);
        }
    }

    [Fact]
    public void Protocol_24_carries_the_dash_landing_bit_in_slim_and_own_records_and_the_config_hash_covers_the_dash()
    {
        var p = Fresh() with { PlayerId = 7, EvadeTicks = 9, EvadeDirection = EvadeDirection.BackLeft, EvadeLanded = true, EvadeLock = 4 };
        var bytes = Protocol.Write(new Snapshot(1, 1, 1, [p, p with { PlayerId = 8 }], Self: 7));
        var back = Assert.IsType<Snapshot>(Protocol.Read(bytes));
        Assert.Equal((9, EvadeDirection.BackLeft, true), (back.Players[0].EvadeTicks, back.Players[0].EvadeDirection, back.Players[0].EvadeLanded));
        Assert.True(back.Players[1].EvadeLanded);   // slim
        Assert.True(Protocol.Slim(p).EvadeLanded);
        Assert.False(Protocol.Slim(p with { EvadeTicks = 0 }).EvadeLanded);
        Assert.Equal(46, Protocol.Version);
        Assert.Equal(1194, Protocol.MaxSnapshotBytes);   // the bit uses a spare one of the slot byte
        // The dash's data are in the config hash.
        Assert.NotEqual(Config.Hash(), (Config with { Evades = [Dash with { ToJumpAt = 0.2f }] }).Hash());
        Assert.NotEqual(Config.Hash(), (Config with { Evades = [Dash with { Forward = Dash.Forward with { Landing = Dash.Forward.Landing! with { MoveAt = 0.3f } } }] }).Hash());
        Assert.NotEqual(Config.Hash(), (Config with { Evades = [Dash with { Dash = false }] }).Hash());
    }

    // ---------------------------------------------------------------- M44 review additions

    static StaticMesh FlatMesh() => new([new(-300, -300, 0), new(300, -300, 0), new(300, 300, 0), new(-300, 300, 0)], [0, 1, 2, 0, 2, 3]);

    [Fact]
    public void The_dash_direction_is_latched_at_the_press_so_a_key_let_go_before_Space_still_counts()
    {
        // D held at the press, let go one tick before Space: the dash still goes right (east).
        var s = Fresh();
        Step(ref s, InputButtons.Jump, 1f, 0f);
        Assert.Equal((JumpPhase.TakeOff, EvadeDirection.Right), (s.Jump, s.JumpDashDirection));
        Step(ref s, InputButtons.Jump, 0f, 0f);   // D up, Space still down
        Assert.Equal(EvadeDirection.Right, s.JumpDashDirection);
        Step(ref s, InputButtons.None, 0f, 0f);   // Space up: the dash
        Assert.Equal(EvadeDirection.Right, s.EvadeDirection);
        Assert.Equal(EvadeDirection.Forward, s.JumpDashDirection);   // canonical outside the take-off
        for (int k = 0; k < 6; k++) Step(ref s, InputButtons.None);
        Assert.True(s.Position.X > 3f && MathF.Abs(s.Position.Y) < 0.6f, $"moved {s.Position}");
        // A stick pressed only after Space does not change the latched direction (no stick at the press: forward).
        var t = Fresh();
        Step(ref t, InputButtons.Jump);
        Step(ref t, InputButtons.Jump, -1f, -1f);
        Step(ref t, InputButtons.None, -1f, -1f);
        Assert.Equal(EvadeDirection.Forward, t.EvadeDirection);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(4, true)]
    [InlineData(5, true)]    // released on the decision tick itself (tick 5 = 0.167 s): still a dash
    [InlineData(6, false)]   // held through the decision tick: the jump
    [InlineData(7, false)]
    public void Jump_held_for_n_ticks_from_the_press_dashes_below_six_and_jumps_from_six(int heldTicks, bool dash)
    {
        var s = Fresh();
        bool dashed = false, launched = false;
        for (int k = 0; k < 20; k++)
        {
            var e = Step(ref s, k < heldTicks ? InputButtons.Jump : InputButtons.None, 0f, 1f);
            dashed |= Evade.Evading(s);
            launched |= e.Jump == JumpEventKind.Launch;
        }
        Assert.Equal((dash, !dash), (dashed, launched));
        Assert.Equal(5f / 30f, Dash.ToJumpAt, 5);   // the synthetic value; the install's 0.16666667 s lands on the same tick
    }

    [Fact]
    public void A_release_the_server_never_receives_in_time_becomes_a_jump_because_it_repeats_the_held_input()
    {
        // GameServer repeats a player's last input for a tick whose input has not arrived: a release that misses its tick (late or lost)
        // is replaced by the held input, and past the decision tick the press is a jump. The client predicted the dash and is corrected.
        var client = Fresh();
        var server = Fresh();
        var sent = new[] { InputButtons.Jump, InputButtons.Jump, InputButtons.Jump, InputButtons.Jump, InputButtons.None, InputButtons.None, InputButtons.None, InputButtons.None };
        var last = InputButtons.None;
        bool serverLaunched = false;
        for (int k = 0; k < sent.Length; k++)
        {
            Step(ref client, sent[k], 0f, 1f);
            bool lost = k is 4 or 5 or 6;   // the release's packets arrive after their ticks
            var applied = lost ? last : sent[k];
            last = applied;
            serverLaunched |= Step(ref server, applied, 0f, 1f).Jump == JumpEventKind.Launch;
        }
        Assert.True(Evade.Evading(client) || client.EvadeLock > 0, "the client predicted the dash");
        Assert.True(serverLaunched, "the server's repeated held input committed the jump");
    }

    [Fact]
    public void The_dash_is_stopped_by_the_other_sides_spawn_room_blocker_and_passes_its_own_sides()
    {
        // A wall 1 m thick across the X axis at x = 6 (blocker yaw 0: its thin axis is X), 40 m tall; the marine dashes right (D).
        var floor = FlatMesh();
        CollisionWorld With(Team side) => new(floor, floor, null, [new SpawnBlocker(side, new Vector3(6f, 0f, 19f), new Vector3(1f, 24f, 40f), 0f)]);
        PlayerState Dash(CollisionWorld world)
        {
            var s = Fresh() with { Team = Team.SpaceMarines };
            Step(ref s, InputButtons.Jump, 1f, 0f, world: world);
            Step(ref s, InputButtons.None, 1f, 0f, world: world);
            for (int k = 0; k < 40; k++) Step(ref s, InputButtons.None, world: world);
            return s;
        }
        var stopped = Dash(With(Team.Chaos));
        var passed = Dash(With(Team.SpaceMarines));
        output.WriteLine($"chaos blocker: {stopped.Position}; own blocker: {passed.Position}");
        Assert.True(stopped.Position.X < 5.5f - Config.CapsuleRadius + 0.05f);
        Assert.True(passed.Position.X > 10f);
    }

    [Fact]
    public void The_dash_is_stopped_by_a_cover_crate()
    {
        // An M39 crate (a static prop, 2.5 m tall: taller than the dash's 1.7 m rise) across the dash's line 4 m ahead.
        var floor = FlatMesh();
        var level = new CollisionWorld(floor, floor, props: [PropTests.Box("crate", new Vector3(-3f, 4f, 0f), new Vector3(3f, 5.2f, 2.5f))]);
        var open = new CollisionWorld(floor, floor);
        var s = Fresh();
        Step(ref s, InputButtons.Jump, world: level);
        Step(ref s, InputButtons.None, world: level);
        for (int k = 0; k < 40; k++) Step(ref s, InputButtons.None, world: level);
        output.WriteLine($"after the crate: {s.Position}");
        Assert.True(s.Position.Y < 4f - Config.CapsuleRadius + 0.05f);
        // Control: without the crate the same dash goes past it.
        var free = Fresh();
        Step(ref free, InputButtons.Jump, world: open);
        Step(ref free, InputButtons.None, world: open);
        for (int k = 0; k < 40; k++) Step(ref free, InputButtons.None, world: open);
        Assert.True(free.Position.Y > 10f);
    }

    [Theory]
    [InlineData(451)]
    [InlineData(452)]
    public void Prediction_matches_the_server_for_holds_of_exactly_five_and_six_ticks(int seed)
    {
        var config = Config with
        {
            RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f,
        };
        var link = new LinkConditions(TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(10), 0.05);
        var h = new Harness(link, seed, 0, config, new ServerOptions { PeerTimeout = TimeSpan.FromMinutes(5) });
        for (int i = 0; i < 2; i++) h.AddClient(nonce: (uint)(500 + i), options: new ClientOptions { RequestedClass = Assault });
        var truth = new SortedDictionary<uint, Dictionary<int, PlayerState>>();
        var predicted = Enumerable.Range(0, 2).Select(_ => new Dictionary<uint, PlayerState>()).ToArray();
        PlayerInput Input(int i, uint t)
        {
            uint cycle = (t + (uint)i * 17) / 90, phase = (t + (uint)i * 17) % 90;
            int hold = cycle % 2 == 0 ? 5 : 6;   // exactly at the boundary: 5 dashes, 6 jumps
            var buttons = phase < hold ? InputButtons.Jump : InputButtons.None;
            return new PlayerInput(t, cycle % 3 == 0 ? 1f : 0f, 1f, 0.3f * i, buttons, -0.3f);
        }
        h.Run(TimeSpan.FromSeconds(24), Input, () =>
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
            int dashes = mine.Zip(mine.Skip(1)).Count(p => p.First.EvadeTicks == 0 && p.Second.EvadeTicks > 0);
            int launches = mine.Zip(mine.Skip(1)).Count(p => p.First.Jump == JumpPhase.TakeOff && p.Second.Jump == JumpPhase.Ascent);
            output.WriteLine($"client {i}: compared {compared}, {mismatches.Count} mismatches; {dashes} dashes, {launches} lift-offs");
            foreach (var m in mismatches.Take(3)) output.WriteLine("  " + m);
            Assert.True(compared > 300);
            Assert.True(dashes >= 3 && launches >= 3, $"client {i}: {dashes} dashes, {launches} lift-offs");
            Assert.Empty(mismatches);
        }
    }

    // ---- the pound's stun: hyper armour, a charger, friendly fire, a victim the blast kills

    const int Pounder = 0, Enemy = 1, Ally = 2;

    static SimConfig StunConfig(bool lethal = false)
    {
        var pound = JumpPackTests.PoundBlast with { Reactions = [new ReactionEntry(0f, ReactionKind.Stun)] };
        if (lethal) pound = pound with { Damage = pound.Damage with { Damage = 5000f } };
        return Config with
        {
            Classes =
            [
                JumpPackTests.Config.Classes[0] with { Evade = 0, Side = Team.SpaceMarines },
                new ClassLoadout("chaos", 1, 0, SimConfig.PlaceholderMeleeSteps) { Evade = 1, MeleeRush = RushTests.Spec, Side = Team.Chaos },
                new ClassLoadout("ally", 1, 0, SimConfig.PlaceholderMeleeSteps) { Side = Team.SpaceMarines },
            ],
            JumpPacks = [Pack with { Pound = pound }],
            Evades = [Dash, EvadeTests.Roll with { TargetableModeTime = 0.5f, CollisionModeTime = 0.5f }],
            RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f,
        };
    }

    /// <summary>
    /// The pounder (1) jumps (8 held ticks) and pounds at the hang aiming almost straight down; the others act by <paramref name="other"/>
    /// (tick, id, ticks to the impact: null = idle). Returns the reactions, the hits and the impact tick.
    /// </summary>
    static (List<ReactionEvent> Reactions, List<HitEvent> Hits, int ImpactAt, World World) PoundAmong(SimConfig c, bool friendlyFire, (int Class, Vector3 At, float Yaw)[] others,
        Func<int, int, int, PlayerInput?>? other = null, int impactAt = -1)
    {
        var spots = new[] { new SpawnPoint(Vector3.Zero, 0f) }.Concat(others.Select(o => new SpawnPoint(o.At, o.Yaw))).ToArray();
        var world = new World(c, null, spots) { Rules = new MatchRules { TimeLimit = 0f, FriendlyFire = friendlyFire } };
        world.Spawn(1, Pounder);
        for (int i = 0; i < others.Length; i++) world.Spawn(i + 2, others[i].Class);
        var reactions = new List<ReactionEvent>(); var hits = new List<HitEvent>();
        bool pounded = false; int found = -1;
        for (int k = 0; k < 300 && found < 0; k++)
        {
            var p = world.Players[1];
            var b = k < 8 ? InputButtons.Jump : !pounded && p.Jump == JumpPhase.Hang ? InputButtons.Melee : InputButtons.None;
            if (b == InputButtons.Melee) pounded = true;
            int left = impactAt < 0 ? int.MaxValue : impactAt - k;
            world.Step(id => id == 1 ? new PlayerInput(0, 0, 0, 0f, b, -1.55f)
                : other?.Invoke(k, id, left) ?? new PlayerInput(0, 0, 0, world.Players[id].Yaw, InputButtons.None));
            reactions.AddRange(world.LastReactions); hits.AddRange(world.LastHits);
            if (world.LastJumps.Any(j => j.Kind == JumpEventKind.Impact)) found = k;
        }
        Assert.True(found >= 0, "no impact");
        return (reactions, hits, found, world);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_pound_stun_spares_hyper_armour_and_a_fresh_charger_and_allies_follow_friendly_fire(bool friendlyFire)
    {
        var c = StunConfig();
        // 2: an idle enemy; 3: an enemy rolling at the impact (hyper armour 0.5 s); 4: an enemy charging at the impact (hyper armour 0.3 s);
        // 5: an ally. The impact lands about 2 m ahead of the pounder (+Y).
        (int, Vector3, float)[] others = [(Enemy, new Vector3(3f, 2f, 0), 0f), (Enemy, new Vector3(2.5f, 3.5f, 0), 0f), (Enemy, new Vector3(-1f, -1f, 0), MathF.PI / 2f), (Ally, new Vector3(3f, 5f, 0), 0f)];
        int impact = PoundAmong(c, friendlyFire, others).ImpactAt;   // a dry run for the impact tick (the others do not change the pounder's flight)
        PlayerInput? Act(int k, int id, int left) => id switch
        {
            3 when left == 3 => new PlayerInput(0, 0f, 1f, 0f, InputButtons.Jump),                                                   // roll 3 ticks before
            4 when left is > 2 and <= 16 => new PlayerInput(0, 0f, 1f, MathF.PI / 2f, InputButtons.Sprint),                            // sprint in …
            4 when left == 2 => new PlayerInput(0, 0f, 1f, MathF.PI / 2f, InputButtons.Sprint | InputButtons.MeleeHeavy),              // … and charge 2 ticks before
            _ => null,
        };
        var (reactions, hits, at, world) = PoundAmong(c, friendlyFire, others, Act, impact);
        Assert.Equal(impact, at);
        foreach (var r in reactions) output.WriteLine($"reaction {r.Kind} on P{r.Target}");
        output.WriteLine($"rolling at the impact: {world.Players[3].EvadeTicks} evade ticks; charging: {world.Players[4].Rush}");
        var blast = hits.Where(h => h.Landing && h.Shooter == 1).ToList();
        Assert.Contains(reactions, r => r.Target == 2 && r.Kind == ReactionKind.Stun);
        Assert.Contains(blast, h => h.Target == 3);                     // the roller is damaged …
        Assert.DoesNotContain(reactions, r => r.Target == 3);           // … but its hyper armour negates the stun
        Assert.Equal(RushPhase.Rush, world.Players[4].Rush);
        Assert.Contains(blast, h => h.Target == 4);                     // the fresh charger is damaged, not stunned, and charges on
        Assert.DoesNotContain(reactions, r => r.Target == 4);
        if (friendlyFire) Assert.Contains(reactions, r => r.Target == 5 && r.Kind == ReactionKind.Stun);
        else { Assert.DoesNotContain(reactions, r => r.Target == 5); Assert.DoesNotContain(blast, h => h.Target == 5); }
    }

    [Fact]
    public void A_victim_the_pound_kills_is_not_stunned()
    {
        var (reactions, hits, _, world) = PoundAmong(StunConfig(lethal: true), false, [(Enemy, new Vector3(2f, 2f, 0), 0f)]);
        Assert.Contains(hits, h => h.Target == 2 && h.Killed);
        Assert.False(world.Players[2].Alive);
        Assert.DoesNotContain(reactions, r => r.Target == 2);
        Assert.Equal(ReactionKind.None, world.Players[2].Reaction);
    }

    [Fact]
    public void Bot_navigation_flies_holds_under_the_decision_tick_as_the_shortest_jump()
    {
        Assert.Equal(5, NavJumpFlight.ShortestJumpHold(Config, Assault));
        Assert.Equal(1, NavJumpFlight.ShortestJumpHold(JumpPackTests.Config, Assault));   // no dash: every hold jumps
        var world = new CollisionWorld(FlatMesh(), FlatMesh());
        var dash = NavJumpFlight.Simulate(world, Config, Assault, Team.None, Vector3.Zero, 0, 4);
        var jump = NavJumpFlight.Simulate(world, Config, Assault, Team.None, Vector3.Zero, 0, 5);
        output.WriteLine($"hold 4: {dash.Note}; hold 5: {jump.Note}, apex {jump.Apex:F1} m");
        Assert.False(dash.Landed);   // the dash is no jump: "jump lost"
        Assert.True(jump.Landed && jump.Apex > 5f);
    }
}
