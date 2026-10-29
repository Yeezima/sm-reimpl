using System.Numerics;
using SmFormats.Baf;
using SmFormats.Havok;
using SmFormats.Obpk;
using SmSim;

namespace SmContent;

/// <summary>
/// The Ork Boy (Ork O4; docs/ork-faction-design.md section 1): a **deliberate addition** to the original's multiplayer (owner's decision
/// 2026-10-28). One synthetic class, <see cref="Key"/> (**ours**: the data have no Ork multiplayer class), built from the Exterminatus boy's
/// data: the blueprint <see cref="Blueprint"/> (health, speeds), the wargear <see cref="Slugga"/> (pistol), <see cref="Shoota"/> (gun),
/// <see cref="Choppa"/> (melee, its two-hit combo set 1, heavy strike and sprint attack as the charge), <see cref="Stikkbomb"/> (equipment),
/// <see cref="EvadeWargear"/> (the side-only dodge hop), the boy rig's graphs (<see cref="RigProfiles.OrkBoy"/>) and its ragdoll's hit
/// volumes. Perks and default perk are the Tactical's. It has no side (<see cref="Team.None"/>): it joins the side it asks for
/// (<see cref="World.AssignSide"/>). Appended after the six classes (index 6), its weapons, thrown item and evade after theirs, so the
/// marine and Chaos indices and values never change. Balance: <see cref="OrkBalance"/>. Read-only; nothing is written.
/// </summary>
public static class OrkClass
{
    public const string Key = "ork - boy", Role = "ork", Race = "ork";
    public const string Blueprint = "ork_coop_boy";
    public const string Slugga = "slugga", Shoota = "ork_coop_boy_2hand_shoota_rapid", Choppa = "ork_coop_boy_choppa", Stikkbomb = "ork_coop_boy_stikkbomb",
        EvadeWargear = "ork_coop_boy_evade_ranged";
    /// <summary>The class whose perks the Ork takes and whose gear the parity rows scale to.</summary>
    public const string Tactical = "sm - tactical";
    /// <summary>The marine player blueprint the reference body comes from.</summary>
    public const string MarineBlueprint = GameplayTuning.MarineBlueprint;

    /// <summary>The Ork's wargear files.</summary>
    public static IReadOnlyList<string> Wargear { get; } = [Slugga, Shoota, Choppa, Stikkbomb, EvadeWargear];

    /// <summary>The choppa's charge: its sprint attack (<c>ability_melee_sprint</c>; <c>Base_MELEE_Coop</c> "Sprint Attack Sequencer").</summary>
    public const string RushAbility = "ability_melee_sprint", RushGraph = "Base_MELEE_Coop", RushClip = "BOY_1HM_MEL_Atk_Sprint_01";

    /// <summary>The class row (<see cref="MpClass"/>) with the Tactical's perks.</summary>
    public static MpClass ClassOf(MpClass tactical) => new(Key, Role, Race, Wargear, [], tactical.Perks, tactical.DefaultPerk);

    // ---- reading ----

