using System.Numerics;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// cancel-editor (owner, 2026-11-17): the cancel table and the gate that plays the simulation by it. Synthetic classes and weapons only (the configs of the melee-cancel, hop and charge tests). The
/// default table must change nothing; an edited cell must change exactly what it says, for the class and weapon it names and no other.
/// </summary>
public class CancelGateTests(ITestOutputHelper output)
{
    static int Row(string id) => CancelLayout.RowIndex(id) is var i and >= 0 ? i : throw new ArgumentException(id);
    static int Col(string id) => CancelLayout.ColIndex(id) is var i and >= 0 ? i : throw new ArgumentException(id);

    /// <summary>The config with its own holder playing by <paramref name="table"/> (a copy made with <c>with</c> would share the static config's).</summary>
    internal static SimConfig Plays(SimConfig c, CancelData? table) => c with { Cancel = CancelLive.Of(table ?? CancelData.Baseline) };

    internal static CancelData Edit(string row, string col, CancelCell cell, CancelScope? scope = null) =>
        CancelData.Baseline.With(scope ?? CancelScope.Any, Row(row), Col(col), cell);

    static PlayerState Step(PlayerState s, SimConfig c, InputButtons b, float my = 0f) =>
        Combat.StepPlayer(s, new PlayerInput(0, 0f, my, 0f, b), c, out _);

    // ---- the pieces ----

    [Fact]
    public void The_shipped_table_is_the_baseline_plus_the_four_exported_cells_and_the_baseline_makes_the_gate_do_nothing()
    {
        // the shipped default is the owner's 11-23 export: the baseline plus exactly 4 cells (CancelDefaultTests checks which); the baseline itself is what the code does
        Assert.Equal(CancelDefaultTests.ExportCells.Length, CancelData.Shipped.DiffAgainst(CancelData.Baseline).Count);
        Assert.Empty(CancelData.Shipped.DiffFromShipped());
        Assert.False(CancelData.Baseline.DiffersFromBaseline);
        Assert.False(CancelGate.Active(new SimConfig() with { Cancel = CancelLive.Of(CancelData.Baseline) }));
    }

    [Fact]
    public void A_config_copy_shares_its_holder_and_two_fresh_configs_are_still_equal()
    {
        var a = new SimConfig(); var b = new SimConfig();
        Assert.Equal(a, b);                       // the holder never makes two configs differ
        Assert.Same(a.Cancel, (a with { }).Cancel);
        Assert.NotSame(a.Cancel, Plays(a, null).Cancel);
    }

    [Fact]
    public void An_edit_is_seen_by_the_gate_only_when_it_differs_from_the_code()
    {
        var c = Plays(MeleeCancelTests.Pack, Edit("jp_hop", "light", new(CancelState.Yes)));   // the same as the code does: yes
        Assert.False(CancelGate.Active(c));
        c = Plays(MeleeCancelTests.Pack, Edit("jp_hop", "light", new(CancelState.No)));
        Assert.True(CancelGate.Active(c));
        Assert.False(CancelGate.Active(c with { Extras = ContentExtras.Musou }));              // never in Musou Mode
        Assert.True(CancelGate.Active(c with { Extras = ContentExtras.All }));             // Classic with the extended set (the default profile): the owner's edits apply
        Assert.True(CancelGate.Active(c with { Extras = ContentExtras.Orks | ContentExtras.Gore }));
        Assert.True(CancelGate.Active(new SimConfig() with { Extras = ContentExtras.All }));    // the shipped default (5 cells off the baseline) plays under extended content too
        Assert.False(CancelGate.Active(new SimConfig() with { Extras = ContentExtras.Musou }));
    }

    static PlayerState Hopping(SimConfig c, int ticks = 3)
    {
        var s = Step(MeleeCancelTests.Fresh(c), c, InputButtons.Jump);
        s = Step(s, c, InputButtons.None);
        Assert.True(Evade.Evading(s));
        for (int k = 0; k < ticks; k++) s = Step(s, c, InputButtons.None);
        return s;
    }

