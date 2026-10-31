using System.Numerics;
using SmNet;
using SmServer;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// feel/jump-pack (protocol 31; the owner's requests of 2026-10-29): jump-pack air control ("somewhat move around in the air with WASD and combinations
/// such as WA, SD"), the sword slash's own dive toward the aim, its landing lunge ("move forward a bit") and short recovery, a slash press held over the
/// take-off. Values are ours (spec "Jump-pack feel (feel/jump-pack)"); synthetic specs shaped like the install's. No original bytes.
/// </summary>
public class JumpPackFeelTests(ITestOutputHelper output)
{
    const int Assault = 0;
    const float Dt = 1f / 30f;

    /// <summary>The M14 pack (no dash: Space always jumps) with air control and the snappy slash, as <c>SmContent</c> sets them.</summary>
    internal static readonly SlashSpec Snappy = JumpPackAttackTests.Slash with
    {
        Recovery = 0.3f, DiveSpeed = 40f, DiveMinDown = 40f, DiveReach = 10f, LungeSpeed = 6f, LungeTime = 0.2f,
    };
    static readonly JumpPackSpec Pack = JumpPackTests.Pack with { AirSpeed = 8f, AirAcceleration = 15f, Slash = Snappy, MaxPower = 300f };
    static readonly SimConfig Config = JumpPackTests.Config with
    {
        JumpPacks = [Pack],
        RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f,
    };
    static readonly SimConfig NoAir = Config with { JumpPacks = [Pack with { AirSpeed = 0f, AirAcceleration = 0f }] };

    static PlayerState Fresh(SimConfig c, Vector3 at = default, float yaw = 0f) =>
        Combat.Respawned(new PlayerState(1, at, Vector3.Zero, yaw, true) { Class = Assault }, c);

    static SelfEvents Step(ref PlayerState s, SimConfig c, InputButtons b, float mx = 0f, float my = 0f, float yaw = 0f, float pitch = 0f, CollisionWorld? world = null)
    {
        s = Combat.StepPlayer(s, new PlayerInput(0, mx, my, yaw, b, pitch), c, out var e, world);
        return e;
    }

    /// <summary>A full standing jump (Jump held 13 ticks); from the first flight tick after <paramref name="from"/> the stick is (mx, my) for <paramref name="ticks"/> ticks.</summary>
    static List<PlayerState> Fly(SimConfig c, float mx, float my, float yaw, int from = 8, int ticks = 30)
    {
        var s = Fresh(c, yaw: yaw);
        var states = new List<PlayerState>();
        for (int k = 0; k < from + ticks; k++)
        {
            bool steer = k >= from;
            Step(ref s, c, k < 13 ? InputButtons.Jump : InputButtons.None, steer ? mx : 0f, steer ? my : 0f, yaw);
            states.Add(s);
        }
        return states;
    }

    // ---------------------------------------------------------------- air control

    [Theory]
    [InlineData(0f)]
    [InlineData(0.7f)]
    public void The_stick_steers_the_flight_in_eight_directions_relative_to_the_view_at_the_same_speed_and_adds_no_height(float yaw)
    {
        (float X, float Y)[] sticks = [(0, 1), (1, 1), (1, 0), (1, -1), (0, -1), (-1, -1), (-1, 0), (-1, 1)];   // W, WD, D, SD, S, SA, A, WA
        var straight = Fly(Config, 0f, 0f, yaw);
        var forward = new Vector2(-MathF.Sin(yaw), MathF.Cos(yaw));
        var right = new Vector2(forward.Y, -forward.X);
        var speeds = new List<float>();
        foreach (var (x, y) in sticks)
        {
            var states = Fly(Config, x, y, yaw);
            var v = new Vector2(states[^1].Velocity.X, states[^1].Velocity.Y);
            var wish = Vector2.Normalize(right * x + forward * y);
            Assert.True(Vector2.Dot(Vector2.Normalize(v), wish) > 0.9999f, $"stick {x},{y}: velocity {v} not along {wish}");
            speeds.Add(v.Length());
            // Horizontal only: the heights are the straight jump's tick for tick.
            for (int k = 0; k < states.Count; k++) Assert.Equal(straight[k].Position.Z, states[k].Position.Z, 4);
            // 1 s of stick: the top speed, reached in 8 / 15 s (0.53 s).
            Assert.Equal(8f, v.Length(), 3);
        }
        Assert.True(speeds.Max() - speeds.Min() < 1e-3f, "a diagonal steers at the same speed as a single key");
        // Before (the data's gain alone) 1 s of stick gave about 7.45 m/s and 0.5 s about 4.4: the steering now reaches 8 m/s by 0.53 s.
        var half = Fly(Config, 1f, 0f, yaw, ticks: 15);
        var halfOld = Fly(NoAir, 1f, 0f, yaw, ticks: 15);
        output.WriteLine($"0.5 s of D: {new Vector2(half[^1].Velocity.X, half[^1].Velocity.Y).Length():F2} m/s (data gain alone {new Vector2(halfOld[^1].Velocity.X, halfOld[^1].Velocity.Y).Length():F2})");
        Assert.True(new Vector2(half[^1].Velocity.X, half[^1].Velocity.Y).Length() > new Vector2(halfOld[^1].Velocity.X, halfOld[^1].Velocity.Y).Length() + 2f);
    }

