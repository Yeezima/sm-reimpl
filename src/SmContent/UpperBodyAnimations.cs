using SmFormats.Havok;

namespace SmContent;

// Animation pass 2: the weapon switch, the hit flinches, the death clips before the ragdoll and the jump pack's get-up and flight noise, read from the marine rig's behaviour graphs
// (SM_Base_MP and MP_SM_NAV_JP_Sim; `smtool graph-dump --upper`). Read-only; engine-free; every reader is tested on synthetic graphs.

/// <summary>Small graph walks shared by the pass-2 readers.</summary>
static class GraphWalk
{
    public static IEnumerable<int> Walk(BehaviorGraphDetail g, int? node, int maxDepth = 16)
    {
        var seen = new HashSet<int>();
        var stack = new Stack<(int At, int Depth)>();
        if (node is int start) stack.Push((start, 0));
        while (stack.Count > 0)
        {
            var (at, depth) = stack.Pop();
            if (!g.Nodes.TryGetValue(at, out var n) || !seen.Add(at)) continue;
            yield return at;
            if (depth >= maxDepth) continue;
            for (int i = n.Children.Count - 1; i >= 0; i--)
                if (n.Children[i].Target is int c) stack.Push((c, depth + 1));
        }
    }

    public static BehaviorSequencer? SequencerUnder(BehaviorGraphDetail g, int? node)
    {
        foreach (var at in Walk(g, node))
            if (g.Sequencers.TryGetValue(at, out var s) && s.Animations.Count > 0) return s;
        return null;
    }

    public static float Speed(BehaviorSequencer seq) => seq.PlaybackSpeeds.Count > 0 && seq.PlaybackSpeeds[0] > 0f ? seq.PlaybackSpeeds[0] : 1f;

    /// <summary>The first tag of a kind (and, when given, a name containing <paramref name="nameContains"/>), or null.</summary>
    public static BehaviorTag? Tag(BehaviorSequencer seq, string kind, string? nameContains = null, float minStart = -1f) =>
        seq.Tags.Where(t => t.Kind == kind && (nameContains is null || t.Name?.Contains(nameContains, StringComparison.OrdinalIgnoreCase) == true) && t.Start > minStart)
                .OrderBy(t => t.Start).Cast<BehaviorTag?>().FirstOrDefault();
}

// ---------------------------------------------------------------------------------------------------------------------------------------------------------
// 1. Weapon switch

/// <summary>
/// One weapon-switch clip of the base graph: it plays on the whole body except the 14 leg bones (<paramref name="Mask"/>: the swap child's bone mask, whose 14 entries are all zero and
/// whose later bones read as 1) while the navigation graph keeps the legs. <paramref name="SwapAt"/>: seconds into the clip at which the new weapon is in the hand (the sequencer's
/// <c>Weapons - Ranged Weapon</c> / <c>Weapons - Melee and Ranged</c> modifier tag starts); <paramref name="BranchIn"/>: the <c>BranchIn</c> tag (seconds; where the next action may blend in).
/// </summary>
/// <param name="Length">Seconds of playback (the animation's duration over <paramref name="Speed"/>); <paramref name="SwapAt"/> and <paramref name="BranchIn"/> are in the same seconds.</param>
/// <param name="Speed">The sequencer's playback speed (fix/stow-right review: 1 for both swap clips on the install, printed at the client's start; a sample time in the animation is playback seconds x speed).</param>
public sealed record SwitchClip(string Clip, float Length, IReadOnlyDictionary<string, float> Mask, float SwapAt, float BranchIn, float Speed = 1f)
{
    /// <summary>The animation's own time at <paramref name="playbackSeconds"/> into the clip.</summary>
    public float AnimationTime(float playbackSeconds) => playbackSeconds * (Speed > 0f && float.IsFinite(Speed) ? Speed : 1f);
}

/// <summary>
/// The weapon switches of <c>SM_Base_MP</c>: <see cref="ToRifle"/> (state "Bolter Weapon Switch" and the other rifle-class behaviours': <c>SM_Swap_Rifle_to_Rifle_01</c>, 0.467 s, the model swaps at
/// 0.5 of it) and <see cref="ToPistol"/> (the bolt pistol behaviour's "BoltPistol Weapon Switch": <c>SM_Swap_Rifle_to_Melee_01</c>, 0.667 s, the swap at 0.6). Which of them a multiplayer switch plays
/// (the pistol's for a switch to a pistol, the rifle's for any other) is an interpretation: the graph runs the new weapon's behaviour state.
/// </summary>
public sealed record SwitchAnimations(SwitchClip? ToRifle, SwitchClip? ToPistol, IReadOnlyList<string> Notes)
{
    public const string Graph = "SM_Base_MP";
    public const string RifleState = "Bolter Weapon Switch", PistolState = "BoltPistol Weapon Switch";

    public static SwitchAnimations From(IReadOnlyList<BehaviorGraphDetail> graphs, IReadOnlyList<string> boneNames, Func<string, float> animationLength)
    {
        var notes = new List<string>();
        if (graphs.FirstOrDefault(x => x.Name.Equals(Graph, StringComparison.OrdinalIgnoreCase)) is not { } g) { notes.Add($"graph {Graph} not found"); return new(null, null, notes); }
        return new(Read(g, RifleState, boneNames, animationLength, notes), Read(g, PistolState, boneNames, animationLength, notes), notes);
    }

    static SwitchClip? Read(BehaviorGraphDetail g, string stateName, IReadOnlyList<string> boneNames, Func<string, float> length, List<string> notes)
    {
        var state = g.AllStates.FirstOrDefault(x => x.State.Name == stateName).State;
        if (state is null) { notes.Add($"{Graph}: no state '{stateName}'"); return null; }
        foreach (var at in GraphWalk.Walk(g, state.Generator))
        {
            if (g.BlenderAt(at) is not { } blend) continue;
            foreach (var child in blend.Children)
            {
                if (g.BoneWeightsAt(child.BoneWeights) is not { } weights || GraphWalk.SequencerUnder(g, child.Generator) is not { } seq) continue;
                string clip = seq.Animations[0];
                float len = length(clip) / GraphWalk.Speed(seq);
                var mask = new Dictionary<string, float>(StringComparer.Ordinal);
                for (int i = 0; i < boneNames.Count; i++) mask[boneNames[i]] = weights.WeightOf(i);
                // The tag that puts the new weapon in the hand: "Weapons - Ranged Weapon" (0.5 to 1 of the rifle swap) or "Weapons - Melee and Ranged" (0.6 to 1 of the stow clip), the first modifier tag
                // naming the ranged weapon that does not start at 0.
                float swap = GraphWalk.Tag(seq, "Modifier", "Ranged", 0.01f) is { } tag ? tag.Start : 0.5f;
                float branchIn = GraphWalk.Tag(seq, "BranchIn") is { } b ? b.Start : 1f;
                return new SwitchClip(clip, len, mask, Math.Clamp(swap, 0f, 1f) * len, Math.Clamp(branchIn, 0f, 1f) * len, GraphWalk.Speed(seq));
            }
        }
        notes.Add($"{Graph}/{stateName}: no masked swap clip");
        return null;
    }
}

/// <summary>
/// One piece of a <see cref="SwitchPlan"/>: <paramref name="Clip"/> from <paramref name="From"/> to <paramref name="To"/> seconds of the animation's own time (<see cref="SwitchClip.AnimationTime"/>: the
/// sequencer's speed applied; <paramref name="From"/> &gt; <paramref name="To"/>: played backwards) over the fractions <paramref name="Start"/> to <paramref name="End"/> of the switch time.
/// </summary>
public sealed record SwitchSegment(string Clip, float From, float To, float Start, float End, IReadOnlyDictionary<string, float> Mask)
{
    /// <summary>The clip time at a fraction of the switch inside (clamped to) this segment.</summary>
    public float ClipTime(float fraction) => End > Start ? From + (To - From) * Math.Clamp((fraction - Start) / (End - Start), 0f, 1f) : To;
}

