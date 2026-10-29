namespace SmFormats.Havok;

/// <summary>
/// A behaviour-graph node (generator, state, modifier) with its outgoing links, for walking a graph's structure.
/// </summary>
/// <param name="Class">Havok class name, e.g. "hkbBlenderGenerator".</param>
/// <param name="Children">Linked nodes, each with a label (state id and name, blend weight, …).</param>
/// <param name="Bindings">Variable bindings: bound member path (e.g. "blendParameter") → graph variable name.</param>
public sealed record BehaviorNode(int Offset, string Class, string Name, IReadOnlyList<BehaviorLink> Children, IReadOnlyDictionary<string, string> Bindings);

/// <param name="Target">Data-section offset of the linked node; null for a null pointer.</param>
public sealed record BehaviorLink(int? Target, string Label);

/// <summary>A blend child: its generator, weight, optional bone mask and world-from-model weight.</summary>
public sealed record BehaviorBlendChild(int Offset, int? Generator, int? BoneWeights, float Weight, float WorldFromModelWeight);

/// <summary>
/// An <c>hkbBlenderGenerator</c>. <paramref name="BlendParameter"/> chooses between children by their weights unless the
/// blend's flags say the weights are used directly; it is usually bound to a graph variable (<see cref="BehaviorNode.Bindings"/>).
/// </summary>
public sealed record BehaviorBlender(int Offset, string Name, float ReferencePoseWeightThreshold, float BlendParameter,
    float MinCyclicBlendParameter, float MaxCyclicBlendParameter, short IndexOfSyncMasterChild, short Flags, bool SubtractLastChild,
    IReadOnlyList<BehaviorBlendChild> Children);

/// <summary>
/// An <c>hkbBoneWeightArray</c>: one weight per skeleton bone, by bone index. Masks come in pairs on a two-child blend: the
/// lower layer lists every bone (1 on the legs, 0 elsewhere) while the upper layer lists only the leg bones, at 0. Bones past
/// the end are therefore read as weight 1 (<see cref="WeightOf"/>; interpretation: read as 0 the upper body would have no
/// weight in either layer).
/// </summary>
public sealed record BehaviorBoneWeights(int Offset, IReadOnlyList<float> Weights)
{
    public float WeightOf(int bone) => bone >= 0 && bone < Weights.Count ? Weights[bone] : 1f;
}

/// <param name="Event">The event that triggers it (resolved through the graph's event names); null when none (-1) or out of range.</param>
/// <param name="Transition">The transition effect (e.g. an <c>hkbBlendingTransitionEffect</c>), or null.</param>
/// <param name="Priority">int16 @24 (Havok: priority; the stumble entry's eight exits are numbered 9 down to 1).</param>
/// <param name="BranchEvent">int16 @26 resolved through the event names: the sequencer branch whose open window takes this
/// transition (Relic; e.g. "BRANCH_Idle_To_Run_End" on the idle-to-run start, "BRANCH_to_Fire" in the rifle recoil); null when -1.</param>
/// <param name="ConditionText">The condition in readable form (<see cref="HavokBehaviorDetailReader"/> lists the condition layouts), or null.</param>
public sealed record BehaviorTransition(string? Event, int EventId, int ToStateId, int? Transition, int? Condition, short Priority, string? BranchEvent,
    string? ConditionText);

public sealed record BehaviorState(int Offset, int Id, string Name, int? Generator, float Probability, bool Enable, IReadOnlyList<BehaviorTransition> Transitions);

/// <param name="StartChoices">The start-state chooser's entries (<c>hkbConditionChooser</c>: entries @8, each an
/// <c>hkbConditionChooserConditionEntry</c> with state id @8 and condition @12), in order; empty without a chooser.</param>
public sealed record BehaviorStateMachine(int Offset, string Name, int StartStateId, int? StartStateChooser, IReadOnlyList<BehaviorState> States,
    IReadOnlyList<BehaviorTransition> WildcardTransitions, IReadOnlyList<(int StateId, string? Condition)> StartChoices);

