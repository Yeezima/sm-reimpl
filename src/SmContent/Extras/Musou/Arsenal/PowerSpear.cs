using SmSim;
using SmSim.Extras.Musou.Arsenal;

namespace SmContent.Extras.Musou.Arsenal;

/// <summary>
/// The power spear (W3; docs/new-weapons-design.md section 6; the owner approved it 2026-11-10): a two-handed melee weapon for the Musou Assault Marine, on the thunder hammer's graph map and clips at
/// <see cref="SpearRules.Tempo"/>. Added content, Musou only; the name is ours (no wargear file in the install). The combo is the Assault's hammer combo slowed and lightened
/// (<see cref="SpearRules.FromHammer"/>), appended at the end of the Assault's melee choices (no existing index moves) and to the customizer's melee row. The animations are the hammer's as stand-ins (the map, and the
/// alias for the clip lists); the approved custom clips come later through the client's animation seam.
/// </summary>
public static partial class Arsenal
{
    const string HammerSuffix = "_mp_melee_2hand_thunder_hammer";

    static bool HasHammer(IEnumerable<string> names) => names.Any(n => n.EndsWith(HammerSuffix, StringComparison.OrdinalIgnoreCase));

    static partial void AppendPowerSpear(ref SimConfig config, string gameDir, List<string> notes)
    {
        // The Assault Marines that carry the hammer; none in a config without one (a test's, a faithful read), so nothing is added.
        var classes = config.Classes;
        List<ClassLoadout>? changed = null;
        for (int i = 0; i < classes.Count; i++)
        {
            var c = classes[i];
            if (c.Role != "assault" || c.Side != Team.SpaceMarines || c.MeleeChoices.Any(m => ArsenalRules.PowerSpear.Equals(m.Wargear, StringComparison.OrdinalIgnoreCase))) continue;
            var hammer = c.MeleeChoices.FirstOrDefault(m => m.Wargear.EndsWith(HammerSuffix, StringComparison.OrdinalIgnoreCase));
            if (hammer is null) continue;
            changed ??= [.. classes];
            changed[i] = c with { MeleeChoices = [.. c.MeleeChoices, SpearRules.FromHammer(hammer, ArsenalRules.PowerSpear)] };
        }
        if (changed is null) return;
        config = config with { Classes = changed };
        notes.Add($"musou arsenal: the power spear ({ArsenalRules.PowerSpear}) added to {changed.Count(c => c.MeleeChoices.Any(m => ArsenalRules.PowerSpear.Equals(m.Wargear, StringComparison.OrdinalIgnoreCase)))} class(es), on the thunder hammer's combo at {SpearRules.Tempo}x");
    }

    /// <summary>The spear's graph map: the thunder hammer's, named for the spear, at <see cref="SpearRules.Tempo"/> (the client poses the clips at the same rate).</summary>
    static partial void PowerSpearMap(string wargear, ref MeleeTuning.WeaponMap? map)
    {
        if (map is null && ArsenalRules.PowerSpear.Equals(wargear, StringComparison.OrdinalIgnoreCase))
            map = MeleeTuning.ThunderHammer with { Wargear = wargear, ClipSpeed = SpearRules.Tempo };
    }

    /// <summary>The customizer row: after the hammer in the Assault's melee slot.</summary>
    static partial void PowerSpearItems(ref PerkCatalog catalog)
    {
        var item = new LoadoutItem(ArsenalRules.PowerSpear, "melee", "Power Spear",
            "A two-handed spear with a blue power blade. Slow, very wide sweeps that clear the crowd around you, and a jump-pack dive that impales the Ork where you land.",
            0, true, null, null);
        catalog = catalog.WithClasses(c => c.Role == "assault" && HasHammer(c.Melee.Items.Select(i => i.Name)) && !c.Melee.Items.Any(i => i.Name == item.Name)
            ? c with { Melee = c.Melee with { Items = [.. c.Melee.Items, item] } }
            : c);
    }

    /// <summary>The spear borrows the thunder hammer's animation names (hip fire, sprint, flinch, stance).</summary>
    static partial void PowerSpearAlias(ref string wargear)
    {
        if (ArsenalRules.PowerSpear.Equals(wargear, StringComparison.OrdinalIgnoreCase)) wargear = "sm_mp_melee_2hand_thunder_hammer";
    }
}
