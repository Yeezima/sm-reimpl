using SmClient;
using SmSim;

namespace SmNet.Tests;

/// <summary>
/// Controller feature (owner's request 2026-10-30; engine-free, client/Gamepad/GamepadRules.cs): the binding table, the stick curves, the D-pad slots and the device-switch rule.
/// Synthetic numbers; the look curves have the shapes of the install's default set (camera_input_tuning), written out here as plain numbers.
/// </summary>
public class GamepadTests
{
    static readonly (float, float)[] DefaultMapping = [(0f, 0f), (0.2f, 0f), (0.3f, 0.02f), (0.6f, 0.07f), (0.77f, 0.17f), (0.87f, 0.47f), (0.97f, 1f), (1f, 1f)];
    static readonly (float, float)[] DefaultDamping = [(0f, 0.12f), (0.04f, 0.2f), (0.05f, 0.25f), (0.2f, 0.5f), (0.5f, 0.6f), (0.9f, 0.8f), (1f, 1f)];

    // ---- the binding table ----

    [Fact]
    public void The_table_has_the_original_xbox_layout_from_the_owners_screen()
    {
        PadControl Of(string action) => GamepadRules.Bindings.Single(b => b.Action.StartsWith(action)).Control;
        Assert.Equal(PadControl.LeftTrigger, Of("Aim"));
        Assert.Equal(PadControl.RightTrigger, Of("Fire"));
        Assert.Equal(PadControl.LeftBumper, Of("Grenade"));
        Assert.Equal(PadControl.RightBumper, Of("Reload"));
        Assert.Equal(PadControl.X, Of("Strike"));
        Assert.Equal(PadControl.Y, Of("Stun"));
        Assert.Equal(PadControl.B, Of("Use"));
        Assert.Equal(PadControl.A, Of("Evade"));
        Assert.Equal(PadControl.LeftStick, Of("Move"));
        Assert.Equal(PadControl.RightStick, Of("Look"));
        Assert.Equal(PadControl.LeftStickClick, Of("Sprint"));
        Assert.Equal(PadControl.Start, Of("Game menu"));
        Assert.Equal(PadControl.Back, Of("Scoreboard"));
    }

    [Fact]
    public void Every_row_has_a_note_and_no_control_has_two_actions()
    {
        Assert.All(GamepadRules.Bindings, b => { Assert.False(string.IsNullOrWhiteSpace(b.Note)); Assert.False(string.IsNullOrWhiteSpace(b.Action)); });
        Assert.Empty(GamepadRules.Bindings.GroupBy(b => b.Control).Where(g => g.Count() > 1));
    }

    // ---- sticks ----

    [Fact]
    public void The_move_stick_has_a_dead_zone_and_keeps_its_direction_and_magnitude()
    {
        Assert.Equal((0f, 0f), GamepadRules.MoveStick(0.15f, 0.1f));
        var (x, y) = GamepadRules.MoveStick(0f, -1f);   // up on the stick is forward
        Assert.Equal(0f, x); Assert.Equal(1f, y);
        var (hx, hy) = GamepadRules.MoveStick(0f, -0.6f);
        Assert.InRange(hy, 0.4f, 0.6f); Assert.Equal(0f, hx);   // a half-pushed stick walks slower: the magnitude reaches the simulation
        var (dx, dy) = GamepadRules.MoveStick(1f, -1f);
        Assert.True(dx * dx + dy * dy <= 1.0001f && MathF.Abs(dx - dy) < 1e-4f);   // clamped to the unit circle, the diagonal kept
        Assert.Equal((0f, 0f), GamepadRules.MoveStick(float.NaN, float.PositiveInfinity));
    }

    [Fact]
    public void The_charge_needs_the_stick_forward_like_the_simulations_rule()
    {
        Assert.True(GamepadRules.ChargeForward(0f, -1f));
        Assert.False(GamepadRules.ChargeForward(0f, 0f));
        Assert.False(GamepadRules.ChargeForward(0f, 1f));       // back
        Assert.False(GamepadRules.ChargeForward(1f, 0f));       // sideways
        Assert.False(GamepadRules.ChargeForward(0f, -0.45f));   // a light push is below the rule's 0.7
    }

    [Fact]
    public void The_look_rate_follows_the_datas_mapping_speed_and_damping()
    {
        Assert.Equal(0f, GamepadRules.LookRate(0.15f, 5f, 300f, DefaultMapping, DefaultDamping));
        Assert.Equal(0f, GamepadRules.LookRate(0.2f, 5f, 300f, DefaultMapping, DefaultDamping));
        Assert.Equal(300f, GamepadRules.LookRate(1f, 5f, 300f, DefaultMapping, DefaultDamping));
        Assert.Equal(-200f, GamepadRules.LookRate(-1f, 5f, 200f, DefaultMapping, DefaultDamping));
        Assert.Equal(0.07f * 300f, GamepadRules.LookRate(0.6f, 5f, 300f, DefaultMapping, DefaultDamping), 3);   // slow in the middle of the curve
        Assert.Equal(0.12f * 300f, GamepadRules.LookRate(1f, 0f, 300f, DefaultMapping, DefaultDamping), 3);     // a fresh push is held back by the damping
        Assert.True(GamepadRules.LookRate(1f, 0.5f, 300f, DefaultMapping, DefaultDamping) < GamepadRules.LookRate(1f, 1f, 300f, DefaultMapping, DefaultDamping));
        Assert.Equal(150f, GamepadRules.LookRate(0.5f, 0f, 300f, null, null));   // no tables: the identity at the given speed
        Assert.Equal(0f, GamepadRules.LookRate(1f, 1f, float.NaN, null, null));
    }

