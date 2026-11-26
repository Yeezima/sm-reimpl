using SmFormats.Baf;
using SmFormats.Havok;
using SmFormats.Obpk;
using SmSim.Extras.Executions;

namespace SmContent.Extras.Executions;

// Executions sound and gore (owner brief 2026-11-27, card "Original events first"; docs/executions-cues.md): each catalogue row's cues as the original binds them. A sequencer's Action
// tags (the attacker's and the victim's, seconds on the shared execution clock) name the triggered actions of the entity's blueprint and its weapon; their post_sound_event, speech and
// play_fx commands are the cues. Read at run time from the player's install (read-only), never committed: the names and times in docs/executions-cues.md are the inventory's.

/// <summary>Which half of the pair a cue's tag belongs to: the attacker's sequencer or the victim's.</summary>
public enum ExecutionCueSide : byte { Attacker = 0, Victim = 1 }

/// <summary>
/// Where a cue plays (the brief: "voices positioned on the speaker, impacts at the wound"): on the attacker, on the victim, or at the wound (an impact, a grind or a cut: <see cref="ExecutionCueRules.AtWound"/>;
/// ours, the original posts every attacker cue on the attacker's entity).
/// </summary>
public enum ExecutionCuePlace : byte { Attacker = 0, Victim = 1, Wound = 2 }

/// <summary>What a cue does: post a sound event, post a stop (or end) event, a speech line, an effect, or a borrowed weapon's motor sound that the wielder's own sound replaces (<see cref="ExecutionMotorRole"/>).</summary>
public enum ExecutionCueKind : byte { Sound = 0, Stop = 1, Speech = 2, Effect = 3, Motor = 4 }

/// <summary>What a borrowed weapon's dropped motor sound was (<see cref="ExecutionCueKind.Motor"/>): a swing, a grind or cut under load, a rev or wind-up, a hit.</summary>
public enum ExecutionMotorRole : byte { None = 0, Swing = 1, Grind = 2, Rev = 3, Hit = 4 }

/// <summary>
/// Which weapon plays a row (the sheet keeps one cue list per pair): the row's own family (<see cref="Own"/>), the power sword on the chainsword's rows (owner 2026-11-25), or the combat knife on the
/// chainsword's rows (owner 2026-11-26: every non-Ork victim; and the knife's crossovers that borrow a chainsword move).
/// </summary>
public enum ExecutionWield : byte { Own = 0, PowerSword = 1, CombatKnife = 2 }

/// <summary>
/// One cue: the row and wield it belongs to, the side whose tag sets it off, what it does and where, when (<see cref="At"/>: seconds on the shared execution clock, the clip time
/// <c>ExecutionPresentMath.Pose</c> gives; the victim's tags are on the same clock, the victim's clip starting with the attacker's), the tag and the event (or effect) name, the file whose
/// triggered action holds it. <see cref="Until"/>: a windowed tag's end (a grind's), else <see cref="At"/>. <see cref="Ours"/>: not the original's binding (a borrowed weapon's motor, an
/// impact moved to the wound). <see cref="Blood"/>: a blood effect, drawn only where the match's gore allows (<see cref="ExecutionCueRules.BloodAllowed"/>).
/// </summary>
public sealed record ExecutionCue(string RowId, ExecutionWield Wield, ExecutionCueSide Side, ExecutionCueKind Kind, ExecutionCuePlace Place, float At, float Until, string Tag, string Event,
    string File, bool Ours = false, SpeechBinding? Speech = null, FxPlay? Fx = null, ExecutionMotorRole Motor = ExecutionMotorRole.None, bool Blood = false)
{
    /// <summary>A speech line whose character the data names (not <c>autodetect</c>): the attacker files' Ork voice on a grind (<c>ork_boy_2</c>) is the victim's (ours: placed on the victim).</summary>
    public bool NamedCharacter => Speech is { } s && s.Character.Length > 0 && !s.Character.Equals("autodetect", StringComparison.OrdinalIgnoreCase);
}

