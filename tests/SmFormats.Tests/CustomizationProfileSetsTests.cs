using System.Text.Json.Nodes;
using SmClient.Customizer.Model;
using SmContent;
using SmContent.Customization;
using Xunit;

namespace SmFormats.Tests;

/// <summary>
/// musou/loadout-slots (2026-11-10): the profile's further loadout sets (<see cref="CustomizationProfile.Sets"/>) and added content's own data (<see cref="CustomizationProfile.Extras"/>), and the
/// profile session's view (<see cref="IProfileView"/>). An old profile (without either) reads and writes byte for byte as before; a set follows the profile's own repair rules; the extras are
/// kept as they were written. Synthetic install and profiles only.
/// </summary>
public partial class CustomizationProfileTests
{
    static CustomClass Tactical(int slot, string name, string? look = null) =>
        new(slot, name, "tactical", "mp_ranged_plasma_gun", null, null, "mp_eq_frag_grenade", ["mp_perk_a", null], new AppearanceRefs(look, null));

    [Fact]
    public void An_old_profile_without_sets_or_extras_loads_and_saves_byte_for_byte()
    {
        var store = NewStore();
        var profile = RichProfile(store.SaveEmblem(EmblemBytes(2)).ToHex());
        store.Save(profile);
        string before = File.ReadAllText(store.ProfilePath);
        Assert.DoesNotContain("\"sets\"", before);
        Assert.DoesNotContain("\"extras\"", before);

        var (loaded, repairs) = NewStore().Load(Catalog);
        Assert.Empty(repairs);
        Assert.Empty(loaded.Sets);
        Assert.Empty(loaded.Extras);
        NewStore().Save(loaded);
        Assert.Equal(before, File.ReadAllText(store.ProfilePath));
    }

