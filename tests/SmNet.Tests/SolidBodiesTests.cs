using System.Numerics;
using SmNet;
using SmServer;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// fix/charge-player-collision (<see cref="SolidBodies"/>; the owner's playtest of 2026-11-06: "Sometimes you can charge through an enemy instead of impacting on them ... ensure that
/// players never pass directly through other players ... Ensure the charge clash and stun and recoil animations play more reliably"). A fast synthetic charge (35 m/s, 1.17 m a tick:
/// the install's bull rush bursts at 28 to 41 m/s) is run at every lateral offset and tick phase against a standing, a dodging and a lagging target; walkers are run through each
/// other with a wrong picture; the clash, stun and recoil states are followed through the world's tick, the snapshots and the clients' views. Each rule is also run with the
/// protocol 56 rules (<c>--old-body-contact</c>) to show what the tests measure.
/// </summary>
public class SolidBodiesTests(ITestOutputHelper output)
{
    const int Marine = 0, Chaos = 1;
    const float Tol = 0.02f;
    static Vector2[] Line(float metres, int samples) => [.. Enumerable.Range(0, samples + 1).Select(k => new Vector2(0f, metres * k / samples))];

    static readonly DamageSpec Plain = new(0f, 0f, 1f, 1f, 1f, 1f, false);
    /// <summary>35 m/s for 0.6 s (21 m), contacts until 0.5 s, no wall stops before 0.3 s; the stop clip is RushTests'.</summary>
    static readonly RushMove FastClip = new("fast_rush", Line(21f, 21), 0.6f, 0.6f, 0.6f, 0.6f, 0.6f);
    static readonly RushMove StopClip = new("SM_NAV_Bullrush2Idle_F_Collision_01", Line(0.5f, 10), 1.0f, 0.9f, 0.9f, 0.6f, 0.3f);
    static readonly RushSpec Fast = new("ability_bullrush_fast", FastClip, StopClip, Plain with { Damage = 75f, ShieldDamage = 25f }, new ReactionEntry(0f, ReactionKind.Stun),
        Plain with { Damage = 30f }, new ReactionEntry(0f, ReactionKind.Interrupt), 4f, 1f) { ContactUntil = 0.5f, WallFrom = 0.3f, HyperArmourUntil = 0.2f };
    static readonly WeaponSpec Rifle = new("rifle", WeaponSlot.Ranged, TriggerKind.FullAuto, 1f, 0.1f, 30, 270, 1.0f, 250f, 45f, 70f, 0.8f, 2.5f, 1f, 0.5f, 0.5f);
    static readonly MeleeStep Light = new(1f, 0.2f, 0.4f, 0.6f, 0.5f, 0.7f, 10f, 5f, 3.5f);

    static readonly SimConfig Config = new()
    {
        Weapons = [Rifle],
        Classes =
        [
            new ClassLoadout("marine", 0, -1, [Light]) { MeleeRush = Fast, Side = Team.SpaceMarines },
            new ClassLoadout("chaos", 0, -1, [Light]) { MeleeRush = Fast, Side = Team.Chaos },
        ],
        RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f,
        HealthMax = 1000f, ShieldMax = 100f, HealthRegenDelay = 1e6f, ShieldRegenDelay = 1e6f,
    };

    static World Arena(SimConfig c, params (int Class, Vector3 At)[] players)
    {
        var world = new World(c, null, players.Select(p => new SpawnPoint(p.At, 0f)).ToArray()) { Rules = new MatchRules { TimeLimit = 0f }, SolidGuard = true };   // the server's world
        for (int i = 0; i < players.Length; i++) world.Spawn(i + 1, players[i].Class);
        return world;
    }

    static float Flat(Vector3 a, Vector3 b) => new Vector2(a.X - b.X, a.Y - b.Y).Length();

    /// <summary>What one charge run did: the first contact tick, the closest the two came up to it (the whole run without one) and after it, how far the charger's axis got past the
    /// target's along the charge up to it, the end states, and whether the charger's axis ever went from before the target's to beyond it with the bodies in line (through it).</summary>
    sealed record Outcome(uint Contact, float Closest, float Past, PlayerState Charger, PlayerState Target, int Stops, float After, bool Crossed);

