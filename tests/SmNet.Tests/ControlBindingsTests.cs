using SmClient;
using SmSim;

namespace SmNet.Tests;

/// <summary>
/// ui/keybinds (the owner's request 2026-10-31; engine-free, client/Controls/ControlBindings.cs): the defaults are today's bindings, conflicts are resolved by swap or replace, the file
/// loads and saves, and a remapped action drives the same input bits the old hard-coded key did.
/// </summary>
public class ControlBindingsTests
{
    static BindingSet Fresh() => BindingSet.Defaults();

    static (float X, float Y, InputButtons Buttons) Sample(BindingSet set, BindDevice device, IEnumerable<string> down, bool captured = true)
    {
        var pressed = down.ToHashSet();
        var s = BindingSet.Sample(a => set.Inputs(device, a).Any(pressed.Contains), captured, includeMove: device == BindDevice.Keyboard);
        return (s.X, s.Y, s.Buttons);
    }

    // ---- defaults ----

    [Fact]
    public void The_keyboard_defaults_are_the_documented_keys()
    {
        var s = Fresh();
        string K(GameAction a, int slot = 0) => s.Get(BindDevice.Keyboard, a, slot) ?? "";
        Assert.Equal(new[] { "Key:W", "Key:S", "Key:A", "Key:D" }, new[] { K(GameAction.MoveForward), K(GameAction.MoveBack), K(GameAction.MoveLeft), K(GameAction.MoveRight) });
        Assert.Equal("Key:Shift", K(GameAction.Sprint));
        Assert.Equal("Mouse:Left", K(GameAction.Fire));
        Assert.Equal("Key:Ctrl", K(GameAction.Aim));
        Assert.Equal("Mouse:Right", K(GameAction.Melee)); Assert.Equal("Key:V", K(GameAction.Melee, 1));
        Assert.Equal("Key:F", K(GameAction.StunStrike));
        Assert.Equal("Key:Space", K(GameAction.Jump));
        Assert.Equal("Key:G", K(GameAction.Grenade)); Assert.Equal("Key:R", K(GameAction.Reload)); Assert.Equal("Key:E", K(GameAction.Use));
        Assert.Equal(new[] { "Key:Key1", "Key:Key2", "Key:Key3" }, new[] { K(GameAction.Weapon1), K(GameAction.Weapon2), K(GameAction.Weapon3) });
        Assert.Equal("Key:X", K(GameAction.NextWeapon)); Assert.Equal("Mouse:Wheel", K(GameAction.NextWeapon, 1));
        Assert.Equal("Key:Tab", K(GameAction.Scoreboard));
        Assert.Equal("Key:T", K(GameAction.Shout));
    }

    [Fact]
    public void The_controller_defaults_are_the_sm1_layout_of_the_binding_table()
    {
        var s = Fresh();
        string P(GameAction a) => s.Get(BindDevice.Controller, a, 0) ?? "";
        Assert.Equal("Pad:LT", P(GameAction.Aim)); Assert.Equal("Pad:RT", P(GameAction.Fire));
        Assert.Equal("Pad:LeftShoulder", P(GameAction.Grenade)); Assert.Equal("Pad:RightShoulder", P(GameAction.Reload));
        Assert.Equal("Pad:X", P(GameAction.Melee)); Assert.Equal("Pad:Y", P(GameAction.StunStrike));
        Assert.Equal("Pad:B", P(GameAction.Use)); Assert.Equal("Pad:A", P(GameAction.Jump));
        Assert.Equal("Pad:LeftStick", P(GameAction.Sprint)); Assert.Equal("Pad:RightStick", P(GameAction.ZoomCycle)); Assert.Equal("Pad:Back", P(GameAction.Scoreboard));
        // The weapon-swap buttons are the table's D-pad rows (GamepadRules.WeaponSwap).
        Assert.Equal(InputButtons.SlotPistol, GamepadRules.WeaponSwap(PadControl.DpadUp)); Assert.Equal("Pad:DpadUp", P(GameAction.Weapon1));
        Assert.Equal(InputButtons.SlotPrimary, GamepadRules.WeaponSwap(PadControl.DpadRight)); Assert.Equal("Pad:DpadRight", P(GameAction.Weapon2));
        Assert.Equal(InputButtons.SlotSecond, GamepadRules.WeaponSwap(PadControl.DpadDown)); Assert.Equal("Pad:DpadDown", P(GameAction.Weapon3));
    }

