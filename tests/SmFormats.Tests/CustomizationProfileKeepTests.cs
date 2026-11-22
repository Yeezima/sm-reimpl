using System.Text.Json.Nodes;
using SmContent;
using SmContent.Customization;
using SmSim;
using Xunit;

namespace SmFormats.Tests;

/// <summary>
/// customizer/keep-unknown-picks: a build whose catalogue lacks added content's items (the build without added content) keeps what another build wrote: a loadout set of content it does not have
/// whole, and a pick of an install item no class of it offers inside the classes it does read; genuinely invalid values are repaired as ever. Synthetic install, temp profile directory.
/// </summary>
public partial class CustomizationProfileTests
{
    const string AddedProfile = """
        { "schema": 1, "selected": { "kind": "custom", "slot": 1 },
          "classes": [
            { "slot": 1, "name": "Heavy", "role": "heavy", "primary": "mp_exotic_auto_cannon", "perks": ["mp_perk_a", "mp_perk_added"], "appearance": { "space_marine": "sm-1" } },
            { "slot": 2, "name": "Tac", "role": "tactical", "primary": "mp_ranged_bolter", "second": "mp_exotic_auto_cannon", "perks": [] },
            { "slot": 3, "name": "Other", "role": "tactical", "primary": "mp_ranged_plasma_gun", "perks": [] } ],
          "appearances": [ { "id": "sm-1", "race": "space_marine", "name": "Look" } ],
          "sets": { "musou": { "selected": { "kind": "custom", "slot": 4 }, "classes": [
              { "slot": 4, "name": "Musou", "role": "heavy", "primary": "mp_musou_only_weapon", "perks": ["mp_musou_only_perk"], "appearance": { "space_marine": "sm-1" } } ] } } }
        """;

    string ProfileFile => Path.Combine(_profileDir, ProfileStore.FileName);

    void WriteProfile(string json)
    {
        Directory.CreateDirectory(_profileDir);
        File.WriteAllText(ProfileFile, json);
    }

    /// <summary>The catalogue of the build that has the content: the Devastator also offers the auto cannon and the added perk.</summary>
    CustomizationCatalog WithAddedItems() => Catalog.WithPerks(Catalog.Perks.WithClasses(c => c.Key is not ("sm - devastator" or "sm - tactical") ? c : c with
    {
        Primary = c.Primary with { Items = [.. c.Primary.Items, new LoadoutItem("sm_mp_exotic_auto_cannon", "ranged", "Auto", "", 1, true, null, null)] },
        Perks = [.. c.Perks, new PerkInfo("mp_perk_added", null, PerkKind.Class, 1, null, "Added", "")],
    }));