    // ---- the cells ----

    [Fact]
    public void Default_table_a_melee_press_in_a_hop_cancels_it_as_before()
    {
        var c = Plays(MeleeCancelTests.Pack, null);
        var s = Step(Hopping(c), c, InputButtons.Melee);
        Assert.False(Evade.Evading(s));
        Assert.Equal(1, s.MeleeStep);
    }

    [Fact]
    public void No_ignores_the_press_and_the_hop_plays_on_and_a_held_key_does_not_fire_when_the_cell_is_edited_back()
    {
        var c = Plays(MeleeCancelTests.Pack, Edit("jp_hop", "light", new(CancelState.No)));
        var s = Step(Hopping(c), c, InputButtons.Melee);
        Assert.True(Evade.Evading(s));
        Assert.Equal(0, s.MeleeStep);
        Assert.True(s.MeleeHeld);                   // the key is down: it is not a fresh press next tick
        Assert.Equal(BufferedAction.None, s.Buffered);   // and nothing was kept in the buffer: "no" is not "queued"
        s = Step(s, c, InputButtons.Melee);
        Assert.True(Evade.Evading(s));
    }

    [Fact]
    public void Queued_drops_the_press_now_and_keeps_it_for_when_the_action_ends()
    {
        var c = Plays(MeleeCancelTests.Pack, Edit("jp_hop", "light", new(CancelState.Queued)));
        var s = Step(Hopping(c), c, InputButtons.Melee);
        Assert.True(Evade.Evading(s));
        Assert.Equal(0, s.MeleeStep);
        Assert.Equal(BufferedAction.Melee, s.Buffered);
        Assert.True(s.BufferTicks > 0);
    }

    [Fact]
    public void A_window_counts_only_inside_its_seconds()
    {
        var c = Plays(MeleeCancelTests.Pack, Edit("jp_hop", "light", new(CancelState.Window, 400, 700)));
        var early = Step(Hopping(c, 3), c, InputButtons.Melee);           // about 0.13 s in: before the window
        Assert.True(Evade.Evading(early));
        Assert.Equal(0, early.MeleeStep);
        var s = Hopping(c, 3);
        for (int k = 0; k < 10; k++) s = Step(s, c, InputButtons.None);   // about 0.47 s in: inside
        output.WriteLine($"hop time {Evade.Time(s, c):F3} s");
        var inside = Step(s, c, InputButtons.Melee);
        Assert.False(Evade.Evading(inside));
        Assert.Equal(1, inside.MeleeStep);
        for (int k = 0; k < 10; k++) s = Step(s, c, InputButtons.None);   // about 0.8 s in: after it
        var late = Step(s, c, InputButtons.Melee);
        Assert.True(Evade.Evading(late));
        Assert.Equal(0, late.MeleeStep);
    }

    [Fact]
    public void A_window_set_on_a_cell_that_was_no_opens_it_only_inside()
    {
        // Tactical roll with the charge: roll presses while a swing is under way are "no"; window 0-1000 ms makes a press in the swing end it
        var c = Plays(MeleeCancelTests.Heavy, Edit("melee_light", "roll", new(CancelState.Window, 0, 1000)));
        var s = Step(MeleeCancelTests.Fresh(c), c, InputButtons.Melee);
        Assert.True(s.MeleeStep > 0);
        s = Step(s, c, InputButtons.None);
        var rolled = Step(s, c, InputButtons.Jump);
        output.WriteLine($"after the roll press: melee step {rolled.MeleeStep}, evade ticks {rolled.EvadeTicks}");
        Assert.Equal(0, rolled.MeleeStep);
        Assert.True(Evade.Evading(rolled));
    }