    [Theory]
    [InlineData(BindDevice.Keyboard)]
    [InlineData(BindDevice.Controller)]
    public void No_default_input_is_used_twice_and_none_is_reserved(BindDevice device)
    {
        var s = Fresh();
        var all = ControlActions.For(device, true).SelectMany(a => s.Inputs(device, a.Action)).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
        Assert.All(all, i => Assert.Null(BindingSet.Reserved(device, i)));
        Assert.True(s.IsDefault(device));
    }

    [Fact]
    public void Every_action_with_a_button_has_its_own_bit()
    {
        var bits = ControlActions.All.Where(a => a.Buttons != InputButtons.None).Select(a => a.Buttons).ToList();
        Assert.Equal(bits.Count, bits.Distinct().Count());
        Assert.All(bits, b => Assert.Equal(1, System.Numerics.BitOperations.PopCount((uint)b)));
    }

    // ---- conflicts ----

    [Fact]
    public void A_free_input_binds_and_clear_empties_the_slot()
    {
        var s = Fresh();
        Assert.True(s.Assign(BindDevice.Keyboard, GameAction.Reload, 1, "Key:Q"));
        Assert.Equal("Key:Q", s.Get(BindDevice.Keyboard, GameAction.Reload, 1));
        Assert.False(s.IsDefault(BindDevice.Keyboard));
        s.Clear(BindDevice.Keyboard, GameAction.Reload, 1);
        Assert.Null(s.Get(BindDevice.Keyboard, GameAction.Reload, 1));
        Assert.True(s.IsDefault(BindDevice.Keyboard));
    }

    [Fact]
    public void A_used_input_is_reported_and_replace_unbinds_the_other_action()
    {
        var s = Fresh();
        var conflict = s.FindConflict(BindDevice.Keyboard, GameAction.Reload, 0, "Key:G");
        Assert.Equal((GameAction.Grenade, 0), conflict);
        Assert.True(s.Assign(BindDevice.Keyboard, GameAction.Reload, 0, "Key:G", ConflictChoice.Replace));
        Assert.Equal("Key:G", s.Get(BindDevice.Keyboard, GameAction.Reload, 0));
        Assert.Null(s.Get(BindDevice.Keyboard, GameAction.Grenade, 0));
        Assert.Single(s.ActionsFor(BindDevice.Keyboard, "Key:G"));
    }

    [Fact]
    public void Swap_gives_the_other_action_the_old_input()
    {
        var s = Fresh();
        Assert.True(s.Assign(BindDevice.Keyboard, GameAction.Reload, 0, "Key:G", ConflictChoice.Swap));
        Assert.Equal("Key:G", s.Get(BindDevice.Keyboard, GameAction.Reload, 0));
        Assert.Equal("Key:R", s.Get(BindDevice.Keyboard, GameAction.Grenade, 0));
        Assert.Single(s.ActionsFor(BindDevice.Keyboard, "Key:R"));
        Assert.Single(s.ActionsFor(BindDevice.Keyboard, "Key:G"));
    }

    [Fact]
    public void The_two_slots_of_one_action_never_hold_the_same_input()
    {
        var s = Fresh();
        // Melee: slot 0 right mouse, slot 1 V. Binding V to slot 0 swaps the slots.
        Assert.True(s.Assign(BindDevice.Keyboard, GameAction.Melee, 0, "Key:V", ConflictChoice.Swap));
        Assert.Equal("Key:V", s.Get(BindDevice.Keyboard, GameAction.Melee, 0));
        Assert.Equal("Mouse:Right", s.Get(BindDevice.Keyboard, GameAction.Melee, 1));
        Assert.True(s.Assign(BindDevice.Keyboard, GameAction.Melee, 1, "Key:V", ConflictChoice.Replace));
        Assert.Null(s.Get(BindDevice.Keyboard, GameAction.Melee, 0));
    }

    [Fact]
    public void Swap_with_an_empty_slot_leaves_the_other_action_unbound()
    {
        var s = Fresh();
        Assert.True(s.Assign(BindDevice.Controller, GameAction.NextWeapon, 0, "Pad:A", ConflictChoice.Swap));   // NextWeapon had nothing on the pad
        Assert.Null(s.Get(BindDevice.Controller, GameAction.Jump, 0));
        Assert.Equal("Pad:A", s.Get(BindDevice.Controller, GameAction.NextWeapon, 0));
    }

