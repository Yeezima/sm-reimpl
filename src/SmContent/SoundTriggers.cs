using System.Numerics;
using SmFormats.Baf;
using SmFormats.Bod;
using SmFormats.Havok;
using SmFormats.Obpk;

namespace SmContent;

/// <summary>
/// A <c>post_sound_event</c> command (M26b) in a blueprint or wargear file: where it sits (<see cref="Path"/>), what triggers
/// it (<see cref="Trigger"/>: the nearest enclosing table that is not structural, e.g. a behaviour tag under
/// <c>triggered_actions</c> such as <c>evt_left_foot</c>, or <c>fire_audio</c>, <c>death_action</c>, <c>action_apex</c>,
/// <c>armourshield_broken</c>, <c>10% health</c>), the ability and charge stage it belongs to, the upgrades it needs, and its
/// fields as the data has them: the event to post at the start and the one at the end of the trigger, whether the terrain's
/// material sets the surface switch, whether it is a footstep, whether only the local player hears it, the delay (ms in the
/// file; <see cref="DelaySeconds"/> converts, interpretation) and the trigger point. <see cref="Parameters"/>: numeric fields of
/// the tables between the trigger and the command (e.g. <c>distance_from_apex_to_land</c> of a landing entry).
/// </summary>
public sealed record SoundBinding(string Path, string Trigger, string Ability, string ChargeStage, IReadOnlyList<string> RequiredUpgrades,
    string StartEvent, string StopEvent, bool UseTerrainMaterial, bool IsFootstep, bool OnlyIfOnPlayer, float DelayMilliseconds, string TriggerPoint,
    IReadOnlyDictionary<string, float> Parameters)
{
    public bool IsDefault => RequiredUpgrades.Count == 0;
    public float DelaySeconds => DelayMilliseconds / 1000f;
    public bool UnderTriggeredActions => Path.Contains("/triggered_actions/", StringComparison.Ordinal);

    /// <summary>Tables that only group commands; the trigger is the nearest ancestor not among them.</summary>
    static readonly HashSet<string> Structural = new(StringComparer.Ordinal)
    {
        "commands", "actions", "actions_by_upgrade", "entry", "action", "delayed_action", "in_action", "landing_action_entry",
        "post_sound_event", "by_upgrade",
    };

    /// <summary>Every <c>post_sound_event</c> command in a file, in file order.</summary>
    public static IReadOnlyList<SoundBinding> Collect(BafTable root)
    {
        var found = new List<SoundBinding>();
        WalkCommands(root, (child, kind, path, upgrades, tables) =>
        {
            if (!kind.EndsWith("post_sound_event", StringComparison.OrdinalIgnoreCase)) return false;
            int trigger = TriggerIndex(path);
            var parameters = new Dictionary<string, float>(StringComparer.Ordinal);
            for (int i = Math.Max(0, trigger); i < tables.Count - 1; i++)
                foreach (var x in tables[i].Entries)
                    if (x.Type == BafType.Float && float.IsFinite(x.AsFloat())) parameters.TryAdd(x.Name, x.AsFloat());
            found.Add(new SoundBinding(string.Join("/", path), trigger >= 0 ? path[trigger] : "", AbilityOf(path), StageOf(path), upgrades.Distinct().ToList(),
                child.String("start_event_name") ?? "", child.String("stop_event_name") ?? "", Flag(child, "use_terrain_material"), Flag(child, "is_footstep_sound"),
                Flag(child, "apply_only_if_on_player"), child.Float("time_delay") ?? (child["time_delay"] is { Type: BafType.Int } d ? d.Raw : 0f),
                child.String("trigger_point") ?? "", parameters));
            return true;
        });
        return found;
    }

    /// <summary>
    /// Walks every table with a <c>$ref</c> kind (a command or a template), calling <paramref name="visit"/> with the table, its
    /// kind, the path of table names down to it, the upgrades required on the way and the tables on the way; when it returns
    /// true the table is taken (its children are not walked).
    /// </summary>
    internal static void WalkCommands(BafTable root, Func<BafTable, string, List<string>, List<string>, List<BafTable>, bool> visit) =>
        Walk(root, [], [], [], visit, 0);

    static void Walk(BafTable table, List<string> path, List<string> upgrades, List<BafTable> tables,
        Func<BafTable, string, List<string>, List<string>, List<BafTable>, bool> visit, int depth)
    {
        if (depth > 48) return;
        foreach (var e in table.Entries)
        {
            if (e.Type != BafType.Table) continue;
            var child = e.AsTable();
            string kind = child.String("$ref") ?? "";
            path.Add(e.Name);
            tables.Add(child);
            var required = child.Table("required_upgrade_name/required_list")?.Entries.Where(x => x.Type == BafType.String).Select(x => x.AsString()).ToList() ?? [];
            upgrades.AddRange(required);
            if (!visit(child, kind, path, upgrades, tables)) Walk(child, path, upgrades, tables, visit, depth + 1);
            upgrades.RemoveRange(upgrades.Count - required.Count, required.Count);
            tables.RemoveAt(tables.Count - 1);
            path.RemoveAt(path.Count - 1);
        }
    }

    internal static int TriggerIndex(List<string> path)
    {
        for (int i = path.Count - 2; i >= 0; i--)
            if (!Structural.Contains(path[i])) return i;
        return -1;
    }

    internal static string AbilityOf(List<string> path)
    {
        int at = path.IndexOf("ability_definitions");
        return at >= 0 && at + 1 < path.Count - 1 ? path[at + 1] : "";
    }

    internal static string StageOf(List<string> path)
    {
        int at = path.IndexOf("charge_states");
        return at >= 0 && at + 1 < path.Count - 1 ? path[at + 1] : "";
    }

    internal static bool Flag(BafTable t, string name) => t[name] is { Type: BafType.Bool } e && e.AsBool();
}

