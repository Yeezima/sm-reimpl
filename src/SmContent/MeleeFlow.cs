using SmFormats.Havok;

namespace SmContent;

/// <summary>
/// anim/melee-flow (the owner, 2026-10-29: the power sword and chainsword slash "feels quite janky ... copy the animation from sm1 better"): the blends a melee weapon's graph puts
/// between its strikes, read from its state machines (client presentation only; the simulation's timing is <see cref="MeleeTuning"/>'s). For each strike the simulation knows (the
/// <see cref="MeleeTuning.WeaponMap"/>'s light and heavy sequencers) the state that plays it is found (the smallest state whose generator reaches the sequencer and that some
/// transition enters), and from the transitions of its state machine:
/// <list type="bullet">
/// <item><b>Entry</b>: the blend from the attack machine's entry conduit (<c>ENTRY - ... Attack Chain</c>, else another <c>ENTRY</c> state) into the strike: the first strike of a
/// string, out of the navigation (the chainsword's and power sword's 'Entry to Melee' 0.167 s; the hammer's first swing 'Transition to 1' 0.4 s, its interrupt strike 'Blend Three Frame' 0.1 s).</item>
/// <item><b>Chain</b>: the blend of the transition from one strike's state to another's ('Melee To Melee' 0.1 s, 'Melee to 180' 0.333 s, 'Blend In to Slam' 0.267 s ...; 0 when the
/// transition carries no blend: the clips are authored to run into each other).</item>
/// <item><b>Exit</b>: the attack state's way back to the navigation state of the same graph: on <c>BRANCH_Melee_End</c> (the strike played out; the chainsword and power sword
/// 'Standard Blend' 0.333 s, the hammer 0.1 s) and on the move branch with the stick held (<c>BRANCH_Melee_Run</c> / <c>BRANCH_Evade</c> with <c>FLT_Input_Nav_Speed</c> in
/// range: 0.333 s). The knife's graph leaves through the base graph's conduits: no exit is read (null).</item>
/// </list>
/// Which graph state the nested conduits really pass through when Havok enters a machine is not modelled: the inner entry's blend is taken (interpretation).
/// </summary>
public sealed class MeleeFlowBlends
{
    public string Graph { get; }
    /// <summary>The graph state names of the light strikes and the heavy strikes (heavies then finishers), as the simulation numbers them; null where none was found.</summary>
    public IReadOnlyList<string?> LightStates { get; }
    public IReadOnlyList<string?> HeavyStates { get; }
    /// <summary>Seconds of the blend into the strike out of the navigation (the entry conduit's transition); null: not read.</summary>
    public IReadOnlyDictionary<int, float> Entries { get; }
    /// <summary>Seconds of the blend from strike to strike (keys: <see cref="Key"/>); 0: a transition without a blend.</summary>
    public IReadOnlyDictionary<(int From, int To), float> Chains { get; }
    /// <summary>Seconds of the blend back to the navigation when the strike has played out (<c>BRANCH_Melee_End</c>), and when the marine moves on (the move branch); null: not read.</summary>
    public float? ExitEnd { get; }
    public float? ExitRun { get; }
    public IReadOnlyList<string> Notes { get; }

    public MeleeFlowBlends(string graph, IReadOnlyList<string?> light, IReadOnlyList<string?> heavy, IReadOnlyDictionary<int, float> entries,
        IReadOnlyDictionary<(int, int), float> chains, float? exitEnd, float? exitRun, IReadOnlyList<string> notes)
    {
        Graph = graph; LightStates = light; HeavyStates = heavy; Entries = entries; Chains = chains; ExitEnd = exitEnd; ExitRun = exitRun; Notes = notes;
    }

    /// <summary>The key of a strike: light strike <paramref name="index"/> (0-based) or heavy strike <paramref name="index"/> (0-based, heavies then finishers).</summary>
    public static int Key(bool heavy, int index) => heavy ? 1000 + index : index;

    /// <summary>The blend into a strike entered from the navigation, or null.</summary>
    public float? EntryOf(bool heavy, int index) => Entries.TryGetValue(Key(heavy, index), out var v) ? v : null;

    /// <summary>The blend from strike to strike, or null when the graph has no such transition.</summary>
    public float? ChainOf(bool fromHeavy, int fromIndex, bool heavy, int index) =>
        Chains.TryGetValue((Key(fromHeavy, fromIndex), Key(heavy, index)), out var v) ? v : null;