    /// <summary>Reads the Ork's data from the install (read-only); a part that cannot be read is null with a note.</summary>
    public static OrkData Read(string gameDir, IReadOnlyDictionary<string, string>? mapping, List<string> notes)
    {
        var docs = new Dictionary<string, BafDocument>(StringComparer.OrdinalIgnoreCase);
        var names = Wargear.ToHashSet(StringComparer.OrdinalIgnoreCase);
        using (var archive = ObpkArchive.Open(Path.Combine(gameDir, GameplayTuning.WargearPackage)))
            archive.ReadEntries(e => archive.GroupName(e) == "attr_pc" && names.Contains(e.FileName), (e, d) => docs[e.FileName] = BafDocument.Read(d));
        var wanted = docs.Values.SelectMany(d => ProjectileRefs(d.Root)).Select(Leaf).Append(Blueprint).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var blueprints = new Dictionary<string, BafDocument>(StringComparer.OrdinalIgnoreCase);
        using (var archive = ObpkArchive.Open(Path.Combine(gameDir, GameplayTuning.BlueprintPackage)))
            archive.ReadEntries(e => archive.GroupName(e) == "attr_pc" && wanted.Contains(e.FileName), (e, d) => blueprints[e.FileName] = BafDocument.Read(d));
        var character = RigProfiles.OrkBoy.LoadCharacter(gameDir);
        var graphs = HavokBehavior.Read(character.Pack);
        HipFireAnimations? hipFire = null;
        try { hipFire = HipFireAnimations.From(character, RigProfiles.OrkBoy); }
        catch (Exception e) when (e is InvalidDataException or KeyNotFoundException or ArgumentException or IndexOutOfRangeException)
        { notes.Add($"ork: hip fire: {e.Message}; the raise delays are the reference's"); }
        IReadOnlyList<HitVolume>? volumes = null;
        try { volumes = HitVolumeTuning.FromRagdoll(HavokRagdoll.Read(character.Pack, notes), notes); }
        catch (Exception e) when (e is InvalidDataException or KeyNotFoundException or ArgumentException or IndexOutOfRangeException)
        { notes.Add($"ork: hit volumes: {e.Message}"); }
        if (volumes is null) notes.Add("ork: no hit volumes of its own; the marine's are used");
        return From(docs, blueprints.GetValueOrDefault(Blueprint), path => blueprints.GetValueOrDefault(Leaf(path)), graphs,
            a => character.LoadAnimation(a).Animation.Duration, character.LoadExtractedMotion,
            (clip, samples) => EvadeTuning.LowestFoot(character, clip, samples), hipFire, volumes, mapping, notes, MeleeMotion.Source.From(character, notes));
    }

    /// <summary>The Ork's data from already-read files and rig accessors (testable without an install).</summary>
    public static OrkData From(IReadOnlyDictionary<string, BafDocument> wargear, BafDocument? blueprint, Func<string, BafDocument?> projectiles,
        IReadOnlyList<BehaviorGraph> graphs, Func<string, float> animationLength, Func<string, HavokExtractedMotion?> extractedMotion,
        Func<string, int, IReadOnlyList<float>?>? lowestFoot, HipFireAnimations? hipFire, IReadOnlyList<HitVolume>? volumes,
        IReadOnlyDictionary<string, string>? mapping, List<string> notes, MeleeMotion.Source? meleeReach = null)
    {
        OrkGunData? Gun(string name, HipFireWeaponClass family)
        {
            if (!wargear.TryGetValue(name, out var doc)) { notes.Add($"ork: {name} not found"); return null; }
            var gunNotes = new List<string>();
            var spec = WeaponTuning.Read(name, doc, gunNotes, projectiles);
            notes.AddRange(gunNotes.Select(n => $"ork: {n}"));
            return spec is null ? null : GunData(spec, doc, hipFire?.For(family)?.RaiseDelay);
        }
        var shoota = Gun(Shoota, HipFireWeaponClass.Rifle);
        var slugga = Gun(Slugga, HipFireWeaponClass.Pistol);

        MeleeCombo? choppa = null;
        IReadOnlyList<string> animations = [], heavyAnimations = [];
        if (wargear.TryGetValue(Choppa, out var choppaDoc))
        {
            try
            {
                var tuning = MeleeTuning.From(graphs, animationLength, choppaDoc, MeleeTuning.OrkChoppa, mapping, reach: meleeReach);
                notes.AddRange(tuning.Notes.Select(n => $"ork: {Choppa}: {n}"));
                choppa = new MeleeCombo(Choppa, tuning.Steps) { Heavy = tuning.Heavy, Rush = ReadRush(Choppa, choppaDoc, graphs, animationLength, extractedMotion, mapping, notes) };
                (animations, heavyAnimations) = (tuning.Animations, tuning.HeavyAnimations);
            }
            catch (InvalidDataException e) { notes.Add($"ork: {Choppa}: {e.Message}"); }
        }
        else notes.Add($"ork: {Choppa} not found");

        ThrownSpec? stikk = null;
        if (wargear.TryGetValue(Stikkbomb, out var stikkDoc))
        {
            var thrownNotes = new List<string>();
            stikk = WeaponTuning.ReadThrown(Stikkbomb, stikkDoc, thrownNotes, projectiles);
            notes.AddRange(thrownNotes.Select(n => $"ork: {n}"));
        }
        else notes.Add($"ork: {Stikkbomb} not found");

        EvadeSpec? evade = null;
        if (wargear.TryGetValue(EvadeWargear, out var evadeDoc))
        {
            var evadeNotes = new List<string>();
            evade = EvadeTuning.Read(EvadeTuning.OrkRig, EvadeWargear, evadeDoc, graphs, animationLength, extractedMotion, evadeNotes, lowestFoot);
            notes.AddRange(evadeNotes.Select(n => $"ork: {n}"));
        }
        else notes.Add($"ork: {EvadeWargear} not found");

        if (blueprint is null) notes.Add($"ork: blueprint {Blueprint} not found; body values are the reference's");
        return new OrkData(shoota, slugga, choppa, stikk, BodyData(blueprint), evade, volumes) { ChoppaAnimations = animations, ChoppaHeavyAnimations = heavyAnimations };
    }