/// <summary>One file's cue commands (a blueprint or wargear): its triggered-action keys, sounds, speech and effects, and its speech character (<c>audio_ext/character</c>).</summary>
public sealed record ExecutionCueFile(string Name, IReadOnlySet<string> Keys, IReadOnlyList<SoundBinding> Sounds, IReadOnlyList<SpeechBinding> Speech, IReadOnlyList<FxBinding> Fx, string? Character)
{
    public static ExecutionCueFile From(string name, BafDocument doc) =>
        new(name, new HashSet<string>(SoundTriggers.TriggeredActionKeys(doc.Root), StringComparer.OrdinalIgnoreCase),
            SoundBinding.Collect(doc.Root).Where(b => b.UnderTriggeredActions && b.IsDefault).ToArray(),
            SpeechBinding.Collect(doc.Root).Where(b => b.UnderTriggeredActions && b.IsDefault).ToArray(),
            FxBinding.Collect(doc.Root).Where(b => b.IsDefault && b.Play is not null && b.Path.Contains("/triggered_actions/", StringComparison.Ordinal)).ToArray(),
            OrkSounds.SpeechCharacter(doc));

    /// <summary>Whether the file reacts to <paramref name="tag"/> with a sound, speech or effect command.</summary>
    public bool Reacts(string tag) => Sounds.Any(b => Is(b.Trigger, tag)) || Speech.Any(b => Is(b.Trigger, tag)) || Fx.Any(b => TagOf(b.Path) is { } t && Is(t, tag));

    internal static bool Is(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>The triggered action an effect's path sits in (the table under <c>triggered_actions</c>), or null.</summary>
    internal static string? TagOf(string path)
    {
        var parts = path.Split('/');
        int at = Array.IndexOf(parts, "triggered_actions");
        return at >= 0 && at + 1 < parts.Length ? parts[at + 1] : null;
    }
}

/// <summary>The cue lists by (row id, wield), each in time order; <see cref="For"/> falls back to the row's own list.</summary>
public sealed class ExecutionCueSheet
{
    readonly Dictionary<(string, ExecutionWield), IReadOnlyList<ExecutionCue>> _cues;

    /// <summary>Each read file's speech character (<c>audio_ext/character</c>): an enemy victim's <c>autodetect</c> voice (the boy's <c>ork_boy_2</c>).</summary>
    public IReadOnlyDictionary<string, string> Characters { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public ExecutionCueSheet(IEnumerable<ExecutionCue> cues, IEnumerable<string>? notes = null)
    {
        _cues = cues.GroupBy(c => (c.RowId.ToLowerInvariant(), c.Wield))
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ExecutionCue>)g.OrderBy(c => c.At).ThenBy(c => (byte)c.Side).ThenBy(c => (byte)c.Kind).ToArray());
        Notes = notes?.ToArray() ?? [];
    }

    public static readonly ExecutionCueSheet Empty = new([]);

    public IReadOnlyList<string> Notes { get; }

    /// <summary>The cues of <paramref name="rowId"/> played with <paramref name="wield"/> (the row's own list when that wield has none), or none.</summary>
    public IReadOnlyList<ExecutionCue> For(string rowId, ExecutionWield wield = ExecutionWield.Own) =>
        _cues.TryGetValue((rowId.ToLowerInvariant(), wield), out var c) ? c : _cues.TryGetValue((rowId.ToLowerInvariant(), ExecutionWield.Own), out var own) ? own : [];

    public IEnumerable<(string RowId, ExecutionWield Wield, IReadOnlyList<ExecutionCue> Cues)> All =>
        _cues.OrderBy(kv => kv.Key.Item1, StringComparer.Ordinal).ThenBy(kv => kv.Key.Item2).Select(kv => (kv.Value.Count > 0 ? kv.Value[0].RowId : kv.Key.Item1, kv.Key.Item2, kv.Value));

    public int Count => _cues.Values.Sum(c => c.Count);
}

/// <summary>The rules of the cues (ours where marked; spec/behavior.md "Added content: execution cues").</summary>
public static class ExecutionCueRules
{
    /// <summary>The cue rules' version (docs/executions-cues.md): presentation only, not in the config hash.</summary>
    public const int Version = 1;

    /// <summary>The campaign player blueprint, whose triggered actions the attacker's body tags (foley, footsteps, the grab, the slam) set off (inventory 1.3: the rig's entity).</summary>
    public const string AttackerBlueprint = "vanguard";

    /// <summary>The campaign weapon file of a row family or wield (inventory 1.1: the weapons that define the sync kills; the power sword's <c>powersword_new</c> reacts to none of their tags).</summary>
    public static string WeaponFile(string family) => family switch
    {
        ExecutionWeapons.Chainsword => "chainsword_new",
        ExecutionWeapons.PowerAxe => "poweraxe",
        ExecutionWeapons.ThunderHammer => "thunderhammer_new",
        ExecutionWeapons.CombatKnife => "combat_knife",
        _ => "chainsword_new",
    };

