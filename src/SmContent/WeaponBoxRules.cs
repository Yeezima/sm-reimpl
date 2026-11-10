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

    /// <summary>
    /// The display of a weapon by its <c>ui_data/weapon_type</c> (the wargear name when missing) and race (the Chaos plasma bar is the movie's purple one); null: none.
    /// The same spec, and so the same <see cref="WeaponBoxSpec.Show"/> list, on every call for one display (fix/fx-hud-leaks, polish item 30: the HUD asks every frame, and a
    /// new spec a call was a new list a frame for its show-set cache).
    /// </summary>
    public static WeaponBoxSpec? For(string wargear, string? weaponType, bool chaos)
    {
        string type = (weaponType is { Length: > 0 } ? weaponType : HudRules.TypeFromName(wargear)).ToLowerInvariant();
        return type switch
        {
            "bolter" => Bolter,
            "bolt_pistol" => BoltPistol,
            "storm_bolter" => StormBolter,
            "heavy_bolter" => HeavyBolter,
            "plasma_gun" => chaos ? PlasmaGunChaos : PlasmaGun,
            "plasma_cannon" => chaos ? PlasmaCannonChaos : PlasmaCannon,
            "plasma_pistol" => chaos ? PlasmaPistolChaos : PlasmaPistol,
            "laz_cannon" or "lascannon" => Lascannon,
            "melta" => Melta,
            "stalker" => Stalker,
            "grenade_launcher" => GrenadeLauncher,
            _ => null,
        };
    }

    // The displays, built once (fix/fx-hud-leaks; the paths and bars as before).
    static readonly WeaponBoxSpec
        Bolter = new("bolter", ["ammo_mc", "ammo_mc/ammo_count_mc", "ammo_mc/ammo_txt"], "ammo_mc/ammo_count_mc", WeaponBoxBar.Spent, "ammo_mc/ammo_txt"),
        BoltPistol = new("bolt_pistol", ["ammo_mc", "ammo_mc/ammo_count_bolt_pistol_mc", "ammo_mc/ammo_txt"], "ammo_mc/ammo_count_bolt_pistol_mc", WeaponBoxBar.Spent, "ammo_mc/ammo_txt"),
        StormBolter = new("storm_bolter", ["ammo_stormbolter_mc", "ammo_mc", "ammo_mc/ammo_txt"], "ammo_stormbolter_mc/ammo_bar", WeaponBoxBar.Spent, "ammo_mc/ammo_txt"),
        // fix/pvp-deploy-spawn (the owner, 2026-11-06: "heavy bolter currently has infinite ammo. It should cap at 400"): the rounds left in the bolter's count field instead of the
        // movie's endless sign (infinity_mc), which read as unlimited ammunition; the simulation always spent the data's 400 a life (no reserve).
        HeavyBolter = new("heavy_bolter", ["ammo_autocannon_mc", "ammo_mc", "ammo_mc/ammo_txt"], "ammo_autocannon_mc/ammo_bar", WeaponBoxBar.Heat, "ammo_mc/ammo_txt"),
        PlasmaGun = Plasma("plasma_gun", chaos: false), PlasmaGunChaos = Plasma("plasma_gun", chaos: true),
        PlasmaCannon = Plasma("plasma_cannon", chaos: false), PlasmaCannonChaos = Plasma("plasma_cannon", chaos: true),
        PlasmaPistol = new("plasma_pistol", ["ammo_plasmapistol_mc"], "ammo_plasmapistol_mc/ammo_bar", WeaponBoxBar.Heat, "ammo_plasmapistol_mc/ammo_count"),
        PlasmaPistolChaos = new("plasma_pistol", ["ammo_plasmapistol_mc", "ammo_plasmapistol_mc/ammo_plasmagun_chaos_mc"], "ammo_plasmapistol_mc/ammo_plasmagun_chaos_mc", WeaponBoxBar.Heat,
            "ammo_plasmapistol_mc/ammo_count"),
        Lascannon = new("lascannon", ["ammo_lascannon_mc"], "ammo_lascannon_mc/ammo_bar", WeaponBoxBar.Spent, "ammo_lascannon_mc/ammo_count"),
        Melta = new("melta", ["ammo_meltagun_mc"], "ammo_meltagun_mc/ammo_bar", WeaponBoxBar.Spent, "ammo_meltagun_mc/ammo_count"),
        Stalker = new("stalker", ["Ammo_Stalker_mc"], null, WeaponBoxBar.None, "Ammo_Stalker_mc/ammo_count"),
        GrenadeLauncher = new("grenade_launcher", ["loadout_grenaderounddisplay_mc", "ammo_mc", "ammo_mc/ammo_txt"], null, WeaponBoxBar.None, "ammo_mc/ammo_txt");

    /// <summary>The plasma gun's and plasma cannon's display (the movie's plasma gun bar, purple for Chaos).</summary>
    static WeaponBoxSpec Plasma(string family, bool chaos) =>
        new(family, chaos ? ["ammo_plasmagun_mc", "ammo_plasmagun_mc/ammo_plasmagun_chaos_mc"] : ["ammo_plasmagun_mc", "ammo_plasmagun_mc/ammo_plasmagun_mc"],
            chaos ? "ammo_plasmagun_mc/ammo_plasmagun_chaos_mc" : "ammo_plasmagun_mc/ammo_plasmagun_mc", WeaponBoxBar.Heat, "ammo_plasmagun_mc/ammo_count");

    /// <summary>A bar clip's frame for a fill (0 empty .. 1 full) over <paramref name="frames"/> frames: frame 0 full, frame <paramref name="frames"/> - 2 empty (the last one is the empty flash).</summary>
    public static int BarFrame(float full, int frames)
    {
        int last = Math.Max(0, frames - 2);
        float f = float.IsFinite(full) ? Math.Clamp(full, 0f, 1f) : 0f;
        return (int)MathF.Round((1f - f) * last);
    }

    /// <summary>
    /// The frame of a clip's tick strip with no round left (fix/bolter-hud-last-round, owner: the last round stayed white after its shot). The bolter strip's mask stops moving
    /// at frame 95 and keeps its first tick white through <paramref name="frames"/> - 2, which <see cref="BarFrame"/> picks for an empty bar; only the last frame (the mask gone) shows every
    /// tick spent (red), as rendered in game. Used for the clip (Spent) bars only, not the heat bars.
    /// </summary>
    public static int EmptyBarFrame(int frames) => Math.Max(0, frames - 1);

    /// <summary>True when a clip display has no round loaded (a clip size known, none left).</summary>
    public static bool ClipEmpty(int clip, int clipMax) => clipMax > 0 && clip <= 0;

    /// <summary>The clip strips whose last frame is checked in game as "every tick spent" (lead review, 2026-11-08: only the bolter strip was checked, which the bolter and the relic
    /// bolter share; the storm bolter, bolt pistol, lascannon and melta strips keep <see cref="BarFrame"/> until their last frames are checked).</summary>
    public static bool UsesEmptyBarFrame(string family) => family == "bolter";

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