/// <summary>
/// How the client draws one weapon switch (fix/stow-right, the owner's playtest: "tactical marine when stowing their primary weapon to switch to bolt pistol should stow it to the right
/// around their back not the left. currently the gun floats through the air"): the clip pieces over the switch time and when the old weapon leaves the hand for its stow marker
/// (<see cref="Release"/>) and the new one comes into the hand (<see cref="Take"/>), both fractions of the switch time; between them both are stowed.
/// <para>
/// Why: the pistol switch's own clip (<c>SM_Swap_Rifle_to_Melee_01</c>) carries the rifle's chest prop round the marine's <b>left</b> side to the back marker on its own, 0.4 to 2 m from either
/// hand prop from 0.2 s on (measured, <c>--switch-test --switch-probe --switch-clips</c>): the gun flies by itself. The rifle draw (<c>SM_Swap_Rifle_to_Rifle_01</c>) has the hand take the rifle off the
/// back marker at its <c>Weapons - Ranged Weapon</c> tag (0.5 of the clip: the chest prop 4 cm from <c>MKR_Stow_Bolter</c>, 11 to 14 cm from the right-hand prop from there on) and carry it round
/// the <b>right</b> side to the front. So a weapon is put away by its own draw played backwards from the clip's end down to that attach tag, where it is released onto its marker (owner's
/// knowledge: the right side; the detach moment: the draw's tag), then the new weapon's draw plays from <see cref="DrawLead"/> before its tag to its end and the weapon comes into the hand at
/// the tag. Played backwards, the pistol's draw puts the pistol back on its thigh marker the same way. Each piece keeps its clip's own speed share: the two pieces are stretched together over
/// the switch time (ours).
/// </para>
/// </summary>
public sealed record SwitchPlan(IReadOnlyList<SwitchSegment> Segments, float Release, float Take)
{
    /// <summary>
    /// Seconds of the new weapon's draw clip played before its attach tag: the rifle draw's own reach to the back (its tag, 0.233 s); the pistol's draw starts as far before its tag (ours).
    /// Null takes the rifle draw's tag.
    /// </summary>
    public static float DrawLead(SwitchAnimations switches) => switches.ToRifle?.SwapAt ?? 0.233f;

    /// <summary>The whole clip over the switch with the weapon swapped at its tag (the behaviour before fix/stow-right; also any switch the rules below do not cover).</summary>
    public static SwitchPlan Single(SwitchClip clip)
    {
        float swap = clip.Length > 0f ? Math.Clamp(clip.SwapAt / clip.Length, 0.05f, 0.95f) : 0.5f;
        return new([new SwitchSegment(clip.Clip, 0f, clip.AnimationTime(clip.Length), 0f, 1f, clip.Mask)], swap, swap);
    }

    /// <summary>
    /// The plan for a switch from the slot <paramref name="from"/> to <paramref name="to"/>: the old weapon's draw backwards, then the new weapon's draw (see the type). Null when nothing is
    /// switched or neither clip is known; <see cref="Single"/> of the known clip when the other is missing; <paramref name="oldRule"/> gives the pre-fix plan (the new weapon's clip alone).
    /// </summary>
    public static SwitchPlan? For(SwitchAnimations switches, SmSim.WeaponSlot from, SmSim.WeaponSlot to, bool oldRule = false)
    {
        if (from == to) return null;
        var draw = to == SmSim.WeaponSlot.Pistol ? switches.ToPistol : switches.ToRifle;
        var stow = from == SmSim.WeaponSlot.Pistol ? switches.ToPistol : switches.ToRifle;
        if (oldRule || draw is null || stow is null)
        {
            var only = (to == SmSim.WeaponSlot.Pistol ? switches.ToPistol ?? switches.ToRifle : switches.ToRifle ?? switches.ToPistol);
            return only is null ? null : Single(only);
        }
        float stowSeconds = Math.Max(stow.Length - stow.SwapAt, 0f);
        float drawFrom = Math.Clamp(draw.SwapAt - DrawLead(switches), 0f, draw.SwapAt);
        float drawSeconds = Math.Max(draw.Length - drawFrom, 0f);
        float total = stowSeconds + drawSeconds;
        if (total <= 1e-4f) return Single(draw);
        float split = stowSeconds / total;
        var segments = new List<SwitchSegment>(2);
        // The shares are in playback seconds; the pieces sample the animation's own time.
        if (stowSeconds > 0f) segments.Add(new SwitchSegment(stow.Clip, stow.AnimationTime(stow.Length), stow.AnimationTime(stow.SwapAt), 0f, split, stow.Mask));
        if (drawSeconds > 0f) segments.Add(new SwitchSegment(draw.Clip, draw.AnimationTime(drawFrom), draw.AnimationTime(draw.Length), split, 1f, draw.Mask));
        float take = split + (draw.SwapAt - drawFrom) / total;
        return new(segments, split, Math.Clamp(take, split, 1f));
    }

    /// <summary>The segment shown at a fraction of the switch (the last one at or after its end; the first before 0).</summary>
    public SwitchSegment SegmentAt(float fraction)
    {
        for (int i = 0; i < Segments.Count; i++) if (fraction < Segments[i].End || i == Segments.Count - 1) return Segments[i];
        return Segments[^1];
    }

    /// <summary>
    /// The segment before the one at <paramref name="fraction"/> and how far the cross-fade into the current one has gone (0..1), when <paramref name="fraction"/> lies within
    /// <paramref name="blend"/> (a fraction of the switch) after a segment boundary; null otherwise (the joint between the two draws is a different pose: ours, a short cross-fade).
    /// </summary>
    public (SwitchSegment Previous, float Progress)? Crossfade(float fraction, float blend)
    {
        if (blend <= 0f) return null;
        for (int i = 1; i < Segments.Count; i++)
        {
            float since = fraction - Segments[i].Start;
            if (since >= 0f && since < blend && fraction < Segments[i].End) return (Segments[i - 1], since / blend);
        }
        return null;
    }
}

// ---------------------------------------------------------------------------------------------------------------------------------------------------------
// 2. Hit flinch

/// <summary>
/// A flinch clip (<paramref name="Additive"/>: its binding's blend hint; the flinches are additive offsets laid over the pose). <paramref name="DeathBranches"/> (hit reactions, 2026-11-11): the
/// death branches its sequencer raises over the whole clip (<c>BRANCH_Headshot</c>, <c>BRANCH_Death_Ranged_Left</c> / <c>_Right</c> / <c>_Back</c>, <c>BRANCH_Las_Death</c>,
/// <c>BRANCH_Plasma_Death</c>, <c>BRANCH_Melta_Death</c>): the graph's DEATH state takes the transition of that branch when the hit that started this flinch kills (<see cref="DeathRules"/>); null: none.
/// </summary>
public sealed record FlinchClip(string Clip, float Length, bool Additive = true, IReadOnlyList<string>? DeathBranches = null);

/// <summary>One of the graph's random flinches with its weight (<c>hkbWeightedRandomSelector</c> "Random Flinches").</summary>
public sealed record WeightedFlinch(FlinchClip Clip, float Weight);

/// <summary>
/// The flinch states of one weapon's set in <c>SM_Base_MP</c> ("Upper Body Flinches" > "Ranged Flinch Behavior" > the weapon's machine). Every set has the same twelve slots by state id; the
/// set machines' start-state chooser (<c>hkbReactionStateChooser</c>, read 2026-11-11) names them: 0 head front left, 1 head front right, 2 head back left, 3 head back right (the chooser's head entries:
/// <c>front_high_left</c> ... <c>back_high_right</c> with its head flag), 4 front high left, 5 front high right, 6 front low left, 7 front low right, 8 back high left, 9 back high right, 10 back low left,
/// 11 back low right (<see cref="Slots"/> index = id; a slot the weapon's set lacks is null).
/// </summary>
public sealed record FlinchSet(string Weapon, IReadOnlyList<FlinchClip?> Slots)
{
    public const int SlotCount = 12;
    public FlinchClip? Slot(int id) => id >= 0 && id < Slots.Count ? Slots[id] : null;
}

/// <summary>
/// The graph's flinch sets by weapon and the blueprint's <c>min_hit_reaction_time</c> (<see cref="MinHitReactionTime"/>, seconds: the least time between two flinches of one player).
/// <see cref="Ports"/> (hit reactions, 2026-11-11): which set a hit's reaction type plays. The graph's event list holds thirteen ports, <c>PORT_rng_bolter</c>, <c>PORT_reaction_ranged_light</c>,
/// <c>PORT_rng_bolt_pistol</c>, <c>PORT_rng_sniperrifle</c>, <c>PORT_rng_lascannon</c>, <c>PORT_rng_plasma_gun_weak</c> / <c>_strong</c>, <c>PORT_rng_plasma_pistol_weak</c> / <c>_strong</c>,
/// <c>PORT_rng_heavybolter</c>, <c>PORT_rng_plasma_cannon_strong</c> / <c>_weak</c>, <c>PORT_rng_melta</c>, in exactly the order of the machine's thirteen set states by id (Bolter 1 ... Melta 13): the
/// k-th port is read as the k-th set (interpretation of the order; the transitions out of the machine's entry carry no decoded trigger). So the set is the one of the <b>attacker's</b> weapon's reaction
/// type (the weapon files' <c>trigger_reaction</c>, <see cref="WeaponReactions"/>), not the victim's weapon. <see cref="Random"/>: the "Random Flinches" state of "Upper Body Flinch Behavior"
/// (entered on the event <c>Hit_Flinch</c>): six additive 0.2 s clips at equal weight.
/// </summary>
public sealed record FlinchAnimations(IReadOnlyDictionary<string, FlinchSet> Sets, float MinHitReactionTime, IReadOnlyList<string> Notes)
{
    public const string Machine = "Ranged Flinch Behavior";
    public const string UpperMachine = "Upper Body Flinch Behavior", RandomState = "Random Flinches";
    /// <summary>Used when the blueprint has none (the install's value is 1).</summary>
    public const float DefaultMinHitReactionTime = 1f;

