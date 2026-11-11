using SmContent.Extras.Musou;
using SmSim;
using SmSim.Extras.Musou;

namespace SmFormats.Tests;

/// <summary>
/// Musou Mode's customizer (owner, 2026-11-10): the top level Standard Marines / Librarian / Heretic replaces the faction row in Musou only; the Librarian's page shows his real, fixed loadout and
/// marks what does not exist yet; the Heretic is a placeholder. Classic PvP keeps its layout (the layout is used only on the Musou profile, and the restore switch brings the old one back).
/// </summary>
public class MusouCustomizerTests
{
    [Theory]
    [InlineData(ContentExtras.Musou, false, true)]
    [InlineData(ContentExtras.Musou, true, false)]      // --musou-old-customizer: the shared layout again
    [InlineData(ContentExtras.None, false, false)]       // faithful (Classic)
    [InlineData(ContentExtras.Orks | ContentExtras.Gore | ContentExtras.LoyalistBarks, false, false)]   // extended (Classic)
    [InlineData(ContentExtras.Orks, false, false)]
    public void The_musou_layout_is_used_only_on_the_musou_profile_without_the_restore_switch(ContentExtras content, bool oldSwitch, bool uses) =>
        Assert.Equal(uses, MusouCustomizer.Uses(content, oldSwitch));

    [Fact]
    public void The_extended_profile_never_gets_the_musou_layout() =>
        Assert.False(MusouCustomizer.Uses(ContentExtras.All, false));

    [Fact]
    public void The_top_level_is_standard_marines_librarian_and_heretic_in_that_order()
    {
        Assert.Equal([MusouCustomizerEntry.StandardMarines, MusouCustomizerEntry.Librarian, MusouCustomizerEntry.Heretic], MusouCustomizer.Entries.Select(e => e.Entry));
        Assert.Equal("Standard Marines", MusouCustomizer.Info(MusouCustomizerEntry.StandardMarines).Label);
        Assert.Equal("Librarian", MusouCustomizer.Info(MusouCustomizerEntry.Librarian).Label);
        Assert.StartsWith("Heretic", MusouCustomizer.Info(MusouCustomizerEntry.Heretic).Label);
    }