    [Fact]
    public void Yes_on_a_cell_that_was_no_ends_the_action_and_starts_the_next()
    {
        // melee_light | roll is "window" in the data; the same press with the default table is held by the swing (checked first), the edit makes it cancel from the first tick
        var def = Plays(MeleeCancelTests.Heavy, null);
        var s0 = Step(Step(MeleeCancelTests.Fresh(def), def, InputButtons.Melee), def, InputButtons.None);
        var held = Step(s0, def, InputButtons.Jump);
        var c = Plays(MeleeCancelTests.Heavy, Edit("melee_light", "roll", new(CancelState.Yes)));
        var s = Step(Step(MeleeCancelTests.Fresh(c), c, InputButtons.Melee), c, InputButtons.None);
        var rolled = Step(s, c, InputButtons.Jump);
        Assert.Equal(0, rolled.MeleeStep);
        Assert.True(Evade.Evading(rolled));
        output.WriteLine($"default table: melee step {held.MeleeStep}, evading {Evade.Evading(held)}");
    }

    [Fact]
    public void A_back_press_cell_edited_to_no_stops_the_stick_from_cancelling_the_charge()
    {
        var c0 = Plays(MeleeCancelTests.Heavy, null);
        var charging0 = MeleeCancelTests.Charging(c0, 0, ticks: 3);
        var cancelled = Step(charging0, c0, InputButtons.None, my: -1f);
        Assert.NotEqual(RushPhase.Rush, cancelled.Rush);   // the default: S ends it
        var c = Plays(MeleeCancelTests.Heavy, Edit("charge", "back", new(CancelState.No)));
        var charging = MeleeCancelTests.Charging(c, 0, ticks: 3);
        var held = Step(charging, c, InputButtons.None, my: -1f);
        Assert.Equal(RushPhase.Rush, held.Rush);           // edited: S is ignored, the rush plays on
    }

    // ---- class and weapon scopes ----

    [Fact]
    public void A_class_override_changes_that_class_only_and_a_weapon_override_that_weapon_only()
    {
        var table = Edit("jp_hop", "light", new(CancelState.No), new CancelScope("tactical", ""));
        // the Assault (class 'assault') is untouched by an override for the tactical class
        var c = Plays(MeleeCancelTests.Pack, table);
        var s = Step(Hopping(c), c, InputButtons.Melee);
        Assert.Equal(1, s.MeleeStep);
        var assault = Loadouts.ClassOf(c, 0)!.Name;
        var byClass = Plays(MeleeCancelTests.Pack, Edit("jp_hop", "light", new(CancelState.No), new CancelScope(assault, "")));
        var blocked = Step(Hopping(byClass), byClass, InputButtons.Melee);
        Assert.Equal(0, blocked.MeleeStep);
        string held = Loadouts.Weapon(c, MeleeCancelTests.Fresh(c), MeleeCancelTests.Fresh(c).Slot)!.Name;
        var byWeapon = Plays(MeleeCancelTests.Pack, Edit("jp_hop", "light", new(CancelState.No), new CancelScope("", held)));
        Assert.Equal(0, Step(Hopping(byWeapon), byWeapon, InputButtons.Melee).MeleeStep);
        var otherWeapon = Plays(MeleeCancelTests.Pack, Edit("jp_hop", "light", new(CancelState.No), new CancelScope("", "no such gun")));
        Assert.Equal(1, Step(Hopping(otherWeapon), otherWeapon, InputButtons.Melee).MeleeStep);
    }

    [Fact]
    public void The_most_specific_override_wins()
    {
        var t = CancelData.Shipped
            .With(CancelScope.Any, Row("jp_hop"), Col("light"), new(CancelState.Queued))
            .With(new("assault", ""), Row("jp_hop"), Col("light"), new(CancelState.No))
            .With(new("", "rifle"), Row("jp_hop"), Col("light"), new(CancelState.Window, 100, 200))
            .With(new("assault", "rifle"), Row("jp_hop"), Col("light"), new(CancelState.Yes));
        int r = Row("jp_hop"), k = Col("light");
        Assert.Equal(CancelState.Yes, t.Resolve(r, k, "assault", "rifle").State);
        Assert.Equal(CancelState.Window, t.Resolve(r, k, "tactical", "rifle").State);   // the weapon beats the class
        Assert.Equal(CancelState.No, t.Resolve(r, k, "assault", "pistol").State);
        Assert.Equal(CancelState.Queued, t.Resolve(r, k, "tactical", "pistol").State);
        Assert.Equal(CancelState.Queued, t.Resolve(r, k, null, null).State);
    }

