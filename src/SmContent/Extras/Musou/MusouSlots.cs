using System.Text.Json.Nodes;
using SmContent.Customization;
using SmSim;

namespace SmContent.Extras.Musou;

/// <summary>
/// The Librarian's own loadout (musou/loadout-slots, owner 2026-11-10: "Musou own loadout spans approved"; every rule here ours): his melee weapon, equipment, two perks and look, stored by name
/// in the profile's Musou extras (<see cref="MusouSlots.ExtrasKey"/>, entry <c>librarian</c>). Null is his default: the first melee weapon of his class, his class's default equipment, the
/// class default perk and no second perk, and the active Standard Marines loadout's look (what he wore before he had a loadout). The names are checked against his class only when a
/// loadout is made from them (<see cref="MusouSlots.LibrarianChoice"/>), so a weapon added to his class later can be stored now.
/// </summary>
/// <param name="Melee">A melee wargear name (side prefix stripped), or null.</param>
/// <param name="Equipment">A thrown item's name (side prefix stripped), or null.</param>
/// <param name="Perk1">Perk slot 1's wargear name, or null (the class default perk).</param>
/// <param name="Perk2">Perk slot 2's wargear name, or null (none).</param>
/// <param name="Look">A Space Marine look of the profile (<see cref="AppearanceProfile.Id"/>), or null: the active Standard Marines loadout's look.</param>
public sealed record MusouLibrarianLoadout(string? Melee, string? Equipment, string? Perk1, string? Perk2, string? Look)
{
    public static readonly MusouLibrarianLoadout Default = new(null, null, null, null, null);

    /// <summary>Every choice is the default (nothing to store).</summary>
    public bool IsDefault => Melee is null && Equipment is null && Perk1 is null && Perk2 is null && Look is null;

    /// <summary>The longest name kept.</summary>
    public const int MaxNameLength = 64;
}

/// <summary>
/// Musou Mode's own loadout slots (musou/loadout-slots, 2026-11-10; the owner approved "Musou own loadout spans"; docs/changes-from-original.md section 0). Musou's pages stop sharing Classic
/// PvP's custom-class slots: the profile keeps a loadout set of their own (<see cref="SetName"/>: ten slots and a selection, read and repaired as Classic's) and the Librarian's loadout
/// (<see cref="MusouLibrarianLoadout"/>). The customizer serves Musou's pages through <see cref="MusouProfileView"/>; Classic PvP's slots, selection and file stay as they were.
/// </summary>
/// <remarks>
/// Migration: a profile without the Musou set (every profile before this) gets one the first time Musou's pages use it (<see cref="Opened"/>): a copy of the profile's own slots and selection
/// as they are then (Ork loadouts left out: Musou Mode plays Space Marines only), so a player's loadouts are still there in Musou, and from then on the two are apart. Nothing is written before the
/// player saves, and a profile Musou never opened is written byte for byte as before.
/// </remarks>
public static class MusouSlots
{
    /// <summary>The loadout set of Musou's Standard Marines (<see cref="CustomizationProfile.Sets"/>).</summary>
    public const string SetName = "musou";

    /// <summary>The profile extras entry of Musou's own data (<see cref="CustomizationProfile.Extras"/>): <c>{ "librarian": { ... } }</c>.</summary>
    public const string ExtrasKey = "musou";

    /// <summary>
    /// The slot the Librarian shows in while his page is open (<see cref="MusouProfileView.WithLibrarian"/>): past the ten (<see cref="CustomizationProfile.MaxCustomClasses"/>), which the slot strip,
    /// the creator's slot actions and the Join never reach, so the appearance editor can edit his look as any loadout's.
    /// </summary>
    public const int LibrarianSlot = CustomizationProfile.MaxCustomClasses + 1;

    /// <summary>His role in the profile: the Tactical Marine's (his body and perks; <c>MusouLoadout.AppendLibrarian</c>).</summary>
    public const string LibrarianRole = "tactical";

    /// <summary>The name his slot shows.</summary>
    public const string LibrarianName = "Librarian";

    /// <summary>The Musou set of <paramref name="profile"/>: its own when there is one, else what <see cref="Opened"/> would make (a copy of the profile's own slots).</summary>
    public static LoadoutSet MarinesOf(CustomizationProfile profile) => profile.Set(SetName) ?? Seed(profile);

    /// <summary>The profile with its Musou set made (a copy of its own slots) when it has none; the profile itself when it has one.</summary>
    public static CustomizationProfile Opened(CustomizationProfile profile) => profile.Set(SetName) is not null ? profile : profile.WithSet(SetName, Seed(profile));