    [Fact]
    public void A_fast_forward_flight_is_turned_not_braked_and_never_speeds_past_its_own_speed_or_the_air_speed()
    {
        // Lift off with W (16 m/s along the view), then hold A: the flight turns left at 16 m/s at most; W alone keeps it.
        var s = Fresh(Config);
        float most = 0f;
        for (int k = 0; k < 60; k++)
        {
            bool air = s.Jump is JumpPhase.Ascent or JumpPhase.Hang or JumpPhase.Fall && k > 6;
            Step(ref s, Config, k < 13 ? InputButtons.Jump : InputButtons.None, air ? -1f : 0f, air ? 0f : 1f);
            if (air) most = MathF.Max(most, new Vector2(s.Velocity.X, s.Velocity.Y).Length());
        }
        Assert.True(most <= 16f + 1e-3f, $"fastest {most}");
        Assert.True(s.Velocity.X < -7f, $"turned left: {s.Velocity}");
        var w = Fresh(Config);
        for (int k = 0; k < 40; k++) Step(ref w, Config, k < 13 ? InputButtons.Jump : InputButtons.None, 0f, 1f);
        Assert.Equal(16f, new Vector2(w.Velocity.X, w.Velocity.Y).Length(), 3);
        // Stick back: brakes, then pushes back up to 8 m/s only.
        var b = Fresh(Config);
        for (int k = 0; k < 90 && b.Jump != JumpPhase.Landed; k++)
        {
            bool air = b.Jump is JumpPhase.Ascent or JumpPhase.Hang or JumpPhase.Fall && k > 6;
            Step(ref b, Config, k < 30 ? InputButtons.Jump : InputButtons.None, 0f, air ? -1f : 1f);
            if (air && b.Velocity.Y < 0f) Assert.True(-b.Velocity.Y <= 8f + 1e-3f);
        }
        // Released: the data's drift (1% a tick) as before.
        var d = Fly(Config, 0f, 0f, 0f);
        var dOld = Fly(NoAir, 0f, 0f, 0f);
        Assert.Equal(dOld[^1], d[^1]);
    }

    [Fact]
    public void The_stick_does_not_steer_the_dive_and_the_take_off_is_ground_movement()
    {
        PlayerState Dive(float mx)
        {
            var s = Fresh(Config);
            for (int k = 0; k < 13; k++) Step(ref s, Config, InputButtons.Jump);
            while (s.Jump != JumpPhase.Hang) Step(ref s, Config, InputButtons.None);
            Step(ref s, Config, InputButtons.Melee, mx, 0f, 0f, -1.2f);
            Assert.Equal(JumpPhase.Dive, s.Jump);
            for (int k = 0; k < 6 && s.Jump == JumpPhase.Dive; k++) Step(ref s, Config, InputButtons.None, mx);
            return s;
        }
        Assert.Equal(Dive(0f).Position, Dive(1f).Position);   // a pound from 13 m and more: unchanged by the stick
    }

    // ---------------------------------------------------------------- the slash