    /// <summary>The campaign weapon whose graph the row's sequencer is in (<see cref="ExecutionRow.Graph"/>).</summary>
    public static string GraphFile(ExecutionRow row) => row.Graph switch
    {
        "SM_MEL_ChainSword" => "chainsword_new",
        "SM_MEL_PowerAxe" => "poweraxe",
        "SM_MEL_ThunderHammer" => "thunderhammer_new",
        "SM_MEL_CombatKnife" => "combat_knife",
        _ => WeaponFile(row.Weapon),
    };

    /// <summary>The weapon file a row is played with under <paramref name="wield"/>.</summary>
    public static string WieldFile(ExecutionRow row, ExecutionWield wield) => wield switch
    {
        ExecutionWield.PowerSword => "powersword_new",
        ExecutionWield.CombatKnife => "combat_knife",
        _ => WeaponFile(row.Weapon),
    };

    /// <summary>The wields a row is played with: its own; the power sword and the knife on a chainsword row (the knife only on a non-Ork kind, owner 2026-11-26: its Ork rows are its own).</summary>
    public static IReadOnlyList<ExecutionWield> Wields(ExecutionRow row)
    {
        var list = new List<ExecutionWield> { ExecutionWield.Own };
        if (string.Equals(row.Weapon, ExecutionWeapons.Chainsword, StringComparison.OrdinalIgnoreCase))
        {
            list.Add(ExecutionWield.PowerSword);
            if (!ExecutionTargets.IsOrk(row.Target)) list.Add(ExecutionWield.CombatKnife);
        }
        return list;
    }

    /// <summary>The wield of a run: the executor's melee wargear (<paramref name="weaponKey"/>) on <paramref name="row"/>: the power sword or the knife on a chainsword row, else its own.</summary>
    public static ExecutionWield WieldOf(ExecutionRow row, string? weaponKey)
    {
        if (!string.Equals(row.Weapon, ExecutionWeapons.Chainsword, StringComparison.OrdinalIgnoreCase) || weaponKey is null) return ExecutionWield.Own;
        string compact = new(weaponKey.Where(ch => ch is not ('_' or '-' or ' ')).Select(char.ToLowerInvariant).ToArray());
        if (compact.Contains("powersword", StringComparison.Ordinal)) return ExecutionWield.PowerSword;
        return ExecutionWeapons.FamilyOf(weaponKey) == ExecutionWeapons.CombatKnife ? ExecutionWield.CombatKnife : ExecutionWield.Own;
    }

    /// <summary>The victim's campaign blueprints by kind, first that reacts (the rig's own entity: the boy's, the Gretchin's, the Chaos Marine's, the Bloodletter's, the renegade's), then its co-op twin.</summary>
    public static IReadOnlyList<string> VictimFiles(string kind) => kind.ToLowerInvariant() switch
    {
        ExecutionTargets.OrkBoy => ["boy", "ork_coop_boy"],
        ExecutionTargets.Gretchin => ["gretchin", "ork_coop_gretchin"],
        ExecutionTargets.ChaosMarine => ["chaos_marine_melee", "chaos_coop_marine_melee"],
        ExecutionTargets.Bloodletter => ["chaos_bloodletter", "chaos_coop_bloodletter"],
        ExecutionTargets.Renegade => ["chaos_renegade_ig", "chaos_coop_renegade_ig"],
        _ => [],
    };

    /// <summary>Every file the cues read (the blueprints in <c>ebps.oppc</c>, the weapons in <c>wargear.oppc</c>).</summary>
    public static IReadOnlyList<string> Files =>
        [AttackerBlueprint, "chainsword_new", "poweraxe", "thunderhammer_new", "combat_knife", "powersword_new", .. ExecutionRows.VictimRigs.Keys.SelectMany(VictimFiles)];

    /// <summary>The name parts that mark a weapon file's own motor sounds and blade trails (a chainsword's teeth, the axe's and hammer's power field), dropped when another weapon borrows the move.</summary>
    public static IReadOnlyList<string> MotorTokens(string weaponFile) => weaponFile switch
    {
        "chainsword_new" => ["chainsword", "chain_sword"],
        "poweraxe" => ["power_axe", "poweraxe"],
        "thunderhammer_new" => ["thunder_hammer", "thunderhammer"],
        "combat_knife" => ["combat_knife"],
        _ => [],
    };

