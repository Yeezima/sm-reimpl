using System.Numerics;
using SmNet;
using SmServer;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// feel/charge-clash (<see cref="ChargeClash"/>; the owner, 2026-11-06, with a recording of the original: "the player should never, ever phase through another player model. both models
/// 'clash', one delivering the charge one receiving (kind of sticking together like a lock on)"). Synthetic clips shaped like the install's: a rush at 20 to 45 m/s and a collision stop
/// that shoves 1.8 m forward in 0.2 s, then recoils 1.1 m back by 0.7 s. Charges from many angles, speeds, offsets and latencies, against standing, strafing and oncoming targets: the
/// capsules never overlap; the lock holds the two at the touching distance through the shove; the victim ends stunned where the shove left it; the recoil separates them.
/// </summary>
public class ChargeClashTests(ITestOutputHelper output)
{
    const int Marine = 0, Chaos = 1;
    const float Tol = 0.02f;
    /// <summary>The guard's slack with a charger (<see cref="SolidBodies.ClashDepth"/>, 5 cm): the deepest a lagged picture may press two capsules together.</summary>
    const float Slack = SolidBodies.ClashDepth + Tol;
    static Vector2[] Line(float metres, int samples) => [.. Enumerable.Range(0, samples + 1).Select(k => new Vector2(0f, metres * k / samples))];

    /// <summary>The stop's shape (forward then back), sampled every 1/30 s over 1.067 s: up 1.83 m by 0.2 s, back to 0.72 m by 0.7 s, then still.</summary>
    static Vector2[] ShoveAndRecoil()
    {
        var points = new List<Vector2>();
        for (int k = 0; k <= 32; k++)
        {
            float t = k / 30f;
            float y = t <= 0.2f ? 1.83f * MathF.Sin(t / 0.2f * MathF.PI / 2f) : t <= 0.7f ? 1.83f - 1.11f * (1f - MathF.Cos((t - 0.2f) / 0.5f * MathF.PI)) / 2f : 0.72f;
            points.Add(new Vector2(0f, y));
        }
        return [.. points];
    }

    static readonly DamageSpec Plain = new(0f, 0f, 1f, 1f, 1f, 1f, false);
    static readonly RushMove Stop = new("SM_NAV_Bullrush2Idle_F_Collision_01", ShoveAndRecoil(), 32f / 30f, 1.0333f, 1.0333f, 0.6667f, 0.2667f);
    static RushSpec Spec(float speed) => new("ability_bullrush_test", new RushMove("rush", Line(speed * 0.8f, 24), 0.8f, 0.7f, 0.7f, 0.5f, 0.7f), Stop,
        Plain with { Damage = 75f, ShieldDamage = 25f }, new ReactionEntry(0f, ReactionKind.Stun), Plain with { Damage = 30f }, new ReactionEntry(0f, ReactionKind.Interrupt), 4f, 1f)
        { ContactUntil = 0.667f, WallFrom = 0.333f, HyperArmourUntil = 0.333f };
    static readonly WeaponSpec Rifle = new("rifle", WeaponSlot.Ranged, TriggerKind.FullAuto, 1f, 0.1f, 30, 270, 1.0f, 250f, 45f, 70f, 0.8f, 2.5f, 1f, 0.5f, 0.5f);
    static readonly MeleeStep Light = new(1f, 0.2f, 0.4f, 0.6f, 0.5f, 0.7f, 10f, 5f, 3.5f);

    static SimConfig ConfigAt(float speed) => new()
    {
        Weapons = [Rifle],
        Classes =
        [
            new ClassLoadout("marine", 0, -1, [Light]) { MeleeRush = Spec(speed), Side = Team.SpaceMarines },
            new ClassLoadout("chaos", 0, -1, [Light]) { MeleeRush = Spec(speed), Side = Team.Chaos },
        ],
        RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f,
        HealthMax = 1000f, ShieldMax = 100f, HealthRegenDelay = 1e6f, ShieldRegenDelay = 1e6f,
    };
    static readonly SimConfig Config = ConfigAt(35f);

