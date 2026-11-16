using SmContent;

namespace SmFormats.Tests;

/// <summary>
/// fix/fx-hud-leaks (polish item 30): the HUD's weapon box asked <see cref="WeaponBoxRules.For"/> every frame, got a new spec and a new <c>Show</c> list each time, and cached a
/// show set keyed by the list's identity, so the cache gained one set a frame for the whole match. Now <see cref="WeaponBoxRules.For"/> hands out one spec per display and the
/// HUD's cache (<see cref="WeaponBoxShowSets"/>) is keyed by the paths' values. Pure rules: no install and no original bytes.
/// </summary>
public class WeaponBoxShowSetTests
{
    static readonly string?[] Types =
    [
        "bolter", "bolt_pistol", "storm_bolter", "heavy_bolter", "plasma_gun", "plasma_cannon", "plasma_pistol", "laz_cannon", "lascannon", "melta", "stalker",
        "grenade_launcher", "knife", "chainsword", "", null,
    ];

    /// <summary>Every display as the rules gave it before the fix (Family, Show joined by '|', Bar, Value, Count): the HUD draws exactly as before.</summary>
    [Theory]
    [InlineData("bolter", false, "bolter", "ammo_mc|ammo_mc/ammo_count_mc|ammo_mc/ammo_txt", "ammo_mc/ammo_count_mc", WeaponBoxBar.Spent, "ammo_mc/ammo_txt")]
    [InlineData("bolter", true, "bolter", "ammo_mc|ammo_mc/ammo_count_mc|ammo_mc/ammo_txt", "ammo_mc/ammo_count_mc", WeaponBoxBar.Spent, "ammo_mc/ammo_txt")]
    [InlineData("bolt_pistol", false, "bolt_pistol", "ammo_mc|ammo_mc/ammo_count_bolt_pistol_mc|ammo_mc/ammo_txt", "ammo_mc/ammo_count_bolt_pistol_mc", WeaponBoxBar.Spent, "ammo_mc/ammo_txt")]
    [InlineData("bolt_pistol", true, "bolt_pistol", "ammo_mc|ammo_mc/ammo_count_bolt_pistol_mc|ammo_mc/ammo_txt", "ammo_mc/ammo_count_bolt_pistol_mc", WeaponBoxBar.Spent, "ammo_mc/ammo_txt")]
    [InlineData("storm_bolter", false, "storm_bolter", "ammo_stormbolter_mc|ammo_mc|ammo_mc/ammo_txt", "ammo_stormbolter_mc/ammo_bar", WeaponBoxBar.Spent, "ammo_mc/ammo_txt")]
    [InlineData("storm_bolter", true, "storm_bolter", "ammo_stormbolter_mc|ammo_mc|ammo_mc/ammo_txt", "ammo_stormbolter_mc/ammo_bar", WeaponBoxBar.Spent, "ammo_mc/ammo_txt")]
    [InlineData("heavy_bolter", false, "heavy_bolter", "ammo_autocannon_mc|ammo_mc|ammo_mc/ammo_txt", "ammo_autocannon_mc/ammo_bar", WeaponBoxBar.Heat, "ammo_mc/ammo_txt")]
    [InlineData("heavy_bolter", true, "heavy_bolter", "ammo_autocannon_mc|ammo_mc|ammo_mc/ammo_txt", "ammo_autocannon_mc/ammo_bar", WeaponBoxBar.Heat, "ammo_mc/ammo_txt")]
    [InlineData("plasma_gun", false, "plasma_gun", "ammo_plasmagun_mc|ammo_plasmagun_mc/ammo_plasmagun_mc", "ammo_plasmagun_mc/ammo_plasmagun_mc", WeaponBoxBar.Heat, "ammo_plasmagun_mc/ammo_count")]
    [InlineData("plasma_gun", true, "plasma_gun", "ammo_plasmagun_mc|ammo_plasmagun_mc/ammo_plasmagun_chaos_mc", "ammo_plasmagun_mc/ammo_plasmagun_chaos_mc", WeaponBoxBar.Heat, "ammo_plasmagun_mc/ammo_count")]
    [InlineData("plasma_cannon", false, "plasma_cannon", "ammo_plasmagun_mc|ammo_plasmagun_mc/ammo_plasmagun_mc", "ammo_plasmagun_mc/ammo_plasmagun_mc", WeaponBoxBar.Heat, "ammo_plasmagun_mc/ammo_count")]
    [InlineData("plasma_cannon", true, "plasma_cannon", "ammo_plasmagun_mc|ammo_plasmagun_mc/ammo_plasmagun_chaos_mc", "ammo_plasmagun_mc/ammo_plasmagun_chaos_mc", WeaponBoxBar.Heat, "ammo_plasmagun_mc/ammo_count")]
    [InlineData("plasma_pistol", false, "plasma_pistol", "ammo_plasmapistol_mc", "ammo_plasmapistol_mc/ammo_bar", WeaponBoxBar.Heat, "ammo_plasmapistol_mc/ammo_count")]
    [InlineData("plasma_pistol", true, "plasma_pistol", "ammo_plasmapistol_mc|ammo_plasmapistol_mc/ammo_plasmagun_chaos_mc", "ammo_plasmapistol_mc/ammo_plasmagun_chaos_mc", WeaponBoxBar.Heat, "ammo_plasmapistol_mc/ammo_count")]
    [InlineData("laz_cannon", false, "lascannon", "ammo_lascannon_mc", "ammo_lascannon_mc/ammo_bar", WeaponBoxBar.Spent, "ammo_lascannon_mc/ammo_count")]
    [InlineData("lascannon", true, "lascannon", "ammo_lascannon_mc", "ammo_lascannon_mc/ammo_bar", WeaponBoxBar.Spent, "ammo_lascannon_mc/ammo_count")]
    [InlineData("melta", false, "melta", "ammo_meltagun_mc", "ammo_meltagun_mc/ammo_bar", WeaponBoxBar.Spent, "ammo_meltagun_mc/ammo_count")]
    [InlineData("melta", true, "melta", "ammo_meltagun_mc", "ammo_meltagun_mc/ammo_bar", WeaponBoxBar.Spent, "ammo_meltagun_mc/ammo_count")]
    [InlineData("stalker", false, "stalker", "Ammo_Stalker_mc", null, WeaponBoxBar.None, "Ammo_Stalker_mc/ammo_count")]
    [InlineData("stalker", true, "stalker", "Ammo_Stalker_mc", null, WeaponBoxBar.None, "Ammo_Stalker_mc/ammo_count")]
    [InlineData("grenade_launcher", false, "grenade_launcher", "loadout_grenaderounddisplay_mc|ammo_mc|ammo_mc/ammo_txt", null, WeaponBoxBar.None, "ammo_mc/ammo_txt")]
    [InlineData("grenade_launcher", true, "grenade_launcher", "loadout_grenaderounddisplay_mc|ammo_mc|ammo_mc/ammo_txt", null, WeaponBoxBar.None, "ammo_mc/ammo_txt")]
    [InlineData("BOLTER", false, "bolter", "ammo_mc|ammo_mc/ammo_count_mc|ammo_mc/ammo_txt", "ammo_mc/ammo_count_mc", WeaponBoxBar.Spent, "ammo_mc/ammo_txt")]
    public void Every_display_keeps_its_paths(string type, bool chaos, string family, string show, string? bar, WeaponBoxBar value, string? count)
    {
        var spec = WeaponBoxRules.For("", type, chaos);
        Assert.NotNull(spec);
        Assert.Equal(family, spec.Family);
        Assert.Equal(show, string.Join("|", spec.Show));
        Assert.Equal(bar, spec.Bar);
        Assert.Equal(value, spec.Value);
        Assert.Equal(count, spec.Count);
    }