    [Fact]
    public void Curves_are_piecewise_linear_and_hold_their_ends()
    {
        Assert.Equal(0.01f, GamepadRules.Curve(DefaultMapping, 0.25f), 3);   // halfway between (0.2, 0) and (0.3, 0.02)
        Assert.Equal(1f, GamepadRules.Curve(DefaultMapping, 2f));
        Assert.Equal(0f, GamepadRules.Curve(DefaultMapping, -1f));
    }

    // ---- triggers, D-pad, radial ----

    [Fact]
    public void Triggers_press_at_half_and_release_below_a_third_with_hysteresis()
    {
        Assert.False(GamepadRules.Trigger(false, 0.4f));
        Assert.True(GamepadRules.Trigger(false, 0.5f));
        Assert.True(GamepadRules.Trigger(true, 0.4f));    // held through the band
        Assert.False(GamepadRules.Trigger(true, 0.3f));
    }

    [Fact]
    public void The_dpad_maps_to_the_weapon_slots_and_the_radial_stick_picks_the_same_directions()
    {
        Assert.Equal(InputButtons.SlotPistol, GamepadRules.WeaponSwap(PadControl.DpadUp));
        Assert.Equal(InputButtons.SlotSecond, GamepadRules.WeaponSwap(PadControl.DpadDown));   // the south slot: Weapon Versatility's second primary
        Assert.Equal(InputButtons.SlotPrimary, GamepadRules.WeaponSwap(PadControl.DpadRight));
        Assert.Equal(InputButtons.None, GamepadRules.WeaponSwap(PadControl.DpadLeft));   // nothing for now
        Assert.Equal(InputButtons.None, GamepadRules.WeaponSwap(PadControl.A));
        Assert.Null(GamepadRules.RadialSlot(0.2f, 0.2f));
        Assert.Equal(PadControl.DpadUp, GamepadRules.RadialSlot(0.1f, -0.9f));
        Assert.Equal(PadControl.DpadDown, GamepadRules.RadialSlot(-0.1f, 0.9f));
        Assert.Equal(PadControl.DpadRight, GamepadRules.RadialSlot(0.9f, 0.3f));
        Assert.Equal(PadControl.DpadLeft, GamepadRules.RadialSlot(-0.9f, -0.3f));
    }

    // ---- the device switch ----

    [Fact]
    public void A_connecting_controller_becomes_the_scheme_and_a_disconnect_gives_it_back()
    {
        var t = new SchemeTracker();
        var changes = new List<InputScheme>();
        t.Changed += changes.Add;
        Assert.Equal(InputScheme.KeyboardMouse, t.Scheme);
        t.Connect(0, true, 1);
        Assert.Equal(InputScheme.Gamepad, t.Scheme);
        t.Connect(0, false, 0);
        Assert.Equal(InputScheme.KeyboardMouse, t.Scheme);
        Assert.Equal([InputScheme.Gamepad, InputScheme.KeyboardMouse], changes);
    }

    [Fact]
    public void The_last_device_used_wins_and_drift_or_a_bumped_mouse_does_not_swap()
    {
        var t = new SchemeTracker();
        t.Connect(0, true, 1);
        t.KeyOrClick();
        Assert.Equal(InputScheme.KeyboardMouse, t.Scheme);
        t.PadAxis(0, 0.2f);   // stick drift
        Assert.Equal(InputScheme.KeyboardMouse, t.Scheme);
        t.PadAxis(0, 0.8f);
        Assert.Equal(InputScheme.Gamepad, t.Scheme);
        t.MouseMoved(1f);     // a bumped desk
        Assert.Equal(InputScheme.Gamepad, t.Scheme);
        for (int i = 0; i < 6; i++) t.MouseMoved(1f);   // a small steady movement adds up
        Assert.Equal(InputScheme.KeyboardMouse, t.Scheme);
        t.PadButton(0);
        Assert.Equal(InputScheme.Gamepad, t.Scheme);
        t.MouseMoved(40f);
        Assert.Equal(InputScheme.KeyboardMouse, t.Scheme);
    }

    [Fact]
    public void With_two_controllers_the_scheme_survives_the_loss_of_one_it_was_not_using()
    {
        var t = new SchemeTracker();
        t.Connect(0, true, 1);
        t.Connect(1, true, 2);
        Assert.Equal(1, t.Device);
        t.Connect(0, false, 1);   // the idle one left
        Assert.Equal(InputScheme.Gamepad, t.Scheme);
        t.Connect(1, false, 0);
        Assert.Equal(InputScheme.KeyboardMouse, t.Scheme);
    }
}