    /// <summary>Reaction type (the port's name without <c>PORT_</c>, e.g. <c>rng_bolter</c>) to the set state's name.</summary>
    public IReadOnlyDictionary<string, string> Ports { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
    /// <summary>The random flinches (<see cref="RandomState"/>), with their weights.</summary>
    public IReadOnlyList<WeightedFlinch> Random { get; init; } = [];

    public FlinchSet? For(string weaponState) => Sets.GetValueOrDefault(weaponState);

    /// <summary>The set a reaction type plays (<see cref="Ports"/>), or null for a type without a port (the storm bolter's <c>rng_stormbolter</c>, the grenade launcher's <c>rng_autocannon</c>).</summary>
    public string? SetOfReaction(string? reactionType) => reactionType is not null && Ports.TryGetValue(reactionType, out var set) ? set : null;

    /// <summary>A flinch clip from the sequencer that plays it: its first clip, length over the sequencer's speed, blend hint and the death branches it raises.</summary>
    internal static FlinchClip ClipOf(BehaviorSequencer seq, Func<string, float> animationLength, Func<string, bool>? isAdditive)
    {
        string clip = seq.Animations[0];
        var branches = seq.Tags.Where(t => t.Kind == "Branch" && t.Name is { Length: > 0 } n && n != "BRANCH_Reaction_End").Select(t => t.Name!).Distinct(StringComparer.Ordinal).ToArray();
        return new FlinchClip(clip, animationLength(clip) / GraphWalk.Speed(seq), isAdditive?.Invoke(clip) ?? true, branches.Length > 0 ? branches : null);
    }

    public static FlinchAnimations From(IReadOnlyList<BehaviorGraphDetail> graphs, Func<string, float> animationLength, float? minHitReactionTime = null, Func<string, bool>? isAdditive = null)
    {
        var notes = new List<string>();
        var sets = new Dictionary<string, FlinchSet>(StringComparer.Ordinal);
        float min = minHitReactionTime is { } m && float.IsFinite(m) && m >= 0f ? m : DefaultMinHitReactionTime;
        if (graphs.FirstOrDefault(x => x.Name.Equals(SwitchAnimations.Graph, StringComparison.OrdinalIgnoreCase)) is not { } g) { notes.Add($"graph {SwitchAnimations.Graph} not found"); return new(sets, min, notes); }
        var machine = g.StateMachines.FirstOrDefault(x => x.Name == Machine);
        if (machine is null) { notes.Add($"{SwitchAnimations.Graph}: no machine '{Machine}'"); return new(sets, min, notes) { Random = ReadRandom(g, animationLength, isAdditive, notes) }; }
        var setStates = new List<BehaviorState>();
        foreach (var weapon in machine.States.OrderBy(s => s.Id))
        {
            BehaviorStateMachine? inner = null;
            foreach (var at in GraphWalk.Walk(g, weapon.Generator, 4)) if (g.MachineAt(at) is { } m2) { inner = m2; break; }
            if (inner is null) continue;
            setStates.Add(weapon);
            var slots = new FlinchClip?[FlinchSet.SlotCount];
            foreach (var s in inner.States)
            {
                if (s.Id < 0 || s.Id >= slots.Length) continue;
                if (GraphWalk.SequencerUnder(g, s.Generator) is { } seq) { slots[s.Id] = ClipOf(seq, animationLength, isAdditive); continue; }
                if (HipFireAnimations.FirstClip(g, s.Generator) is { } c)
                    slots[s.Id] = new FlinchClip(c.Animation, animationLength(c.Animation) / (c.PlaybackSpeed > 0f ? c.PlaybackSpeed : 1f), isAdditive?.Invoke(c.Animation) ?? true);
            }
            if (slots.Any(x => x is not null)) sets[weapon.Name] = new FlinchSet(weapon.Name, slots);
        }
        if (sets.Count == 0) notes.Add($"{SwitchAnimations.Graph}/{Machine}: no flinch sets");
        // The ports, in the event list's order, pair with the set states by id (see the type).
        var ports = g.Events.Where(e => e.StartsWith("PORT_rng_", StringComparison.Ordinal) || e.StartsWith("PORT_reaction_", StringComparison.Ordinal)).ToList();
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (ports.Count > 0 && ports.Count == setStates.Count)
            for (int i = 0; i < ports.Count; i++) map[ports[i]["PORT_".Length..]] = setStates[i].Name;
        else notes.Add($"{SwitchAnimations.Graph}/{Machine}: {ports.Count} reaction ports for {setStates.Count} sets: no port mapping (the sets are chosen by weapon name)");
        return new(sets, min, notes) { Ports = map, Random = ReadRandom(g, animationLength, isAdditive, notes) };
    }

    static IReadOnlyList<WeightedFlinch> ReadRandom(BehaviorGraphDetail g, Func<string, float> animationLength, Func<string, bool>? isAdditive, List<string> notes)
    {
        var state = g.StateMachines.FirstOrDefault(x => x.Name == UpperMachine)?.States.FirstOrDefault(s => s.Name == RandomState);
        if (state?.Generator is not int at || !g.Nodes.TryGetValue(at, out var node)) { notes.Add($"{SwitchAnimations.Graph}: no '{RandomState}' state"); return []; }
        var list = new List<WeightedFlinch>();
        foreach (var link in node.Children)
        {
            if (link.Target is not int t || GraphWalk.SequencerUnder(g, t) is not { } seq) continue;
            float weight = link.Label.StartsWith("random weight ", StringComparison.Ordinal)
                && float.TryParse(link.Label["random weight ".Length..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var w) ? w : 1f;
            list.Add(new WeightedFlinch(ClipOf(seq, animationLength, isAdditive), weight));
        }
        if (list.Count == 0) notes.Add($"{SwitchAnimations.Graph}/{RandomState}: no clips");
        return list;
    }
}

/// <summary>
/// Which of the twelve flinch slots a hit takes (client presentation) and which set a hit plays. The slot by the hit's place on the body: the set machines' <c>hkbReactionStateChooser</c> names each
/// slot by front / back, high / low and left / right, the head slots flagged (data, read 2026-11-11); how the engine measures "high" and "left" is not in the files: ours, <see cref="LowBelow"/> and
/// <see cref="Side"/>. The set by the attacker's weapon's reaction type (<see cref="FlinchAnimations.Ports"/>), else by its name (<see cref="SetFor(string, bool)"/>, the earlier reading).
/// </summary>
public static class FlinchRules
{
    /// <summary>A body hit lower than this above the victim's feet takes a "low" slot (m; ours: about the marine's belt, the pelvis bone stands near 1.05 m in the bind pose).</summary>
    public const float LowBelow = 1.15f;

    /// <summary>The slot for a hit: a head shot takes the head slots (front or back, left or right), a body hit the front or back slots, high or <paramref name="low"/>, left or right.</summary>
    public static int Slot(bool head, bool fromBehind, bool fromLeft, bool low = false) =>
        head ? (fromBehind ? 2 : 0) + (fromLeft ? 0 : 1) : (fromBehind ? 8 : 4) + (low ? 2 : 0) + (fromLeft ? 0 : 1);

    /// <summary>
    /// The slots to try for a hit, in order: the wanted one, then the body slot of the same side and height (a set without head clips), the same side's other height, the other side's body slot of the
    /// same height, then any slot of the set.
    /// </summary>
    public static int Choose(FlinchSet set, bool head, bool fromBehind, bool fromLeft, bool low = false)
    {
        int wanted = Slot(head, fromBehind, fromLeft, low);
        if (set.Slot(wanted) is not null) return wanted;
        foreach (int body in (ReadOnlySpan<int>)[Slot(false, fromBehind, fromLeft, low), Slot(false, fromBehind, fromLeft, !low), Slot(false, fromBehind, !fromLeft, low)])
            if (set.Slot(body) is not null) return body;
        for (int i = 0; i < FlinchSet.SlotCount; i++) if (set.Slot(i) is not null) return i;
        return -1;
    }

    /// <summary>Whether a body hit at height <paramref name="hitZ"/> (game z, m) on a victim standing at <paramref name="feetZ"/> takes a low slot (<see cref="LowBelow"/>).</summary>
    public static bool IsLow(float hitZ, float feetZ) => float.IsFinite(hitZ) && float.IsFinite(feetZ) && hitZ - feetZ < LowBelow;

    /// <summary>
    /// Whether a shot at <paramref name="victimYaw"/>'s marine from <paramref name="shooterX"/>, <paramref name="shooterY"/> comes from behind and from its left (game space, yaw 0 faces +Y, positive yaw turns left).
    /// </summary>
    public static (bool Behind, bool Left) Side(float victimX, float victimY, float victimYaw, float shooterX, float shooterY)
    {
        float dx = shooterX - victimX, dy = shooterY - victimY;
        float fx = -MathF.Sin(victimYaw), fy = MathF.Cos(victimYaw);   // the facing
        float along = dx * fx + dy * fy;                                 // positive: the shooter is in front
        float left = dx * -fy + dy * fx;                                 // positive: on the marine's left
        return (along < 0f, left > 0f);
    }

    /// <summary>
    /// The set a hit plays: the one the attacking weapon's reaction type names (<paramref name="reactionType"/>, through <paramref name="data"/>'s ports), else the set of the weapon's name
    /// (<see cref="SetFor(string, bool)"/>; ours for a type without a port, such as the storm bolter's and the grenade launcher's).
    /// </summary>
    public static string SetFor(FlinchAnimations? data, string? reactionType, string weaponName, bool charging = false) =>
        data?.SetOfReaction(reactionType) is { } set && data.For(set) is not null ? set : SetFor(weaponName, charging);

    /// <summary>The set (the graph's weapon state name) a weapon uses by its name; the Bolter's set is the default. **Interpretation** of the names (the reading before the ports, kept as the fallback).</summary>
    public static string SetFor(string weaponName, bool charging)
    {
        string n = AnimationAlias.Of(weaponName).ToLowerInvariant();   // AnimationAlias: an added weapon's borrowed name (Musou's arsenal)
        if (n.Contains("heavy_bolter")) return "Heavy Bolter Flinch";
        if (n.Contains("las_cannon") || n.Contains("lascannon")) return "Lascannon Flinch";
        if (n.Contains("plasma_cannon")) return charging ? "Plasma Cannon Strong Flinch" : "Plasma Cannon Weak Flinch";
        if (n.Contains("plasma_pistol")) return charging ? "Plasma Pistol Strong Flinch" : "Plasma Pistol Weak Flinch";
        if (n.Contains("plasma")) return charging ? "Plasma Strong Flinch" : "Plasma Weak Flinch";
        if (n.Contains("melta")) return "Melta Flinch";
        if (n.Contains("stalker") || n.Contains("sniper")) return "Stalker Pattern Flinch";
        if (n.Contains("bolt_pistol")) return "Bolt Pistol Flinch";
        if (n.Contains("pistol_bolter")) return "Ranged Light Flinch";
        return "Bolter Flinch";
    }

    /// <summary>
    /// One of the random flinches for <paramref name="seed"/> (any number: the same seed picks the same clip on every client), by weight; -1 when there are none. The graph picks at random (the
    /// seed is ours: a hash of the victim and the hit time, so every view of one hit shows the same clip).
    /// </summary>
    public static int PickRandom(IReadOnlyList<WeightedFlinch> random, uint seed)
    {
        if (random.Count == 0) return -1;
        float total = 0f;
        foreach (var r in random) total += MathF.Max(r.Weight, 0f);
        if (!(total > 0f)) return (int)(seed % (uint)random.Count);
        uint h = seed * 2654435761u;
        h ^= h >> 15;
        float pick = (h & 0xFFFFFF) / (float)0x1000000 * total;
        for (int i = 0; i < random.Count; i++)
        {
            pick -= MathF.Max(random[i].Weight, 0f);
            if (pick < 0f) return i;
        }
        return random.Count - 1;
    }
}

/// <summary>
/// The reaction type each multiplayer weapon's ranged hit triggers (the wargear files' <c>ability_data/ability_definitions/ability_ranged_*</c> ... <c>delivery/hit_sim</c> ...
/// <c>trigger_reaction/reaction_by_intensity/entry/reaction_type</c>, the primary ability first; penetration exits and delayed actions left out): <c>sm_mp_ranged_bolter</c> <c>rng_bolter</c>,
/// <c>sm_mp_pistol_bolt_pistol</c> <c>rng_bolt_pistol</c>, <c>sm_mp_pistol_bolter</c> <c>reaction_ranged_light</c>, <c>sm_mp_ranged_plasma_gun</c> <c>rng_plasma_gun_weak</c>, <c>sm_mp_ranged_melta</c>
/// <c>rng_melta</c>, <c>sm_mp_ranged_sniper_rifle</c> <c>rng_sniperrifle</c>, <c>sm_mp_exotic_heavy_bolter</c> <c>rng_heavybolter</c>, <c>sm_mp_exotic_las_cannon</c> <c>rng_lascannon</c>,
/// <c>sm_mp_ranged_storm_bolter</c> <c>rng_stormbolter</c>, <c>sm_mp_ranged_grenade_launcher</c> <c>rng_autocannon</c> (read 2026-11-11). Hit reactions only: the flinch set (<see cref="FlinchRules"/>).
/// </summary>
public static class WeaponReactions
{
    const string Abilities = "ability_data/ability_definitions";

    /// <summary>The ranged hit's reaction type of one wargear file, or null when it has none.</summary>
    public static string? Of(SmFormats.Baf.BafDocument doc)
    {
        if (doc.Root.Table(Abilities) is not { } abilities) return null;
        var ranged = abilities.Entries.Where(e => e.Type == SmFormats.Baf.BafType.Table && e.Name.StartsWith("ability_ranged", StringComparison.Ordinal))
            .OrderBy(e => e.Name == "ability_ranged_primary" ? 0 : 1).ToList();   // stable: file order after the primary
        foreach (var e in ranged)
            if (First(e.AsTable()) is { } type) return type;
        return null;
    }

    static string? First(SmFormats.Baf.BafTable t)
    {
        foreach (var e in t.Entries)
        {
            if (e.Type == SmFormats.Baf.BafType.String && e.Name == "reaction_type") return e.AsString();
            if (e.Type != SmFormats.Baf.BafType.Table || e.Name is "penetration" or "delayed_action") continue;
            if (First(e.AsTable()) is { } found) return found;
        }
        return null;
    }

    /// <summary>Reads every multiplayer wargear file's ranged reaction type (by file name; a few hundred milliseconds, for a worker). Errors leave the table empty (the names stand in).</summary>
    public static IReadOnlyDictionary<string, string> Load(string gameDir, List<string>? notes = null)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var archive = SmFormats.Obpk.ObpkArchive.Open(Path.Combine(gameDir, GameplayTuning.WargearPackage));
            archive.ReadEntries(e => archive.GroupName(e) == "attr_pc" && e.FileName.Contains("_mp_", StringComparison.OrdinalIgnoreCase),
                (e, d) => { if (Of(SmFormats.Baf.BafDocument.Read(d)) is { } type) map.TryAdd(e.FileName, type); });
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { notes?.Add($"weapon reactions: not read ({ex.Message})"); }
        return map;
    }
}

// ---------------------------------------------------------------------------------------------------------------------------------------------------------
// 3. Death before the ragdoll

public enum MarineDeathKind
{
    DefaultFront, DefaultBack, Sprint, Bullrush, HeadshotFront, HeadshotBack, Plasma,
    LasFront, LasBack, LasRight, LasLeft, MeltaFront, MeltaBack, MeltaRight, MeltaLeft,
    // Hit reactions (2026-11-11): the ranged deaths of the flinch branches, the blast deaths of the reaction graph and the jump pack's death in the air.
    RangedLeft, RangedRight, RangedBack, BlastFront, BlastRight, BlastBack, BlastLeft, JumpStart, JumpFall, JumpImpact,
}

/// <summary>
/// A death clip and the time the graph hands the body to the ragdoll (<paramref name="RagdollAt"/>: the "Powered Ragdoll Controls Modifier" tag's start x the clip's length; for the jump death's start
/// the branch into its fall, for the looping fall its length). <paramref name="Blend"/>: the seconds of the transition that enters it (the graph's blending effect).
/// </summary>
public sealed record DeathClip(string Clip, float Length, float RagdollAt, float Blend = DeathAnimations.DefaultBlend);

/// <summary>
/// One transition into the death clips (hit reactions, 2026-11-11): from the base graph's ROOT into its DEATH state (<paramref name="Target"/>: the nested state it enters, Havok's to-nested-state id;
/// null: the death machine's start, the conduit "ENTRY - Death State"), or from that conduit (<paramref name="Target"/>: the state it leads to). <paramref name="Branch"/>: the sequencer branch that must be open
/// (the killing hit's flinch raises it), <paramref name="Condition"/> the condition text; the highest <paramref name="Priority"/> that holds is taken. <paramref name="Blend"/>: its blend (s).
/// </summary>
public sealed record DeathRoute(int Priority, string? Branch, string? Condition, string? Target, float Blend = DeathAnimations.DefaultBlend);

/// <summary>
/// The death states of <c>SM_Base_MP</c> ("DEATH" > "Behavior Death"), by <see cref="MarineDeathKind"/>, the transitions into them (<see cref="Routes"/>, <see cref="EntryRoutes"/>) and the blast deaths of
/// <c>MP_SM_Reactions</c> (the grenade reactions <c>SM_MP_REC_Grenade_F/R/B/L_01</c> carry "Set Reaction Death Bool" over the whole clip and "Death Powered Ragdoll Controls" from 0.3: a player killed
/// in such a reaction dies in it, the death state held off).
/// </summary>
public sealed record DeathAnimations(IReadOnlyDictionary<MarineDeathKind, DeathClip> Clips, IReadOnlyList<string> Notes)
{
    public const string Machine = "Behavior Death", DeathState = "DEATH";
    /// <summary>The graph's "Default 3 Frame Transition" (s): the blend of every ROOT-to-DEATH transition, used where none is read.</summary>
    public const float DefaultBlend = 0.1f;

