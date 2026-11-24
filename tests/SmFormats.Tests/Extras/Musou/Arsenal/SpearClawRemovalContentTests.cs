using SmContent;
using SmContent.Extras.Musou.Arsenal;
using SmSim;
using SmSim.Extras.Musou.Arsenal;

namespace SmFormats.Tests;

/// <summary>
/// remove-spear-claw (owner 2026-11-23: "turn off the power spear and power claw - remove them from the game for now"), install-free so CI sees it, on <see cref="PowerSpearContentTests"/>' fixture:
/// by default Musou's arsenal appends neither the lightning claw nor the power spear and its catalogue has no row for either; the Remote Detonators and the autocannon's row stay as before; the
/// restore switch (<see cref="ArsenalRules.SpearClawSwitch"/>, here its test scope) brings both back.
/// </summary>
public class SpearClawRemovalContentTests
{
    static readonly string[] Removed = [ArsenalRules.LightningClaw, ArsenalRules.PowerSpear];

    static List<List<string>> Melees(SimConfig c) => [.. c.Classes.Select(k => k.MeleeChoices.Select(m => m.Wargear).ToList())];

    [Fact]
    public void The_default_append_carries_neither_and_keeps_the_rest()
    {
        Assert.False(ArsenalRules.SpearClawOffered);   // no test run sets the switch
        var fixture = PowerSpearContentTests.Config();   // the Assault has the power sword (the claw's base) and the hammer (the spear's)
        var off = Arsenal.Append(fixture, "", []);
        foreach (var name in Removed) Assert.DoesNotContain(off.Classes.SelectMany(c => c.MeleeChoices), m => m.Wargear == name);
        Assert.Equal(Melees(fixture), Melees(off));   // every melee list as it was
        Assert.Contains(off.Thrown, t => t.Name == ArsenalRules.RemoteDetonators);   // W4 still appended (the data's stand-in values without the install)

        using var restore = ArsenalRules.SpearClawScoped(true);
        var on = Arsenal.Append(PowerSpearContentTests.Config(), "", []);
        foreach (var name in Removed) Assert.Contains(on.Classes[1].MeleeChoices, m => m.Wargear == name);
        Assert.Equal(on.Thrown.Select(t => t.Name), off.Thrown.Select(t => t.Name));
        Assert.Equal(on.Weapons.Select(w => w.Name), off.Weapons.Select(w => w.Name));   // the autocannon's append is the same either way
        Assert.Equal(on.Classes[1].MeleeChoices.Where(m => !ArsenalRules.IsSpearOrClaw(m.Wargear)).Select(m => m.Wargear), off.Classes[1].MeleeChoices.Select(m => m.Wargear));
    }

    [Fact]
    public void The_explicit_overloads_follow_their_argument_over_the_switch()
    {
        using var restore = ArsenalRules.SpearClawScoped(true);
        Assert.DoesNotContain(Arsenal.Append(PowerSpearContentTests.Config(), "", [], spearClaw: false).Classes[1].MeleeChoices, m => ArsenalRules.IsSpearOrClaw(m.Wargear));
        Assert.Equal(2, Arsenal.Append(PowerSpearContentTests.Config(), "", [], spearClaw: true).Classes[1].MeleeChoices.Count(m => ArsenalRules.IsSpearOrClaw(m.Wargear)));
    }
}

public partial class CustomizationCatalogTests
{
    [Fact]
    public void Musous_catalogue_has_no_spear_or_claw_row_by_default_and_keeps_the_rest()
    {
        var global = SmFormats.Baf.BafDocument.Read(GlobalBaf());
        var data = PerkCatalog.Build(global, name => name.StartsWith("mp_perk") ? SmFormats.Baf.BafDocument.Read(Gear("perk")) : SmFormats.Baf.BafDocument.Read(Gear(name.Contains("pistol") ? "pistol" : "melee")), null, []);
        // As the spear's catalogue test (withHammer): the fixture's Assault given the hammer (the spear's base), and here the power sword too (the claw's), as the install's carries them.
        var hammer = new LoadoutItem("sm_mp_melee_2hand_thunder_hammer", "melee", "Thunder Hammer", "", 1, true, null, null);
        var sword = new LoadoutItem("sm_mp_melee_powersword", "melee", "Power Sword", "", 1, true, null, null);
        // The shared fixture names a data "sm_mp_melee_lightning_claw" of its own (the profile tests' made-up melee item); it is taken out, so only the arsenal's rows are measured.
        var withHammer = data.WithClasses(c => c with { Melee = c.Melee with { Items = [.. c.Melee.Items.Where(i => !SpearClawNames.Contains(i.Name) && (c.Role != "assault" || (i.Name != sword.Name && i.Name != hammer.Name))), .. c.Role == "assault" ? new[] { sword, hammer } : []] } });
        static List<string> Rows(PerkCatalog c) => [.. c.Classes.SelectMany(k => k.Primary.Items.Concat(k.Pistol.Items).Concat(k.Melee.Items).Concat(k.Equipment.Items).Select(i => $"{k.Key}/{i.Slot}/{i.Name}"))];

        var off = withHammer.For(ContentExtras.Musou);
        foreach (var name in SpearClawNames) Assert.DoesNotContain(off.Classes.SelectMany(c => c.Melee.Items), i => i.Name == name);
        Assert.Contains(off.Classes.SelectMany(c => c.Equipment.Items), i => i.Name == ArsenalRules.RemoteDetonators);   // W4's row stays

        using var restore = ArsenalRules.SpearClawScoped(true);
        var on = withHammer.For(ContentExtras.Musou);
        foreach (var name in SpearClawNames) Assert.Contains(on.Classes.SelectMany(c => c.Melee.Items), i => i.Name == name);   // the fixture does make both rows
        Assert.Equal(Rows(on).Where(r => !SpearClawNames.Any(r.EndsWith)), Rows(off));   // the autocannon's and every other row, in order
        Assert.Equal(Rows(on).Count(r => r.EndsWith(ArsenalRules.Autocannon)), Rows(off).Count(r => r.EndsWith(ArsenalRules.Autocannon)));
    }

    static readonly string[] SpearClawNames = [ArsenalRules.LightningClaw, ArsenalRules.PowerSpear];
}