    /// <summary>
    /// Player 1 (at the origin) charges +Y (Sprint + F with W on tick <paramref name="press"/>); player 2 acts by <paramref name="target"/> (default: stands). Views and bodies are
    /// <paramref name="lag"/> ticks old (0: the newest).
    /// </summary>
    static Outcome Charge(SimConfig c, Vector3 at, int press = 2, int lag = 0, Func<int, PlayerState, PlayerInput>? target = null, int ticks = 40)
    {
        var w = Arena(c, (Marine, Vector3.Zero), (Chaos, at));
        uint contact = 0; float closest = float.MaxValue, past = float.MinValue, after = float.MaxValue; int stops = 0;
        bool crossed = false; var last = w.Players[1].Position - w.Players[2].Position;
        for (int k = 0; k < ticks; k++)
        {
            int tick = k;
            uint view = (uint)Math.Max(1, (long)w.Tick - lag), body = w.Tick > (uint)lag + 1 ? w.Tick - (uint)lag - 1 : (w.Tick > 1 ? 1u : 0u);
            w.Step(id => (id == 1
                ? new PlayerInput(0, 0f, tick <= press ? 1f : 0f, 0f, tick == press ? InputButtons.Sprint | InputButtons.MeleeHeavy : InputButtons.None)
                : target?.Invoke(tick, w.Players[2]) ?? new PlayerInput(0, 0f, 0f, MathF.PI, InputButtons.None)) with { ViewTick = view, BodyTick = body });
            stops += w.LastGuardStops;
            var a = w.Players[1]; var b = w.Players[2];
            if (contact == 0 && w.LastRushes.Count > 0) contact = w.Tick;
            if (contact == 0 || w.Tick == contact)
            {
                closest = MathF.Min(closest, Flat(a.Position, b.Position));
                if (MathF.Abs(a.Position.X - b.Position.X) < 2f * c.CapsuleRadius) past = MathF.Max(past, a.Position.Y - b.Position.Y);   // the charger's axis beyond the target's, the bodies in line
            }
            else after = MathF.Min(after, Flat(a.Position, b.Position));
            var rel = a.Position - b.Position;
            if (last.Y < 0f && rel.Y >= 0f && MathF.Abs(last.X) < 2f * c.CapsuleRadius && MathF.Abs(rel.X) < 2f * c.CapsuleRadius) crossed = true;
            last = rel;
        }
        return new Outcome(contact, closest, past, w.Players[1], w.Players[2], stops, after, crossed);
    }

    // ---------------------------------------------------------------- the charge meets every body in its path

    [Fact]
    public void A_fast_charge_meets_a_standing_body_at_every_offset_and_tick_phase_and_never_passes_or_enters_it()
    {
        int runs = 0, connects = 0, oldMisses = 0, oldPast = 0;
        float worstClosest = float.MaxValue, worstPast = float.MinValue;
        for (float x = -1.15f; x <= 1.151f; x += 0.115f)
            for (float phase = 0f; phase < 1.17f; phase += 0.13f)
            {
                var at = new Vector3(x, 6f + phase, 0f);
                var o = Charge(Config, at);
                runs++;
                if (o.Contact > 0) connects++;
                worstClosest = MathF.Min(worstClosest, o.Closest);
                worstPast = MathF.Max(worstPast, o.Past);
                Assert.True(o.Contact > 0, $"target at ({x:F2}, {at.Y:F2}): the charge passed it without a contact (closest {o.Closest:F2} m)");
                Assert.True(o.Past < 0f, $"target at ({x:F2}, {at.Y:F2}): the charger's axis went {o.Past:F2} m past the target's");
                Assert.True(o.Closest >= 2f * Config.CapsuleRadius - Tol, $"target at ({x:F2}, {at.Y:F2}): the bodies overlapped by {2f * Config.CapsuleRadius - o.Closest:F2} m");
                Assert.Equal(RushPhase.Collision, o.Charger.Rush == RushPhase.Collision || o.Charger.RushTicks > 0 ? o.Charger.Rush : RushPhase.Collision);
                // The protocol 56 rules for comparison.
                var old = Charge(Config with { SolidBodies = false }, at);
                if (old.Contact == 0) oldMisses++;
                if (old.Past > 0f) oldPast++;
            }
        output.WriteLine($"{runs} runs (lateral -1.15..1.15 m, 9 tick phases): {connects} connects, closest {worstClosest:F3} m, furthest past {worstPast:F3} m; " +
                         $"protocol 56 rules: {oldMisses} passed without a contact, {oldPast} ended with the charger past the target");
        Assert.True(oldMisses > 0, "the control: the old cone from the tick's start missed some grazes (else the test proves nothing)");
    }

