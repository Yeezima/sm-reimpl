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

    [Fact]
    public void The_librarian_page_has_five_steps_each_with_rows()
    {
        Assert.Equal(["CLASS", "WEAPONS", "POWERS", "PERKS", "LOOK"], MusouCustomizer.LibrarianSteps);
        for (int step = 0; step < MusouCustomizer.LibrarianSteps.Count; step++)
        {
            var sections = MusouCustomizer.LibrarianStep(step, "CUSTOM 1 \"Bolter line\"");
            Assert.NotEmpty(sections);
            Assert.All(sections, s => { Assert.False(string.IsNullOrWhiteSpace(s.Heading)); Assert.NotEmpty(s.Rows); });
            Assert.All(sections.SelectMany(s => s.Rows), r => { Assert.False(string.IsNullOrWhiteSpace(r.Title)); Assert.False(string.IsNullOrWhiteSpace(r.Description)); });
        }
    }

    [Fact]
    public void What_does_not_exist_yet_is_marked_not_yet_and_what_is_real_is_not()
    {
        var rows = Enumerable.Range(0, MusouCustomizer.LibrarianSteps.Count).SelectMany(s => MusouCustomizer.LibrarianStep(s, "x")).SelectMany(s => s.Rows).ToList();
        Assert.All(rows.Where(r => r.Kind == MusouRowKind.NotYet), r => Assert.Equal(["NOT YET"], r.Tags));
        Assert.All(rows.Where(r => r.Kind != MusouRowKind.NotYet), r => Assert.DoesNotContain("NOT YET", r.Tags));
        // The customizations the Librarian lacks today: his own perks, other melee weapons, a choice of powers, a look of his own.
        Assert.Equal(4, rows.Count(r => r.Kind == MusouRowKind.NotYet));
    }

    [Fact]
    public void The_look_step_carries_the_active_loadouts_look_over()
    {
        var look = MusouCustomizer.LibrarianStep(MusouCustomizer.LookStep, "CUSTOM 2 \"Paint\"").SelectMany(s => s.Rows).ToList();
        var carried = Assert.Single(look, r => r.Kind == MusouRowKind.CarriedOver);
        Assert.Contains("CUSTOM 2 \"Paint\"", carried.Description);
        Assert.Contains("FROM YOUR LOADOUT", carried.Tags);
        Assert.Contains("CUSTOM 2", MusouCustomizer.LibrarianSummary("CUSTOM 2 \"Paint\""));
    }

    [Fact]
    public void The_powers_text_follows_the_librarian_rules()
    {
        string powers = string.Join("\n", MusouCustomizer.LibrarianStep(2, "x").SelectMany(s => s.Rows).Select(r => r.Description));
        Assert.Contains($"{LibrarianRules.LightningRange:0} m", powers);
        Assert.Contains($"{LibrarianRules.MaxTargets} targets", powers);
        Assert.Contains($"{LibrarianRules.PullRange:0} m", powers);
        Assert.Contains($"{LibrarianRules.ShockwaveRadius:0} m", powers);
        Assert.Contains($"{LibrarianRules.PullCooldown:0} s cooldown", powers);
        Assert.Contains($"{LibrarianRules.ShockwaveCooldown:0} s cooldown", powers);
        Assert.Contains("12.5", powers);   // the cooling rate, invariant culture
    }

    [Fact]
    public void The_restore_switch_is_the_documented_flag() => Assert.Equal("musou-old-customizer", MusouCustomizer.OldCustomizerFlag);
}