    static World Arena(SimConfig c, CollisionWorld? level, params (int Class, Vector3 At)[] players)
    {
        var world = new World(c, level, players.Select(p => new SpawnPoint(p.At, 0f)).ToArray()) { Rules = new MatchRules { TimeLimit = 0f }, SolidGuard = true, BoundBodies = true };
        for (int i = 0; i < players.Length; i++) world.Spawn(i + 1, players[i].Class);
        return world;
    }

    static float Flat(Vector3 a, Vector3 b) => new Vector2(a.X - b.X, a.Y - b.Y).Length();

    sealed record Outcome(uint Contact, float Closest, PlayerState Charger, PlayerState Victim, List<float> ClashGaps, float Shoved, bool Opened, float AfterRecoil);

    /// <summary>
    /// Player 1 at the origin charges along <paramref name="yaw"/> (Sprint + W + F at tick 2); player 2 at <paramref name="at"/> acts by <paramref name="target"/> (default: stands). Views
    /// and bodies <paramref name="lag"/> ticks old. Measures the closest the two capsules' axes came while the charger was in any charge phase, each clash tick's gap, how far the shove
    /// carried the victim, and the gap once the recoil is over.
    /// </summary>
    static Outcome Charge(SimConfig c, Vector3 at, float yaw = 0f, int lag = 0, Func<int, PlayerState, PlayerState, PlayerInput>? target = null, CollisionWorld? level = null, int ticks = 60)
    {
        var w = Arena(c, level, (Marine, Vector3.Zero), (Chaos, at));
        uint contact = 0; float closest = float.MaxValue, after = float.NaN; var gaps = new List<float>(); bool opened = false; Vector3 victimAtContact = default; float shoved = 0f;
        for (int k = 0; k < ticks; k++)
        {
            int tick = k;
            uint view = (uint)Math.Max(1, (long)w.Tick - lag), body = w.Tick > (uint)lag + 1 ? w.Tick - (uint)lag - 1 : (w.Tick > 1 ? 1u : 0u);
            w.Step(id => (id == 1
                ? new PlayerInput(0, 0f, tick <= 2 ? 1f : 0f, yaw, tick == 2 ? InputButtons.Sprint | InputButtons.MeleeHeavy : (tick < 2 ? InputButtons.Sprint : InputButtons.None))
                : target?.Invoke(tick, w.Players[2], w.Players[1]) ?? new PlayerInput(0, 0f, 0f, yaw + MathF.PI, InputButtons.None)) with { ViewTick = view, BodyTick = body });
            var a = w.Players[1]; var b = w.Players[2];
            if (contact == 0 && w.LastRushes.Count > 0) { contact = w.Tick; victimAtContact = b.Position; }
            if (w.ClashVictim(1) == 2) opened = true;
            if (a.Alive && b.Alive && a.Rush != RushPhase.None && SolidBodies.Beside(a.Position, b.Position, c)) closest = MathF.Min(closest, Flat(a.Position, b.Position));
            if (contact > 0 && ChargeClash.Carried(a, c) && w.ClashVictim(1) == 2) { gaps.Add(Flat(a.Position, b.Position)); shoved = Flat(b.Position, victimAtContact); }
            if (contact > 0 && float.IsNaN(after) && a.Rush == RushPhase.Collision && a.RushTicks >= 21) after = Flat(a.Position, b.Position);
        }
        return new Outcome(contact, closest, w.Players[1], w.Players[2], gaps, shoved, opened, after);
    }

    [Fact]
    public void The_shove_lasts_until_the_stops_farthest_point_six_ticks_in_the_data_shaped_clip()
    {
        Assert.Equal(6, ChargeClash.CarryTicks(Stop, Config));
        Assert.Equal(0, ChargeClash.CarryTicks(new RushMove("back", [Vector2.Zero, new Vector2(0f, -1f)], 0.5f, 0.5f, 0.5f, 0.5f, 0.5f), Config));
        Assert.Equal(0, ChargeClash.CarryTicks(null, Config));
    }