/// <summary>
/// The HUD's weapon box show sets: a display's <see cref="WeaponBoxSpec.Show"/> paths as a set (the per-path skip test of every frame), built once per distinct list of paths and
/// keyed by the paths' values, not by the list's identity (fix/fx-hud-leaks, polish item 30: keyed by identity, a spec built anew each frame added one set a frame for the whole
/// match). The lookup allocates nothing; a new set is made only for paths not seen before. Engine-free so the bound is tested; not thread-safe (the HUD's thread only).
/// </summary>
public sealed class WeaponBoxShowSets
{
    readonly Dictionary<IReadOnlyList<string>, HashSet<string>> _sets = new(PathsComparer.Instance);

    /// <summary>The sets made so far (one per distinct list of paths).</summary>
    public int Count => _sets.Count;

    /// <summary>The set of <paramref name="show"/>'s paths (ordinal), the same instance for any list of the same paths in the same order.</summary>
    public IReadOnlySet<string> Get(IReadOnlyList<string> show)
    {
        if (_sets.TryGetValue(show, out var set)) return set;
        set = new HashSet<string>(show, StringComparer.Ordinal);
        _sets[show.ToArray()] = set;   // keyed by a copy: a caller's list changed later cannot move the key
        return set;
    }

    /// <summary>Two path lists are equal when they hold the same paths in the same order (ordinal).</summary>
    sealed class PathsComparer : IEqualityComparer<IReadOnlyList<string>>
    {
        public static readonly PathsComparer Instance = new();

        public bool Equals(IReadOnlyList<string>? a, IReadOnlyList<string>? b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a is null || b is null || a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++) if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
            return true;
        }

        public int GetHashCode(IReadOnlyList<string> list)
        {
            var hash = new HashCode();
            for (int i = 0; i < list.Count; i++) hash.Add(list[i], StringComparer.Ordinal);
            return hash.ToHashCode();
        }
    }
}
