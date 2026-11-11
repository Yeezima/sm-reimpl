using SmFormats.Baf;
using SmFormats.Obpk;
using SmSim;
using SmSim.Extras.Musou.Arsenal;

namespace SmContent.Extras.Musou.Arsenal;

/// <summary>
/// W1, the Musou autocannon (docs/new-weapons-design.md section 4; the owner, 2026-11-10: "the autocannon should fire slower but do more damage than the heavy bolter and have 2x the HE explosion size
/// of the bolter and bolt pistol"): a Devastator loadout primary in Musou Mode, never in Classic PvP. The install has the gun only as <c>csm_mp_exotic_auto_cannon</c> (a Chaos file no class names, cut from
/// the multiplayer game), so the Space Marines' weapon is its twin built here in code (<see cref="ArsenalRules.Autocannon"/>, never written to disk): the file's numbers (0.4 s between rounds,
/// 150 m with the fall-off 0.75 from 45 to 70 m, heat 1 a round with overheat at 15, cooling 4.5 a second after 0.41 s: kind data), fired full auto (the campaign's trigger; the file's is a 2-round burst), a
/// clip of 400 with no reserve (Musou's ammunition is endless anyway; the autocannon's clip is never spent there since 2026-11-11), and the heavy bolter's handling (raise, stance, spread: the file's scatter
/// sets are not read; ours) without its brace (the owner: no brace). Musou's round is <see cref="AutocannonRules.DirectDamage"/> (130, the blast's + 30: the owner, 2026-11-11; the file's 50 until then).
/// It reaches the Devastator's primary list by appending an index, so no existing index moves. The explosive round is <c>AutocannonBlast.cs</c> (the simulation); the model and animations are
/// <c>client/Extras/Musou/Arsenal/Autocannon/</c>. Classic PvP's autocannon (<c>ClassicAutocannon</c>, <see cref="ContentExtras.Autocannon"/>) is the same <see cref="Twin"/> with the Stalker
/// Bolter's shot (<see cref="ClassicTwin"/>).
/// </summary>
public static partial class Arsenal
{
    /// <summary>The customizer row's text (ours: the install's own text 104314 "Autocannon" and 104315, which the catalogue hook cannot read; the description is our wording of the file's).</summary>
    public const string AutocannonTitle = "Autocannon", AutocannonDescription = "A heavy gun that fires slow, enormously damaging explosive shells through several enemies at once. It overheats easily.";

    /// <summary>The Stalker Bolter's files (the sniper rifle of each side): the autocannon's sound source (the owner, 2026-11-10: "make the autocannon use stalker bolter sounds").</summary>
    public const string StalkerBolterFile = "sm_mp_ranged_sniper_rifle", StalkerBolterFileChaos = "csm_mp_ranged_sniper_rifle";

    static readonly IReadOnlyList<string> AutocannonSoundFiles = [StalkerBolterFile, StalkerBolterFileChaos];

    /// <summary>The install files <paramref name="wargear"/> takes its SOUNDS from in Musou Mode when they are not its effects' files (the autocannon: the Stalker Bolter's, fire, reload, overheat, vent and the hit confirm), else null.</summary>
    public static IReadOnlyList<string>? SoundSources(string wargear) =>
        string.Equals(wargear, ArsenalRules.Autocannon, StringComparison.OrdinalIgnoreCase) ? AutocannonSoundFiles : null;

    /// <summary>
    /// <see cref="SoundSources(string)"/> for a match or page with <paramref name="extras"/>: Musou Mode's alone. Classic PvP's autocannon (<see cref="ContentExtras.Autocannon"/>) has none, so it sounds as its
    /// presentation source, the install's own autocannon (the owner, 2026-11-11: "the sound of the autocannon in classic pvp mode should simply it's original sound - no stalker bolter sound change").
    /// </summary>
    public static IReadOnlyList<string>? SoundSources(ContentExtras extras, string wargear) =>
        extras.HasFlag(ContentExtras.Musou) && ArsenalRules.Shown(extras, wargear) ? SoundSources(wargear) : null;

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