    /// <summary>
    /// The role of a dropped motor sound by its event name (ours): a swing or whoosh, a grind or cut, a rev or wind-up, a blade drawn out (<c>remove</c>, <c>release</c>: dropped, as a rev: the
    /// hit has already sounded; Fable review 1), a hit (anything else).
    /// </summary>
    public static ExecutionMotorRole RoleOf(string evt)
    {
        string e = evt.ToLowerInvariant();
        if (e.Contains("swing") || e.Contains("whoosh")) return ExecutionMotorRole.Swing;
        if (e.Contains("grind") || e.Contains("cut")) return ExecutionMotorRole.Grind;
        if (e.Contains("rev") || e.Contains("antic") || e.Contains("remove") || e.Contains("release")) return ExecutionMotorRole.Rev;
        return ExecutionMotorRole.Hit;
    }

    /// <summary>Whether a sound plays at the wound (ours, the brief's "impacts at the wound"): hits, impacts, slams, stomps, smashes, kicks, grinds, cuts, gore, stabs, the arm break.</summary>
    public static bool AtWound(string evt)
    {
        string e = evt.ToLowerInvariant();
        foreach (var part in WoundParts) if (e.Contains(part, StringComparison.Ordinal)) return true;
        return false;
    }

    static readonly string[] WoundParts = ["hit", "impact", "slam", "stomp", "smash", "kick", "grind", "cut", "gore", "stab", "break", "release_flesh"];

    /// <summary>An effect name the data parks with an "xx" suffix (<c>chainsword_blood_swipexx</c> beside <c>chainsword_blood_swipe</c>): read as switched off by its authors (interpretation), not drawn.</summary>
    public static bool Disabled(string fx) => fx.EndsWith("xx", StringComparison.OrdinalIgnoreCase);

    /// <summary>A blood effect (by name): drawn only where the match's gore allows.</summary>
    public static bool IsBlood(string fx) =>
        !fx.StartsWith("bloodletter", StringComparison.OrdinalIgnoreCase) && (fx.Contains("blood", StringComparison.OrdinalIgnoreCase) || fx.Contains("gore", StringComparison.OrdinalIgnoreCase));   // the daemon's limb vanish and burn are not blood (Fable review 9)

    /// <summary>Tags that are no cue (rewards, kills, camera, alignment): their commands are not sound, speech or effect anyway; skipped by name for a clear sheet.</summary>
    public static bool Skipped(string tag)
    {
        string t = tag.ToLowerInvariant();
        return t.StartsWith("actn_give_", StringComparison.Ordinal) || t.Contains("suicide", StringComparison.Ordinal) || t is "actn_slam" or "actn_slam_light" or "actn_aoe_sync_stun";
    }

    /// <summary>
    /// Whether blood effects (the data's blood sprays and the kill's streak) draw (owner: "respect the gore settings"): in Warfront, and in Classic PvP with the Extended gore (<see cref="SmSim.ContentExtras.Gore"/>);
    /// never with <paramref name="off"/> (<c>--no-execution-gore</c>). The faithful game and Musou alone never run executions.
    /// </summary>
    public static bool BloodAllowed(SmSim.ContentExtras extras, bool off = false) =>
        !off && (extras.HasFlag(SmSim.ContentExtras.Warfront) || extras.HasFlag(SmSim.ContentExtras.Gore));
}

/// <summary>
/// Reads the cue sheet (read-only): the attacker rig's and the victim rigs' sequencer tags of each row (<see cref="ExecutionClipReader.Uses"/>, seconds on the row's clock) and the files of
/// <see cref="ExecutionCueRules.Files"/>, then <see cref="Build"/>.
/// </summary>
public static class ExecutionCueLoader
{
    public static ExecutionCueSheet Load(string gameDir, IReadOnlyList<ExecutionRow> rows, List<string>? notes = null)
    {
        notes ??= [];
        var files = new Dictionary<string, ExecutionCueFile>(StringComparer.OrdinalIgnoreCase);
        var wanted = ExecutionCueRules.Files.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var package in new[] { GameplayTuning.BlueprintPackage, GameplayTuning.WargearPackage })
        {
            string path = Path.Combine(gameDir, package);
            if (!File.Exists(path)) { notes.Add($"execution cues: {package} not in the install"); continue; }
            using var archive = ObpkArchive.Open(path);
            archive.ReadEntries(e => archive.GroupName(e) == "attr_pc" && wanted.Contains(e.FileName), (e, d) =>
            {
                try { files.TryAdd(e.FileName, ExecutionCueFile.From(e.FileName, BafDocument.Read(d))); }
                catch (InvalidDataException ex) { notes.Add($"execution cues: {e.FileName} unreadable ({ex.Message})"); }
            });
        }
        foreach (var f in wanted.Where(f => !files.ContainsKey(f)).Order(StringComparer.Ordinal)) notes.Add($"execution cues: {f} not found");