/// <param name="Mode">Playback mode byte @74 (Havok: 0 single play, 1 looping, 2 user controlled, 3 ping-pong, 4 count; assumed).</param>
public sealed record BehaviorClip(int Offset, string Name, string Animation, float PlaybackSpeed, int Mode)
{
    /// <summary>feel/jump-pack-2: <c>cropStartAmountLocalTime</c> @48 (seconds cut from the clip's start; interpretation of the layout: the fields between
    /// the animation name @40 and the playback speed @60 in hkbClipGenerator's declared order: triggers @44, crop start @48, crop end @52, start time @56).</summary>
    public float CropStart { get; init; }
    /// <summary>feel/jump-pack-2: <c>cropEndAmountLocalTime</c> @52 (seconds cut from the clip's end).</summary>
    public float CropEnd { get; init; }
    /// <summary>feel/jump-pack-2: <c>startTime</c> @56 (seconds into the cropped clip where playback starts).</summary>
    public float StartTime { get; init; }
}

/// <summary>An <c>hkbBlendingTransitionEffect</c>: duration @64, seconds (assumed; values are whole frames at 30 fps).</summary>
public sealed record BehaviorBlendEffect(int Offset, string Name, float Duration, int Flags);

/// <param name="Sequencers">The graph's sequencers (as <see cref="HavokBehavior"/> reads them) by object offset.</param>
public sealed record BehaviorGraphDetail(string Name, int Start, int End, int? Root, IReadOnlyDictionary<string, int> ClassCounts,
    IReadOnlyList<string> Events, IReadOnlyList<string> Variables, IReadOnlyDictionary<int, BehaviorNode> Nodes,
    IReadOnlyDictionary<int, BehaviorSequencer> Sequencers, IReadOnlyList<BehaviorBlender> Blenders, IReadOnlyList<BehaviorBoneWeights> BoneWeights,
    IReadOnlyList<BehaviorStateMachine> StateMachines, IReadOnlyList<BehaviorClip> Clips, IReadOnlyList<BehaviorBlendEffect> BlendEffects)
{
    public BehaviorStateMachine? MachineAt(int offset) => StateMachines.FirstOrDefault(m => m.Offset == offset);
    public BehaviorBlender? BlenderAt(int offset) => Blenders.FirstOrDefault(b => b.Offset == offset);
    public BehaviorClip? ClipAt(int offset) => Clips.FirstOrDefault(c => c.Offset == offset);
    public BehaviorBlendEffect? EffectAt(int? offset) => offset is int o ? BlendEffects.FirstOrDefault(e => e.Offset == o) : null;
    public BehaviorBoneWeights? BoneWeightsAt(int? offset) => offset is int o ? BoneWeights.FirstOrDefault(b => b.Offset == o) : null;

    /// <summary>Every state of every state machine, with the machine it belongs to.</summary>
    public IEnumerable<(BehaviorStateMachine Machine, BehaviorState State)> AllStates =>
        StateMachines.SelectMany(m => m.States.Select(s => (m, s)));

    /// <summary>Blend children that carry a bone mask, with the blend they belong to.</summary>
    public IEnumerable<(BehaviorBlender Blend, BehaviorBlendChild Child)> MaskedChildren =>
        Blenders.SelectMany(b => b.Children.Where(c => c.BoneWeights is not null).Select(c => (b, c)));
}

/// <summary>
/// The structure of the behaviour graphs in a character packfile (hk_2010, 32-bit), beyond the sequencer timing that
/// <see cref="HavokBehavior"/> reads: state machines and their transitions, blends with weights and bone masks, clip
/// generators, modifier and behaviour-reference generators, variable bindings and blending transitions. Layouts were
/// established on the install's own <c>havok_sm_base_mp__hkxx</c> (M27; docs/discovery.md "Behaviour graph structure"):
/// <list type="bullet">
/// <item><c>hkbBindable</c> base: variable binding set @8. <c>hkbNode</c>: name @32.</item>
/// <item><c>hkbVariableBindingSet</c>: bindings @8, 32 bytes each: member path @0, variable index @20.</item>
/// <item><c>hkbBlenderGenerator</c>: reference-pose threshold @40, blend parameter @44, cyclic min/max @48/@52, sync master
/// child (int16) @56, flags (int16) @58, subtract last child (byte) @60, children (pointer array) @64.</item>
/// <item><c>hkbBlenderGeneratorChild</c>: generator @32, bone weights @36, weight @40, world-from-model weight @44.</item>
/// <item><c>hkbBoneWeightArray</c>: weights (float array) @28.</item>
/// <item><c>hkbStateMachine</c>: start state chooser @52, start state id @56, states (pointer array) @92, wildcard
/// transitions @104. <c>hkbStateMachineStateInfo</c>: transitions @48, generator @52, name @56, id @60, probability @64,
/// enable (byte) @68. <c>hkbStateMachineTransitionInfoArray</c>: entries @8, 44 bytes each: transition effect @0, condition
/// @4, event id @8, to-state id @12, priority (int16) @24, branch event (int16) @26.</item>
/// <item><c>hkbClipGenerator</c>: animation @40, playback speed @60, mode (byte) @74. <c>hkbModifierGenerator</c>: modifier
/// @40, generator @44. <c>hkbBehaviorReferenceGenerator</c>: behaviour file @40. <c>hkbSequencerGenerator</c>: generator @52.
/// <c>hkbBlendingTransitionEffect</c>: duration @64, flags (int16) @72.</item>
/// </list>
/// </summary>
public static class HavokBehaviorDetailReader
{
    const int MaxItems = 4096;

