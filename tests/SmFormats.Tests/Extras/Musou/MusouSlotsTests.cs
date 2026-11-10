using System.Text.Json.Nodes;
using SmContent;
using SmContent.Customization;
using SmContent.Extras.Musou;
using SmSim;
using SmSim.Extras.Musou;

using Xunit;

namespace SmFormats.Tests.Extras.Musou;

/// <summary>
/// Musou Mode's own loadout slots and the Librarian's loadout (musou/loadout-slots, 2026-11-10; <see cref="MusouSlots"/>, <see cref="MusouProfileView"/>): the migration of an old profile, the
/// two sets kept apart, looks deleted through the view, the Librarian's slot, the lenient reading of his entry and his loadout as wire indices. Hand-built profiles and configs only.
/// </summary>
public class MusouSlotsTests
{
    static CustomClass Class(int slot, string name, string role, string? look = null, string? primary = null) =>
        new(slot, name, role, primary, null, null, null, [null, null], new AppearanceRefs(look, null));

    static readonly AppearanceProfile Look1 = AppearanceProfile.Empty("sm-1", CustomizationCatalog.SpaceMarine, "Blue"),
        Look2 = AppearanceProfile.Empty("sm-2", CustomizationCatalog.SpaceMarine, "Red"), ChaosLook = AppearanceProfile.Empty("csm-1", CustomizationCatalog.Chaos, "Black");

    /// <summary>A Classic profile as every build before Musou's own slots wrote it: three loadouts (one the Ork Boy's), slot 2 active.</summary>
    static CustomizationProfile OldProfile(Selection? selected = null) => new(1, selected ?? Selection.CustomSlot(2),
        [Class(1, "Bolter", "tactical", "sm-1"), Class(2, "Hammer", "assault", "sm-2"), Class(3, "Boy", OrkClass.Role)], [Look1, Look2, ChaosLook]);

    [Fact]
    public void An_old_profile_gets_musou_slots_copied_from_classics_the_first_time()
    {
        var old = OldProfile();
        Assert.Null(old.Set(MusouSlots.SetName));
        var opened = MusouSlots.Opened(old);
        var set = opened.Set(MusouSlots.SetName)!;
        Assert.Equal([1, 2], set.Classes.Select(c => c.Slot));   // the Ork Boy's loadout stays Classic's: Musou plays Space Marines
        Assert.Equal(Selection.CustomSlot(2), set.Selected);
        Assert.Same(old.Classes, opened.Classes);   // Classic's own slots and selection are not touched
        Assert.Equal(old.Selected, opened.Selected);
        Assert.Same(opened, MusouSlots.Opened(opened));   // once made, never made again
        Assert.Equal(ProfileStore.Serialize(opened), ProfileStore.Serialize(MusouProfileView.Marines.Open(opened)));

        // An Ork loadout or the Ork DEFAULTS active in Classic: Musou starts on the Tactical DEFAULTS.
        Assert.Equal(Selection.DefaultFor("tactical"), MusouSlots.Opened(OldProfile(Selection.CustomSlot(3))).Set(MusouSlots.SetName)!.Selected);
        Assert.Equal(Selection.DefaultFor("tactical"), MusouSlots.Opened(OldProfile(Selection.DefaultFor(OrkClass.Role))).Set(MusouSlots.SetName)!.Selected);
        Assert.Equal(Selection.DefaultFor("heavy"), MusouSlots.Opened(OldProfile(Selection.DefaultFor("heavy"))).Set(MusouSlots.SetName)!.Selected);
    }