        var rigs = new Dictionary<string, (HavokCharacterFile, IReadOnlyList<BehaviorGraph>)?>(StringComparer.OrdinalIgnoreCase);
        (HavokCharacterFile C, IReadOnlyList<BehaviorGraph> G)? Rig(RigProfile? profile)
        {
            if (profile is null) return null;
            string key = profile.Archive + ":" + profile.Entry;
            if (rigs.TryGetValue(key, out var done)) return done;
            (HavokCharacterFile, IReadOnlyList<BehaviorGraph>)? loaded = null;
            try { var c = profile.LoadCharacter(gameDir); loaded = (c, HavokBehavior.Read(c.Pack)); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or KeyNotFoundException or UnauthorizedAccessException)
            { notes.Add($"execution cues: rig {profile.Entry} unreadable ({ex.Message})"); }
            return rigs[key] = loaded;
        }
        IReadOnlyList<ExecutionClipEvent>? TagsOf((HavokCharacterFile C, IReadOnlyList<BehaviorGraph> G)? rig, string clip, string? sequencer, string? graph)
        {
            if (rig is not { } r || sequencer is null) return null;
            try
            {
                var uses = ExecutionClipReader.Uses(clip, r.G, name => r.C.LoadAnimation(name).Animation.Duration);
                var use = uses.FirstOrDefault(u => ExecutionCueFile.Is(u.Sequencer, sequencer) && (graph is null || ExecutionCueFile.Is(u.Graph, graph)))
                          ?? uses.FirstOrDefault(u => ExecutionCueFile.Is(u.Sequencer, sequencer));
                return use?.Events;
            }
            catch (Exception ex) when (ex is KeyNotFoundException or InvalidDataException or NotSupportedException) { notes.Add($"execution cues: {clip}: {ex.Message}"); return null; }
        }
        var attacker = Rig(ExecutionCatalogueLoader.AttackerProfile);
        return Build(rows, r => TagsOf(attacker, r.AttackerClip, r.Sequencer, r.Graph), r => TagsOf(Rig(ExecutionCatalogueLoader.VictimProfile(r.Target)), r.VictimClip, r.VictimSequencer, null), files, notes);
    }

    /// <summary>
    /// The sheet from tags and files (testable without an install). For each row and each of its wields (<see cref="ExecutionCueRules.Wields"/>), each Action tag of the attacker's sequencer
    /// sets off the attacker blueprint's commands of that name and the weapon's: the wielded weapon's file when it reacts to the tag (the knife's own cut sounds), else the graph's file, from
    /// which a borrowing weapon drops the graph weapon's motor sounds (<see cref="ExecutionCueKind.Motor"/>: the wielder's own sound replaces them at run time, ours) and its blade trails
    /// (its blood effects stay). Each Action tag of the victim's sequencer sets off the first victim file of its kind that reacts. A windowed tag's stop event is posted at the window's end;
    /// an instant tag's at the move's end (the client's <see cref="ExecutionCueTimeline"/> stops the emitters there; interpretation: the action ends with the state).
    /// </summary>
    public static ExecutionCueSheet Build(IReadOnlyList<ExecutionRow> rows, Func<ExecutionRow, IReadOnlyList<ExecutionClipEvent>?> attackerTags,
        Func<ExecutionRow, IReadOnlyList<ExecutionClipEvent>?> victimTags, IReadOnlyDictionary<string, ExecutionCueFile> files, List<string>? notes = null)
    {
        notes ??= [];
        var cues = new List<ExecutionCue>();
        foreach (var row in rows)
        {
            var att = attackerTags(row);
            var vic = victimTags(row);
            if (att is null) notes.Add($"execution cues: row '{row.Id}' attacker sequencer not found; no attacker cues");
            if (vic is null) notes.Add($"execution cues: row '{row.Id}' victim sequencer not found; no victim cues");
            string? victimFile = ExecutionCueRules.VictimFiles(row.Target).FirstOrDefault(files.ContainsKey);
            foreach (var wield in ExecutionCueRules.Wields(row))
            {
                string wieldFile = ExecutionCueRules.WieldFile(row, wield), graphFile = ExecutionCueRules.GraphFile(row);
                bool borrowed = !string.Equals(wieldFile, graphFile, StringComparison.OrdinalIgnoreCase);
                foreach (var t in Actions(att))
                {
                    if (files.GetValueOrDefault(ExecutionCueRules.AttackerBlueprint) is { } body) Add(cues, row, wield, ExecutionCueSide.Attacker, t, body, borrowedFrom: null);
                    var mine = files.GetValueOrDefault(wieldFile);
                    // The wielded file's own cues; on a borrowed move the graph weapon's motor and trails are dropped from it too (the knife file's leftover chainsword cut bed; Fable review 4).
                    if (mine is not null && mine.Reacts(t.Name)) Add(cues, row, wield, ExecutionCueSide.Attacker, t, mine, borrowed ? graphFile : null);
                    else if (files.GetValueOrDefault(graphFile) is { } graph) Add(cues, row, wield, ExecutionCueSide.Attacker, t, graph, borrowed ? graphFile : null);
                }
                foreach (var t in Actions(vic))
                {
                    var file = ExecutionCueRules.VictimFiles(row.Target).Select(f => files.GetValueOrDefault(f)).FirstOrDefault(f => f is not null && f.Reacts(t.Name))
                               ?? (victimFile is null ? null : files[victimFile]);
                    if (file is not null) Add(cues, row, wield, ExecutionCueSide.Victim, t, file, borrowedFrom: null);
                }
            }
        }
        return new ExecutionCueSheet(DropEchoes(Dedupe(cues).ToList()), notes)
        {
            Characters = files.Values.Where(f => f.Character is not null).ToDictionary(f => f.Name, f => f.Character!, StringComparer.OrdinalIgnoreCase),
        };
    }

    static IEnumerable<ExecutionClipEvent> Actions(IReadOnlyList<ExecutionClipEvent>? tags) =>
        (tags ?? []).Where(t => t.Tag == "Action" && !string.IsNullOrEmpty(t.Name) && !ExecutionCueRules.Skipped(t.Name) && float.IsFinite(t.Start));

    static void Add(List<ExecutionCue> into, ExecutionRow row, ExecutionWield wield, ExecutionCueSide side, ExecutionClipEvent tag, ExecutionCueFile file, string? borrowedFrom)
    {
        float at = MathF.Max(0f, tag.Start), until = MathF.Max(at, tag.End);
        bool windowed = until - at > 1f / 60f;
        var tokens = borrowedFrom is null ? [] : ExecutionCueRules.MotorTokens(borrowedFrom);
        bool Motor(string name) => tokens.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase));
        var own = side == ExecutionCueSide.Attacker ? ExecutionCuePlace.Attacker : ExecutionCuePlace.Victim;
        foreach (var b in file.Sounds.Where(b => ExecutionCueFile.Is(b.Trigger, tag.Name)))
        {
            float delay = MathF.Max(0f, b.DelaySeconds);
            if (b.StartEvent.Length > 0)
            {
                if (Motor(b.StartEvent) && b.StartEvent.StartsWith("stop_", StringComparison.OrdinalIgnoreCase)) { }   // a borrowed motor's stop: the wielder's replacement has its own length
                else if (Motor(b.StartEvent))
                {
                    var role = ExecutionCueRules.RoleOf(b.StartEvent);   // a swing plays on the attacker (the blade in his hand), a hit or grind at the wound (the sheet as the playback; Fable review 9)
                    into.Add(new ExecutionCue(row.Id, wield, side, ExecutionCueKind.Motor, role == ExecutionMotorRole.Swing ? ExecutionCuePlace.Attacker : ExecutionCuePlace.Wound, at + delay,
                        windowed ? until : at + delay, tag.Name, b.StartEvent, file.Name, Ours: true, Motor: role));
                }
                else
                {
                    bool stop = b.StartEvent.StartsWith("stop_", StringComparison.OrdinalIgnoreCase);
                    into.Add(new ExecutionCue(row.Id, wield, side, stop ? ExecutionCueKind.Stop : ExecutionCueKind.Sound, ExecutionCueRules.AtWound(b.StartEvent) ? ExecutionCuePlace.Wound : own,
                        at + delay, windowed ? until : at + delay, tag.Name, b.StartEvent, file.Name, Ours: side == ExecutionCueSide.Attacker && ExecutionCueRules.AtWound(b.StartEvent)));
                }
            }
            // A windowed tag's stop at its end (an instant tag's stop waits for the move's end: the timeline stops the emitters).
            if (b.StopEvent.Length > 0 && windowed && !Motor(b.StopEvent) && !Motor(b.StartEvent))
                into.Add(new ExecutionCue(row.Id, wield, side, b.StopEvent.StartsWith("stop_", StringComparison.OrdinalIgnoreCase) ? ExecutionCueKind.Stop : ExecutionCueKind.Sound,
                    ExecutionCueRules.AtWound(b.StartEvent) ? ExecutionCuePlace.Wound : own, until, until, tag.Name, b.StopEvent, file.Name));
        }
        foreach (var s in file.Speech.Where(s => ExecutionCueFile.Is(s.Trigger, tag.Name)))
        {
            var cue = new ExecutionCue(row.Id, wield, side, ExecutionCueKind.Speech, own, at, at, tag.Name, $"{s.Type}{s.State}", file.Name, Speech: s);
            // An attacker file's line in a named character (the grind's Ork voice) is the victim's (ours).
            if (side == ExecutionCueSide.Attacker && cue.NamedCharacter) cue = cue with { Place = ExecutionCuePlace.Victim, Ours = true };
            // An attacker file's damage line with no character (the grind's end "damage_small") is the victim's reaction (interpretation: the data leaves the speaker to the target).
            else if (side == ExecutionCueSide.Attacker && s.Character.Length == 0 && s.Type.StartsWith("damage", StringComparison.OrdinalIgnoreCase)) cue = cue with { Place = ExecutionCuePlace.Victim, Ours = true };
            into.Add(cue);
        }
        foreach (var f in file.Fx.Where(f => ExecutionCueFile.TagOf(f.Path) is { } k && ExecutionCueFile.Is(k, tag.Name)))
        {
            var p = f.Play!;
            if (p.Name.Length == 0 || ExecutionCueRules.Disabled(p.Name)) continue;
            bool blood = ExecutionCueRules.IsBlood(p.Name);
            if (Motor(p.Name) && !blood) continue;   // a borrowed weapon's blade trail (the chainsword's teeth) is not drawn on another blade
            into.Add(new ExecutionCue(row.Id, wield, side, ExecutionCueKind.Effect, own, at, until, tag.Name, p.Name, file.Name, Fx: p, Blood: blood));
        }
    }

    /// <summary>How close the victim's own line of a type must be for a line of the same type moved from an attacker file to be dropped (seconds, ours; Fable review 7).</summary>
    public const float EchoSeconds = 0.1f;

    /// <summary>A victim line moved from an attacker file is dropped when the victim's own line of the same type plays within <see cref="EchoSeconds"/> (the knife's 0.700 s damage_big beside the boy's 0.733 s).</summary>
    static IEnumerable<ExecutionCue> DropEchoes(List<ExecutionCue> cues) =>
        cues.Where(c => !(c is { Kind: ExecutionCueKind.Speech, Side: ExecutionCueSide.Attacker, Place: ExecutionCuePlace.Victim } && cues.Any(v =>
            v.Kind == ExecutionCueKind.Speech && v.Side == ExecutionCueSide.Victim && v.RowId == c.RowId && v.Wield == c.Wield
            && string.Equals(v.Speech?.Type, c.Speech?.Type, StringComparison.OrdinalIgnoreCase) && MathF.Abs(v.At - c.At) <= EchoSeconds)));

    /// <summary>The same event at the same time and place from two files (the blueprint's and the weapon's <c>play_sm_sk_fist_hit</c>) plays once.</summary>
    static IEnumerable<ExecutionCue> Dedupe(List<ExecutionCue> cues) =>
        cues.GroupBy(c => (c.RowId, c.Wield, c.Side, c.Kind, c.Place, MathF.Round(c.At * 1000f), c.Event.ToLowerInvariant(), c.Speech?.Character)).Select(g => g.First());
}