/// <summary>The sound commands of one file (a blueprint or wargear), indexed by trigger (case-insensitive).</summary>
public sealed partial class SoundFile
{
    public string Name { get; }
    public IReadOnlyList<SoundBinding> Bindings { get; }
    readonly Dictionary<string, List<SoundBinding>> _byTrigger = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Every key under the file's <c>triggered_actions</c> tables (behaviour tags it reacts to), with or without a sound command
    /// (e.g. the jump pack's <c>actn_take_off</c>: area effects, an effect and a rumble, no sound). Case-insensitive.
    /// </summary>
    public IReadOnlySet<string> TriggeredActions { get; }

    public SoundFile(string name, IReadOnlyList<SoundBinding> bindings, IEnumerable<string>? triggeredActions = null)
    {
        Name = name;
        Bindings = bindings;
        TriggeredActions = new HashSet<string>(triggeredActions ?? [], StringComparer.OrdinalIgnoreCase);
        foreach (var b in bindings)
        {
            if (!_byTrigger.TryGetValue(b.Trigger, out var list)) _byTrigger[b.Trigger] = list = new();
            list.Add(b);
        }
    }

    /// <summary>The default (no upgrade) commands of a trigger: a behaviour tag (<c>EVT_Left_Foot</c>) or a table name (<c>fire_audio</c>).</summary>
    public IReadOnlyList<SoundBinding> Of(string trigger) =>
        _byTrigger.TryGetValue(trigger, out var list) ? list.Where(b => b.IsDefault).ToList() : [];

    /// <summary>A behaviour tag's commands (only those under <c>triggered_actions</c>).</summary>
    public IReadOnlyList<SoundBinding> Tag(string tag) =>
        _byTrigger.TryGetValue(tag, out var list) ? list.Where(b => b.IsDefault && b.UnderTriggeredActions).ToList() : [];

    public IEnumerable<string> Triggers => _byTrigger.Keys;

