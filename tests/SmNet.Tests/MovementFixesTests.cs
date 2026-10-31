using System.Numerics;
using SmNet;
using SmServer;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// sim/movement-fixes (the owner's report of 2026-11-01; protocol 51): (A) the Devastator / Havoc hop goes down stairs and off ledges while it keeps going forward
/// (gravity on top of its curve over a drop; input does not end it in mid-air); (B) the jump-pack melee press in a full jump acts on its own tick at every point of
/// the jump (the data's 8 m split between the ground pound and the sword slash is the only gate); (C) the Assault / Raptor short hop (a Space tap, the dash) starts
/// at any moment while falling: off a ledge, at a jump's apex and in its fall; a hold there stays the buffered long press; the spacing applies. Server against
/// client prediction for each. Synthetic specs (EvadeTests' hop, JumpPackDashTests' dash, JumpPackAttackTests' slash); no original bytes.
/// </summary>
public class MovementFixesTests(ITestOutputHelper output)
{
    const float Dt = 1f / 30f;
    const int Assault = 0, Heavy = 3;

    // ---------------------------------------------------------------- level helpers

    static CollisionWorld Mesh(params (Vector3 A, Vector3 B, Vector3 C, Vector3 D)[] quads)
    {
        var v = new List<Vector3>(); var idx = new List<int>();
        foreach (var (a, b, c, d) in quads) { int n = v.Count; v.AddRange([a, b, c, d]); idx.AddRange([n, n + 1, n + 2, n, n + 2, n + 3]); }
        var mesh = new StaticMesh(v.ToArray(), idx.ToArray());
        return new CollisionWorld(mesh, mesh);
    }

    static (Vector3, Vector3, Vector3, Vector3) Floor(float z, float x0 = -60, float x1 = 60, float y0 = -60, float y1 = 60) =>
        (new(x0, y0, z), new(x1, y0, z), new(x1, y1, z), new(x0, y1, z));

    static (Vector3, Vector3, Vector3, Vector3) Riser(float y, float z0, float z1) => (new(-60, y, z0), new(60, y, z0), new(60, y, z1), new(-60, y, z1));

    /// <summary>A landing at <paramref name="top"/> to y = <paramref name="from"/>, then <paramref name="steps"/> stairs of <paramref name="rise"/> m (negative: down) every <paramref name="tread"/> m, then a floor.</summary>
    static CollisionWorld Stairs(float top, float from, int steps, float rise, float tread)
    {
        var quads = new List<(Vector3, Vector3, Vector3, Vector3)> { Floor(top, y0: -300f, y1: from) };
        float z = top;
        for (int k = 0; k < steps; k++)
        {
            float y = from + k * tread, next = z + rise;
            quads.Add(Riser(y, MathF.Min(z, next) - 1f, MathF.Max(z, next)));
            quads.Add(Floor(next, y0: y, y1: y + tread));
            z = next;
        }
        quads.Add(Floor(z, y0: from + steps * tread, y1: 300f));
        return Mesh(quads.ToArray());
    }

    // ---------------------------------------------------------------- A: the heavy hop

    static readonly SimConfig Hop = EvadeTests.HopConfig;

    static PlayerState Heavyweight(Vector3 at) => Combat.Respawned(new PlayerState(1, at, Vector3.Zero, 0f, true) { Class = Heavy }, Hop);

    /// <summary>
    /// A forward hop pressed on tick 1 with W held throughout (<paramref name="stick"/>; the stick ends a hop on the ground from its MoveAt, so a held W tests that it
    /// does not in the air) or with no stick after the press; every state after each tick.
    /// </summary>
    static List<PlayerState> HopForward(Vector3 at, CollisionWorld world, int ticks = 60, bool stick = true)
    {
        var s = Heavyweight(at);
        var states = new List<PlayerState>();
        for (int n = 1; n <= ticks; n++)
        {
            s = Combat.StepPlayer(s, new PlayerInput(0, 0f, stick || n == 1 ? 1f : 0f, 0f, n == 1 ? InputButtons.Jump : InputButtons.None), Hop, out _, world);
            states.Add(s);
        }
        return states;
    }

    static string Trace(List<PlayerState> states, int count = 34) =>
        string.Join(" ", states.Take(count).Select((p, i) => $"{i + 1}:{p.Position.Y:F2}/{p.Position.Z:F2}{(p.Grounded ? "g" : "")}{(p.EvadeTicks > 0 ? "e" : "")}"));

