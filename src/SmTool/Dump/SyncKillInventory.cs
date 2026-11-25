using System.Text.RegularExpressions;
using SmFormats.Havok;

namespace SmTool.Dump;

/// <summary>
/// executions-inventory (2026-11-25): the sync kills (the original's executions) a character packfile's behaviour graphs carry, for
/// <c>smtool sync-kills</c> and docs/executions-coverage.md. Read-only; names and numbers only.
/// <list type="bullet">
/// <item>An attacker's move is a sequencer under a state whose path names "sync" or "struggle", or one that plays a clip named
/// <c>*_SYN_*</c> / <c>*_SYNC_*</c>.</item>
/// <item><c>hkbInteractionSequenceTag</c> (layout read on the install, interpretation): window start @32 and end @36 (fractions of the
/// clip), payload @56, an <c>hkbInteractionRequestPayload</c> whose int @12 is an event index of the attacker's graph. The events
/// named so are <c>PORT_*</c> names the victim's graphs take as transition triggers (the name, not the index, links the two rigs).</item>
/// <item><c>hkbAttackAlignmentSequenceTag</c>: window @32/@36; @28, @80, @84, @88 (degrees on the reaction clips), @92 as in
/// docs/discovery.md "Reaction alignment tags" (meanings beyond the window not decoded).</item>
/// <item>A victim's reaction is the state a transition on one of those <c>PORT_*</c> events enters (state or wildcard transition).</item>
/// </list>
/// </summary>
public static class SyncKillInventory
{
    /// <summary>A tag as the inventory prints it: kind (class without "hkb"/"SequenceTag"), window (fractions), and what it names.</summary>
    /// <param name="Fields">Extra words: AttackAlignment's @28, @80, @84, @88, @92 (floats for @28/@84/@88, ints for @80/@92).</param>
    public sealed record SyncTag(string Kind, float Start, float End, string? Name, IReadOnlyList<float>? Fields = null);

    /// <param name="StatePath">The state names from the graph root to the sequencer, joined by " / ".</param>
    /// <param name="Clips">The sequencer's clip(s) (blend children in order) with the blend weight (null: a single clip).</param>
    public sealed record SyncMove(string Graph, string StatePath, string Sequencer, int SequencerOffset,
        IReadOnlyList<(string Clip, float? Weight)> Clips, IReadOnlyList<SyncTag> Tags);

    /// <summary>A transition into a state on a move's path: from which state (or "*"), on which event, if which condition.</summary>
    public sealed record SyncEntry(string Graph, string Machine, string From, string To, string? Event, string? Condition, string? Branch)
    {
        /// <summary>The transition's priority (int16 @24) and flags (int16 @28; 0x100 = Havok's FLAG_DISABLE_CONDITION).</summary>
        public short Priority { get; init; }
        public short Flags { get; init; }
    }

    /// <summary>A victim's state entered on a <c>PORT_*</c> event.</summary>
    public sealed record VictimReaction(string Graph, string Event, string From, string StatePath, IReadOnlyList<SyncMove> Sequencers);