    /// <summary>
    /// A shot's fire sounds (<c>fire_audio</c> of a delivery): stage 0 the basic shot (outside charge stages; the primary
    /// ability's first, else the secondary's), stage k the charge stage named <paramref name="chargeStage"/>.
    /// </summary>
    public IReadOnlyList<SoundBinding> Fire(string? chargeStage)
    {
        var fire = Of("fire_audio");
        if (chargeStage is { Length: > 0 }) return fire.Where(b => b.ChargeStage == chargeStage).ToList();
        foreach (var ability in new[] { "ability_ranged_primary", "ability_ranged_secondary", "ability_utility" })
        {
            var basic = fire.Where(b => b.Ability == ability && b.ChargeStage.Length == 0).ToList();
            if (basic.Count > 0) return basic;
        }
        return fire.Where(b => b.ChargeStage.Length == 0).ToList();
    }
}

/// <summary>
/// The multiplayer sound triggers (M26b), read at runtime from the install: per file the <c>post_sound_event</c> commands
/// (players' blueprints, the wargear the configuration names, the projectile blueprints they launch), the hit sound of every
/// material effect container they name (<c>sfx/start_event_name</c>; the surface sets <c>material_type</c>), and the
/// behaviour tags of the marine rig's clips (clip → tag name and time as a fraction of the clip) that the
/// <c>triggered_actions</c> are keyed by.
/// </summary>
public sealed partial class SoundTriggers
{
    public required IReadOnlyDictionary<string, SoundFile> Files { get; init; }
    /// <summary>Hit sounds by container path (as the wargear names them) and by base name.</summary>
    public required IReadOnlyDictionary<string, string> ContainerSounds { get; init; }
    /// <summary>
    /// Per file (weapon wargear, projectile blueprint) the container of its first default impact effect (<c>hit_fx</c>, else
    /// <c>initial_tracer_fx</c>, else <c>detonate_action</c>), as <see cref="ShotEffects.From"/> picks the basic shot's.
    /// </summary>
    public IReadOnlyDictionary<string, string> HitContainers { get; init; } = new Dictionary<string, string>();

