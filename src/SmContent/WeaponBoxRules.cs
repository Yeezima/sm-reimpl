namespace SmContent;

/// <summary>What a weapon's bar in the box shows: the spent part of the clip, the heat (the bar drains as the weapon heats: footage), or nothing.</summary>
public enum WeaponBoxBar { None, Spent, Heat }

/// <summary>
/// One weapon family's display in the HUD movie's weapon box (ui/hud-fidelity): the instance paths under <c>weaponInfo_mc</c> it shows (every other optional display of the box is
/// hidden), the bar sprite whose frame carries the clip or the heat, and the text field that carries the count.
/// </summary>
public sealed record WeaponBoxSpec(string Family, IReadOnlyList<string> Show, string? Bar, WeaponBoxBar Value, string? Count);

/// <summary>
/// Which of the weapon box's displays (<c>Hud.gfx</c> <c>weaponInfo_Slide/weaponInfo_container/weaponInfo_mc</c>) a weapon shows (ui/hud-fidelity): the instance names are the
/// movie's (<c>ammo_count_mc</c>, <c>ammo_count_bolt_pistol_mc</c>, <c>ammo_plasmagun_mc</c>, <c>ammo_lascannon_mc</c> ...); which weapon type picks which display is an
/// <b>interpretation</b> of the names (the script that picks them is not read). The bars' frames: frame 0 is the full bar, the last frames the empty one (every bar clip of the box);
/// the heat bar drains as the weapon heats (the reference footage's plasma gun: the bar shortens while it fires and refills as it cools).
/// </summary>
public static class WeaponBoxRules
{
    /// <summary>The optional displays the box hides unless a family shows them (paths under <c>weaponInfo_mc</c>).</summary>
    public static readonly IReadOnlyList<string> Optional =
    [
        "infinity_mc", "Ammo_Stalker_mc", "loadout_grenaderounddisplay_mc", "ammo_meltagun_mc", "ammo_stormbolter_mc", "ammo_autocannon_mc", "ammo_lascannon_mc",
        "ammo_plasmagun_mc", "ammo_plasmapistol_mc", "ammo_mc",
        "ammo_mc/ammo_count_mc", "ammo_mc/ammo_count_bolt_pistol_mc", "ammo_mc/ammo_count_bolt_pistol_16_mc", "ammo_mc/ammo_count_heavy_mc", "ammo_mc/ammo_txt",
        "ammo_mc/generic_ammo_gone", "ammo_mc/dead_rounds_mc", "ammo_mc/ammo_count_red_mc",
        "ammo_plasmagun_mc/ammo_plasmagun_mc", "ammo_plasmagun_mc/ammo_plasmagun_chaos_mc", "ammo_plasmapistol_mc/ammo_plasmagun_chaos_mc",
    ];

