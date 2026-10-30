using System.Numerics;
using SmSim;

namespace SmNet.Tests;

/// <summary>sim/combat-2 (the owner's playtest round 2, 2026-10-30): air control twice as strong (item 28), the bolt pistol ending a melee swing (item 37).</summary>
public class Combat2Tests
{
    [Fact]
    public void Jump_pack_air_control_is_twice_as_strong_as_it_was()
    {
        var c = JumpPackTests.Config with { RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f };
        float After(float speed, float acceleration)
        {
            var cfg = c with { JumpPacks = [JumpPackTests.Pack with { AirSpeed = speed, AirAcceleration = acceleration }] };
            var s = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = 0 }, cfg);
            // A full jump (Jump held 13 ticks); from tick 8 on the stick is forward for 10 ticks.
            for (int k = 0; k < 18; k++) s = Combat.StepPlayer(s, new PlayerInput(0, 0f, k >= 8 ? 1f : 0f, 0f, k < 13 ? InputButtons.Jump : InputButtons.None), cfg, out _);
            return new Vector2(s.Velocity.X, s.Velocity.Y).Length();
        }
        float before = After(8f, 15f), now = After(16f, 30f);
        Assert.InRange(now / before, 1.9f, 2.1f);
    }

    static readonly SimConfig Cfg = EvadeTests.Config;

    [Fact]
    public void Firing_the_bolt_pistol_ends_a_melee_swing_at_once_and_frees_the_player()
    {
        PlayerState Swing(WeaponSlot slot)
        {
            var s = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = 1 }, Cfg);
            s = s with { Slot = slot };
            s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, InputButtons.Melee), Cfg, out _);
            s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, InputButtons.None), Cfg, out _);
            Assert.True(s.MeleeStep > 0);
            return s;
        }
        // The pistol in hand: the trigger press ends the swing in that tick (before RangedAt) and the shot leaves.
        var pistol = Swing(WeaponSlot.Pistol);
        pistol = Combat.StepPlayer(pistol, new PlayerInput(0, 0f, 0f, 0f, InputButtons.Fire), Cfg, out var e);
        Assert.Equal(0, pistol.MeleeStep);
        Assert.True(e.Fired);
        // Free for any new action on the next tick: a new swing.
        pistol = Combat.StepPlayer(pistol, new PlayerInput(0, 0f, 0f, 0f, InputButtons.Melee), Cfg, out _);
        Assert.Equal(1, pistol.MeleeStep);
        // A pistol that cannot fire (empty, or cooling down after a shot) does not cancel the swing: a dry press costs nothing.
        var dry = Swing(WeaponSlot.Pistol) with { Clip = 0 };
        dry = Combat.StepPlayer(dry, new PlayerInput(0, 0f, 0f, 0f, InputButtons.Fire), Cfg, out var e3);
        Assert.True(dry.MeleeStep > 0);
        Assert.False(e3.Fired);
        var cooling = Swing(WeaponSlot.Pistol) with { Cooldown = 1f };
        cooling = Combat.StepPlayer(cooling, new PlayerInput(0, 0f, 0f, 0f, InputButtons.Fire), Cfg, out var e4);
        Assert.True(cooling.MeleeStep > 0);
        Assert.False(e4.Fired);
        // The rifle in hand: the swing is not cut (Fire waits for RangedAt, as before).
        var rifle = Swing(WeaponSlot.Ranged);
        rifle = Combat.StepPlayer(rifle, new PlayerInput(0, 0f, 0f, 0f, InputButtons.Fire), Cfg, out var e2);
        Assert.True(rifle.MeleeStep > 0);
        Assert.False(e2.Fired);
    }
}