    /// <summary>ROOT-to-DEATH transitions (see <see cref="DeathRoute"/>); empty when not read (<see cref="DeathRules.DefaultRoutes"/> stand in).</summary>
    public IReadOnlyList<DeathRoute> Routes { get; init; } = [];
    /// <summary>The transitions out of the death machine's start conduit; empty when not read (<see cref="DeathRules.DefaultEntryRoutes"/> stand in).</summary>
    public IReadOnlyList<DeathRoute> EntryRoutes { get; init; } = [];

    /// <summary>(outer state, inner state or null for a leaf) of each kind.</summary>
    static readonly (MarineDeathKind Kind, string Outer, string? Inner)[] Map =
    [
        (MarineDeathKind.DefaultFront, "Death - Default", "Default Death Front"), (MarineDeathKind.DefaultBack, "Death - Default", "Default Death Back"),
        (MarineDeathKind.Sprint, "Sprinting Death", null), (MarineDeathKind.Bullrush, "Bullrush Death", null),
        (MarineDeathKind.HeadshotFront, "Headshot Death", "Headshot Front"), (MarineDeathKind.HeadshotBack, "Headshot Death", "Headshot Back"),
        (MarineDeathKind.Plasma, "Plasma Death", null),
        (MarineDeathKind.LasFront, "Lascannon Deaths", "Las Death Front"), (MarineDeathKind.LasBack, "Lascannon Deaths", "Las Death Back"),
        (MarineDeathKind.LasRight, "Lascannon Deaths", "Las Death Right"), (MarineDeathKind.LasLeft, "Lascannon Deaths", "Las Death Left"),
        (MarineDeathKind.MeltaFront, "Melta Death", "Melta Death Front"), (MarineDeathKind.MeltaBack, "Melta Death", "Melta Death Back"),
        (MarineDeathKind.MeltaRight, "Melta Death", "Melta Death Right"), (MarineDeathKind.MeltaLeft, "Melta Death", "Melta Death Left"),
        (MarineDeathKind.RangedLeft, "Death Ranged Left", null), (MarineDeathKind.RangedRight, "Death Ranged Right", null), (MarineDeathKind.RangedBack, "Death Ranged Back", null),
        (MarineDeathKind.JumpStart, "Jumppack Death", "Jumppack Death Start"), (MarineDeathKind.JumpFall, "Jumppack Death", "Jumppack Death Fall"),
        (MarineDeathKind.JumpImpact, "Jumppack Death", "Jumppack Death Hit Floor"),
    ];

