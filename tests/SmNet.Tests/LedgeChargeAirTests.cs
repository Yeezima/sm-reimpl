using System.Numerics;
using SmFormats.Havok;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// sim/ledge-charge-air (protocol 90; the owner 2026-11-23, brief <c>2026-11-23-series-36-ledge-charge</c>): in Classic PvP a bull rush that runs off a ledge travels half as far from the ledge to its first landing.
/// Synthetic level (a floor at z = 0 and a plateau whose edge is the line y = 0) and synthetic specs (<see cref="JumpPackAttackTests.Config"/>'s marine with RushTests' 10 m/s, 1.2 s rush; the Assault of
/// <see cref="MeleeCancelTests.Pack"/> for the hop). Before = <see cref="SimConfig.LedgeChargeHalfAir"/> false (the restore switch, protocol 89's rule), after = the default. Same start, heading, inputs and loadout; no
/// steering and no Space after the departure in the distance runs.
/// </summary>
public class LedgeChargeAirTests(ITestOutputHelper output)
{
    const int Marine = 1, Assault = 0;
    static readonly SimConfig After = JumpPackAttackTests.Config;
    static readonly SimConfig Before = After with { LedgeChargeHalfAir = false };

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

    /// <summary>A floor at z = 0 and a straight plateau edge along y = 0 (the plateau at y &lt; 0), <paramref name="height"/> m high.</summary>
    static CollisionWorld Ledge(float height) => new Level().Box(new(-100, -100, -1), new(100, 200, 0)).Box(new(-100, -100, 0), new(100, 0, height)).World();

    static CollisionWorld Flat() => new Level().Box(new(-100, -100, -1), new(100, 200, 0)).World();

    static PlayerState Fresh(SimConfig c, int cls, Vector3 at) => Combat.Respawned(new PlayerState(1, at, Vector3.Zero, 0f, true) { Class = cls }, c);

    static PlayerState Step(PlayerState s, SimConfig c, InputButtons b, float my, CollisionWorld world) =>
        Combat.StepPlayer(s, new PlayerInput(0, 0f, my, 0f, b), c, out _, world);

    static float Flat2(Vector3 a, Vector3 b) => new Vector2(b.X - a.X, b.Y - a.Y).Length();

    /// <summary>One run: sprint +Y, the charge (Sprint + F), then no input; the states after each tick from the charge's start until the first landing after leaving the ground (or <paramref name="ticks"/>).</summary>
    /// <param name="airButtons">Buttons on the given air tick (0 = the first tick that starts in the air); none by default.</param>
    static List<PlayerState> Run(SimConfig c, int cls, CollisionWorld world, Vector3 start, int ticks = 150, Func<int, InputButtons>? airButtons = null, bool stopAtLanding = true)
    {
        var s = Fresh(c, cls, start);
        for (int k = 0; k < 6; k++) s = Step(s, c, InputButtons.Sprint, 1f, world);
        s = Step(s, c, InputButtons.Sprint | InputButtons.MeleeHeavy, 1f, world);
        Assert.Equal(RushPhase.Rush, s.Rush);
        var trace = new List<PlayerState> { s };
        bool left = false;
        int air = 0;
        for (int k = 0; k < ticks; k++)
        {
            bool inAir = !s.Grounded;
            var b = inAir && airButtons is not null ? airButtons(air) : InputButtons.None;
            if (inAir) air++;
            s = Step(s, c, b, 0f, world);
            trace.Add(s);
            if (!s.Grounded) left = true;
            else if (left && stopAtLanding) break;
        }
        return trace;
    }

    /// <summary>The departure (the first state in the air: the end of the tick that left the ground), the last state on the plateau and the first landing.</summary>
    static (PlayerState LastGround, PlayerState Departure, PlayerState Landing, int AirTicks) Split(List<PlayerState> t)
    {
        int d = t.FindIndex(p => !p.Grounded);
        Assert.True(d > 0, "the charge left the ground");
        int l = t.FindIndex(d, p => p.Grounded);
        Assert.True(l > d, "and landed");
        return (t[d - 1], t[d], t[l], l - d);
    }

    // ---------------------------------------------------------------- the distance target

