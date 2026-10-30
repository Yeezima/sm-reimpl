using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SmContent;
using SmContent.Customization;

namespace SmClient.Customizer.Model;

/// <summary>An edit's result: the new profile and a one-line notice for the status line (null when nothing needs saying; the profile is unchanged when the edit was refused).</summary>
public readonly record struct EditResult(CustomizationProfile Profile, string? Notice, bool Changed);

/// <summary>
/// Pure edits of the custom classes (docs/customizer-design.md F4, F5): create, change class, choose items and perks, rename, copy, reset, clear.
/// Names are stored race-neutral (<see cref="ProfileWire.Neutral"/>); null is the class default.
/// </summary>
public static class LoadoutEdits
{
    /// <summary>The longest loadout name the creator accepts (the store keeps 24).</summary>
    public const int MaxNameLength = 20;

    public static string DefaultName(int slot) => $"Custom {slot}";

    /// <summary>A custom class in <paramref name="slot"/> with the role's defaults (no appearance chosen).</summary>
    public static CustomClass New(int slot, string role, CustomizationCatalog catalog, string? name = null) =>
        new(slot, name ?? DefaultName(slot), role, null, null, null, null, [null, null], AppearanceRefs.None);

    static CustomizationProfile Put(CustomizationProfile profile, CustomClass custom) =>
        profile with { Classes = profile.Classes.Where(c => c.Slot != custom.Slot).Append(custom).OrderBy(c => c.Slot).ToList() };

    static EditResult Done(CustomizationProfile profile, string? notice = null) => new(profile, notice, true);
    static EditResult Refused(CustomizationProfile profile, string notice) => new(profile, notice, false);

    /// <summary>Creates the class of <paramref name="role"/> in an empty slot (the slot becomes the open one; the caller selects it).</summary>
    public static EditResult Create(CustomizationProfile profile, int slot, string role, CustomizationCatalog catalog)
    {
        if (slot < 1 || slot > CustomizationProfile.MaxCustomClasses) return Refused(profile, "There is no such loadout slot.");
        if (profile.ClassAt(slot) is not null) return Refused(profile, "That slot is already in use.");
        return Done(Put(profile, New(slot, role, catalog)));
    }

    /// <summary>The first of the ten custom slots with no class in it, or 0 when all are used.</summary>
    public static int FirstEmptySlot(CustomizationProfile profile)
    {
        for (int slot = 1; slot <= CustomizationProfile.MaxCustomClasses; slot++) if (profile.ClassAt(slot) is null) return slot;
        return 0;
    }

    /// <summary>
    /// The DEFAULTS loadouts cannot be changed (they are the data's classes), so choosing an item or a perk on one makes an editable copy first: a custom class of the
    /// same role in the first empty slot, named after the class, every choice the class default (what the DEFAULTS loadout is), the open slot. Nothing is saved until the
    /// player saves; Revert drops it. <paramref name="makeActive"/>: the copy also becomes the selected loadout (the player was playing these defaults: what they
    /// change is what they join with). Refused with a notice when all ten slots are used. <paramref name="slot"/> is the slot used (0 when refused).
    /// </summary>
    public static EditResult CopyDefaults(CustomizationProfile profile, string role, string className, CustomizationCatalog catalog, bool makeActive, out int slot)
    {
        slot = FirstEmptySlot(profile);
        if (slot == 0) return Refused(profile, "All ten loadout slots are in use. Clear one to make an editable copy of the defaults.");
        var created = Create(profile, slot, role, catalog);
        var renamed = Rename(created.Profile, slot, className);
        var made = renamed.Changed ? renamed.Profile : created.Profile;
        if (makeActive) made = Select(made, Selection.CustomSlot(slot));
        return Done(made, $"Made CUSTOM {slot} from the defaults{(makeActive ? " (now active)" : "")}; SAVE keeps it.");
    }

    /// <summary>
    /// After a Revert or Discard dropped the copy <see cref="CopyDefaults"/> made in <paramref name="copySlot"/>: the DEFAULTS it came from (<paramref name="defaults"/>) when
    /// <paramref name="open"/> is still that now-empty slot, else <paramref name="open"/> unchanged (the player is elsewhere, or the copy was saved).
    /// </summary>
    public static Selection ReturnFromCopy(CustomizationProfile profile, Selection open, int copySlot, Selection defaults) =>
        open.Kind == SelectionKind.Custom && open.Slot == copySlot && profile.ClassAt(copySlot) is null ? defaults : open;