    /// <summary>A jump of <paramref name="held"/> ticks, the melee press at the apex aimed at <paramref name="pitch"/>; every state from the press to free.</summary>
    static (PlayerState Press, List<PlayerState> After, List<SelfEvents> Events) Slash(SimConfig c, int held, float pitch, int perk = -1)
    {
        var s = Fresh(c);
        if (perk >= 0) s = s with { Perk1 = perk };
        for (int k = 0; k < held; k++) Step(ref s, c, InputButtons.Jump);
        while (s.Jump != JumpPhase.Hang) Step(ref s, c, InputButtons.None);
        var press = s;
        var after = new List<PlayerState>();
        var events = new List<SelfEvents>();
        events.Add(Step(ref s, c, InputButtons.Melee, 0f, 0f, 0f, pitch));
        after.Add(s);
        for (int k = 0; k < 60 && s.Jump != JumpPhase.None; k++) { events.Add(Step(ref s, c, InputButtons.None)); after.Add(s); }
        return (press, after, events);
    }

    [Theory]
    [InlineData(3f)]
    [InlineData(4f)]
    [InlineData(5f)]
    public void The_slash_dives_straight_toward_the_point_under_the_aim_and_lands_on_it(float ahead)
    {
        // The 4 m tap: apex about 3.8 m; aim from the eye at a point `ahead` m in front on the ground.
        var probe = Fresh(Config);
        Step(ref probe, Config, InputButtons.Jump);
        while (probe.Jump != JumpPhase.Hang) Step(ref probe, Config, InputButtons.None);
        float pitch = -MathF.Atan2(probe.Position.Z + Config.EyeHeight, ahead);
        var (press, after, events) = Slash(Config, 1, pitch);
        Assert.True(after[0].JumpSlash);
        int land = events.FindIndex(e => e.Jump == JumpEventKind.SlashLand);
        Assert.True(land >= 0);
        var landed = after[land];
        float off = landed.Position.Y - press.Position.Y - ahead;
        output.WriteLine($"aim {ahead} m ahead from {press.Position.Z:F2} m: lands {landed.Position.Y - press.Position.Y:F2} m ahead after {land} ticks (off {off:+0.00;-0.00})");
        Assert.InRange(off, -0.05f, 40f * Dt);   // the point, overshot by at most the last tick's travel (flat ground)
        // Straight: a constant velocity (no gravity) until the ground.
        for (int k = 0; k < land; k++) Assert.Equal(40f, after[k].Velocity.Length(), 2);
    }

    [Fact]
    public void Aiming_level_dives_at_the_least_angle_and_the_lunge_carries_forward_through_the_hit()
    {
        var (press, after, events) = Slash(Config, 1, 0f);
        int land = events.FindIndex(e => e.Jump == JumpEventKind.SlashLand), hit = events.FindIndex(e => e.SlashHit), free = after.FindIndex(s => s.Jump == JumpPhase.None);
        var first = Vector3.Normalize(after[0].Velocity);
        Assert.Equal(-MathF.Sin(40f * MathF.PI / 180f), first.Z, 3);   // 40 degrees below level
        Assert.Equal(4, hit - land);                                      // the data's 0.133 s
        Assert.Equal(9, free - land);                                     // 0.3 s (was 19 ticks, 0.638 s)
        float lunge = after[hit].Position.Y - after[land].Position.Y, total = after[free].Position.Y - after[land].Position.Y;
        output.WriteLine($"landed {after[land].Position.Y - press.Position.Y:F2} m ahead; lunge to the hit {lunge:F2} m, to free {total:F2} m");
        Assert.InRange(lunge, 0.7f, 1.0f);   // 6 m/s for the 4 ticks to the hit
        Assert.InRange(total, 1.2f, 2.2f);   // 1.2 m of lunge, then the ground motor's stop
        Assert.True(after[free].Velocity.Length() < 6f);
        // Moving again at once: the stick on the free tick.
        var s = after[free];
        Step(ref s, Config, InputButtons.None, 0f, 1f);
        Assert.True(s.Velocity.Y > 0f);
    }