    // ---- being hit ----

    [Fact]
    public void A_hit_reacts_in_a_swing_by_default_and_not_when_the_table_says_no()
    {
        var def = Plays(MeleeCancelTests.Heavy, null);
        var s = Step(MeleeCancelTests.Fresh(def), def, InputButtons.Melee);
        Assert.True(s.MeleeStep > 0);
        bool natural = Reaction.CanReact(s, def);
        var c = Plays(MeleeCancelTests.Heavy, Edit("melee_light", "hit_react", new(CancelState.No)));
        Assert.False(Reaction.CanReact(s, c));
        output.WriteLine($"a hit reacts mid-swing by default: {natural}");
        Assert.True(natural);
    }

    [Fact]
    public void A_hit_stun_cell_edited_to_yes_lets_a_charge_be_stunned()
    {
        var def = Plays(MeleeCancelTests.Heavy, null);
        var charging = MeleeCancelTests.Charging(def, 0, ticks: 3);
        Assert.False(Reaction.CanReact(charging, def));   // the charge's hyper armour
        var c = Plays(MeleeCancelTests.Heavy, Edit("charge", "hit_react", new(CancelState.Yes)));
        Assert.True(Reaction.CanReact(charging, c));
    }

    // ---- the default changes nothing ----

    /// <summary>A seeded random play: every button and stick at random, in bursts, for <paramref name="ticks"/> ticks.</summary>
    static List<PlayerState> Play(SimConfig c, int cls, int seed, int ticks)
    {
        var rng = new Random(seed);
        var s = MeleeCancelTests.Fresh(c, cls);
        var list = new List<PlayerState>(ticks);
        var buttons = InputButtons.None; float mx = 0, my = 0;
        for (int t = 0; t < ticks; t++)
        {
            if (rng.Next(4) == 0)
            {
                buttons = InputButtons.None;
                foreach (var b in new[] { InputButtons.Jump, InputButtons.Sprint, InputButtons.Fire, InputButtons.Reload, InputButtons.Melee, InputButtons.Switch, InputButtons.Grenade, InputButtons.Aim, InputButtons.MeleeHeavy })
                    if (rng.Next(5) == 0) buttons |= b;
                mx = rng.Next(3) - 1; my = rng.Next(3) - 1;
            }
            s = Combat.StepPlayer(s, new PlayerInput((uint)t, mx, my, 0f, buttons), c, out _);
            if (!s.Alive) s = MeleeCancelTests.Fresh(c, cls);
            list.Add(s);
        }
        return list;
    }

    [Theory]
    [InlineData(0, 1)] [InlineData(0, 2)] [InlineData(1, 3)] [InlineData(1, 4)]
    public void The_default_table_plays_every_random_input_exactly_as_the_code_does_and_the_gate_never_acts(int cls, int seed)
    {
        foreach (var cfg in new[] { MeleeCancelTests.Pack, MeleeCancelTests.Heavy })
        {
            var plain = Play(Plays(cfg, null), cls, seed, 1500);
            // the gate switched on for a cell no action can reach (exec_doing | light is "not applicable"): it is active, finds nothing edited for the play, and the play is identical
            var gated = Plays(cfg, Edit("exec_doing", "light", new(CancelState.No)));
            Assert.True(CancelGate.Active(gated));
            var again = Play(gated, cls, seed, 1500);
            Assert.Equal(plain.Count, again.Count);
            for (int i = 0; i < plain.Count; i++) Assert.Equal(plain[i], again[i]);
            // and with the default table nothing is ever planned
            var rng = new Random(seed); var def = Plays(cfg, null);
            foreach (var st in plain) Assert.False(CancelGate.Acts(st, new PlayerInput(0, 0, 1, 0, (InputButtons)(rng.Next(0, 4096))), def));
        }
    }
}