    [Theory]
    [InlineData(0.3f)]   // left before the hop's curve (it lifts from 0.1 s): before the fix the curve lifted the falling body back above the ledge
    [InlineData(1.5f)]   // left while rising
    [InlineData(2.5f)]   // left on the way down
    public void The_hop_off_a_ledge_goes_down_while_it_keeps_going_forward(float edge)
    {
        const float top = 2f;
        var world = Mesh(Floor(top, y1: edge), Riser(edge, -1f, top), Floor(0f, y0: edge));
        var states = HopForward(new Vector3(0, 0, top), world);
        output.WriteLine($"edge {edge}: {Trace(states)}");
        int left = states.FindIndex(p => !p.Grounded && p.Position.Y > edge);
        int landed = states.FindIndex(left, p => p.Grounded && p.Position.Z < 0.01f);
        Assert.True(left >= 0 && landed > left, "never fell off the ledge or never landed");
        for (int k = left + 1; k <= landed; k++)
        {
            var (was, now) = (states[k - 1], states[k]);
            Assert.True(now.EvadeTicks == k + 1, $"tick {k + 1}: the hop ended in the air ({Trace(states)})");   // the stick does not end it in mid-air
            Assert.True(now.Position.Y > was.Position.Y + 0.05f, $"tick {k + 1}: no forward motion in the air");
            Assert.True(now.Position.Z <= top + Hop.Evades[1].Forward.PeakHeight + 1e-3f);
        }
        // Over the drop the body is never held at or above the take-off height once its curve has peaked, and it comes down well before the clip's end.
        float peakAt = 0.35f;   // the synthetic curve's top (0.3 to 0.4 s)
        int afterPeak = Math.Max(left, (int)MathF.Ceiling(peakAt / Dt));
        Assert.True(states.Skip(afterPeak + 3).TakeWhile(p => !p.Grounded).All(p => p.Position.Z < top), $"held at the take-off height: {Trace(states)}");
        Assert.True(landed + 1 < 30, $"landed only on tick {landed + 1}");
        Assert.InRange(states[landed].Position.Y, edge + 0.5f, 6.05f);
        // Then it walks on with the stick (the hop's motion was not cut short in the air).
        Assert.True(states[^1].Grounded && states[^1].Position.Y > states[landed].Position.Y);
    }

    [Fact]
    public void A_hop_off_a_ledge_before_its_curve_is_not_lifted_back_up_by_it()
    {
        // Off the edge before the curve starts (0.1 s): before the fix the curve then lifted the falling body back up to 2.2 m and held it there.
        var world = Mesh(Floor(2f, y1: 0.3f), Riser(0.3f, -1f, 2f), Floor(0f, y0: 0.3f));
        var hop = HopForward(new Vector3(0, 0, 2), world);
        var states = hop.Take(14).ToList();
        float lowest = states.Skip(2).Min(p => p.Position.Z);
        output.WriteLine(Trace(hop));
        // Fourteen ticks after the press (0.47 s) the body has come well down from 2 m (before the fix: 2.2 m, above the take-off height).
        Assert.True(states[^1].Position.Z < 1.2f, $"z {states[^1].Position.Z:F2} after 14 ticks");
        Assert.True(lowest < 1.2f);
    }

    [Fact]
    public void The_hop_down_stairs_follows_them_down_while_it_keeps_going_forward()
    {
        // 0.25 m risers every 0.5 m going down from y = 0.5 (a 27 degree flight), from 3 m to 0.
        var world = Stairs(3f, 0.5f, 12, -0.25f, 0.5f);
        var states = HopForward(new Vector3(0, 0, 3), world);
        output.WriteLine(Trace(states));
        // By the end of the curve (0.6 s, tick 18) the body is well below the take-off height and still going forward; it never rose above take-off + the curve.
        Assert.True(states[17].Position.Z < 3f - 0.6f, $"z {states[17].Position.Z:F2} at 0.6 s: {Trace(states)}");
        Assert.All(states, p => Assert.True(p.Position.Z <= 3f + 0.29f));
        for (int k = 1; k < 25; k++) Assert.True(states[k].Position.Y > states[k - 1].Position.Y + 0.05f, $"tick {k + 1}: stopped ({Trace(states)})");
        Assert.True(states[29].Grounded);
        Assert.True(states[29].Position.Z < 3f - 1.2f);
    }