    /// <summary>The reaction graph's grenade clips (front, right, back, left: <see cref="ReactionTuning.Sets"/>' stun set) and their blast-death kinds.</summary>
    static readonly (MarineDeathKind Kind, string Clip)[] BlastMap =
    [
        (MarineDeathKind.BlastFront, "SM_MP_REC_Grenade_F_01"), (MarineDeathKind.BlastRight, "SM_MP_REC_Grenade_R_01"),
        (MarineDeathKind.BlastBack, "SM_MP_REC_Grenade_B_01"), (MarineDeathKind.BlastLeft, "SM_MP_REC_Grenade_L_01"),
    ];

    public DeathClip? Get(MarineDeathKind kind) => Clips.GetValueOrDefault(kind);

    public static DeathAnimations From(IReadOnlyList<BehaviorGraphDetail> graphs, Func<string, float> animationLength)
    {
        var notes = new List<string>();
        var clips = new Dictionary<MarineDeathKind, DeathClip>();
        if (graphs.FirstOrDefault(x => x.Name.Equals(SwitchAnimations.Graph, StringComparison.OrdinalIgnoreCase)) is not { } g) { notes.Add($"graph {SwitchAnimations.Graph} not found"); return new(clips, notes); }
        var machine = g.StateMachines.FirstOrDefault(x => x.Name == Machine);
        if (machine is null) { notes.Add($"{SwitchAnimations.Graph}: no machine '{Machine}'"); return new(clips, notes); }
        var (routes, entry) = ReadRoutes(g, machine, notes);
        float BlendOf(string outer) =>
            routes.FirstOrDefault(r => r.Target == outer)?.Blend ?? entry.FirstOrDefault(r => r.Target == outer)?.Blend ?? DefaultBlend;
        foreach (var (kind, outerName, innerName) in Map)
        {
            var outer = machine.States.FirstOrDefault(s => s.Name == outerName);
            if (outer is null) { notes.Add($"death: no state '{outerName}'"); continue; }
            int? generator = outer.Generator;
            float blend = BlendOf(outerName);
            if (innerName is not null)
            {
                BehaviorStateMachine? inner = null;
                foreach (var at in GraphWalk.Walk(g, outer.Generator, 4)) if (g.MachineAt(at) is { } m) { inner = m; break; }
                var leaf = inner?.States.FirstOrDefault(s => s.Name == innerName);
                if (leaf is null) { notes.Add($"death: no state '{outerName}' / '{innerName}'"); continue; }
                generator = leaf.Generator;
                // A state inside the jump death is entered from its sibling (start -> fall -> floor): that transition's blend.
                if (inner!.States.SelectMany(s => s.Transitions).FirstOrDefault(t => t.ToStateId == leaf.Id) is { } into) blend = g.EffectAt(into.Transition)?.Duration ?? 0f;
            }
            if (GraphWalk.SequencerUnder(g, generator) is not { } seq) { notes.Add($"death: no clip in '{outerName}'{(innerName is null ? "" : " / '" + innerName + "'")}"); continue; }
            string clip = seq.Animations[0];
            float length = animationLength(clip) / GraphWalk.Speed(seq);
            float handOver = kind switch
            {
                MarineDeathKind.JumpStart => GraphWalk.Tag(seq, "Branch", "Jump_Death_Start")?.Start ?? 1f,   // into the fall
                MarineDeathKind.JumpFall => 1f,                                                                     // loops until the floor
                _ => GraphWalk.Tag(seq, "Modifier", "Powered Ragdoll") is { } tag ? tag.Start : 0.8f,
            };
            clips[kind] = new DeathClip(clip, length, Math.Clamp(handOver, 0f, 1f) * length, blend);
        }
        ReadBlasts(graphs, animationLength, clips, notes);
        return new(clips, notes) { Routes = routes, EntryRoutes = entry };
    }

    /// <summary>The transitions into DEATH from the states beside it (ROOT) and out of the death machine's start conduit.</summary>
    static (List<DeathRoute> Routes, List<DeathRoute> Entry) ReadRoutes(BehaviorGraphDetail g, BehaviorStateMachine deathMachine, List<string> notes)
    {
        var routes = new List<DeathRoute>();
        var entry = new List<DeathRoute>();
        string? StateName(BehaviorStateMachine m, int id) => m.States.FirstOrDefault(s => s.Id == id)?.Name;
        var outer = g.AllStates.FirstOrDefault(x => x.State.Name == DeathState);
        if (outer.State is null) notes.Add($"death: no state '{DeathState}'");
        else
        {
            foreach (var t in outer.Machine.States.SelectMany(s => s.Transitions).Concat(outer.Machine.WildcardTransitions))
            {
                if (t.ToStateId != outer.State.Id) continue;
                string? target = t.ToNestedStateId >= 0 ? StateName(deathMachine, t.ToNestedStateId) : null;
                var route = new DeathRoute(t.Priority, t.BranchEvent, t.ConditionText, target, g.EffectAt(t.Transition)?.Duration ?? DefaultBlend);
                if (!routes.Contains(route)) routes.Add(route);
            }
            if (routes.Count == 0) notes.Add($"death: no transition into '{DeathState}'");
        }
        if (deathMachine.States.FirstOrDefault(s => s.Id == deathMachine.StartStateId) is { } start)
            foreach (var t in start.Transitions)
                if (StateName(deathMachine, t.ToStateId) is { } to) entry.Add(new DeathRoute(t.Priority, t.BranchEvent, t.ConditionText, to, g.EffectAt(t.Transition)?.Duration ?? DefaultBlend));
        return (routes, entry);
    }