    static readonly Regex SyncClip = new(@"_SYNC?_", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>True for a clip name the original authors named as a paired (sync) clip.</summary>
    public static bool IsSyncClip(string clip) => SyncClip.IsMatch(clip);

    /// <summary>True for a state name of a sync kill or struggle branch.</summary>
    public static bool IsSyncState(string state) =>
        state.Contains("sync", StringComparison.OrdinalIgnoreCase) || state.Contains("struggle", StringComparison.OrdinalIgnoreCase);

    static readonly Regex RigPrefix = new(@"^(SM2|SM|BOY|NOB|GRE|CSM|BL|PSY|IG|EB)_(2HM_)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex DistanceSuffix = new(@"_?0?\d+(\.\d+)?m$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// The pairing key of a sync clip: the name without the rig prefix (<c>SM_</c>, <c>SM2_</c>, <c>BOY_</c>, <c>NOB_</c>, <c>GRE_</c>, <c>CSM_</c>,
    /// <c>BL_</c>, <c>PSY_</c>, <c>IG_</c>, <c>EB_</c>, with an optional <c>2HM_</c>) and the distance variant (<c>_1m</c>, <c>_3.5M</c>, <c>7M</c>, <c>_02m</c>),
    /// lower case, <c>SYNC_</c> read as <c>SYN_</c>. The original authors named each victim half after its attacker half this way (interpretation:
    /// no data field links the two; the executable pairs them).
    /// </summary>
    public static string Stem(string clip) =>
        DistanceSuffix.Replace(RigPrefix.Replace(clip, ""), "").ToLowerInvariant().Replace("sync_", "syn_", StringComparison.Ordinal);

    /// <summary>A clip's <c>RIG_Interaction</c> bone at one time: model-space position on the ground plane (metres; X right, Y forward) and heading (degrees, counter-clockwise from +Y).</summary>
    public readonly record struct BoneMark(System.Numerics.Vector2 Position, float HeadingDeg);

    /// <summary>
    /// Where the victim starts in the attacker's frame (attacker at the origin facing +Y) and the victim's yaw relative to the attacker, so that both
    /// clips' interaction bones coincide at the clip start: the authored shared mark (interpretation, docs/executions-coverage.md "Alignment").
    /// </summary>
    public static (System.Numerics.Vector2 VictimStart, float VictimYawDeg) Align(BoneMark attacker, BoneMark victim)
    {
        float yaw = NormalizeDeg(attacker.HeadingDeg - victim.HeadingDeg);
        return (attacker.Position - Rotate(victim.Position, yaw), yaw);
    }

    /// <summary>A ground-plane vector turned counter-clockwise by <paramref name="deg"/> degrees.</summary>
    public static System.Numerics.Vector2 Rotate(System.Numerics.Vector2 v, float deg)
    {
        float r = deg * MathF.PI / 180f, c = MathF.Cos(r), s = MathF.Sin(r);
        return new(v.X * c - v.Y * s, v.X * s + v.Y * c);
    }

    /// <summary>An angle in (-180, 180].</summary>
    public static float NormalizeDeg(float deg)
    {
        float d = deg % 360f;
        if (d <= -180f) d += 360f;
        if (d > 180f) d -= 360f;
        return d;
    }

    /// <summary>Every tag of a sequencer, with the interaction payloads and the alignment words resolved.</summary>
    public static IReadOnlyList<SyncTag> Tags(HavokPackfile pack, IReadOnlyList<string> events, int sequencer)
    {
        var classes = ClassMap(pack);
        var list = new List<SyncTag>();
        var (at, count) = pack.Array(sequencer + 40, 4);
        if (count is < 0 or > 4096) throw new InvalidDataException($"Sequencer @{sequencer}: {count} tags.");
        for (int i = 0; i < count; i++)
        {
            if (pack.Pointer(at!.Value + 4 * i) is not int tag || !classes.TryGetValue(tag, out var cls)) continue;
            string kind = cls.StartsWith("hkb", StringComparison.Ordinal) ? cls[3..] : cls;
            if (kind.EndsWith("SequenceTag", StringComparison.Ordinal)) kind = kind[..^"SequenceTag".Length];
            float start = pack.ReadSingle(tag + 32), end = pack.ReadSingle(tag + 36);
            string? EventAt(int id) => id >= 0 && id < events.Count ? events[id] : null;
            switch (kind)
            {
                case "Interaction":
                {
                    string? name = pack.Pointer(tag + 56) is int payload && classes.GetValueOrDefault(payload) == "hkbInteractionRequestPayload"
                        ? EventAt(pack.ReadInt32(payload + 12)) : null;
                    list.Add(new SyncTag(kind, start, end, name));
                    break;
                }
                case "AttackAlignment":
                    list.Add(new SyncTag(kind, start, end, null,
                        [pack.ReadSingle(tag + 28), pack.ReadInt32(tag + 80), pack.ReadSingle(tag + 84), pack.ReadSingle(tag + 88), pack.ReadInt32(tag + 92)]));
                    break;
                case "Event" or "Branch":
                    list.Add(new SyncTag(kind, start, end, EventAt(pack.ReadInt32(tag + 52))));
                    break;
                case "Action":
                    list.Add(new SyncTag(kind, start, end, pack.String(tag + 52)));
                    break;
                default:
                    list.Add(new SyncTag(kind, start, end, null));
                    break;
            }
        }
        return list;
    }

    /// <summary>The attacker's sync moves in <paramref name="graphs"/>: one per sequencer (a sequencer reached on two paths is listed once, on the first).</summary>
    public static IReadOnlyList<SyncMove> Moves(HavokPackfile pack, IReadOnlyList<BehaviorGraphDetail> graphs)
    {
        var moves = new List<SyncMove>();
        foreach (var g in graphs)
        {
            var seen = new HashSet<int>();
            Walk(g, g.Root, [], new HashSet<int>(), (path, seq) =>
            {
                if (!seen.Add(seq)) return;
                var clips = ClipsOf(g, g.Nodes[seq]);
                if (!path.Any(IsSyncState) && !clips.Any(c => IsSyncClip(c.Clip))) return;
                moves.Add(new SyncMove(g.Name, string.Join(" / ", path), g.Nodes[seq].Name, seq, clips, Tags(pack, g.Events, seq)));
            });
        }
        return moves;
    }

    /// <summary>The transitions into the states on <paramref name="move"/>'s path (each machine's transitions whose target is on the path).</summary>
    public static IReadOnlyList<SyncEntry> Entries(BehaviorGraphDetail g, SyncMove move, HavokPackfile? pack = null)
    {
        var classes = pack is null ? null : ClassMap(pack);
        // A condition the detail reader cannot put in words is shown by its class (e.g. a Relic condition), so it is not mistaken for none.
        string? Cond(BehaviorTransition t) => t.ConditionText ?? (t.Condition is int c && classes is not null ? $"<{classes.GetValueOrDefault(c, "?")}>" : null);
        var names = move.StatePath.Split(" / ").ToHashSet(StringComparer.Ordinal);
        var list = new List<SyncEntry>();
        foreach (var sm in g.StateMachines)
        {
            string StateName(int id) => sm.States.FirstOrDefault(s => s.Id == id)?.Name ?? $"#{id}";
            foreach (var s in sm.States)
                foreach (var t in s.Transitions)
                    if (names.Contains(StateName(t.ToStateId)) && !names.Contains(s.Name))
                        list.Add(new SyncEntry(g.Name, sm.Name, s.Name, StateName(t.ToStateId), t.Event, Cond(t), t.BranchEvent) { Priority = t.Priority, Flags = t.Flags });
            foreach (var t in sm.WildcardTransitions)
                if (names.Contains(StateName(t.ToStateId)))
                    list.Add(new SyncEntry(g.Name, sm.Name, "*", StateName(t.ToStateId), t.Event, Cond(t), t.BranchEvent) { Priority = t.Priority, Flags = t.Flags });
            // Start-state choosers pick a state of the machine by condition on entry (the "CONDUIT" machines).
            foreach (var (id, cond) in sm.StartChoices)
                if (names.Contains(StateName(id)))
                    list.Add(new SyncEntry(g.Name, sm.Name, "(start)", StateName(id), null, cond, null));
        }
        return list;
    }

    /// <summary>The victim states the <paramref name="events"/> enter in <paramref name="graphs"/>, with every sequencer under each.</summary>
    public static IReadOnlyList<VictimReaction> Reactions(HavokPackfile pack, IReadOnlyList<BehaviorGraphDetail> graphs, IReadOnlySet<string> events)
    {
        var list = new List<VictimReaction>();
        foreach (var g in graphs)
        {
            // Where each state machine sits (its path from the root), so a reaction state's path can be printed.
            var machinePaths = new Dictionary<int, List<string>>();
            WalkMachines(g, g.Root, [], new HashSet<int>(), machinePaths);
            foreach (var sm in g.StateMachines)
            {
                var basePath = machinePaths.GetValueOrDefault(sm.Offset) ?? [];
                void Add(string from, BehaviorTransition t)
                {
                    if (t.Event is not { } e || !events.Contains(e)) return;
                    if (sm.States.FirstOrDefault(s => s.Id == t.ToStateId) is not { } to) return;
                    var path = new List<string>(basePath) { to.Name };
                    var seqs = new List<SyncMove>();
                    var seen = new HashSet<int>();
                    Walk(g, to.Generator, path, new HashSet<int>(), (p, seq) =>
                    {
                        if (!seen.Add(seq)) return;
                        seqs.Add(new SyncMove(g.Name, string.Join(" / ", p), g.Nodes[seq].Name, seq, ClipsOf(g, g.Nodes[seq]), Tags(pack, g.Events, seq)));
                    });
                    list.Add(new VictimReaction(g.Name, e, from, string.Join(" / ", path), seqs));
                }
                foreach (var s in sm.States) foreach (var t in s.Transitions) Add(s.Name, t);
                foreach (var t in sm.WildcardTransitions) Add("*", t);
            }
        }
        return list;
    }

    /// <summary>The event names the interaction tags of <paramref name="moves"/> send.</summary>
    public static IReadOnlySet<string> SentEvents(IEnumerable<SyncMove> moves) =>
        moves.SelectMany(m => m.Tags).Where(t => t.Kind == "Interaction" && t.Name is not null).Select(t => t.Name!).ToHashSet(StringComparer.Ordinal);

    const int MaxDepth = 48;

    static void Walk(BehaviorGraphDetail g, int? at, List<string> path, HashSet<int> onPath, Action<List<string>, int> sequencer)
    {
        if (at is not int node || !g.Nodes.TryGetValue(node, out var n) || path.Count > MaxDepth || !onPath.Add(node)) return;
        if (n.Class == "hkbSequencerGenerator") sequencer(path, node);
        foreach (var c in n.Children)
        {
            string? state = StateName(c.Label);
            if (state is not null) path.Add(state);
            Walk(g, c.Target, path, onPath, sequencer);
            if (state is not null) path.RemoveAt(path.Count - 1);
        }
        onPath.Remove(node);
    }

    static void WalkMachines(BehaviorGraphDetail g, int? at, List<string> path, HashSet<int> onPath, Dictionary<int, List<string>> found)
    {
        if (at is not int node || !g.Nodes.TryGetValue(node, out var n) || path.Count > MaxDepth || !onPath.Add(node)) return;
        if (n.Class == "hkbStateMachine") found.TryAdd(node, [.. path]);
        foreach (var c in n.Children)
        {
            string? state = StateName(c.Label);
            if (state is not null) path.Add(state);
            WalkMachines(g, c.Target, path, onPath, found);
            if (state is not null) path.RemoveAt(path.Count - 1);
        }
        onPath.Remove(node);
    }

    /// <summary>"state 21 'Chainsword Sync Kill'" → "Chainsword Sync Kill"; null for other link labels.</summary>
    internal static string? StateName(string label)
    {
        if (!label.StartsWith("state ", StringComparison.Ordinal)) return null;
        int a = label.IndexOf('\''), b = label.LastIndexOf('\'');
        return a >= 0 && b > a ? label[(a + 1)..b] : label;
    }

    /// <summary>A sequencer's clips: its single clip, or each blend child's clip with the child's weight (the distance variant, metres, on the sync blends).</summary>
    static List<(string Clip, float? Weight)> ClipsOf(BehaviorGraphDetail g, BehaviorNode sequencer)
    {
        var list = new List<(string, float?)>();
        void Visit(int? at, float? weight, int depth)
        {
            if (at is not int o || depth > 8) return;
            if (g.ClipAt(o) is { } clip) { list.Add((clip.Animation, weight)); return; }
            if (g.BlenderAt(o) is { } blend) { foreach (var ch in blend.Children) Visit(ch.Generator, ch.Weight, depth + 1); return; }
            if (g.Nodes.TryGetValue(o, out var n) && n.Class is "hkbModifierGenerator")
                foreach (var c in n.Children) Visit(c.Target, weight, depth + 1);
        }
        foreach (var c in sequencer.Children) Visit(c.Target, null, 0);
        return list;
    }

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<HavokPackfile, Dictionary<int, string>> ClassMaps = new();

    static Dictionary<int, string> ClassMap(HavokPackfile pack) => ClassMaps.GetValue(pack, p =>
    {
        var classes = new Dictionary<int, string>();
        foreach (var o in p.Objects) classes.TryAdd(o.Offset, o.ClassName);
        return classes;
    });
}