    [Theory]
    [InlineData(0.25f, 0.6f)]   // stairs up: 0.25 m risers every 0.6 m
    [InlineData(0.2f, 0.4f)]    // steeper
    public void The_hop_up_stairs_still_climbs_them_without_ending_early(float rise, float tread)
    {
        var world = Stairs(0f, 1f, 10, rise, tread);
        var states = HopForward(Vector3.Zero, world, ticks: 29, stick: false);
        output.WriteLine(Trace(states));
        for (int k = 0; k < 29; k++) Assert.True(states[k].EvadeTicks == k + 1, $"tick {k + 1}: the hop ended at {states[k].Position}");
        var end = states[^1];
        Assert.True(end.Grounded);
        Assert.True(end.Position.Z > 4f * rise, $"climbed only to {end.Position.Z:F2}");
        Assert.InRange(end.Position.Y, 4.5f, 6.05f);
    }

    [Fact]
    public void The_hop_up_a_20_degree_ramp_and_on_flat_ground_is_unchanged()
    {
        float rise = 6f * MathF.Tan(20f * MathF.PI / 180f);
        var ramp = Mesh(Floor(0f, y1: 1f), (new(-60, 1, 0), new(60, 1, 0), new(60, 7, rise), new(-60, 7, rise)), Floor(rise, y0: 7f));
        var up = HopForward(Vector3.Zero, ramp, ticks: 29, stick: false);
        Assert.All(up.Select((p, k) => (p, k)), x => Assert.Equal(x.k + 1, x.p.EvadeTicks));
        Assert.True(up[^1].Grounded && up[^1].Position.Z > 1.2f, $"{up[^1].Position}");
        // Flat ground: the curve tick for tick (no gravity on top of it), as before.
        var flat = HopForward(Vector3.Zero, Mesh(Floor(0f)), ticks: 29, stick: false);
        for (int n = 1; n <= 29; n++)
            Assert.True(MathF.Abs(flat[n - 1].Position.Z - Hop.Evades[1].Forward.HeightAt(n * Dt)) < 0.01f, $"tick {n}: z {flat[n - 1].Position.Z}");
    }

    [Fact]
    public void A_hop_whose_clip_ends_in_the_air_falls_on_normally_and_lands()
    {
        // A 30 m drop just ahead: the clip (1 s) ends in the air; then the walk-off rule's straight drop under gravity, landing at the bottom.
        var world = Mesh(Floor(30f, y1: 0.5f), Riser(0.5f, -1f, 30f), Floor(0f, y0: 0.5f));
        var states = HopForward(new Vector3(0, 0, 30), world, ticks: 120);
        int ended = states.FindIndex(p => p.EvadeTicks == 0);
        output.WriteLine($"ended on tick {ended + 1} at {states[ended].Position}; {Trace(states, 40)}");
        Assert.Equal(31, ended + 1);                               // the whole clip played (1 s = 30 ticks; the stick did not end it in the air)
        Assert.False(states[ended].Grounded);
        Assert.True(states[ended].Position.Z < 30f - 3f);
        var after = states.Skip(ended + 2).TakeWhile(p => !p.Grounded).ToList();
        Assert.NotEmpty(after);
        Assert.All(after, p => Assert.True(MathF.Abs(p.Velocity.X) + MathF.Abs(p.Velocity.Y) < 1e-4f));   // a normal walk-off fall: straight down
        Assert.Contains(states, p => p.Grounded && p.Position.Z < 0.01f);
    }

    // ---------------------------------------------------------------- C: the short hop (dash) while falling

    static readonly SimConfig Dash = JumpPackDashTests.Config with
    {
        JumpPacks = [JumpPackDashTests.Config.JumpPacks[0] with { HoldTiers = true }],
        InputBufferWindow = 0.3f,
        RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f,
    };

    static PlayerState Flyer(Vector3 at) => Combat.Respawned(new PlayerState(1, at, Vector3.Zero, 0f, true) { Class = Assault }, Dash);

    static PlayerState Step(PlayerState s, InputButtons b, float my = 0f, CollisionWorld? world = null) =>
        Combat.StepPlayer(s, new PlayerInput(0, 0f, my, 0f, b), Dash, out _, world);

    /// <summary>A 20 m platform ending at y = 2 over flat ground.</summary>
    static readonly CollisionWorld Platform = Mesh(Floor(20f, y1: 2f), Riser(2f, -1f, 20f), Floor(0f, y0: 2f, y1: 300f));