    /// <summary>Changes the role of a slot's class: items and perks go back to the new class's defaults; the name and looks stay.</summary>
    public static EditResult SetRole(CustomizationProfile profile, int slot, string role, CustomizationCatalog catalog)
    {
        if (profile.ClassAt(slot) is not { } current) return Refused(profile, "That slot is empty.");
        if (current.Role.Equals(role, StringComparison.OrdinalIgnoreCase)) return Refused(profile, "");
        return Done(Put(profile, New(slot, role, catalog, current.Name) with { Appearance = current.Appearance }), "Class changed: weapons, equipment and perks are back to the defaults.");
    }

    /// <summary>
    /// Chooses an item for a slot key ("primary", "pistol", "melee", "equipment") by neutral name; null is the class default. The name may be either side's
    /// (the creator lists Chaos names while it previews Chaos: "mp_melee_chain_axe", "mp_melee_2hand_power_mace"): a role offers an item when a class of either side
    /// lists it. A pistol that needs a perk the class does not carry is refused.
    /// </summary>
    public static EditResult SetItem(CustomizationProfile profile, int slot, string key, string? neutralName, CustomizationCatalog catalog)
    {
        if (profile.ClassAt(slot) is not { } c) return Refused(profile, "That slot is empty.");
        if (neutralName is not null && LoadoutRows.Options(catalog, c.Role, CustomizationCatalog.SpaceMarine) is { } options)
        {
            if (key == "second" && !PerkIn(c, options, SmSim.Loadouts.SecondWeaponPerk)) return Refused(profile, "Requires perk: Weapon Versatility");
            if (key == "second" && !c.Role.Equals("tactical", StringComparison.OrdinalIgnoreCase)) return Refused(profile, "Only the Tactical Marine carries a second primary.");
            if (catalog.Perks.Offered(c.Role, key is "primary" or "second" ? "ranged" : key, neutralName) is not { } offered) return Refused(profile, "That class does not offer it.");
            if (!offered.Selectable) return Refused(profile, "That item is fixed for the class.");
            if (offered.Item.RequiredPerk is { } need && !PerkIn(c, options, need))
                return Refused(profile, $"Requires perk: {catalog.Perks.Perk(need)?.DisplayName ?? need}");
        }
        var next = key switch
        {
            "primary" => c with { Primary = neutralName },
            "second" => c with { Second = neutralName },   // protocol 44 (Weapon Versatility)
            "pistol" => c with { Pistol = neutralName },
            "melee" => c with { Melee = neutralName },
            "equipment" => c with { Equipment = neutralName },
            _ => throw new ArgumentException($"Unknown slot key '{key}'.", nameof(key)),
        };
        return Done(Put(profile, next));
    }

    /// <summary>
    /// Puts a perk into perk slot <paramref name="index"/> (0 or 1; any perk of the class in either slot); null clears it (slot 1 returns to the class default).
    /// A perk already in the other slot is refused. Removing the perk a pistol needs (True Grit) resets that pistol with a notice.
    /// </summary>
    public static EditResult SetPerk(CustomizationProfile profile, int slot, int index, string? perk, CustomizationCatalog catalog)
    {
        if (profile.ClassAt(slot) is not { } c) return Refused(profile, "That slot is empty.");
        if (index is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(index));
        var options = LoadoutRows.Options(catalog, c.Role, CustomizationCatalog.SpaceMarine);
        if (perk is not null && options is not null && !options.Perks.Any(p => p.Name.Equals(perk, StringComparison.OrdinalIgnoreCase)))
            return Refused(profile, "That class does not offer this perk.");
        string? other = index == 0 ? c.Perk(1) : c.Perk(0) ?? options?.DefaultPerk?.Name;
        if (perk is not null && other is not null && other.Equals(perk, StringComparison.OrdinalIgnoreCase))
            return Refused(profile, $"Already chosen as perk {(index == 0 ? 2 : 1)}.");
        var perks = new List<string?> { c.Perk(0), c.Perk(1) };
        perks[index] = perk;
        string? notice = null;
        // Clearing slot 1 puts the class default back there; if slot 2 holds that default, it would sit in both: clear it too.
        if (index == 0 && perk is null && options?.DefaultPerk is { } defaultPerk && perks[1] is { } second && second.Equals(defaultPerk.Name, StringComparison.OrdinalIgnoreCase))
        {
            perks[1] = null;
            notice = "Perk 2 was the default perk and is cleared with it.";
        }
        var next = c with { Perks = perks };
        string need = PerkCatalog.BolterPistolPerk;
        bool had = PerkIn(c, options, need), has = PerkIn(next, options, need);
        if (had && !has && next.Pistol is { } pistol && options?.Pistol.Items.FirstOrDefault(i => ProfileWire.Neutral(i.Name).Equals(pistol, StringComparison.OrdinalIgnoreCase)) is { RequiredPerk: not null })
        {
            next = next with { Pistol = null };
            notice = $"{catalog.Perks.Perk(need)?.DisplayName ?? need} removed: the pistol is back to the default.";
        }
        // Protocol 44: without Weapon Versatility the second primary goes (the server would drop it anyway).
        if (next.Second is not null && !PerkIn(next, options, SmSim.Loadouts.SecondWeaponPerk))
        {
            next = next with { Second = null };
            notice = (notice is null ? "" : notice + " ") + "Weapon Versatility removed: no second primary.";
        }
        return Done(Put(profile, next), notice);
    }