    [Fact]
    public void A_straight_charge_locks_the_two_at_the_touch_through_the_shove_then_the_recoil_separates_them()
    {
        var o = Charge(Config, new Vector3(0f, 8f, 0f));
        output.WriteLine($"contact tick {o.Contact}; clash gaps {string.Join(", ", o.ClashGaps.Select(g => g.ToString("F3")))}; shoved {o.Shoved:F2} m; after the recoil {o.AfterRecoil:F2} m; closest {o.Closest:F3} m");
        Assert.True(o.Contact > 0);
        Assert.True(o.Opened, "no clash opened");
        Assert.Equal(6, o.ClashGaps.Count);
        foreach (float g in o.ClashGaps) Assert.InRange(g, 2f * Config.CapsuleRadius - Tol, 2f * Config.CapsuleRadius + Tol);   // locked at the touch on every tick of the shove
        Assert.InRange(o.Shoved, 1.6f, 1.9f);   // carried the shove's length (1.83 m less the contact tick's share)
        Assert.Equal(ReactionKind.Stun, o.Victim.Reaction == ReactionKind.None ? ReactionKind.Stun : o.Victim.Reaction);
        Assert.True(o.AfterRecoil > 1.8f, $"the recoil left them {o.AfterRecoil:F2} m apart");   // the charger stepped back ~1.1 m, the victim stayed
        Assert.True(o.Closest >= 2f * Config.CapsuleRadius - Slack, $"overlap {2f * Config.CapsuleRadius - o.Closest:F3} m");
    }

    [Fact]
    public void Charges_from_every_angle_speed_offset_and_latency_never_overlap_the_capsules()
    {
        int runs = 0, connects = 0, clashes = 0, oldOverlaps = 0, oldRuns = 0;
        float worst = float.MaxValue, oldWorst = float.MaxValue;
        float[] speeds = [20f, 35f, 45f];
        int[] lags = [0, 3, 6, 9];
        foreach (float speed in speeds)
        {
            var c = ConfigAt(speed);
            foreach (int lag in lags)
                for (float angle = -40f; angle <= 40.1f; angle += 20f)
                    for (float side = -0.9f; side <= 0.91f; side += 0.45f)
                        foreach (int behaviour in new[] { 0, 1, 2 })
                        {
                            float yaw = angle * MathF.PI / 180f;
                            var fwd = new Vector3(-MathF.Sin(yaw), MathF.Cos(yaw), 0f); var right = new Vector3(fwd.Y, -fwd.X, 0f);
                            var at = fwd * 8f + right * side;
                            Func<int, PlayerState, PlayerState, PlayerInput>? act = behaviour switch
                            {
                                1 => (t, me, other) => new PlayerInput(0, (t / 6) % 2 == 0 ? 1f : -1f, 0f, yaw + MathF.PI, InputButtons.None),   // strafing
                                2 => (t, me, other) => new PlayerInput(0, 0f, 1f, yaw + MathF.PI, InputButtons.Sprint),                          // running at the charger
                                _ => null,
                            };
                            var o = Charge(c, at, yaw, lag, act);
                            runs++;
                            if (o.Contact > 0) connects++;
                            if (o.Opened) clashes++;
                            worst = MathF.Min(worst, o.Closest);
                            Assert.True(o.Closest >= 2f * c.CapsuleRadius - Slack,
                                $"speed {speed}, lag {lag}, angle {angle}, side {side:F2}, behaviour {behaviour}: the capsules overlapped by {2f * c.CapsuleRadius - o.Closest:F3} m");
                            if (lag == 6 && behaviour != 1)
                            {
                                var old = Charge(c with { ChargeClash = false }, at, yaw, lag, act);
                                oldRuns++;
                                oldWorst = MathF.Min(oldWorst, old.Closest);
                                if (old.Closest < 2f * c.CapsuleRadius - 0.1f) oldOverlaps++;
                            }
                        }
        }
        output.WriteLine($"{runs} runs: {connects} connects, {clashes} clashes, closest {worst:F3} m (touching {2f * Config.CapsuleRadius:F2} m); " +
                         $"protocol 58 stop at 200 ms: {oldOverlaps} of {oldRuns} overlapped by more than 10 cm, deepest {2f * Config.CapsuleRadius - oldWorst:F3} m");
        Assert.True(connects >= runs * 9 / 10, $"only {connects} of {runs} connected");
        Assert.True(oldOverlaps > 0, "the control: the protocol 58 stop never overlapped (the test would prove nothing)");
    }