    /// <summary>The hit sound event of a file's impacts (<see cref="HitContainers"/> → <see cref="ContainerSound"/>), or null.</summary>
    public string? HitSound(string file) => HitContainers.TryGetValue(file, out var c) ? ContainerSound(c) : null;
    /// <summary>
    /// feel/melee-charge-2: a melee wargear's hit effect container by file: the first default <c>trigger_material_effect</c> of a light attack's
    /// (<c>ability_melee_x…</c>) <c>primary_target_actions/hit_fx</c> (the power sword's <c>…\melee_weapons\power_sword</c>, the chainsword's
    /// <c>…\chainsword</c>); not the file's first <c>hit_fx</c>, which is the charge's secondary kick.
    /// </summary>
    public IReadOnlyDictionary<string, string> MeleeHitContainers { get; init; } = new Dictionary<string, string>();
    /// <summary>
    /// feel/melee-charge-2: the sound of a melee weapon's hit on a player: its <see cref="MeleeHitContainers"/> container's <c>sfx</c> event (the power sword's
    /// <c>play_sm_power_sword_hit</c>, the chainsword's <c>play_sm_chain_sword_hit</c>), or null.
    /// </summary>
    public string? MeleeHitSound(string file) => MeleeHitContainers.TryGetValue(file, out var c) ? ContainerSound(c) : null;
    /// <summary>
    /// fx/melee-presentation: a wargear file's charge impact container by file: the first default <c>trigger_material_effect</c> of the bull rush's (<c>ability_bullrush_01</c>, the
    /// melee weapons) or the dash attack's (<c>ability_dash_attack</c>, the jump packs) <c>primary_target_actions</c>: <c>…\melee_weapons\body_smash</c> in both (the upgraded entries use another).
    /// </summary>
    public IReadOnlyDictionary<string, string> ChargeHitContainers { get; init; } = new Dictionary<string, string>();
    /// <summary>
    /// fx/melee-presentation: the sound of a charge hit on a player: its <see cref="ChargeHitContainers"/> container's <c>sfx</c> event (<c>body_smash</c>: <c>play_sm_bull_rush_smash</c>;
    /// the wargear's own <c>post_sound_event</c> there, <c>play_sm_sk01_bodyslam</c>, is in no bank of this install), or null.
    /// </summary>
    public string? ChargeHitSound(string? file) => file is not null && ChargeHitContainers.TryGetValue(file, out var c) ? ContainerSound(c) : null;
    /// <summary>fx/melee-presentation: the charge impact container among a file's effect bindings (see <see cref="ChargeHitContainers"/>), or null.</summary>
    public static string? ChargeHitContainer(IEnumerable<FxBinding> fx) =>
        fx.FirstOrDefault(b => b.IsDefault && b.Material is { Container.Length: > 0 } && b.Path.Contains("/primary_target_actions/", StringComparison.Ordinal)
            && (b.Ability.Equals("ability_bullrush_01", StringComparison.OrdinalIgnoreCase) || b.Ability.Equals("ability_dash_attack", StringComparison.OrdinalIgnoreCase)))?.Material!.Container;
    /// <summary>Behaviour Action tags per clip name (case-insensitive): tag name and start as a fraction of the clip.</summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<(string Tag, float At)>> ClipTags { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }
    /// <summary>
    /// Ork O8: the clips of the Ork rig whose names the marine rig does not have (the rigs' names differ: <c>BOY_*</c>). <see cref="ReloadTags"/> scans one rig's clips only, so the
    /// Ork's reload clip can never be picked for a marine's weapon (or the other way round) once both rigs' tags share <see cref="ClipTags"/>.
    /// </summary>
    public IReadOnlyCollection<string> OrkOnlyClips { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public SoundFile? File(string name) => Files.GetValueOrDefault(name);

    /// <summary>The player blueprints of both sides and the Ork Boy's (their sounds: footsteps, death, spawn, armour, landing; the Ork's: Ork O8, <see cref="OrkSounds"/>).</summary>
    public static readonly IReadOnlyList<string> PlayerBlueprints = [GameplayTuning.MarineBlueprint, GameplayTuning.ChaosBlueprint, OrkSounds.Blueprint];

    /// <summary>Every wargear file a configuration uses: weapons, thrown items, jump packs, evades and melee choices.</summary>
    public static IReadOnlyList<string> WargearNames(SmSim.SimConfig c) =>
        c.Weapons.Select(w => w.Name).Concat(c.Thrown.Select(t => t.Name)).Concat(c.JumpPacks.Select(j => j.Name)).Concat(c.Evades.Select(e => e.Name))
            .Concat(c.Classes.SelectMany(k => k.MeleeChoices.Select(m => m.Wargear))).Where(n => !string.IsNullOrEmpty(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// A weapon's reload sounds in time (M26b): the clip named <c>*reload*</c> whose action tags match the most of the file's
    /// <c>triggered_actions</c> (e.g. the bolter's <c>SM_RNG_Bolter_Reload_UB_S_01</c>: remove clip, load clip, pull and release the
    /// bolt), as (tag, fraction of the clip). **Interpretation**: the client plays no reload clip, so the fractions are spread
    /// over the simulation's reload time. Empty when no clip matches.
    /// </summary>
    public IReadOnlyList<(string Tag, float At)> ReloadTags(SoundFile weapon, bool orkRig = false)
    {
        IReadOnlyList<(string Tag, float At)> best = [];
        int bestScore = 0;
        foreach (var (clip, tags) in ClipTags.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!clip.Contains("reload", StringComparison.OrdinalIgnoreCase) || OrkOnlyClips.Contains(clip) != orkRig) continue;
            var matching = tags.Where(t => weapon.Tag(t.Tag).Count > 0).ToList();
            if (matching.Count > bestScore) { best = matching; bestScore = matching.Count; }
        }
        return best;
    }

    /// <summary>
    /// One reload plays one sample once (owner, 2026-10-30, the stalker bolter: <c>Clip_Load</c> and <c>Bolt_Release</c> post events that resolve to the same sample): of the tags whose
    /// whole set of samples (<paramref name="samplesOf"/>: the wem ids of the tag's bindings) equals that of a later tag of another name, only the last stays, so the sample plays at the later timing.
    /// A tag the clip carries twice (the bolt pistol's load tag at 0.283 and 0.4) is the clip's own repeat and stays. Tags with different samples, or with no known samples, are untouched (the bolter's two <c>play_sm_reload_foley</c> posts sit in tags with other samples too: unchanged).
    /// </summary>
    public static IReadOnlyList<(string Tag, float At)> OncePerSample(IReadOnlyList<(string Tag, float At)> tags, Func<string, IReadOnlyCollection<uint>> samplesOf)
    {
        string Key(string tag) { var w = samplesOf(tag); return w.Count == 0 ? "" : string.Join(",", w.Distinct().Order()); }
        var keys = tags.Select(t => Key(t.Tag)).ToList();
        var kept = new List<(string Tag, float At)>();
        for (int i = 0; i < tags.Count; i++)
            {
            bool later = false;
            for (int j = i + 1; j < tags.Count && keys[i].Length > 0; j++)
                if (keys[j] == keys[i] && !string.Equals(tags[j].Tag, tags[i].Tag, StringComparison.OrdinalIgnoreCase)) later = true;
            if (!later) kept.Add(tags[i]);
        }
        return kept;
    }

    /// <summary>The keys of every <c>triggered_actions</c> table in a file (any depth), in file order.</summary>
    public static IReadOnlyList<string> TriggeredActionKeys(BafTable root)
    {
        var keys = new List<string>();
        void Walk(BafTable t, int depth)
        {
            if (depth > 48) return;
            foreach (var e in t.Entries)
            {
                if (e.Type != BafType.Table) continue;
                if (e.Name == "triggered_actions")
                    foreach (var k in e.AsTable().Entries.Where(x => x.Type == BafType.Table)) keys.Add(k.Name);
                Walk(e.AsTable(), depth + 1);
            }
        }
        Walk(root, 0);
        return keys;
    }

    /// <summary>Every event name the triggers can post (start and stop events, container hit sounds), sorted.</summary>
    public IReadOnlyList<string> EventNames() =>
        Files.Values.SelectMany(f => f.Bindings).SelectMany(b => new[] { b.StartEvent, b.StopEvent }).Concat(ContainerSounds.Values)
            .Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The hit sound event of a material effect container path (null if unknown).</summary>
    public string? ContainerSound(string? container)
    {
        if (string.IsNullOrEmpty(container)) return null;
        if (ContainerSounds.TryGetValue(container, out var s)) return s;
        return ContainerSounds.GetValueOrDefault(container.Split('\\', '/')[^1]);
    }

    /// <summary>The first <c>trigger_material_effect</c> container of a file's trigger (e.g. a weapon's <c>hit_fx</c>), from <see cref="FxBinding"/>.</summary>
    public static string? ContainerOf(BafDocument doc, string evt) =>
        FxBinding.Collect(doc.Root).FirstOrDefault(b => b.IsDefault && b.Event == evt && b.Material is not null)?.Material?.Container;

    /// <summary>
    /// Reads the files: <paramref name="wargear"/> (names in <c>wargear.oppc</c>), the given player blueprints and the projectile
    /// blueprints the wargear names (<c>ebps.oppc</c>), the containers they name, and the rig's behaviour tags.
    /// </summary>
    public static SoundTriggers Load(string gameDir, IEnumerable<string> wargear, IEnumerable<string> blueprints, HavokPackfile? rig = null) =>
        Load(gameDir, wargear, blueprints, rig is null ? null : HavokBehavior.Read(rig));

    /// <summary>As above, with the rig's behaviour graphs already read (<see cref="HavokBehavior.Read"/>).</summary>
    public static SoundTriggers Load(string gameDir, IEnumerable<string> wargear, IEnumerable<string> blueprints, IReadOnlyList<BehaviorGraph>? graphs,
        IReadOnlyList<BehaviorGraph>? orkGraphs = null)
    {
        var notes = new List<string>();
        var docs = new Dictionary<string, BafDocument>(StringComparer.OrdinalIgnoreCase);
        var wanted = wargear.Where(n => n.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        using (var archive = ObpkArchive.Open(Path.Combine(gameDir, GameplayTuning.WargearPackage)))
            archive.ReadEntries(e => archive.GroupName(e) == "attr_pc" && wanted.Contains(e.FileName), (e, d) => docs.TryAdd(e.FileName, BafDocument.Read(d)));
        foreach (var n in wanted.Where(n => !docs.ContainsKey(n))) notes.Add($"wargear {n} not found; no sounds for it");
        var ebps = blueprints.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var d in docs.Values.ToList()) foreach (var p in EffectCatalogue.Strings(d.Root, "ebp")) ebps.Add(Leaf(p));
        using (var archive = ObpkArchive.Open(Path.Combine(gameDir, GameplayTuning.BlueprintPackage)))
            archive.ReadEntries(e => archive.GroupName(e) == "attr_pc" && ebps.Contains(e.FileName), (e, d) => docs.TryAdd(e.FileName, BafDocument.Read(d)));
        var containers = docs.Values.SelectMany(d => EffectCatalogue.Strings(d.Root, "effect_container")).Select(Leaf).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sounds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using (var archive = ObpkArchive.Open(Path.Combine(gameDir, EffectCatalogue.MaterialEffectPackage)))
            archive.ReadEntries(e => archive.GroupName(e) == "attr_pc" && containers.Contains(e.FileName), (e, d) =>
            {
                // Base names repeat (a Space Marine and a Chaos variant); the first keeps the name (interpretation, as EffectCatalogue).
                if (BafDocument.Read(d).Root.Table("sfx")?.String("start_event_name") is { Length: > 0 } s) sounds.TryAdd(e.FileName, s);
            });
        foreach (var path in docs.Values.SelectMany(d => EffectCatalogue.Strings(d.Root, "effect_container")).Distinct(StringComparer.OrdinalIgnoreCase))
            if (sounds.TryGetValue(Leaf(path), out var s)) sounds.TryAdd(path, s);
        var built = Build(docs, sounds, graphs, notes, orkGraphs);
        var hits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var meleeHits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var chargeHits = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, doc) in docs)
        {
            var fx = FxBinding.Collect(doc.Root);
            foreach (var evt in new[] { "hit_fx", "initial_tracer_fx", "detonate_action" })
                if (fx.FirstOrDefault(b => b.IsDefault && b.Event == evt && b.Material is { Container.Length: > 0 }) is { } hit) { hits[name] = hit.Material!.Container; break; }
            // feel/melee-charge-2: a melee weapon's own hit container (a light attack's primary hit), for its hit sound.
            if (fx.Where(b => b.IsDefault && b.Event == "hit_fx" && b.Material is { Container.Length: > 0 } && b.Ability.StartsWith("ability_melee_x", StringComparison.OrdinalIgnoreCase)
                    && b.Path.Contains("/primary_target_actions/", StringComparison.Ordinal))
                .OrderBy(b => b.Ability, StringComparer.Ordinal).FirstOrDefault() is { } melee)
                meleeHits[name] = melee.Material!.Container;
            if (ChargeHitContainer(fx) is { } chargeHit) chargeHits[name] = chargeHit;
        }
        // Ork O8: the Ork class is ours (no entry in global's class table); its voice is the blueprint's audio_ext character.
        var speechCharacters = new Dictionary<string, string>(LoadSpeechCharacters(gameDir, notes), StringComparer.OrdinalIgnoreCase);
        if (docs.TryGetValue(OrkSounds.Blueprint, out var boy) && OrkSounds.SpeechCharacter(boy) is { } boyVoice) speechCharacters[OrkClass.Key] = boyVoice;
        return new SoundTriggers
        {
            Files = built.Files, ContainerSounds = built.ContainerSounds, ClipTags = built.ClipTags, Notes = built.Notes, HitContainers = hits, MeleeHitContainers = meleeHits, ChargeHitContainers = chargeHits,
            SpeechCharacters = speechCharacters, OrkOnlyClips = built.OrkOnlyClips,
        };
    }