    [Fact]
    public void A_weapon_without_a_display_and_the_name_rule_are_unchanged()
    {
        Assert.Null(WeaponBoxRules.For("sm_mp_melee_combat_knife", "knife", false));
        Assert.Null(WeaponBoxRules.For("", "", true));
        Assert.Null(WeaponBoxRules.For("", null, false));
        Assert.Equal("bolter", WeaponBoxRules.For("sm_mp_ranged_bolter", null, false)!.Family);   // no weapon_type: the wargear name's rule
        Assert.Equal("lascannon", WeaponBoxRules.For("csm_mp_ranged_las_cannon", "", true)!.Family);
    }

    [Fact]
    public void The_rules_hand_out_one_spec_per_display()
    {
        foreach (var type in Types)
            foreach (bool chaos in new[] { false, true })
            {
                var first = WeaponBoxRules.For("", type, chaos);
                for (int i = 0; i < 100; i++)
                {
                    var again = WeaponBoxRules.For("", type, chaos);
                    Assert.Same(first, again);
                    if (first is not null) Assert.Same(first.Show, again!.Show);
                }
            }
    }

    [Fact]
    public void The_show_set_cache_stays_bounded_over_many_frames()
    {
        var sets = new WeaponBoxShowSets();
        var distinct = new HashSet<string>(StringComparer.Ordinal);
        for (int frame = 0; frame < 5_000; frame++)
            foreach (var type in Types)
                foreach (bool chaos in new[] { false, true })
                {
                    if (WeaponBoxRules.For("", type, chaos) is not { } spec) continue;
                    distinct.Add(string.Join("|", spec.Show));
                    var set = sets.Get(spec.Show);
                    // A fresh list of the same paths (a spec built anew, as every frame before the fix) finds the same set: the key is the paths, not the list.
                    Assert.Same(set, sets.Get(spec.Show.ToArray()));
                    Assert.Same(set, sets.Get(new List<string>(spec.Show)));
                    if (frame == 0) Assert.True(set.SetEquals(spec.Show));
                }
        Assert.Equal(distinct.Count, sets.Count);
        Assert.InRange(sets.Count, 1, Types.Length * 2);
    }

    [Fact]
    public void The_cache_keeps_different_paths_apart()
    {
        var sets = new WeaponBoxShowSets();
        var a = sets.Get(["ammo_mc", "ammo_mc/ammo_txt"]);
        var b = sets.Get(["ammo_mc", "ammo_mc/ammo_count_mc"]);
        var c = sets.Get(["ammo_mc"]);
        var empty = sets.Get([]);
        Assert.NotSame(a, b);
        Assert.NotSame(a, c);
        Assert.Equal(4, sets.Count);
        Assert.Empty(empty);
        Assert.True(a.Contains("ammo_mc/ammo_txt"));
        Assert.False(a.Contains("AMMO_MC/AMMO_TXT"));   // ordinal, as the HUD's paths
        Assert.Same(a, sets.Get(new[] { "ammo_mc", "ammo_mc/ammo_txt" }));
        Assert.Equal(4, sets.Count);
    }

    [Fact]
    public void The_hud_keys_its_show_sets_by_value()
    {
        string hud = File.ReadAllText(Path.Combine(Root(), "client", "Hud", "Hud.Fidelity.cs"));
        Assert.Contains("static readonly WeaponBoxShowSets ShowSets = new();", hud);
        Assert.Contains("ShowSets.Get(spec.Show)", hud);
        Assert.DoesNotContain("Dictionary<IReadOnlyList<string>, HashSet<string>> ShowSets", hud);
    }

    static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "SmReimpl.slnx"))) d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("SmReimpl.slnx");
    }
}