    public static IReadOnlyList<BehaviorGraphDetail> Read(HavokPackfile pack)
    {
        var objects = pack.Objects.OrderBy(o => o.Offset).ToList();
        var classes = new Dictionary<int, string>();
        foreach (var o in objects) classes.TryAdd(o.Offset, o.ClassName);
        var simple = HavokBehavior.Read(pack);
        var starts = objects.Where(o => o.ClassName == "hkbBehaviorGraph").Select(o => o.Offset).ToList();
        var graphs = new List<BehaviorGraphDetail>();
        for (int g = 0; g < starts.Count; g++)
        {
            int start = starts[g];
            var members = objects.Where(o => o.Offset >= start && (g + 1 >= starts.Count || o.Offset < starts[g + 1])).ToList();
            // A graph's objects end with its string data; what follows the last graph is not part of it.
            int last = members.FindIndex(o => o.ClassName == "hkbBehaviorGraphStringData");
            if (last >= 0) members = members.Take(last + 1).ToList();
            graphs.Add(Graph(pack, classes, members, start, simple[g].Name, simple[g].Events));
        }
        return graphs;
    }

    static BehaviorGraphDetail Graph(HavokPackfile pack, Dictionary<int, string> classes, List<HavokObject> members, int start, string graphName, IReadOnlyList<string> events)
    {
        var variables = new List<string>();
        if (members.FirstOrDefault(o => o.ClassName == "hkbBehaviorGraphStringData") is { } sd)
        {
            var (at, count) = pack.Array(sd.Offset + 32, 4);
            Check(count, "variable names");
            for (int i = 0; i < count; i++) variables.Add(pack.String(at!.Value + 4 * i) ?? "");
        }
        var eventList = events.ToList();
        var sequencers = new Dictionary<int, BehaviorSequencer>();
        string? EventName(int id) => id >= 0 && id < events.Count ? events[id] : null;
        string? Class(int? at) => at is int a ? classes.GetValueOrDefault(a) : null;

        var nodes = new Dictionary<int, BehaviorNode>();
        var blenders = new List<BehaviorBlender>();
        var masks = new List<BehaviorBoneWeights>();
        var machines = new List<BehaviorStateMachine>();
        var clips = new List<BehaviorClip>();
        var effects = new List<BehaviorBlendEffect>();

        List<BehaviorTransition> Transitions(int? array)
        {
            var list = new List<BehaviorTransition>();
            if (array is not int a || Class(a) != "hkbStateMachineTransitionInfoArray") return list;
            var (at, count) = pack.Array(a + 8, 44);
            Check(count, "transitions");
            for (int i = 0; i < count; i++)
            {
                int t = at!.Value + 44 * i;
                int eventId = pack.ReadInt32(t + 8);
                int? condition = pack.Pointer(t + 4);
                list.Add(new BehaviorTransition(EventName(eventId), eventId, pack.ReadInt32(t + 12), pack.Pointer(t), condition, pack.ReadInt16(t + 24),
                    EventName(pack.ReadInt16(t + 26)), condition is int c ? Condition(pack, classes, c, variables, 0) : null));
            }
            return list;
        }

        IReadOnlyList<int?> PointerArray(int field, string what)
        {
            var (at, count) = pack.Array(field, 4);
            Check(count, what);
            var list = new List<int?>(count);
            for (int i = 0; i < count; i++) list.Add(pack.Pointer(at!.Value + 4 * i));
            return list;
        }

        foreach (var m in members)
        {
            int o = m.Offset;
            string name = IsNode(m.ClassName) ? pack.String(o + 32) ?? "" : "";
            var links = new List<BehaviorLink>();
            switch (m.ClassName)
            {
                case "hkbBehaviorGraph":
                    links.Add(new BehaviorLink(pack.Pointer(o + 76), "root"));
                    break;
                case "hkbBlenderGenerator":
                {
                    var children = new List<BehaviorBlendChild>();
                    foreach (var c in PointerArray(o + 64, "blend children"))
                    {
                        if (c is not int child || Class(child) != "hkbBlenderGeneratorChild") continue;
                        int? weights = pack.Pointer(child + 36);
                        if (weights is int w && Class(w) != "hkbBoneWeightArray") weights = null;
                        var bc = new BehaviorBlendChild(child, pack.Pointer(child + 32), weights, pack.ReadSingle(child + 40), pack.ReadSingle(child + 44));
                        children.Add(bc);
                        links.Add(new BehaviorLink(bc.Generator, $"weight {bc.Weight:0.###}" + (weights is int mw ? $" mask @{mw}" : "")));
                    }
                    blenders.Add(new BehaviorBlender(o, name, pack.ReadSingle(o + 40), pack.ReadSingle(o + 44), pack.ReadSingle(o + 48), pack.ReadSingle(o + 52),
                        pack.ReadInt16(o + 56), pack.ReadInt16(o + 58), pack.ReadByte(o + 60) != 0, children));
                    break;
                }
                case "hkbBoneWeightArray":
                {
                    var (at, count) = pack.Array(o + 28, 4);
                    Check(count, "bone weights");
                    var w = new float[count];
                    for (int i = 0; i < count; i++) w[i] = pack.ReadSingle(at!.Value + 4 * i);
                    masks.Add(new BehaviorBoneWeights(o, w));
                    break;
                }
                case "hkbStateMachine":
                {
                    var states = new List<BehaviorState>();
                    foreach (var s in PointerArray(o + 92, "states"))
                    {
                        if (s is not int st || Class(st) != "hkbStateMachineStateInfo") continue;
                        var state = new BehaviorState(st, pack.ReadInt32(st + 60), pack.String(st + 56) ?? "", pack.Pointer(st + 52),
                            pack.ReadSingle(st + 64), pack.ReadByte(st + 68) != 0, Transitions(pack.Pointer(st + 48)));
                        states.Add(state);
                        links.Add(new BehaviorLink(state.Generator, $"state {state.Id} '{state.Name}'"));
                    }
                    var choices = new List<(int, string?)>();
                    if (pack.Pointer(o + 52) is int chooser && Class(chooser) == "hkbConditionChooser")
                        foreach (var e in PointerArray(chooser + 8, "chooser entries"))
                            if (e is int entry && Class(entry) == "hkbConditionChooserConditionEntry")
                                choices.Add((pack.ReadInt32(entry + 8), pack.Pointer(entry + 12) is int c ? Condition(pack, classes, c, variables, 0) : null));
                    machines.Add(new BehaviorStateMachine(o, name, pack.ReadInt32(o + 56), pack.Pointer(o + 52), states, Transitions(pack.Pointer(o + 104)), choices));
                    break;
                }
                case "hkbClipGenerator":
                {
                    string file = pack.String(o + 40) ?? "";
                    if (file.EndsWith(".hkxa", StringComparison.OrdinalIgnoreCase)) file = file[..^5];
                    clips.Add(new BehaviorClip(o, name, file, pack.ReadSingle(o + 60), pack.ReadByte(o + 74)) { CropStart = pack.ReadSingle(o + 48), CropEnd = pack.ReadSingle(o + 52), StartTime = pack.ReadSingle(o + 56) });
                    break;
                }
                case "hkbSequencerGenerator":
                    sequencers[o] = HavokBehavior.Sequencer(pack, classes, o, eventList);
                    links.Add(new BehaviorLink(pack.Pointer(o + 52), "plays"));
                    break;
                case "hkbModifierGenerator":
                    links.Add(new BehaviorLink(pack.Pointer(o + 40), "modifier"));
                    links.Add(new BehaviorLink(pack.Pointer(o + 44), "generator"));
                    break;
                case "hkbBehaviorReferenceGenerator":
                    name = $"{name} -> {pack.String(o + 40)}";
                    break;
                case "hkbBlendingTransitionEffect":
                    effects.Add(new BehaviorBlendEffect(o, name, pack.ReadSingle(o + 64), pack.ReadInt16(o + 72)));
                    break;
            }
            if (!IsNode(m.ClassName)) continue;
            nodes[o] = new BehaviorNode(o, m.ClassName, name, links, Bindings(pack, classes, o, variables));
        }
        int? root = nodes.TryGetValue(start, out var graphNode) ? graphNode.Children.FirstOrDefault()?.Target : null;
        int end = members.Count > 0 ? members[^1].Offset + 1 : start + 1;
        var counts = members.GroupBy(o => o.ClassName).ToDictionary(x => x.Key, x => x.Count());
        return new BehaviorGraphDetail(graphName, start, end, root, counts, events, variables, nodes, sequencers, blenders, masks, machines, clips, effects);
    }