    [Fact]
    public void A_melee_press_in_the_take_off_is_buffered_to_the_tick_after_the_lift_off_but_a_button_held_since_before_the_jump_is_no_press()
    {
        // Pressed on tick 1, 3, 5 (the take-off) or 6 (the lift-off tick), held or tapped for one tick: the slash on tick 7.
        foreach (int pressAt in new[] { 1, 3, 5, 6 })
            foreach (bool tap in new[] { false, true })
            {
                var s = Fresh(Config);
                int dive = -1;
                for (int k = 0; k < 20 && dive < 0; k++)
                {
                    bool melee = tap ? k == pressAt : k >= pressAt;
                    var b = (k < 13 ? InputButtons.Jump : InputButtons.None) | (melee ? InputButtons.Melee : InputButtons.None);
                    Step(ref s, Config, b);
                    if (k >= 1 && k < 6) Assert.Equal(k >= pressAt, s.JumpSlash);   // the buffer (the slot byte's bit 7 on the wire)
                    if (JumpPack.Pounding(s.Jump)) dive = k;
                }
                Assert.Equal(7, dive);   // the lift-off is tick 6
                Assert.True(s.JumpSlash);
            }
        // The review's case: melee already held when Space is pressed (and kept down): no press, no slash.
        var held = Fresh(Config);
        Step(ref held, Config, InputButtons.Melee);
        for (int k = 0; k < 40; k++)
        {
            Step(ref held, Config, (k < 13 ? InputButtons.Jump : InputButtons.None) | InputButtons.Melee);
            Assert.False(JumpPack.Pounding(held.Jump), $"tick {k}");
            Assert.False(held.JumpSlash);
        }
        // Without the feel values (the protocol 30 slash) a take-off press is lost, as before.
        var old = Config with { JumpPacks = [Pack with { Slash = JumpPackAttackTests.Slash }] };
        var t = Fresh(old);
        for (int k = 0; k < 20; k++)
        {
            Step(ref t, old, (k < 13 ? InputButtons.Jump : InputButtons.None) | (k >= 3 ? InputButtons.Melee : InputButtons.None));
            Assert.False(JumpPack.Pounding(t.Jump));
        }
    }

    [Fact]
    public void The_buffered_press_round_trips_in_the_own_record_and_is_refused_in_the_hang()
    {
        var s = Fresh(Config);
        for (int k = 0; k < 3; k++) Step(ref s, Config, InputButtons.Jump | (k == 2 ? InputButtons.Melee : InputButtons.None));
        Assert.Equal((JumpPhase.TakeOff, true), (s.Jump, s.JumpSlash));
        var snap = Protocol.Write(new Snapshot(9, 1, 1, [s], Self: s.PlayerId));
        var read = Assert.IsType<Snapshot>(Protocol.Read(snap));
        Assert.Equal((JumpPhase.TakeOff, true), (read.Players[0].Jump, read.Players[0].JumpSlash));
        var slim = Protocol.Write(new Snapshot(9, 1, 1, [s]));   // another player's slim record: the jumping flag with the bit
        Assert.True(Assert.IsType<Snapshot>(Protocol.Read(slim)).Players[0].JumpSlash);
        // In the hang the writer leaves the bit out (and the reader refuses it there).
        var hang = s with { Jump = JumpPhase.Hang };
        Assert.False(Assert.IsType<Snapshot>(Protocol.Read(Protocol.Write(new Snapshot(9, 1, 1, [hang], Self: s.PlayerId)))).Players[0].JumpSlash);
    }

    // ---------------------------------------------------------------- with level collision (the review of 2026-10-29)

