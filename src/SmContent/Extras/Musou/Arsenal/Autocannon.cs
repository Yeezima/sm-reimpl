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
    /// <summary>The customizer row's text (ours: the install's own text 104314 "Autocannon" and 104315, which the catalogue hook cannot read; the description is our wording of the file's).</summary>
    public const string AutocannonTitle = "Autocannon", AutocannonDescription = "A heavy gun that fires slow, enormously damaging explosive shells through several enemies at once. It overheats easily.";

    /// <summary>The Stalker Bolter's files (the sniper rifle of each side): the autocannon's sound source (the owner, 2026-11-10: "make the autocannon use stalker bolter sounds").</summary>
    public const string StalkerBolterFile = "sm_mp_ranged_sniper_rifle", StalkerBolterFileChaos = "csm_mp_ranged_sniper_rifle";

    static readonly IReadOnlyList<string> AutocannonSoundFiles = [StalkerBolterFile, StalkerBolterFileChaos];

    /// <summary>The install files <paramref name="wargear"/> takes its SOUNDS from when they are not its effects' files (the autocannon: the Stalker Bolter's, fire, reload, overheat, vent and the hit confirm), else null.</summary>
    public static IReadOnlyList<string>? SoundSources(string wargear) =>
        string.Equals(wargear, ArsenalRules.Autocannon, StringComparison.OrdinalIgnoreCase) ? AutocannonSoundFiles : null;

    /// <summary>The twin's Chaos muzzle flash and tracer (purple), as its file names them (<c>smtool baf-dump data/packages/wargear.oppc --entry csm_mp_exotic_auto_cannon</c>).</summary>
    public const string ChaosMuzzleEffect = "chaos_autocannon_muzzle", ChaosTracerEffect = "chaos_autocannon_tracer";

    /// <summary>
    /// W1c, the loyalist muzzle flash and tracer (the owner, 2026-11-10: "Loyalist effects"): the effects package's <c>autocannon_muzzle</c> (the loyalist autocannon's flash: the campaign's
    /// <c>auto_cannon</c> and the install's <c>test_assault_cannon</c> fire it with the twin's scale 1.5 and length 1.5) and <c>heavy_bolter_tracer</c> (the multiplayer heavy bolter's tracer, which
    /// its file binds with the twin's scale 1.5, length 1 and marker). Both are in <c>master_fx</c> (<c>smtool fx-dump auto autocannon tracer --list</c>).
    /// </summary>
    public const string LoyalistMuzzleEffect = "autocannon_muzzle", LoyalistTracerEffect = "heavy_bolter_tracer";

    /// <summary>
    /// The twin's impact container (<c>weapon_hits\chaos\ranged_weapons\autocannon</c>, the Chaos one of the install's two <c>autocannon</c> containers) plays <c>chaos_autocannon_dmg</c> (scale 0.3) for
    /// every hit no surface of its own names: the walls and the ground of every map, and the body hits that fall back to it. Its flashes, sparks, hot crater and smoke are purple and lavender.
    /// </summary>
    public const string ChaosImpactEffect = "chaos_autocannon_dmg";

    /// <summary>
    /// W1c, the loyalist impact (the owner, 2026-11-10: "Loyalist impact"): <c>autocannon_dmg</c>, the world hit of the install's Space Marine <c>autocannon</c> container (its <c>default/default</c>, at
    /// the twin's very scale 0.3): the same effect element for element (crater, flash, debris and smoke columns, the fire, the light) in warm colours, so the twin's twin as the muzzle flash is.
    /// In <c>master_fx</c> (<c>smtool fx-dump auto autocannon_dmg chaos_autocannon_dmg</c>).
    /// </summary>
    public const string LoyalistImpactEffect = "autocannon_dmg";

    static readonly IReadOnlyDictionary<string, string> AutocannonEffectNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [ChaosMuzzleEffect] = LoyalistMuzzleEffect,
        [ChaosTracerEffect] = LoyalistTracerEffect,
        [ChaosImpactEffect] = LoyalistImpactEffect,   // in the autocannon's own copy of the twin's container (the container itself is shared)
    };

    /// <summary>
    /// The effects <paramref name="wargear"/> plays in place of its presentation source's (from → to, <see cref="EffectCatalogue.WithEffectNames"/>), else null: the autocannon shows the twin's
    /// effect commands (markers, scales, the impacts' surfaces, the smoke) with the loyalist muzzle flash, tracer and impact burst instead of the purple Chaos ones. Its sounds (the Stalker's, the
    /// impact's hit sound too: the client plays SoundTriggers.HitSound, which W1b sources from the Stalker; the copied container's own sound field is not played), reticle and ammo box are untouched.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? EffectNames(string wargear) =>
        string.Equals(wargear, ArsenalRules.Autocannon, StringComparison.OrdinalIgnoreCase) ? AutocannonEffectNames : null;

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
            archive.ReadEntries(e => archive.GroupName(e) == "attr_pc" && e.FileName.Equals(AutocannonTwinFile, StringComparison.OrdinalIgnoreCase), (_, data) => doc = BafDocument.Read(data));
        if (doc is null) { notes.Add($"musou arsenal: {AutocannonTwinFile} not found in {GameplayTuning.WargearPackage}; no autocannon"); return null; }
        var spec = WeaponTuning.Read(AutocannonTwinFile, doc, notes);
        if (spec is null) notes.Add($"musou arsenal: {AutocannonTwinFile} could not be read; no autocannon");
        return spec;
    }

    /// <summary>
    /// The config with the autocannon appended to the weapons and to the primary list of every Space Marine class that lists the heavy bolter (the Devastator); the config itself when it has no
    /// heavy bolter to borrow from (a note says so). <paramref name="source"/> is the Chaos file's weapon as read.
    /// </summary>
    public static SimConfig WithAutocannon(SimConfig config, WeaponSpec source, List<string> notes)
    {
        int heavy = config.Weapons.ToList().FindIndex(w => w.Name.Equals(HeavyBolterFile, StringComparison.OrdinalIgnoreCase));
        if (heavy < 0) { notes.Add($"musou arsenal: {HeavyBolterFile} not in the config; no autocannon"); return config; }
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
        if (wargear.Equals(ArsenalRules.Autocannon, StringComparison.OrdinalIgnoreCase)) wargear = HeavyBolterFile;
    }
}