    [Theory]
    [InlineData(BindDevice.Keyboard, "Key:Escape")]
    [InlineData(BindDevice.Keyboard, "Key:F1")]
    [InlineData(BindDevice.Keyboard, "Pad:A")]
    [InlineData(BindDevice.Controller, "Pad:Start")]
    [InlineData(BindDevice.Controller, "Key:W")]
    [InlineData(BindDevice.Keyboard, "")]
    [InlineData(BindDevice.Keyboard, "Key:")]
    [InlineData(BindDevice.Keyboard, "Key:W;")]
    public void Reserved_and_foreign_inputs_are_refused(BindDevice device, string input)
    {
        var s = Fresh();
        Assert.NotNull(BindingSet.Reserved(device, input));
        Assert.False(s.Assign(device, GameAction.Fire, 0, input));
        Assert.True(s.IsDefault(device));
    }

    [Fact]
    public void Slots_that_do_not_exist_are_refused()
    {
        var s = Fresh();
        Assert.False(s.Assign(BindDevice.Controller, GameAction.Fire, 1, "Pad:A"));
        Assert.False(s.Assign(BindDevice.Keyboard, GameAction.Fire, 2, "Key:Z"));
    }

    [Fact]
    public void Reset_restores_one_device_only()
    {
        var s = Fresh();
        s.Assign(BindDevice.Keyboard, GameAction.Reload, 0, "Key:Z");
        s.Assign(BindDevice.Controller, GameAction.Reload, 0, "Pad:A");
        s.ResetDevice(BindDevice.Keyboard);
        Assert.True(s.IsDefault(BindDevice.Keyboard));
        Assert.False(s.IsDefault(BindDevice.Controller));
        s.ResetDevice(BindDevice.Controller);
        Assert.True(s.IsDefault(BindDevice.Controller));
    }

    [Fact]
    public void The_two_devices_do_not_conflict_with_each_other()
    {
        var s = Fresh();
        Assert.True(s.Assign(BindDevice.Keyboard, GameAction.Fire, 1, "Key:Z"));
        Assert.Equal("Pad:RT", s.Get(BindDevice.Controller, GameAction.Fire, 0));
    }

    // ---- saving ----