    /// <summary>A gun's raw values (<see cref="OrkGunData"/>): default entries of <c>basic_weapon_tuning</c>, <c>ammunition_info</c> and <c>reload_info</c>.</summary>
    public static OrkGunData GunData(WeaponSpec spec, BafDocument doc, float? raiseDelay)
    {
        var defs = doc.Root.Table("ability_data/ability_definitions");
        var basic = defs?.Table("ability_ranged_primary") ?? defs?.Table("ability_ranged_secondary");
        var tuning = WeaponTuning.DefaultEntry(basic?.Table("delivery/basic_weapon_tuning"), "entry");
        var ammo = WeaponTuning.DefaultEntry(doc.Root.Table("common_data/ammunition/entry/by_upgrade"), null);
        var reload = WeaponTuning.DefaultEntry(doc.Root.Table("ability_data/ability_definitions/ability_reload/reload_upgrade_info"), "wargear\\reload_info");
        static float? Positive(BafTable? t, string f) => Number(t, f) is float v && float.IsFinite(v) && v > 0f ? v : null;
        static int? Count(BafTable? t, string f) => Number(t, f) is float v && float.IsFinite(v) && v >= 0f ? (int)v : null;
        bool infinite = ammo?["capacity_reserve_infinite"] is { Type: BafType.Bool } inf && inf.AsBool();
        int? clip = Count(ammo, "capacity_clip") is int c && c > 0 ? c : null;
        return new OrkGunData(spec, Positive(tuning, "shot_cooldown"), Positive(tuning, "time_between_shots"), clip, Count(ammo, "capacity_reserve"), infinite,
            Positive(reload, "triggered_reload_time"), raiseDelay is float r && float.IsFinite(r) && r >= 0f ? r : null);
    }

    /// <summary>The blueprint's body values (null without one, or for a field it lacks).</summary>
    public static OrkBodyData BodyData(BafDocument? blueprint)
    {
        var root = blueprint?.Root;
        var health = root?.Table("extensions/health_ext");
        var shield = WeaponTuning.DefaultEntry(health?.Table("armourshield_info"), "armourshield_info");
        var locomotion = root?.Table("extensions/biped_locomotion_ext");
        var standing = locomotion?.Table("speed_standing");
        var aiming = locomotion?.Table("speed_aiming");
        static float? Positive(BafTable? t, string f) => Number(t, f) is float v && float.IsFinite(v) && v > 0f ? v : null;
        static float? NonNegative(BafTable? t, string f) => Number(t, f) is float v && float.IsFinite(v) && v >= 0f ? v : null;
        return new OrkBodyData(Positive(health, "health_max"), NonNegative(health, "health_regen_delay"), Positive(health, "health_regen_rate"),
            Positive(shield, "armourshield_max"), NonNegative(shield, "armourshield_regen_delay"), Positive(shield, "armourshield_regen_rate"),
            Positive(standing, "run_speed"), Positive(standing, "strafe_speed"), Positive(standing, "back_speed"), Positive(standing, "sprint_speed"), Positive(standing, "walk_speed"),
            Positive(aiming, "run_speed"), Positive(aiming, "strafe_speed"), Positive(aiming, "back_speed"));
    }

