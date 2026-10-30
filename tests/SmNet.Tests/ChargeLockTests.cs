using System.Numerics;
using SmNet;
using SmServer;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// anim/melee-interaction (<see cref="ChargeLock"/>; the owner's request of 2026-10-31): a charge that connects locks the two together on every connect: the charger stops in the
/// collision stop (the wall-hit recoil clip) facing the victim, the stunned victim stands 0.9 m in front of it on that line; a blocking body counts as a contact (a player running at the
/// charger is met when the bodies touch, not a lag later); the own client predicts the contact tick exactly. Each rule is also run with the lock off (<c>--no-charge-lock</c>) to show
/// the old inconsistency the tests measure. Synthetic clips (<see cref="RushTests.Spec"/>: 10 m/s for 1.2 s, contacts until 0.6 s).
/// </summary>
public class ChargeLockTests(ITestOutputHelper output)
{
    const int Marine = 0, Chaos = 1;
    static readonly WeaponSpec Rifle = new("rifle", WeaponSlot.Ranged, TriggerKind.FullAuto, 1f, 0.1f, 30, 270, 1.0f, 250f, 45f, 70f, 0.8f, 2.5f, 1f, 0.5f, 0.5f);
    static readonly MeleeStep Light = new(1f, 0.2f, 0.4f, 0.6f, 0.5f, 0.7f, 10f, 5f, 3.5f);

    static readonly SimConfig Config = new()
    {
        Weapons = [Rifle],
        Classes =
        [
            new ClassLoadout("marine", 0, -1, [Light]) { MeleeRush = RushTests.Spec, Side = Team.SpaceMarines },
            new ClassLoadout("chaos", 0, -1, [Light]) { MeleeRush = RushTests.Spec, Side = Team.Chaos },
        ],
        RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f,
        HealthMax = 1000f, ShieldMax = 100f, HealthRegenDelay = 1e6f, ShieldRegenDelay = 1e6f,
    };

    static World Arena(SimConfig c, params (int Class, Vector3 At)[] players)
    {
        var world = new World(c, null, players.Select(p => new SpawnPoint(p.At, 0f)).ToArray()) { Rules = new MatchRules { TimeLimit = 0f } };
        for (int i = 0; i < players.Length; i++) world.Spawn(i + 1, players[i].Class);
        return world;
    }

    /// <summary>Player 1 charges +Y (F with W at <paramref name="pressAt"/>); the others act by <paramref name="other"/> (default: stand facing −Y). View and body ticks by <paramref name="lag"/>.</summary>
    static (uint Tick, PlayerState Charger, PlayerState Victim)? Run(World w, int ticks, int pressAt, int lag = 0, Func<int, int, PlayerInput>? other = null, float yaw = 0f)
    {
        (uint, PlayerState, PlayerState)? contact = null;
        for (int k = 0; k < ticks; k++)
        {
            int tick = k;
            uint view = (uint)Math.Max(1, (long)w.Tick - lag), body = w.Tick > 1 ? w.Tick - 1 : 0;
            w.Step(id => (id == 1
                ? new PlayerInput(0, 0f, tick <= pressAt ? 1f : 0f, yaw, tick == pressAt ? InputButtons.MeleeHeavy : InputButtons.None)
                : other?.Invoke(tick, id) ?? new PlayerInput(0, 0f, 0f, MathF.PI, InputButtons.None)) with { ViewTick = view, BodyTick = body });
            if (contact is null && w.LastRushes.Count > 0) contact = (w.Tick, w.Players[1], w.Players[w.LastRushes[0].Target]);
        }
        return contact;
    }

    static float Flat(Vector3 a, Vector3 b) => new Vector2(a.X - b.X, a.Y - b.Y).Length();
    static float Bearing(Vector3 from, Vector3 to) => MathF.Atan2(-(to.X - from.X), to.Y - from.Y);
    static float AngleBetween(float a, float b) { float d = a - b; d -= MathF.Round(d / (2f * MathF.PI)) * 2f * MathF.PI; return MathF.Abs(d); }