/// <summary>
/// The client's cue clock (engine-free; tested in <c>ExecutionCueTests</c>): each run (executor and start tick) fires each of its cues once, in time order, as the shared execution clock passes it,
/// for the attacker, the victim and every observer alike (each client runs its own copy on the same clock). A run first seen late (an observer joining, a lagging view) skips the cues more than
/// <see cref="LateSkipSeconds"/> behind the clock; a clock that steps back (a prediction correction) never fires a cue again. <see cref="Interrupt"/> cancels the pending cues and asks the caller
/// to stop the run's sustained sounds at once; <see cref="Finish"/> lets the cues after the move's end play out (the victim's half runs longer), then asks the caller to end the run's loops.
/// </summary>
public sealed class ExecutionCueTimeline
{
    public readonly record struct RunKey(int Executor, uint StartTick);

    public enum StepKind : byte { Fire = 0, Interrupt = 1, Finish = 2 }

    public readonly record struct Step(StepKind Kind, RunKey Run, ExecutionCue? Cue);

    /// <summary>A cue more than this far behind the clock when its run is first seen does not play (seconds, ours).</summary>
    public const float LateSkipSeconds = 0.25f;
    /// <summary>A finishing run ends this long after its last cue, or at once when none is left (seconds, ours).</summary>
    public const float FinishGraceSeconds = 0.5f;