    /// <summary>
    /// The choppa's sprint attack as the Ork's charge (design section 1.1; reading **ours**): <see cref="RushAbility"/>'s hits (damage,
    /// shield part, reactions, reach, spacing) and the sequencer playing <see cref="RushClip"/> in <see cref="RushGraph"/>: its extracted
    /// motion; contacts count until its hit event (<c>ACTN_Hit_Targets_Multiplayer</c>), walls end it from half that; the stick and
    /// other input end it from <c>BRANCH_Run</c>, it ends at <c>BRANCH_Melee_End</c>. The boy has no collision-stop clip: the stop
    /// stands still for the attack's time from its hit to <c>BRANCH_Run</c> playing the same clip (ours). Hyper armour for half the
    /// attack part as the marine's (M32). Null with a note when anything is missing.
    /// </summary>
    public static RushSpec? ReadRush(string name, BafDocument wargear, IReadOnlyList<BehaviorGraph> graphs, Func<string, float> animationLength,
        Func<string, HavokExtractedMotion?> extractedMotion, IReadOnlyDictionary<string, string>? mapping, List<string> notes)
    {
        if (wargear.Root.Table($"ability_data/ability_definitions/{RushAbility}") is not BafTable ability) { notes.Add($"ork: {name}: no {RushAbility}; no charge"); return null; }
        var seq = graphs.FirstOrDefault(g => g.Name.Equals(RushGraph, StringComparison.OrdinalIgnoreCase))?.Sequencers
            .FirstOrDefault(s => s.Animations.Any(a => a.Equals(RushClip, StringComparison.OrdinalIgnoreCase)));
        if (seq is null) { notes.Add($"ork: {name}: no {RushGraph} sequencer plays {RushClip}; no charge"); return null; }
        var primaryActions = ability.Table("primary_target_actions/hit_sim/actions");
        var secondaryActions = ability.Table("secondary_target_actions/hit_sim/actions");
        var primary = WeaponTuning.DamageOf(UpgradeScope.Command(primaryActions, "do_damage"), name, notes);
        if (primary is null) { notes.Add($"ork: {name}: {RushAbility} has no primary damage; no charge"); return null; }
        var secondary = WeaponTuning.DamageOf(UpgradeScope.Command(secondaryActions, "do_damage"), name, notes) ?? new DamageSpec(0f, 0f, 1f, 1f, 1f, 1f, false);
        float length;
        HavokExtractedMotion? motion;
        try { length = animationLength(RushClip); motion = extractedMotion(RushClip); }
        catch (Exception e) when (e is InvalidDataException or KeyNotFoundException or NotSupportedException or ArgumentException or IndexOutOfRangeException or OverflowException)
        { notes.Add($"ork: {name}: {RushClip}: {e.Message}; no charge"); return null; }
        if (!float.IsFinite(length) || length <= 0f || motion is null || motion.Samples.Count < 2)
        { notes.Add($"ork: {name}: {RushClip} has no usable length or extracted motion; no charge"); return null; }
        if (Vector3.Dot(Vector3.Normalize(motion.Up), Vector3.UnitZ) < 0.999f || Vector3.Dot(Vector3.Normalize(motion.Forward), Vector3.UnitY) < 0.999f)
        { notes.Add($"ork: {name}: {RushClip}'s motion frame is not +Z up, +Y forward; no charge"); return null; }
        float? Tag(string kind, string tag) => seq.Tags.Where(t => t.Kind == kind && t.Name == tag).Select(t => (float?)Math.Clamp(t.Start, 0f, 1f)).Min();
        float end = (Tag("Branch", "BRANCH_Melee_End") ?? 1f) * length;
        float moveAt = MathF.Min(end, (Tag("Branch", "BRANCH_Run") ?? 1f) * length);
        float hit = MathF.Min(moveAt, (Tag("Event", "ACTN_Hit_Targets_Multiplayer") ?? Tag("Event", "ACTN_Hit_Primary_Target") ?? 0.4f) * length);
        var first = new Vector2(motion.Samples[0].X, motion.Samples[0].Y);
        var path = motion.Samples.Select(v => new Vector2(v.X, v.Y) - first).ToArray();
        var rush = new RushMove(RushClip, path, length, end, moveAt, moveAt, moveAt);
        float stop = MathF.Max(1f / 30f, moveAt - hit);
        var collision = new RushMove(RushClip, [Vector2.Zero, Vector2.Zero], stop, stop, stop, stop, stop);
        float range = ability.Table("ability_metadata/generic_metadata")?.Float("max_range") is float m && float.IsFinite(m) && m > 0f ? m : 4f;
        float interval = Number(ability, "min_time_between_activations") is float i && float.IsFinite(i) && i >= 0f && i <= 8f ? i : 1f;
        notes.Add($"ork: {name}: the charge is the sprint attack {RushClip}: {path[^1].Y:F1} m forward over {end:F2} s, hit at {hit:F2} s, control back at {moveAt:F2} s (ours)");
        return new RushSpec(RushAbility, rush, collision, primary, ReactionTuning.HitReaction(primaryActions, mapping), secondary,
            ReactionTuning.HitReaction(secondaryActions, mapping), range, interval)
        {
            ContactUntil = hit, WallFrom = 0.5f * hit, HyperArmourUntil = 0.5f * moveAt,
        };
    }