    [Theory]
    [InlineData(3)]    // just off the edge
    [InlineData(12)]   // well into the fall
    public void A_space_tap_while_falling_off_a_ledge_dashes_on_its_release(int fallTicks)
    {
        var s = Flyer(new Vector3(0, 0, 20));
        int n = 0;
        while (s.Grounded && n++ < 60) s = Step(s, InputButtons.None, 1f, Platform);   // walk off the edge
        Assert.False(s.Grounded);
        for (int k = 0; k < fallTicks; k++) s = Step(s, InputButtons.None, 0f, Platform);
        Assert.Equal((JumpPhase.None, 0), (s.Jump, s.EvadeTicks));
        float power = s.JumpPower, z = s.Position.Z;
        s = Step(s, InputButtons.Jump, 0f, Platform);              // the tap's press: kept in the buffer while Space is down
        Assert.Equal(0, s.EvadeTicks);
        s = Step(s, InputButtons.None, 0f, Platform);              // its release: the dash
        output.WriteLine($"after {fallTicks} ticks of falling: {s.EvadeTicks} evade ticks, z {z:F2} -> {s.Position.Z:F2}, power {power} -> {s.JumpPower}");
        Assert.Equal(1, s.EvadeTicks);
        Assert.Equal(EvadeDirection.Forward, s.EvadeDirection);
        Assert.True(s.EvadeLock > 0);
        Assert.Equal(JumpPhase.None, s.Jump);
        Assert.Equal(BufferedAction.None, s.Buffered);             // the tap is spent
        Assert.True(s.JumpPower >= power);                         // a dash costs no power
        // The dash's flight: it rises off the fall and goes forward, then lands on the ground below in its landing part.
        float startY = s.Position.Y;
        for (int k = 0; k < 8; k++) s = Step(s, InputButtons.None, 0f, Platform);
        Assert.True(s.Position.Z > z, $"z {s.Position.Z:F2}");
        Assert.True(s.Position.Y > startY + 3f);
        int guard = 0;
        while (!s.Grounded && guard++ < 200) s = Step(s, InputButtons.None, 0f, Platform);
        Assert.True(s.Grounded);
    }

    [Theory]
    [InlineData(JumpPhase.Hang)]
    [InlineData(JumpPhase.Fall)]
    public void A_space_tap_after_a_jumps_apex_dashes_on_its_release(JumpPhase when)
    {
        var s = Flyer(Vector3.Zero);
        for (int k = 0; k < 20; k++) s = Step(s, InputButtons.Jump);   // the full jump (held past the full hold)
        int guard = 0;
        while (s.Jump != when && guard++ < 300) s = Step(s, InputButtons.None);
        if (when == JumpPhase.Fall) for (int k = 0; k < 6; k++) s = Step(s, InputButtons.None);
        Assert.Equal(when, s.Jump);
        float power = s.JumpPower;
        s = Step(s, InputButtons.Jump, 1f);
        Assert.Equal(when, s.Jump);                                  // still the jump while Space is down
        float y = s.Position.Y, z = s.Position.Z;
        s = Step(s, InputButtons.None, 1f);
        output.WriteLine($"{when}: z {z:F2} -> {s.Position.Z:F2}, phase {s.Jump}, evade {s.EvadeTicks}, power {power:F1} -> {s.JumpPower:F1}");
        Assert.Equal((JumpPhase.None, 1), (s.Jump, s.EvadeTicks));
        Assert.True(s.JumpPower >= power);
        Assert.True(s.Position.Y > y && s.Position.Z > z, $"y {y:F2} -> {s.Position.Y:F2}, z {z:F2} -> {s.Position.Z:F2}");   // the flight lifts it off the fall
        // It lands from the dash (the dash's landing part), not from the jump.
        guard = 0;
        while (!s.Grounded && guard++ < 300) s = Step(s, InputButtons.None);
        Assert.True(s.Grounded);
        Assert.NotEqual(JumpPhase.Landed, s.Jump);
    }

    [Fact]
    public void A_hold_while_falling_stays_the_long_press_and_a_press_while_rising_does_nothing_in_the_air()
    {
        // Held past the dash's decision tick in the fall: no dash; it lands and takes off committed to at least the small jump (feel/jump-pack-2, unchanged).
        var s = Flyer(Vector3.Zero);
        for (int k = 0; k < 20; k++) s = Step(s, InputButtons.Jump);
        int guard = 0;
        while (s.Jump != JumpPhase.Fall && guard++ < 300) s = Step(s, InputButtons.None);
        var states = new List<PlayerState>();
        for (int k = 0; k < 200 && states.Count(p => p.Jump == JumpPhase.Ascent) < 3; k++) { s = Step(s, InputButtons.Jump); states.Add(s); }
        Assert.DoesNotContain(states, p => p.EvadeTicks > 0);
        Assert.Contains(states, p => p.Jump == JumpPhase.Landed);
        Assert.Contains(states, p => p.Jump == JumpPhase.Ascent);
        // A tap in the ascent (still rising): Space is the hold there; no dash in the air.
        var r = Flyer(Vector3.Zero);
        for (int k = 0; k < 20; k++) r = Step(r, InputButtons.Jump);
        Assert.Equal(JumpPhase.Ascent, r.Jump);
        r = Step(r, InputButtons.None); r = Step(r, InputButtons.Jump); r = Step(r, InputButtons.None);
        Assert.Equal((JumpPhase.Ascent, 0), (r.Jump, r.EvadeTicks));
    }