    [Theory]
    [InlineData(8f)]
    [InlineData(4f)]
    [InlineData(1.5f)]
    public void A_classic_ledge_charge_travels_half_as_far_from_departure_to_its_first_landing(float height)
    {
        var world = Ledge(height);
        var start = new Vector3(0f, -9f, height);
        var before = Split(Run(Before, Marine, world, start));
        var after = Split(Run(After, Marine, world, start));
        // Matched up to the departure: the same run-up, the same tick of departure, the same vertical speed and drop.
        Assert.Equal(before.LastGround.Position, after.LastGround.Position);
        Assert.Equal(before.Departure.Position, after.Departure.Position);
        Assert.Equal(before.Departure.Velocity.Z, after.Departure.Velocity.Z);
        Assert.Equal(before.AirTicks, after.AirTicks);
        Assert.Equal(before.Landing.Position.Z, after.Landing.Position.Z, 3);

        // (1) The simulation's departure (the end of the tick that left the ground) to the first landing: exactly half (the held speed is halved, the time in the air is the same).
        float b1 = Flat2(before.Departure.Position, before.Landing.Position), a1 = Flat2(after.Departure.Position, after.Landing.Position);
        // (2) Bracketing the lift-off: the body leaves the ground somewhere inside the departure tick (the capsule rides the edge until its foot is past it, so the edge line itself is not the lift-off).
        // From the start of that tick (the last state on the plateau) to the landing the ratio is 0.5 + p / (2 B), p the departure tick's part before the ground probe let go (0 <= p <= one tick's
        // travel): the tolerance is half of one 30 Hz tick's travel over the before distance.
        float b2 = Flat2(before.LastGround.Position, before.Landing.Position), a2 = Flat2(after.LastGround.Position, after.Landing.Position);
        float tickTravel = Flat2(before.LastGround.Position, before.Departure.Position);
        float tolerance2 = 0.5f * tickTravel / b2;
        output.WriteLine($"{height} m ledge: {before.AirTicks} ticks in the air; departure -> landing before {b1:F3} m, after {a1:F3} m, ratio {a1 / b1:F4}; "
            + $"departure tick start -> landing before {b2:F3} m, after {a2:F3} m, ratio {a2 / b2:F4} (bound 0.5 .. {0.5f + tolerance2:F4}; one tick {tickTravel:F3} m); edge y = 0 to landing before {before.Landing.Position.Y:F3} m, after {after.Landing.Position.Y:F3} m");
        Assert.InRange(a1 / b1, 0.5f - 1e-3f, 0.5f + 1e-3f);
        Assert.InRange(a2 / b2, 0.5f - 1e-3f, 0.5f + tolerance2 + 1e-3f);
        // The vertical part and the charge's end on the landing are unchanged (a real fall ends it).
        Assert.Equal(before.Landing.Rush, after.Landing.Rush);
    }

    [Fact]
    public void The_held_momentum_is_the_full_departure_speed_only_the_travel_is_halved()
    {
        var world = Ledge(8f);
        var after = Run(After, Marine, world, new Vector3(0f, -9f, 8f));
        var before = Run(Before, Marine, world, new Vector3(0f, -9f, 8f));
        var (_, departure, _, _) = Split(after);
        float held = new Vector2(departure.Velocity.X, departure.Velocity.Y).Length();
        Assert.Equal(10f, held, 2);   // RushTests' 10 m/s clip: the velocity in the state is the full one (owner 2026-11-23: "only the charge should be slowed down")
        Assert.Equal(held, new Vector2(Split(before).Departure.Velocity.X, Split(before).Departure.Velocity.Y).Length(), 4);
        var air = after.Where(p => !p.Grounded).ToList();
        foreach (var p in air) Assert.Equal(held, new Vector2(p.Velocity.X, p.Velocity.Y).Length(), 3);
        // ... while each held tick moves half of it (5 m/s x 1/30 s)
        for (int k = 1; k < air.Count; k++) Assert.Equal(0.5f * held / 30f, Flat2(air[k - 1].Position, air[k].Position), 4);
        // The landing's speed (capped at sprint speed for the slowdown) is the same as before.
        Assert.Equal(Split(before).Landing.Velocity, Split(after).Landing.Velocity);
    }

