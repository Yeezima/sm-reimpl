using SmFormats.Baf;
using SmFormats.Havok;
using SmFormats.Obpk;
using SmSim;

namespace SmContent;

/// <summary>
/// Hit reactions from the install (M28; values in spec/behavior.md "Reactions", evidence in docs/discovery.md):
/// <list type="bullet">
/// <item>the player blueprints' <c>animated_character_ext/character_reaction_mapping</c>,
/// <c>character_reaction_mapper\multiplayer_reaction_mapping</c> (package <see cref="MapperPackage"/>): it renames the melee
/// weapons' reaction types (<c>cs_default_1</c>, <c>ax_default_2</c>, …) to <c>reaction_mp_melee</c>;</item>
/// <item><c>trigger_reaction/reaction_by_intensity</c> entries in wargear abilities and blasts: kind and knockback;</item>
/// <item>the marine rig's <see cref="Graph"/> behavior graph: the reaction clips and their branch times.</item>
/// </list>
/// Which clip set a reaction type plays is an **interpretation**: the graph's ports are named <c>PORT_Reaction_MP_Stun</c> and
/// <c>PORT_Reaction_MP_Stagger</c> (its "Ranged Reactions" state chooses the multiplayer grenade set or the stagger set), and
/// <c>reaction_mp_melee</c> is read as the light-attack interrupt (the graph's CSM light-attack reactions); the state machine's
/// transitions are not decoded.
/// </summary>
public static class ReactionTuning
{
    public const string MapperPackage = "data/packages/character_reaction_mapper.oppc";
    public const string MultiplayerMapping = "multiplayer_reaction_mapping";
    public const string Graph = "MP_SM_Reactions";

    /// <summary>The clips of each kind in the order front, right, back, left (the two-way kinds repeat front and back).</summary>
    public static readonly (ReactionKind Kind, bool FourWay, string[] Clips)[] Sets =
    [
        (ReactionKind.Interrupt, false, ["SM_REC_Light_Itrpt_FHL_01", "SM_REC_Light_Itrpt_FHL_01", "SM_REC_Light_Itrpt_BHR_01", "SM_REC_Light_Itrpt_BHR_01"]),
        (ReactionKind.Stun, true, ["SM_MP_REC_Grenade_F_01", "SM_MP_REC_Grenade_R_01", "SM_MP_REC_Grenade_B_01", "SM_MP_REC_Grenade_L_01"]),
        (ReactionKind.Stagger, false, ["SM_REC_Stagger_F_01", "SM_REC_Stagger_F_01", "SM_REC_Stagger_B_01", "SM_REC_Stagger_B_01"]),
    ];

    /// <summary>
    /// A rig's reaction graph and clip sets (Ork O2); <paramref name="PreferredSequencer"/>: when several sequencers play a clip alone,
    /// the one whose name contains it ("CSM" on the marine), else the first.
    /// </summary>
    public sealed record RigMap(string Graph, IReadOnlyList<(ReactionKind Kind, bool FourWay, string[] Clips)> Sets, string PreferredSequencer = "CSM");

    /// <summary>The marine rig's reactions (<see cref="Graph"/>, <see cref="Sets"/>).</summary>
    public static readonly RigMap MarineRig = new(Graph, Sets);

    /// <summary>
    /// The Ork Boy's reactions (Ork O2) from <c>BOY_REACTIONS_Coop</c>, which has light and heavy hits front and back (high / low, left /
    /// right) but no side hits and no multiplayer stun or stagger set. **Ours**: the light front-high-left / back-high-right hits are the
    /// interrupt (as the marine's FHL / BHR), the heavy high-left hits the stun and the heavy low-right hits the stagger, each two-way. The
    /// simulation keeps the marine's timings (design section 5: the client stretches these clips to them).
    /// </summary>
    public static readonly RigMap OrkRig = new("BOY_REACTIONS_Coop",
    [
        (ReactionKind.Interrupt, false, ["BOY_1HM_REC_Lt_Front_Hi_L_01", "BOY_1HM_REC_Lt_Front_Hi_L_01", "BOY_1HM_REC_Lt_Back_Hi_R_01", "BOY_1HM_REC_Lt_Back_Hi_R_01"]),
        (ReactionKind.Stun, false, ["BOY_1HM_REC_Hvy_Front_Hi_L_01", "BOY_1HM_REC_Hvy_Front_Hi_L_01", "BOY_1HM_REC_Hvy_Back_Hi_L_01", "BOY_1HM_REC_Hvy_Back_Hi_L_01"]),
        (ReactionKind.Stagger, false, ["BOY_1HM_REC_Hvy_Front_Low_R_01", "BOY_1HM_REC_Hvy_Front_Low_R_01", "BOY_1HM_REC_Hvy_Back_Low_R_01", "BOY_1HM_REC_Hvy_Back_Low_R_01"]),
    ]);

