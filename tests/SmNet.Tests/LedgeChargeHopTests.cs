using System.Numerics;
using SmNet;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// sim/ledge-charge-hop (protocol 85; the owner, 2026-11-17): a hop out of a ledge charge (Assault Marine, Raptor) keeps the charge's speed and eases it down to the hop's top speed instead of stopping dead and starting at the hop's own
/// speed. Synthetic specs (MeleeCancelTests.Pack: a dash that runs 16.37 m/s along its flight, every direction); the restore switch is <see cref="SimConfig.LedgeHopCarry"/> false.
/// </summary>
public class LedgeChargeHopTests(ITestOutputHelper output)
{
    const float Dt = 1f / 30f, HopSpeed = 16.37f;
    static readonly SimConfig Classic = MeleeCancelTests.Pack;
    static readonly SimConfig Old = MeleeCancelTests.Pack with { LedgeHopCarry = false };

    static float Speed(PlayerState s) => MathF.Sqrt(s.Velocity.X * s.Velocity.X + s.Velocity.Y * s.Velocity.Y);

    static PlayerState Step(PlayerState s, SimConfig c, InputButtons b, float mx = 0f, float my = 1f) => Combat.StepPlayer(s, new PlayerInput(0, mx, my, 0f, b), c, out _);

    /// <summary>A charge held in the air off a 30 m ledge, at <paramref name="speed"/> m/s along +Y (the held speed is the speed it left the ground with).</summary>
    static PlayerState Held(SimConfig c, float speed)
    {
        var s = MeleeCancelTests.Charging(c, cls: 0) with { Position = new Vector3(0f, 0f, 30f), Grounded = false };
        s = Step(s, c, InputButtons.None);
        s = s with { Velocity = new Vector3(0f, speed, s.Velocity.Z) };
        s = Step(s, c, InputButtons.None);
        Assert.Equal(RushPhase.Rush, s.Rush);
        Assert.InRange(Speed(s), speed - 0.01f, speed + 0.01f);
        return s;
    }

    /// <summary>The Space tap (a press, then released) with the stick forward, then <paramref name="ticks"/> ticks on; the horizontal speed after each tick (the press tick first).</summary>
    static List<float> Tap(PlayerState s, SimConfig c, int ticks, out PlayerState end, float mx = 0f, float my = 1f)
    {
        var speeds = new List<float>();
        s = Step(s, c, InputButtons.Jump, mx, my); speeds.Add(Speed(s));
        for (int k = 0; k < ticks; k++) { s = Step(s, c, InputButtons.None, mx, my); speeds.Add(Speed(s)); }
        end = s;
        return speeds;
    }

    [Fact]
    public void A_hop_out_of_a_fast_ledge_charge_keeps_its_speed_and_converges_to_the_hops_top_speed()
    {
        var speeds = Tap(Held(Classic, 30f), Classic, 24, out var end);
        output.WriteLine("with: " + string.Join(" ", speeds.Select(v => v.ToString("F1"))));
        Assert.True(Evade.Evading(end) || end.EvadeTicks == 0);
        // never below the hop's own speed on the way down from the charge (no dead stop, no dip to the hop's start)
        Assert.All(speeds.Take(20), v => Assert.True(v >= HopSpeed - 0.01f, $"speed {v}"));
        Assert.True(speeds[0] > 25f, "the press tick keeps the charge's speed");
        // ... eases down monotonically (never speeds up) and arrives at the hop's top speed
        for (int k = 1; k < speeds.Count; k++) Assert.True(speeds[k] <= speeds[k - 1] + 0.01f, $"tick {k}: {speeds[k]} after {speeds[k - 1]}");
        Assert.InRange(speeds[20], HopSpeed - 0.01f, HopSpeed + 1.5f);
    }

    [Fact]
    public void The_restore_switch_gives_the_protocol_84_stop()
    {
        var speeds = Tap(Held(Old, 30f), Old, 6, out _);
        output.WriteLine("old: " + string.Join(" ", speeds.Select(v => v.ToString("F1"))));
        Assert.True(speeds[0] < 1f, "the press tick stopped the body dead");
    }

    [Fact]
    public void A_ledge_charge_slower_than_the_hop_is_not_slowed_or_sped_up_by_the_carry()
    {
        var speeds = Tap(Held(Classic, 12f), Classic, 8, out _);
        output.WriteLine("slow: " + string.Join(" ", speeds.Select(v => v.ToString("F1"))));
        Assert.InRange(speeds[^1], HopSpeed - 0.01f, HopSpeed + 0.01f);   // the clip's own speed, as before
    }