    [Fact]
    public void The_view_shows_musous_slots_and_keeps_its_edits_apart_from_classics()
    {
        var view = MusouProfileView.Marines;
        var stored = view.Open(OldProfile());
        var shown = view.Show(stored);
        Assert.Equal([1, 2], shown.Classes.Select(c => c.Slot));
        Assert.Equal(Selection.CustomSlot(2), shown.Selected);

        // Musou: rename slot 1, add slot 4 and make it active.
        var edited = shown with { Classes = [shown.ClassAt(1)! with { Name = "Musou bolter" }, shown.ClassAt(2)!, Class(4, "New", "heavy")], Selected = Selection.CustomSlot(4) };
        var kept = view.Keep(stored, edited);
        Assert.Equal(["Bolter", "Hammer", "Boy"], kept.Classes.Select(c => c.Name));   // Classic's slots as they were
        Assert.Equal(Selection.CustomSlot(2), kept.Selected);
        Assert.Equal(["Musou bolter", "Hammer", "New"], kept.Set(MusouSlots.SetName)!.Classes.Select(c => c.Name));
        Assert.Equal(Selection.CustomSlot(4), kept.Set(MusouSlots.SetName)!.Selected);

        // Classic: an edit of its own slot (no view) leaves Musou's alone.
        var classicEdit = kept with { Classes = kept.Classes.Select(c => c.Slot == 1 ? c with { Name = "Classic bolter" } : c).ToList() };
        Assert.Equal("Musou bolter", view.Show(classicEdit).ClassAt(1)!.Name);
        Assert.Null(view.Show(classicEdit).ClassAt(3));   // the Ork's slot is not Musou's
    }

    [Fact]
    public void A_look_deleted_through_the_view_is_cleared_from_classic_and_the_librarian_too()
    {
        var view = MusouProfileView.WithLibrarian;
        var stored = MusouSlots.WithLibrarian(view.Open(OldProfile()), new MusouLibrarianLoadout(null, null, null, null, "sm-1"));
        var shown = view.Show(stored);
        // As the appearance editor's delete does: the look goes, and the shown classes that wore it fall back to the default look.
        var deleted = shown with
        {
            Appearances = shown.Appearances.Where(a => a.Id != "sm-1").ToList(),
            Classes = shown.Classes.Select(c => c.Appearance.SpaceMarine == "sm-1" ? c with { Appearance = AppearanceRefs.None } : c).ToList(),
        };
        var kept = view.Keep(stored, deleted);
        Assert.Null(kept.ClassAt(1)!.Appearance.SpaceMarine);   // Classic's slot 1 wore it
        Assert.Equal("sm-2", kept.ClassAt(2)!.Appearance.SpaceMarine);
        Assert.Null(kept.Set(MusouSlots.SetName)!.ClassAt(1)!.Appearance.SpaceMarine);
        Assert.Null(MusouSlots.LibrarianOf(kept).Look);
        Assert.Equal(ProfileStore.Serialize(kept), ProfileStore.Serialize(view.Keep(kept, view.Show(kept))));   // nothing else changes on the way through
    }

    [Fact]
    public void The_librarian_shows_in_his_own_slot_only_with_his_view_and_his_choices_are_kept()
    {
        var stored = MusouProfileView.Marines.Open(OldProfile());
        Assert.Null(MusouProfileView.Marines.Show(stored).ClassAt(MusouSlots.LibrarianSlot));
        var shown = MusouProfileView.WithLibrarian.Show(stored);
        var librarian = shown.ClassAt(MusouSlots.LibrarianSlot)!;
        Assert.Equal(MusouSlots.LibrarianRole, librarian.Role);
        Assert.True(MusouSlots.LibrarianSlot > CustomizationProfile.MaxCustomClasses);   // past the ten: the slot strip and the Join never reach it
        Assert.Equal(MusouLibrarianLoadout.Default, MusouSlots.LibrarianLoadoutOf(librarian));
        Assert.Empty(stored.Extras);

        var chosen = librarian with { Melee = "mp_melee_combat_knife", Equipment = "mp_eq_krak_grenade", Perks = ["mp_perk_x", "mp_perk_y"], Appearance = new AppearanceRefs("sm-2", null) };
        var kept = MusouProfileView.WithLibrarian.Keep(stored, shown with { Classes = shown.Classes.Where(c => c.Slot != MusouSlots.LibrarianSlot).Append(chosen).ToList() });
        Assert.Equal(new MusouLibrarianLoadout("mp_melee_combat_knife", "mp_eq_krak_grenade", "mp_perk_x", "mp_perk_y", "sm-2"), MusouSlots.LibrarianOf(kept));
        Assert.Equal([1, 2], kept.Set(MusouSlots.SetName)!.Classes.Select(c => c.Slot));   // he is not one of the Musou slots
        Assert.DoesNotContain(kept.Classes, c => c.Slot == MusouSlots.LibrarianSlot);
        Assert.Equal(MusouSlots.LibrarianOf(kept), MusouSlots.LibrarianOf(MusouProfileView.Marines.Keep(kept, MusouProfileView.Marines.Show(kept))));   // the Marines' view keeps him as he is

        // Back to every default: the entry goes.
        var reset = MusouProfileView.WithLibrarian.Keep(kept, MusouProfileView.WithLibrarian.Show(kept) with
        {
            Classes = MusouProfileView.WithLibrarian.Show(kept).Classes.Select(c => c.Slot == MusouSlots.LibrarianSlot ? MusouSlots.LibrarianClass(MusouLibrarianLoadout.Default) : c).ToList(),
        });
        Assert.False(reset.Extras.ContainsKey(MusouSlots.ExtrasKey));
    }