    /// <summary>From already-read files (testable without an install).</summary>
    public static SoundTriggers Build(IReadOnlyDictionary<string, BafDocument> docs, IReadOnlyDictionary<string, string> containerSounds,
        IReadOnlyList<BehaviorGraph>? graphs, List<string>? notes = null, IReadOnlyList<BehaviorGraph>? orkGraphs = null)
    {
        notes ??= [];
        var files = new Dictionary<string, SoundFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, doc) in docs) files[name] = new SoundFile(name, SoundBinding.Collect(doc.Root), TriggeredActionKeys(doc.Root)) { Speech = SpeechBinding.Collect(doc.Root) };
        var tags = new Dictionary<string, List<(string, float)>>(StringComparer.OrdinalIgnoreCase);
        void Collect(IReadOnlyList<BehaviorGraph>? source)
        {
            foreach (var g in source ?? [])
                foreach (var s in g.Sequencers)
                    foreach (var t in s.Tags)
                    {
                        if (t.Kind != "Action" || string.IsNullOrEmpty(t.Name) || !float.IsFinite(t.Start)) continue;
                        foreach (var clip in s.Animations)
                        {
                            if (!tags.TryGetValue(clip, out var list)) tags[clip] = list = new();
                            var entry = (t.Name!, Math.Clamp(t.Start, 0f, 1f));
                            if (!list.Contains(entry)) list.Add(entry);
                        }
                    }
        }
        Collect(graphs);
        var marineClips = new HashSet<string>(tags.Keys, StringComparer.OrdinalIgnoreCase);
        Collect(orkGraphs);
        var orkOnly = new HashSet<string>(tags.Keys.Where(k => !marineClips.Contains(k)), StringComparer.OrdinalIgnoreCase);
        return new SoundTriggers
        {
            OrkOnlyClips = orkOnly,
            Files = files, ContainerSounds = new Dictionary<string, string>(containerSounds, StringComparer.OrdinalIgnoreCase),
            ClipTags = tags.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<(string, float)>)kv.Value.OrderBy(x => x.Item2).ToList(), StringComparer.OrdinalIgnoreCase),
            Notes = notes,
        };
    }

    static string Leaf(string path) => path.Split('\\', '/')[^1];
}

