using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Musou.Campaign;

/// <summary>
/// Musou Dreadnought D3 and D4 (docs/musou-dreadnought-design.md section 3.3): his class carries the data's assault cannon (the ranged primary: held fire, 30 a round, 0.06 s apart, a 140-round clip, a 2.5 s reload) and the
/// melta (the "pistol": Switch toggles; 200 a shot, 0.33 s apart, a clip of one that recharges). The weapon values are the data's (the specs below use the install's numbers; the install-gated tests of <c>DreadnoughtDataTests</c> pin them).
/// </summary>
public class DreadnoughtGunsTests
{
    static readonly WeaponSpec Cannon = new(DreadnoughtRules.CannonWargear, WeaponSlot.Ranged, TriggerKind.FullAuto, 30f, 0.06f, 140, 2_000_000, 2.5f, 250f, 250f, 250f, 1f, 1f, 1f, 1f, 1f) { AlwaysReady = true };
    static readonly WeaponSpec Melta = new(DreadnoughtRules.MeltaWargear, WeaponSlot.Pistol, TriggerKind.SemiAuto, 200f, 0.33f, 1, 12, 1.33f, 250f, 250f, 250f, 1f, 1f, 1f, 1f, 1f) { AlwaysReady = true, Recharge = new RechargeSpec(0.33f, 1f) };

    static readonly SimConfig Config = MusouLoadout.Prepare(TfwRig.Config with { Weapons = [.. TfwRig.Config.Weapons, Cannon, Melta] });
    static int Dread => Config.Classes.ToList().FindIndex(k => k.Name == DreadnoughtRules.ClassName);
    const int Human = TfwRig.Human;

    static World Landed()
    {
        var w = TfwRig.War(null, Config);
        Assert.NotNull(w.SetNextLoadout(Human, LoadoutChoice.Default with { Class = Dread }));
        TfwRig.Command(w, TfwCommand.ChangeClass);
        TfwRig.Steps(w, 3);
        TfwRig.Deploy(w);
        TfwRig.Seconds(w, DreadnoughtRules.EntranceSeconds + 0.3f);   // the entrance clip's lock is over
        return w;
    }

    static PlayerState Me(World w) => w.Players[Human];
    static PlayerInput Held(InputButtons b) => new(0, 0f, 0f, 0f, b);

    [Fact]
    public void The_class_carries_the_cannon_as_primary_and_the_melta_as_pistol_and_arms_with_a_full_clip()
    {
        var c = Config.Classes[Dread];
        Assert.Equal(Config.Weapons.ToList().FindIndex(x => x.Name == DreadnoughtRules.CannonWargear), c.PrimaryChoices.Single());
        Assert.Equal(Config.Weapons.ToList().FindIndex(x => x.Name == DreadnoughtRules.MeltaWargear), c.PistolChoices.Single());
        Assert.True(MusouLoadout.Prepared(Config));   // his Primary stays -1: the strip rule still sees every class unarmed
        var w = Landed();
        var p = Me(w);
        Assert.Equal(WeaponSlot.Ranged, p.Slot);
        Assert.Equal(DreadnoughtRules.CannonWargear, Loadouts.InHand(w.Config, p)!.Name);
        Assert.Equal(140, p.Clip);
        Assert.Equal(DreadnoughtRules.MeltaWargear, Loadouts.Weapon(w.Config, p, WeaponSlot.Pistol)!.Name);
    }

    [Fact]
    public void Held_fire_spends_no_round_since_the_2026_11_15_buff_the_cannon_is_endless_and_its_limit_is_heat()
    {
        // The cannon's reload and clip belong to the data's gun (140 rounds, 2.5 s reload): the buff made it the autocannon's, which never spends a round (DreadnoughtBuffTests: the heat meter and the rate).
        var w = Landed();
        int clip0 = Me(w).Clip;
        Assert.Equal(140, clip0);
        for (int k = 0; k < 30 * 10; k++) w.Step(_ => Held(InputButtons.Fire));
        Assert.Equal(clip0, Me(w).Clip);
    }

    [Fact]
    public void Switch_toggles_to_the_melta_and_back_and_the_melta_holds_one_round()
    {
        var w = Landed();
        w.Step(_ => new PlayerInput(0, 0f, 0f, 0f, InputButtons.Switch));
        TfwRig.Seconds(w, 1.5f);
        var p = Me(w);
        Assert.Equal(WeaponSlot.Pistol, p.Slot);
        Assert.Equal(DreadnoughtRules.MeltaWargear, Loadouts.InHand(w.Config, p)!.Name);
        Assert.Equal(1, p.Clip);
        w.Step(_ => new PlayerInput(0, 0f, 0f, 0f, InputButtons.Switch));
        TfwRig.Seconds(w, 1.5f);
        Assert.Equal(WeaponSlot.Ranged, Me(w).Slot);
    }
}
