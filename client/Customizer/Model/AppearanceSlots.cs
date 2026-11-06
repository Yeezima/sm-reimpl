using System;
using System.Collections.Generic;
using System.Linq;
using SmContent;
using SmContent.Customization;

namespace SmClient.Customizer.Model;

/// <summary>
/// The ten appearance slots of a side (ui/appearance-slots, the owner's request 2026-11-06: "make an appearance, save it in one of 10 slots, then apply that appearance to any
/// loadout"). The slots are the profile's appearance library (<see cref="CustomizationProfile.Appearances"/>, at most <see cref="CustomizationProfile.MaxAppearancesPerRace"/> per
/// race), so no new file field and no migration: the looks every loadout already wore are its slots. A look's slot number comes from its id ("sm-3" / "csm-3" is slot 3, the
/// ids <see cref="CustomizationProfile.NewAppearanceId"/> hands out); a look with any other id (a hand-edited file) takes the first free slot in file order. A loadout wears a slot
/// by reference (<see cref="CustomClass.Appearance"/>): every loadout that wears it shows an edit (ours: docs/changes-from-original.md). Engine-free.
/// </summary>
public static class AppearanceSlots
{
    public const int Count = CustomizationProfile.MaxAppearancesPerRace;

    public static string Prefix(string race) => race == CustomizationCatalog.Chaos ? "csm-" : "sm-";

    /// <summary>The slot number an id names on a race (1..<see cref="Count"/>), or 0.</summary>
    static int Numbered(string id, string race) =>
        id.StartsWith(Prefix(race), StringComparison.Ordinal) && int.TryParse(id.AsSpan(Prefix(race).Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int n)
        && n is >= 1 and <= Count && id == Prefix(race) + n ? n : 0;

    /// <summary>The ten slots of a race: index 0 is slot 1; null is an empty slot.</summary>
    public static AppearanceProfile?[] Layout(CustomizationProfile profile, string race)
    {
        var slots = new AppearanceProfile?[Count];
        var rest = new List<AppearanceProfile>();
        foreach (var look in profile.AppearancesFor(race))
        {
            int n = Numbered(look.Id, race);
            if (n > 0 && slots[n - 1] is null) slots[n - 1] = look; else rest.Add(look);
        }
        foreach (var look in rest)
        {
            int free = Array.IndexOf(slots, null);
            if (free < 0) break;   // the store keeps at most ten per race
            slots[free] = look;
        }
        return slots;
    }

    /// <summary>The slot (1..<see cref="Count"/>) a look sits in, or 0 (null, another race, or not in the profile).</summary>
    public static int SlotOf(CustomizationProfile profile, string race, string? id) =>
        id is null ? 0 : Array.FindIndex(Layout(profile, race), a => a?.Id == id) + 1;

    /// <summary>The custom loadout slots that wear a look on <paramref name="race"/>, in order.</summary>
    public static IReadOnlyList<int> WornBy(CustomizationProfile profile, string race, string? id) =>
        id is null ? [] : profile.Classes.Where(c => c.Appearance.For(race) == id).Select(c => c.Slot).OrderBy(s => s).ToList();

    /// <summary>"loadout 2" / "loadouts 1, 3 and 5"; empty for none.</summary>
    public static string LoadoutList(IReadOnlyList<int> slots) => slots.Count switch
    {
        0 => "",
        1 => $"loadout {slots[0]}",
        _ => $"loadouts {string.Join(", ", slots.Take(slots.Count - 1))} and {slots[^1]}",
    };

    /// <summary>
    /// Saves a copy of <paramref name="source"/> (pieces, paint, badges, set, lens) into a slot: an empty slot gets a new look named "Appearance N", a filled one keeps its id and
    /// name and takes the copy (every loadout that wears it changes with it). <paramref name="wearer"/> (a custom loadout slot, or 0) then wears the slot. Returns the look's id,
    /// or null when the slot number is outside 1..<see cref="Count"/>.
    /// </summary>
    public static (CustomizationProfile Profile, string? Id) SaveInto(CustomizationProfile profile, string race, int slot, AppearanceProfile source, int wearer = 0)
    {
        if (slot is < 1 or > Count) return (profile, null);
        var layout = Layout(profile, race);
        string id;
        if (layout[slot - 1] is { } existing)
        {
            id = existing.Id;
            if (existing.Id != source.Id) profile = AppearanceEdits.ReplaceAppearance(profile, source with { Id = existing.Id, Race = race, Name = existing.Name });
        }
        else
        {
            id = Prefix(race) + slot;
            for (int n = 2; profile.Appearance(id) is not null; n++) id = $"{Prefix(race)}{slot}-{n}";   // another race's look holds the id (hand-edited file)
            string name = $"Appearance {slot}";
            for (int n = 2; profile.AppearancesFor(race).Any(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); n++) name = $"Appearance {slot} ({n})";
            var copy = source with { Id = id, Race = race, Name = name };
            // Kept in slot order in the file, so a look with an odd id cannot take this slot from the new one on the next read.
            var list = profile.Appearances.ToList();
            int at = list.FindIndex(a => a.Race == race && SlotOf(profile, race, a.Id) > slot);
            if (at < 0) list.Add(copy); else list.Insert(at, copy);
            profile = profile with { Appearances = list };
        }
        if (wearer > 0) profile = AppearanceEdits.Assign(profile, wearer, race, id);
        return (profile, id);
    }

    /// <summary>A new default look in an empty slot (null id when the slot is taken or out of range).</summary>
    public static (CustomizationProfile Profile, string? Id) NewIn(CustomizationProfile profile, string race, int slot) =>
        slot is < 1 or > Count || Layout(profile, race)[slot - 1] is not null ? (profile, null) : SaveInto(profile, race, slot, AppearanceProfile.Empty("", race, ""));

    /// <summary>Every custom loadout wears the look on <paramref name="race"/> (the other side's look of each loadout stays).</summary>
    public static CustomizationProfile ApplyToAll(CustomizationProfile profile, string race, string id) =>
        profile with { Classes = profile.Classes.Select(c => c with { Appearance = c.Appearance.With(race, id) }).ToList() };

    /// <summary>A loadout puts the look on (<paramref name="on"/>) or takes it off (back to the side's default look).</summary>
    public static CustomizationProfile SetWorn(CustomizationProfile profile, int loadout, string race, string id, bool on)
    {
        if (profile.ClassAt(loadout) is not { } c) return profile;
        if (on) return AppearanceEdits.Assign(profile, loadout, race, id);
        return c.Appearance.For(race) == id ? AppearanceEdits.Assign(profile, loadout, race, null) : profile;
    }
}