    /// <summary>The display of a weapon by its <c>ui_data/weapon_type</c> (the wargear name when missing) and race (the Chaos plasma bar is the movie's purple one); null: none.</summary>
    public static WeaponBoxSpec? For(string wargear, string? weaponType, bool chaos)
    {
        string type = (weaponType is { Length: > 0 } ? weaponType : HudRules.TypeFromName(wargear)).ToLowerInvariant();
        return type switch
        {
            "bolter" => new("bolter", ["ammo_mc", "ammo_mc/ammo_count_mc", "ammo_mc/ammo_txt"], "ammo_mc/ammo_count_mc", WeaponBoxBar.Spent, "ammo_mc/ammo_txt"),
            "bolt_pistol" => new("bolt_pistol", ["ammo_mc", "ammo_mc/ammo_count_bolt_pistol_mc", "ammo_mc/ammo_txt"], "ammo_mc/ammo_count_bolt_pistol_mc", WeaponBoxBar.Spent, "ammo_mc/ammo_txt"),
            "storm_bolter" => new("storm_bolter", ["ammo_stormbolter_mc", "ammo_mc", "ammo_mc/ammo_txt"], "ammo_stormbolter_mc/ammo_bar", WeaponBoxBar.Spent, "ammo_mc/ammo_txt"),
            "heavy_bolter" => new("heavy_bolter", ["ammo_autocannon_mc", "infinity_mc"], "ammo_autocannon_mc/ammo_bar", WeaponBoxBar.Heat, null),
            "plasma_gun" or "plasma_cannon" => new(type, chaos ? ["ammo_plasmagun_mc", "ammo_plasmagun_mc/ammo_plasmagun_chaos_mc"] : ["ammo_plasmagun_mc", "ammo_plasmagun_mc/ammo_plasmagun_mc"],
                chaos ? "ammo_plasmagun_mc/ammo_plasmagun_chaos_mc" : "ammo_plasmagun_mc/ammo_plasmagun_mc", WeaponBoxBar.Heat, "ammo_plasmagun_mc/ammo_count"),
            "plasma_pistol" => new("plasma_pistol", chaos ? ["ammo_plasmapistol_mc", "ammo_plasmapistol_mc/ammo_plasmagun_chaos_mc"] : ["ammo_plasmapistol_mc"],
                chaos ? "ammo_plasmapistol_mc/ammo_plasmagun_chaos_mc" : "ammo_plasmapistol_mc/ammo_bar", WeaponBoxBar.Heat, "ammo_plasmapistol_mc/ammo_count"),
            "laz_cannon" or "lascannon" => new("lascannon", ["ammo_lascannon_mc"], "ammo_lascannon_mc/ammo_bar", WeaponBoxBar.Spent, "ammo_lascannon_mc/ammo_count"),
            "melta" => new("melta", ["ammo_meltagun_mc"], "ammo_meltagun_mc/ammo_bar", WeaponBoxBar.Spent, "ammo_meltagun_mc/ammo_count"),
            "stalker" => new("stalker", ["Ammo_Stalker_mc"], null, WeaponBoxBar.None, "Ammo_Stalker_mc/ammo_count"),
            "grenade_launcher" => new("grenade_launcher", ["loadout_grenaderounddisplay_mc", "ammo_mc", "ammo_mc/ammo_txt"], null, WeaponBoxBar.None, "ammo_mc/ammo_txt"),
            _ => null,
        };
    }

    /// <summary>A bar clip's frame for a fill (0 empty .. 1 full) over <paramref name="frames"/> frames: frame 0 full, frame <paramref name="frames"/> - 2 empty (the last one is the empty flash).</summary>
    public static int BarFrame(float full, int frames)
    {
        int last = Math.Max(0, frames - 2);
        float f = float.IsFinite(full) ? Math.Clamp(full, 0f, 1f) : 0f;
        return (int)MathF.Round((1f - f) * last);
    }

    /// <summary>The grenade launcher display's round slots (<c>loadout_grenaderounddisplay_mc/round00</c> .. <c>round17</c>: the movie's eighteen, the launcher's clip 1 plus reserve 17).</summary>
    public const int LauncherSlots = 18;

    /// <summary>The launcher display's path prefix under the box's <c>weaponInfo_mc</c>.</summary>
    public const string LauncherDisplay = "loadout_grenaderounddisplay_mc";

    /// <summary>The slot a path of the launcher display names (<c>loadout_grenaderounddisplay_mc/round07</c> is 7), or null for any other path.</summary>
    public static int? LauncherRoundIndex(string path)
    {
        const string prefix = LauncherDisplay + "/round";
        if (!path.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var rest = path.AsSpan(prefix.Length);
        return rest.Length is >= 1 and <= 2 && int.TryParse(rest, out int i) && i >= 0 ? i : null;
    }

    /// <summary>
    /// Which launcher rounds are shown for the rounds left (clip plus reserve): the slots below the count, the others hidden (their dark underlays stay as placed). <b>Interpretation</b>:
    /// the movie names the slots and the script that fills them is not read, so which slot empties first is ours (the highest).
    /// </summary>
    public static bool LauncherRoundLoaded(int index, int roundsLeft) => index >= 0 && index < Math.Min(roundsLeft, LauncherSlots);

    /// <summary>
    /// The stalker bolter's rounds (<c>round0_mc</c> .. <c>round9_mc</c>): which are shown loaded for a clip (one each; a clip of more than ten fills them in shares, ours).
    /// </summary>
    public static bool RoundLoaded(int index, int clip, int clipMax)
    {
        if (clipMax <= 0) return false;
        int slots = Math.Min(10, clipMax);
        int loaded = clipMax <= 10 ? clip : (int)Math.Ceiling(clip * 10.0 / clipMax);
        return index < slots && index < loaded;
    }
}
