using SmSim.Extras.Musou.Campaign;

namespace SmSim.Extras.Warfront.Chaos;

/// <summary>Why a detonation does or does not close a breach.</summary>
public enum WarfrontBreachWhy : byte
{
    /// <summary>A qualifying explosive (it counts).</summary>
    Explosive = 0,
    /// <summary>A weapon or item of the table that is not a qualifying explosive (a bolter, a melta gun, a heavy weapon other than the plasma cannon...).</summary>
    NotAnExplosive = 1,
    /// <summary>A blind grenade (its blast only blinds; in this game's data the "krak" file is the blind grenade).</summary>
    Blind = 2,
    /// <summary>Combat stimulants (no blast).</summary>
    Stim = 3,
    /// <summary>No player owns it (an enemy's, a source without a player): owner 0 or below.</summary>
    NotAPlayer = 4,
    /// <summary>A retainer's or an AI Marine's (the owner: "humans only").</summary>
    Puppet = 5,
    /// <summary>A key the table does not list: excluded, and logged once.</summary>
    Unknown = 6,
    /// <summary>The index names nothing in the config.</summary>
    BadIndex = 7,
}

/// <summary>A detonation's verdict: whether it closes a breach, why, and the wargear key it was judged by (null when none).</summary>
public readonly record struct BreachHit(bool Counts, WarfrontBreachWhy Why, string? Key);

/// <summary>
/// Which detonations close a warp breach (Chaos Warfront C4; owner 2026-11-25: humans only, the plasma cannon counts). A pure table keyed by the wargear key of <see cref="SimConfig.Weapons"/> or
/// <see cref="SimConfig.Thrown"/>: frag grenades, melta bombs, the demolition charge (the Remote Detonators; its centre's detonation only, the ring spots are not detonations), the grenade launcher
/// (its remote detonation too), any rocket or missile weapon, and the plasma cannon (every shot) count; blind grenades, stims and every other weapon do not. A key the table does not list is
/// excluded and logged once, so nothing is excluded silently. Bolt and autocannon splash, the artillery, the drop pod and the orbital strike never make a detonation at all (they are
/// <c>MusouBlast</c>s and shells), so they never reach this table.
/// </summary>
public sealed class WarfrontBreachFilter
{
    // The base keys (after the sm_mp_ / csm_mp_ prefix) of every MP weapon and item in docs/customizer-and-perks-data.md section 1.1 and Musou's added arsenal: true counts, false does not.
    static readonly (string Key, bool Counts)[] BaseKeys =
    [
        // qualifying explosives
        ("eq_frag_grenade", true), ("eq_melta_bomb", true), ("eq_demolition_charge", true), ("ranged_grenade_launcher", true), ("exotic_plasma_cannon", true),
        // the "krak" file is the blind grenade (owner 2026-10-28): a blind one is excluded by its blast below; a damaging krak would count
        ("eq_krak_grenade", true),
        // equipment that is no explosive
        ("eq_stimpack", false), ("eq_targeter", false), ("eq_iron_halo", false),
        // every other gun
        ("ranged_bolter", false), ("ranged_bolter_gold", false), ("ranged_sniper_rifle", false), ("ranged_plasma_gun", false), ("ranged_melta", false), ("ranged_storm_bolter", false),
        ("ranged_lasgun_ai", false), ("exotic_heavy_bolter", false), ("exotic_las_cannon", false), ("exotic_auto_cannon", false),
        ("pistol_bolt_pistol", false), ("pistol_bolt_pistol_devastator", false), ("pistol_plasma_pistol", false), ("pistol_bolter", false), ("pistol_bolter_gold", false),
        // melee (no detonation of their own; listed so the census is complete)
        ("melee_chainsword", false), ("melee_chainsword_gold", false), ("melee_powersword", false), ("melee_power_axe", false), ("melee_chain_axe", false),
        ("melee_2hand_thunder_hammer", false), ("melee_2hand_power_mace", false), ("melee_combat_knife", false), ("melee_lightning_claw", false), ("melee_power_spear", false),
    ];