    /// <summary>Classic's slots and selection as Musou's first set: every Space Marine role's loadout (the Ork Boy's left out), the selection when it is one of them, else the Tactical DEFAULTS.</summary>
    static LoadoutSet Seed(CustomizationProfile profile)
    {
        var classes = profile.Classes.Where(c => !IsOrk(c.Role)).ToList();
        var selected = profile.Selected;
        bool keep = selected.Kind == SelectionKind.Custom ? classes.Any(c => c.Slot == selected.Slot) : selected.Role is { Length: > 0 } role && !IsOrk(role);
        return new LoadoutSet(keep ? selected : Selection.DefaultFor(LibrarianRole), classes);
    }

    static bool IsOrk(string? role) => string.Equals(role, OrkClass.Role, StringComparison.OrdinalIgnoreCase);

    // ---- the Librarian ----

    /// <summary>The Librarian's loadout in <paramref name="profile"/> (the defaults when it stores none or it cannot be read).</summary>
    public static MusouLibrarianLoadout LibrarianOf(CustomizationProfile profile)
    {
        if (!profile.Extras.TryGetValue(ExtrasKey, out var text)) return MusouLibrarianLoadout.Default;
        try
        {
            if (JsonNode.Parse(text) is not JsonObject root || root["librarian"] is not JsonObject lib) return MusouLibrarianLoadout.Default;
            string? Name(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) && s.Trim() is { Length: > 0 and <= MusouLibrarianLoadout.MaxNameLength } t ? t : null;
            var perks = lib["perks"] as JsonArray;
            string? perk1 = perks is { Count: > 0 } ? Name(perks[0]) : null, perk2 = perks is { Count: > 1 } ? Name(perks[1]) : null;
            if (perk2 is not null && string.Equals(perk1, perk2, StringComparison.OrdinalIgnoreCase)) perk2 = null;   // a perk once
            string? look = Name(lib["look"]);
            if (look is not null && profile.Appearance(look) is not { Race: CustomizationCatalog.SpaceMarine }) look = null;   // a look that is gone, or not a Space Marine one: the active loadout's
            return new MusouLibrarianLoadout(Name(lib["melee"]), Name(lib["equipment"]), perk1, perk2, look);
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException or FormatException) { return MusouLibrarianLoadout.Default; }
    }

    /// <summary>The profile with the Librarian's loadout <paramref name="loadout"/> (the defaults remove it; anything else in the Musou extras entry is kept).</summary>
    public static CustomizationProfile WithLibrarian(CustomizationProfile profile, MusouLibrarianLoadout loadout)
    {
        JsonObject root;
        try { root = profile.Extras.TryGetValue(ExtrasKey, out var text) && JsonNode.Parse(text) is JsonObject o ? o : new JsonObject(); }
        catch (System.Text.Json.JsonException) { root = new JsonObject(); }
        if (loadout.IsDefault) root.Remove("librarian");
        else
            root["librarian"] = new JsonObject
            {
                ["melee"] = loadout.Melee, ["equipment"] = loadout.Equipment, ["perks"] = new JsonArray(loadout.Perk1, loadout.Perk2), ["look"] = loadout.Look,
            };
        string? json = root.Count == 0 ? null : root.ToJsonString();
        if (profile.Extras.TryGetValue(ExtrasKey, out var old) ? old == json : json is null) return profile;
        return profile.WithExtra(ExtrasKey, json);
    }

    /// <summary>The Librarian as the custom class his page edits (<see cref="LibrarianSlot"/>, the Tactical's role; his look as its Space Marine look).</summary>
    public static CustomClass LibrarianClass(MusouLibrarianLoadout loadout) =>
        new(LibrarianSlot, LibrarianName, LibrarianRole, null, null, loadout.Melee, loadout.Equipment, [loadout.Perk1, loadout.Perk2], new AppearanceRefs(loadout.Look, null));

    /// <summary>The Librarian's loadout of the class his page edited.</summary>
    public static MusouLibrarianLoadout LibrarianLoadoutOf(CustomClass c) => new(c.Melee, c.Equipment, c.Perk(0), c.Perk(1), c.Appearance.SpaceMarine);

