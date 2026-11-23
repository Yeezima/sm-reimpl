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
    [InlineData(12f)]
    [InlineData(8f)]
    [InlineData(4f)]
    [InlineData(2.5f)]
    [InlineData(1.5f)]
    [InlineData(0.8f)]
    public void A_classic_ledge_charge_travels_half_as_far_from_departure_to_its_first_landing(float height)
    {
        var world = Ledge(height);
        var start = new Vector3(0f, -9f, height);
        var before = Split(Run(Before, Marine, world, start));
        var after = Split(Run(After, Marine, world, start));
        // Matched up to the departure: the same run-up, the same tick of departure, the same vertical speed.
        Assert.Equal(before.LastGround.Position, after.LastGround.Position);
        Assert.Equal(before.Departure.Position, after.Departure.Position);
        Assert.Equal(before.Departure.Velocity, after.Departure.Velocity);
        Assert.Equal(before.Landing.Position.Z, after.Landing.Position.Z, 3);
        // The simulation's departure (the end of the tick that left the ground) to the first landing. The held speed is the same, so the distance is (air ticks) x one tick's travel, and
        // 4 x gravity halves the air time: half the distance to within one 30 Hz tick's travel (the landing tick is whole; the departure tick's own vertical speed, from normal gravity, is the same in both).
        float b1 = Flat2(before.Departure.Position, before.Landing.Position), a1 = Flat2(after.Departure.Position, after.Landing.Position);
        float tick = new Vector2(before.Departure.Velocity.X, before.Departure.Velocity.Y).Length() / 30f;
        output.WriteLine($"{height} m ledge: air ticks before {before.AirTicks}, after {after.AirTicks}; departure -> landing before {b1:F3} m, after {a1:F3} m, ratio {a1 / b1:F4} "
            + $"(tolerance +/- one tick {tick:F3} m = +/- {tick / b1:F4}); landing-tick vertical speed before {before.Landing.Velocity.Z:F2}, after {after.Landing.Velocity.Z:F2} m/s");
        Assert.InRange(a1, 0.5f * b1 - tick - 1e-3f, 0.5f * b1 + tick + 1e-3f);
        Assert.InRange(after.AirTicks, before.AirTicks / 2 - 1, (before.AirTicks + 1) / 2 + 1);
        // A real fall still ends the charge on the landing (the landing rule keeps its drop height).
        Assert.Equal(before.Landing.Rush, after.Landing.Rush);
    }

    [Fact]
    public void The_horizontal_speed_of_every_air_tick_is_unchanged_only_the_fall_is_faster()
    {
        var world = Ledge(8f);
        var after = Run(After, Marine, world, new Vector3(0f, -9f, 8f));
        var before = Run(Before, Marine, world, new Vector3(0f, -9f, 8f));
        var a = Split(after); var b = Split(before);
        var airA = after.Where(p => !p.Grounded).ToList(); var airB = before.Where(p => !p.Grounded).ToList();
        Assert.True(airA.Count < airB.Count);
        // tick by tick (as far as the shorter fall goes): the same horizontal velocity and the same horizontal step; the drop is faster
        for (int k = 0; k < airA.Count; k++)
        {
            Assert.Equal(airB[k].Velocity.X, airA[k].Velocity.X, 4); Assert.Equal(airB[k].Velocity.Y, airA[k].Velocity.Y, 4);
            if (k > 0) Assert.Equal(Flat2(airB[k - 1].Position, airB[k].Position), Flat2(airA[k - 1].Position, airA[k].Position), 4);
            if (k > 0) Assert.True(airA[k].Position.Z < airB[k].Position.Z);
        }
        Assert.Equal(10f, new Vector2(a.Departure.Velocity.X, a.Departure.Velocity.Y).Length(), 2);
        output.WriteLine($"8 m: {b.AirTicks} air ticks before, {a.AirTicks} after; landing vertical speed before {b.Landing.Velocity.Z:F2}, after {a.Landing.Velocity.Z:F2} m/s (the tick before landing: {airB[^1].Velocity.Z:F2} / {airA[^1].Velocity.Z:F2})");
        // the velocity in the state is the body's real travel again (review finding 1 is moot): each air step is the velocity x dt
        for (int k = 1; k < airA.Count; k++) Assert.Equal(new Vector2(airA[k].Velocity.X, airA[k].Velocity.Y).Length() / 30f, Flat2(airA[k - 1].Position, airA[k].Position), 4);
    }

    [Theory]
    [InlineData(0.2f)]
    [InlineData(0.3f)]   // a curb: the clip resumes
    [InlineData(0.4f)]
    [InlineData(0.5f)]
    // 0.6 to 0.8 m: the boundary band, pinned in 0.01 m steps by The_charge_ends_on_landing_from_a_pinned_drop_in_the_0_6_to_0_8_m_band (0.80 m; protocol 89 0.77 m)
    [InlineData(0.9f)]
    [InlineData(1.0f)]
    [InlineData(1.5f)]   // a real drop: the landing ends the charge
    public void The_landing_rule_gives_the_protocol_89_outcome_for_the_same_drop(float height)
    {
        // feel/melee-charge-2's rule (a landing after a fall of at least 0.5 m ends the charge, a shallower one resumes its clip) keeps its drop height under the faster fall (Rush.EndsOnLanding).
        var world = Ledge(height);
        RushPhase? Outcome(SimConfig c)
        {
            var t = Run(c, Marine, world, new Vector3(0f, -2.5f, height), ticks: 60);
            int d = t.FindIndex(p => !p.Grounded);
            if (d < 0) return null;
            int l = t.FindIndex(d, p => p.Grounded);
            if (l < 0) return null;
            output.WriteLine($"{height} m, {(c.LedgeChargeHalfAir ? "after" : "before")}: air vz {string.Join(" ", t.Skip(d).Take(l - d).Select(p => p.Velocity.Z.ToString("F2")))}; landing {t[l].Rush}");
            return t[l].Rush;
        }
        var before = Outcome(Before);
        var after = Outcome(After);
        Assert.Equal(before, after);
    }

    /// <summary>The first drop (0.01 m steps from 0.40 m) whose landing ends the charge, and whether every higher drop up to 1.0 m ends it too (one boundary, no flicker).</summary>
    (float First, bool Monotone) Boundary(SimConfig c)
    {
        float first = float.NaN;
        bool monotone = true;
        for (int i = 40; i <= 100; i++)
        {
            float h = i / 100f;
            var t = Run(c, Marine, Ledge(h), new Vector3(0f, -2.5f, h), ticks: 60);
            int d = t.FindIndex(p => !p.Grounded);
            int l = d < 0 ? -1 : t.FindIndex(d, p => p.Grounded);
            bool ends = l >= 0 && t[l].Rush == RushPhase.None;
            if (ends && float.IsNaN(first)) first = h;
            if (!ends && !float.IsNaN(first)) monotone = false;
        }
        return (first, monotone);
    }

    [Fact]
    public void The_charge_ends_on_landing_from_a_pinned_drop_in_the_0_6_to_0_8_m_band()
    {
        var after = Boundary(After);
        var before = Boundary(Before);
        output.WriteLine($"the landing ends the charge from {after.First:F2} m (protocol 89: from {before.First:F2} m); one boundary: {after.Monotone} / {before.Monotone}");
        Assert.True(after.Monotone);
        Assert.Equal(PinnedBoundary, after.First, 3);
        Assert.InRange(after.First, 0.6f, 0.8f);
    }

    /// <summary>The drop from which a Classic ledge charge's landing ends it (synthetic marine, 30 Hz, the game's 20 m/s^2; measured in 0.01 m steps).</summary>
    const float PinnedBoundary = 0.80f;   // protocol 89 (the restore switch): 0.77 m

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
        Assert.True(new SimConfig().LedgeChargeHalfAir, "on by default");
        // the switch hashes on its own (not folded into another restore switch's entry)
        var hop = After with { LedgeHopCarry = false };
        Assert.NotEqual(hop.Hash(), (hop with { LedgeChargeHalfAir = false }).Hash());
        Assert.NotEqual(Before.Hash(), hop.Hash());
    }

    [Fact]
    public void The_server_switch_parses()
    {
        Assert.True(SmServer.ServerCommandLine.TryParse(["--old-ledge-charge-air"], out var a, out var error), error);
        Assert.True(a.OldLedgeChargeAir);
        Assert.True(SmServer.ServerCommandLine.TryParse([], out var none, out _));
        Assert.False(none.OldLedgeChargeAir);
    }

    [Fact]
    public void Body_lag_carries_a_held_ledge_charger_by_its_real_travel()
    {
        // review finding 1 (Fable High, 2026-11-23), moot with answer 2: PlayerCollision.See moves another body on by its velocity, which is again its real horizontal travel.
        var world = Ledge(8f);
        var air = Run(After, Marine, world, new Vector3(0f, -9f, 8f)).Where(p => !p.Grounded).ToList();
        var charger = air[3] with { PlayerId = 2 };
        float ahead = PlayerCollision.See([charger], 1, 0, 3, After)!.Bodies.Single(b => b.Id == 2).Feet.Y - charger.Position.Y;
        Assert.Equal(air[6].Position.Y - air[3].Position.Y, ahead, 3);
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

    static void SameHorizontal(ITestOutputHelper output, string name, (List<Vector2> From, List<Vector3> Velocity, bool Hopped, float Distance) before, (List<Vector2> From, List<Vector3> Velocity, bool Hopped, float Distance) after, int tickSlack)
    {
        output.WriteLine($"{name}: press -> landing before {before.Distance:F3} m in {before.From.Count} ticks, after {after.Distance:F3} m in {after.From.Count} ticks; horizontal speeds after "
            + string.Join(" ", after.Velocity.Take(12).Select(v => new Vector2(v.X, v.Y).Length().ToString("F1"))));
        Assert.Equal(before.Hopped, after.Hopped);
        Assert.InRange(after.From.Count, before.From.Count - tickSlack, before.From.Count);
        // the horizontal part tick by tick (within 1 mm and 1 mm/s: float rounding, the press happens at another place)
        for (int k = 0; k < after.From.Count - 1; k++)
        {
            Assert.Equal(before.From[k].X, after.From[k].X, 3); Assert.Equal(before.From[k].Y, after.From[k].Y, 3);
            Assert.Equal(before.Velocity[k].X, after.Velocity[k].X, 3); Assert.Equal(before.Velocity[k].Y, after.Velocity[k].Y, 3);
        }
    }

    [Theory]
    [InlineData(1f)]   // 10 m/s: slower than the hop (16.4 m/s), the hop's own speed
    [InlineData(2f)]   // 20 m/s (about the install's 20.6 m/s ledge charge): the ledge hop's carried excess easing down
    public void The_hop_out_of_a_ledge_charge_keeps_the_protocol_89_speed_momentum_and_distance(float rushScale)
    {
        // Owner answer 2 (2026-11-23): "the hop is normal speed, unchanged." The press comes lower (the charge fell faster for 4 ticks), so the hop lands at most one tick sooner; its horizontal
        // speeds tick by tick and its distance from the press are protocol 89's.
        var pack = MeleeCancelTests.Pack;
        var before = After_(pack with { LedgeChargeHalfAir = false }, Assault, InputButtons.Jump, rushScale: rushScale);
        var after = After_(pack, Assault, InputButtons.Jump, rushScale: rushScale);
        Assert.True(after.Hopped, "the Space tap hops");
        SameHorizontal(output, $"hop ({10f * rushScale} m/s charge)", before, after, tickSlack: 1);
        Assert.Equal(before.Distance, after.Distance, 2);
    }

    [Theory]
    [InlineData(Assault)]
    [InlineData(Marine)]
    public void A_light_melee_out_of_a_ledge_charge_is_unchanged_horizontally(int cls)
    {
        // The light press zeroes the momentum as before and the swing is the same; the body was lower and falling faster at the press, so it lands sooner (reported, not asserted equal).
        var c = cls == Assault ? MeleeCancelTests.Pack : After;
        var before = After_(c with { LedgeChargeHalfAir = false }, cls, InputButtons.Melee);
        var after = After_(c, cls, InputButtons.Melee);
        SameHorizontal(output, $"light melee (class {cls})", before, after, tickSlack: 20);
        Assert.Equal(before.Distance, after.Distance, 3);
    }

    [Fact]
    public void A_space_press_by_a_class_without_a_jump_pack_does_not_leave_the_charge_so_its_travel_stays_halved()
    {
        // The Tactical's Space in a held charge is no hop: the charge goes on in the air (as at protocol 89) and falls as the faster charge.
        var before = After_(After with { LedgeChargeHalfAir = false }, Marine, InputButtons.Jump);
        var after = After_(After, Marine, InputButtons.Jump);
        output.WriteLine($"Space (marine): press -> landing before {before.Distance:F3} m in {before.From.Count} ticks, after {after.Distance:F3} m in {after.From.Count} ticks");
        Assert.False(after.Hopped);
        Assert.True(after.Distance < before.Distance);
        for (int k = 0; k < after.Velocity.Count - 1; k++) { Assert.Equal(before.Velocity[k].X, after.Velocity[k].X, 3); Assert.Equal(before.Velocity[k].Y, after.Velocity[k].Y, 3); }
    }
}