    /// <summary>Reads a weapon's blends from the graphs (<paramref name="graphs"/>: <see cref="HavokBehaviorDetailReader.Read"/>); null when its graph is missing.</summary>
    public static MeleeFlowBlends? Read(IReadOnlyList<BehaviorGraphDetail> graphs, MeleeTuning.WeaponMap weapon)
    {
        var g = graphs.FirstOrDefault(x => x.Name.Equals(weapon.Graph, StringComparison.OrdinalIgnoreCase));
        if (g is null) return null;
        var notes = new List<string>();
        var reach = new Dictionary<int, HashSet<string>>();
        HashSet<string> Reachable(int? node)
        {
            if (node is not int at) return [];
            if (reach.TryGetValue(at, out var known)) return known;
            var found = new HashSet<string>(StringComparer.Ordinal);
            reach[at] = found;   // cycles end here
            if (g.Sequencers.TryGetValue(at, out var seq)) found.Add(seq.Name);
            if (g.Nodes.TryGetValue(at, out var n))
                foreach (var c in n.Children)
                    if (c.Target is int t && t != at) found.UnionWith(Reachable(t));
            return found;
        }
        int Incoming(BehaviorStateMachine m, BehaviorState s) =>
            m.States.Sum(o => o.Id == s.Id ? 0 : o.Transitions.Count(t => t.ToStateId == s.Id)) + m.WildcardTransitions.Count(t => t.ToStateId == s.Id);
        (BehaviorStateMachine Machine, BehaviorState State)? StateOf(string sequencer)
        {
            var c = g.AllStates.Where(x => Reachable(x.State.Generator).Contains(sequencer))
                .OrderBy(x => Reachable(x.State.Generator).Count).ToList();
            foreach (var x in c) if (Incoming(x.Machine, x.State) > 0) return x;
            return null;
        }
        float Seconds(BehaviorTransition t) => g.EffectAt(t.Transition) is { } e && float.IsFinite(e.Duration) && e.Duration >= 0f ? e.Duration : 0f;

        var lightStates = weapon.Sequencers.Select(StateOf).ToList();
        var heavyStates = weapon.HeavySequencers.Concat(weapon.FinisherSequencers).Select(StateOf).ToList();
        var all = lightStates.Select((s, i) => (Key: Key(false, i), S: s)).Concat(heavyStates.Select((s, i) => (Key: Key(true, i), S: s))).ToList();
        foreach (var (key, s) in all) if (s is null) notes.Add($"{weapon.Graph}: no state found for strike {key}");

        var entries = new Dictionary<int, float>();
        var chains = new Dictionary<(int, int), float>();
        foreach (var (key, target) in all)
        {
            if (target is not { } t) continue;
            // Entry: from the machine's entry conduits (the attack chain's, not the "from stowed" one, when there is a choice).
            var conduits = t.Machine.States.Where(s => s.Name.StartsWith("ENTRY", StringComparison.OrdinalIgnoreCase))
                .OrderBy(s => s.Name.Contains("Stowed", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ThenBy(s => s.Name.Contains("Chain", StringComparison.OrdinalIgnoreCase) ? 0 : 1);
            foreach (var conduit in conduits)
                if (conduit.Transitions.Where(x => x.ToStateId == t.State.Id).OrderBy(x => x.Priority).FirstOrDefault() is { } tr) { entries[key] = Seconds(tr); break; }
            // Chains: from every other strike's state in the same machine.
            foreach (var (fromKey, source) in all)
            {
                if (source is not { } f || !ReferenceEquals(f.Machine, t.Machine) || fromKey == key) continue;
                var into = f.State.Transitions.Where(x => x.ToStateId == t.State.Id).ToList();
                if (into.Count == 0) continue;
                // A combo's own branch first (BRANCH_Melee_Combo, _Y_Combo, BRANCH_To_Slam), else the first.
                var pick = into.FirstOrDefault(x => x.BranchEvent is { } b && (b.Contains("Combo", StringComparison.OrdinalIgnoreCase) || b.Contains("Slam", StringComparison.OrdinalIgnoreCase))) ?? into[0];
                chains[(fromKey, key)] = Seconds(pick);
            }
        }

        // Exits: the state of the outer machine that holds the strikes and leaves to the graph's navigation state.
        float? exitEnd = null, exitRun = null;
        var anyStrike = weapon.Sequencers.FirstOrDefault();
        if (anyStrike is not null)
            foreach (var (m, s) in g.AllStates.Where(x => Reachable(x.State.Generator).Contains(anyStrike)).OrderBy(x => Reachable(x.State.Generator).Count))
            {
                var nav = m.States.Where(n => n.Name.EndsWith("Nav", StringComparison.OrdinalIgnoreCase)).Select(n => n.Id).ToHashSet();
                var outs = s.Transitions.Where(x => nav.Contains(x.ToStateId)).ToList();
                if (outs.Count == 0) continue;
                if (outs.FirstOrDefault(x => x.BranchEvent == "BRANCH_Melee_End") is { } end) exitEnd = Seconds(end);
                if (outs.FirstOrDefault(x => x.BranchEvent is "BRANCH_Melee_Run" or "BRANCH_Evade" && (x.ConditionText ?? "").Contains("Nav_Speed", StringComparison.OrdinalIgnoreCase)) is { } run) exitRun = Seconds(run);
                break;
            }
        if (exitEnd is null) notes.Add($"{weapon.Graph}: no way back to the navigation on BRANCH_Melee_End in this graph");
        return new MeleeFlowBlends(weapon.Graph, lightStates.Select(s => s?.State.Name).ToList(), heavyStates.Select(s => s?.State.Name).ToList(), entries, chains, exitEnd, exitRun, notes);
    }

    /// <summary>One line per strike and the exits, for <c>smtool graph-dump --melee-flow</c>.</summary>
    public IEnumerable<string> Describe()
    {
        string Name(int key) => key >= 1000 ? $"heavy {key - 999} ({HeavyStates[key - 1000] ?? "-"})" : $"light {key + 1} ({LightStates[key] ?? "-"})";
        for (int i = 0; i < LightStates.Count; i++) yield return $"{Name(Key(false, i))}: entry {Fmt(EntryOf(false, i))}";
        for (int i = 0; i < HeavyStates.Count; i++) yield return $"{Name(Key(true, i))}: entry {Fmt(EntryOf(true, i))}";
        foreach (var ((from, to), s) in Chains.OrderBy(kv => kv.Key.From).ThenBy(kv => kv.Key.To)) yield return $"  {Name(from)} -> {Name(to)}: {s:0.###} s";
        yield return $"exit: played out {Fmt(ExitEnd)}, moving on {Fmt(ExitRun)}";
        foreach (var n in Notes) yield return "  note: " + n;
        static string Fmt(float? v) => v is float f ? $"{f:0.###} s" : "-";
    }
}