    /// <summary>The blast deaths: the reaction graph's sequencers that play the grenade clips with a "Death Powered Ragdoll" modifier tag.</summary>
    static void ReadBlasts(IReadOnlyList<BehaviorGraphDetail> graphs, Func<string, float> animationLength, Dictionary<MarineDeathKind, DeathClip> clips, List<string> notes)
    {
        if (graphs.FirstOrDefault(x => x.Name.Equals(ReactionTuning.Graph, StringComparison.OrdinalIgnoreCase)) is not { } r) { notes.Add($"graph {ReactionTuning.Graph} not found: no blast deaths"); return; }
        foreach (var (kind, clip) in BlastMap)
        {
            var seq = r.Sequencers.Values.FirstOrDefault(s => s.Animations.Count > 0 && s.Animations[0] == clip && GraphWalk.Tag(s, "Modifier", "Death Powered Ragdoll") is not null);
            if (seq is null) { notes.Add($"death: no blast death sequencer for {clip}"); continue; }
            float length = animationLength(clip) / GraphWalk.Speed(seq);
            clips[kind] = new DeathClip(clip, length, Math.Clamp(GraphWalk.Tag(seq, "Modifier", "Death Powered Ragdoll")!.Start, 0f, 1f) * length);
        }
    }
}

/// <summary>
/// What decides a marine's death clip (hit reactions, 2026-11-11), from its last living frame and the killing blow: in a jump-pack jump (<c>BOOL_JumpIsActive</c>), sprinting (<c>BOOL_SprintIsActive</c>), in
/// its own bull rush's attack clip (the branch <c>BRANCH_Bullrush_Death</c> that clip raises), killed by a blast (the reaction graph's grenade reaction plays as the death), the death branches of the
/// killing hit's flinch (<see cref="FlinchClip.DeathBranches"/>) and the blow's direction in the victim's frame (<paramref name="BlowAlongFacing"/>: the part along its facing, positive = travelling the
/// way it faces = from behind; <paramref name="BlowRight"/>: the part to its right).
/// </summary>
public sealed record DeathContext(bool JumpActive, bool Sprinting, bool BullRush, bool Blast, IReadOnlyList<string> Branches, bool HasDirection, float BlowAlongFacing, float BlowRight);

/// <summary>
/// Which death clip a kill plays (client presentation). Data (2026-11-11): the base graph's ROOT enters DEATH by the highest-priority transition that holds (<see cref="DeathAnimations.Routes"/>: jump pack
/// 10, <c>BRANCH_Headshot</c> 9, <c>BRANCH_Las_Death</c> 8, <c>BRANCH_Melta_Death</c> 7, sprint 6, <c>BRANCH_Plasma_Death</c> 5, <c>BRANCH_Death_Ranged_Left</c> 4, <c>_Right</c> 3, <c>_Back</c> 2,
/// else 1 into the conduit, which goes on to the bull rush death (6), the sprint (5) or the default pair (1)); the branches are raised by the flinch the killing hit plays (its set's slot sequencer).
/// **Interpretation**: that the killing hit's own flinch is the one whose branch counts; the conduit's untriggered transitions to the lascannon, melta and plasma deaths (4, 3, 2) are not taken (else
/// every default death would be a lascannon death); a blast kill on the ground plays the grenade reaction it would have played (the reaction holds the death state off) in four 90 degree sectors.
/// </summary>
public static class DeathRules
{
    /// <summary>The install's ROOT-to-DEATH transitions (read 2026-11-11), for a graph whose routes were not read (tests, an older reader).</summary>
    public static readonly IReadOnlyList<DeathRoute> DefaultRoutes =
    [
        new(10, null, "all[BOOL_JumpIsActive, all[!BOOL_IsRagdoll, BOOL_IsDead, !BOOL_Death_State_Disabled]]", "Jumppack Death"),
        new(9, "BRANCH_Headshot", null, "Headshot Death"), new(8, "BRANCH_Las_Death", null, "Lascannon Deaths"), new(7, "BRANCH_Melta_Death", null, "Melta Death"),
        new(6, null, "all[BOOL_SprintIsActive, all[!BOOL_IsRagdoll, BOOL_IsDead, !BOOL_Death_State_Disabled]]", "Sprinting Death"),
        new(5, "BRANCH_Plasma_Death", null, "Plasma Death"), new(4, "BRANCH_Death_Ranged_Left", null, "Death Ranged Left"),
        new(3, "BRANCH_Death_Ranged_Right", null, "Death Ranged Right"), new(2, "BRANCH_Death_Ranged_Back", null, "Death Ranged Back"),
        new(1, null, "all[!BOOL_IsRagdoll, BOOL_IsDead, !BOOL_Death_State_Disabled]", null),
    ];

    /// <summary>The install's transitions out of the death conduit (read 2026-11-11).</summary>
    public static readonly IReadOnlyList<DeathRoute> DefaultEntryRoutes =
    [
        new(6, "BRANCH_Bullrush_Death", null, "Bullrush Death"), new(5, null, "(BOOL_SprintIsActive & BOOL_IsDead)", "Sprinting Death"),
        new(4, null, null, "Lascannon Deaths", 0.167f), new(3, null, null, "Melta Death"), new(2, null, null, "Plasma Death", 0.167f), new(1, null, null, "Death - Default", 0.5f),
    ];

    /// <summary>The branch the bull rush attack clip raises (<c>MP_SM_Bolter_NuSprint</c>'s <c>SM_BUL_Hit_M_2.5m</c>, 0 to 1).</summary>
    public const string BullrushBranch = "BRANCH_Bullrush_Death";

    /// <summary>The death kind for a kill (see the type); null only when nothing in the routes applies (not on the install's graph).</summary>
    public static MarineDeathKind? Choose(DeathContext c, DeathAnimations? data = null)
    {
        if (c.Blast && !c.JumpActive) return Blast(c);   // no reaction plays in jump-pack flight (the reaction graph has no airborne clips), so a blast in the air takes the jump death
        var routes = data?.Routes is { Count: > 0 } read ? read : DefaultRoutes;
        foreach (var route in routes.OrderByDescending(r => r.Priority))
        {
            if (route.Branch is { } b && !c.Branches.Contains(b)) continue;
            if (!ConditionHolds(route.Condition, c)) continue;
            if (route.Target is null) return Entry(c, data);
            if (KindOf(route.Target, c) is { } kind) return kind;
        }
        return Entry(c, data);
    }

    /// <summary>The conduit: the triggered transitions by priority (the bull rush branch, the sprint condition), else the lowest-priority untriggered one (the default pair).</summary>
    static MarineDeathKind? Entry(DeathContext c, DeathAnimations? data)
    {
        var entry = data?.EntryRoutes is { Count: > 0 } read ? read : DefaultEntryRoutes;
        foreach (var route in entry.OrderByDescending(r => r.Priority))
        {
            if (route.Branch is null && route.Condition is null) continue;
            if (route.Branch is { } b && !(b == BullrushBranch ? c.BullRush : c.Branches.Contains(b))) continue;
            if (!ConditionHolds(route.Condition, c)) continue;
            if (route.Target is { } t && KindOf(t, c) is { } kind) return kind;
        }
        var fallback = entry.Where(r => r.Branch is null && r.Condition is null).OrderBy(r => r.Priority).FirstOrDefault();
        return KindOf(fallback?.Target ?? "Death - Default", c) ?? DefaultPair(c);
    }

    static MarineDeathKind DefaultPair(DeathContext c) => c.HasDirection && c.BlowAlongFacing > 0f ? MarineDeathKind.DefaultBack : MarineDeathKind.DefaultFront;

    /// <summary>The kind a death state stands for, with its own front / back or wedge choice; null for a state not modelled.</summary>
    static MarineDeathKind? KindOf(string target, DeathContext c)
    {
        bool behind = c.HasDirection && c.BlowAlongFacing > 0f;
        return target switch
        {
            "Death - Default" => DefaultPair(c),
            "Sprinting Death" => MarineDeathKind.Sprint,
            "Bullrush Death" => MarineDeathKind.Bullrush,
            "Headshot Death" => behind ? MarineDeathKind.HeadshotBack : MarineDeathKind.HeadshotFront,
            "Plasma Death" => MarineDeathKind.Plasma,
            "Lascannon Deaths" => Wedge(c, melta: false),
            "Melta Death" => Wedge(c, melta: true),
            "Death Ranged Left" => MarineDeathKind.RangedLeft,
            "Death Ranged Right" => MarineDeathKind.RangedRight,
            "Death Ranged Back" => MarineDeathKind.RangedBack,
            "Jumppack Death" => MarineDeathKind.JumpStart,
            _ => null,
        };
    }