    // ---------------------------------------------------------------- unchanged: the ground, the other modes

    [Fact]
    public void A_charge_that_stays_on_the_ground_is_unchanged()
    {
        var world = Flat();
        var before = Run(Before, Marine, world, new Vector3(0f, -9f, 0f), ticks: 60, stopAtLanding: false);
        var after = Run(After, Marine, world, new Vector3(0f, -9f, 0f), ticks: 60, stopAtLanding: false);
        Assert.All(after, p => Assert.True(p.Grounded));
        Assert.Equal(before.Count, after.Count);
        for (int k = 0; k < before.Count; k++) Assert.Equal(before[k], after[k]);
        output.WriteLine($"ground charge: {Flat2(before[0].Position, before[^1].Position):F3} m both ways");
    }

    [Theory]
    [InlineData(ContentExtras.Musou | ContentExtras.Warfront)]
    [InlineData(ContentExtras.Musou)]
    public void Warfront_and_Musou_ledge_charges_are_unchanged(ContentExtras extras)
    {
        var on = After with { Extras = extras };
        var off = on with { LedgeChargeHalfAir = false };
        Assert.False(LedgeChargeAir.Active(on));
        var world = Ledge(8f);
        var a = Run(on, Marine, world, new Vector3(0f, -9f, 8f));
        var b = Run(off, Marine, world, new Vector3(0f, -9f, 8f));
        Assert.Equal(b.Count, a.Count);
        for (int k = 0; k < a.Count; k++) Assert.Equal(b[k].Position, a[k].Position);
        var (_, dep, land, _) = Split(a);
        output.WriteLine($"{extras}: departure -> landing {Flat2(dep.Position, land.Position):F3} m (full speed {new Vector2(dep.Velocity.X, dep.Velocity.Y).Length():F2} m/s)");
        Assert.Equal(10f, new Vector2(dep.Velocity.X, dep.Velocity.Y).Length(), 2);
    }

    [Fact]
    public void Classic_profiles_with_added_content_use_the_rule_and_the_switch_is_hashed_only_when_off()
    {
        Assert.True(LedgeChargeAir.Active(After));
        Assert.True(LedgeChargeAir.Active(After with { Extras = ContentExtras.Orks | ContentExtras.Autocannon | ContentExtras.LoyalistBarks }));
        Assert.False(LedgeChargeAir.Active(Before));
        Assert.NotEqual(After.Hash(), Before.Hash());
        Assert.Equal(After.Hash(), (After with { LedgeChargeHalfAir = true }).Hash());
    }

    // ---------------------------------------------------------------- the Space-to-hop and light-melee follow-ons (regressions: as protocol 89)

    /// <summary>
    /// A ledge charge off a 12 m ledge with <paramref name="buttons"/> pressed on its <paramref name="press"/>th air tick (stick forward, as LedgeChargeHopTests); from the state before the press: the
    /// horizontal displacement after each tick until the first landing and the horizontal velocity of each tick.
    /// </summary>
    static (List<Vector2> From, List<Vector3> Velocity, bool Hopped, float Distance) After_(SimConfig c, int cls, InputButtons buttons, int press = 4, float rushScale = 1f)
    {
        if (rushScale != 1f)
        {
            var spec = RushTests.Spec with { Rush = RushTests.Spec.Rush with { Path = [.. RushTests.Spec.Rush.Path.Select(v => v * rushScale)] } };
            c = c with { Classes = [.. c.Classes.Select(k => k.MeleeRush is null ? k : k with { MeleeRush = spec })] };
        }
        var world = Ledge(12f);
        var s = Fresh(c, cls, new Vector3(0f, -9f, 12f));
        for (int k = 0; k < 6; k++) s = Step(s, c, InputButtons.Sprint, 1f, world);
        s = Step(s, c, InputButtons.Sprint | InputButtons.MeleeHeavy, 1f, world);
        Assert.Equal(RushPhase.Rush, s.Rush);
        int air = -1;
        for (int k = 0; k < 60 && air < press; k++) { s = Step(s, c, InputButtons.None, 0f, world); if (!s.Grounded) air++; }
        Assert.Equal(RushPhase.Rush, s.Rush);
        Assert.False(s.Grounded);
        var at = s;
        var from = new List<Vector2>(); var vel = new List<Vector3>();
        bool hopped = false;
        s = Step(s, c, buttons, 1f, world);
        for (int k = 0; k < 200; k++)
        {
            from.Add(new Vector2(s.Position.X - at.Position.X, s.Position.Y - at.Position.Y)); vel.Add(s.Velocity);
            hopped |= Evade.Evading(s);
            if (s.Grounded) break;
            s = Step(s, c, InputButtons.None, 1f, world);
        }
        Assert.True(s.Grounded, "landed");
        return (from, vel, hopped, from[^1].Length());
    }