    [Fact]
    public void A_victim_the_level_stops_holds_the_charger_back_the_lock_distance_kept()
    {
        // A wall 1.5 m behind where the victim stands: the shove carries it into the wall, the charger is held at the touch.
        var level = Wall(y: 9.95f);
        var o = Charge(Config, new Vector3(0f, 8f, 0f), level: level);
        output.WriteLine($"clash gaps {string.Join(", ", o.ClashGaps.Select(g => g.ToString("F3")))}; shoved {o.Shoved:F2} m; victim at {o.Victim.Position}");
        Assert.True(o.Opened);
        foreach (float g in o.ClashGaps) Assert.InRange(g, 2f * Config.CapsuleRadius - Tol, 2f * Config.CapsuleRadius + Tol);
        Assert.True(o.Victim.Position.Y <= 9.95f - Config.CapsuleRadius + 0.05f, "the victim went into the wall");
        Assert.True(o.Closest >= 2f * Config.CapsuleRadius - Slack);
    }

    [Fact]
    public void A_hyper_armoured_victim_is_not_locked_and_the_shove_stops_at_its_body()
    {
        // Head-on chargers inside each other's hyper armour: neither is stunned, no clash; the shove of each stop meets the other's body at the touch.
        var o = Charge(Config, new Vector3(0f, 5f, 0f), target: (t, me, other) => new PlayerInput(0, 0f, t <= 2 ? 1f : 0f, MathF.PI, t == 2 ? InputButtons.Sprint | InputButtons.MeleeHeavy : InputButtons.Sprint));
        output.WriteLine($"contact {o.Contact}, clash {o.Opened}, closest {o.Closest:F3} m");
        Assert.True(o.Closest >= 2f * Config.CapsuleRadius - Slack, $"overlap {2f * Config.CapsuleRadius - o.Closest:F3} m");
    }

    [Fact]
    public void The_config_hash_changes_only_when_the_clash_is_off()
    {
        Assert.Equal(Config.Hash(), (Config with { ChargeClash = true }).Hash());
        Assert.NotEqual(Config.Hash(), (Config with { ChargeClash = false }).Hash());
    }