    /// <summary>Reads <see cref="MultiplayerMapping"/> from the install (empty with a note when missing).</summary>
    public static IReadOnlyDictionary<string, string> LoadMapping(string gameDir, List<string> notes)
    {
        BafDocument? doc = null;
        string path = Path.Combine(gameDir, MapperPackage);
        if (!File.Exists(path)) { notes.Add($"reactions: {MapperPackage} not found; reaction types unmapped"); return new Dictionary<string, string>(); }
        using (var archive = ObpkArchive.Open(path))
            archive.ReadEntries(e => archive.GroupName(e) == "attr_pc" && e.FileName == MultiplayerMapping, (_, d) => doc = BafDocument.Read(d));
        if (doc is null) { notes.Add($"reactions: {MultiplayerMapping} not found; reaction types unmapped"); return new Dictionary<string, string>(); }
        return ReadMapping(doc);
    }

    /// <summary>
    /// The mapping's <c>reaction_mapping/character_reaction_mapping</c> entries without a hit zone: source reaction → destination
    /// (entries keyed to a hit zone, e.g. legs → <c>rng_leg_hit</c>, are flinches and left out). The first entry per source wins.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ReadMapping(BafDocument mapper)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in mapper.Root.Table("reaction_mapping")?.Entries.Where(e => e.Type == BafType.Table) ?? [])
        {
            var t = e.AsTable();
            if (t.String("source_reaction") is not { Length: > 0 } source || t.String("destination_reaction") is not { Length: > 0 } destination) continue;
            if (t.String("source_hit_zone") is { Length: > 0 }) continue;
            map.TryAdd(source, destination);
        }
        return map;
    }

    /// <summary>A reaction type after the mapping (unchanged when not mapped).</summary>
    public static string Resolve(IReadOnlyDictionary<string, string>? mapping, string name) =>
        mapping is not null && mapping.TryGetValue(name, out var to) ? to : name;

    /// <summary>
    /// A <c>trigger_reaction</c>'s entries that name a multiplayer reaction (after the mapping), in file order, with their
    /// <c>reaction_distance</c>. Entries with another type (flinches, campaign reactions) are left out.
    /// </summary>
    public static IReadOnlyList<ReactionEntry> Entries(BafTable? triggerReaction, IReadOnlyDictionary<string, string>? mapping = null)
    {
        var list = new List<ReactionEntry>();
        foreach (var e in triggerReaction?.Table("reaction_by_intensity")?.Entries.Where(e => e.Type == BafType.Table) ?? [])
        {
            var t = e.AsTable();
            var kind = Reaction.KindOf(Resolve(mapping, t.String("reaction_type") ?? ""));
            if (kind == ReactionKind.None) continue;
            var d = t.Table("reaction_distance");
            static float F(BafTable? table, string field) => table?.Float(field) is float v && float.IsFinite(v) ? v : 0f;
            list.Add(new ReactionEntry(Math.Max(0f, F(t, "intensity")), kind, F(d, "distance_backward"), F(d, "distance_right"), F(d, "distance_up"), Math.Max(0f, F(d, "duration"))));
        }
        return list;
    }

    /// <summary>
    /// The reaction of a melee hit (an ability's <c>primary_target_actions/hit_sim/actions</c> or <c>secondary_…</c>): the first
    /// multiplayer entry of its <c>trigger_reaction</c>, directly in the actions or in the <c>actions_by_upgrade</c> entry without
    /// a required upgrade (the default; reactions in <c>delayed_action</c>s are campaign names and never map). None without one.
    /// </summary>
    public static ReactionEntry HitReaction(BafTable? actions, IReadOnlyDictionary<string, string>? mapping)
    {
        if (actions is not { } a) return default;
        if (Entries(a.Table("trigger_reaction"), mapping) is { Count: > 0 } direct) return direct[0];
        // M43: the first upgrade block's entry chosen for the granted upgrades (the default entry outside a perk read; Sure Strike's under one).
        if (UpgradeScope.Pick(a.Table("actions_by_upgrade/actions"), "entry") is { } entry && Entries(entry.Table("action/commands/trigger_reaction"), mapping) is { Count: > 0 } chosen)
            return chosen[0];
        // A perk entry without a reaction keeps the default entry's (as UpgradeScope.Command).
        if (WeaponTuning.Entry(a.Table("actions_by_upgrade/actions"), "entry", []) is { } basic && Entries(basic.Table("action/commands/trigger_reaction"), mapping) is { Count: > 0 } fallback)
            return fallback[0];
        return default;
    }

    /// <summary>A blast's reactions (<c>hit_actions/commands/trigger_reaction</c>) and blindness (<c>do_blindness/blindness_duration</c>, s).</summary>
    public static (IReadOnlyList<ReactionEntry> Reactions, float Blindness) BlastEffects(BafTable aoe, IReadOnlyDictionary<string, string>? mapping = null)
    {
        var commands = aoe.Table("hit_actions/commands");
        float blind = commands?.Table("do_blindness")?.Float("blindness_duration") is float b && float.IsFinite(b) && b > 0f ? b : 0f;
        return (Entries(commands?.Table("trigger_reaction"), mapping), blind);
    }

    /// <summary>
    /// The reaction kinds' clips from <see cref="Graph"/>: per clip of <see cref="Sets"/>, the sequencer playing it alone (the
    /// "CSM" one when several do), its length / playback speed, and its branch tags' starts × that length:
    /// <c>BRANCH_Reaction_End</c> (else the length), <c>BRANCH_Run</c>, <c>BRANCH_Evade</c>, <c>BRANCH_Melee_Combo</c>,
    /// <c>BRANCH_HitReact</c> (each the earliest; missing or later than the end: the end). Null with a note when a clip is missing.
    /// </summary>
    public static IReadOnlyList<ReactionSpec>? Specs(IReadOnlyList<BehaviorGraph> graphs, Func<string, float> animationLength, List<string> notes) =>
        Specs(MarineRig, graphs, animationLength, notes);

    /// <summary>A rig's reaction clips (Ork O2), as <see cref="Specs(IReadOnlyList{BehaviorGraph}, Func{string, float}, List{string})"/>.</summary>
    public static IReadOnlyList<ReactionSpec>? Specs(RigMap map, IReadOnlyList<BehaviorGraph> graphs, Func<string, float> animationLength, List<string> notes)
    {
        var graph = graphs.FirstOrDefault(g => g.Name.Equals(map.Graph, StringComparison.OrdinalIgnoreCase));
        if (graph is null) { notes.Add($"reactions: graph {map.Graph} not found; placeholders"); return null; }
        var specs = new List<ReactionSpec>();
        foreach (var (kind, fourWay, names) in map.Sets)
        {
            var clips = new ReactionClip[4];
            for (int i = 0; i < 4; i++)
            {
                if (Clip(graph, map, names[i], animationLength, notes) is not { } clip) return null;
                clips[i] = clip;
            }
            specs.Add(new ReactionSpec(kind, fourWay, clips[0], clips[1], clips[2], clips[3]));
        }
        return specs;
    }

    static ReactionClip? Clip(BehaviorGraph graph, RigMap map, string animation, Func<string, float> animationLength, List<string> notes)
    {
        var candidates = graph.Sequencers.Where(s => s.Animations.Count == 1 && s.Animations[0].Equals(animation, StringComparison.OrdinalIgnoreCase)).ToList();
        var seq = candidates.FirstOrDefault(s => s.Name.Contains(map.PreferredSequencer, StringComparison.Ordinal)) ?? candidates.FirstOrDefault();
        if (seq is null) { notes.Add($"reactions: no sequencer plays {animation} in {map.Graph}; placeholders"); return null; }
        float speed = seq.PlaybackSpeeds.Count > 0 && float.IsFinite(seq.PlaybackSpeeds[0]) && seq.PlaybackSpeeds[0] > 0f ? seq.PlaybackSpeeds[0] : 1f;
        float duration;
        try { duration = animationLength(animation) / speed; }
        catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException)) { notes.Add($"reactions: {animation}: {e.Message}; placeholders"); return null; }
        if (!float.IsFinite(duration) || duration <= 0f) { notes.Add($"reactions: {animation} has no usable length; placeholders"); return null; }
        float? Start(string branch) => seq.Tags.Where(t => t.Kind == "Branch" && t.Name == branch).Select(t => (float?)t.Start).Min();
        float end = Math.Clamp(Start("BRANCH_Reaction_End") ?? 1f, 0f, 1f) * duration;
        float At(string branch) => Start(branch) is float f && float.IsFinite(f) ? Math.Clamp(f * duration, 0f, end) : end;
        return new ReactionClip(animation, duration, end, At("BRANCH_Run"), At("BRANCH_Evade"), At("BRANCH_Melee_Combo"), At("BRANCH_HitReact"));
    }

    /// <summary>
    /// The first window of a sequencer without <c>BRANCH_HitReact</c> (fractions of the clip × <paramref name="duration"/>): the
    /// first gap in the union of its HitReact windows over [0, 1]. (0, 0) when it has no HitReact tag (no window: always
    /// interruptible, interpretation) or no gap.
    /// </summary>
    public static (float From, float To) NoReactWindow(BehaviorSequencer seq, float duration)
    {
        var windows = seq.Tags.Where(t => t.Kind == "Branch" && t.Name == "BRANCH_HitReact" && float.IsFinite(t.Start) && float.IsFinite(t.End))
            .Select(t => (Start: Math.Clamp(t.Start, 0f, 1f), End: Math.Clamp(t.End, 0f, 1f))).OrderBy(w => w.Start).ToList();
        if (windows.Count == 0) return (0f, 0f);
        float covered = 0f;
        foreach (var (start, end) in windows)
        {
            if (start > covered + 1e-4f) return (covered * duration, start * duration);
            covered = MathF.Max(covered, end);
        }
        return covered < 1f - 1e-4f ? (covered * duration, duration) : (0f, 0f);
    }
}
