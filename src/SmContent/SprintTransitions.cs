using SmFormats.Havok;

namespace SmContent;

/// <summary>
/// One clip that leads into or out of the sprint loop (animation pass 3): the sprint start (<c>SM_NAV_Bullrush_Start_F_01</c>) or the sprint stop
/// (<c>SM_NAV_Bullrush2Idle_F_01</c>, <c>SM_1HR_NAV_Sprint_to_Idle</c>, <c>SM_NAV_TB_Sprint_to_Idle</c>, <c>SM_NAV_LC_Sprint_to_Idle</c>) with the blend that enters it, the
/// branch window that lets the graph leave it and its extracted root motion along the facing.
/// </summary>
/// <param name="BlendIn">The graph's blend into this state, seconds (the sprint loop into the stop: '8 Frame Blend' 0.267 s; the entry into the start: 'Default 3 Frame Blend' 0.1 s).</param>
/// <param name="BranchAt">Seconds into the clip from which the graph may leave it (<c>BRANCH_Sprint_Finished</c> for a stop, <c>BRANCH_Idle2SprintLoop</c> for a start).</param>
/// <param name="BlendOut">The blend out of it at that branch, seconds (the start into the loop: 0.1 s), or the navigation's (<see cref="SprintTransitions.DefaultBlendOut"/>) when the graph leaves through the base graph.</param>
/// <param name="RootForward">Extracted motion along the facing, metres, evenly spaced over <paramref name="RootDuration"/>.</param>
public sealed record SprintEdgeClip(string Graph, string State, string Clip, float Length, float BlendIn, float BranchAt, float BlendOut, IReadOnlyList<float> RootForward, float RootDuration)
{
    /// <summary>Metres along the facing at <paramref name="time"/> s.</summary>
    public float DistanceAt(float time) => StartClip.Sample(RootForward, RootDuration, time);

    /// <summary>The time at which the clip's root has gone <paramref name="distance"/> m.</summary>
    public float TimeAtDistance(float distance) => StartClip.Invert(RootForward, RootDuration, distance);

    /// <summary>The root's speed along the facing at <paramref name="time"/> s (m/s, from the samples' differences).</summary>
    public float SpeedAt(float time)
    {
        if (RootForward.Count < 2 || RootDuration <= 0f) return 0f;
        float step = RootDuration / (RootForward.Count - 1);
        int i = Math.Clamp((int)(time / step), 0, RootForward.Count - 2);
        return (RootForward[i + 1] - RootForward[i]) / step;
    }

    /// <summary>The root's top speed and when it is reached (s).</summary>
    public (float Speed, float At) Peak
    {
        get
        {
            float best = 0f, at = 0f;
            if (RootForward.Count < 2 || RootDuration <= 0f) return (0f, 0f);
            float step = RootDuration / (RootForward.Count - 1);
            for (int i = 0; i + 1 < RootForward.Count; i++)
            {
                float v = (RootForward[i + 1] - RootForward[i]) / step;
                if (v > best) { best = v; at = (i + 0.5f) * step; }
            }
            return (best, at);
        }
    }

    /// <summary>
    /// For a slowing clip: the time after its peak at which its root first slows to <paramref name="speed"/> (m/s); the peak's time for a speed at or above the peak, the time the root
    /// comes to rest (its last move above 0.05 m/s) for a speed at or below that. A clip posed at this time moves its feet at the body's speed (the stop pose follows the body).
    /// </summary>
    public float TimeAtSlowingSpeed(float speed)
    {
        if (RootForward.Count < 2 || RootDuration <= 0f) return 0f;
        float step = RootDuration / (RootForward.Count - 1);
        var (peak, peakAt) = Peak;
        if (speed >= peak) return peakAt;
        int from = Math.Clamp((int)(peakAt / step), 0, RootForward.Count - 2);
        float previous = peak, previousAt = peakAt;
        for (int i = from; i + 1 < RootForward.Count; i++)
        {
            float v = (RootForward[i + 1] - RootForward[i]) / step, at = (i + 0.5f) * step;
            if (v <= speed)
            {
                float span = previous - v;
                return span > 1e-4f ? previousAt + (at - previousAt) * (previous - speed) / span : at;
            }
            previous = v; previousAt = at;
        }
        return RestAt;
    }

    /// <summary>The time the clip's root comes to rest: the end of its last sample step faster than 0.05 m/s.</summary>
    public float RestAt
    {
        get
        {
            if (RootForward.Count < 2 || RootDuration <= 0f) return 0f;
            float step = RootDuration / (RootForward.Count - 1);
            for (int i = RootForward.Count - 2; i >= 0; i--)
                if ((RootForward[i + 1] - RootForward[i]) / step > 0.05f) return (i + 1) * step;
            return 0f;
        }
    }
}

