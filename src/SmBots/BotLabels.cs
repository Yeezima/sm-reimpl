using SmContent;
using SmSim;

namespace SmBots;

/// <summary>The names the bot's log and the Godot client's HUD give weapons and points (one copy: the client's NetworkView.WeaponLabel and PointLetter call these).</summary>
public static class BotLabels
{
    /// <summary>A control point's letter: A, B, C, ... (the index from 26 on).</summary>
    public static string PointLetter(int index) => index < 26 ? ((char)('A' + index)).ToString() : index.ToString();

    /// <summary>A wargear name without its race and category prefix: "sm_mp_pistol_bolt_pistol" gives "bolt pistol".</summary>
    public static string Weapon(string wargear)
    {
        // Ork O5 review: the Ork wargear's own names (the AI files carry no MP display name; ours) instead of "ork coop boy 2hand shoota rapid".
        if (wargear.Equals(OrkClass.Shoota, StringComparison.OrdinalIgnoreCase)) return "shoota";
        if (wargear.Equals(OrkClass.Slugga, StringComparison.OrdinalIgnoreCase)) return "slugga";
        if (wargear.Equals(OrkClass.Choppa, StringComparison.OrdinalIgnoreCase)) return "choppa";
        if (wargear.Equals(OrkClass.Stikkbomb, StringComparison.OrdinalIgnoreCase)) return "stikkbomb";
        // Weapons C: the Devastator's and the Havoc's melee is their core ability's kick and stomp.
        if (wargear.EndsWith("_core_abil_heavy_evade", StringComparison.OrdinalIgnoreCase)) return "kick / stomp";
        string label = wargear;
        int mp = label.IndexOf("_mp_", StringComparison.OrdinalIgnoreCase);
        if (mp >= 0) label = label[(mp + 4)..];
        foreach (var category in new[] { "ranged_", "pistol_", "exotic_", "melee_", "eq_" })
            if (label.StartsWith(category, StringComparison.OrdinalIgnoreCase)) { label = label[category.Length..]; break; }
        return label.Replace('_', ' ');
    }

    /// <summary>The HUD name of a weapon; the single-weapon fallback ("default") is the bolter's values.</summary>
    public static string Weapon(WeaponSpec? weapon) => weapon is null ? "no weapon" : weapon.Name == "default" ? "bolter" : Weapon(weapon.Name);

    /// <summary>The HUD name of a thrown item (M37: "blind grenade"; the files call it krak).</summary>
    public static string Equipment(ThrownSpec? item) => item is null ? "none" : item.PlayerName ?? Weapon(item.Name);

    /// <summary>Thrown item state for the log: "frag grenade 1/2 cooldown 0.4 s throwing 0.10 s;" (the client's ThrowText).</summary>
    public static string Throw(SimConfig config, PlayerState me) => Loadouts.Equipment(config, me) is { } item
        ? $"{Equipment(item)} {me.Grenades}/{item.Count}{(me.GrenadeCooldown > 0f ? $" cooldown {me.GrenadeCooldown:F1} s" : "")}{(me.Throwing ? $" throwing {me.ThrowTime:F2} s" : "")};"
        : "no equipment;";
}