    [Theory]
    [InlineData(0f, 6f)]
    [InlineData(0.8f, 6f)]      // off the heading: met within the contact angle
    [InlineData(-0.9f, 5f)]
    public void A_connect_locks_the_two_together_facing_each_other_at_the_contact_distance(float x, float y)
    {
        var w = Arena(Config, (Marine, Vector3.Zero), (Chaos, new Vector3(x, y, 0f)));
        var c = Run(w, 40, 5);
        Assert.NotNull(c);
        var (_, charger, victim) = c.Value;
        float apart = Flat(charger.Position, victim.Position);
        float facing = AngleBetween(charger.Yaw, Bearing(charger.Position, victim.Position));
        output.WriteLine($"victim at ({x}, {y}): apart {apart:F3} m, charger heading off the victim {facing * 180f / MathF.PI:F2} deg, charger {charger.Rush}, victim {victim.Reaction}");
        Assert.Equal(RushPhase.Collision, charger.Rush);   // the data's collision stop: the wall-hit recoil clip
        Assert.Equal(ReactionKind.Stun, victim.Reaction);  // the stun
        Assert.Equal(ChargeLock.LockDistance(Config), apart, 2);
        Assert.True(facing < 0.01f, $"charger faces {facing * 180f / MathF.PI:F2} deg off the victim");
        // Without the lock (before): the distance and the facing are whatever the tick's move left.
        var off = Arena(Config with { ChargeLock = false }, (Marine, Vector3.Zero), (Chaos, new Vector3(x, y, 0f)));
        var o = Run(off, 40, 5);
        Assert.NotNull(o);
        output.WriteLine($"  without the lock: apart {Flat(o.Value.Charger.Position, o.Value.Victim.Position):F3} m, off the victim {AngleBetween(o.Value.Charger.Yaw, Bearing(o.Value.Charger.Position, o.Value.Victim.Position)) * 180f / MathF.PI:F2} deg");
    }

    [Fact]
    public void The_lock_holds_through_the_impact_the_two_neither_overlap_nor_slide_apart_sideways()
    {
        var w = Arena(Config, (Marine, Vector3.Zero), (Chaos, new Vector3(0.5f, 6f, 0f)));
        uint contact = 0; Vector2 line = default;
        float min = float.MaxValue, maxSide = 0f;
        for (int k = 0; k < 40; k++)
        {
            Run(w, 1, k == 4 ? 0 : -1);
            var a = w.Players[1]; var b = w.Players[2];
            if (contact == 0 && w.LastRushes.Count > 0) { contact = w.Tick; line = Vector2.Normalize(new Vector2(b.Position.X - a.Position.X, b.Position.Y - a.Position.Y)); }
            if (contact > 0 && w.Tick <= contact + 6)   // the impact window (0.2 s)
            {
                var d = new Vector2(b.Position.X - a.Position.X, b.Position.Y - a.Position.Y);
                min = MathF.Min(min, d.Length());
                maxSide = MathF.Max(maxSide, MathF.Abs(d.X * line.Y - d.Y * line.X));
            }
        }
        output.WriteLine($"contact tick {contact}: closest {min:F3} m, sideways drift {maxSide:F3} m in the 0.2 s after");
        Assert.True(contact > 0);
        Assert.True(min >= 2f * Config.CapsuleRadius - 0.02f, $"overlap: {min:F3} m");
        Assert.True(maxSide < 0.05f, $"slid sideways {maxSide:F3} m");
    }