    [Fact]
    public void A_charge_that_meets_an_opponent_stops_where_it_met_it_not_a_tick_beyond()
    {
        // Straight on, every tick phase: the charger stops within the contact reach and the stunned victim is pulled to the lock distance (never pushed through the charger).
        var reach = 2f * Config.CapsuleRadius + Fast.ContactMargin;
        float worstGap = 0f, oldWorst = 0f;
        for (float phase = 0f; phase < 1.17f; phase += 0.065f)
        {
            var w = Arena(Config, (Marine, Vector3.Zero), (Chaos, new Vector3(0f, 6f + phase, 0f)));
            var target0 = w.Players[2].Position;
            Vector3 stop = default; bool met = false;
            for (int k = 0; k < 30 && !met; k++)
            {
                int tick = k;
                w.Step(id => (id == 1 ? new PlayerInput(0, 0f, tick <= 2 ? 1f : 0f, 0f, tick == 2 ? InputButtons.Sprint | InputButtons.MeleeHeavy : InputButtons.None)
                    : new PlayerInput(0, 0f, 0f, MathF.PI, InputButtons.None)) with { ViewTick = w.Tick, BodyTick = w.Tick });
                if (w.LastRushes.Count > 0) { met = true; stop = w.Players[1].Position; }
            }
            Assert.True(met);
            float gap = target0.Y - stop.Y;
            worstGap = MathF.Max(worstGap, MathF.Abs(gap - reach));
            Assert.True(gap >= 2f * Config.CapsuleRadius - Tol && gap <= reach + Tol, $"phase {phase:F2}: the charger stopped {gap:F3} m short of where the target stood (reach {reach:F2} m)");
            Assert.Equal(RushPhase.Collision, w.Players[1].Rush);
            Assert.Equal(ReactionKind.Stun, w.Players[2].Reaction);
            Assert.Equal(ChargeLock.LockDistance(Config), Flat(w.Players[1].Position, w.Players[2].Position), 2);
            var o = Arena(Config with { SolidBodies = false }, (Marine, Vector3.Zero), (Chaos, new Vector3(0f, 6f + phase, 0f)));
            for (int k = 0; k < 30; k++)
            {
                int tick = k;
                o.Step(id => (id == 1 ? new PlayerInput(0, 0f, tick <= 2 ? 1f : 0f, 0f, tick == 2 ? InputButtons.Sprint | InputButtons.MeleeHeavy : InputButtons.None)
                    : new PlayerInput(0, 0f, 0f, MathF.PI, InputButtons.None)) with { ViewTick = o.Tick, BodyTick = o.Tick });
                if (o.LastRushes.Count > 0) { oldWorst = MathF.Max(oldWorst, MathF.Abs(target0.Y - o.Players[1].Position.Y - reach)); break; }
            }
        }
        output.WriteLine($"stop point off the contact reach by at most {worstGap:F3} m (protocol 56 rules: {oldWorst:F3} m)");
    }