    [Fact]
    public void A_build_without_the_content_keeps_foreign_picks_and_sets_through_an_edit_of_another_class()
    {
        WriteProfile(AddedProfile);

        // The build without added content: its catalogue lacks the items, it reads no loadout set of added content.
        var (stripped, repairs) = NewStore().Load(Catalog, readsAddedSets: false);
        Assert.All(repairs, r => Assert.True(r.Informational, r.Path + ": " + r.Message));   // nothing repaired, only noted
        var heavy = stripped.ClassAt(1)!;
        Assert.Equal("mp_exotic_auto_cannon", heavy.Primary);
        Assert.Equal(["mp_perk_a", "mp_perk_added"], heavy.Perks);
        Assert.Equal("mp_exotic_auto_cannon", stripped.ClassAt(2)!.Second);
        Assert.Equal("sm-1", heavy.Appearance.SpaceMarine);
        Assert.NotNull(stripped.Set("musou")!.Raw);

        // In play and on the wire this build uses the class default: the creator's lookup finds nothing, the join sends no index.
        var devastator = Catalog.Perks.Class("sm - devastator")!;
        Assert.Null(Catalog.Perks.Resolve(devastator, "ranged", heavy.Primary));
        Assert.Equal(-1, ProfileWire.ToLoadout(heavy, Config(), SmSim.Team.SpaceMarines).Primary);

        // A Classic edit of another class, then a save of the whole profile.
        var edited = stripped with { Classes = stripped.Classes.Select(c => c.Slot == 3 ? c with { Name = "Edited" } : c).ToList() };
        NewStore().Save(edited);

        // The build with the content reads it all again.
        var (normal, normalRepairs) = NewStore().Load(WithAddedItems());
        Assert.Equal("Edited", normal.ClassAt(3)!.Name);
        Assert.Equal("mp_exotic_auto_cannon", normal.ClassAt(1)!.Primary);
        Assert.Equal("mp_perk_added", normal.ClassAt(1)!.Perk(1));
        Assert.Equal("mp_exotic_auto_cannon", normal.ClassAt(2)!.Second);
        Assert.DoesNotContain(normalRepairs, r => r.Path.StartsWith("classes[0]") && !r.Informational);
        var musou = JsonNode.Parse(File.ReadAllText(ProfileFile))!["sets"]!["musou"];
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(AddedProfile)!["sets"]!["musou"], musou));   // the Musou set is as it was written
    }

    [Fact]
    public void A_set_of_missing_content_round_trips_byte_stable_and_a_build_with_the_content_still_repairs_it()
    {
        WriteProfile(AddedProfile);
        var (first, _) = NewStore().Load(Catalog, readsAddedSets: false);
        NewStore().Save(first);
        string once = File.ReadAllText(ProfileFile);
        var (second, _) = NewStore().Load(Catalog, readsAddedSets: false);
        NewStore().Save(second);
        Assert.Equal(once, File.ReadAllText(ProfileFile));   // untouched sets: stable
        Assert.Contains("mp_musou_only_weapon", once);

        // The build that reads sets repairs what is invalid for it, as ever (the synthetic catalogue has no such weapon).
        var (read, repairs) = NewStore().Load(Catalog, readsAddedSets: true);
        Assert.Null(read.Set("musou")!.Raw);
        Assert.Contains(repairs, r => r.Path == "sets.musou.classes[0].primary" && !r.Informational);
    }

    [Fact]
    public void Genuinely_invalid_picks_are_still_repaired_in_the_build_without_the_content()
    {
        WriteProfile("""
            { "schema": 1, "selected": { "kind": "default", "role": "tactical" },
              "classes": [
                { "slot": 1, "name": "Bad", "role": "tactical", "primary": "mp_ranged_plazma_gun", "pistol": "mp_melee_chainsword", "second": "mp_ranged_nothing", "perks": ["mp_perk_typo", "mp_perk_a"] },
                { "slot": 2, "name": "WrongClass", "role": "tactical", "melee": "mp_melee_lightning_claw", "perks": [] } ],
              "appearances": [],
              "sets": { "musou": [], "Bad Name": {}, "ok": { "classes": [ { "slot": 1, "role": "tactical" } ] } } }
            """);
        var (loaded, repairs) = NewStore().Load(Catalog, readsAddedSets: false);
        var bad = loaded.ClassAt(1)!;
        Assert.Null(bad.Primary);      // a typo: not in the install
        Assert.Null(bad.Pistol);       // a melee weapon in the pistol slot
        Assert.Null(bad.Second);
        Assert.Equal([null, "mp_perk_a"], bad.Perks);
        Assert.Null(loaded.ClassAt(2)!.Melee);   // in the install and offered to another class: the wrong class, repaired
        foreach (var path in new[] { "classes[0].primary", "classes[0].pistol", "classes[0].second", "classes[0].perks[0]", "classes[1].melee", "sets.musou", "sets.Bad Name" })
            Assert.Contains(repairs, r => r.Path == path && !r.Informational);
        Assert.NotNull(loaded.Set("ok")!.Raw);   // a well-formed set of another content is kept as written
    }

    [Fact]
    public void Removing_a_look_clears_it_from_a_kept_set_too()
    {
        WriteProfile(AddedProfile);
        var (loaded, _) = NewStore().Load(Catalog, readsAddedSets: false);
        var removed = ProfileViews.RemoveLook(loaded, "sm-1");
        Assert.Null(removed.ClassAt(1)!.Appearance.SpaceMarine);
        var raw = JsonNode.Parse(removed.Set("musou")!.Raw!)!;
        Assert.Null(raw["classes"]![0]!["appearance"]!["space_marine"]);
        Assert.Equal("mp_musou_only_weapon", (string?)raw["classes"]![0]!["primary"]);
    }

    [Fact]
    public void A_build_that_has_the_content_reads_the_added_picks_without_a_repair()
    {
        WriteProfile(AddedProfile);
        var (loaded, repairs) = NewStore().Load(WithAddedItems(), readsAddedSets: true);
        Assert.DoesNotContain(repairs, r => r.Path.StartsWith("classes[") && !r.Informational);
        Assert.Equal("mp_exotic_auto_cannon", loaded.ClassAt(1)!.Primary);
        Assert.Null(loaded.Set("musou")!.Raw);
    }

    [Fact]
    public void A_foreign_pick_is_kept_only_in_a_slot_of_its_own_kind()
    {
        string armour = ProfileWire.Neutral("sm_a_head");   // an install wargear file of the armour slot "head"
        WriteProfile($$"""
            { "schema": 1, "selected": { "kind": "default", "role": "tactical" },
              "classes": [
                { "slot": 1, "name": "Wrong", "role": "tactical", "primary": "mp_melee_exotic_blade", "pistol": "mp_perk_added", "equipment": "{{armour}}",
                  "second": "mp_melee_exotic_blade", "perks": ["mp_exotic_auto_cannon", "{{armour}}"] },
                { "slot": 2, "name": "WrongMelee", "role": "assault", "melee": "mp_exotic_auto_cannon", "pistol": "mp_exotic_auto_cannon", "equipment": "mp_melee_exotic_blade", "perks": ["mp_melee_exotic_blade"] },
                { "slot": 3, "name": "Right", "role": "assault", "melee": "mp_melee_exotic_blade", "perks": ["mp_perk_added"] },
                { "slot": 4, "name": "Second", "role": "tactical", "second": "mp_exotic_auto_cannon", "perks": ["mp_perk_added", "mp_perk_added"] } ],
              "appearances": [] }
            """);
        var (loaded, repairs) = NewStore().Load(Catalog, readsAddedSets: false);
        var wrong = loaded.ClassAt(1)!;
        Assert.Null(wrong.Primary);     // a melee weapon in the primary slot
        Assert.Null(wrong.Pistol);      // a perk in the pistol slot
        Assert.Null(wrong.Equipment);   // armour in the equipment slot
        Assert.Null(wrong.Second);      // a melee weapon as a second primary
        Assert.Equal([null, null], wrong.Perks);   // a ranged weapon and armour in the perk slots
        var assault = loaded.ClassAt(2)!;
        Assert.Null(assault.Melee);     // a ranged weapon in the melee slot
        Assert.Null(assault.Pistol);
        Assert.Null(assault.Equipment);
        Assert.Equal([null, null], assault.Perks);
        foreach (var path in new[] { "classes[0].primary", "classes[0].pistol", "classes[0].equipment", "classes[0].second", "classes[0].perks[0]", "classes[0].perks[1]",
                                     "classes[1].melee", "classes[1].pistol", "classes[1].equipment", "classes[1].perks[0]" })
            Assert.Contains(repairs, r => r.Path == path && !r.Informational);
        // The kind fits: kept (and noted, not repaired).
        Assert.Equal("mp_melee_exotic_blade", loaded.ClassAt(3)!.Melee);
        Assert.Equal("mp_perk_added", loaded.ClassAt(3)!.Perk(0));
        Assert.Equal("mp_exotic_auto_cannon", loaded.ClassAt(4)!.Second);
        Assert.Equal("mp_perk_added", loaded.ClassAt(4)!.Perk(0));
        Assert.Null(loaded.ClassAt(4)!.Perk(1));   // the same perk twice: the second is repaired
        Assert.DoesNotContain(repairs, r => r.Path.StartsWith("classes[2]") && !r.Informational);
    }

    [Fact]
    public void A_pick_whose_install_slot_is_unknown_is_repaired()
    {
        WriteProfile("""{ "schema": 1, "selected": { "kind": "default", "role": "heavy" }, "classes": [ { "slot": 1, "role": "heavy", "primary": "mp_exotic_auto_cannon" } ], "appearances": [] }""");
        var undescribed = Catalog.WithInstallItems(new Dictionary<string, string> { ["sm_mp_exotic_auto_cannon"] = "" });
        var (loaded, repairs) = NewStore().Load(undescribed, readsAddedSets: false);
        Assert.Null(loaded.ClassAt(1)!.Primary);
        Assert.Contains(repairs, r => r.Path == "classes[0].primary" && !r.Informational);
    }

    [Fact]
    public void A_foreign_second_primary_of_a_non_tactical_role_is_repaired()
    {
        WriteProfile("""{ "schema": 1, "selected": { "kind": "default", "role": "heavy" }, "classes": [ { "slot": 1, "role": "heavy", "second": "mp_exotic_auto_cannon" } ], "appearances": [] }""");
        var (loaded, repairs) = NewStore().Load(Catalog, readsAddedSets: false);
        Assert.Null(loaded.ClassAt(1)!.Second);
        Assert.Contains(repairs, r => r.Path == "classes[0].second" && !r.Informational);
    }

    [Fact]
    public void Kept_foreign_second_and_perks_are_the_default_on_the_wire()
    {
        var custom = new CustomClass(1, "t", "tactical", null, null, null, null, ["mp_perk_added", "mp_exotic_auto_cannon"], AppearanceRefs.None, "mp_exotic_auto_cannon");
        var choice = ProfileWire.ToLoadout(custom, Config(), SmSim.Team.SpaceMarines);
        Assert.Equal(-1, choice.Second);
        Assert.Equal(-1, choice.Perk1);
        Assert.Equal(-1, choice.Perk2);
    }

    [Fact]
    public void Raw_sets_stop_at_the_set_cap()
    {
        var sets = string.Join(",", Enumerable.Range(1, CustomizationProfile.MaxSets + 2).Select(i => $$"""  "added{{i}}": { "classes": [ { "slot": 1, "role": "heavy", "primary": "mp_added_only" } ] }"""));
        WriteProfile($$"""{ "schema": 1, "selected": { "kind": "default", "role": "tactical" }, "classes": [], "appearances": [], "sets": { {{sets}} } }""");
        var (loaded, repairs) = NewStore().Load(Catalog, readsAddedSets: false);
        Assert.Equal(CustomizationProfile.MaxSets, loaded.Sets.Count);
        Assert.All(loaded.Sets.Values, set => Assert.NotNull(set.Raw));
        Assert.Equal(2, repairs.Count(r => r.Path.StartsWith("sets.") && r.Message.Contains("more than") && !r.Informational));
    }

    [Fact]
    public void The_install_items_survive_a_content_view_and_a_changed_list()
    {
        Assert.NotEmpty(Catalog.InstallItems);
        Assert.Equal("ranged", Catalog.InstallItems["mp_exotic_auto_cannon"]);
        Assert.Equal("perk", Catalog.InstallItems["mp_perk_added"]);
        Assert.Equal("melee", Catalog.InstallItems["mp_melee_exotic_blade"]);
        Assert.Same(Catalog.InstallItems, Catalog.For(ContentExtras.None).InstallItems);
        var withPerks = Catalog.WithPerks(Catalog.Perks.WithClasses(c => c));
        Assert.Equal(Catalog.InstallItems.Count, withPerks.InstallItems.Count);
        Assert.True(withPerks.IsForeignPick("mp_exotic_auto_cannon", "ranged"));
        Assert.False(withPerks.IsForeignPick("mp_exotic_auto_cannon", "melee"));
    }
}
