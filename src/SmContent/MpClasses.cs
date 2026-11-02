using SmFormats.Baf;
using SmFormats.Havok;
using SmFormats.Obpk;
using SmSim;

namespace SmContent;

/// <summary>A multiplayer class as the game data defines it (global <c>multiplayer_settings/ingame_progression/classes</c>).</summary>
/// <param name="Key">e.g. "sm - tactical".</param>
/// <param name="Role">The data's <c>mp_class</c>: "assault", "tactical" or "heavy".</param>
/// <param name="Race">"space_marine" or "chaos".</param>
/// <param name="BaseWargear">Always carried (wargear file names, without folders).</param>
/// <param name="StartupWargear">The choices offered for the loadout, in the data's order.</param>
public sealed record MpClass(string Key, string Role, string Race, IReadOnlyList<string> BaseWargear, IReadOnlyList<string> StartupWargear,
    IReadOnlyList<string> Perks, string? DefaultPerk);

/// <summary>
/// Multiplayer classes and their default loadouts, from the install. The default loadout is a **provisional
/// approximation**: the base wargear plus, for each slot the class has none of, the first choice of that slot in its
/// startup list whose weapon the simulation supports (hit-scan firearms; the knife and chainsword combos). The original
/// front end's default selection and the unlock levels are not applied. Projectile weapons are read and offered only when
/// asked (<see cref="OfferProjectileWeaponsByDefault"/>); thrown grenades are read into <see cref="Thrown"/>.
/// </summary>
public sealed record MpClasses(IReadOnlyList<MpClass> Classes, IReadOnlyList<WeaponSpec> Weapons, IReadOnlyList<ClassLoadout> Loadouts,
    IReadOnlyList<IReadOnlyList<string>> MeleeAnimations, IReadOnlyList<string> Notes)
{
    /// <summary>The clips each supported melee weapon's combo plays, by wargear name (for loadout choices).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> MeleeAnimationsByWeapon { get; init; } = new Dictionary<string, IReadOnlyList<string>>();
    /// <summary>The clips each supported melee weapon's heavy strikes play, by wargear name (M28).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> MeleeHeavyAnimationsByWeapon { get; init; } = new Dictionary<string, IReadOnlyList<string>>();
    /// <summary>The reaction kinds' clips from the rig's reaction graph (<see cref="ReactionTuning.Specs"/>, M28), or null (placeholders stay).</summary>
    public IReadOnlyList<ReactionSpec>? Reactions { get; init; }
    /// <summary>
    /// Thrown equipment the classes may carry (<see cref="SimConfig.Thrown"/>; indices match <see cref="ClassLoadout.EquipmentChoices"/>):
    /// every equipment item with a thrown projectile that some class may choose.
    /// </summary>
    public IReadOnlyList<ThrownSpec> Thrown { get; init; } = [];
    /// <summary>The throw's timing from the toss behavior graph, or null when it could not be read (placeholders stay).</summary>
    public ThrowTiming? Throw { get; init; }
    /// <summary>Jump packs the classes carry (<see cref="SimConfig.JumpPacks"/>; indices match <see cref="ClassLoadout.JumpPack"/>), with behavior-graph timing.</summary>
    public IReadOnlyList<JumpPackSpec> JumpPacks { get; init; } = [];
    /// <summary>Evades the classes without a jump pack carry (<see cref="SimConfig.Evades"/>; indices match <see cref="ClassLoadout.Evade"/>; M21).</summary>
    public IReadOnlyList<EvadeSpec> Evades { get; init; } = [];
    /// <summary>The hip-fire graphs' data the weapons' raise delays came from (M31; <see cref="WithRaise"/>), or null when not read.</summary>
    public HipFireAnimations? HipFire { get; init; }
    /// <summary>
    /// The perks the classes may take (<see cref="SimConfig.Perks"/>; indices match <see cref="ClassLoadout.PerkChoices"/> and
    /// <see cref="ClassLoadout.DefaultPerk"/>): every perk file a class names, in first-named order (<see cref="ReadPerk"/>).
    /// </summary>
    public IReadOnlyList<PerkSpec> Perks { get; init; } = [];
    /// <summary>Class bodies (<see cref="SimConfig.Bodies"/>; indices match <see cref="ClassLoadout.Body"/>): the Ork Boy's when it is appended (Ork O4), else none.</summary>
    public IReadOnlyList<BodySpec> Bodies { get; init; } = [];
    /// <summary>The Ork Boy's balance build when it was appended (<see cref="OrkClass.Append"/>; its rows for <c>smtool ork-balance</c>), else null.</summary>
    public OrkBuild? Ork { get; init; }
    /// <summary>The owner's weapon overrides applied to <see cref="Weapons"/> (<see cref="OwnerBalance"/>; data and result per rule), empty when off.</summary>
    public IReadOnlyList<OwnerBalanceRow> OwnerRows { get; init; } = [];
    /// <summary>The sprint stop clip's timing (feel/locks-sprint-stop; <see cref="SprintStopTuning"/>), or null when it could not be read.</summary>
    public SprintStopTiming? SprintStop { get; init; }

    /// <summary>Wargear files whose name starts with this are weapon perks (<see cref="PerkKind.Weapon"/>; interpretation, see <see cref="ReadPerk"/>).</summary>
    public const string WeaponPerkPrefix = "mp_perk_weap_";

    /// <summary>
    /// A perk from its wargear file (docs/customizer-and-perks-data.md section 2; **Verified-local** layout): the upgrade string is the
    /// first non-empty string of <c>action_data/lifetime_actions/commands/apply_upgrade_string/strings</c> (the file's <c>entry</c>; null
    /// when the file applies none, e.g. Weapon Versatility), the level <c>common_data/unlock_info/level_requirement/level_required</c>
    /// (<see cref="MissingLevel"/> when missing, as every catalogue item), the kind <see cref="KindOf"/>. The one perk reader: the
    /// simulation's <see cref="SimConfig.Perks"/> and the creator's <see cref="PerkCatalog"/> both use it.
    /// </summary>
    public static PerkSpec ReadPerk(string name, BafDocument doc)
    {
        string? upgrade = null;
        if (doc.Root.Table("action_data/lifetime_actions/commands/apply_upgrade_string/strings") is { } strings)
            foreach (var e in strings.Entries)
                if (e.Type == BafType.String && e.AsString().Length > 0) { upgrade = e.AsString(); break; }
        int level = doc.Root.Table("common_data/unlock_info/level_requirement")?["level_required"] is { Type: BafType.Int } l ? l.Raw : MissingLevel;
        return new PerkSpec(name, upgrade, KindOf(name), level);
    }

    /// <summary>The unlock level of a wargear file without <c>level_required</c>: 1 (unlocked from the start).</summary>
    public const int MissingLevel = 1;

    /// <summary>A perk's kind by its file name: <see cref="PerkKind.Weapon"/> for the <see cref="WeaponPerkPrefix"/> files (**interpretation**:
    /// <c>ui_data/weapon_type</c> is also set on a class perk, Final Vengeance), else <see cref="PerkKind.Class"/>.</summary>
    public static PerkKind KindOf(string name) => name.StartsWith(WeaponPerkPrefix, StringComparison.OrdinalIgnoreCase) ? PerkKind.Weapon : PerkKind.Class;
    public const string GlobalPackage = "data/packages/global.oppc";

    /// <summary>
    /// Whether equipment hidden from the customizer (<c>ui_data/visible_in_customizer</c> false: the melta bomb) is offered.
    /// Off: the data hides it from the player's choice (**interpretation**); it is still read into <see cref="Thrown"/> only when offered.
    /// </summary>
    public const bool OfferHiddenEquipmentByDefault = false;

    /// <summary>The graph and sequencer events the throw timing is read from (the marine rig's grenade toss).</summary>
    public const string TossGraph = "SM_Bolter_GrenadeToss", ReleaseEvent = "Ability_Ranged_Fire", EndBranch = "BRANCH_UseEquipment_End";

    /// <summary>
    /// The throw's timing: <see cref="TossGraph"/>'s sequencer, its animation's length / playback speed, the
    /// <see cref="ReleaseEvent"/> event's start and the <see cref="EndBranch"/> branch's start (tag times are fractions of the
    /// animation, as for melee). Null with a note when missing or unusable.
    /// </summary>
    public static ThrowTiming? ReadThrowTiming(IReadOnlyList<BehaviorGraph> graphs, Func<string, float> animationLength, List<string> notes)
    {
        var seq = graphs.FirstOrDefault(g => g.Name == TossGraph)?.Sequencers.FirstOrDefault(s => s.Animations.Count > 0 && s.Tags.Any(t => t.Name == ReleaseEvent));
        if (seq is null) { notes.Add($"throw: graph {TossGraph} with a {ReleaseEvent} event not found; throw timing is a placeholder"); return null; }
        float speed = seq.PlaybackSpeeds.Count > 0 && float.IsFinite(seq.PlaybackSpeeds[0]) && seq.PlaybackSpeeds[0] > 0f ? seq.PlaybackSpeeds[0] : 1f;
        float duration;
        try { duration = animationLength(seq.Animations[0]) / speed; }
        catch (Exception e) when (e is InvalidDataException or KeyNotFoundException or ArgumentException) { notes.Add($"throw: {seq.Animations[0]}: {e.Message}; throw timing is a placeholder"); return null; }
        var release = seq.Tags.Where(t => t.Name == ReleaseEvent).OrderBy(t => t.Start).First();
        var end = seq.Tags.Where(t => t.Kind == "Branch" && t.Name == EndBranch).OrderBy(t => t.Start).FirstOrDefault();
        if (end is null) notes.Add($"throw: no {EndBranch} branch; the throw lasts the whole animation");
        var timing = new ThrowTiming(release.Start * duration, (end?.Start ?? 1f) * duration, seq.Animations[0]);
        if (!(float.IsFinite(timing.Release) && float.IsFinite(timing.Duration) && timing.Release >= 0f && timing.Duration >= timing.Release))
        { notes.Add($"throw: timing {timing.Release}/{timing.Duration} s unusable; placeholders stay"); return null; }
        return timing;
    }

    /// <summary>
    /// Whether projectile weapons (<see cref="WeaponSpec.Delivery"/> = Projectile: grenade launcher, plasma cannon) are
    /// offered as loadout choices. On since M12: the simulation launches projectiles (<c>SmSim.World</c>). Charged fire
    /// (<see cref="WeaponSpec.Charge"/>) on hit-scan weapons does not gate them.
    /// </summary>
    public const bool OfferProjectileWeaponsByDefault = true;

    /// <summary>
    /// The chainsword: four light attacks; heavy strikes (M28) by their clips: "CS Y Atk Sequencer" (<c>SM_MEL_CS_YATK_Combo1</c>),
    /// "Sequencer22" (<c>…Combo2</c>), "Sequencer21" (<c>…Combo3</c>) and the stomp "Sequencer20" (<c>SM_MEL_CS_YATK_DSP_Stomp</c>),
    /// with <c>ability_melee_y</c>, <c>_y2</c>, <c>_y3</c> (the order by clip name is an interpretation; the state machine is not decoded).
    /// </summary>
    static readonly MeleeTuning.WeaponMap ChainSword = new("sm_mp_melee_chainsword", "MP_SM_MEL_ChainSword",
        ["Sequencer_X1", "Sequencer_X2", "Sequencer_X3", "Sequencer_X4"], ["ability_melee_x1", "ability_melee_x2", "ability_melee_x3", "ability_melee_x4"])
    {
        HeavySequencers = ["CS Y Atk Sequencer", "Sequencer22", "Sequencer21", "Sequencer20"],
        HeavyAbilities = ["ability_melee_y", "ability_melee_y2", "ability_melee_y3", ""],
        // feel/melee-charge-2 (protocol 42; the owner: "normal combo is backhand > stomp"): the graph's Backhand (Y), Kick (X, Y) and Shoulder (X, X, Y) go on
        // BTN_Melee_Stun in their BRANCH_Melee_Y_Combo window to "CS Default Combo End 4 ( Y , Y ) AOE Attack", whose sequencer is Sequencer20 (the stomp,
        // heavy strike 4 here). The graph's X, X, X, Y is a separate "Quick AOE Attack" (Sequencer31) that this map still plays as Sequencer20 (M28, a gap).
        YNext = [4, 4, 4, 0],
    };

    /// <summary>Melee weapons whose combo the simulation knows: wargear name (without the race prefix) → graph and sequencers.</summary>
    static readonly (string Suffix, Func<string, MeleeTuning.WeaponMap> Map)[] MeleeMaps =
    [
        ("_mp_melee_combat_knife", w => MeleeTuning.CombatKnife with { Wargear = w }),
        ("_mp_melee_chainsword_gold", w => ChainSword with { Wargear = w }),
        ("_mp_melee_chainsword", w => ChainSword with { Wargear = w }),
        // Weapons C: the power sword, the power axe / chain axe and the thunder hammer / daemon maul (both races share the marine rig's graphs).
        ("_mp_melee_powersword", w => MeleeTuning.PowerSword with { Wargear = w }),
        ("_mp_melee_power_axe", w => MeleeTuning.PowerAxe with { Wargear = w }),
        ("_mp_melee_chain_axe", w => MeleeTuning.PowerAxe with { Wargear = w }),
        ("_mp_melee_2hand_thunder_hammer", w => MeleeTuning.ThunderHammer with { Wargear = w }),
        ("_mp_melee_2hand_power_mace", w => MeleeTuning.ThunderHammer with { Wargear = w }),
    ];

    /// <summary>
    /// Weapons C: melee that comes with a class's core ability instead of a melee slot item (the Devastator's and the Havoc's kick and
    /// stomp, <see cref="MeleeTuning.Displace"/>): used only by a class with no supported melee weapon (<see cref="CoreMelee"/>).
    /// </summary>
    static readonly (string Suffix, Func<string, MeleeTuning.WeaponMap> Map)[] CoreMeleeMaps =
    [
        ("_mp_core_abil_heavy_evade", w => MeleeTuning.Displace with { Wargear = w }),
    ];

    /// <summary>The melee map of a wargear name: the melee slot's (<see cref="MeleeMaps"/>), else a core ability's (<see cref="CoreMeleeMaps"/>), else null.</summary>
    static Func<string, MeleeTuning.WeaponMap>? MeleeMapOf(string name) =>
        MeleeMaps.Concat(CoreMeleeMaps).FirstOrDefault(m => name.EndsWith(m.Suffix, StringComparison.OrdinalIgnoreCase)).Map;

    /// <summary>anim/melee-flow (client presentation): the graph and sequencers the simulation reads a melee wargear's combo from, or null (read only; the same maps as the loader).</summary>
    public static MeleeTuning.WeaponMap? MeleeMapFor(string wargear) => MeleeMapOf(wargear)?.Invoke(wargear);

    /// <summary>Weapons C: the base wargear of a class without a melee weapon that brings melee of its own (its core ability), in data order.</summary>
    static IEnumerable<string> CoreMelee(MpClass c) =>
        c.BaseWargear.Where(n => CoreMeleeMaps.Any(m => n.EndsWith(m.Suffix, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// The melee weapons the simulation has combos for (wargear name suffixes, most specific first): the one list the loader and the
    /// customizer's catalogue (<see cref="PerkCatalog.SimulatedMeleeSuffixes"/>) share.
    /// </summary>
    public static IReadOnlyList<string> SimulatedMeleeSuffixes { get; } = Array.AsReadOnly(MeleeMaps.Select(m => m.Suffix).ToArray());

    public static IReadOnlyList<MpClass> ReadClasses(BafDocument global)
    {
        var classes = global.Root.Table("extensions/multiplayer_settings/ingame_progression/classes")
            ?? throw new InvalidDataException("global: no multiplayer_settings/ingame_progression/classes.");
        static string Leaf(string path) => path.Split('\\', '/')[^1];
        static List<string> List(BafTable? t) => t?.Entries.Where(e => e.Type == BafType.String).Select(e => Leaf(e.AsString())).ToList() ?? [];
        return classes.Entries.Where(e => e.Type == BafType.Table).Select(e =>
        {
            var c = e.AsTable();
            return new MpClass(e.Name, c.String("mp_class") ?? "", c.String("race") ?? "",
                List(c.Table("base_wargear")), List(c.Table("startup_wargear")), List(c.Table("class_perks")),
                c.String("default_perk") is { } p ? Leaf(p) : null);
        }).ToList();
    }

    /// <param name="offerProjectileWeapons">See <see cref="OfferProjectileWeaponsByDefault"/>.</param>
    /// <param name="ork">Ork O4: append the Ork Boy (<see cref="OrkClass"/>) balanced in this mode; null (the default): the six classes only, as before.</param>
    /// <param name="ownerBalance">The owner's weapon overrides (<see cref="OwnerBalance"/>) against a marine of this shield and health at this
    /// tick, applied before the Ork so that its parity reference is the owner-balanced bolter; null: the data's weapon values.</param>
    public static MpClasses Load(string gameDir, bool offerProjectileWeapons = OfferProjectileWeaponsByDefault, OrkBalanceMode? ork = null,
        (float TickSeconds, float Shield, float Health)? ownerBalance = null, bool raceParity = true)
    {
        var notes = new List<string>();
        BafDocument? global = null;
        using (var archive = ObpkArchive.Open(Path.Combine(gameDir, GlobalPackage)))
            archive.ReadEntries(e => archive.GroupName(e) == "attr_pc" && e.FileName == "global", (_, d) => global = BafDocument.Read(d));
        if (global is null) throw new FileNotFoundException($"global not found in {GlobalPackage}.");
        var classes = ReadClasses(global);

        // Every wargear file a class names, read once.
        var names = classes.SelectMany(c => c.BaseWargear.Concat(c.StartupWargear).Concat(c.Perks).Concat(c.DefaultPerk is { } d ? [d] : []))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var docs = new Dictionary<string, BafDocument>(StringComparer.OrdinalIgnoreCase);
        using (var archive = ObpkArchive.Open(Path.Combine(gameDir, GameplayTuning.WargearPackage)))
            archive.ReadEntries(e => archive.GroupName(e) == "attr_pc" && names.Contains(e.FileName), (e, d) => docs[e.FileName] = BafDocument.Read(d));

        // The projectile blueprints those files name (ranged_delivery_type/entry/type/ebp), by file name in ebps.
        var wanted = docs.Values.SelectMany(d => BlueprintRefs(d.Root)).Select(Leaf).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var blueprints = new Dictionary<string, BafDocument>(StringComparer.OrdinalIgnoreCase);
        if (wanted.Count > 0)
            using (var archive = ObpkArchive.Open(Path.Combine(gameDir, GameplayTuning.BlueprintPackage)))
                archive.ReadEntries(e => archive.GroupName(e) == "attr_pc" && wanted.Contains(e.FileName), (e, d) =>
                {
                    if (blueprints.ContainsKey(e.FileName)) notes.Add($"ebps: several blueprints named {e.FileName}; the last is used");
                    blueprints[e.FileName] = BafDocument.Read(d);
                });

        // The marine rig's behavior graphs (both sides use it) for melee timing.
        byte[]? rig = null;
        using (var archive = ObpkArchive.Open(Path.Combine(gameDir, CharacterModel.MarineRigArchive)))
            archive.ReadEntries(e => e.FileName == CharacterModel.MarineRigEntry, (_, d) => rig = d.ToArray());
        if (rig is null) throw new FileNotFoundException($"'{CharacterModel.MarineRigEntry}' not found.");
        var character = new HavokCharacterFile(rig);
        var graphs = HavokBehavior.Read(character.Pack);
        var mapping = ReactionTuning.LoadMapping(gameDir, notes);
        // M31: the weapon families' raise delays and ready conditions (hip fire's graphs, the same rig file).
        HipFireAnimations? hipFire = null;
        try { hipFire = HipFireAnimations.From(character); }
        catch (Exception e) when (e is InvalidDataException or KeyNotFoundException or ArgumentException or IndexOutOfRangeException)
        { notes.Add($"hip fire: {e.GetType().Name}: {e.Message}; raise delays not read (shots never wait)"); }
        // weapons/spread: the climb_sway_scatter sets the wargear names.
        // A package that cannot be read degrades to "no spread" with a note, like the hip-fire read above (review, weapons/spread).
        Dictionary<string, BafDocument> scatterSets;
        try { scatterSets = SpreadTuning.LoadSets(gameDir, docs.Values.SelectMany(SpreadTuning.SetNames), notes); }
        catch (Exception e) when (e is InvalidDataException or KeyNotFoundException or ArgumentException or IndexOutOfRangeException or EndOfStreamException or IOException)
        { notes.Add($"spread: {e.GetType().Name}: {e.Message}; weapons have no scatter"); scatterSets = new(StringComparer.OrdinalIgnoreCase); }
        var built = Build(classes, docs, graphs, a => character.LoadAnimation(a).Animation.Duration, notes,
            path => blueprints.GetValueOrDefault(Leaf(path)), offerProjectileWeapons, extractedMotion: character.LoadExtractedMotion,
            lowestFoot: (clip, samples) => EvadeTuning.LowestFoot(character, clip, samples), reactionMapping: mapping, hipFire: hipFire,
            meleeSpeedUpgrades: MeleeTuning.ReadSpeedUpgrades(character.Pack, notes),
            scatterSets: scatterSets.GetValueOrDefault, meleeReach: MeleeMotion.Source.From(character, notes));
        built = built with { SprintStop = SprintStopTuning.Read(graphs, a => character.LoadAnimation(a).Animation.Duration, character.LoadExtractedMotion, notes) };   // feel/locks-sprint-stop
        if (raceParity) built = RaceParity.Apply(built, notes);
        if (ownerBalance is { } owner) built = WithOwnerBalance(built, owner.TickSeconds, owner.Shield, owner.Health);
        return ork is { } mode ? WithOrk(built, gameDir, mode, mapping, notes) : built;
    }

    /// <summary>
    /// The classes with the owner's weapon overrides (<see cref="OwnerBalance.Apply"/> against a marine of <paramref name="shield"/> and
    /// <paramref name="health"/>; weapon indices unchanged; <see cref="OwnerRows"/> set). <see cref="Load"/> applies it before the
    /// Ork, whose parity reference is then the owner-balanced bolter (maintainer's decision, 2026-10-28).
    /// </summary>
    public static MpClasses WithOwnerBalance(MpClasses classes, float tickSeconds, float shield, float health)
    {
        var (weapons, rows) = OwnerBalance.Apply(OwnerBalance.WithStalkerHeadshot(classes.Weapons), tickSeconds, shield, health);
        return classes with { Weapons = weapons, OwnerRows = rows.Count == 0 ? classes.OwnerRows : rows };
    }

    /// <summary>
    /// Ork O4: the classes with the Ork Boy appended in <paramref name="mode"/> (<see cref="OrkClass"/>, <see cref="OrkBalance"/>); the
    /// reference body is the marine blueprint's. On any failure (any non-fatal exception while reading, or an Ork the config would refuse) the
    /// classes are returned unchanged with a note starting "ork:" and ending "no Ork": a failed Ork read never drops the six classes (O5 review).
    /// </summary>
    public static MpClasses WithOrk(MpClasses classes, string gameDir, OrkBalanceMode mode, IReadOnlyDictionary<string, string>? mapping, List<string> notes)
    {
        try
        {
            BafDocument? marine = null;
            using (var archive = ObpkArchive.Open(Path.Combine(gameDir, GameplayTuning.BlueprintPackage)))
                archive.ReadEntries(e => archive.GroupName(e) == "attr_pc" && e.FileName == OrkClass.MarineBlueprint, (_, d) => marine = BafDocument.Read(d));
            if (marine is null) { notes.Add($"ork: {OrkClass.MarineBlueprint} not found; no Ork"); return classes; }
            var reference = OrkClass.ReferenceOf(classes, BodySpec.Of(GameplayTuning.FromBlueprint(marine, [], out _)), notes);
            if (reference is null) return classes;
            var data = OrkClass.Read(gameDir, mapping, notes);
            if (data.Shoota is null || data.Slugga is null || data.Choppa is null) { notes.Add("ork: the shoota, slugga or choppa could not be read; no Ork"); return classes; }
            var build = OrkBalance.Apply(data, reference, mode, new SimConfig().TickSeconds);
            var appended = OrkClass.Append(classes, data, build, notes);
            // The config's own checks (GameplayTuning.WithClasses refuses every class for one bad value): an unusable Ork is left out, not the classes.
            if (GameplayTuning.WithClasses(new SimConfig(), appended.Weapons, appended.Loadouts, appended.Thrown, appended.JumpPacks, appended.Evades, appended.Reactions,
                    appended.Perks, appended.Bodies) is null
                && GameplayTuning.WithClasses(new SimConfig(), classes.Weapons, classes.Loadouts, classes.Thrown, classes.JumpPacks, classes.Evades, classes.Reactions,
                    classes.Perks, classes.Bodies) is not null)
            {
                notes.Add("ork: the Ork's values are unusable for the simulation (a non-finite or out-of-range value); no Ork");
                return classes;
            }
            return appended;
        }
        catch (Exception e) when (e is not (StackOverflowException or AccessViolationException or OutOfMemoryException))   // as GameplayTuning.WithClasses
        {
            notes.Add($"ork: {e.GetType().Name}: {e.Message}; no Ork");
            return classes;
        }
    }

    /// <summary>
    /// The weapon with its raise (M31): <see cref="WeaponSpec.RaiseDelay"/> = its family's <see cref="HipFireSet.RaiseDelay"/>
    /// (<see cref="HipFireAnimations.FamilyOf"/>), <see cref="WeaponSpec.AlwaysReady"/> from the family's ready condition
    /// (<see cref="HipFireAnimations.AlwaysReady"/>). Without the data (or the family's set or delay) the weapon never waits.
    /// </summary>
    /// <param name="animationSet">The wargear's <c>common_data/fire_mode_primary</c> <c>animation_set</c> (<see cref="AnimationSetOf"/>): "pistol" puts the weapon in
    /// the pistol family whatever its name (anim/relic-bolter-idle: the True Grit pistol-slot bolters name "pistol" as the bolt pistol does, so their raise is
    /// the pistol's 0.167 s = 5 ticks, not the rifle's 6); null or another set: the name rule <see cref="HipFireAnimations.FamilyOf"/>.</param>
    public static WeaponSpec WithRaise(WeaponSpec w, HipFireAnimations? hipFire, List<string> notes, string? animationSet = null)
    {
        if (hipFire is null) return w;
        var family = string.Equals(animationSet, "pistol", StringComparison.OrdinalIgnoreCase) ? HipFireWeaponClass.Pistol : HipFireAnimations.FamilyOf(w.Name);
        if (hipFire.For(family) is not { } set) { notes.Add($"{w.Name}: no hip-fire data for the {family} family; the shot never waits for a raise"); return w; }
        bool always = HipFireAnimations.AlwaysReady(set, w.Name);
        if (set.RaiseDelay is not { } delay || !float.IsFinite(delay) || delay < 0f)
        {
            if (!always) notes.Add($"{w.Name}: the {family} family has no raise delay; the shot never waits for a raise");
            return w with { AlwaysReady = always };
        }
        return w with { RaiseDelay = delay, AlwaysReady = always };
    }

    /// <summary>
    /// The animation set a wargear file names for its primary fire mode (<c>common_data/fire_mode_primary/entry/animation_set</c>: "bolter", "pistol", ...;
    /// the first non-empty one), or null. The pistol-slot bolters name "pistol" (anim/relic-bolter-idle).
    /// </summary>
    public static string? AnimationSetOf(BafDocument doc)
    {
        if (doc.Root.Table("common_data")?.Table("fire_mode_primary") is not { } modes) return null;
        foreach (var e in modes.Entries)
            if (e.Type == BafType.Table && e.AsTable()["animation_set"] is { Type: BafType.String } set && set.AsString().Length > 0) return set.AsString();
        return null;
    }

    /// <summary>Whether an equipment file is hidden from the customizer (<c>ui_data/visible_in_customizer</c> false).</summary>
    static bool Hidden(BafDocument doc) => doc.Root.Table("ui_data")?["visible_in_customizer"] is { Type: BafType.Bool } v && !v.AsBool();

    static string Leaf(string path) => path.Split('\\', '/')[^1];

    /// <summary>Every <c>ebp</c> string under a <c>ranged_delivery_type</c> (projectile blueprints a wargear file names).</summary>
    static IEnumerable<string> BlueprintRefs(BafTable table, bool inDelivery = false, int depth = 0)
    {
        if (depth > 24) yield break;
        foreach (var e in table.Entries)
        {
            if (e.Type == BafType.String && inDelivery && e.Name == "ebp" && e.AsString().Length > 0) yield return e.AsString();
            else if (e.Type == BafType.Table)
                foreach (var r in BlueprintRefs(e.AsTable(), inDelivery || e.Name == "ranged_delivery_type", depth + 1)) yield return r;
        }
    }

    /// <summary>The loadouts from already-read data (testable without an install).</summary>
    /// <param name="blueprints">Projectile blueprints by the path the wargear names; null: projectile weapons are refused.</param>
    /// <param name="offerProjectileWeapons">See <see cref="OfferProjectileWeaponsByDefault"/>.</param>
    /// <param name="offerHiddenEquipment">See <see cref="OfferHiddenEquipmentByDefault"/>.</param>
    /// <param name="extractedMotion">A clip's extracted motion by name (M21: the evade rolls' paths); null: evades are not read.</param>
    /// <param name="lowestFoot">A clip's lowest foot heights (<see cref="EvadeTuning.LowestFoot"/>; the heavy hop's height); null: hops stay on the ground.</param>
    /// <param name="reactionMapping">The multiplayer reaction mapping (<see cref="ReactionTuning.ReadMapping"/>; M28); null: reaction types as written.</param>
    /// <param name="hipFire">The hip-fire graphs' data (M31: weapons' raise delays, <see cref="WithRaise"/>); null: no weapon waits.</param>
    /// <param name="meleeSpeedUpgrades">Clip speeds perks set through the graphs (<see cref="MeleeTuning.ReadSpeedUpgrades"/>; Weapons C, Fast Stomp); null: none.</param>
    /// <param name="scatterSets">The <c>climb_sway_scatter</c> tuning sets by name (weapons/spread, <see cref="SpreadTuning"/>); null: weapons have no scatter.</param>
    public static MpClasses Build(IReadOnlyList<MpClass> classes, IReadOnlyDictionary<string, BafDocument> wargear,
        IReadOnlyList<BehaviorGraph> graphs, Func<string, float> animationLength, List<string> notes,
        Func<string, BafDocument?>? blueprints = null, bool offerProjectileWeapons = OfferProjectileWeaponsByDefault,
        bool offerHiddenEquipment = OfferHiddenEquipmentByDefault, Func<string, HavokExtractedMotion?>? extractedMotion = null,
        Func<string, int, IReadOnlyList<float>?>? lowestFoot = null, IReadOnlyDictionary<string, string>? reactionMapping = null,
        HipFireAnimations? hipFire = null, IReadOnlyList<MeleeTuning.SpeedUpgrade>? meleeSpeedUpgrades = null,
        Func<string, BafDocument?>? scatterSets = null, MeleeMotion.Source? meleeReach = null)
    {
        // M43: the perks' upgrade strings (every perk file a class names), and per file the ones it requires somewhere: a spec is read
        // again with each one and each pair of them granted (UpgradeScope), and the reads that differ are its perk variants.
        var perkStrings = classes.SelectMany(c => c.Perks.Concat(c.DefaultPerk is { } d ? [d] : []))
            .Select(n => wargear.TryGetValue(n, out var pd) ? ReadPerk(n, pd).UpgradeString : null).OfType<string>().Where(u => u.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<string> PerkStringsOf(BafDocument doc) => UpgradeScope.Required(doc.Root).Where(perkStrings.Contains).ToList();
        int variantCount = 0;
        // The rig's clips are decoded once each (variant reads ask again).
        var lengths = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        var motions = new Dictionary<string, HavokExtractedMotion?>(StringComparer.OrdinalIgnoreCase);
        var innerLength = animationLength;
        animationLength = a => lengths.TryGetValue(a, out float l) ? l : lengths[a] = innerLength(a);
        if (extractedMotion is { } innerMotion) extractedMotion = a => motions.TryGetValue(a, out var m) ? m : motions[a] = innerMotion(a);

        var weapons = new List<WeaponSpec>();
        var weaponIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int Weapon(string name)
        {
            if (weaponIndex.TryGetValue(name, out int i)) return i;
            if (!wargear.TryGetValue(name, out var doc)) { notes.Add($"{name}: wargear file not found"); return weaponIndex[name] = -1; }
            var spec = WeaponTuning.Read(name, doc, notes, blueprints);
            if (spec is null) return weaponIndex[name] = -1;
            spec = SpreadTuning.With(WithRaise(spec, hipFire, notes, AnimationSetOf(doc)), doc, scatterSets, notes);   // weapons/spread; anim/relic-bolter-idle: the data's animation set
            var baseSpec = spec;
            var variants = UpgradeScope.VariantsOf(baseSpec, PerkStringsOf(doc),
                () => WeaponTuning.Read(name, doc, []/* notes of the base read only */, blueprints) is { } v ? SpreadTuning.With(WithRaise(v, hipFire, [], AnimationSetOf(doc)), doc, scatterSets, []) : null, Variants.Bytes);
            if (variants.Count > 0) { spec = spec with { Variants = variants }; variantCount += variants.Count; }
            if (spec.Delivery == DeliveryKind.Projectile && !offerProjectileWeapons)
            {
                notes.Add($"{name}: projectile weapon read but not offered (projectile simulation pending; MpClasses.OfferProjectileWeaponsByDefault)");
                return weaponIndex[name] = -1;
            }
            weapons.Add(spec);
            return weaponIndex[name] = weapons.Count - 1;
        }
        string? SlotOf(string name) => wargear.TryGetValue(name, out var d) ? d.Root.Table("common_data")?.String("slot") : null;

        // Thrown equipment, read once each: an equipment item whose utility ability delivers a projectile (grenades).
        var thrown = new List<ThrownSpec>();
        var thrownIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        (float Release, float End, string Animation)? stimTiming = null;   // perks/inert-2: read once, when a class carries stims
        int Thrown(string name)
        {
            if (thrownIndex.TryGetValue(name, out int i)) return i;
            var doc = wargear[name];
            if (StimTuning.IsStim(doc))
            {
                // perks/inert-2: combat stims, used like a thrown item (the Grenade button, the count, the cooldown) but applied to the user.
                if (Hidden(doc) && !offerHiddenEquipment)
                { notes.Add($"{name}: hidden from the customizer (visible_in_customizer false); not offered"); return thrownIndex[name] = -1; }
                stimTiming ??= StimTuning.ReadTiming(graphs, animationLength, notes) ?? (-1f, -1f, "");
                if (stimTiming.Value.Release < 0f || StimTuning.Read(name, doc, stimTiming.Value, notes) is not { } stim) return thrownIndex[name] = -1;
                var timing = stimTiming.Value;
                var stimVariants = UpgradeScope.VariantsOf(stim, PerkStringsOf(doc), () => StimTuning.Read(name, doc, timing, []), Variants.Bytes);
                if (stimVariants.Count > 0) { stim = stim with { Variants = stimVariants }; variantCount += stimVariants.Count; }
                thrown.Add(stim);
                return thrownIndex[name] = thrown.Count - 1;
            }
            if (doc.Root.Table("ability_data/ability_definitions/ability_utility/delivery") is null)
            { notes.Add($"{name}: equipment without a thrown delivery; not simulated"); return thrownIndex[name] = -1; }
            if (Hidden(doc) && !offerHiddenEquipment)
            { notes.Add($"{name}: hidden from the customizer (visible_in_customizer false); not offered"); return thrownIndex[name] = -1; }
            if (WeaponTuning.ReadThrown(name, doc, notes, blueprints) is not { } spec) return thrownIndex[name] = -1;
            var variants = UpgradeScope.VariantsOf(spec, PerkStringsOf(doc), () => WeaponTuning.ReadThrown(name, doc, [], blueprints), Variants.Bytes);
            if (variants.Count > 0) { spec = spec with { Variants = variants }; variantCount += variants.Count; }
            thrown.Add(spec);
            return thrownIndex[name] = thrown.Count - 1;
        }
        // Jump packs (base wargear with an ability_jumppack), read once each, with the jump graph's timing.
        var jumpPacks = new List<JumpPackSpec>();
        var jumpIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int JumpPack(string name)
        {
            if (jumpIndex.TryGetValue(name, out int i)) return i;
            if (JumpPackTuning.Read(name, wargear[name], notes) is not { } spec) return jumpIndex[name] = -1;
            spec = JumpPackTuning.WithTiming(spec, graphs, animationLength, notes);
            // Protocol 30: the dash attack and the sword slash (Furious Charge and the heal-on-hit perks are their variants).
            spec = JumpPackAttackTuning.With(spec, name, wargear[name], graphs, animationLength, extractedMotion, reactionMapping, notes);
            var variants = UpgradeScope.VariantsOf(spec, PerkStringsOf(wargear[name]),
                () => JumpPackTuning.Read(name, wargear[name], []) is { } v
                    ? JumpPackAttackTuning.With(JumpPackTuning.WithTiming(v, graphs, animationLength, []), name, wargear[name], graphs, animationLength, extractedMotion, reactionMapping, [])
                    : null, Variants.Bytes);
            if (variants.Count > 0) { spec = spec with { Variants = variants }; variantCount += variants.Count; }
            jumpPacks.Add(spec);
            return jumpIndex[name] = jumpPacks.Count - 1;
        }
        // Evades (base wargear with an ability_evade) of classes without a jump pack, read once each.
        var evades = new List<EvadeSpec>();
        var evadeIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int Evade(string name)
        {
            if (evadeIndex.TryGetValue(name, out int i)) return i;
            if (extractedMotion is null) { notes.Add($"{name}: no extracted motion reader; evade not read"); return evadeIndex[name] = -1; }
            if (EvadeTuning.Read(name, wargear[name], graphs, animationLength, extractedMotion, notes, lowestFoot) is not { } spec) return evadeIndex[name] = -1;
            evades.Add(spec);
            return evadeIndex[name] = evades.Count - 1;
        }
        // M44: the jump-pack dash (the pack classes' evade file), read once each.
        int Dash(string name)
        {
            string key = "dash:" + name;
            if (evadeIndex.TryGetValue(key, out int i)) return i;
            if (extractedMotion is null) { notes.Add($"{name}: no extracted motion reader; dash not read"); return evadeIndex[key] = -1; }
            if (EvadeTuning.ReadDash(name, wargear[name], graphs, animationLength, extractedMotion, notes) is not { } spec) return evadeIndex[key] = -1;
            evades.Add(spec);
            return evadeIndex[key] = evades.Count - 1;
        }
        // Perks (class_perks and default_perk), read once each.
        var perks = new List<PerkSpec>();
        var perkIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int Perk(string name)
        {
            if (perkIndex.TryGetValue(name, out int i)) return i;
            if (!wargear.TryGetValue(name, out var doc)) { notes.Add($"{name}: perk file not found"); return perkIndex[name] = -1; }
            perks.Add(ReadPerk(name, doc));
            return perkIndex[name] = perks.Count - 1;
        }
        var loadouts = new List<ClassLoadout>();
        var meleeAnimations = new List<IReadOnlyList<string>>();
        var combos = new Dictionary<string, (MeleeCombo Combo, IReadOnlyList<string> Animations)?>(StringComparer.OrdinalIgnoreCase);
        var heavyAnimations = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        (MeleeCombo Combo, IReadOnlyList<string> Animations)? Combo(string classKey, string name)
        {
            if (combos.TryGetValue(name, out var known)) return known;
            var map = MeleeMapOf(name);
            if (map is null) { notes.Add($"{classKey}: melee weapon {name} not supported yet"); return combos[name] = null; }
            if (!wargear.TryGetValue(name, out var doc)) { notes.Add($"{name}: wargear file not found"); return combos[name] = null; }
            try
            {
                var weaponMap = map(name);
                var tuning = MeleeTuning.From(graphs, animationLength, doc, weaponMap, reactionMapping, meleeSpeedUpgrades, meleeReach);
                notes.AddRange(tuning.Notes.Select(n => $"{name}: {n}"));
                heavyAnimations[name] = tuning.HeavyAnimations;
                // M29: the weapon's charge (bull rush), when the rig's extracted motion can be read (a core ability's melee has none: Weapons C).
                bool core = CoreMeleeMaps.Any(m => name.EndsWith(m.Suffix, StringComparison.OrdinalIgnoreCase));
                var rush = extractedMotion is null || core ? null : RushTuning.Read(name, doc, graphs, animationLength, extractedMotion, reactionMapping, notes);
                var combo = new MeleeCombo(name, tuning.Steps) { Heavy = tuning.Heavy, Rush = rush };
                // M43: Serrated Combat Blade, Sure Strike, Furious Charge and the heal-on-hit perks (a variant read failing is skipped).
                MeleeCombo? ReadVariant()
                {
                    try
                    {
                        var t = MeleeTuning.From(graphs, animationLength, doc, weaponMap, reactionMapping, meleeSpeedUpgrades, meleeReach);
                        var r = extractedMotion is null || core ? null : RushTuning.Read(name, doc, graphs, animationLength, extractedMotion, reactionMapping, []);
                        return new MeleeCombo(name, t.Steps) { Heavy = t.Heavy, Rush = r };
                    }
                    catch (InvalidDataException) { return null; }
                }
                // Weapons C: the perks a graph speed needs (Fast Stomp) count as required by the file too.
                var required = PerkStringsOf(doc).Concat((meleeSpeedUpgrades ?? []).Where(u => u.Graph.Equals(weaponMap.Graph, StringComparison.OrdinalIgnoreCase))
                    .Select(u => u.Upgrade).Where(perkStrings.Contains)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var variants = UpgradeScope.VariantsOf(combo, required, ReadVariant, Variants.Bytes);
                if (variants.Count > 0) { combo = combo with { Variants = variants }; variantCount += variants.Count; }
                return combos[name] = (combo, tuning.Animations);
            }
            catch (InvalidDataException e) { notes.Add($"{name}: {e.Message}"); return combos[name] = null; }
        }
        foreach (var c in classes)
        {
            // Base wargear first, then startup choices, in the data's order; every supported one is a choice, the first the default.
            var candidates = c.BaseWargear.Concat(c.StartupWargear).ToList();
            var primaries = candidates.Where(n => SlotOf(n) == "ranged").Select(Weapon).Where(i => i >= 0).Distinct().ToList();
            var pistols = candidates.Where(n => SlotOf(n) == "pistol").Select(Weapon).Where(i => i >= 0).Distinct().ToList();
            var melees = candidates.Where(n => SlotOf(n) == "melee").Distinct(StringComparer.OrdinalIgnoreCase).Select(n => Combo(c.Key, n)).OfType<(MeleeCombo Combo, IReadOnlyList<string> Animations)>().ToList();
            // Weapons C: a class without a melee weapon fights with its core ability's melee (the Devastator's and the Havoc's kick and stomp).
            if (melees.Count == 0) melees = CoreMelee(c).Select(n => Combo(c.Key, n)).OfType<(MeleeCombo Combo, IReadOnlyList<string> Animations)>().Take(1).ToList();
            int primary = primaries.Count > 0 ? primaries[0] : -1, pistol = pistols.Count > 0 ? pistols[0] : -1;
            IReadOnlyList<MeleeStep> melee = melees.Count > 0 ? melees[0].Combo.Steps : [];
            if (primary < 0) notes.Add($"{c.Key}: no supported primary weapon");
            if (melee.Count == 0) notes.Add($"{c.Key}: no melee combo");
            // Equipment in the data's order; the first supported one is the default (the original front end's default not decoded).
            var equipment = candidates.Where(n => SlotOf(n) == "equipment" && wargear.ContainsKey(n)).Distinct(StringComparer.OrdinalIgnoreCase).Select(Thrown).Where(i => i >= 0).ToList();
            int pack = c.BaseWargear.Where(n => wargear.TryGetValue(n, out var d) && JumpPackTuning.IsJumpPack(d)).Select(JumpPack).FirstOrDefault(i => i >= 0, -1);
            // Space is "Evade / Jump Pack" (locale 110693): a class with a pack jumps; the others evade.
            var perkChoices = c.Perks.Select(Perk).Where(i => i >= 0).Distinct().ToList();
            int defaultPerk = c.DefaultPerk is { } dp ? Perk(dp) : -1;
            if (defaultPerk >= 0 && !perkChoices.Contains(defaultPerk)) notes.Add($"{c.Key}: default perk {c.DefaultPerk} is not in its class_perks");
            // M44: a class with a pack dashes on a tap (its evade file read as the jump-pack dash); the others roll or hop.
            int evade = c.BaseWargear.Where(n => wargear.TryGetValue(n, out var d) && EvadeTuning.IsEvade(d)).Select(n => pack >= 0 ? Dash(n) : Evade(n)).FirstOrDefault(i => i >= 0, -1);
            loadouts.Add(new ClassLoadout(c.Key, primary, pistol, melee)
            {
                PrimaryChoices = primaries, PistolChoices = pistols, MeleeChoices = melees.Select(m => m.Combo).ToList(), EquipmentChoices = equipment,
                MeleeHeavy = melees.Count > 0 ? melees[0].Combo.Heavy : [],
                MeleeRush = melees.Count > 0 ? melees[0].Combo.Rush : null,
                JumpPack = pack, Evade = evade,
                Side = GameModes.SideOfRace(c.Race), Role = c.Role,
                PerkChoices = perkChoices, DefaultPerk = defaultPerk,
            });
            if (GameModes.SideOfRace(c.Race) == Team.None) notes.Add($"{c.Key}: race '{c.Race}' is neither side; the server puts it on the smaller side");
            meleeAnimations.Add(melees.Count > 0 ? melees[0].Animations : []);
        }
        notes.Add($"perks: {perkStrings.Count} upgrade strings; {variantCount} perk variants read (weapons, equipment, jump packs, melee)");
        return new MpClasses(classes, weapons, loadouts, meleeAnimations, notes)
        {
            Thrown = thrown, Throw = ReadThrowTiming(graphs, animationLength, notes), JumpPacks = jumpPacks, Evades = evades, HipFire = hipFire,
            Perks = perks,

            MeleeAnimationsByWeapon = combos.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value!.Value.Animations, StringComparer.OrdinalIgnoreCase),
            MeleeHeavyAnimationsByWeapon = heavyAnimations,
            Reactions = ReactionTuning.Specs(graphs, animationLength, notes),
        };
    }
}

/// <summary>The throw's timing in seconds from the press (behavior graph): when the item leaves the hand and when the throw ends.</summary>
/// <param name="Animation">The toss animation the timing comes from.</param>
public sealed record ThrowTiming(float Release, float Duration, string Animation);