    // ---- building the class ----

    /// <summary>The Tactical's gear and body (its class defaults) the parity rows scale to; null with a note when the class or a part is missing.</summary>
    public static OrkReference? ReferenceOf(MpClasses classes, BodySpec body, List<string> notes)
    {
        int t = classes.Loadouts.ToList().FindIndex(l => l.Name.Equals(Tactical, StringComparison.OrdinalIgnoreCase));
        if (t < 0) { notes.Add($"ork: no class {Tactical}; no Ork"); return null; }
        var tac = classes.Loadouts[t];
        WeaponSpec? Weapon(int i) => i >= 0 && i < classes.Weapons.Count ? classes.Weapons[i] : null;
        var bolter = Weapon(tac.Primary);
        var pistol = Weapon(tac.Pistol);
        var knife = tac.MeleeChoices.Count > 0 ? tac.MeleeChoices[0] : null;
        var frag = tac.EquipmentChoices.Count > 0 && tac.EquipmentChoices[0] < classes.Thrown.Count ? classes.Thrown[tac.EquipmentChoices[0]] : null;
        var evade = tac.Evade >= 0 && tac.Evade < classes.Evades.Count ? classes.Evades[tac.Evade] : null;
        if (bolter is null || pistol is null || knife is null || frag is null) { notes.Add($"ork: {Tactical} lacks a primary, pistol, melee combo or grenade; no Ork"); return null; }
        return new OrkReference(bolter, pistol, knife, frag, evade, body);
    }