    static void SameAsProtocol89(ITestOutputHelper output, string name, (List<Vector2> From, List<Vector3> Velocity, bool Hopped, float Distance) before, (List<Vector2> From, List<Vector3> Velocity, bool Hopped, float Distance) after)
    {
        output.WriteLine($"{name}: press -> landing before {before.Distance:F3} m in {before.From.Count} ticks, after {after.Distance:F3} m in {after.From.Count} ticks; speeds after "
            + string.Join(" ", after.Velocity.Take(12).Select(v => new Vector2(v.X, v.Y).Length().ToString("F1"))));
        Assert.Equal(before.Hopped, after.Hopped);
        Assert.Equal(before.From.Count, after.From.Count);
        for (int k = 0; k < before.From.Count; k++)
        {
            // within 1 mm and 1 mm/s (float rounding: the press happens at another place on the same flat floor)
            Assert.Equal(before.From[k].X, after.From[k].X, 3); Assert.Equal(before.From[k].Y, after.From[k].Y, 3);
            Assert.Equal(before.Velocity[k].X, after.Velocity[k].X, 3); Assert.Equal(before.Velocity[k].Y, after.Velocity[k].Y, 3); Assert.Equal(before.Velocity[k].Z, after.Velocity[k].Z, 3);
        }
    }

    [Theory]
    [InlineData(1f)]   // 10 m/s: slower than the hop (16.4 m/s), the hop's own speed
    [InlineData(2f)]   // 20 m/s (about the install's 20.6 m/s ledge charge): the ledge hop's carried excess easing down
    public void The_hop_out_of_a_ledge_charge_keeps_the_protocol_89_distance_and_momentum(float rushScale)
    {
        var pack = MeleeCancelTests.Pack;
        var before = After_(pack with { LedgeChargeHalfAir = false }, Assault, InputButtons.Jump, rushScale: rushScale);
        var after = After_(pack, Assault, InputButtons.Jump, rushScale: rushScale);
        Assert.True(after.Hopped, "the Space tap hops");
        SameAsProtocol89(output, $"hop ({10f * rushScale} m/s charge)", before, after);
    }

    [Theory]
    [InlineData(Assault)]
    [InlineData(Marine)]
    public void A_light_melee_out_of_a_ledge_charge_is_unchanged(int cls)
    {
        var c = cls == Assault ? MeleeCancelTests.Pack : After;
        var before = After_(c with { LedgeChargeHalfAir = false }, cls, InputButtons.Melee);
        var after = After_(c, cls, InputButtons.Melee);
        SameAsProtocol89(output, $"light melee (class {cls})", before, after);
    }

    [Fact]
    public void A_space_press_by_a_class_without_a_jump_pack_does_not_leave_the_charge_so_its_travel_stays_halved()
    {
        // The Tactical's Space in a held charge is no hop: the charge goes on in the air (as at protocol 89), so its travel is the halved charge's, its momentum the full one.
        var before = After_(After with { LedgeChargeHalfAir = false }, Marine, InputButtons.Jump);
        var after = After_(After, Marine, InputButtons.Jump);
        output.WriteLine($"Space (marine): press -> landing before {before.Distance:F3} m, after {after.Distance:F3} m in {after.From.Count} ticks");
        Assert.False(after.Hopped);
        Assert.Equal(before.From.Count, after.From.Count);
        Assert.InRange(after.Distance / before.Distance, 0.5f - 1e-3f, 0.5f + 1e-3f);
        for (int k = 0; k < before.Velocity.Count; k++) Assert.Equal(before.Velocity[k], after.Velocity[k]);
    }
}