/// <summary>A positioned audio emitter of a map (<c>base/FXObject</c> with an <c>FXAudioEventName</c>): name, event, world position (game space).</summary>
public sealed record MapAudioEmitter(string Name, string Layer, string Event, Vector3 Position, bool AutoTrigger);

/// <summary>A box region (<c>base/AudioEventTriggerRegion</c> or <c>base/AudioEnvironmentRegion</c>): centre, rotation (degrees, X then Y then Z), size, its enter/exit events or reverb preset (see <see cref="Contains"/>).</summary>
public sealed record MapAudioRegion(string Name, string Kind, Vector3 Position, Vector3 RotationDegrees, Vector3 Scale, string EnterEvent, string ExitEvent,
    string Environment, float EnvironmentAmount)
{
    /// <summary>
    /// Whether a point lies in the region: a box of size <see cref="Scale"/> centred on <see cref="Position"/>, rotated as the
    /// layer says (**interpretation**: the shape value 1 read as a unit box scaled by Scale; the original's region shapes are not decoded).
    /// </summary>
    public bool Contains(Vector3 p)
    {
        var m = MapScene.Transform(Position, RotationDegrees, Vector3.One);
        if (!Matrix4x4.Invert(m, out var inv)) return false;
        var local = Vector3.Transform(p, inv);
        return MathF.Abs(local.X) <= Scale.X * 0.5f && MathF.Abs(local.Y) <= Scale.Y * 0.5f && MathF.Abs(local.Z) <= Scale.Z * 0.5f;
    }
}