    /// <summary>The lascannon / melta wedges: the angle between the blow and the facing's reverse (0 = shot from the front): front within 55 degrees, back beyond 125, else the side.</summary>
    static MarineDeathKind Wedge(DeathContext c, bool melta)
    {
        if (!c.HasDirection) return melta ? MarineDeathKind.MeltaFront : MarineDeathKind.LasFront;
        float degrees = MathF.Atan2(c.BlowRight, -c.BlowAlongFacing) * 180f / MathF.PI;   // 0 = the blow comes straight at the face
        float a = MathF.Abs(degrees);
        if (a <= 55f) return melta ? MarineDeathKind.MeltaFront : MarineDeathKind.LasFront;
        if (a >= 125f) return melta ? MarineDeathKind.MeltaBack : MarineDeathKind.LasBack;
        return degrees > 0f ? (melta ? MarineDeathKind.MeltaLeft : MarineDeathKind.LasLeft) : (melta ? MarineDeathKind.MeltaRight : MarineDeathKind.LasRight);
    }

    /// <summary>The blast death's clip: where the blast came from in four 90 degree sectors (the reaction's own rule, <see cref="SmSim.Reactions.DirectionOf"/>); no direction: the front.</summary>
    static MarineDeathKind Blast(DeathContext c)
    {
        if (!c.HasDirection) return MarineDeathKind.BlastFront;
        float f = -c.BlowAlongFacing, r = -c.BlowRight;   // the source lies against the blow
        if (MathF.Abs(f) >= MathF.Abs(r)) return f >= 0f ? MarineDeathKind.BlastFront : MarineDeathKind.BlastBack;
        return r > 0f ? MarineDeathKind.BlastRight : MarineDeathKind.BlastLeft;
    }

    /// <summary>
    /// Whether a transition's condition holds at the death: every <c>BOOL_</c> variable it names is read as true (or, with <c>!</c>, false) under <c>all[...]</c> and <c>&amp;</c>; the known ones are
    /// <c>BOOL_IsDead</c> (true), <c>BOOL_IsRagdoll</c> and <c>BOOL_Death_State_Disabled</c> (false), <c>BOOL_JumpIsActive</c> and <c>BOOL_SprintIsActive</c> (from the context); any other variable fails.
    /// </summary>
    public static bool ConditionHolds(string? condition, DeathContext c)
    {
        if (string.IsNullOrEmpty(condition)) return true;
        bool any = condition.Contains("any[", StringComparison.Ordinal);
        bool seen = false;
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(condition, @"(!?)(BOOL_[A-Za-z0-9_]+)"))
        {
            seen = true;
            bool? value = m.Groups[2].Value switch
            {
                "BOOL_IsDead" => true, "BOOL_IsRagdoll" => false, "BOOL_Death_State_Disabled" => false,
                "BOOL_JumpIsActive" => c.JumpActive, "BOOL_SprintIsActive" => c.Sprinting, _ => null,
            };
            bool holds = value is { } v && (m.Groups[1].Value == "!" ? !v : v);
            if (any && holds) return true;
            if (!any && !holds) return false;
        }
        return !any || !seen;
    }

    /// <summary>
    /// Whether a death plays a standing clip before the ragdoll: not for a fall out of the level and not for a victim that was falling at its last living frame without a jump pack's jump (the body is not
    /// placed while dead, so a standing death clip would hang it in the air; those go straight to the ragdoll). A jump-pack jump has its own death (<see cref="MarineDeathKind.JumpStart"/>).
    /// </summary>
    public static bool Holds(bool fellOutOfLevel, bool airborneAtLastLivingFrame) => !fellOutOfLevel && !airborneAtLastLivingFrame;

    /// <summary>
    /// The seconds a death clip holds the body before the ragdoll takes it: the clip's hand-over (<see cref="DeathClip.RagdollAt"/>: the graph's powered-ragdoll tag), at most <paramref name="cap"/> (the
    /// client's 1.5 s: ours). The respawn, the kill's credit and the body's collision do not wait for it (the simulation's own timings; the clip is drawn only).
    /// </summary>
    public static float HoldSeconds(DeathClip clip, float cap) => float.IsFinite(clip.RagdollAt) ? Math.Clamp(clip.RagdollAt, 0f, MathF.Max(cap, 0f)) : 0f;

    /// <summary>The share of the body's last horizontal velocity that is left <paramref name="age"/> seconds into the hold (an exponential decay with time constant <paramref name="tau"/>; ours): the held body slides on and slows.</summary>
    public static float HeldVelocityFactor(float age, float tau) => tau > 1e-4f && age > 0f ? MathF.Exp(-age / tau) : 1f;

    /// <summary>
    /// Animation pass 2's choice (before the hit-reaction routes; the client's <c>--old-hit-reactions</c>): a sprinting victim its stagger; a charge's kill the bull rush clip; a head shot the head-shot
    /// pair; a plasma weapon the plasma death; a lascannon, plasma cannon or melta kill the wedge clip; everything else the default pair.
    /// </summary>
    public static MarineDeathKind ChoosePass2(bool sprinting, bool chargeKill, bool head, WeaponDeath weapon, bool hasDirection, float blowAlongFacing, float blowRight)
    {
        var c = new DeathContext(false, sprinting, chargeKill, false, [], hasDirection, blowAlongFacing, blowRight);
        if (sprinting && !chargeKill) return MarineDeathKind.Sprint;
        if (chargeKill) return MarineDeathKind.Bullrush;
        if (head && weapon == WeaponDeath.Bolter) return c.HasDirection && c.BlowAlongFacing > 0f ? MarineDeathKind.HeadshotBack : MarineDeathKind.HeadshotFront;
        if (weapon == WeaponDeath.Plasma) return MarineDeathKind.Plasma;
        if (weapon is WeaponDeath.Las or WeaponDeath.Melta) return Wedge(c, weapon == WeaponDeath.Melta);
        return DefaultPair(c);
    }

    /// <summary>The weapon class of a kill by the wargear name (interpretation; kept for the deaths whose reaction type is unknown).</summary>
    public static WeaponDeath WeaponOf(string? weaponName)
    {
        if (weaponName is null) return WeaponDeath.Bolter;
        string n = weaponName.ToLowerInvariant();
        if (n.Contains("las_cannon") || n.Contains("lascannon") || n.Contains("plasma_cannon")) return WeaponDeath.Las;
        if (n.Contains("melta")) return WeaponDeath.Melta;
        if (n.Contains("plasma")) return WeaponDeath.Plasma;
        return WeaponDeath.Bolter;
    }
}

public enum WeaponDeath { Bolter, Plasma, Las, Melta }

/// <summary>
/// The hand-over from the death clip to the ragdoll keeps the clip's motion (hit reactions, 2026-11-11; ours: the original powers the ragdoll's joints toward the clip from its tag, which is not
/// modelled): each ragdoll body starts with the velocity and spin its bone had over the clip's last frame, capped, on top of the body's own slide and the killing blow's push.
/// </summary>
public static class DeathHandover
{
    /// <summary>The most speed (m/s) and spin (rad/s) a body takes from the clip (ours: a clip sampled over a short frame must not fling the body).</summary>
    public const float MaxSpeed = 6f, MaxSpin = 12f;

    /// <summary>
    /// The linear velocity (of the point <paramref name="centre"/> of the body, in its own frame) and the angular velocity of a body that went from <paramref name="before"/> to <paramref name="after"/>
    /// (rotation and translation, any common frame) over <paramref name="seconds"/>, each capped (<see cref="MaxSpeed"/>, <see cref="MaxSpin"/>). Zero for no time.
    /// </summary>
    public static (System.Numerics.Vector3 Linear, System.Numerics.Vector3 Angular) Velocity(System.Numerics.Quaternion beforeRotation, System.Numerics.Vector3 beforeOrigin,
        System.Numerics.Quaternion afterRotation, System.Numerics.Vector3 afterOrigin, System.Numerics.Vector3 centre, float seconds)
    {
        if (!(seconds > 1e-4f) || !float.IsFinite(seconds)) return (default, default);
        var a = System.Numerics.Vector3.Transform(centre, beforeRotation) + beforeOrigin;
        var b = System.Numerics.Vector3.Transform(centre, afterRotation) + afterOrigin;
        var linear = Cap((b - a) / seconds, MaxSpeed);
        var delta = System.Numerics.Quaternion.Normalize(afterRotation * System.Numerics.Quaternion.Inverse(beforeRotation));
        if (delta.W < 0f) delta = new System.Numerics.Quaternion(-delta.X, -delta.Y, -delta.Z, -delta.W);   // the short way round
        float angle = 2f * MathF.Acos(Math.Clamp(delta.W, -1f, 1f));
        float s = MathF.Sqrt(MathF.Max(1f - delta.W * delta.W, 0f));
        var axis = s > 1e-5f ? new System.Numerics.Vector3(delta.X, delta.Y, delta.Z) / s : System.Numerics.Vector3.Zero;
        return (linear, Cap(axis * (angle / seconds), MaxSpin));
    }

    /// <summary>The most the clip's whole-body motion may lift the corpse (m/s; ours: the grenade reaction throws the body up at its hand-over and must not launch it).</summary>
    public const float MaxLift = 3f;