    /// <summary>Whether the class carries the perk in either slot (slot 1 counts as the class default perk while it is unset).</summary>
    public static bool PerkIn(CustomClass c, ClassOptions? options, string perk) =>
        perk.Equals(c.Perk(0) ?? options?.DefaultPerk?.Name, StringComparison.OrdinalIgnoreCase) || perk.Equals(c.Perk(1), StringComparison.OrdinalIgnoreCase);

    /// <summary>Cleans a typed name (control characters out, trimmed, at most <see cref="MaxNameLength"/> characters); empty gives "Custom n".</summary>
    public static string SanitizeName(string? input, int slot)
    {
        var sb = new StringBuilder();
        foreach (var ch in input ?? "")
            if (!char.IsControl(ch)) sb.Append(ch);
        string text = sb.ToString().Trim();
        if (text.Length > MaxNameLength)
        {
            text = text[..MaxNameLength];
            if (char.IsHighSurrogate(text[^1])) text = text[..^1];
            text = text.TrimEnd();
        }
        return text.Length == 0 ? DefaultName(slot) : text;
    }

    public static EditResult Rename(CustomizationProfile profile, int slot, string? input)
    {
        if (profile.ClassAt(slot) is not { } c) return Refused(profile, "That slot is empty.");
        string name = SanitizeName(input, slot);
        return name == c.Name ? Refused(profile, "") : Done(Put(profile, c with { Name = name }));
    }

    /// <summary>Copies a slot over another (the caller has confirmed a replacement); the copy is named "<c>name (copy)</c>" and shares the looks.</summary>
    public static EditResult Copy(CustomizationProfile profile, int from, int to)
    {
        if (profile.ClassAt(from) is not { } c) return Refused(profile, "Nothing to copy.");
        if (from == to) return Refused(profile, "Choose another slot.");
        if (to < 1 || to > CustomizationProfile.MaxCustomClasses) return Refused(profile, "There is no such loadout slot.");
        string suffix = " (copy)";
        string baseName = c.Name.Length + suffix.Length > MaxNameLength ? c.Name[..(MaxNameLength - suffix.Length)].TrimEnd() : c.Name;
        return Done(Put(profile, c with { Slot = to, Name = baseName + suffix }), $"Copied to slot {to}.");
    }

    /// <summary>The class defaults for the slot's role: items and perks reset, name, role and looks kept.</summary>
    public static EditResult Reset(CustomizationProfile profile, int slot)
    {
        if (profile.ClassAt(slot) is not { } c) return Refused(profile, "That slot is empty.");
        return Done(Put(profile, c with { Primary = null, Pistol = null, Melee = null, Equipment = null, Second = null, Perks = [null, null] }), "Reset to the class defaults.");
    }

    /// <summary>Empties a slot. When it was the selected loadout, the role's DEFAULTS take its place.</summary>
    public static EditResult Clear(CustomizationProfile profile, int slot)
    {
        if (profile.ClassAt(slot) is not { } c) return Refused(profile, "That slot is already empty.");
        var next = profile with { Classes = profile.Classes.Where(x => x.Slot != slot).ToList() };
        if (profile.Selected.Kind == SelectionKind.Custom && profile.Selected.Slot == slot) next = next with { Selected = Selection.DefaultFor(c.Role) };
        return Done(next, $"Slot {slot} cleared.");
    }

    public static CustomizationProfile Select(CustomizationProfile profile, Selection selection) => profile with { Selected = selection };

    /// <summary>Whether a selection can be played: a DEFAULTS role, or a custom slot in use.</summary>
    public static bool IsValid(CustomizationProfile profile, Selection selection) =>
        selection.Kind == SelectionKind.Default ? selection.Role is { Length: > 0 } : profile.ClassAt(selection.Slot) is not null;

    /// <summary>The selection that is used: the selected one, falling back to slot 1, then the first role's DEFAULTS (design F3). <paramref name="fellBack"/> says it fell back.</summary>
    public static Selection Active(CustomizationProfile profile, CustomizationCatalog catalog, out bool fellBack)
    {
        fellBack = false;
        if (IsValid(profile, profile.Selected)) return profile.Selected;
        fellBack = true;
        if (profile.ClassAt(1) is not null) return Selection.CustomSlot(1);
        return Selection.DefaultFor(ProfileStore.FirstRole(catalog));
    }
}
