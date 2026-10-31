using System.Numerics;
using SmContent;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// sim/charge-needs-sprint (protocol 49; owner 2026-10-31): "you also need to be holding shift. if you are holding W, A, S, D, or a combination of them, then press f, you should do a
/// stun strike in that direction." Shift + W + F charges; F without Shift (or without W) is the stun strike in the held direction (forward for W or none); the buffered press follows the same test.
/// </summary>
public class ChargeNeedsSprintTests(ITestOutputHelper output)
{
    const int Marine = 0;
    static readonly MeleeStep Light = new(1f, 0.2f, 0.4f, 0.6f, 0.5f, 0.7f, 10f, 5f, 3.5f);
    static readonly MeleeStep Heavy = new(1.2f, 0.3f, 0f, 0f, 0.8f, 0.9f, 20f, 5f, 3.5f) { PrimaryReaction = new(0f, ReactionKind.Stun) };
    static readonly SimConfig Config = new()
    {
        Classes = [new ClassLoadout("marine", -1, -1, [Light]) { MeleeHeavy = [Heavy], MeleeRush = RushTests.Spec, Evade = -1, Side = Team.SpaceMarines }],
        RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f,
    };
    static PlayerState Fresh(SimConfig c) => Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = Marine }, c);
    static PlayerState Go(PlayerState s, SimConfig c, float mx, float my, InputButtons b) => Combat.StepPlayer(s, new PlayerInput(0, mx, my, 0f, b), c, out _);

    [Fact]
    public void Shift_W_F_charges_and_W_F_without_Shift_is_a_forward_stun_strike_never_a_charge()
    {
        var charge = Go(Fresh(Config), Config, 0f, 1f, InputButtons.Sprint | InputButtons.MeleeHeavy);
        Assert.Equal((RushPhase.Rush, 0), (charge.Rush, charge.MeleeStep));
        var strike = Go(Fresh(Config), Config, 0f, 1f, InputButtons.MeleeHeavy);
        Assert.Equal((RushPhase.None, 1, true, EvadeDirection.Forward), (strike.Rush, strike.MeleeStep, strike.MeleeHeavy, strike.MeleeDirection));
    }

    [Theory]
    [InlineData(0f, 0f, EvadeDirection.Forward)]       // F alone
    [InlineData(0f, 1f, EvadeDirection.Forward)]       // W
    [InlineData(-1f, 0f, EvadeDirection.Left)]         // A
    [InlineData(1f, 0f, EvadeDirection.Right)]         // D
    [InlineData(0f, -1f, EvadeDirection.Back)]         // S
    [InlineData(-1f, 1f, EvadeDirection.ForwardLeft)]  // W + A
    [InlineData(1f, 1f, EvadeDirection.ForwardRight)]  // W + D
    [InlineData(-1f, -1f, EvadeDirection.BackLeft)]    // S + A
    [InlineData(1f, -1f, EvadeDirection.BackRight)]    // S + D
    public void F_without_Shift_is_a_stun_strike_in_the_held_direction(float mx, float my, EvadeDirection expected)
    {
        var s = Go(Fresh(Config), Config, mx, my, InputButtons.MeleeHeavy);
        Assert.Equal((RushPhase.None, 1, true, expected), (s.Rush, s.MeleeStep, s.MeleeHeavy, s.MeleeDirection));
    }

    [Theory]
    [InlineData(-1f, 0f)]
    [InlineData(0f, -1f)]
    [InlineData(1f, -1f)]
    [InlineData(0f, 0f)]
    public void Shift_without_W_is_still_the_stun_strike(float mx, float my)
    {
        var s = Go(Fresh(Config), Config, mx, my, InputButtons.Sprint | InputButtons.MeleeHeavy);
        Assert.Equal((RushPhase.None, 1, true), (s.Rush, s.MeleeStep, s.MeleeHeavy));
    }

    [Fact]
    public void Shift_W_A_and_Shift_W_D_keep_the_side_charge()
    {
        foreach (float mx in new[] { -1f, 1f })
        {
            var s = Go(Fresh(Config), Config, mx, 1f, InputButtons.Sprint | InputButtons.MeleeHeavy);
            Assert.Equal(RushPhase.Rush, s.Rush);
            Assert.Equal(-mx * 25f * MathF.PI / 180f, s.Yaw, 3);
        }
    }

    [Fact]
    public void A_buffered_Shift_W_F_in_the_spacing_replays_as_the_charge_and_W_F_in_it_is_the_stun_strike_at_once()
    {
        var c = Config with { InputBufferWindow = 0.3f };
        // Shift + W + F in the spacing: buffered as a charge; Shift let go before it is allowed, W still held: it charges.
        var s = Fresh(c) with { RushLock = 4 };
        s = Go(s, c, 0f, 1f, InputButtons.Sprint | InputButtons.MeleeHeavy);
        Assert.True(s.BufferedCharge);
        for (int k = 0; k < 8 && s.Rush == RushPhase.None; k++) { Assert.Equal(0, s.MeleeStep); s = Go(s, c, 0f, 1f, InputButtons.None); }
        Assert.Equal(RushPhase.Rush, s.Rush);
        // W + F (no Shift) in the spacing: the stun strike, at once (the spacing only gates the charge); never a charge request.
        var t = Fresh(c) with { RushLock = 4 };
        t = Go(t, c, 0f, 1f, InputButtons.MeleeHeavy);
        Assert.False(t.BufferedCharge);
        Assert.Equal((RushPhase.None, 1, true), (t.Rush, t.MeleeStep, t.MeleeHeavy));
        // A buffered charge replayed with W let go is dropped (neither strike nor charge), as before.
        var u = Fresh(c) with { RushLock = 5 };
        u = Go(u, c, 0f, 1f, InputButtons.Sprint | InputButtons.MeleeHeavy);
        for (int k = 0; k < 25; k++) { u = Go(u, c, 0f, 0f, InputButtons.None); Assert.Equal((RushPhase.None, 0), (u.Rush, u.MeleeStep)); }
    }
}