    // Keys outside the two MP prefixes: the plan's short names and the Dreadnought's wargear (Musou Mode's Dreadnought may deploy in Warfront).
    static readonly (string Key, bool Counts)[] OtherKeys =
    [
        ("eq_frag_grenade", true), ("krakgrenades", true), ("mp_ranged_grenade_launcher", true), ("plasma_cannon", true), ("mp_exotic_plasma_cannon", true),
        ("dread_assault_cannon", false), ("dread_csm_assault_cannon", false), ("dread_melta", false), ("dread_melee_default", false), ("dread_buff", false), ("dread_sprint", false),
    ];

    static readonly Dictionary<string, bool> Table = Build();

    static Dictionary<string, bool> Build()
    {
        var t = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, counts) in BaseKeys) { t["sm_mp_" + key] = counts; t["csm_mp_" + key] = counts; }
        foreach (var (key, counts) in OtherKeys) t[key] = counts;
        return t;
    }

    /// <summary>Whether <paramref name="key"/> is a rocket or missile weapon (the plan's "any rocket weapon key containing rocket"; no MP class carries one today).</summary>
    public static bool IsRocket(string key) => key.Contains("rocket", StringComparison.OrdinalIgnoreCase) || key.Contains("missile", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the table decides <paramref name="key"/> (listed, or a rocket by its name).</summary>
    public static bool IsListed(string key) => Table.ContainsKey(key) || IsRocket(key);

    /// <summary>Every weapon and thrown key of <paramref name="config"/> the table does not decide (the server logs them at a Chaos stage's start).</summary>
    public static IEnumerable<string> Unlisted(SimConfig config) =>
        config.Weapons.Select(w => w.Name).Concat(config.Thrown.Select(t => t.Name)).Where(k => !IsListed(k)).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every key the table lists (the census test).</summary>
    public static IReadOnlyCollection<string> Listed => Table.Keys;

    readonly HashSet<string> _logged = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Where an unknown key is reported (once per key).</summary>
    public Action<string>? Log { get; set; }

    /// <summary>The verdict on a detonation (<see cref="DetonationEvent"/>; its owner, weapon index and whether it was thrown).</summary>
    public BreachHit Classify(in DetonationEvent e, SimConfig config) => Classify(e.Owner, e.Weapon, e.Thrown, config);

    /// <summary>
    /// The verdict on an explosive of <paramref name="owner"/> launched by weapon <paramref name="weapon"/> (an index of <see cref="SimConfig.Thrown"/> when <paramref name="thrown"/>, else of
    /// <see cref="SimConfig.Weapons"/>). The owner must be a player above 0 and not a puppet (whether he is connected is the World's to check).
    /// </summary>
    public BreachHit Classify(int owner, int weapon, bool thrown, SimConfig config)
    {
        if (owner <= 0) return new(false, WarfrontBreachWhy.NotAPlayer, null);
        if (TfwContracts.IsPuppet(owner)) return new(false, WarfrontBreachWhy.Puppet, null);
        string key;
        if (thrown)
        {
            if (weapon < 0 || weapon >= config.Thrown.Count) return new(false, WarfrontBreachWhy.BadIndex, null);
            var item = config.Thrown[weapon];
            key = item.Name;
            if (item.Stim is not null) return new(false, WarfrontBreachWhy.Stim, key);
            if (item.IsBlindGrenade) return new(false, WarfrontBreachWhy.Blind, key);
        }
        else
        {
            if (weapon < 0 || weapon >= config.Weapons.Count) return new(false, WarfrontBreachWhy.BadIndex, null);
            key = config.Weapons[weapon].Name;
        }
        if (Table.TryGetValue(key, out bool counts)) return new(counts, counts ? WarfrontBreachWhy.Explosive : WarfrontBreachWhy.NotAnExplosive, key);
        if (IsRocket(key)) return new(true, WarfrontBreachWhy.Explosive, key);
        if (_logged.Add(key)) Log?.Invoke($"chaos breach filter: '{key}' is not in the table: its detonations do not close a breach");
        return new(false, WarfrontBreachWhy.Unknown, key);
    }
}