    /// <summary>
    /// The loadout request of the Librarian's saved loadout for his class <paramref name="librarianClass"/> of a Musou config (<paramref name="config"/>): every name looked up in his class's lists
    /// (<see cref="LoadoutIndices.Choice"/>); a name his class does not have is his default. His melee is the first choice when none or an unknown one is chosen (his starting weapon).
    /// </summary>
    public static LoadoutChoice LibrarianChoice(MusouLibrarianLoadout loadout, SimConfig config, int librarianClass)
    {
        int melee = LoadoutIndices.Choice(config, librarianClass, "melee", loadout.Melee);
        int perk1 = LoadoutIndices.Choice(config, librarianClass, "perk", loadout.Perk1), perk2 = LoadoutIndices.Choice(config, librarianClass, "perk", loadout.Perk2);
        return new LoadoutChoice(Class: librarianClass, Melee: Math.Max(0, melee), Equipment: LoadoutIndices.Choice(config, librarianClass, "equipment", loadout.Equipment),
            Perk1: perk1, Perk2: perk2 == perk1 ? -1 : perk2);
    }

    /// <summary>The wire look of the Librarian's own look, or null when he wears the active loadout's (the caller's).</summary>
    public static AppearanceCode? LibrarianLook(CustomizationProfile profile, CustomizationCatalog catalog) =>
        LibrarianOf(profile).Look is { } id && profile.Appearance(id) is { Race: CustomizationCatalog.SpaceMarine } look ? ProfileWire.ToCode(look, catalog) : null;
}

/// <summary>
/// The customizer's view of a profile in Musou Mode (<see cref="IProfileView"/>; <see cref="MusouSlots"/>): the screens see the Musou set's slots and selection as the profile's own, and with
/// <see cref="WithLibrarian"/> (his page open) the Librarian's loadout as one more class in <see cref="MusouSlots.LibrarianSlot"/>. Edits go back to the Musou set and the Librarian's entry; Classic PvP's
/// slots and selection are never changed through it, but a look the screens delete is cleared from them too (the looks are one list for every set).
/// </summary>
public sealed class MusouProfileView : IProfileView
{
    /// <summary>The Standard Marines' slots.</summary>
    public static readonly MusouProfileView Marines = new(false);

    /// <summary>The same, and the Librarian in <see cref="MusouSlots.LibrarianSlot"/> (his page and the look editor opened from it).</summary>
    public static readonly MusouProfileView WithLibrarian = new(true);

    MusouProfileView(bool librarian) => ShowsLibrarian = librarian;

    /// <summary>The Librarian shows as a class in <see cref="MusouSlots.LibrarianSlot"/>.</summary>
    public bool ShowsLibrarian { get; }

    public CustomizationProfile Show(CustomizationProfile stored)
    {
        var set = MusouSlots.MarinesOf(stored);
        IReadOnlyList<CustomClass> classes = ShowsLibrarian ? [.. set.Classes, MusouSlots.LibrarianClass(MusouSlots.LibrarianOf(stored))] : set.Classes;
        return stored with { Selected = set.Selected, Classes = classes };
    }

    public CustomizationProfile Keep(CustomizationProfile stored, CustomizationProfile shown)
    {
        var marines = shown.Classes.Where(c => c.Slot is >= 1 and <= CustomizationProfile.MaxCustomClasses).OrderBy(c => c.Slot).ToList();
        var kept = stored with
        {
            Appearances = shown.Appearances,
            OrkLook = shown.OrkLook,
            Extras = shown.Extras,
            Sets = shown.Sets,
            Classes = ProfileViews.DropMissingLooks(stored.Classes, shown.Appearances),   // Classic's own slots: only a deleted look leaves them
        };
        kept = kept.WithSet(MusouSlots.SetName, new LoadoutSet(shown.Selected, marines));
        foreach (var (name, set) in kept.Sets.Where(kv => kv.Key != MusouSlots.SetName).ToList())
            if (!ReferenceEquals(ProfileViews.DropMissingLooks(set.Classes, shown.Appearances), set.Classes))
                kept = kept.WithSet(name, set with { Classes = ProfileViews.DropMissingLooks(set.Classes, shown.Appearances) });
        var librarian = ShowsLibrarian && shown.ClassAt(MusouSlots.LibrarianSlot) is { } c ? MusouSlots.LibrarianLoadoutOf(c) : MusouSlots.LibrarianOf(stored);
        if (librarian.Look is { } look && !shown.Appearances.Any(a => a.Id == look)) librarian = librarian with { Look = null };
        return MusouSlots.WithLibrarian(kept, librarian);
    }

    public CustomizationProfile Open(CustomizationProfile stored) => MusouSlots.Opened(stored);
}