    [Fact]
    public void The_heretic_is_a_placeholder_marked_not_yet_available()
    {
        var heretic = MusouCustomizer.Info(MusouCustomizerEntry.Heretic);
        Assert.False(heretic.Available);
        Assert.Contains("not yet", heretic.Label, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NOT YET AVAILABLE", MusouCustomizer.HereticHeading);
        Assert.True(MusouCustomizer.Info(MusouCustomizerEntry.StandardMarines).Available);
        Assert.True(MusouCustomizer.Info(MusouCustomizerEntry.Librarian).Available);
    }

    // A small config: the Tactical (bolter and plasma gun, bolt pistol, knife, frag and blind grenades), the Assault (the power sword), a Chaos Tactical. The Librarian is appended by MusouLoadout.Prepare.
    static readonly MeleeStep Light = new(1f, 0.2f, 0.4f, 0.6f, 0.5f, 0.7f, 25f, 10f, 3.5f);
    static WeaponSpec Gun(string name, WeaponSlot slot) => new(name, slot, TriggerKind.SemiAuto, 20f, 0.3f, 10, 40, 1.2f, 120f, 30f, 60f, 0.5f, 3f, 1f, 0.6f, 0.6f);
    static readonly ProjectileSpec Ball = new("ball", 10f, 2f, "sphere", 0.1f, [], true, true, false, 0, 0f, 0f, 4, null, null);
    static readonly SimConfig Raw = new()
    {
        Weapons = [Gun("sm_mp_bolter", WeaponSlot.Ranged), Gun("sm_mp_plasma_gun", WeaponSlot.Ranged), Gun("sm_mp_bolt_pistol", WeaponSlot.Pistol)],
        Thrown = [new ThrownSpec("sm_mp_eq_frag_grenade", 2, 2, 1f, 30f, Ball), new ThrownSpec("sm_mp_eq_krak_grenade", 2, 2, 1f, 30f, Ball)],
        Classes =
        [
            new("sm - tactical", 0, 2, [Light]) { PrimaryChoices = [0, 1], PistolChoices = [2], EquipmentChoices = [0, 1], MeleeChoices = [new("sm_mp_melee_combat_knife", [Light])], Side = Team.SpaceMarines, Role = "tactical" },
            new("sm - assault marine", -1, 2, [Light]) { PistolChoices = [2], MeleeChoices = [new("sm_mp_melee_chainsword", [Light]), new(LibrarianRules.PowerSwordWargear, [Light])], Side = Team.SpaceMarines, Role = "assault" },
            new("csm - tactical", 0, 2, [Light]) { PrimaryChoices = [0], Side = Team.Chaos, Role = "tactical" },
        ],
    };

    static string Name(string wargear) => "<" + wargear + ">";

    static LibrarianLoadout Librarian => LibrarianLoadout.Of(Raw, Name);

    [Fact]
    public void The_librarians_loadout_is_read_from_his_class_as_musou_loadout_makes_it()
    {
        var expected = MusouLoadout.Prepare(Raw with { Extras = ContentExtras.Musou }).Classes.Single(c => c.Name == LibrarianRules.ClassName);
        var shown = Librarian;
        Assert.Equal(Name(expected.MeleeChoices[0].Wargear), shown.Melee);
        Assert.Equal(Name(LibrarianRules.PowerSwordWargear), shown.Melee);   // his starting melee: the power sword (the force sword)
        Assert.Equal(expected.EquipmentChoices.Select(i => Name(Raw.Thrown[i].Name)), shown.Equipment);   // whatever his class carries: none today, the Marines' equipment once that lands
        Assert.Equal(expected.PrimaryChoices.Count + expected.PistolChoices.Count + (expected.Primary >= 0 ? 1 : 0) + (expected.Pistol >= 0 ? 1 : 0) > 0, shown.Guns.Count > 0);
        Assert.Equal(expected.MeleeChoices.Skip(1).Select(m => Name(m.Wargear)).Distinct(), shown.OtherMelee);
    }

    [Fact]
    public void A_config_without_a_tactical_has_no_librarian() =>
        Assert.Same(LibrarianLoadout.None, LibrarianLoadout.Of(Raw with { Classes = [Raw.Classes[2]] }, Name));

    [Fact]
    public void The_page_and_summary_follow_the_equipment_his_class_lists()
    {
        var none = new LibrarianLoadout("Force sword", [], [], []);
        var some = new LibrarianLoadout("Force sword", [], [], ["Frag Grenades", "Blind Grenades", "Combat Stimulants"]);
        var gearNone = MusouCustomizer.LibrarianStep(2, "x", none).SelectMany(s => s.Rows).ToList();
        Assert.Contains(gearNone, r => r.Title == "None" && r.Kind == MusouRowKind.Fixed);
        Assert.Contains("no grenades", MusouCustomizer.LibrarianSummary("x", none));
        var gearSome = MusouCustomizer.LibrarianStep(2, "x", some).SelectMany(s => s.Rows).ToList();
        Assert.Contains(gearSome, r => r.Title == "Frag Grenades" && r.Kind == MusouRowKind.Fixed);   // the default: the first
        Assert.Contains(gearSome, r => r.Title == "Blind Grenades" && r.Kind == MusouRowKind.NotYet);   // a choice needs a Librarian loadout
        Assert.Contains("Frag Grenades or Blind Grenades or Combat Stimulants", MusouCustomizer.LibrarianSummary("x", some));
        Assert.Contains("frag grenades", MusouCustomizer.LibrarianStep(0, "x", some).Single().Rows.Single().Description);
    }

    [Fact]
    public void The_librarian_page_has_six_steps_each_with_rows()
    {
        Assert.Equal(["CLASS", "WEAPONS", "POWERS", "PERKS", "LOOK", "SUPPORT"], MusouCustomizer.LibrarianSteps);
        for (int step = 0; step < MusouCustomizer.LibrarianSteps.Count; step++)
        {
            var sections = MusouCustomizer.LibrarianStep(step, "CUSTOM 1 \"Bolter line\"", Librarian);
            Assert.NotEmpty(sections);
            Assert.All(sections, s => { Assert.False(string.IsNullOrWhiteSpace(s.Heading)); Assert.NotEmpty(s.Rows); });
            Assert.All(sections.SelectMany(s => s.Rows), r => { Assert.False(string.IsNullOrWhiteSpace(r.Title)); Assert.False(string.IsNullOrWhiteSpace(r.Description)); });
        }
    }

    [Fact]
    public void What_does_not_exist_yet_is_marked_not_yet_and_what_is_real_is_not()
    {
        var rows = Enumerable.Range(0, MusouCustomizer.LibrarianSteps.Count).SelectMany(s => MusouCustomizer.LibrarianStep(s, "x", Librarian)).SelectMany(s => s.Rows).ToList();
        Assert.All(rows.Where(r => r.Kind == MusouRowKind.NotYet), r => Assert.Equal(["NOT YET"], r.Tags));
        Assert.All(rows.Where(r => r.Kind != MusouRowKind.NotYet), r => Assert.DoesNotContain("NOT YET", r.Tags));
        // The customizations the Librarian lacks: his own perks, a choice of powers, a look of his own (and other melee weapons while his class lists some).
        foreach (var title in new[] { "His own perks", "Choice of powers", "A look of his own", "Other melee weapons" })
            Assert.Contains(rows, r => r.Title == title && r.Kind == MusouRowKind.NotYet);
    }

    [Fact]
    public void The_look_step_carries_the_active_loadouts_look_over()
    {
        var look = MusouCustomizer.LibrarianStep(MusouCustomizer.LookStep, "CUSTOM 2 \"Paint\"", Librarian).SelectMany(s => s.Rows).ToList();
        var carried = Assert.Single(look, r => r.Kind == MusouRowKind.CarriedOver);
        Assert.Contains("CUSTOM 2 \"Paint\"", carried.Description);
        Assert.Contains("FROM YOUR LOADOUT", carried.Tags);
        Assert.Contains("CUSTOM 2", MusouCustomizer.LibrarianSummary("CUSTOM 2 \"Paint\"", Librarian));
    }

    [Fact]
    public void The_powers_are_the_ones_whose_numbers_the_librarian_rules_hold()
    {
        var powers = LibrarianPowers.Present();
        // Each power shows exactly while its key number exists in LibrarianRules (looked up by name: a power taken out there drops from the page by itself).
        Assert.Equal(LibrarianPowers.Value("LightningRange") is not null, powers.Any(p => p.Name == "Chain lightning"));
        Assert.Equal(LibrarianPowers.Value("PullRange") is not null, powers.Any(p => p.Name == "Warp Pull"));
        Assert.Equal(LibrarianPowers.Value("ShockwaveRadius") is not null, powers.Any(p => p.Name == "Psychic Shockwave"));
        Assert.Contains(powers, p => p.IsPower);
        Assert.Null(LibrarianPowers.Value("NoSuchNumber"));
        string text = string.Join("\n", MusouCustomizer.LibrarianStep(2, "x", Librarian).SelectMany(s => s.Rows).Select(r => r.Description));
        Assert.Contains(LibrarianPowers.Value("ShockwaveRadius")!.Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " m", text);   // the number the rules hold now
        Assert.Contains($"{LibrarianRules.MaxTargets} targets", text);
        string summary = MusouCustomizer.LibrarianSummary("x", Librarian);
        Assert.All(powers.Where(p => p.IsPower), p => Assert.Contains(p.Name, summary));
    }

    [Fact]
    public void A_share_is_shown_as_a_percentage() =>
        Assert.Equal(LibrarianPowers.Value("CoolPerSecond") * 100, LibrarianPowers.Value("CoolPerSecond*100"));

    [Fact]
    public void The_restore_switch_is_the_documented_flag() => Assert.Equal("musou-old-customizer", MusouCustomizer.OldCustomizerFlag);
}