    /// <summary><see cref="EffectNames(string)"/> where <paramref name="extras"/> carries the item (<see cref="ArsenalRules.Shown"/>): the same loyalist look in Musou Mode and in Classic PvP, else null.</summary>
    public static IReadOnlyDictionary<string, string>? EffectNames(ContentExtras extras, string wargear) => ArsenalRules.Shown(extras, wargear) ? EffectNames(wargear) : null;

    static partial void AppendAutocannon(ref SimConfig config, string gameDir, List<string> notes)
    {
        if (config.Weapons.Any(w => w.Name.Equals(ArsenalRules.Autocannon, StringComparison.OrdinalIgnoreCase))) return;   // already appended
        if (ReadAutocannonSource(gameDir, notes) is not { } source) return;
        config = WithAutocannon(config, source, notes);
    }

    /// <summary>The Chaos file's weapon as the simulation's reader gives it (hit-scan, a burst), or null with a note starting with <paramref name="who"/> (Musou's arsenal or Classic's autocannon).</summary>
    public static WeaponSpec? ReadAutocannonSource(string gameDir, List<string> notes, string who = "musou arsenal")
    {
        string path = Path.Combine(gameDir, GameplayTuning.WargearPackage);
        if (!File.Exists(path)) { notes.Add($"{who}: {GameplayTuning.WargearPackage} not found; no autocannon"); return null; }
        BafDocument? doc = null;
        using (var archive = ObpkArchive.Open(path))
            archive.ReadEntries(e => archive.GroupName(e) == "attr_pc" && e.FileName.Equals(AutocannonTwinFile, StringComparison.OrdinalIgnoreCase), (_, data) => doc = BafDocument.Read(data));
        if (doc is null) { notes.Add($"{who}: {AutocannonTwinFile} not found in {GameplayTuning.WargearPackage}; no autocannon"); return null; }
        var spec = WeaponTuning.Read(AutocannonTwinFile, doc, notes);
        if (spec is null) notes.Add($"{who}: {AutocannonTwinFile} could not be read; no autocannon");
        return spec;
    }

    /// <summary>
    /// Musou's config with the autocannon (<see cref="MusouTwin"/>) appended to the weapons and to the primary list of every Space Marine class that lists the heavy bolter (the Devastator); the config
    /// itself when it has no heavy bolter to borrow from (a note says so). <paramref name="source"/> is the Chaos file's weapon as read.
    /// </summary>
    public static SimConfig WithAutocannon(SimConfig config, WeaponSpec source, List<string> notes)
    {
        int heavy = config.Weapons.ToList().FindIndex(w => w.Name.Equals(HeavyBolterFile, StringComparison.OrdinalIgnoreCase));
        if (heavy < 0) { notes.Add($"musou arsenal: {HeavyBolterFile} not in the config; no autocannon"); return config; }
        return Appended(config, MusouTwin(source, config.Weapons[heavy]), [heavy], Team.SpaceMarines);
    }