    sealed class Track
    {
        public IReadOnlyList<ExecutionCue> Cues = [];
        public int Next;
        public bool Seen, Finishing;
    }

    readonly Dictionary<RunKey, Track> _tracks = new();
    readonly HashSet<RunKey> _closed = new();
    readonly Queue<RunKey> _closedOrder = new();
    /// <summary>How many closed runs are remembered against a restart (ours; a run's key is never reused: its start tick only grows).</summary>
    public const int ClosedMemory = 256;

    void Close(RunKey key)
    {
        if (!_closed.Add(key)) return;
        _closedOrder.Enqueue(key);
        while (_closedOrder.Count > ClosedMemory) _closed.Remove(_closedOrder.Dequeue());
    }

    public int Fired { get; private set; }
    public int Skipped { get; private set; }
    public int Interrupted { get; private set; }
    public int Finished { get; private set; }
    public int Count => _tracks.Count;
    public IEnumerable<RunKey> Runs => _tracks.Keys;
    public bool Has(RunKey key) => _tracks.ContainsKey(key);
    /// <summary>Whether <paramref name="key"/> finished or was interrupted: a revived run (a live-list repeat after an Invalid end for silence) stays closed and plays nothing again (Fable review 5).</summary>
    public bool IsClosed(RunKey key) => _closed.Contains(key);