    static string TempDir() => Path.Combine(Path.GetTempPath(), "sm-reimpl-tests", "controls-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void A_changed_set_saves_and_loads_back()
    {
        string dir = TempDir();
        try
        {
            var s = Fresh();
            s.Assign(BindDevice.Keyboard, GameAction.Reload, 0, "Key:Q");
            s.Clear(BindDevice.Keyboard, GameAction.Melee, 1);
            s.Assign(BindDevice.Controller, GameAction.Jump, 0, "Pad:RightShoulder", ConflictChoice.Swap);
            Assert.Null(s.Save(dir));
            Assert.True(File.Exists(Path.Combine(dir, BindingSet.FileName)));
            var back = BindingSet.Load(dir, out var notes);
            Assert.Empty(notes);
            Assert.Equal("Key:Q", back.Get(BindDevice.Keyboard, GameAction.Reload, 0));
            Assert.Null(back.Get(BindDevice.Keyboard, GameAction.Melee, 1));
            Assert.Equal("Mouse:Right", back.Get(BindDevice.Keyboard, GameAction.Melee, 0));
            Assert.Equal("Pad:RightShoulder", back.Get(BindDevice.Controller, GameAction.Jump, 0));
            Assert.Equal("Pad:A", back.Get(BindDevice.Controller, GameAction.Reload, 0));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_set_equal_to_the_defaults_leaves_no_file()
    {
        string dir = TempDir();
        try
        {
            var s = Fresh();
            s.Assign(BindDevice.Keyboard, GameAction.Reload, 0, "Key:Q");
            Assert.Null(s.Save(dir));
            Assert.True(File.Exists(Path.Combine(dir, BindingSet.FileName)));
            s.ResetDevice(BindDevice.Keyboard);
            Assert.Null(s.Save(dir));
            Assert.False(File.Exists(Path.Combine(dir, BindingSet.FileName)));
            Assert.True(BindingSet.Load(dir, out _).IsDefault(BindDevice.Keyboard));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_missing_or_unreadable_file_gives_the_defaults()
    {
        Assert.True(BindingSet.Load(TempDir(), out var none).IsDefault(BindDevice.Keyboard));
        Assert.Empty(none);
        var broken = BindingSet.FromJson("{ not json", out var notes);
        Assert.True(broken.IsDefault(BindDevice.Keyboard) && broken.IsDefault(BindDevice.Controller));
        Assert.Single(notes);
        Assert.True(BindingSet.FromJson("[1,2]", out _).IsDefault(BindDevice.Keyboard));
        Assert.True(BindingSet.FromJson("", out _).IsDefault(BindDevice.Keyboard));
    }

    [Fact]
    public void Bad_entries_are_dropped_and_doubles_are_kept_once()
    {
        const string json = """
            { "version": 1,
              "keyboard": { "Fire": ["Key:Z", null], "Reload": ["Key:Z", "Key:Escape"], "Nonsense": ["Key:Q"], "Jump": ["Pad:A"] },
              "controller": { "Fire": ["Key:W"], "Aim": ["Pad:Start"] } }
            """;
        var s = BindingSet.FromJson(json, out var notes);
        Assert.Equal("Key:Z", s.Get(BindDevice.Keyboard, GameAction.Fire, 0));
        Assert.Null(s.Get(BindDevice.Keyboard, GameAction.Reload, 0));   // Z is Fire's already
        Assert.Null(s.Get(BindDevice.Keyboard, GameAction.Reload, 1));   // Esc is reserved
        Assert.Null(s.Get(BindDevice.Keyboard, GameAction.Jump, 0));     // a pad input on the keyboard table
        Assert.Null(s.Get(BindDevice.Controller, GameAction.Fire, 0));
        Assert.Null(s.Get(BindDevice.Controller, GameAction.Aim, 0));
        Assert.NotEmpty(notes);
        // Whatever was read is a consistent table: no input twice, nothing reserved.
        foreach (var device in new[] { BindDevice.Keyboard, BindDevice.Controller })
        {
            var all = ControlActions.For(device, true).SelectMany(a => s.Inputs(device, a.Action)).ToList();
            Assert.Equal(all.Count, all.Distinct().Count());
        }
    }

    [Fact]
    public void Actions_missing_from_the_file_keep_their_defaults()
    {
        var s = BindingSet.FromJson("""{ "version": 1, "keyboard": { "Reload": ["Key:Q", null] } }""", out _);
        Assert.Equal("Key:Q", s.Get(BindDevice.Keyboard, GameAction.Reload, 0));
        Assert.Equal("Mouse:Left", s.Get(BindDevice.Keyboard, GameAction.Fire, 0));
        Assert.True(s.IsDefault(BindDevice.Controller));
    }

    // ---- a remapped action drives the same input bits ----

    [Fact]
    public void The_default_inputs_make_the_bits_the_old_keys_made()
    {
        var s = Fresh();
        Assert.Equal((0f, 1f, InputButtons.Sprint), Sample(s, BindDevice.Keyboard, ["Key:W", "Key:Shift"]));
        Assert.Equal((1f, 0f, InputButtons.Fire), Sample(s, BindDevice.Keyboard, ["Key:D", "Mouse:Left"]));
        Assert.Equal((-1f, -1f, InputButtons.None), Sample(s, BindDevice.Keyboard, ["Key:A", "Key:S"]));
        Assert.Equal(InputButtons.Melee, Sample(s, BindDevice.Keyboard, ["Mouse:Right"]).Buttons);
        Assert.Equal(InputButtons.Melee, Sample(s, BindDevice.Keyboard, ["Key:V"]).Buttons);   // the second binding is the same button
        Assert.Equal(InputButtons.MeleeHeavy, Sample(s, BindDevice.Keyboard, ["Key:F"]).Buttons);
        Assert.Equal(InputButtons.Aim | InputButtons.Jump | InputButtons.Reload | InputButtons.Grenade, Sample(s, BindDevice.Keyboard, ["Key:Ctrl", "Key:Space", "Key:R", "Key:G"]).Buttons);
        Assert.Equal(InputButtons.SlotPistol | InputButtons.SlotPrimary | InputButtons.SlotSecond, Sample(s, BindDevice.Keyboard, ["Key:Key1", "Key:Key2", "Key:Key3"]).Buttons);
        Assert.Equal(InputButtons.Switch, Sample(s, BindDevice.Keyboard, ["Key:X"]).Buttons);
    }

    [Fact]
    public void A_remapped_action_drives_the_same_bit_and_the_old_input_does_nothing()
    {
        var s = Fresh();
        s.Assign(BindDevice.Keyboard, GameAction.Fire, 0, "Key:Z");
        s.Assign(BindDevice.Keyboard, GameAction.Reload, 0, "Mouse:Left", ConflictChoice.Replace);
        Assert.Equal(InputButtons.Fire, Sample(s, BindDevice.Keyboard, ["Key:Z"]).Buttons);
        Assert.Equal(InputButtons.Reload, Sample(s, BindDevice.Keyboard, ["Mouse:Left"]).Buttons);
        Assert.Equal(InputButtons.None, Sample(s, BindDevice.Keyboard, ["Key:R"]).Buttons);
    }

    [Fact]
    public void Remapped_movement_gives_the_same_axes()
    {
        var s = Fresh();
        s.Assign(BindDevice.Keyboard, GameAction.MoveForward, 0, "Key:I");
        s.Assign(BindDevice.Keyboard, GameAction.MoveRight, 1, "Key:L");
        Assert.Equal((0f, 1f, InputButtons.None), Sample(s, BindDevice.Keyboard, ["Key:I"]));
        Assert.Equal((1f, 0f, InputButtons.None), Sample(s, BindDevice.Keyboard, ["Key:L"]));
        Assert.Equal((0f, 0f, InputButtons.None), Sample(s, BindDevice.Keyboard, ["Key:W"]));   // W is free now and does nothing
    }

    [Fact]
    public void The_charge_inputs_follow_the_bound_actions()
    {
        // The charge is sprint + forward + stun strike (the sim reads these bits); with all three remapped the same bits and the forward axis are sent.
        var s = Fresh();
        s.Assign(BindDevice.Keyboard, GameAction.Sprint, 0, "Key:Capslock");
        s.Assign(BindDevice.Keyboard, GameAction.MoveForward, 0, "Key:I");
        s.Assign(BindDevice.Keyboard, GameAction.StunStrike, 0, "Key:H");
        var sample = Sample(s, BindDevice.Keyboard, ["Key:Capslock", "Key:I", "Key:H"]);
        Assert.Equal(1f, sample.Y);
        Assert.Equal(InputButtons.Sprint | InputButtons.MeleeHeavy, sample.Buttons);
        // And the old keys no longer make a charge.
        var old = Sample(s, BindDevice.Keyboard, ["Key:Shift", "Key:W", "Key:F"]);
        Assert.Equal((0f, InputButtons.None), (old.Y, old.Buttons));
    }

    [Fact]
    public void Mouse_actions_need_the_mouse_captured_and_the_others_do_not()
    {
        var s = Fresh();
        var down = new[] { "Mouse:Left", "Key:Ctrl", "Key:F", "Key:G", "Key:X", "Mouse:Right", "Key:Shift", "Key:Space", "Key:R", "Key:Key2" };
        Assert.Equal(InputButtons.Sprint | InputButtons.Jump | InputButtons.Reload | InputButtons.SlotPrimary, Sample(s, BindDevice.Keyboard, down, captured: false).Buttons);
        Assert.Equal(InputButtons.Fire | InputButtons.Aim | InputButtons.Melee | InputButtons.MeleeHeavy | InputButtons.Grenade | InputButtons.Switch | InputButtons.Sprint | InputButtons.Jump | InputButtons.Reload | InputButtons.SlotPrimary,
            Sample(s, BindDevice.Keyboard, down, captured: true).Buttons);
    }

    [Fact]
    public void The_controller_samples_buttons_only_and_a_remapped_pad_button_drives_the_bit()
    {
        var s = Fresh();
        Assert.Equal((0f, 0f, InputButtons.Fire | InputButtons.Aim), Sample(s, BindDevice.Controller, ["Pad:RT", "Pad:LT"]));
        s.Assign(BindDevice.Controller, GameAction.Jump, 0, "Pad:LeftShoulder", ConflictChoice.Swap);
        Assert.Equal(InputButtons.Jump, Sample(s, BindDevice.Controller, ["Pad:LeftShoulder"]).Buttons);
        Assert.Equal(InputButtons.Grenade, Sample(s, BindDevice.Controller, ["Pad:A"]).Buttons);   // the swap gave A to the grenade
    }

    // ---- display ----

    [Theory]
    [InlineData("Key:W", "W")]
    [InlineData("Key:Key1", "1")]
    [InlineData("Key:Kp1", "Num 1")]
    [InlineData("Key:Capslock", "Caps Lock")]
    [InlineData("Mouse:Left", "Left mouse")]
    [InlineData("Mouse:Wheel", "Mouse wheel")]
    [InlineData("Pad:LeftShoulder", "LB")]
    [InlineData("Pad:RT", "RT")]
    [InlineData("Pad:DpadUp", "D-pad up")]
    public void Names_read_well(string input, string shown) => Assert.Equal(shown, BindingSet.Display(input));

    [Fact]
    public void An_empty_binding_shows_a_dash() => Assert.Equal("-", BindingSet.Display(null));
}