    /// <summary>
    /// <paramref name="config"/> with <paramref name="gun"/> appended to the weapons and its index to the end of the primary list of every class of <paramref name="sides"/> that lists one of
    /// <paramref name="heavies"/> (weapon indices), so no existing index moves and the heavy classes of both sides find it at the same position.
    /// </summary>
    public static SimConfig Appended(SimConfig config, WeaponSpec gun, IReadOnlyCollection<int> heavies, params Team[] sides)
    {
        int index = config.Weapons.Count;
        var weapons = new List<WeaponSpec>(config.Weapons) { gun };
        var classes = config.Classes.Select(c => sides.Contains(c.Side) && c.PrimaryChoices.Any(heavies.Contains) ? c with { PrimaryChoices = [.. c.PrimaryChoices, index] } : c).ToList();
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

    /// <summary>Musou's autocannon: <see cref="Twin"/> with its round at <see cref="AutocannonRules.DirectDamage"/> (the blast's + 30; the owner, 2026-11-11), its explosive round in the war's shot path.</summary>
    public static WeaponSpec MusouTwin(WeaponSpec source, WeaponSpec heavy) => Twin(source, heavy) with { Damage = AutocannonRules.DirectDamage };

    /// <summary>
    /// Classic PvP's autocannon (the owner, 2026-11-11: "everything the same except: no aoe blast radius / simply higher single shot damage to be the same as the stalker - with the original games
    /// headshot bonus value"): <see cref="Twin"/> (the rate, trigger, heat, range and fall-off, the clip of 400 and the heavy bolter's handling, as Musou's) with the Stalker Bolter's shot as Classic
    /// reads it (<paramref name="stalker"/>: its damage, 50 on the install, and its head, body and armour multipliers, 2 and 2.5 on the head): no blast (Classic's shot path has none) and the data's
    /// ammunition (the clip spends; an ammunition box refills it, as the heavy bolter's).
    /// </summary>
    public static WeaponSpec ClassicTwin(WeaponSpec source, WeaponSpec heavy, WeaponSpec stalker) => Twin(source, heavy) with
    {
        Damage = stalker.Damage, HeadMultiplier = stalker.HeadMultiplier, BodyMultiplier = stalker.BodyMultiplier,
        ShieldHeadMultiplier = stalker.ShieldHeadMultiplier, ShieldBodyMultiplier = stalker.ShieldBodyMultiplier,
    };

    // The Space Marine Devastator's primary list, after the heavy bolter and the others: one row, simulated (the twin is in Musou's config), level 1 (the file's).
    static partial void AutocannonItems(ref PerkCatalog catalog) => catalog = WithAutocannonRow(catalog, CustomizationCatalog.SpaceMarine);

    /// <summary>The customizer's autocannon row (one, simulated, level 1: the file's).</summary>
    static readonly LoadoutItem AutocannonItem = new(ArsenalRules.Autocannon, "ranged", AutocannonTitle, AutocannonDescription, 1, true, null, null);

    /// <summary>
    /// <paramref name="catalog"/> with the autocannon's row at the end of the heavy class's primary list of each of <paramref name="races"/> (Musou: the Space Marine Devastator; Classic: the Havoc
    /// too); a class that has it already is left alone. Its name is the simulation's on every side (one weapon serves both: <c>LoadoutIndices</c> and the profile go by the side-neutral name).
    /// </summary>
    public static PerkCatalog WithAutocannonRow(PerkCatalog catalog, params string[] races) =>
        catalog.WithClasses(c => races.Contains(c.Race) && c.Role == "heavy" && !c.Primary.Items.Any(i => i.Name.Equals(AutocannonItem.Name, StringComparison.OrdinalIgnoreCase))
            ? c with { Primary = c.Primary with { Items = [.. c.Primary.Items, AutocannonItem] } } : c);

    /// <summary>
    /// The wargear a carried <paramref name="wargear"/> wears as for the class overrides (every mode, the owner 2026-11-11: "equipping the autocannon should also change the devastator/havoc's backpack to
    /// be the same one as the heavy bolter"): the autocannon the heavy bolter's (the Devastator's <c>sm_backpack_heavy_bolter</c>; the Havoc's pack is the same for every weapon), any other name itself.
    /// </summary>
    public static string WornAs(string wargear) =>
        LoadoutIndices.Neutral(wargear).Equals(LoadoutIndices.Neutral(ArsenalRules.Autocannon), StringComparison.OrdinalIgnoreCase) ? HeavyBolterFile : wargear;

    /// <summary>The animations the autocannon plays: the heavy bolter's (walk, sprint, hop, fall, hip fire, flinch): the campaign's autocannon carry uses the same navigation graph.</summary>
    static partial void AutocannonAlias(ref string wargear)
    {
        if (wargear.Equals(ArsenalRules.Autocannon, StringComparison.OrdinalIgnoreCase)) wargear = HeavyBolterFile;
    }
}