    /// <summary>Advances <paramref name="key"/> to clock <paramref name="clock"/> (seconds of the move) with <paramref name="cues"/> (taken at the first call), adding the due cues to <paramref name="steps"/>.</summary>
    public void Advance(RunKey key, IReadOnlyList<ExecutionCue> cues, float clock, List<Step> steps)
    {
        if (_closed.Contains(key)) return;   // a run that finished or was interrupted never starts again (a stale frame, a late repeat)
        if (!_tracks.TryGetValue(key, out var t)) _tracks[key] = t = new Track { Cues = cues };
        if (!float.IsFinite(clock)) return;
        if (!t.Seen)
        {
            t.Seen = true;
            while (t.Next < t.Cues.Count && t.Cues[t.Next].At < clock - LateSkipSeconds) { t.Next++; Skipped++; }
        }
        while (t.Next < t.Cues.Count && t.Cues[t.Next].At <= clock)
        {
            steps.Add(new Step(StepKind.Fire, key, t.Cues[t.Next]));
            t.Next++;
            Fired++;
        }
        if (t.Finishing && (t.Next >= t.Cues.Count && (t.Cues.Count == 0 || clock >= t.Cues[^1].At + FinishGraceSeconds) || t.Next >= t.Cues.Count && clock >= LastAt(t) + FinishGraceSeconds))
        {
            _tracks.Remove(key); Close(key);
            Finished++;
            steps.Add(new Step(StepKind.Finish, key, null));
        }
    }

    static float LastAt(Track t) => t.Cues.Count == 0 ? 0f : t.Cues[^1].At;

    /// <summary>The run completed (its End said Done): its remaining cues still fire on the clock; <see cref="Advance"/> then reports <see cref="StepKind.Finish"/>.</summary>
    public void Finish(RunKey key)
    {
        if (_tracks.TryGetValue(key, out var t)) t.Finishing = true;
    }

    /// <summary>The run ended otherwise (the executor killed, an invalid end, a disconnect, the match's end, the view gone): its pending cues are dropped and the caller stops its sounds.</summary>
    public void Interrupt(RunKey key, List<Step> steps)
    {
        if (!_tracks.Remove(key, out var t)) return;
        Close(key);
        Skipped += t.Cues.Count - t.Next;
        Interrupted++;
        steps.Add(new Step(StepKind.Interrupt, key, null));
    }

    /// <summary>Interrupts every run (the match's exit).</summary>
    public void InterruptAll(List<Step> steps)
    {
        foreach (var key in _tracks.Keys.ToArray()) Interrupt(key, steps);
    }
}