    [Fact]
    public void His_entry_keeps_the_rest_of_musous_extras_and_is_read_leniently()
    {
        var withOther = OldProfile().WithExtra(MusouSlots.ExtrasKey, """{ "future": [1], "librarian": { "melee": "x" } }""");
        var cleared = MusouSlots.WithLibrarian(withOther, MusouLibrarianLoadout.Default);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{ "future": [1] }"""), JsonNode.Parse(cleared.Extras[MusouSlots.ExtrasKey])));
        Assert.Same(cleared, MusouSlots.WithLibrarian(cleared, MusouLibrarianLoadout.Default));   // nothing to change: the same profile

        MusouLibrarianLoadout Read(string json) => MusouSlots.LibrarianOf(OldProfile().WithExtra(MusouSlots.ExtrasKey, json));
        Assert.Equal(MusouLibrarianLoadout.Default, Read("""{ "librarian": 5 }"""));
        Assert.Equal(MusouLibrarianLoadout.Default, Read("""{ "librarian": { "melee": 3, "perks": "x", "look": "sm-404" } }"""));
        Assert.Null(Read("""{ "librarian": { "look": "csm-1" } }""").Look);   // a Chaos look: he is a Space Marine
        var dup = Read("""{ "librarian": { "perks": ["mp_perk_a", "MP_PERK_A"] } }""");
        Assert.Equal("mp_perk_a", dup.Perk1);
        Assert.Null(dup.Perk2);   // a perk once
        Assert.Equal(MusouLibrarianLoadout.Default, Read("""{ "librarian": { "melee": "     " } }"""));
        Assert.Equal("sm-2", Read("""{ "librarian": { "look": "sm-2" } }""").Look);
    }

    [Fact]
    public void A_removed_look_is_taken_off_the_librarian_and_his_other_choices_stay()
    {
        var withHim = MusouSlots.WithLibrarian(OldProfile(), new MusouLibrarianLoadout("mp_melee_combat_knife", null, "mp_perk_x", null, "sm-1"));
        var removed = ProfileViews.RemoveLook(withHim, "sm-1");
        Assert.Equal(new MusouLibrarianLoadout("mp_melee_combat_knife", null, "mp_perk_x", null, null), MusouSlots.LibrarianOf(removed));
        // A new look taking the id sm-1 is not put on him: the stored entry names no look.
        var reused = removed with { Appearances = [.. removed.Appearances, AppearanceProfile.Empty("sm-1", CustomizationCatalog.SpaceMarine, "New")] };
        Assert.Null(MusouSlots.LibrarianOf(reused).Look);
        Assert.Same(withHim, MusouSlots.LookRemoved(withHim, "sm-2"));   // a look he does not wear: nothing changes
    }

    [Fact]
    public void Removing_his_look_takes_only_the_look_key_and_keeps_fields_this_build_does_not_know()
    {
        // Re-review 2026-11-10: a newer build's fields inside his entry and beside it survive the look's removal.
        var profile = OldProfile().WithExtra(MusouSlots.ExtrasKey, """{ "librarian": { "melee": "mp_melee_combat_knife", "look": "sm-1", "later": [1, 2] }, "other": true }""");
        var removed = MusouSlots.LookRemoved(profile, "sm-1");
        var root = System.Text.Json.Nodes.JsonNode.Parse(removed.Extras[MusouSlots.ExtrasKey])!.AsObject();
        var lib = root["librarian"]!.AsObject();
        Assert.False(lib.ContainsKey("look"));
        Assert.Equal("mp_melee_combat_knife", (string?)lib["melee"]);
        Assert.Equal("[1,2]", lib["later"]!.ToJsonString());
        Assert.True((bool)root["other"]!);
        // His only key the look: his entry goes, and Musou's entry with it when nothing else is left.
        var lookOnly = OldProfile().WithExtra(MusouSlots.ExtrasKey, """{ "librarian": { "look": "sm-1" } }""");
        Assert.False(MusouSlots.LookRemoved(lookOnly, "sm-1").Extras.ContainsKey(MusouSlots.ExtrasKey));
    }

    [Fact]
    public void A_hand_edited_entry_with_a_key_used_twice_reads_as_his_defaults_and_does_not_block_edits()
    {
        var broken = OldProfile().WithExtra(MusouSlots.ExtrasKey, """{ "librarian": { "melee": "a", "melee": "b" } }""");
        Assert.Equal(MusouLibrarianLoadout.Default, MusouSlots.LibrarianOf(broken));
        var rootDup = OldProfile().WithExtra(MusouSlots.ExtrasKey, """{ "librarian": { "melee": "a" }, "librarian": { "melee": "b" } }""");
        Assert.Equal(MusouLibrarianLoadout.Default, MusouSlots.LibrarianOf(rootDup));
        var edited = MusouSlots.WithLibrarian(rootDup, new MusouLibrarianLoadout("mp_melee_combat_knife", null, null, null, null));   // no throw: his entry starts again
        Assert.Equal("mp_melee_combat_knife", MusouSlots.LibrarianOf(edited).Melee);
        var view = MusouProfileView.WithLibrarian;
        var stored = view.Open(rootDup);
        Assert.NotNull(view.Keep(stored, view.Show(stored)));   // every Musou edit goes through Keep
        Assert.Same(rootDup, MusouSlots.LookRemoved(rootDup, "sm-1"));
    }

    [Fact]
    public void His_page_offers_his_own_choices_with_the_chosen_ones_selected()
    {
        var data = new LibrarianLoadout("Power Sword", ["Combat Knife"], [], ["Frag", "Blind"])
        {
            MeleeOptions = [("mp_melee_powersword", "Power Sword"), ("mp_melee_combat_knife", "Combat Knife")],
            EquipmentOptions = [("mp_eq_frag_grenade", "Frag"), ("mp_eq_krak_grenade", "Blind")],
            PerkNames = ["mp_perk_x", "mp_perk_y"],
        };
        var picks = new MusouLibrarianPicks(new MusouLibrarianLoadout("mp_melee_combat_knife", null, null, "mp_perk_y", "sm-2"),
            [("mp_perk_x", "X", "x text"), ("mp_perk_y", "Y", "y text")], "mp_perk_x", [("sm-1", "Blue"), ("sm-2", "Red")]);
        List<MusouRow> Rows(int step) => MusouCustomizer.LibrarianStep(step, "CUSTOM 1", data, picks).SelectMany(s => s.Rows).ToList();

        var weapons = Rows(1);
        Assert.Equal(["Combat Knife"], weapons.Where(r => r.Selected).Select(r => r.Title));
        Assert.Contains(weapons, r => r.Action == "melee:" && r.Title == "Power Sword");   // his default stores nothing
        Assert.Contains(weapons, r => r.Action == "melee:mp_melee_combat_knife");
        var gear = Rows(2);
        Assert.Equal(["Frag"], gear.Where(r => r.Selected).Select(r => r.Title));   // nothing chosen: his class's first
        Assert.Contains(gear, r => r.Action == "equipment:mp_eq_krak_grenade");
        Assert.Contains(gear, r => r.Title == "Choice of powers" && r.Kind == MusouRowKind.NotYet);
        var perks = MusouCustomizer.LibrarianStep(3, "CUSTOM 1", data, picks);
        Assert.Equal(["PERK 1", "PERK 2"], perks.Select(s => s.Heading));
        Assert.Equal(["X"], perks[0].Rows.Where(r => r.Selected).Select(r => r.Title));   // the class default in slot 1
        Assert.Equal(["Y"], perks[1].Rows.Where(r => r.Selected).Select(r => r.Title));
        Assert.Contains(perks[1].Rows, r => r.Action == "perk2:" && r.Title == "None");
        var look = Rows(4);
        Assert.Equal(["Look: Red"], look.Where(r => r.Selected).Select(r => r.Title));
        Assert.Contains(look, r => r.Action == "look:");
        Assert.Contains(look, r => r.Action == "look:new");
        Assert.All(Rows(1).Concat(Rows(3)).Concat(Rows(4)), r => Assert.NotEqual(MusouRowKind.NotYet, r.Kind));   // his own loadout: nothing "not yet" there any more
        Assert.Equal(["SELECTED"], look.First(r => r.Selected).Tags);

        string summary = MusouCustomizer.LibrarianSummary("CUSTOM 1", data, picks);
        foreach (var part in new[] { "Combat Knife", "Frag", "X, Y", "Look: Red" }) Assert.Contains(part, summary);
    }

    // A small Musou config: the Tactical (two perks, frag and blind grenades, the knife) and the Assault (the power sword); MusouLoadout.Prepare appends the Librarian.
    static readonly MeleeStep Light = new(1f, 0.2f, 0.4f, 0.6f, 0.5f, 0.7f, 25f, 10f, 3.5f);
    static readonly ProjectileSpec Ball = new("ball", 10f, 2f, "sphere", 0.1f, [], true, true, false, 0, 0f, 0f, 4, null, null);
    static readonly SimConfig Raw = new()
    {
        Thrown = [new ThrownSpec("sm_mp_eq_frag_grenade", 2, 2, 1f, 30f, Ball), new ThrownSpec("sm_mp_eq_krak_grenade", 2, 2, 1f, 30f, Ball)],
        Perks = [new PerkSpec("mp_perk_x", null, PerkKind.Class, 1), new PerkSpec("mp_perk_y", null, PerkKind.Class, 1)],
        Classes =
        [
            new("sm - tactical", -1, -1, [Light]) { EquipmentChoices = [0, 1], PerkChoices = [0, 1], MeleeChoices = [new("sm_mp_melee_combat_knife", [Light])], Side = Team.SpaceMarines, Role = "tactical" },
            new("sm - assault marine", -1, -1, [Light]) { MeleeChoices = [new(LibrarianRules.PowerSwordWargear, [Light])], Side = Team.SpaceMarines, Role = "assault" },
        ],
    };

    [Fact]
    public void His_loadout_becomes_indices_of_his_own_class_and_unknown_names_his_defaults()
    {
        var config = MusouLoadout.Prepare(Raw with { Extras = ContentExtras.Musou });
        int librarian = config.Classes.ToList().FindIndex(c => c.Name == LibrarianRules.ClassName);
        Assert.True(librarian >= 0);
        var names = LoadoutIndices.Names(config, librarian, "melee");

        var defaults = MusouSlots.LibrarianChoice(MusouLibrarianLoadout.Default, config, librarian);
        Assert.Equal(new LoadoutChoice(Class: librarian, Melee: 0), defaults);   // his starting weapon, the class's own defaults

        var knife = names.ToList().FindIndex(n => n.EndsWith("combat_knife", StringComparison.Ordinal));
        var chosen = MusouSlots.LibrarianChoice(new MusouLibrarianLoadout("mp_melee_combat_knife", "mp_eq_krak_grenade", "mp_perk_y", "mp_perk_x", null), config, librarian);
        Assert.Equal(knife, chosen.Melee);
        Assert.Equal(LoadoutIndices.Choice(config, librarian, "equipment", "sm_mp_eq_krak_grenade"), chosen.Equipment);
        Assert.Equal(LoadoutIndices.Choice(config, librarian, "perk", "mp_perk_y"), chosen.Perk1);
        Assert.Equal(LoadoutIndices.Choice(config, librarian, "perk", "mp_perk_x"), chosen.Perk2);

        var unknown = MusouSlots.LibrarianChoice(new MusouLibrarianLoadout("mp_melee_no_such", "mp_eq_no_such", "no_perk", "no_perk", null), config, librarian);
        Assert.Equal(new LoadoutChoice(Class: librarian, Melee: 0), unknown);
    }
}