/// <summary>A sprint family's start and stop clips.</summary>
/// <param name="OneHanded">The pistol's loop in the rifle graph ("1HM Sprint Loop": the pistols and the plasma pistol), with its own stop clip.</param>
/// <param name="Start">The clip the graph plays on entering the sprint from standing, or null when it plays the loop's own forward clip (the heavy bolter and the cannons).</param>
/// <param name="RunOnBlend">The base graph's blend out of the sprint into the navigation when the sprint is let go with the stick held (<c>FLT_Input_Nav_Speed</c> in [0.5, 100] and
/// <c>!BOOL_SprintIsActive</c>): 'Standard Blend' 0.333 s for the bolter's and the plasma cannon's states, 'Default 3 Frame Transition' 0.1 s for the bolt pistol's and the heavy bolter's.</param>
/// <param name="StopExitBlend">The base graph's blend from the sprint state into the navigation at the stop's <c>BRANCH_Sprint_Finished</c> (the same values).</param>
public sealed record SprintEdges(SprintFamily Family, bool OneHanded, SprintEdgeClip? Start, SprintEdgeClip? Stop, float RunOnBlend, float StopExitBlend);

/// <summary>
/// The sprint's connecting clips from the sprint graphs (animation pass 3; <c>smtool graph-dump --sprint-edges</c>), and the pistol's relaxed idle. Read-only.
/// <list type="bullet">
/// <item><b>Stop</b>: "Sprint Loop" → "Sprint to Idle" when <c>!BOOL_SprintIsActive</c> and <c>FLT_Input_Nav_Speed_DAMPED</c> in [-10, 0.5] (the sprint let go with the stick at rest)
/// over '8 Frame Blend' 0.267 s; the stop's <c>BRANCH_Sprint_Finished</c> lets the base graph leave (rifle 0.625 of 1.333 s, pistol 0.5435 of 1.533 s).</item>
/// <item><b>Start</b>: the entry state "MP Sprint - ENTRY" → "Transition To Idle" (<c>SM_NAV_Bullrush_Start_F_01</c>, 0.933 s) → the loop at <c>BRANCH_Idle2SprintLoop</c> (its end) over 0.1 s; the
/// other entry, "180 Idle to Sprint" (<c>SM_TRN_BO/CS/HB_180Turn_to_Sprint</c>), on <c>FLT_Aim_Horizontal_Deg</c>. The heavy graphs' "Transition To Idle" plays the loop's own forward clip.</item>
/// <item><b>Pistol relaxed idle</b>: <c>SM_Pistol_NAV</c> "Idle Relaxed" (the pistol in hand, not ready): <c>SM_NAV_IdleL_CS_01</c>; the slope blend beside it is not used.</item>
/// </list>
/// </summary>
public sealed record SprintTransitions(IReadOnlyList<SprintEdges> Sets, string? PistolRelaxedIdle, IReadOnlyList<string> Notes)
{
    /// <summary>The blend out of a stop when the base graph takes over (its "Nav to Nav" / idle blends are not in the sprint graph; ours: the navigation graphs' 'Nav to Nav' 0.333 s is the idle's).</summary>
    public const float DefaultBlendOut = 0.333f;

    /// <param name="BaseSprint">The base graph's sprint state of the weapon (the one referencing the sprint graph) and <paramref name="BaseNav"/> its navigation state, for the blends out.</param>
    public sealed record Map(SprintFamily Family, bool OneHanded, string Graph, string Loop, string StartState, string StopState, string BaseSprint, string BaseNav);

    public static readonly Map[] DefaultMaps =
    [
        new(SprintFamily.Rifle, false, "MP_SM_Bolter_NuSprint", "Sprint Loop", "Transition To Idle", "Sprint to Idle", "Bolter Sprint", "Bolter Navigation"),
        new(SprintFamily.Rifle, true, "MP_SM_Bolter_NuSprint", "1HM Sprint Loop", "Transition To Idle", "1HM Sprint to Idle", "BoltPistol Sprint", "BoltPistol Navigation"),
        new(SprintFamily.Heavy, false, "SM_Heavy_Bolter_NuSprint", "Sprint Loop", "Transition To Idle", "Sprint to Idle", "Heavy Bolter Sprint", "Heavy Bolter Nav"),
        new(SprintFamily.Cannon, false, "SM_Lascannon_Plasma_NuSprint", "Sprint Loop", "Transition To Idle", "Sprint to Idle", "PlasmaCannon Sprint", "PlasmaCannon Navigation"),
    ];

    /// <summary>The graph whose states <see cref="Map.BaseSprint"/> and <see cref="Map.BaseNav"/> are in.</summary>
    public const string BaseGraph = "SM_Base_MP";