    [Fact]
    public void A_player_running_at_the_charger_is_met_when_the_bodies_touch_not_a_lag_later()
    {
        // The victim runs at the charger; the charger's view is 10 ticks old (0.33 s: lag compensation of a 150 ms link plus the interpolation delay), its bodies the newest snapshot.
        PlayerInput Runner(int tick, int id) => new(0, 0f, 1f, MathF.PI, InputButtons.None);
        var w = Arena(Config, (Marine, Vector3.Zero), (Chaos, new Vector3(0f, 9f, 0f)));
        var on = Run(w, 40, 6, lag: 10, other: Runner);
        var o = Arena(Config with { ChargeLock = false }, (Marine, Vector3.Zero), (Chaos, new Vector3(0f, 9f, 0f)));
        var off = Run(o, 40, 6, lag: 10, other: Runner);
        output.WriteLine($"with the lock: contact at tick {on?.Tick.ToString() ?? "none"} apart {(on is { } a ? Flat(a.Charger.Position, a.Victim.Position) : 0f):F2} m; without: {off?.Tick.ToString() ?? "none"} apart {(off is { } b ? Flat(b.Charger.Position, b.Victim.Position) : 0f):F2} m");
        Assert.NotNull(on);
        Assert.True(off is null || off.Value.Tick > on.Value.Tick, "the blocking body is met first");
        Assert.Equal(ChargeLock.LockDistance(Config), Flat(on.Value.Charger.Position, on.Value.Victim.Position), 2);
    }

    [Fact]
    public void An_ally_only_stops_the_charge_and_is_not_moved()
    {
        var w = Arena(Config, (Marine, Vector3.Zero), (Marine, new Vector3(0.4f, 6f, 0f)));
        var c = Run(w, 40, 5);
        Assert.NotNull(c);
        Assert.Equal(new Vector3(0.4f, 6f, 0f), c.Value.Victim.Position);
        Assert.Equal((RushPhase.Collision, ReactionKind.None), (c.Value.Charger.Rush, c.Value.Victim.Reaction));
    }

    [Theory]
    [InlineData(301, true)]
    [InlineData(302, true)]
    [InlineData(301, false)]
    public void The_own_client_predicts_the_contact_tick_exactly_under_150ms_rtt(int seed, bool locked)
    {
        // Two clients charge each other every 4 s (aimed at the other's server position); the charger's predicted state on the contact tick is compared with the server's.
        var config = Config with
        {
            Classes = Config.Classes.Select(k => k with { MeleeRush = RushTests.Spec with { Primary = new DamageSpec(0f, 0f, 1f, 1f, 1f, 1f, false) }, Side = Team.None }).ToArray(),
            Weapons = [Rifle with { Damage = 0f, ReserveSize = 5000 }],
            ChargeLock = locked,
        };
        var link = new LinkConditions(TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(10), 0.0);
        var h = new Harness(link, seed, 0, config, new ServerOptions { PeerTimeout = TimeSpan.FromMinutes(5) });
        for (int i = 0; i < 2; i++) h.AddClient(nonce: (uint)(100 + i), options: new ClientOptions { RequestedClass = Marine });
        var truth = new Dictionary<uint, Dictionary<int, PlayerState>>();
        var predicted = new[] { new Dictionary<uint, PlayerState>(), new Dictionary<uint, PlayerState>() };
        var contacts = new HashSet<(uint Tick, int Charger)>();
        var clientContacts = new[] { new SortedSet<uint>(), new SortedSet<uint>() };
        PlayerInput Input(int i, uint t)
        {
            var me = h.Clients[i];
            var players = h.Server.World.Players;
            int other = players.Keys.FirstOrDefault(id => id != me.PlayerId);
            float yaw = 0f;
            if (players.TryGetValue(me.PlayerId, out var a) && players.TryGetValue(other, out var b)) yaw = MathF.Atan2(-(b.Position.X - a.Position.X), b.Position.Y - a.Position.Y);
            uint phase = (t + (uint)i * 60) % 120;
            if (phase < 30) return new PlayerInput(t, 0f, -1f, yaw, InputButtons.None);
            if (phase is >= 40 and < 52) return new PlayerInput(t, 0f, 1f, yaw, InputButtons.Sprint | (phase == 51 ? InputButtons.MeleeHeavy : InputButtons.None));
            return new PlayerInput(t, 0f, 0f, yaw, InputButtons.None);
        }
        h.Run(TimeSpan.FromSeconds(40), Input, () =>
        {
            truth[h.Server.World.Tick] = new Dictionary<int, PlayerState>(h.Server.World.Players);
            foreach (var r in h.Server.World.LastRushes) contacts.Add((r.Tick, r.Player));
            for (int i = 0; i < 2; i++) if (h.Clients[i].Status == ClientStatus.Connected) predicted[i][h.Clients[i].Tick] = h.Clients[i].Predicted;
            for (int i = 0; i < 2; i++) if (h.Clients[i].LastChargeContact.Tick > 0) clientContacts[i].Add(h.Clients[i].LastChargeContact.Tick);
        });
        for (int i = 0; i < 2; i++) output.WriteLine($"client {i} (P{h.Clients[i].PlayerId}) predicted contacts at {string.Join(",", clientContacts[i])}; server {string.Join(",", contacts.Where(x => x.Charger == h.Clients[i].PlayerId).Select(x => x.Tick).OrderBy(x => x))}");
        int exact = 0, compared = 0;
        foreach (var (tick, charger) in contacts)
        {
            int i = h.Clients[0].PlayerId == charger ? 0 : 1;
            if (!predicted[i].TryGetValue(tick, out var pred) || !truth.TryGetValue(tick, out var world) || !world.TryGetValue(charger, out var real) || !real.Alive) continue;
            compared++;
            if (pred == real) exact++;
            else if (compared - exact <= 3) output.WriteLine($"  tick {tick}: predicted {pred.Rush} yaw {pred.Yaw:F3} at {pred.Position}; server {real.Rush} yaw {real.Yaw:F3} at {real.Position}");
        }
        for (int i = 0; i < 2; i++) output.WriteLine($"client {i}: rush ticks checked {h.Clients[i].ChargeContactChecks}, without a view {h.Clients[i].ChargeContactNoView}, contacts predicted {h.Clients[i].ChargeContactsPredicted}");
        output.WriteLine($"lock {(locked ? "on" : "off")}: contacts {contacts.Count}, compared {compared}, the contact tick predicted exactly {exact}");
        Assert.True(compared >= 4, $"only {compared} contacts compared");
        if (locked) Assert.Equal(compared, exact);
        else Assert.Equal(0, exact);   // before: the own client ran on in the rush for a round trip
    }