    /// <summary>
    /// The classes with the Ork Boy appended (class index = the old count; weapons, thrown item and evade after the others'; its body
    /// <see cref="MpClasses.Bodies"/>[0]); unchanged with a note when its gear is missing or the Tactical class is not there.
    /// </summary>
    public static MpClasses Append(MpClasses classes, OrkData data, OrkBuild build, List<string> notes)
    {
        int t = classes.Classes.ToList().FindIndex(c => c.Key.Equals(Tactical, StringComparison.OrdinalIgnoreCase));
        if (t < 0 || t >= classes.Loadouts.Count) { notes.Add($"ork: no class {Tactical}; no Ork"); return classes; }
        var tac = classes.Loadouts[t];
        var weapons = classes.Weapons.ToList();
        int primary = weapons.Count; weapons.Add(build.Shoota);
        int pistol = weapons.Count; weapons.Add(build.Slugga);
        var thrown = classes.Thrown.ToList();
        int stikk = -1;
        if (build.Stikk is not null) { stikk = thrown.Count; thrown.Add(build.Stikk); }
        var evades = classes.Evades.ToList();
        int evade = -1;
        if (build.Evade is not null) { evade = evades.Count; evades.Add(build.Evade); }
        var bodies = classes.Bodies.ToList();
        int body = bodies.Count; bodies.Add(build.Body);
        var loadout = new ClassLoadout(Key, primary, pistol, build.Choppa.Steps)
        {
            PrimaryChoices = [primary], PistolChoices = [pistol], MeleeChoices = [build.Choppa], EquipmentChoices = stikk >= 0 ? [stikk] : [],
            MeleeHeavy = build.Choppa.Heavy, MeleeRush = build.Choppa.Rush, Evade = evade, Side = Team.None, Role = Role,
            PerkChoices = tac.PerkChoices, DefaultPerk = tac.DefaultPerk, Body = body, Race = SmSim.Race.Orks,   // Ork O5: the Ork look on either side
        };
        notes.Add($"ork: {Key} appended as class {classes.Loadouts.Count} ({build.Mode}): {build.Shoota.Name}, {build.Slugga.Name}, {build.Choppa.Wargear}"
            + (build.Stikk is { } s ? $", {s.Name}" : ", no grenade") + (build.Evade is { } e ? $", {e.Name} (side-only)" : ", no evade")
            + (build.Choppa.Rush is null ? ", no charge" : ", charge") + $"; {build.Body.HitVolumes?.Count.ToString() ?? "the marine's"} hit volumes");
        return classes with
        {
            Classes = [.. classes.Classes, ClassOf(classes.Classes[t])],
            Weapons = weapons, Loadouts = [.. classes.Loadouts, loadout],
            MeleeAnimations = [.. classes.MeleeAnimations, data.ChoppaAnimations],
            MeleeAnimationsByWeapon = new Dictionary<string, IReadOnlyList<string>>(classes.MeleeAnimationsByWeapon, StringComparer.OrdinalIgnoreCase) { [Choppa] = data.ChoppaAnimations },
            MeleeHeavyAnimationsByWeapon = new Dictionary<string, IReadOnlyList<string>>(classes.MeleeHeavyAnimationsByWeapon, StringComparer.OrdinalIgnoreCase) { [Choppa] = data.ChoppaHeavyAnimations },
            Thrown = thrown, Evades = evades, Bodies = bodies, Ork = build,
        };
    }

    static string Leaf(string path) => path.Split('\\', '/')[^1];

    static float? Number(BafTable? t, string name) => t?[name] switch
    {
        { Type: BafType.Float } e => e.AsFloat(),
        { Type: BafType.Int } e => e.Raw,
        _ => null,
    };

    /// <summary>Every <c>ebp</c> string under a <c>ranged_delivery_type</c> (the projectile blueprints a wargear file names).</summary>
    static IEnumerable<string> ProjectileRefs(BafTable table, bool inDelivery = false, int depth = 0)
    {
        if (depth > 24) yield break;
        foreach (var e in table.Entries)
        {
            if (e.Type == BafType.String && inDelivery && e.Name == "ebp" && e.AsString().Length > 0) yield return e.AsString();
            else if (e.Type == BafType.Table)
                foreach (var r in ProjectileRefs(e.AsTable(), inDelivery || e.Name == "ranged_delivery_type", depth + 1)) yield return r;
        }
    }
}