    [Theory]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(15)]
    public void A_target_stepping_into_the_charge_after_the_chargers_picture_was_taken_is_met_never_passed_through(int lag)
    {
        // The target stands 1.5 m off the charge's line (out of its reach) and steps into the line as the charge comes: the charger's picture (lag ticks old) still has it standing
        // aside, so only the bodies as they really are can stop the charge (the protocol 56 rules ran through it).
        int runs = 0, connects = 0, oldThrough = 0, oldMissed = 0;
        float worst = float.MaxValue;
        foreach (float x in new[] { -1.5f, 1.5f })
            for (float y = 6f; y < 8.4f; y += 0.3f)
                for (int start = 0; start <= 4; start += 2)
                {
                    PlayerInput StepIn(int tick, PlayerState me) =>
                        new(0, tick >= start && MathF.Abs(me.Position.X) > 0.05f ? (me.Position.X > 0f ? 1f : -1f) : 0f, 0f, MathF.PI, InputButtons.None);   // facing -Y, a right strafe (+) goes to -X
                    var at = new Vector3(x, y, 0f);
                    var o = Charge(Config, at, lag: lag, target: StepIn);
                    runs++;
                    if (o.Contact > 0) connects++;
                    worst = MathF.Min(worst, o.Closest);
                    Assert.True(o.Closest >= SolidBodies.GuardDistance(Config, charging: true) - Tol, $"({x:F1}, {y:F1}, from tick {start}): the charger came {o.Closest:F3} m from the target's axis");
                    Assert.False(o.Crossed, $"({x:F1}, {y:F1}, from tick {start}): the charger ran through the target");
                    Assert.True(o.After >= SolidBodies.GuardDistance(Config, charging: false) - Tol, $"({x:F1}, {y:F1}, from tick {start}): after the contact {o.After:F3} m apart");
                    var old = Charge(Config with { SolidBodies = false }, at, lag: lag, target: StepIn);
                    if (old.Crossed || old.Closest < 2f * Config.CapsuleRadius - 0.3f) oldThrough++;
                    if (old.Contact == 0) oldMissed++;
                }
        output.WriteLine($"lag {lag}: {runs} runs, {connects} contacts, closest {worst:F3} m; protocol 56 rules: {oldThrough} runs through the body or more than 0.3 m into it, {oldMissed} without a contact");
        Assert.True(oldThrough > 0, "the control: the protocol 56 rules let some charges run into the body (else the test proves nothing)");
    }

    // ---------------------------------------------------------------- the guard

    [Fact]
    public void The_guard_cuts_a_move_through_a_body_but_not_a_move_past_or_away()
    {
        var c = Config;
        float limit = SolidBodies.GuardDistance(c, charging: false);
        // Straight through a standing body: cut at the limit.
        var a1 = new Vector3(0f, 3f, 0f); var b1 = new Vector3(0f, 1.5f, 0f);
        Assert.True(SolidBodies.Guard(Vector3.Zero, ref a1, new Vector3(0f, 1.5f, 0f), ref b1, limit, c, out var n));
        Assert.Equal(1.5f - limit, a1.Y, 3);
        Assert.Equal(1.5f, b1.Y, 5);   // it was not moving: not moved
        Assert.True(n.Y < -0.99f);
        // Past it 0.6 m aside (the capsules overlap 0.3 m: left to the push apart, no cut at the walker's limit).
        var p1 = new Vector3(0f, 3f, 0f); var q1 = new Vector3(0.6f, 1.5f, 0f);
        Assert.False(SolidBodies.Guard(Vector3.Zero, ref p1, new Vector3(0.6f, 1.5f, 0f), ref q1, limit, c, out _));
        // ...but with a charger the limit is the capsules' edge less 0.15 m: cut.
        Assert.True(SolidBodies.Guard(Vector3.Zero, ref p1, new Vector3(0.6f, 1.5f, 0f), ref q1, SolidBodies.GuardDistance(c, charging: true), c, out _));
        // Two moving through each other head on: both cut at the same moment.
        var x1 = new Vector3(0f, 2f, 0f); var y1 = new Vector3(0f, 0f, 0f);
        Assert.True(SolidBodies.Guard(Vector3.Zero, ref x1, new Vector3(0f, 2f, 0f), ref y1, limit, c, out _));
        Assert.Equal(limit, y1.Y - x1.Y, 3);
        Assert.Equal(1f - limit / 2f, x1.Y, 3);
        // Moving away from an overlap: never held.
        var u1 = new Vector3(0f, -1f, 0f); var v1 = new Vector3(0f, 0.2f, 0f);
        Assert.False(SolidBodies.Guard(Vector3.Zero, ref u1, new Vector3(0f, 0.2f, 0f), ref v1, limit, c, out _));
        // Already deeper than the limit and moving closer: held where it began.
        var s1 = new Vector3(0f, 0.2f, 0f); var t1 = new Vector3(0f, 0.3f, 0f);
        Assert.True(SolidBodies.Guard(Vector3.Zero, ref s1, new Vector3(0f, 0.3f, 0f), ref t1, limit, c, out _));
        Assert.Equal(0f, s1.Y, 5);
        // One on the other's head: not the guard's.
        var h1 = new Vector3(0f, 1f, 0f); var k1 = new Vector3(0f, 1f, 2f);
        Assert.False(SolidBodies.Guard(Vector3.Zero, ref h1, new Vector3(0f, 1f, 2f), ref k1, limit, c, out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(20)]
    public void Two_walkers_never_pass_through_each_other_even_with_a_wrong_picture(int lag)
    {
        // Head on and crossing at a slant, sprinting; the second one juking sideways so a lagging picture has it elsewhere.
        foreach (float slant in new[] { 0f, 0.3f, 0.8f })
            foreach (bool on in new[] { true, false })
            {
                var c = Config with { SolidBodies = on };
                var w = Arena(c, (Marine, Vector3.Zero), (Chaos, new Vector3(slant, 8f, 0f)));
                float closest = float.MaxValue; bool crossed = false;
                for (int k = 0; k < 60; k++)
                {
                    int tick = k;
                    uint body = w.Tick > (uint)lag + 1 ? w.Tick - (uint)lag - 1 : (w.Tick > 1 ? 1u : 0u);
                    w.Step(id => (id == 1 ? new PlayerInput(0, 0f, 1f, 0f, InputButtons.Sprint)
                        : new PlayerInput(0, (tick / 6) % 2 == 0 ? 1f : -1f, 1f, MathF.PI, InputButtons.Sprint)) with { BodyTick = body, ViewTick = w.Tick });
                    var a = w.Players[1].Position; var b = w.Players[2].Position;
                    closest = MathF.Min(closest, Flat(a, b));
                    if (a.Y > b.Y + 0.2f && MathF.Abs(a.X - b.X) < 0.45f) crossed = true;   // the walker's axis went through the other's
                }
                output.WriteLine($"lag {lag}, slant {slant}: solid bodies {(on ? "on " : "off")}: closest {closest:F3} m{(crossed ? ", passed through" : "")}");
                if (!on) continue;
                Assert.True(closest >= SolidBodies.GuardDistance(c, charging: false) - Tol, $"lag {lag}, slant {slant}: {closest:F3} m apart");
                Assert.False(crossed, $"lag {lag}, slant {slant}: passed through");
                if (lag == 0) Assert.True(closest >= 2f * c.CapsuleRadius - 0.1f, $"with the newest picture the capsules barely touch ({closest:F3} m; the push apart's tick of overlap)");
            }
    }

    [Fact]
    public void Allies_keep_the_protocol_56_bodies_and_contact()
    {
        // Two marines (allies, friendly fire off): no guard, no swept contact, no stop at the contact point (so the co-op of the added content is unchanged).
        var on = Arena(Config, (Marine, Vector3.Zero), (Marine, new Vector3(1.05f, 6.5f, 0f)));
        var off = Arena(Config with { SolidBodies = false }, (Marine, Vector3.Zero), (Marine, new Vector3(1.05f, 6.5f, 0f)));
        for (int k = 0; k < 30; k++)
        {
            int tick = k;
            PlayerInput In(int id, World w) => (id == 1 ? new PlayerInput(0, 0f, tick <= 2 ? 1f : 0f, 0f, tick == 2 ? InputButtons.Sprint | InputButtons.MeleeHeavy : InputButtons.None)
                : new PlayerInput(0, 0f, 0f, MathF.PI, InputButtons.None)) with { ViewTick = w.Tick, BodyTick = w.Tick };
            on.Step(id => In(id, on)); off.Step(id => In(id, off));
            Assert.Equal(off.Players[1].Position, on.Players[1].Position);
            Assert.Equal(off.Players[2].Position, on.Players[2].Position);
            Assert.Equal(off.LastRushes.Count, on.LastRushes.Count);
        }
    }

    [Fact]
    public void The_config_hash_changes_only_when_solid_bodies_are_off()
    {
        Assert.True(new SimConfig().SolidBodies);
        Assert.Equal(new SimConfig().Hash(), (new SimConfig() with { SolidBodies = true }).Hash());
        Assert.NotEqual(new SimConfig().Hash(), (new SimConfig() with { SolidBodies = false }).Hash());
        Assert.Equal(76, Protocol.Version);
    }

    // ---------------------------------------------------------------- clash, stun and recoil states

    [Fact]
    public void Every_connect_leaves_the_charger_in_its_recoil_and_the_victim_stunned_at_the_end_of_the_tick()
    {
        // Many approaches (offsets, phases, a strafing target, lag): whenever the world reports a harmful connect on a target that may react, the tick's end state has the charger in the
        // collision stop (its first tick) and the victim in the stun; nothing later in the tick overrides either.
        int connects = 0;
        foreach (int lag in new[] { 0, 8 })
            for (float x = -1.1f; x <= 1.11f; x += 0.22f)
                for (float phase = 0f; phase < 1.17f; phase += 0.39f)
                {
                    var w = Arena(Config, (Marine, Vector3.Zero), (Chaos, new Vector3(x, 6f + phase, 0f)));
                    for (int k = 0; k < 30; k++)
                    {
                        int tick = k;
                        var before = w.Players[2];
                        uint body = w.Tick > (uint)lag + 1 ? w.Tick - (uint)lag - 1 : (w.Tick > 1 ? 1u : 0u);
                        w.Step(id => (id == 1 ? new PlayerInput(0, 0f, tick <= 2 ? 1f : 0f, 0f, tick == 2 ? InputButtons.Sprint | InputButtons.MeleeHeavy : InputButtons.None)
                            : new PlayerInput(0, tick % 10 < 5 ? 1f : -1f, 0f, MathF.PI, InputButtons.None)) with { ViewTick = (uint)Math.Max(1, (long)w.Tick - lag), BodyTick = body });
                        foreach (var e in w.LastRushes)
                        {
                            connects++;
                            Assert.True(e.Harmed);
                            Assert.Equal((RushPhase.Collision, 0), (w.Players[e.Player].Rush, w.Players[e.Player].RushTicks));
                            if (Reaction.CanReact(before, Config)) Assert.Equal(ReactionKind.Stun, w.Players[e.Target].Reaction);
                        }
                    }
                }
        output.WriteLine($"{connects} connects checked (66 runs; the rest missed a strafing target)");
        Assert.True(connects >= 40, $"{connects} connects");
    }

    [Theory]
    [InlineData(91)]
    [InlineData(92)]
    public void Every_viewer_sees_the_recoil_and_the_stun_of_every_connect_under_150ms_rtt_and_5pct_loss(int seed)
    {
        // A marine charges a chaos marine over and over on a lossy link (the chaos marine strafes); for every connect the server resolves, the charger's own client shows its recoil
        // (the predicted collision stop), the victim's own client shows its stun, and each one's remote view of the other shows the other's (the clips play from these states).
        var link = new LinkConditions(TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(10), 0.05);
        var h = new Harness(link, seed, 0, Config, new ServerOptions { PeerTimeout = TimeSpan.FromMinutes(5), Match = new MatchRules { TimeLimit = 0f, CountdownTime = 0f } });
        h.AddClient(nonce: 100, options: new ClientOptions { RequestedClass = Marine, RequestedSide = Team.SpaceMarines, SmoothInputLead = false });   // net/responsive: the old lead (about 5 ticks): the script's connect count was set with its timing through 5 % loss
        h.AddClient(nonce: 101, options: new ClientOptions { RequestedClass = Chaos, RequestedSide = Team.Chaos, SmoothInputLead = false });
        var connects = new List<uint>();
        var ownRecoil = new HashSet<uint>(); var ownStun = new HashSet<uint>(); var seenRecoil = new HashSet<uint>(); var seenStun = new HashSet<uint>();
        uint lastServer = 0;
        PlayerInput Input(int i, uint t)
        {
            var me = h.Clients[i].Predicted;
            var others = h.Clients[i].RemotePlayers().Players;
            var other = others.FirstOrDefault(p => p.PlayerId != h.Clients[i].PlayerId);
            if (other.PlayerId == 0) return new PlayerInput(t, 0f, 0f, 0f, InputButtons.None);
            var to = new Vector2(other.Position.X - me.Position.X, other.Position.Y - me.Position.Y);
            float yaw = MathF.Atan2(-to.X, to.Y);
            if (i == 1) return new PlayerInput(t, (t / 20) % 2 == 0 ? 0.6f : -0.6f, 0f, yaw, InputButtons.None);
            float distance = to.Length();
            uint phase = t % 90;
            bool press = phase == 0 && distance is > 2.5f and < 9f;
            if (me.Rush != RushPhase.None) return new PlayerInput(t, 0f, 1f, yaw, InputButtons.Sprint);   // charge-responsive (protocol 69): the back-off below would be an S cancel of the charge itself; the script holds W through it
            if (distance > 9f) return new PlayerInput(t, 0f, 1f, yaw, InputButtons.Sprint);
            if (distance < 2.5f) return new PlayerInput(t, 0f, -1f, yaw, InputButtons.None);   // back off for the next run-up
            return new PlayerInput(t, 0f, 1f, yaw, InputButtons.Sprint | (press ? InputButtons.MeleeHeavy : InputButtons.None));
        }
        h.Run(TimeSpan.FromSeconds(60), Input, () =>
        {
            var w = h.Server.World;
            if (w.Tick != lastServer)
            {
                lastServer = w.Tick;
                foreach (var e in w.LastRushes) if (e.Harmed && w.Players[e.Target].Reaction == ReactionKind.Stun) connects.Add(w.Tick);
            }
            foreach (var since in connects.Where(c => c + 45 > w.Tick))
            {
                var charger = h.Clients[0]; var victim = h.Clients[1];
                if (charger.Predicted.Rush == RushPhase.Collision) ownRecoil.Add(since);
                if (victim.Predicted.Reaction == ReactionKind.Stun) ownStun.Add(since);
                if (victim.RemotePlayers().Players.Any(p => p.PlayerId == charger.PlayerId && p.Rush == RushPhase.Collision)) seenRecoil.Add(since);
                if (charger.RemotePlayers().Players.Any(p => p.PlayerId == victim.PlayerId && p.Reaction == ReactionKind.Stun)) seenStun.Add(since);
            }
        });
        var checkable = connects.Where(c => c + 45 <= h.Server.World.Tick).ToList();
        output.WriteLine($"seed {seed}: {checkable.Count} connects; own recoil shown {checkable.Count(ownRecoil.Contains)}, own stun {checkable.Count(ownStun.Contains)}, " +
                         $"recoil seen by the victim {checkable.Count(seenRecoil.Contains)}, stun seen by the charger {checkable.Count(seenStun.Contains)}");
        Assert.True(checkable.Count >= 5, $"only {checkable.Count} connects");
        foreach (var c in checkable)
        {
            Assert.Contains(c, ownRecoil);
            Assert.Contains(c, ownStun);
            Assert.Contains(c, seenRecoil);
            Assert.Contains(c, seenStun);
        }
    }
}