    [Fact]
    public void The_air_dash_waits_for_the_spacing_and_a_tap_buffered_meanwhile_dashes_once_it_is_over()
    {
        var s = Flyer(new Vector3(0, 0, 20));
        int n = 0;
        while (s.Grounded && n++ < 60) s = Step(s, InputButtons.None, 1f, Platform);
        s = s with { EvadeLock = 6 };                              // as if a dash had started 0.37 s ago
        s = Step(s, InputButtons.Jump, 0f, Platform);
        s = Step(s, InputButtons.None, 0f, Platform);
        Assert.Equal(0, s.EvadeTicks);                             // refused: the spacing is not over
        Assert.Equal(BufferedAction.Jump, s.Buffered);             // kept in the buffer
        Assert.Equal(JumpPhase.None, s.Jump);
        int waited = 0;
        while (s.EvadeTicks == 0 && waited++ < 20) s = Step(s, InputButtons.None, 0f, Platform);
        output.WriteLine($"dashed {waited} ticks after the release");
        Assert.InRange(waited, 1, 6);                              // within the 0.3 s window, once the lock ran out
        // A tap with the lock still on longer than the buffer's window is lost (no dash later).
        var t = Flyer(new Vector3(0, 0, 20));
        n = 0;
        while (t.Grounded && n++ < 60) t = Step(t, InputButtons.None, 1f, Platform);
        t = t with { EvadeLock = 30 };
        t = Step(t, InputButtons.Jump, 0f, Platform);
        for (int k = 0; k < 14; k++) { t = Step(t, InputButtons.None, 0f, Platform); Assert.Equal(0, t.EvadeTicks); }
        Assert.Equal(BufferedAction.None, t.Buffered);
    }

    // ---------------------------------------------------------------- B: the ground pound in a full jump