/// <summary>The audio objects of a map's layers (M26b): emitters, trigger regions and reverb regions (Manufactorum's <c>a_audio</c>).</summary>
public sealed record MapAudio(IReadOnlyList<MapAudioEmitter> Emitters, IReadOnlyList<MapAudioRegion> Regions)
{
    public static MapAudio Load(string gameDir, string map, List<string>? notes = null)
    {
        if (map.Length == 0 || map.Length > 64 || map.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_')))
            throw new ArgumentException($"'{map}' is not a map name.", nameof(map));
        foreach (var region in new[] { "wrld", "r000_main" })
        {
            string path = Path.Combine(gameDir, "data", "chunks", $"{map}__{region}.oppc");
            if (System.IO.File.Exists(path)) return Read(path, notes);
        }
        throw new FileNotFoundException($"No region archive for '{map}'.");
    }

    public static MapAudio Read(string archivePath, List<string>? notes = null)
    {
        var emitters = new List<MapAudioEmitter>();
        var regions = new List<MapAudioRegion>();
        var layers = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        using (var archive = ObpkArchive.Open(archivePath))
            archive.ReadEntries(e => archive.GroupName(e) == "Layer", (e, d) => layers.TryAdd(e.FileName, d.ToArray()));
        foreach (var (name, bytes) in layers)
        {
            BodDocument doc;
            try { doc = BodDocument.Read(bytes); }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException) { notes?.Add($"layer {name}: {ex.Message}"); continue; }
            var one = Read(doc, name);
            emitters.AddRange(one.Emitters);
            regions.AddRange(one.Regions);
        }
        return new MapAudio(emitters, regions);
    }