    /// <summary>
    /// What the clip adds to each body's start velocity on top of the push <paramref name="kick"/> (given to every body anyway) and the slide: each body's own motion relative to the whole body
    /// (the mass-weighted mean of <paramref name="clip"/>), plus the whole body's motion, where along the push only what goes beyond the push counts (the clip's throw and the push do not add up: the
    /// larger counts; against the push they do), lifting at most <see cref="MaxLift"/> along <paramref name="up"/>. Ours (2026-11-11, from the stage's blast death: summed, the body flew 10 m).
    /// </summary>
    public static System.Numerics.Vector3[] Contributions(IReadOnlyList<System.Numerics.Vector3> clip, IReadOnlyList<float> masses, System.Numerics.Vector3 kick, System.Numerics.Vector3 up)
    {
        int n = clip.Count;
        var result = new System.Numerics.Vector3[n];
        float total = 0f;
        var mean = System.Numerics.Vector3.Zero;
        for (int i = 0; i < n; i++) { float m = i < masses.Count && masses[i] > 0f ? masses[i] : 1f; mean += clip[i] * m; total += m; }
        if (n == 0 || !(total > 0f)) return result;
        mean /= total;
        var whole = mean;
        float k = kick.Length();
        if (k > 1e-4f)
        {
            var d = kick / k;
            float along = System.Numerics.Vector3.Dot(mean, d);
            float beyond = along > k ? along - k : along > 0f ? 0f : along;
            whole = mean - along * d + beyond * d;
        }
        if (up.LengthSquared() > 1e-8f)
        {
            var u = System.Numerics.Vector3.Normalize(up);
            float lift = System.Numerics.Vector3.Dot(whole, u);
            if (lift > MaxLift) whole -= (lift - MaxLift) * u;
        }
        for (int i = 0; i < n; i++) result[i] = clip[i] - mean + whole;
        return result;
    }

    static System.Numerics.Vector3 Cap(System.Numerics.Vector3 v, float max)
    {
        if (!float.IsFinite(v.X) || !float.IsFinite(v.Y) || !float.IsFinite(v.Z)) return default;
        float l = v.Length();
        return l > max ? v * (max / l) : v;
    }
}

// ---------------------------------------------------------------------------------------------------------------------------------------------------------
// 4. The jump pack's get-up and the flight noise

/// <summary>
/// The ground pound's recovery into a run (<c>MP_SM_NAV_JP_Sim</c>: "GroundPoundImpact" goes to "GroundPoundRun" over <see cref="Blend"/> (0.167 s, 'Fast Blend BranchIn') when the requested speed is at least
/// <see cref="MinSpeed"/> (0.5) from the impact clip's <c>BRANCH_to_Getup</c> window (<see cref="FromImpact"/> seconds into it), else to "GroundPound Idle" at <c>BRANCH_To_Idle_Getup</c>):
/// <see cref="RunClip"/> (<c>SM_JMP_Landing_MedRun</c>, <see cref="RunLength"/> s) ends at its <c>BRANCH_Run</c> (<see cref="RunEnd"/> s). <see cref="NoiseClip"/>: the flight's <c>Noise</c> blend
/// (<c>SM_JMP_Blend_90_Pos_Fast</c>, 1 s, looping minus its first pose, added to the flight pose).
/// </summary>
public sealed record JumpPackAnimations(string? RunClip, float RunLength, float FromImpact, float MinSpeed, float Blend, float RunEnd, string? NoiseClip, float NoiseLength, IReadOnlyList<string> Notes, bool NoiseAdditive = false)
{
    public const string Graph = "MP_SM_NAV_JP_Sim";

    public static JumpPackAnimations From(IReadOnlyList<BehaviorGraphDetail> graphs, Func<string, float> animationLength, Func<string, bool>? isAdditive = null)
    {
        var notes = new List<string>();
        if (graphs.FirstOrDefault(x => x.Name.Equals(Graph, StringComparison.OrdinalIgnoreCase)) is not { } g) { notes.Add($"graph {Graph} not found"); return new(null, 0f, 0f, 0.5f, 0.167f, 0f, null, 0f, notes); }
        string? run = null; float runLength = 0f, fromImpact = 0f, minSpeed = 0.5f, blend = 0.167f, runEnd = 0f;
        var impact = g.AllStates.FirstOrDefault(x => x.State.Name == "GroundPoundImpact");
        var runState = impact.Machine?.States.FirstOrDefault(s => s.Name == "GroundPoundRun");
        if (impact.State is null || runState is null) notes.Add($"{Graph}: no GroundPoundImpact -> GroundPoundRun states");
        else
        {
            var toRun = impact.State.Transitions.FirstOrDefault(t => t.ToStateId == runState.Id);
            blend = toRun is null ? blend : g.EffectAt(toRun.Transition)?.Duration ?? blend;
            if (toRun?.ConditionText is { } c && System.Text.RegularExpressions.Regex.Match(c, @"FLT_Input_Nav_Speed in \[(-?[0-9.]+),") is { Success: true } m
                && float.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lo)) minSpeed = lo;
            if (GraphWalk.SequencerUnder(g, impact.State.Generator) is { } impactSeq)
            {
                float impactLength = animationLength(impactSeq.Animations[0]) / GraphWalk.Speed(impactSeq);
                fromImpact = (GraphWalk.Tag(impactSeq, "Branch", toRun?.BranchEvent ?? "BRANCH_to_Getup")?.Start ?? 0.3333f) * impactLength;
            }
            if (GraphWalk.SequencerUnder(g, runState.Generator) is { } runSeq)
            {
                run = runSeq.Animations[0];
                runLength = animationLength(run) / GraphWalk.Speed(runSeq);
                runEnd = (GraphWalk.Tag(runSeq, "Branch", "BRANCH_Run")?.Start ?? 1f) * runLength;
            }
            else notes.Add($"{Graph}: no clip in GroundPoundRun");
        }
        var noise = g.Clips.FirstOrDefault(c => c.Animation.Contains("Blend_90", StringComparison.OrdinalIgnoreCase) && c.Mode == 1);
        if (noise is null) notes.Add($"{Graph}: no flight noise clip");
        return new(run, runLength, fromImpact, minSpeed, blend, runEnd, noise?.Animation, noise is null ? 0f : animationLength(noise.Animation), notes, noise is not null && isAdditive?.Invoke(noise.Animation) == true);
    }
}

/// <summary>Everything pass 2 reads, loaded in one background task (as <see cref="FallAnimations.Load"/>).</summary>
public sealed record UpperBodyBundle(SwitchAnimations Switch, FlinchAnimations Flinch, DeathAnimations Death, JumpPackAnimations JumpPack)
{
    /// <summary>Hit reactions: each multiplayer wargear file's ranged reaction type (<see cref="WeaponReactions"/>), which picks the flinch set of its hits.</summary>
    public IReadOnlyDictionary<string, string> Reactions { get; init; } = new Dictionary<string, string>();

    /// <summary>The reaction type of a weapon's hits (its own file's, else the weapon it borrows its animations from: <see cref="AnimationAlias"/>), or null.</summary>
    public string? ReactionOf(string? weapon) =>
        weapon is null ? null : Reactions.TryGetValue(weapon, out var r) ? r : Reactions.TryGetValue(AnimationAlias.Of(weapon), out var a) ? a : null;

    public static UpperBodyBundle Load(string gameDir)
    {
        var character = RigProfiles.Marine.LoadCharacter(gameDir);
        float? minHitReactionTime = null;
        try
        {
            using var archive = SmFormats.Obpk.ObpkArchive.Open(Path.Combine(gameDir, GameplayTuning.BlueprintPackage));
            archive.ReadEntries(e => archive.GroupName(e) == "attr_pc" && e.FileName == GameplayTuning.MarineBlueprint,
                (_, d) => minHitReactionTime = SmFormats.Baf.BafDocument.Read(d).Root.Table("extensions/health_ext")?.Float("min_hit_reaction_time"));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException) { /* the default applies */ }
        var graphs = HavokBehaviorDetailReader.Read(character.Pack);
        Func<string, float> length = clip => character.LoadAnimation(clip).Animation.Duration;
        Func<string, bool> additive = clip => { try { return character.LoadBlendHint(clip) == AnimationBlendHint.Additive; } catch (Exception ex) when (ex is KeyNotFoundException or NotSupportedException or InvalidDataException) { return false; } };
        return From(graphs, character.Skeleton.BoneNames, length, minHitReactionTime, additive) with { Reactions = WeaponReactions.Load(gameDir) };
    }

    public static UpperBodyBundle From(IReadOnlyList<BehaviorGraphDetail> graphs, IReadOnlyList<string> boneNames, Func<string, float> length, float? minHitReactionTime = null, Func<string, bool>? isAdditive = null) =>
        new(SwitchAnimations.From(graphs, boneNames, length), FlinchAnimations.From(graphs, length, minHitReactionTime, isAdditive), DeathAnimations.From(graphs, length), JumpPackAnimations.From(graphs, length, isAdditive));
}
