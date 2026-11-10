using SmFormats.Baf;
using SmFormats.Obpk;
using SmSim;
using SmSim.Extras.Musou.Arsenal;

namespace SmContent.Extras.Musou.Arsenal;

/// <summary>
/// W1, the Musou autocannon (docs/new-weapons-design.md section 4; the owner, 2026-11-10: "the autocannon should fire slower but do more damage than the heavy bolter and have 2x the HE explosion size
/// of the bolter and bolt pistol"): a Devastator loadout primary in Musou Mode, never in Classic PvP. The install has the gun only as <c>csm_mp_exotic_auto_cannon</c> (a Chaos file no class names, cut from
/// the multiplayer game), so the Space Marines' weapon is its twin built here in code (<see cref="ArsenalRules.Autocannon"/>, never written to disk): the file's numbers (50 a round, 0.4 s between rounds,
/// 150 m with the fall-off 0.75 from 45 to 70 m, heat 1 a round with overheat at 15, cooling 4.5 a second after 0.41 s: kind data), fired full auto (the campaign's trigger; the file's is a 2-round burst), a
/// clip of 400 with no reserve (Musou's ammunition is endless anyway), and the heavy bolter's handling (raise, stance, spread: the file's scatter sets are not read; ours) without its brace
/// (the owner: no brace). It reaches the Devastator's primary list by appending an index, so no existing index moves. The explosive round is <c>AutocannonBlast.cs</c> (the simulation); the model
/// and animations are <c>client/Extras/Musou/Arsenal/Autocannon/</c>.
/// </summary>
public static partial class Arsenal
{
    /// <summary>The install's Chaos file this twin is built from.</summary>
    public const string AutocannonSource = "csm_mp_exotic_auto_cannon";
    /// <summary>The heavy bolter the autocannon borrows its handling from (and the Devastator's list it joins).</summary>
    public const string AutocannonHeavyBolter = "sm_mp_exotic_heavy_bolter";
    /// <summary>The customizer row's text (ours: the install's own text 104314 "Autocannon" and 104315, which the catalogue hook cannot read; the description is our wording of the file's).</summary>
    public const string AutocannonTitle = "Autocannon", AutocannonDescription = "A heavy gun that fires slow, enormously damaging explosive shells through several enemies at once. It overheats easily.";

    static partial void AppendAutocannon(ref SimConfig config, string gameDir, List<string> notes)
    {
        if (config.Weapons.Any(w => w.Name.Equals(ArsenalRules.Autocannon, StringComparison.OrdinalIgnoreCase))) return;   // already appended
        if (ReadAutocannonSource(gameDir, notes) is not { } source) return;
        config = WithAutocannon(config, source, notes);
    }

    /// <summary>The Chaos file's weapon as the simulation's reader gives it (hit-scan, a burst), or null with a note.</summary>
    static WeaponSpec? ReadAutocannonSource(string gameDir, List<string> notes)
    {
        string path = Path.Combine(gameDir, GameplayTuning.WargearPackage);
        if (!File.Exists(path)) { notes.Add($"musou arsenal: {GameplayTuning.WargearPackage} not found; no autocannon"); return null; }
        BafDocument? doc = null;
        using (var archive = ObpkArchive.Open(path))
            archive.ReadEntries(e => archive.GroupName(e) == "attr_pc" && e.FileName.Equals(AutocannonSource, StringComparison.OrdinalIgnoreCase), (_, data) => doc = BafDocument.Read(data));
        if (doc is null) { notes.Add($"musou arsenal: {AutocannonSource} not found in {GameplayTuning.WargearPackage}; no autocannon"); return null; }
        var spec = WeaponTuning.Read(AutocannonSource, doc, notes);
        if (spec is null) notes.Add($"musou arsenal: {AutocannonSource} could not be read; no autocannon");
        return spec;
    }

    /// <summary>
    /// The config with the autocannon appended to the weapons and to the primary list of every Space Marine class that lists the heavy bolter (the Devastator); the config itself when it has no
    /// heavy bolter to borrow from (a note says so). <paramref name="source"/> is the Chaos file's weapon as read.
    /// </summary>
    public static SimConfig WithAutocannon(SimConfig config, WeaponSpec source, List<string> notes)
    {
        int heavy = config.Weapons.ToList().FindIndex(w => w.Name.Equals(AutocannonHeavyBolter, StringComparison.OrdinalIgnoreCase));
        if (heavy < 0) { notes.Add($"musou arsenal: {AutocannonHeavyBolter} not in the config; no autocannon"); return config; }
        int index = config.Weapons.Count;
        var weapons = new List<WeaponSpec>(config.Weapons) { Twin(source, config.Weapons[heavy]) };
        var classes = config.Classes.Select(c => c.Side == Team.SpaceMarines && c.PrimaryChoices.Contains(heavy) ? c with { PrimaryChoices = [.. c.PrimaryChoices, index] } : c).ToList();
        return config with { Weapons = weapons, Classes = classes };
    }

    /// <summary>
    /// The Space Marines' autocannon: the Chaos file's weapon <paramref name="source"/> (damage, range, fall-off, heat and multipliers are the file's) fired full auto one round every 0.4 s from a clip of 400 with
    /// no reserve, carrying the heavy bolter's handling (<paramref name="heavy"/>: raise, stance, spread, reload; none of its brace).
    /// </summary>
    public static WeaponSpec Twin(WeaponSpec source, WeaponSpec heavy) => source with
    {
        Name = ArsenalRules.Autocannon, Slot = WeaponSlot.Ranged, Trigger = TriggerKind.FullAuto,
        ShotCooldown = AutocannonRules.SecondsPerRound, BurstLength = 0, BurstInterval = 0f,
        ClipSize = AutocannonRules.Clip, ReserveSize = 0, ReloadTime = heavy.ReloadTime,
        RaiseDelay = heavy.RaiseDelay, AlwaysReady = heavy.AlwaysReady, Spread = heavy.Spread,
        Brace = null, Vent = null, Variants = [],
    };

    static partial void AutocannonItems(ref PerkCatalog catalog)
    {
        // The Space Marine Devastator's primary list, after the heavy bolter and the others: one row, simulated (the twin is in Musou's config), level 1 (the file's).
        var item = new LoadoutItem(ArsenalRules.Autocannon, "ranged", AutocannonTitle, AutocannonDescription, 1, true, null, null);
        catalog = catalog.WithClasses(c => c.Race == "space_marine" && c.Role == "heavy" && !c.Primary.Items.Any(i => i.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase))
            ? c with { Primary = c.Primary with { Items = [.. c.Primary.Items, item] } } : c);
    }

    /// <summary>The animations the autocannon plays: the heavy bolter's (walk, sprint, hop, fall, hip fire, flinch): the campaign's autocannon carry uses the same navigation graph.</summary>
    static partial void AutocannonAlias(ref string wargear)
    {
        if (wargear.Equals(ArsenalRules.Autocannon, StringComparison.OrdinalIgnoreCase)) wargear = AutocannonHeavyBolter;
    }
}