    [Fact]
    public void Sets_and_extras_save_and_load_identically()
    {
        var store = NewStore();
        var profile = RichProfile(store.SaveEmblem(EmblemBytes(3)).ToHex())
            .WithSet("musou", new LoadoutSet(Selection.CustomSlot(2), [Tactical(2, "Musou bolter", "sm-1"), Tactical(5, "Second")]))
            .WithSet("other", new LoadoutSet(Selection.DefaultFor("assault"), []))
            .WithExtra("musou", "{\"librarian\":{\"melee\":\"mp_melee_powersword\",\"perks\":[\"mp_perk_a\",null],\"look\":\"sm-1\"}}");
        store.Save(profile);
        string first = File.ReadAllText(store.ProfilePath);

        var (loaded, repairs) = NewStore().Load(Catalog);
        Assert.Empty(repairs);
        Assert.Equal(ProfileStore.Serialize(profile), ProfileStore.Serialize(loaded));
        Assert.Equal(Selection.CustomSlot(2), loaded.Set("musou")!.Selected);
        Assert.Equal(["Musou bolter", "Second"], loaded.Set("musou")!.Classes.Select(c => c.Name));
        Assert.Equal("sm-1", loaded.Set("musou")!.ClassAt(2)!.Appearance.SpaceMarine);
        Assert.Equal(Selection.CustomSlot(3), loaded.Selected);   // the profile's own selection and slots are Classic's, untouched
        Assert.Equal([3, 7], loaded.Classes.Select(c => c.Slot));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(profile.Extras["musou"]), JsonNode.Parse(loaded.Extras["musou"])));

        NewStore().Save(loaded);
        Assert.Equal(first, File.ReadAllText(store.ProfilePath));   // a second save is byte-identical
    }

    [Fact]
    public void A_set_is_read_and_repaired_by_the_profiles_own_rules()
    {
        Directory.CreateDirectory(_profileDir);
        File.WriteAllText(Path.Combine(_profileDir, ProfileStore.FileName), """
            { "schema": 1, "selected": { "kind": "default", "role": "tactical" }, "classes": [], "appearances": [],
              "sets": { "musou": { "selected": { "kind": "custom", "slot": 9 },
                "classes": [
                  { "slot": 1, "name": "Kept", "role": "tactical", "primary": "sm_mp_ranged_bolter", "perks": ["mp_perk_a"] },
                  { "slot": 0, "name": "Bad slot", "role": "tactical" },
                  { "slot": 2, "name": "Bad role", "role": "librarian" },
                  { "slot": 3, "name": "Bad item", "role": "tactical", "primary": "no_such_gun", "appearance": { "space_marine": "sm-9" } } ] } } }
            """);
        var (loaded, repairs) = NewStore().Load(Catalog);
        var set = loaded.Set("musou")!;
        Assert.Equal([1, 3], set.Classes.Select(c => c.Slot));
        Assert.Equal("mp_ranged_bolter", set.ClassAt(1)!.Primary);   // stored race-neutral, as the profile's own
        Assert.Null(set.ClassAt(3)!.Primary);
        Assert.Null(set.ClassAt(3)!.Appearance.SpaceMarine);
        Assert.Equal(Selection.DefaultFor("tactical"), set.Selected);   // slot 9 has no class
        Assert.All(repairs, r => Assert.StartsWith("sets.musou.", r.Path));
        Assert.Contains(repairs, r => r.Path == "sets.musou.classes[1]");
        Assert.Contains(repairs, r => r.Path == "sets.musou.classes[2]");
        Assert.Contains(repairs, r => r.Path == "sets.musou.classes[3].primary");
        Assert.Contains(repairs, r => r.Path == "sets.musou.classes[3].appearance.space_marine");
        Assert.Contains(repairs, r => r.Path == "sets.musou.selected");
    }

    [Fact]
    public void Unusable_set_and_extras_names_and_values_are_dropped_and_extras_keep_what_they_hold()
    {
        Directory.CreateDirectory(_profileDir);
        File.WriteAllText(Path.Combine(_profileDir, ProfileStore.FileName), """
            { "schema": 1, "selected": { "kind": "default", "role": "tactical" }, "classes": [], "appearances": [],
              "sets": { "Bad Name": { "classes": [] }, "musou": 5, "ok": { "classes": [] } },
              "extras": { "x": 3, "Not ok": { "a": 1 }, "musou": { "librarian": { "melee": "anything" }, "future": [1, 2, { "deep": true }] } } }
            """);
        var (loaded, repairs) = NewStore().Load(Catalog);
        Assert.Equal(["ok"], loaded.Sets.Keys);
        Assert.Equal(["musou"], loaded.Extras.Keys);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{ "librarian": { "melee": "anything" }, "future": [1, 2, { "deep": true }] }"""), JsonNode.Parse(loaded.Extras["musou"])));
        foreach (var path in new[] { "sets.Bad Name", "sets.musou", "extras.x", "extras.Not ok" }) Assert.Contains(repairs, r => r.Path == path);

        NewStore().Save(loaded);   // what the store keeps of the extras is written back as it was (a build without that content does not lose it)
        var again = NewStore().Load(Catalog).Profile;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(loaded.Extras["musou"]), JsonNode.Parse(again.Extras["musou"])));
    }

    [Fact]
    public void Removing_a_look_clears_it_from_every_set_so_a_new_look_never_takes_its_place_unasked()
    {
        var look = AppearanceProfile.Empty("sm-1", CustomizationCatalog.SpaceMarine, "Blue");
        var profile = new CustomizationProfile(1, Selection.CustomSlot(1), [Tactical(1, "Classic", "sm-1")], [look])
            .WithSet("mode", new LoadoutSet(Selection.CustomSlot(2), [Tactical(2, "Mode", "sm-1"), Tactical(3, "Other")]));
        var removed = AppearanceEdits.RemoveAppearance(profile, "sm-1");
        Assert.Empty(removed.Appearances);
        Assert.Null(removed.ClassAt(1)!.Appearance.SpaceMarine);
        Assert.Null(removed.Set("mode")!.ClassAt(2)!.Appearance.SpaceMarine);   // the set's slot too (review: it kept the id)
        var (again, id) = AppearanceEdits.AddAppearance(removed, CustomizationCatalog.SpaceMarine);
        Assert.Equal("sm-1", id);   // the lowest free id is reused ...
        Assert.Null(again.Set("mode")!.ClassAt(2)!.Appearance.SpaceMarine);   // ... and nobody wears the new look unasked

        // Saved and read back: no repair notice for a set that named the removed look.
        var store = NewStore();
        store.Save(removed);
        Assert.Empty(NewStore().Load(Catalog).Repairs);
    }

    [Fact]
    public void An_extras_entry_with_a_key_used_twice_is_dropped_alone()
    {
        Directory.CreateDirectory(_profileDir);
        File.WriteAllText(Path.Combine(_profileDir, ProfileStore.FileName), """
            { "schema": 1, "selected": { "kind": "default", "role": "tactical" }, "classes": [], "appearances": [],
              "extras": { "musou": { "librarian": { "melee": "a", "melee": "b" } }, "other": { "list": [ { "x": 1 } ] } } }
            """);
        var result = NewStore().Load(Catalog);
        Assert.Equal(ProfileLoadStatus.Loaded, result.Status);   // not the whole profile
        Assert.Equal(["other"], result.Profile.Extras.Keys);
        Assert.Contains(result.Repairs, r => r.Path == "extras.musou");
    }

    /// <summary>A view of one set's slots, for the session tests (no added content needed).</summary>
    sealed class SetView(string name) : IProfileView
    {
        public CustomizationProfile Show(CustomizationProfile stored) =>
            stored.Set(name) is { } set ? stored with { Selected = set.Selected, Classes = set.Classes } : stored with { Selected = Selection.DefaultFor("tactical"), Classes = [] };
        public CustomizationProfile Keep(CustomizationProfile stored, CustomizationProfile shown) =>
            stored.WithSet(name, new LoadoutSet(shown.Selected, shown.Classes)) with { Appearances = shown.Appearances };
        public CustomizationProfile Open(CustomizationProfile stored) => stored.Set(name) is not null ? stored : stored.WithSet(name, new LoadoutSet(stored.Selected, stored.Classes));
    }

    [Fact]
    public void A_session_view_edits_its_set_only_and_saves_the_whole_profile()
    {
        var store = NewStore();
        var classic = new CustomizationProfile(1, Selection.CustomSlot(1), [Tactical(1, "Classic one")], []);
        store.Save(classic);
        string classicText = File.ReadAllText(store.ProfilePath);

        var session = ProfileSession.Open(Catalog, store);
        var view = new SetView("mode");
        session.UseView(view);
        Assert.False(session.Dirty);   // the set made on opening is in the saved copy too
        Assert.Same(session.Working, session.Working);   // the shown profile is cached
        Assert.Equal("Classic one", session.Working.ClassAt(1)!.Name);   // a copy of the profile's own slots to start

        session.Edit(p => LoadoutEdits.Rename(p, 1, "Mode one").Profile);
        Assert.True(session.Dirty);
        Assert.Equal("Mode one", session.Working.ClassAt(1)!.Name);
        Assert.Equal("Classic one", session.Stored.ClassAt(1)!.Name);   // the profile's own slot is not touched
        session.Edit(p => p);   // an edit that changes nothing keeps the shown profile
        Assert.Null(session.Save());

        var reloaded = NewStore().Load(Catalog).Profile;
        Assert.Equal("Classic one", reloaded.ClassAt(1)!.Name);
        Assert.Equal("Mode one", reloaded.Set("mode")!.ClassAt(1)!.Name);

        session.UseView(null);
        Assert.Equal("Classic one", session.Working.ClassAt(1)!.Name);   // no view: the profile's own slots
        session.UseView(view);
        session.Edit(p => LoadoutEdits.Rename(p, 1, "Unsaved").Profile);
        session.Revert();
        Assert.Equal("Mode one", session.Working.ClassAt(1)!.Name);

        // A profile no view ever opened is written as before.
        var plain = ProfileSession.Open(Catalog, NewStore());
        Assert.False(plain.Dirty);
        Assert.Null(plain.Save());
        var file = File.ReadAllText(store.ProfilePath);
        Assert.Contains("\"mode\"", file);   // (this store's profile has the set by now)
        var untouched = NewStore();
        File.WriteAllText(untouched.ProfilePath, classicText);
        var fresh = ProfileSession.Open(Catalog, untouched);
        Assert.Null(fresh.Save());
        Assert.Equal(classicText, File.ReadAllText(untouched.ProfilePath));
    }
}