    /// <summary>
    /// The edges of a family and hand: the exact set, else the family's first. Looked up in a table built once (qc/reference-pass, review S-1: the client asks for every marine every frame;
    /// the search with a closure allocated each time).
    /// </summary>
    public SprintEdges? For(SprintFamily family, bool oneHanded)
    {
        // Built on first use and again if Sets is replaced (a `with` copy keeps the fields).
        if (!ReferenceEquals(_byKeyOf, Sets)) { _byKey = Table(Sets); _byKeyOf = Sets; }
        int i = (int)family * 2 + (oneHanded ? 1 : 0);
        return (uint)i < (uint)_byKey!.Length ? _byKey[i] : Find(Sets, family, oneHanded);
    }

    SprintEdges?[]? _byKey;
    IReadOnlyList<SprintEdges>? _byKeyOf;

    /// <summary>Equality over the data only (Sets, PistolRelaxedIdle, Notes; review: the lookup cache is not part of it).</summary>
    public bool Equals(SprintTransitions? other) =>
        other is not null && (ReferenceEquals(this, other) || (EqualityComparer<IReadOnlyList<SprintEdges>>.Default.Equals(Sets, other.Sets)
            && PistolRelaxedIdle == other.PistolRelaxedIdle && EqualityComparer<IReadOnlyList<string>>.Default.Equals(Notes, other.Notes)));

    public override int GetHashCode() => HashCode.Combine(Sets, PistolRelaxedIdle, Notes);

    static SprintEdges?[] Table(IReadOnlyList<SprintEdges> sets)
    {
        var families = Enum.GetValues<SprintFamily>();
        var table = new SprintEdges?[(families.Max(f => (int)f) + 1) * 2];
        foreach (var f in families) { table[(int)f * 2] = Find(sets, f, false); table[(int)f * 2 + 1] = Find(sets, f, true); }
        return table;
    }

    static SprintEdges? Find(IReadOnlyList<SprintEdges> sets, SprintFamily family, bool oneHanded) =>
        sets.FirstOrDefault(s => s.Family == family && s.OneHanded == oneHanded) ?? sets.FirstOrDefault(s => s.Family == family);

    public static SprintTransitions Load(string gameDir)
    {
        var character = RigProfiles.Marine.LoadCharacter(gameDir);
        return From(HavokBehaviorDetailReader.Read(character.Pack), clip => character.LoadAnimation(clip).Animation.Duration,
            clip => { try { return character.LoadExtractedMotion(clip); } catch (Exception ex) when (ex is KeyNotFoundException or NotSupportedException or InvalidDataException) { return null; } });
    }