    // ---------------------------------------------------------------- the pull's limits (review)

    static CollisionWorld Mesh(Action<Action<Vector3, Vector3, Vector3, Vector3>> build, IReadOnlyList<KillVolume>? kill = null)
    {
        var v = new List<Vector3>(); var idx = new List<int>();
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) { int n = v.Count; v.AddRange([a, b, c, d]); idx.AddRange([n, n + 1, n + 2, n, n + 2, n + 3]); }
        build(Quad);
        var mesh = new StaticMesh(v.ToArray(), idx.ToArray());
        return new CollisionWorld(mesh, mesh, kill);
    }

    static PlayerState Victim(float y = 0f) => new PlayerState(2, new Vector3(0f, y, 0f), Vector3.Zero, MathF.PI, true) { Alive = true, Health = 100f };
    /// <summary>The charger 3.9 m up +Y facing −Y: the lock spot is (0, 3) for a victim pulled toward +Y.</summary>
    static readonly Vector3 Charger = new(0f, 3.9f, 0f);
    static readonly Vector2 Line = new(0f, -1f);

    [Fact]
    public void The_pull_reaches_the_spot_on_open_ground_and_is_capped_at_2_5_m()
    {
        var open = Mesh(q => q(new(-50, -50, 0), new(50, -50, 0), new(50, 50, 0), new(-50, 50, 0)));
        var near = ChargeLock.Pull(Victim(2f), Charger, Line, Config, open);
        Assert.Equal(3f, near.Position.Y, 2);
        var far = ChargeLock.Pull(Victim(-4f), Charger, Line, Config, open);   // 7 m from the spot: moved 2.5 m
        Assert.Equal(-1.5f, far.Position.Y, 2);
        Assert.Equal(-1.5f, ChargeLock.Pull(Victim(-4f), Charger, Line, Config, null).Position.Y, 3);
    }

    [Fact]
    public void A_wall_stops_the_pull_short()
    {
        var walled = Mesh(q => { q(new(-50, -50, 0), new(50, -50, 0), new(50, 50, 0), new(-50, 50, 0)); q(new(-50, 1.5f, -1), new(50, 1.5f, -1), new(50, 1.5f, 4), new(-50, 1.5f, 4)); });
        var p = ChargeLock.Pull(Victim(), Charger, Line, Config, walled);
        output.WriteLine($"wall at y 1.5: victim stops at y {p.Position.Y:F3}");
        Assert.True(p.Position.Y <= 1.5f - Config.CapsuleRadius + 0.02f && p.Position.Y > 0.5f);
    }

    [Fact]
    public void The_pull_never_takes_a_victim_off_a_ledge()
    {
        // A platform at z 0 ending at y 1, a floor 3 m below beyond it.
        var ledge = Mesh(q =>
        {
            q(new(-50, -50, 0), new(50, -50, 0), new(50, 1, 0), new(-50, 1, 0));
            q(new(-50, 1, -3), new(50, 1, -3), new(50, 50, -3), new(-50, 50, -3));
            q(new(-50, 1, -3), new(50, 1, -3), new(50, 1, 0), new(-50, 1, 0));
        });
        var p = ChargeLock.Pull(Victim(), Charger, Line, Config, ledge);
        output.WriteLine($"ledge at y 1: victim stops at {p.Position} grounded {p.Grounded}");
        Assert.True(p.Grounded);
        Assert.True(p.Position.Z >= -ChargeLock.MaxPullDrop - 1e-3f && p.Position.Y < 1f + Config.CapsuleRadius, "still on the platform");
        Assert.True(p.Position.Y > 0.3f, "it still moved toward the spot");
    }

    [Fact]
    public void The_pull_stops_before_a_kill_volume()
    {
        var pit = new KillVolume(new Vector3(0f, 2f, 0.5f), new Vector3(5f, 0.5f, 1f), 0f);
        var floor = Mesh(q => q(new(-50, -50, 0), new(50, -50, 0), new(50, 50, 0), new(-50, 50, 0)), [pit]);
        var p = ChargeLock.Pull(Victim(), Charger, Line, Config, floor);
        output.WriteLine($"kill volume from y 1.5: victim stops at y {p.Position.Y:F3}");
        Assert.False(floor.KillsPlayer(Victim().Position, p.Position, Config.CapsuleHeight));
        Assert.True(p.Position.Y > 1f && p.Position.Y < 1.5f);
    }

    [Fact]
    public void The_pull_stops_short_of_a_third_player_and_of_a_teammate_in_the_world()
    {
        var p = ChargeLock.Pull(Victim(), Charger, Line, Config, null, [new Vector3(0f, 1.6f, 0f)]);
        output.WriteLine($"third player at y 1.6: victim stops at y {p.Position.Y:F3}");
        Assert.True(1.6f - p.Position.Y >= 2f * Config.CapsuleRadius - 1e-3f);
        Assert.True(p.Position.Y > 0.5f);
        // In the world: an ally of the victim stands at the lock spot's side; the victim ends clear of it.
        var w = Arena(Config, (Marine, Vector3.Zero), (Chaos, new Vector3(0.6f, 6f, 0f)), (Chaos, new Vector3(0.6f, 3.6f, 0f)));
        var c = Run(w, 40, 5);
        Assert.NotNull(c);
        int victimId = c.Value.Victim.PlayerId, otherId = victimId == 2 ? 3 : 2;
        float gap = Flat(w.Players[victimId].Position, w.Players[otherId].Position);
        output.WriteLine($"world: victim P{c.Value.Victim.PlayerId}, gap to the other {gap:F3} m");
        Assert.True(gap >= 2f * Config.CapsuleRadius - 0.02f, $"inside another player: {gap:F3} m");
    }
}
