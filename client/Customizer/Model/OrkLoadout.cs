using System;
using System.Collections.Generic;
using System.Linq;
using SmContent;
using SmContent.Customization;
using SmSim;

namespace SmClient.Customizer.Model;

/// <summary>
/// The Ork Boy in the customizer (ui/customizer-ork, Ork O9; docs/ork-faction-design.md section 7; added content, offered only with the Orks: <see cref="ContentGate"/>). Engine-free.
/// The catalogue reads only the original's two races, so the Ork's class options are made here: his four items, each the only one of its slot (shown FIXED), and the Tactical
/// Marine's perks (<see cref="OrkClass.ClassOf"/>). His custom loadouts are stored with the role <see cref="OrkClass.Role"/>; his look (<see cref="CustomizationProfile.OrkLook"/>)
/// is one choice of the profile, shown instead of the armour and colour editors (the Ork is unpainted and has no armour pieces).
/// </summary>
public static class OrkLoadout
{
    public const string Role = OrkClass.Role;
    /// <summary>The faction / race name the screens use for the Orks (<see cref="ProfileWire.Ork"/>).</summary>
    public const string Race = ProfileWire.Ork;
    public const string ClassName = "Ork Boy";

    public static bool IsOrk(string? role) => role is not null && role.Equals(Role, StringComparison.OrdinalIgnoreCase);

    /// <summary>The roles the screens offer in a content profile: the catalogue's three, then the Ork Boy when the profile has the Orks.</summary>
    public static IReadOnlyList<string> Roles(CustomizationCatalog catalog, ContentExtras extras) =>
        ContentGate.OffersOrks(extras) ? [.. ProfileStore.Roles(catalog), Role] : ProfileStore.Roles(catalog);

    // The player-facing names (ours: the HUD and kill feed say shoota, slugga, choppa and stikkbomb since Ork O5; the files carry no display text for them).
    static readonly LoadoutItem Shoota = new(OrkClass.Shoota, "ranged", "Shoota", "The Ork's two-handed gun.", 1, true, null, null);
    static readonly LoadoutItem Slugga = new(OrkClass.Slugga, "pistol", "Slugga", "The Ork's pistol.", 1, true, null, null);
    static readonly LoadoutItem Choppa = new(OrkClass.Choppa, "melee", "Choppa", "The Ork's cleaver; his charge is its sprint attack.", 1, true, null, null);
    static readonly LoadoutItem Stikkbomb = new(OrkClass.Stikkbomb, "equipment", "Stikkbombs", "The Ork's grenade.", 1, true, null, null);

    /// <summary>
    /// The Ork Boy's class options: the fixed shoota, slugga, choppa and stikkbombs, and the Tactical Marine's perks and default perk (null when the catalogue has no Tactical
    /// Marine). The perks are the same records the Tactical's rows use, so their names, descriptions and simulation states agree.
    /// </summary>
    public static ClassOptions? Options(CustomizationCatalog catalog)
    {
        if (catalog.Perks.Class(OrkClass.Tactical) is not { } tactical) return null;
        static SlotChoices Fixed(string slot, LoadoutItem item) => new(slot, [item], false);
        return new ClassOptions(OrkClass.Key, Role, Race, ClassName,
            "Added content (Extended): an Ork Boy who fights for either side, with a shoota, a slugga, a choppa and stikkbombs, a side hop instead of the roll, and the Tactical Marine's perks.",
            Fixed("ranged", Shoota), Fixed("pistol", Slugga), Fixed("melee", Choppa), Fixed("equipment", Stikkbomb), tactical.Perks, tactical.DefaultPerk);
    }

    // ---- the look ----

    /// <summary>The choices of the look step in order: automatic (null) first, then every look by its index (<see cref="OrkLookRules.Names"/>).</summary>
    public static IReadOnlyList<int?> LookChoices { get; } = [null, .. Enumerable.Range(0, OrkLookRules.Names.Count).Select(i => (int?)i)];

    /// <summary>The title of a look (ours: the install names only the meshes, <c>boy_ranged</c> ...).</summary>
    public static string LookTitle(int? look) => look switch
    {
        null => "Automatic",
        0 => "Shoota Boy",
        1 => "Choppa Boy",
        2 => "Shoota Boy, kitted out",
        3 => "Choppa Boy, kitted out",
        4 => "'Ard Boy",
        _ => "Unknown look",
    };

    /// <summary>One line for the look's card.</summary>
    public static string LookDescription(int? look) => look is { } i && i >= 0 && i < OrkLookRules.Names.Count
        ? $"The Exterminatus boy's own model ({OrkLookRules.Names[i]})." + (i >= OrkLookRules.AutomaticLooks ? " Never chosen automatically." : "")
        : $"Every player sees one of the first {OrkLookRules.AutomaticLooks} looks, picked by your player number, so the Orks in a match look different.";

    /// <summary>The look the preview draws for a choice: the look itself, or the first look for automatic (the real one depends on the player number in the match).</summary>
    public static int PreviewLook(int? look) => look is { } i && i >= 0 && i < OrkLookRules.Names.Count ? i : 0;

    /// <summary>Sets the profile's Ork look (null: automatic); an index outside the looks is refused.</summary>
    public static EditResult SetLook(CustomizationProfile profile, int? look)
    {
        if (look is { } i && (i < 0 || i >= OrkLookRules.Names.Count)) return new EditResult(profile, "There is no such Ork look.", false);
        if (profile.OrkLook == look) return new EditResult(profile, null, false);
        return new EditResult(profile with { OrkLook = look }, $"Ork look: {LookTitle(look)}.", true);
    }
}