    /// <summary>From one layer document (testable).</summary>
    public static MapAudio Read(BodDocument layer, string layerName)
    {
        var emitters = new List<MapAudioEmitter>();
        var regions = new List<MapAudioRegion>();
        Collect(layer.Root, layerName, Matrix4x4.Identity, emitters, regions, 0);
        return new MapAudio(emitters, regions);
    }

    static void Collect(BodValue value, string layer, Matrix4x4 parent, List<MapAudioEmitter> emitters, List<MapAudioRegion> regions, int depth)
    {
        if (depth > 64) return;
        switch (value)
        {
            case BodStruct s:
            {
                if (s["Disabled"] is BodBool { Value: true }) return;
                var position = Vec(s["Position"]) ?? Vector3.Zero;
                var rotation = Vec(s["Rotation"]) ?? Vector3.Zero;
                var scale = Vec(s["Scale"]) ?? Vector3.One;
                var world = MapScene.Transform(position, rotation, scale) * parent;
                string cls = s.Class.Text ?? "";
                if (cls.Equals("base/fxobject", StringComparison.OrdinalIgnoreCase) && Text(s, "FXAudioEventName") is { Length: > 0 } evt)
                    emitters.Add(new MapAudioEmitter(Text(s, "Name"), layer, evt, world.Translation, s["FXAutoTrigger"] is not BodBool { Value: false }));
                else if (cls.Equals("base/audioeventtriggerregion", StringComparison.OrdinalIgnoreCase) || cls.Equals("base/audioenvironmentregion", StringComparison.OrdinalIgnoreCase))
                    regions.Add(new MapAudioRegion(Text(s, "Name"), cls[(cls.IndexOf('/') + 1)..], world.Translation, rotation, scale, Text(s, "EnterRegionEvent"),
                        Text(s, "ExitRegionEvent"), Text(s, "EnvironmentEffectName"), s["EnvironmentEffectAmount"] is BodFloat { Value: var amount } && float.IsFinite(amount) ? amount : 0f));
                foreach (var f in s.Fields)
                    if (f.Name.Text is "Root" or "Objects" or "ChildObjects")
                        Collect(f.Value, layer, f.Name.Text == "ChildObjects" ? world : parent, emitters, regions, depth + 1);
                break;
            }
            case BodList l: foreach (var item in l.Items) Collect(item.Value, layer, parent, emitters, regions, depth + 1); break;
            case BodRef r: Collect(r.Value, layer, parent, emitters, regions, depth + 1); break;
        }
    }

    static string Text(BodStruct s, string field) => s[field] is BodString { Text: { } t } ? t : "";
    static Vector3? Vec(BodValue? v) => v is BodArray { Items: [BodFloat x, BodFloat y, BodFloat z, ..] } && float.IsFinite(x.Value + y.Value + z.Value)
        ? new Vector3(x.Value, y.Value, z.Value) : null;
}
