using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Musou.Campaign;

/// <summary>
/// Musou Dreadnought D6 (docs/musou-dreadnought-design.md section 3.3): the sprint is limited to 6 s then a 1 s gap (the data's), by one rule the server and the client both apply; the buff is the marine stim's mechanism with his
/// numbers (10 s of the stim, Musou's stim scale on top; regeneration doubled while it runs) and refills as Musou's stims do.
/// </summary>
public class DreadnoughtAbilityTests
{
    const float Dt = 1f / 30f;

    [Fact]
    public void Sprint_is_dropped_after_6_seconds_for_1_second_and_let_go_resets_the_run()
    {
        var gate = new DreadnoughtSprintGate();
        var held = new PlayerInput(0, 0f, 1f, 0f, InputButtons.Sprint);
        int sprinting = 0;
        for (int k = 0; k < 30 * 6; k++) { var i = held; gate.Apply(ref i, Dt); if ((i.Buttons & InputButtons.Sprint) != 0) sprinting++; }
        Assert.InRange(sprinting, 30 * 6 - 1, 30 * 6);            // the first 6 s all sprint
        var after = held; gate.Apply(ref after, Dt);
        Assert.Equal(InputButtons.None, after.Buttons & InputButtons.Sprint);   // then the gap
        int gap = 1;
        while (true) { var i = held; gate.Apply(ref i, Dt); if ((i.Buttons & InputButtons.Sprint) != 0) break; gap++; Assert.True(gap < 60); }
        Assert.InRange(gap * Dt, 0.9f, 1.15f);                    // about a second
        // letting go before the limit starts a new run
        gate.Reset();
        for (int k = 0; k < 30 * 5; k++) { var i = held; gate.Apply(ref i, Dt); }
        var off = new PlayerInput(0, 0f, 1f, 0f, InputButtons.None); gate.Apply(ref off, Dt);
        Assert.Equal(0f, gate.Held);
    }

    static readonly WeaponSpec Cannon = new(DreadnoughtRules.CannonWargear, WeaponSlot.Ranged, TriggerKind.FullAuto, 30f, 0.06f, 140, 2_000_000, 2.5f, 250f, 250f, 250f, 1f, 1f, 1f, 1f, 1f) { AlwaysReady = true };
    static readonly ThrownSpec Stims = new("combat_stims", 3, 3, 1f, 0f, ThrownSpec.NoProjectile) { Stim = new StimSpec(5f, 0.2f, 0.6f) };
    static readonly SimConfig Config = MusouLoadout.Prepare(TfwRig.Config with { Weapons = [.. TfwRig.Config.Weapons, Cannon], Thrown = [Stims] });
    static int Dread => Config.Classes.ToList().FindIndex(k => k.Name == DreadnoughtRules.ClassName);
    const int Human = TfwRig.Human;

    static World Landed()
    {
        var w = TfwRig.War(null, Config);
        Assert.NotNull(w.SetNextLoadout(Human, LoadoutChoice.Default with { Class = Dread }));
        TfwRig.Command(w, TfwCommand.ChangeClass);
        TfwRig.Steps(w, 3);
        TfwRig.Deploy(w);
        TfwRig.Seconds(w, DreadnoughtRules.EntranceSeconds + 0.3f);
        return w;
    }

    static PlayerState Me(World w) => w.Players[Human];

    [Fact]
    public void His_equipment_is_his_own_buff_item_and_the_marines_stim_stays_as_it_was()
    {
        var c = Config.Classes[Dread];
        int item = Assert.Single(c.EquipmentChoices);
        Assert.Equal(DreadnoughtRules.BuffName, Config.Thrown[item].Name);
        Assert.Equal((DreadnoughtRules.BuffLifetime, true, 1), (Config.Thrown[item].Stim!.Lifetime, Config.Thrown[item].Stim!.ReactionImmune, Config.Thrown[item].Count));
        Assert.Equal(("combat_stims", 5f), (Config.Thrown[0].Name, Config.Thrown[0].Stim!.Lifetime));
        Assert.DoesNotContain(item, Config.Classes[0].EquipmentChoices);
    }

    [Fact]
    public void The_buff_runs_for_the_stim_lifetime_under_musous_scale_and_comes_back_after_its_refill()
    {
        var w = Landed();
        Assert.Equal(1, Me(w).Grenades);
        w.Step(_ => new PlayerInput(0, 0f, 0f, 0f, InputButtons.Grenade));
        TfwRig.Seconds(w, 1.2f);
        Assert.Equal(0, Me(w).Grenades);
        float expected = DreadnoughtRules.BuffLifetime * MusouCombatRules.StimLifetimeScale;
        Assert.InRange(Me(w).StimLeft, expected - 3f, expected);
        TfwRig.Seconds(w, expected + 2f);
        Assert.Equal(0f, Me(w).StimLeft);
        TfwRig.Seconds(w, TfwSupplyRules.GrenadeRefillSeconds);
        Assert.Equal(1, Me(w).Grenades);   // refilled as Musou's stims are
    }
}