    [Fact]
    public void The_hop_keeps_the_sticks_direction()
    {
        // D held with the tap: the hop's heading is the stick's way (right of the view), not the charge's
        var speeds = Tap(Held(Classic, 30f), Classic, 10, out var end, mx: 1f, my: 0f);
        Assert.True(Speed(end) > HopSpeed - 0.01f);
        Assert.True(end.Velocity.X > 10f, $"the hop goes right: {end.Velocity}");
        Assert.True(MathF.Abs(end.Velocity.Y) < 3f, $"and not on along the charge: {end.Velocity}");
    }

    [Fact]
    public void A_hop_from_rest_or_a_run_is_the_same_with_and_without_the_rule()
    {
        foreach (float run in new[] { 0f, 6.5f })
        {
            var a = MeleeCancelTests.Fresh(Classic); var b = MeleeCancelTests.Fresh(Classic with { LedgeHopCarry = false });
            for (int k = 0; k < 10 && run > 0f; k++) { a = Step(a, Classic, InputButtons.None); b = Step(b, Old, InputButtons.None); }
            var ta = Tap(a, Classic, 40, out var ea); var tb = Tap(b, Old, 40, out var eb);
            Assert.True(ta.Count(v => v > 10f) > 5, "it did hop");
            Assert.Equal(tb, ta);
            Assert.Equal(eb.Position, ea.Position);
        }
    }

    [Fact]
    public void A_non_jump_pack_class_is_unchanged()
    {
        // The Tactical (its charge, no jump pack) and the Devastator (its hop): the same trace with and without the rule
        var c = MeleeCancelTests.Heavy; var o = c with { LedgeHopCarry = false };
        foreach (int cls in new[] { 0, 1 })
        {
            PlayerState Run(SimConfig cfg)
            {
                var s = MeleeCancelTests.Charging(cfg, cls) with { Position = new Vector3(0f, 0f, 30f), Grounded = false };
                for (int k = 0; k < 3; k++) s = Step(s, cfg, InputButtons.None);
                s = Step(s, cfg, InputButtons.Jump);
                for (int k = 0; k < 12; k++) s = Step(s, cfg, InputButtons.None);
                return s;
            }
            var x = Run(c); var y = Run(o);
            Assert.Equal(y.Position, x.Position);
            Assert.Equal(y.Velocity, x.Velocity);
        }
    }

    [Fact]
    public void A_press_whose_hop_is_refused_glides_only_while_the_tap_can_still_resolve()
    {
        // the evade's cooldown refuses the hop: the press waits in the input buffer (0.3 s) and lapses; the body then falls as before (no unsteered glide to the ground)
        var s = Held(Classic, 30f) with { EvadeLock = 60 };
        var speeds = Tap(s, Classic, 24, out var end);
        output.WriteLine("refused: " + string.Join(" ", speeds.Select(v => v.ToString("F1"))));
        Assert.False(Evade.Evading(end));
        Assert.True(speeds[0] > 20f, "the press tick keeps the charge's speed");
        Assert.True(speeds[^1] < 0.01f, $"the body stopped after the buffer lapsed: {speeds[^1]}");
    }

    [Fact]
    public void A_hop_that_did_not_start_from_a_ledge_charge_gets_no_extra_after_a_collision_or_slide()
    {
        // a plain hop whose body is suddenly faster than its clip (a push, a slide that ended): the clip's own speed
        var s = MeleeCancelTests.Fresh(Classic);
        s = Step(s, Classic, InputButtons.Jump); s = Step(s, Classic, InputButtons.None);
        for (int k = 0; k < 4; k++) s = Step(s, Classic, InputButtons.None);
        Assert.True(Evade.Evading(s));
        s = Step(s with { Velocity = new Vector3(0f, 35f, s.Velocity.Z) }, Classic, InputButtons.None);
        Assert.InRange(Speed(s), HopSpeed - 0.01f, HopSpeed + 0.01f);
    }

    [Fact]
    public void A_ledge_hop_whose_excess_is_gone_never_carries_again()
    {
        var speeds = Tap(Held(Classic, 30f), Classic, 22, out var end);
        Assert.True(Evade.Evading(end));
        Assert.False(end.JumpAirOpen, "the carry has ended with its excess");
        end = Step(end with { Velocity = new Vector3(0f, 35f, end.Velocity.Z) }, Classic, InputButtons.None);
        Assert.InRange(Speed(end), HopSpeed - 0.01f, HopSpeed + 0.01f);
    }

    [Fact]
    public void The_restore_switch_changes_the_config_hash_only_when_on()
    {
        Assert.NotEqual(Classic.Hash(), Old.Hash());
        Assert.Equal(Classic.Hash(), (Classic with { LedgeHopCarry = true }).Hash());
    }
}