    sealed class Level
    {
        readonly List<Vector3> _v = new();
        readonly List<int> _i = new();
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) { int n = _v.Count; _v.AddRange([a, b, c, d]); _i.AddRange([n, n + 1, n + 2, n, n + 2, n + 3]); }
        public Level Box(Vector3 min, Vector3 max)
        {
            Vector3 P(int x, int y, int z) => new(x == 0 ? min.X : max.X, y == 0 ? min.Y : max.Y, z == 0 ? min.Z : max.Z);
            Quad(P(0, 0, 0), P(1, 0, 0), P(1, 1, 0), P(0, 1, 0)); Quad(P(0, 0, 1), P(0, 1, 1), P(1, 1, 1), P(1, 0, 1));
            Quad(P(0, 0, 0), P(0, 0, 1), P(1, 0, 1), P(1, 0, 0)); Quad(P(0, 1, 0), P(1, 1, 0), P(1, 1, 1), P(0, 1, 1));
            Quad(P(0, 0, 0), P(0, 1, 0), P(0, 1, 1), P(0, 0, 1)); Quad(P(1, 0, 0), P(1, 0, 1), P(1, 1, 1), P(1, 1, 0));
            return this;
        }
        public CollisionWorld World() { var m = new StaticMesh(_v.ToArray(), _i.ToArray()); return new CollisionWorld(m, m); }
    }

    /// <summary>A floor at z = 0 (x, y in ±100), a wall across y = 8 to 9 (40 m high: above every jump) and a plateau 6 m high for x &gt; 20.</summary>
    static readonly CollisionWorld Room = new Level()
        .Box(new(-100, -100, -1), new(100, 100, 0))
        .Box(new(-100, 8, 0), new(100, 9, 40))
        .Box(new(20, -100, 0), new(100, 100, 6))
        .World();

    static float Deg(Vector3 d) => MathF.Asin(Math.Clamp(-Vector3.Normalize(d).Z, -1f, 1f)) * 180f / MathF.PI;

    [Fact]
    public void The_slash_direction_meets_the_level_under_the_aim_within_its_reach_and_otherwise_follows_the_clamped_aim()
    {
        var at = new PlayerState(1, new Vector3(0, 0, 4f), Vector3.Zero, 0f, true);
        float eye = 4f + Config.EyeHeight;
        // Aimed at the floor 3 m ahead (inside the 10 m reach): from the feet straight to that point (53 degrees down).
        float pitch = -MathF.Atan2(eye, 3f);
        var d = JumpPack.SlashDirection(at, 0f, pitch, Snappy, Config, Room);
        Assert.Equal(0f, d.X, 4);
        Assert.Equal(0.6f, d.Y, 3);
        Assert.Equal(-0.8f, d.Z, 3);
        // The same point on the flat ground (no level) gives the same direction.
        Assert.Equal(d, JumpPack.SlashDirection(at, 0f, pitch, Snappy, Config, null));
        // A floor point 12 m away (past the reach): the aim itself, here shallower than 40 degrees, so the least angle.
        float far = -MathF.Atan2(eye, 12f);
        Assert.Equal(40f, Deg(JumpPack.SlashDirection(at, 0f, far, Snappy, Config, Room)), 2);
        // The sky (aiming up), level, or at the wall's face at eye height: the least angle along the view.
        Assert.Equal(40f, Deg(JumpPack.SlashDirection(at, 0f, 0.5f, Snappy, Config, Room)), 2);
        Assert.Equal(40f, Deg(JumpPack.SlashDirection(at, 0f, 0f, Snappy, Config, Room)), 2);
        var turned = JumpPack.SlashDirection(at, MathF.PI / 2f, 0.3f, Snappy, Config, Room);   // facing -X
        Assert.True(turned.X < -0.7f && MathF.Abs(turned.Y) < 1e-4f);
        // Straight down.
        var down = JumpPack.SlashDirection(at, 0f, -MathF.PI / 2f + 1e-4f, Snappy, Config, Room);
        Assert.True(down.Z < -0.999f, $"{down}");
    }

    static PlayerState SlashFrom(Vector3 at, float yaw, float pitch, CollisionWorld world, out List<PlayerState> after)
    {
        var s = Fresh(Config, at, yaw);
        for (int k = 0; k < 20; k++) Step(ref s, Config, InputButtons.None, yaw: yaw, world: world);   // settle
        Step(ref s, Config, InputButtons.Jump, yaw: yaw, world: world);
        while (s.Jump != JumpPhase.Hang) Step(ref s, Config, InputButtons.None, yaw: yaw, world: world);
        var press = s;
        Step(ref s, Config, InputButtons.Melee, yaw: yaw, pitch: pitch, world: world);
        after = [s];
        for (int k = 0; k < 60 && s.Jump != JumpPhase.None; k++) { Step(ref s, Config, InputButtons.None, yaw: yaw, world: world); after.Add(s); }
        for (int k = 0; k < 30; k++) { Step(ref s, Config, InputButtons.None, yaw: yaw, world: world); after.Add(s); }
        return press;
    }

    [Fact]
    public void The_landing_lunge_stops_at_a_wall_and_off_a_ledge_drops_by_the_walk_off_rule()
    {
        // Facing the wall (y = 8), landing about 4 m short of it: the lunge's 1.6 m is cut by the wall, never into it.
        SlashFrom(new Vector3(0, 2.8f, 0), 0f, -0.9f, Room, out var wall);
        Assert.Contains(wall, p => p.JumpSlash && p.Jump == JumpPhase.Recover);
        Assert.All(wall, p => Assert.True(p.Position.Y <= 8f - Config.CapsuleRadius + 0.02f, $"in the wall: {p.Position}"));
        Assert.True(wall[^1].Position.Y > 7f, $"the lunge reaches the wall: {wall[^1].Position}");
        // On the plateau (x > 20, 6 m up) facing its edge (-X), landing 1 m from the edge: the lunge carries the body off it, then it drops straight
        // (no 6 m/s glide in the air), lands below and the slash's recovery ends.
        SlashFrom(new Vector3(22.6f, 0, 6f), MathF.PI / 2f, -1.3f, Room, out var ledge);
        int off = ledge.FindIndex(p => p.Position.X < 20f - Config.CapsuleRadius && !p.Grounded);
        Assert.True(off > 0, "the lunge leaves the plateau");
        var falling = ledge.Skip(off + 2).TakeWhile(p => !p.Grounded).ToList();
        Assert.NotEmpty(falling);
        Assert.All(falling, p => Assert.True(new Vector2(p.Velocity.X, p.Velocity.Y).Length() < 0.01f, $"glides in the air: {p.Velocity}"));
        Assert.True(ledge[^1].Grounded && ledge[^1].Position.Z < 0.1f && ledge[^1].Jump == JumpPhase.None);
        Assert.True(ledge[^1].Position.X > 18f, $"dropped near the edge: {ledge[^1].Position}");
    }

    [Fact]
    public void Steering_into_a_wall_slides_along_it_and_never_passes_it()
    {
        var s = Fresh(Config, new Vector3(0, 4f, 0));
        for (int k = 0; k < 20; k++) Step(ref s, Config, InputButtons.None, world: Room);
        float most = 0f;
        for (int k = 0; k < 90 && !(k > 10 && s.Jump == JumpPhase.Landed); k++)
        {
            bool air = JumpPack.InFlight(s.Jump);
            Step(ref s, Config, k < 13 ? InputButtons.Jump : InputButtons.None, air ? 1f : 0f, air ? 1f : 0f, world: Room);   // W+D: into the wall and along it
            Assert.True(s.Position.Y <= 8f - Config.CapsuleRadius + 0.02f, $"tick {k}: {s.Position}");
            most = MathF.Max(most, s.Position.X);
        }
        Assert.True(most > 1.5f, $"slid along the wall: {most}");
    }

    [Theory]
    [InlineData(741)]
    [InlineData(742)]
    public void Prediction_matches_the_server_on_a_level_with_walls_and_a_ledge_under_150ms_rtt_and_5pct_loss(int seed)
    {
        var config = Config with { JumpPacks = [Pack with { RechargeRate = 100f }] };
        var map = new GameMap("feel", Room, [new SpawnPoint(new Vector3(0, 3, 0), 0f), new SpawnPoint(new Vector3(23, 0, 6), MathF.PI / 2f)], 91);
        var link = new LinkConditions(TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(10), 0.05);
        var h = new Harness(link, seed, 0, config, new ServerOptions { PeerTimeout = TimeSpan.FromMinutes(5) }, map: map);
        for (int i = 0; i < 2; i++) h.AddClient(nonce: (uint)(710 + i), options: new ClientOptions { RequestedClass = Assault, LoadMap = _ => new LoadedMap(Room, 91) });
        var truth = new SortedDictionary<uint, Dictionary<int, PlayerState>>();
        var predicted = Enumerable.Range(0, 2).Select(_ => new Dictionary<uint, PlayerState>()).ToArray();
        (float, float)[] sticks = [(0, 1), (1, 1), (1, 0), (1, -1), (0, -1), (-1, -1), (-1, 0), (-1, 1)];
        int[] holds = [1, 6, 9, 14];
        PlayerInput Input(int i, uint t)
        {
            uint cycle = (t + (uint)i * 17) / 90, phase = (t + (uint)i * 17) % 90;
            int hold = holds[(int)(cycle % (uint)holds.Length)];
            var buttons = phase < hold ? InputButtons.Jump : InputButtons.None;
            var (x, y) = phase > 8 ? sticks[(int)((cycle + phase / 10) % 8)] : (0f, 0.5f);
            if (cycle % 2 == 1 && phase == 20 + cycle % 7) buttons |= InputButtons.Melee;
            if (cycle % 4 == 2 && phase is 3 or 4) buttons |= InputButtons.Melee;   // a tap in the take-off: buffered
            return new PlayerInput(t, x, y, 0.3f * i + 0.2f * (cycle % 8), buttons, -0.3f - 0.1f * (cycle % 6));
        }
        h.Run(TimeSpan.FromSeconds(30), Input, () =>
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
            int slashes = mine.Zip(mine.Skip(1)).Count(p => !JumpPack.Pounding(p.First.Jump) && p.Second.JumpSlash && JumpPack.Pounding(p.Second.Jump));
            int buffered = mine.Count(p => p.JumpSlash && p.Jump == JumpPhase.TakeOff);
            int walls = mine.Count(p => p.Position.Y > 7f);
            output.WriteLine($"client {i}: compared {compared}, {mismatches.Count} mismatches; {slashes} slashes, {buffered} buffered take-off ticks, {walls} ticks by the wall");
            foreach (var m in mismatches.Take(3)) output.WriteLine("  " + m);
            Assert.True(compared > 300);
            Assert.True(slashes >= 4 && buffered >= 2, $"client {i}: {slashes} slashes, {buffered} buffered");
            Assert.Empty(mismatches);
        }
    }

    [Fact]
    public void The_slash_does_not_wait_for_the_pounds_delay_but_the_pound_does()
    {
        var delayed = Config with { JumpPacks = [Pack with { PoundDelay = 2f }] };
        var (_, after, _) = Slash(delayed, 1, 0f);
        Assert.True(after[0].JumpSlash && after[0].Jump == JumpPhase.Dive);
        var s = Fresh(delayed);
        for (int k = 0; k < 13; k++) Step(ref s, delayed, InputButtons.Jump);
        while (s.Jump != JumpPhase.Hang) Step(ref s, delayed, InputButtons.None);
        Step(ref s, delayed, InputButtons.Melee, pitch: -1.2f);
        Assert.Equal(JumpPhase.Hang, s.Jump);   // 13 m up, under 2 s after the lift-off: no pound yet
    }

    [Fact]
    public void Without_the_feel_values_the_slash_is_the_protocol_30_one()
    {
        var old = Config with { JumpPacks = [Pack with { Slash = JumpPackAttackTests.Slash }] };
        var (press, after, events) = Slash(old, 1, 0f);
        int land = events.FindIndex(e => e.Jump == JumpEventKind.SlashLand), free = after.FindIndex(s => s.Jump == JumpPhase.None);
        Assert.Equal(Vector3.Zero, after[land].Velocity);   // the dead stop
        Assert.Equal(19, free - land);
        Assert.Equal(50f * MathF.Sin(50f * MathF.PI / 180f), after[0].Velocity.Y, 3);   // the pound's dive: 50 m/s leaned 50 degrees from down
        Assert.Equal(old.Hash(), (old with { JumpPacks = [old.JumpPacks[0]] }).Hash());
        Assert.NotEqual(old.Hash(), Config.Hash());
    }

    [Fact]
    public void The_config_hash_covers_air_control_and_the_slash_feel_and_packs_without_them_hash_as_before()
    {
        var plain = JumpPackTests.Config;
        Assert.Equal(plain.Hash(), (plain with { JumpPacks = [JumpPackTests.Pack with { AirSpeed = 0f }] }).Hash());
        var seen = new HashSet<ulong> { plain.Hash() };
        Assert.True(seen.Add((plain with { JumpPacks = [JumpPackTests.Pack with { AirSpeed = 8f, AirAcceleration = 15f }] }).Hash()));
        Assert.True(seen.Add((plain with { JumpPacks = [JumpPackTests.Pack with { AirSpeed = 8f, AirAcceleration = 14f }] }).Hash()));
        Assert.True(seen.Add((plain with { JumpPacks = [JumpPackTests.Pack with { Slash = JumpPackAttackTests.Slash }] }).Hash()));
        Assert.True(seen.Add((plain with { JumpPacks = [JumpPackTests.Pack with { Slash = Snappy }] }).Hash()));
        Assert.True(seen.Add((plain with { JumpPacks = [JumpPackTests.Pack with { Slash = Snappy with { LungeSpeed = 5f } }] }).Hash()));
        Assert.Equal(51, Protocol.Version);
        Assert.Equal(1194, Protocol.MaxSnapshotBytes);
    }

    // ---------------------------------------------------------------- prediction

    [Theory]
    [InlineData(731)]
    [InlineData(732)]
    public void Prediction_matches_the_server_through_steering_and_slashes_under_150ms_rtt_and_5pct_loss(int seed)
    {
        var config = Config with { JumpPacks = [Pack with { RechargeRate = 100f }] };
        var link = new LinkConditions(TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(10), 0.05);
        var h = new Harness(link, seed, 0, config, new ServerOptions { PeerTimeout = TimeSpan.FromMinutes(5) });
        for (int i = 0; i < 2; i++) h.AddClient(nonce: (uint)(700 + i), options: new ClientOptions { RequestedClass = Assault });
        var truth = new SortedDictionary<uint, Dictionary<int, PlayerState>>();
        var predicted = Enumerable.Range(0, 2).Select(_ => new Dictionary<uint, PlayerState>()).ToArray();
        (float, float)[] sticks = [(0, 1), (1, 1), (1, 0), (1, -1), (0, -1), (-1, -1), (-1, 0), (-1, 1)];
        int[] holds = [1, 6, 9, 14];
        PlayerInput Input(int i, uint t)
        {
            uint cycle = (t + (uint)i * 17) / 90, phase = (t + (uint)i * 17) % 90;
            int hold = holds[(int)(cycle % (uint)holds.Length)];
            var buttons = phase < hold ? InputButtons.Jump : InputButtons.None;
            var (x, y) = phase > 8 ? sticks[(int)((cycle + phase / 10) % 8)] : (0f, 0f);   // standing lift-offs, then the stick steers
            if (cycle % 2 == 1 && phase == 20 + cycle % 7) buttons |= InputButtons.Melee;   // slashes at varied heights
            if (cycle % 4 == 2 && phase is >= 3 and < 9) buttons |= InputButtons.Melee;    // a press in the take-off (buffered)
            return new PlayerInput(t, x, y, 0.4f * i + 0.01f * cycle, buttons, -0.3f - 0.05f * (cycle % 5));
        }
        h.Run(TimeSpan.FromSeconds(30), Input, () =>
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
            int slashes = mine.Zip(mine.Skip(1)).Count(p => !p.First.JumpSlash && p.Second.JumpSlash);
            int lunges = mine.Count(p => p.JumpSlash && p.Jump == JumpPhase.Recover && new Vector2(p.Velocity.X, p.Velocity.Y).Length() > 5f);
            int steered = mine.Count(p => JumpPack.InFlight(p.Jump) && new Vector2(p.Velocity.X, p.Velocity.Y).Length() > 3f);   // standing lift-offs: only the stick gives speed
            output.WriteLine($"client {i}: compared {compared}, {mismatches.Count} mismatches; {slashes} slashes, {lunges} lunge ticks, {steered} steered flight ticks");
            foreach (var m in mismatches.Take(3)) output.WriteLine("  " + m);
            Assert.True(compared > 300);
            Assert.True(slashes >= 4 && lunges >= 8 && steered >= 20, $"client {i}: {slashes} slashes, {lunges} lunge ticks, {steered} steered ticks");
            Assert.Empty(mismatches);
        }
    }
}