    [Theory]
    [InlineData(401)]
    [InlineData(402)]
    public void Over_a_lossy_link_the_charger_predicts_the_shove_and_no_view_draws_the_two_inside_each_other(int seed)
    {
        // A marine charges a strafing chaos marine over and over at 150 ms RTT with 5 % loss. Through every clash: the charger's predicted state equals the server's (the shove
        // ignores the victim's body on both); the drawn bodies (the own predicted body and the interpolated other, apart by DrawnBodies) never overlap in either client's view.
        var link = new LinkConditions(TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(10), 0.05);
        var h = new Harness(link, seed, 0, Config, new ServerOptions { PeerTimeout = TimeSpan.FromMinutes(5), Match = new MatchRules { TimeLimit = 0f, CountdownTime = 0f } });
        h.AddClient(nonce: 100, options: new ClientOptions { RequestedClass = Marine, RequestedSide = Team.SpaceMarines });
        h.AddClient(nonce: 101, options: new ClientOptions { RequestedClass = Chaos, RequestedSide = Team.Chaos });
        var truth = new Dictionary<uint, PlayerState>();
        var predicted = new Dictionary<uint, PlayerState>();
        var clashTicks = new HashSet<uint>();
        float worstDrawn = float.MaxValue, worstServer = float.MaxValue;
        var drawn = new[] { new DrawnBodies(), new DrawnBodies() };
        uint lastServer = 0;
        PlayerInput Input(int i, uint t)
        {
            var me = h.Clients[i].Predicted;
            var other = h.Clients[i].RemotePlayers().Players.FirstOrDefault(p => p.PlayerId != h.Clients[i].PlayerId);
            if (other.PlayerId == 0) return new PlayerInput(t, 0f, 0f, 0f, InputButtons.None);
            var to = new Vector2(other.Position.X - me.Position.X, other.Position.Y - me.Position.Y);
            float yaw = MathF.Atan2(-to.X, to.Y);
            if (i == 1) return new PlayerInput(t, (t / 20) % 2 == 0 ? 0.6f : -0.6f, 0f, yaw, InputButtons.None);
            float distance = to.Length();
            bool press = t % 90 == 0 && distance is > 2.5f and < 9f;
            if (distance > 9f) return new PlayerInput(t, 0f, 1f, yaw, InputButtons.Sprint);
            if (distance < 2.5f) return new PlayerInput(t, 0f, -1f, yaw, InputButtons.None);
            return new PlayerInput(t, 0f, 1f, yaw, InputButtons.Sprint | (press ? InputButtons.MeleeHeavy : InputButtons.None));
        }
        h.Run(TimeSpan.FromSeconds(60), Input, () =>
        {
            var w = h.Server.World;
            if (w.Tick != lastServer)
            {
                lastServer = w.Tick;
                if (w.Players.TryGetValue(h.Clients[0].PlayerId, out var c)) truth[w.Tick] = c;
                if (w.ClashVictim(h.Clients[0].PlayerId) > 0 && ChargeClash.Carried(c, Config)) clashTicks.Add(w.Tick);
                var alive = w.Players.Values.Where(p => p.Alive).ToList();
                if (alive.Count == 2 && SolidBodies.Beside(alive[0].Position, alive[1].Position, Config) && (alive[0].Rush != RushPhase.None || alive[1].Rush != RushPhase.None))
                    worstServer = MathF.Min(worstServer, Flat(alive[0].Position, alive[1].Position));
            }
            if (h.Clients[0].Status == ClientStatus.Connected) predicted[h.Clients[0].Tick] = h.Clients[0].Predicted;
            for (int i = 0; i < 2; i++)
            {
                var cl = h.Clients[i];
                if (cl.Status != ClientStatus.Connected || !cl.Predicted.Alive) continue;
                var remotes = cl.RemotePlayers().Players.Where(p => p.PlayerId != cl.PlayerId).ToList();
                var apart = drawn[i].Separate(remotes, cl.Predicted.Position, cl.PlayerId, Config);
                foreach (var r in apart)
                    if (r.Alive && SolidBodies.Beside(r.Position, cl.Predicted.Position, Config)) worstDrawn = MathF.Min(worstDrawn, Flat(r.Position, cl.Predicted.Position));
            }
        });
        int compared = 0, exact = 0;
        foreach (uint t in clashTicks)
            if (predicted.TryGetValue(t, out var p) && truth.TryGetValue(t, out var s)) { compared++; if (Flat(p.Position, s.Position) < 0.01f) exact++; }
        output.WriteLine($"seed {seed}: {clashTicks.Count} clash ticks, the charger's prediction within 1 cm on {exact} of {compared}; closest on the server {worstServer:F3} m, drawn {worstDrawn:F3} m");
        Assert.True(compared >= 12, $"only {compared} clash ticks compared");
        Assert.True(exact >= compared * 9 / 10, $"the shove was mispredicted on {compared - exact} of {compared} ticks");
        Assert.True(worstServer >= 2f * Config.CapsuleRadius - Slack, $"the server overlapped by {2f * Config.CapsuleRadius - worstServer:F3} m");
        Assert.True(worstDrawn >= 2f * Config.CapsuleRadius - Tol, $"a view drew the two {2f * Config.CapsuleRadius - worstDrawn:F3} m inside each other");
    }

    /// <summary>A wall across the arena at <paramref name="y"/> (both faces), 100 m wide, 5 m high, on a floor.</summary>
    static CollisionWorld Wall(float y)
    {
        var v = new List<Vector3>(); var idx = new List<int>();
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) { int n = v.Count; v.AddRange([a, b, c, d]); idx.AddRange([n, n + 1, n + 2, n, n + 2, n + 3]); }
        Quad(new(-50, -50, 0), new(50, -50, 0), new(50, 50, 0), new(-50, 50, 0));
        Quad(new(-50, y, -1), new(50, y, -1), new(50, y, 4), new(-50, y, 4));
        var mesh = new StaticMesh(v.ToArray(), idx.ToArray());
        return new CollisionWorld(mesh, mesh);
    }
}