    [Fact]
    public void A_melee_press_in_a_full_jump_acts_on_its_own_tick_the_pound_from_8_m_up_the_slash_below()
    {
        var pack = JumpPackAttackTests.Config.JumpPacks[0] with { Slash = JumpPackFeelTests.Snappy, HoldTiers = true };
        var c = JumpPackAttackTests.Config with { JumpPacks = [pack] };
        float split = pack.Slash!.MinPoundHeight;
        // The full jump's ticks after the lift-off, then one melee press on each in turn.
        var jump = new List<PlayerState>();
        {
            var s = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = Assault }, c);
            for (int k = 0; k < 400 && !(k > 10 && s.Grounded); k++) { s = Combat.StepPlayer(s, new PlayerInput(0, 0, 0, 0, k < 20 ? InputButtons.Jump : InputButtons.None), c, out _); jump.Add(s); }
        }
        float apex = jump.Max(p => p.Position.Z);
        Assert.True(apex > split + 4f, $"apex {apex:F1}");
        int pounds = 0, slashes = 0;
        for (int at = 0; at < jump.Count; at++)
        {
            if (!JumpPack.InFlight(jump[at].Jump) || jump[at].Grounded) continue;   // from the tick after the lift-off to the last one in the air
            if (at + 1 >= jump.Count) break;
            var s = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = Assault }, c);
            for (int k = 0; k <= at; k++) s = Combat.StepPlayer(s, new PlayerInput(0, 0, 0, 0, k < 20 ? InputButtons.Jump : InputButtons.None), c, out _);
            float height = s.Position.Z;
            var b = at + 1 < 20 ? InputButtons.Jump | InputButtons.Melee : InputButtons.Melee;
            s = Combat.StepPlayer(s, new PlayerInput(0, 0, 0, 0, b), c, out _);
            // The dive started this tick (just off the ground a slash's dive can land in the same tick: then the recovery).
            Assert.True(s.Jump == JumpPhase.Dive || (s.Jump == JumpPhase.Recover && height < 2f), $"tick {at + 1} at {height:F1} m: the press did nothing ({s.Jump})");
            Assert.True((height < split) == s.JumpSlash, $"tick {at + 1} at {height:F2} m: slash {s.JumpSlash}");
            if (s.JumpSlash) slashes++; else pounds++;
        }
        output.WriteLine($"apex {apex:F1} m: {pounds} ticks pound at once, {slashes} slash at once (below {split} m)");
        Assert.True(pounds > 20 && slashes > 5);
    }

    // ---------------------------------------------------------------- D: steering a charge held in the air

    /// <summary>The Assault (jump pack) with RushTests' bull rush (10 m/s), and the marine with the same rush and no pack (class 1).</summary>
    static readonly SimConfig Charger = JumpPackAttackTests.Config with
    {
        Classes = [JumpPackAttackTests.Config.Classes[0] with { MeleeRush = RushTests.Spec }, JumpPackAttackTests.Config.Classes[1]],
    };

    /// <summary>A plateau 8 m high whose edge is y = 0 (plateau at y &lt; 0) over a floor.</summary>
    static CollisionWorld ChargeLedge() => Mesh(Floor(8f, y0: -60f, y1: 0f), Riser(0f, -1f, 8f), Floor(0f, y0: 0f, y1: 300f));

    /// <summary>Charges +Y off the 8 m plateau from 9 m before its edge, then holds the stick's <paramref name="side"/> in the air; every state from the press on.</summary>
    static List<PlayerState> ChargeOff(int cls, float side, CollisionWorld world)
    {
        var s = Combat.Respawned(new PlayerState(1, new Vector3(0, -9f, 8f), Vector3.Zero, 0f, true) { Class = cls }, Charger);
        for (int k = 0; k < 12; k++) s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 1f, 0f, InputButtons.Sprint), Charger, out _, world);
        s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 1f, 0f, InputButtons.Sprint | InputButtons.MeleeHeavy), Charger, out _, world);
        Assert.Equal(RushPhase.Rush, s.Rush);
        var states = new List<PlayerState> { s };
        for (int k = 0; k < 120 && !(states.Count > 3 && s.Grounded && !states[^2].Grounded); k++)
        {
            s = Combat.StepPlayer(s, new PlayerInput(0, s.Grounded ? 0f : side, 0f, 0f, InputButtons.None), Charger, out _, world);
            states.Add(s);
        }
        return states;
    }

    [Theory]
    [InlineData(1f)]    // D: bends right
    [InlineData(-1f)]   // A: bends left
    [InlineData(0.5f)]  // half a stick (a pad): half the rate
    public void A_charge_off_a_ledge_steers_with_A_and_D_in_the_air_at_its_held_speed(float side)
    {
        var world = ChargeLedge();
        var states = ChargeOff(Assault, side, world);
        var straight = ChargeOff(Assault, 0f, world);
        int left = states.FindIndex(p => !p.Grounded), landed = states.FindLastIndex(p => p.Grounded);
        Assert.True(left > 0 && landed > left);
        float speed = new Vector2(states[left].Velocity.X, states[left].Velocity.Y).Length();
        var air = states.Skip(left + 1).Take(landed - left - 1).ToList();
        Assert.NotEmpty(air);
        Assert.All(air, p => Assert.Equal(RushPhase.Rush, p.Rush));
        Assert.All(air, p => Assert.Equal(speed, new Vector2(p.Velocity.X, p.Velocity.Y).Length(), 3));   // a turn: never faster, never slower
        // The heading turns at the rate and the velocity points along it (the contact cone follows the path).
        float perTick = Rush.AirTurnRate * MathF.PI / 180f * Dt * side;
        for (int k = 1; k < air.Count; k++)
        {
            Assert.Equal(-perTick, air[k].Yaw - air[k - 1].Yaw, 4);
            var heading = new Vector2(-MathF.Sin(air[k].Yaw), MathF.Cos(air[k].Yaw));
            Assert.Equal(1f, Vector2.Dot(heading, Vector2.Normalize(new Vector2(air[k].Velocity.X, air[k].Velocity.Y))), 4);
        }
        float drift = states[landed].Position.X - straight[straight.FindLastIndex(p => p.Grounded)].Position.X;
        float turned = (states[landed - 1].Yaw - states[left].Yaw) * 180f / MathF.PI;
        output.WriteLine($"side {side}: {air.Count + 1} ticks in the air at {speed:F2} m/s, turned {turned:F1} deg, landed {drift:F2} m to the side of the straight charge");
        Assert.True(MathF.Sign(drift) == MathF.Sign(side) && MathF.Abs(drift) > 0.5f * MathF.Abs(side), $"drift {drift:F2}");
        // The ground part before the edge is the straight charge's, tick for tick.
        for (int k = 0; k <= left; k++) Assert.Equal(straight[k], states[k]);
    }

    [Fact]
    public void The_tactical_steers_a_held_charge_too_the_ork_does_not_and_the_ground_charge_is_unchanged()
    {
        var world = ChargeLedge();
        // The Tactical / Chaos Tactical (a class with the charge and no jump pack; the owner's follow-up of 2026-11-01): the same steering.
        var marine = ChargeOff(1, 1f, world);
        var straightMarine = ChargeOff(1, 0f, world);
        int left = marine.FindIndex(p => !p.Grounded), landedAt = marine.FindLastIndex(p => p.Grounded);
        float speed = new Vector2(marine[left].Velocity.X, marine[left].Velocity.Y).Length();
        var air = marine.Skip(left + 1).TakeWhile(p => !p.Grounded).ToList();
        for (int k = 1; k < air.Count; k++) Assert.Equal(-Rush.AirTurnRate * MathF.PI / 180f * Dt, air[k].Yaw - air[k - 1].Yaw, 4);
        Assert.All(air, p => Assert.Equal(speed, new Vector2(p.Velocity.X, p.Velocity.Y).Length(), 3));
        Assert.True(marine[landedAt].Position.X - straightMarine[straightMarine.FindLastIndex(p => p.Grounded)].Position.X > 2f);
        // The Ork Boy's sprint attack (added content, class race Orks) is not steered.
        var orkConfig = Charger with { Classes = [Charger.Classes[0], Charger.Classes[1] with { Race = Race.Orks }] };
        var ork = Combat.Respawned(new PlayerState(1, new Vector3(0, -9f, 8f), Vector3.Zero, 0f, true) { Class = 1 }, orkConfig);
        for (int k = 0; k < 12; k++) ork = Combat.StepPlayer(ork, new PlayerInput(0, 0f, 1f, 0f, InputButtons.Sprint), orkConfig, out _, world);
        ork = Combat.StepPlayer(ork, new PlayerInput(0, 0f, 1f, 0f, InputButtons.Sprint | InputButtons.MeleeHeavy), orkConfig, out _, world);
        float orkYaw = ork.Yaw;
        for (int k = 0; k < 40; k++)
        {
            ork = Combat.StepPlayer(ork, new PlayerInput(0, ork.Grounded ? 0f : 1f, 0f, 0f, InputButtons.None), orkConfig, out _, world);
            if (ork.Rush == RushPhase.Rush) Assert.Equal(orkYaw, ork.Yaw, 6);
        }
        // On the ground D still ends the charge from its stick branch (unchanged).
        var s = Combat.Respawned(new PlayerState(1, new Vector3(0, -40f, 8f), Vector3.Zero, 0f, true) { Class = Assault }, Charger);
        for (int k = 0; k < 12; k++) s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 1f, 0f, InputButtons.Sprint), Charger, out _, world);
        s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 1f, 0f, InputButtons.Sprint | InputButtons.MeleeHeavy), Charger, out _, world);
        float yaw = s.Yaw;
        int ticks = 0;
        while (s.Rush == RushPhase.Rush && ticks++ < 60) { s = Combat.StepPlayer(s, new PlayerInput(0, 1f, 0f, 0f, InputButtons.None), Charger, out _, world); if (s.Rush == RushPhase.Rush) Assert.Equal(yaw, s.Yaw, 6); }
        Assert.True(s.Grounded);
        Assert.True(ticks * Dt <= RushTests.Spec.Rush.End + Dt, $"the ground charge ran {ticks} ticks with D held");
    }

    [Fact]
    public void A_steered_charge_in_the_air_hits_a_player_on_its_bent_path_that_the_straight_one_misses()
    {
        // A target standing on the floor 4 m to the right of the straight path, about where the charge lands; prediction of the contact uses the same state.
        var world = ChargeLedge();
        var straight = ChargeOff(Assault, 0f, world);
        var bent = ChargeOff(Assault, 1f, world);
        var land = bent[bent.FindLastIndex(p => p.Grounded)].Position;
        float pathX(List<PlayerState> st, float y) => st.Where(p => p.Position.Y <= y).Select(p => p.Position.X).DefaultIfEmpty(0f).Last();
        var target = new Vector3(land.X, land.Y - 0.5f, 0f);
        bool Reaches(List<PlayerState> st) => st.Zip(st.Skip(1)).Any(p => p.Second.Rush == RushPhase.Rush
            && Rush.Contact(p.First.Position, p.Second.Position, p.Second.Yaw, target, RushTests.Spec, Charger) is not null);
        output.WriteLine($"target at {target}; straight path x {pathX(straight, target.Y):F2}, bent {pathX(bent, target.Y):F2}");
        Assert.True(Reaches(bent));
        Assert.False(Reaches(straight));
    }

    // ---------------------------------------------------------------- prediction against the server

    sealed record Case(string Name, SimConfig Config, int Class, CollisionWorld Level, SpawnPoint[] Spawns, Func<int, uint, PlayerInput> Input);

    static void Matches(ITestOutputHelper output, int seed, Case cs, Func<List<PlayerState>, int> counted, int minCounted)
    {
        var map = new GameMap("movement_fixes", cs.Level, cs.Spawns, 78);
        var link = new LinkConditions(TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(10), 0.05);
        var h = new Harness(link, seed, 0, cs.Config, new ServerOptions { PeerTimeout = TimeSpan.FromMinutes(5) }, map: map);
        const int n = 2;
        for (int i = 0; i < n; i++) h.AddClient(nonce: (uint)(500 + i), options: new ClientOptions { RequestedClass = (byte)cs.Class, LoadMap = _ => new LoadedMap(cs.Level, 78) });
        var truth = new SortedDictionary<uint, Dictionary<int, PlayerState>>();
        var predicted = Enumerable.Range(0, n).Select(_ => new Dictionary<uint, PlayerState>()).ToArray();
        h.Run(TimeSpan.FromSeconds(30), cs.Input, () =>
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
            int count = counted(mine), corrections = c.CorrectionErrors.Count(e => e > 1e-4f);
            output.WriteLine($"{cs.Name} client {i}: compared {compared}, {mismatches.Count} mismatches, {corrections} corrections; counted {count}");
            foreach (var m in mismatches.Take(3)) output.WriteLine("  " + m);
            Assert.True(compared > 300);
            Assert.True(count >= minCounted, $"client {i}: counted only {count}");
            Assert.Empty(mismatches);
            Assert.Equal(0, corrections);
        }
    }

    [Theory]
    [InlineData(511)]
    [InlineData(512)]
    public void Prediction_matches_the_server_for_hops_down_stairs_and_off_ledges_under_150ms_rtt_and_5pct_loss(int seed)
    {
        // A 3 m landing to y = 1, stairs down (0.25 m every 0.5 m) to the ground, and back: the hops start on the landing heading +y.
        var level = Stairs(3f, 1f, 12, -0.25f, 0.5f);
        var config = EvadeTests.Harmless(Hop) with { RespawnTime = 0.5f };
        PlayerInput Input(int i, uint t)
        {
            uint phase = (t + (uint)i * 11) % 75;
            // Hop forward down the stairs (W held through the hop), then walk back up to the landing (about as far as the hop went).
            bool press = phase == 0 || phase == 1;
            float my = phase < 30 ? 1f : -1f;
            return new PlayerInput(t, i == 0 ? 0f : 0.4f, my, 0f, press ? InputButtons.Jump : InputButtons.None, 0f);
        }
        var spawns = new[] { new SpawnPoint(new Vector3(0, -1, 3), 0f), new SpawnPoint(new Vector3(3, -1, 3), 0f) };
        Matches(output, seed, new Case("hop", config, Heavy, level, spawns, Input),
            mine => mine.Count(p => p.EvadeTicks > 0 && !p.Grounded && p.Velocity.Z < -1f), 20);
    }

    [Theory]
    [InlineData(521)]
    [InlineData(522)]
    public void Prediction_matches_the_server_for_space_taps_while_falling_under_150ms_rtt_and_5pct_loss(int seed)
    {
        var config = Dash with { JumpPacks = [Dash.JumpPacks[0] with { RechargeRate = 100f, Pound = null, Landings = [] }] };
        PlayerInput Input(int i, uint t)
        {
            // 150-tick cycles: a full jump (Space down 20 ticks), then a one-tick tap somewhere in its hang or fall, or held 8 ticks there (the long press).
            uint cycle = (t + (uint)i * 37) / 150, phase = (t + (uint)i * 37) % 150;
            int[] at = [48, 56, 64, 72];
            int tapAt = at[(int)(cycle % (uint)at.Length)], length = cycle % 3 == 2 ? 8 : 1;
            bool down = phase < 20 || (phase >= tapAt && phase < tapAt + length);
            return new PlayerInput(t, 0f, phase > 100 ? -0.6f : 0.6f, 0.3f * i, down ? InputButtons.Jump : InputButtons.None, 0f);
        }
        var level = Mesh(Floor(0f, -300, 300, -300, 300));
        var spawns = new[] { new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new Vector3(5, 0, 0), 0f) };
        Matches(output, seed, new Case("air dash", config, Assault, level, spawns, Input),
            mine => mine.Zip(mine.Skip(1)).Count(p => p.Second.EvadeTicks == 1 && !p.First.Grounded), 3);
    }
}