    /// <summary>The data from already-read graphs (testable without an install).</summary>
    public static SprintTransitions From(IReadOnlyList<BehaviorGraphDetail> graphs, Func<string, float> animationLength, Func<string, HavokExtractedMotion?> motion, IEnumerable<Map>? maps = null)
    {
        BehaviorGraphDetail? Graph(string name) => graphs.FirstOrDefault(g => g.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var notes = new List<string>();
        var sets = new List<SprintEdges>();
        foreach (var map in maps ?? DefaultMaps)
        {
            if (Graph(map.Graph) is not { } g) { notes.Add($"graph {map.Graph} not found"); continue; }
            var loop = g.AllStates.FirstOrDefault(x => x.State.Name == map.Loop);
            if (loop.State is null) { notes.Add($"{map.Graph}: no state '{map.Loop}'"); continue; }
            var loopClips = SequencerUnder(g, loop.State.Generator)?.Animations ?? (IReadOnlyList<string>)Array.Empty<string>();

            SprintEdgeClip? stop = null;
            var stopState = loop.Machine.States.FirstOrDefault(s => s.Name == map.StopState);
            if (stopState is null) notes.Add($"{map.Graph}: no state '{map.StopState}'");
            else
            {
                var into = loop.State.Transitions.FirstOrDefault(t => t.ToStateId == stopState.Id);
                float blendIn = into is null ? 0.267f : g.EffectAt(into.Transition)?.Duration ?? 0f;
                stop = Edge(g, map, stopState, "BRANCH_Sprint_Finished", blendIn, DefaultBlendOut, animationLength, motion, notes);
            }

            SprintEdgeClip? start = null;
            var startState = loop.Machine.States.FirstOrDefault(s => s.Name == map.StartState);
            if (startState is not null && SequencerUnder(g, startState.Generator) is { Animations.Count: > 0 } ss && !loopClips.Contains(ss.Animations[0], StringComparer.OrdinalIgnoreCase))
            {
                // The entry state's transition into the start, and the start's into this loop at its branch.
                var entry = loop.Machine.States.FirstOrDefault(s => s.Transitions.Any(t => t.ToStateId == startState.Id) && s.Id != loop.State.Id);
                var enterT = entry?.Transitions.FirstOrDefault(t => t.ToStateId == startState.Id);
                var toLoop = startState.Transitions.FirstOrDefault(t => t.ToStateId == loop.State.Id);
                float blendIn = enterT is null ? 0.1f : g.EffectAt(enterT.Transition)?.Duration ?? 0f;
                float blendOut = toLoop is null ? 0.1f : g.EffectAt(toLoop.Transition)?.Duration ?? 0f;
                start = Edge(g, map, startState, "BRANCH_Idle2SprintLoop", blendIn, blendOut, animationLength, motion, notes);
            }
            // The base graph's ways out of the sprint state: with the stick held (the run on) and at the stop's branch.
            float runOn = DefaultBlendOut, stopExit = DefaultBlendOut;
            if (Graph(BaseGraph) is { } baseGraph && baseGraph.AllStates.FirstOrDefault(x => x.State.Name == map.BaseSprint) is { State: not null } bs
                && bs.Machine.States.FirstOrDefault(x => x.Name == map.BaseNav) is { } nav)
            {
                foreach (var t in bs.State.Transitions.Where(t => t.ToStateId == nav.Id))
                {
                    if (baseGraph.EffectAt(t.Transition) is not { } effect) continue;
                    if (t.BranchEvent == "BRANCH_Sprint_Finished") stopExit = effect.Duration;
                    else if (t.ConditionText is { } c && c.Contains("FLT_Input_Nav_Speed in", StringComparison.Ordinal) && c.Contains("!BOOL_SprintIsActive", StringComparison.Ordinal)) runOn = effect.Duration;
                }
            }
            else notes.Add($"{BaseGraph}: no states '{map.BaseSprint}' / '{map.BaseNav}'; the ways out of the sprint blend over {DefaultBlendOut} s");
            sets.Add(new SprintEdges(map.Family, map.OneHanded, start, stop, runOn, stopExit));
        }

        string? pistolIdle = null;
        if (Graph("SM_Pistol_NAV") is { } pistol && pistol.AllStates.FirstOrDefault(x => x.State.Name == "Idle Relaxed").State is { } relaxed)
            pistolIdle = HipFireAnimations.FirstClip(pistol, relaxed.Generator)?.Animation;
        if (pistolIdle is null) notes.Add("SM_Pistol_NAV: no 'Idle Relaxed' clip");
        return new SprintTransitions(sets, pistolIdle, notes);
    }

    static SprintEdgeClip? Edge(BehaviorGraphDetail g, Map map, BehaviorState state, string branch, float blendIn, float blendOut,
        Func<string, float> animationLength, Func<string, HavokExtractedMotion?> motion, List<string> notes)
    {
        var seq = SequencerUnder(g, state.Generator);
        if (seq is null || seq.Animations.Count == 0) { notes.Add($"{map.Graph}/{state.Name}: no clip"); return null; }
        string clip = seq.Animations[0];
        float speed = seq.PlaybackSpeeds.Count > 0 && seq.PlaybackSpeeds[0] > 0f ? seq.PlaybackSpeeds[0] : 1f;
        float length;
        try { length = animationLength(clip) / speed; }
        catch (Exception ex) when (ex is KeyNotFoundException or NotSupportedException or InvalidDataException) { notes.Add($"{map.Graph}/{state.Name}: {clip}: {ex.Message}"); return null; }
        float at = seq.Tags.Where(t => t.Kind == "Branch" && t.Name == branch).Select(t => t.Start).DefaultIfEmpty(1f).Min() * length;
        var m = motion(clip);
        IReadOnlyList<float> forward = m is { Samples.Count: >= 2 } ? m.Samples.Select(s => s.Y).ToArray() : Array.Empty<float>();
        if (forward.Count == 0) notes.Add($"{map.Graph}/{state.Name}: {clip} has no extracted motion");
        // The root motion's samples span the clip at speed 1; its time line is the played one (the length and the branch are divided by the playback speed too). Every start and stop clip of
        // the install plays at speed 1 (the heavy and cannon stops bind it to FLT_Sprint_Speed at runtime, not read).
        return new SprintEdgeClip(map.Graph, state.Name, clip, length, blendIn, at, blendOut, forward, (m?.Duration ?? 0f) / speed);
    }

    static BehaviorSequencer? SequencerUnder(BehaviorGraphDetail g, int? node)
    {
        var seen = new HashSet<int>();
        var stack = new Stack<int>();
        if (node is int s0) stack.Push(s0);
        while (stack.Count > 0)
        {
            int at = stack.Pop();
            if (!seen.Add(at)) continue;
            if (g.Sequencers.TryGetValue(at, out var seq)) return seq;
            if (!g.Nodes.TryGetValue(at, out var n)) continue;
            for (int i = n.Children.Count - 1; i >= 0; i--) if (n.Children[i].Target is int c) stack.Push(c);
        }
        return null;
    }
}
