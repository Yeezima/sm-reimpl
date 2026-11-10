using SmContent;
using SmContent.Extras.Musou.Arsenal;
using SmSim;
using SmSim.Extras.Musou.Arsenal;

namespace SmFormats.Tests;

/// <summary>
/// W1, the Musou autocannon's content side (docs/new-weapons-design.md section 4 and the customizer's config, section 11): the twin is appended to the Space Marine Devastator's primaries only, a Join
/// resolves its name to that index in Musou's configuration (the data's configuration has no such name, which is why the customizer needs Musou's own config), and the data's configuration, the
/// faithful profile's and the Orks' never gain it.
/// </summary>
public class AutocannonContentTests
{
    static WeaponSpec Gun(string name, WeaponSlot slot = WeaponSlot.Ranged) => new(name, slot, TriggerKind.FullAuto, 35f, 0.2f, 100, 0, 1f, 100f, 40f, 60f, 0.8f, 1f, 1f, 1f, 1f);

    static SimConfig Data() => new()
    {
        Weapons = [Gun("sm_mp_exotic_heavy_bolter"), Gun("sm_mp_exotic_las_cannon"), Gun("csm_mp_exotic_heavy_bolter"), Gun("sm_mp_ranged_bolter")],
        Classes =
        [
            new ClassLoadout("sm - devastator", 0, -1, []) { Side = Team.SpaceMarines, Role = "heavy", PrimaryChoices = [0, 1] },
            new ClassLoadout("csm - havoc", 2, -1, []) { Side = Team.Chaos, Role = "heavy", PrimaryChoices = [2] },
            new ClassLoadout("sm - tactical", 3, -1, []) { Side = Team.SpaceMarines, Role = "tactical", PrimaryChoices = [3] },
        ],
    };

    static readonly WeaponSpec Source = Gun("csm_mp_exotic_auto_cannon") with { Damage = 50f, ShotCooldown = 0.4f, Trigger = TriggerKind.SemiAuto, BurstLength = 2, BurstInterval = 0.2f };

    [Fact]
    public void A_join_names_the_autocannon_by_its_index_in_Musous_configuration_and_not_at_all_in_the_datas()
    {
        var data = Data();
        var musou = Arsenal.WithAutocannon(data with { Extras = ContentExtras.Musou }, Source, []);
        int devastator = LoadoutIndices.Class(musou, "sm - devastator");
        Assert.Equal(2, LoadoutIndices.Choice(musou, devastator, "primary", "mp_exotic_auto_cannon"));   // the stored (side-neutral) name of the custom class
        Assert.Equal(2, LoadoutIndices.Choice(musou, devastator, "primary", "sm_mp_exotic_auto_cannon"));
        Assert.Equal(0, LoadoutIndices.Choice(musou, devastator, "primary", "mp_exotic_heavy_bolter"));    // no existing index moved
        Assert.Equal(1, LoadoutIndices.Choice(musou, devastator, "primary", "mp_exotic_las_cannon"));
        // The data's configuration (what the customizer used for every page before): the name is unknown, the Join would send the default (-1).
        Assert.Equal(-1, LoadoutIndices.Choice(data, LoadoutIndices.Class(data, "sm - devastator"), "primary", "mp_exotic_auto_cannon"));
        // Not the Chaos Havoc's, not the Tactical's.
        Assert.Equal(-1, LoadoutIndices.Choice(musou, LoadoutIndices.Class(musou, "csm - havoc"), "primary", "mp_exotic_auto_cannon"));
        Assert.Equal(-1, LoadoutIndices.Choice(musou, LoadoutIndices.Class(musou, "sm - tactical"), "primary", "mp_exotic_auto_cannon"));
    }

    [Fact]
    public void The_autocannons_sounds_are_the_Stalker_Bolters_and_its_effects_stay_the_twins()
    {
        // The owner (2026-11-10): "make the autocannon use stalker bolter sounds". Sounds: the Stalker's files (the hit confirm rule keys on their names); effects, reticle and ammo box: the twin's (W0b).
        Assert.Equal(["sm_mp_ranged_sniper_rifle", "csm_mp_ranged_sniper_rifle"], Arsenal.SoundSources(ArsenalRules.Autocannon));
        Assert.All(Arsenal.SoundSources(ArsenalRules.Autocannon)!, f => Assert.True(StalkerHitConfirm.Is(f)));
        Assert.Equal([Arsenal.AutocannonTwinFile, Arsenal.HeavyBolterFile], Arsenal.PresentationSources(ArsenalRules.Autocannon));
        Assert.Null(Arsenal.SoundSources(ArsenalRules.LightningClaw));   // the others' sounds are their effects' sources
        Assert.Null(Arsenal.SoundSources("sm_mp_exotic_heavy_bolter"));
    }

    [Fact]
    public void Nothing_but_the_Space_Marine_Devastators_list_and_the_weapon_list_change()
    {
        var data = Data();
        var musou = Arsenal.WithAutocannon(data with { Extras = ContentExtras.Musou }, Source, []);
        Assert.Equal(data.Weapons.Count + 1, musou.Weapons.Count);
        Assert.Equal(data.Weapons, musou.Weapons.Take(data.Weapons.Count));
        Assert.Equal(ArsenalRules.Autocannon, musou.Weapons[^1].Name);
        Assert.Equal([0, 1, data.Weapons.Count], musou.Classes[0].PrimaryChoices);
        Assert.Equal(data.Classes[1].PrimaryChoices, musou.Classes[1].PrimaryChoices);
        Assert.Equal(data.Classes[2].PrimaryChoices, musou.Classes[2].PrimaryChoices);
    }
}