    /// <summary>
    /// A condition in readable form. Layouts (M27, interpretation where marked): <c>hkbExpressionCondition</c> expression
    /// string @8; <c>hkbThresholdCondition</c> min @8, max @12, variable @20; <c>hkbBoolCondition</c> (Relic) two int arrays of
    /// variable indices, @8 read as "must be true" and @20 as "must be false" (interpretation: the stand-to-fall condition
    /// lists BOOL_IsSupported in the second); <c>hkbNegateCondition</c> condition @8; <c>hkbMultiCondition</c> kind @8 (0 all, 1 any:
    /// the idle-to-run test is "speed ≥ 0.5 and no wall", its reverse "speed &lt; 0.5 or wall"), conditions @12; <c>hkbPieWedgeCondition</c> min and max angle @16/@20.
    /// </summary>
    static string Condition(HavokPackfile pack, Dictionary<int, string> classes, int at, List<string> variables, int depth)
    {
        if (depth > 8) return "...";
        string Var(int i) => i >= 0 && i < variables.Count ? variables[i] : $"#{i}";
        IEnumerable<int> Ints(int field)
        {
            var (data, count) = pack.Array(field, 4);
            Check(count, "condition variables");
            for (int i = 0; i < count; i++) yield return pack.ReadInt32(data!.Value + 4 * i);
        }
        switch (classes.GetValueOrDefault(at))
        {
            case "hkbExpressionCondition": return $"({pack.String(at + 8)})";
            case "hkbThresholdCondition": return $"({Var(pack.ReadInt32(at + 20))} in [{pack.ReadSingle(at + 8):0.###}, {pack.ReadSingle(at + 12):0.###}])";
            case "hkbBoolCondition":
            {
                var parts = Ints(at + 8).Select(Var).Concat(Ints(at + 20).Select(v => "!" + Var(v))).ToList();
                return parts.Count == 1 ? parts[0] : "(" + string.Join(" & ", parts) + ")";
            }
            case "hkbNegateCondition": return pack.Pointer(at + 8) is int n ? "not " + Condition(pack, classes, n, variables, depth + 1) : "not ?";
            case "hkbMultiCondition":
            {
                var (data, count) = pack.Array(at + 12, 4);
                Check(count, "conditions");
                var parts = new List<string>();
                for (int i = 0; i < count; i++)
                    parts.Add(pack.Pointer(data!.Value + 4 * i) is int c ? Condition(pack, classes, c, variables, depth + 1) : "null");
                int kind = pack.ReadInt32(at + 8);
                return (kind switch { 0 => "all", 1 => "any", _ => $"multi{kind}" }) + "[" + string.Join(", ", parts) + "]";
            }
            case "hkbPieWedgeCondition": return $"(wedge {pack.ReadSingle(at + 16):0.###}..{pack.ReadSingle(at + 20):0.###} deg)";
            default: return classes.GetValueOrDefault(at) ?? "?";
        }
    }

    static Dictionary<string, string> Bindings(HavokPackfile pack, Dictionary<int, string> classes, int node, List<string> variables)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (pack.Pointer(node + 8) is not int set || classes.GetValueOrDefault(set) != "hkbVariableBindingSet") return result;
        var (at, count) = pack.Array(set + 8, 32);
        Check(count, "variable bindings");
        for (int i = 0; i < count; i++)
        {
            int b = at!.Value + 32 * i;
            int v = pack.ReadInt32(b + 20);
            if (pack.String(b) is { } path) result[path] = v >= 0 && v < variables.Count ? variables[v] : $"#{v}";
        }
        return result;
    }

    static bool IsNode(string cls) => cls.StartsWith("hkb", StringComparison.Ordinal) &&
        (cls.EndsWith("Generator", StringComparison.Ordinal) || cls.EndsWith("Modifier", StringComparison.Ordinal) ||
         cls.EndsWith("TransitionEffect", StringComparison.Ordinal) || cls is "hkbStateMachine" or "hkbBehaviorGraph" or "hkbModifierList");

    static void Check(int count, string what)
    {
        if (count > MaxItems) throw new InvalidDataException($"Behaviour graph: {count} {what} (at most {MaxItems}).");
    }
}
