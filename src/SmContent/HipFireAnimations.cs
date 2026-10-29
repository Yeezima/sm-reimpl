using SmFormats.Havok;

namespace SmContent;

/// <summary>The weapon families whose firing animations differ (the marine rig's navigation graphs).</summary>
public enum HipFireWeaponClass { Rifle, Pistol, Heavy }

/// <summary>One clip of an aim-pitch blend.</summary>
/// <param name="PitchDegrees">The blend weight on the aim pitch variable (<c>FLT_Aim_Vertical_Deg(_DAMPED)</c>): degrees, negative
/// down; the original blends between neighbouring entries by the current pitch.</param>
/// <param name="Length">Seconds (the clip at playback speed 1).</param>
public sealed record AimClip(float PitchDegrees, string Clip, float Length);

/// <summary>One movement speed of a direction: the aim-pitch clips played at that speed.</summary>
/// <param name="Speed">The blend weight on <c>FLT_Input_Nav_Speed_DAMPED</c>, units/s: 0 on the spot (walk-on-spot clips), 2 walk,
/// 4.5 (sides and back) or 6.5 (forward, diagonals) run; the original blends between neighbouring bands by the current speed.</param>
public sealed record AimSpeedBand(float Speed, IReadOnlyList<AimClip> Clips);

/// <summary>One movement direction relative to the facing (the blend on <c>FLT_Nav_Motion_Angle_DAMPED</c>).</summary>
/// <param name="AngleDegrees">Degrees, 0 forward, +90 right, ±180 back (the right-hand strafe clips sit at +90).</param>
public sealed record AimDirection(float AngleDegrees, IReadOnlyList<AimSpeedBand> Bands)
{
    /// <summary>"F", "FR", "R", "BR", "B", "BL", "L", "FL" for the eight 45° directions; the angle otherwise.</summary>
    public string Label => Math.Round(AngleDegrees) switch
    {
        0 => "F", 45 => "FR", 90 => "R", 135 => "BR", 180 or -180 => "B", -135 => "BL", -90 => "L", -45 => "FL",
        var a => $"{a}°",
    };
    /// <summary>The fastest band (the run clips).</summary>
    public AimSpeedBand? Run => Bands.Where(b => b.Speed > 0).MaxBy(b => b.Speed);
    /// <summary>The slowest moving band when there are faster ones (the walk clips).</summary>
    public AimSpeedBand? Walk => Bands.Count(b => b.Speed > 0) >= 2 ? Bands.Where(b => b.Speed > 0).MinBy(b => b.Speed) : null;
    /// <summary>The band at speed 0 (walk-on-spot clips), if any.</summary>
    public AimSpeedBand? OnTheSpot => Bands.FirstOrDefault(b => b.Speed == 0);
}

/// <summary>The clip a shot plays on top of the movement.</summary>
/// <param name="Sequencer">The recoil sequencer's name in its graph.</param>
/// <param name="Additive">The clip's binding blend hint: additive clips are added to the movement pose (no bone mask).</param>
/// <param name="ShotFinished">Seconds after the shot when the shot state hands back to movement (the start of the
/// <c>BRANCH_Shot_Finished</c> / <c>BRANCH_Fire_End</c> window × length ÷ speed); null when the sequencer has no such tag.</param>
/// <param name="RefireFrom">Seconds after the shot from which the next shot restarts the clip (<c>BRANCH_Refire</c> start); null
/// when the clip restarts on the ability's own refire event.</param>
public sealed record RecoilClip(string Sequencer, string Clip, float Length, float PlaybackSpeed, bool Additive, float? ShotFinished, float? RefireFrom);

/// <summary>A weapon family's hip-fire animation data (see <see cref="HipFireAnimations"/>).</summary>
/// <param name="Directions">The ready (weapon raised, not aiming) movement blend: one entry per direction, back first.</param>
/// <param name="Recoil">The recoil clip a shot layers on the movement.</param>
/// <param name="RaiseDelay">Seconds from the trigger to the shot when the weapon is not yet raised (the "Set Weapon Ready"
/// sequencer's <c>BRANCH_to_Fire</c> start; the wargear's <c>wait_for_weapon_ready</c> is true for these weapons).</param>
/// <param name="NavToFireBlend">Blend into the shot state, seconds (0: the transition has no blending effect and switches at once).</param>
/// <param name="FireToNavBlend">Blend from the shot state back to movement (at <see cref="RecoilClip.ShotFinished"/>), seconds.</param>
/// <param name="SprintToFire">How a sprint becomes a shot: the trigger event and the sprint branch window it needs (null: none).</param>
/// <param name="ReadyCondition">The navigation graph's condition for the ready posture (from a relaxed state), readable; null when
/// the graph has no relaxed posture (heavy weapons are always ready).</param>
/// <param name="RelaxedToReadyBlend">Blend from the relaxed into the ready posture, seconds.</param>
/// <param name="ReadyToRelaxedBlend">Blend back to relaxed, seconds.</param>
/// <param name="FireMask">The shot layer's bone mask: null, since the original's shot blends (<c>Bolter_FireBlend</c>,
/// <c>BoltPistol_FireBlend</c>, the heavy bolter's <c>Blend01</c>) give neither child a mask; the additive recoil clip goes on
/// the whole body.</param>
/// <param name="UpperBodyMask">The upper-body layer of the same family's reload blend (movement on the legs, reload on the
/// rest): bone name → weight, every skeleton bone; null when the family has no such blend. Not used by the original's shots.</param>
public sealed record HipFireSet(HipFireWeaponClass Class, string NavGraph, string NavState, IReadOnlyList<AimDirection> Directions,
    string FireGraph, RecoilClip? Recoil, float? RaiseDelay, float? NavToFireBlend, float? FireToNavBlend, string? SprintToFire,
    string? ReadyCondition, float? RelaxedToReadyBlend, float? ReadyToRelaxedBlend,
    IReadOnlyDictionary<string, float>? FireMask, IReadOnlyDictionary<string, float>? UpperBodyMask, IReadOnlyList<string> Notes);

/// <summary>
/// Hip-fire animation data from the marine rig's behaviour graphs (M27; <see cref="HavokBehaviorDetailReader"/>, layouts in
/// docs/discovery.md "Behaviour graph structure"). In the original a shot without aiming keeps the navigation graph running in its
/// ready posture (<c>Run Ready</c>: per direction and speed a three- or four-way blend of aim-pitch clips, e.g.
/// <c>SM_NAV_Run_F_AimDown_01</c> / <c>_Aim_F_01</c> / <c>_AimUp_01</c>) and blends an additive recoil clip
/// (<c>SM_RNG_FireIdle_S_01</c> for rifles) over it, unmasked. Read-only; nothing is written.
/// </summary>
public sealed record HipFireAnimations(IReadOnlyList<HipFireSet> Sets)
{
    /// <summary>Where a family's data sits in the graphs (names as in the install).</summary>
    /// <param name="NavState">The ready movement state in <paramref name="NavGraph"/>.</param>
    /// <param name="FireGraph">The graph holding the recoil state (named "Recoil"), searched from <paramref name="FireState"/>
    /// when the graph is the base graph.</param>
    /// <param name="Machine">The base graph's state machine for the family's weapon, with its fire, navigation and sprint states.</param>
    /// <param name="ReloadBlend">The base graph's reload blend (movement + reload, masked), or null.</param>
    public sealed record ClassMap(HipFireWeaponClass Class, string NavGraph, string NavState, string FireGraph, string Machine,
        string FireState, string NavigationState, string SprintState, string? ReloadBlend)
    {
        /// <summary>The state under the fire graph's root that plays the recoil ("Recoil" on the marine, "Standing Shoot" on the Ork).</summary>
        public string RecoilState { get; init; } = "Recoil";
        /// <summary>The graph holding <see cref="ReloadBlend"/>; null: the base graph (the marine; the Ork's sits in its reload graph).</summary>
        public string? ReloadBlendGraph { get; init; }
    }

    public const string BaseGraph = "SM_Base_MP";

    /// <summary>
    /// Rifles (bolter, plasma gun, melta, grenade launcher, storm bolter, stalker: every one plays <c>SM_Bolter_NAV</c>; the bolter's
    /// recoil graph stands for them), pistols (bolt pistol) and heavy weapons (heavy bolter). Lascannon, plasma cannon and plasma
    /// pistol have their own fire graphs (not mapped here; graph-dump shows them).
    /// </summary>
    public static readonly ClassMap[] DefaultMaps =
    [
        new(HipFireWeaponClass.Rifle, "SM_Bolter_NAV", "Run Ready", "SM_UB_RangedRecoil", "BolterBehavior", "Bolter Fire", "Bolter Navigation", "Bolter Sprint", "Bolter_ReloadBlend"),
        new(HipFireWeaponClass.Pistol, "SM_Pistol_NAV", "Run Ready", "MP_SM_UB_PistolRecoil", "BoltPistol Behavior", "BoltPistol Fire", "BoltPistol Navigation", "BoltPistol Sprint", "Bolter_ReloadBlend00"),
        new(HipFireWeaponClass.Heavy, "SM_TB_NAV", "Heavy Run", BaseGraph, "Behavior_RangedHeavyBolter", "Heavy Bolter Shoot", "Heavy Bolter Nav", "Heavy Bolter Sprint", null),
    ];

    public HipFireSet? For(HipFireWeaponClass weaponClass) => Sets.FirstOrDefault(s => s.Class == weaponClass);

    /// <summary>
    /// The family whose navigation and recoil graphs a weapon plays, by its wargear name: pistol (bolt pistol, plasma pistol:
    /// interpretation, it has a fire graph of its own), heavy (heavy bolter), rifle (every other weapon: the rifle graph's ready
    /// posture covers the lascannon and plasma cannon too; also the bolters in the pistol slot, <c>sm_mp_pistol_bolter</c>, whose
    /// name names the slot, not a pistol: interpretation).
    /// </summary>
    public static HipFireWeaponClass FamilyOf(string weaponName) =>
        weaponName.Contains("bolt_pistol", StringComparison.OrdinalIgnoreCase) || weaponName.Contains("plasma_pistol", StringComparison.OrdinalIgnoreCase) ? HipFireWeaponClass.Pistol
        : weaponName.Contains("heavy_bolter", StringComparison.OrdinalIgnoreCase) ? HipFireWeaponClass.Heavy
        : HipFireWeaponClass.Rifle;

    /// <summary>
    /// Whether a weapon's ready posture is always on (M31): its family has no relaxed posture (<see cref="HipFireSet.ReadyCondition"/>
    /// null: the heavy bolter), or the family's ready condition names the weapon beside <c>BOOL_IsReady</c> (the rifle graph's
    /// <c>any[BOOL_IsReady, any[BOOL_Grenade_Launcher, BOOL_Melta, BOOL_Lascannon, BOOL_PlasmaCannon]]</c>: carrying one of these
    /// is enough). A variable matches a weapon when its name without <c>BOOL_</c> and underscores is in the weapon's name without
    /// underscores (case ignored): <c>Lascannon</c> in <c>sm_mp_exotic_las_cannon</c>.
    /// </summary>
    public static bool AlwaysReady(HipFireSet set, string weaponName)
    {
        // No ready condition means the navigation graph has no relaxed posture: always ready. That is the heavy bolter's family on the
        // marine and every family on the Ork rig (its graphs have no relaxed stance and no "Set Weapon Ready" raise); the Ork's raise
        // delay (Tactical parity, design section 2) is O4's to set, not read here.
        if (set.ReadyCondition is not { } condition) return true;
        string name = weaponName.Replace("_", "", StringComparison.Ordinal);
        return ReadyCarried(condition).Any(v => name.Contains(v, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The variables (without <c>BOOL_</c> and underscores) that stand beside <c>BOOL_IsReady</c> in the innermost <c>any[…]</c>
    /// holding it, nested groups included, other than <c>IsAiming</c>: the "ready while carrying" weapons of a ready condition.
    /// </summary>
    public static IReadOnlyList<string> ReadyCarried(string condition)
    {
        int at = condition.IndexOf("BOOL_IsReady", StringComparison.Ordinal);
        if (at < 0) return [];
        // The innermost "any[" that opens before BOOL_IsReady and is still open there.
        int open = -1, depth = 0;
        for (int i = at - 1; i >= 0; i--)
        {
            if (condition[i] == ']') depth++;
            else if (condition[i] == '[')
            {
                if (depth == 0) { if (i >= 3 && condition.AsSpan(i - 3, 3).SequenceEqual("any")) { open = i; break; } }
                else depth--;
            }
        }
        if (open < 0) return [];
        int close = open, level = 0;
        for (int i = open; i < condition.Length; i++)
        {
            if (condition[i] == '[') level++;
            else if (condition[i] == ']' && --level == 0) { close = i; break; }
        }
        var group = condition[(open + 1)..close];
        return System.Text.RegularExpressions.Regex.Matches(group, @"BOOL_(\w+)").Select(m => m.Groups[1].Value.Replace("_", "", StringComparison.Ordinal))
            .Where(v => v is not ("IsReady" or "IsAiming")).Distinct().ToList();
    }

    public static HipFireAnimations Load(string gameDir) => Load(gameDir, RigProfiles.Marine);

    /// <summary>A profile's hip-fire data (Ork O2).</summary>
    public static HipFireAnimations Load(string gameDir, RigProfile rig) => From(rig.LoadCharacter(gameDir), rig);

    /// <summary>The data from the marine rig's file (already read; <see cref="Load(string)"/> and <c>MpClasses.Load</c> share it).</summary>
    public static HipFireAnimations From(HavokCharacterFile character) => From(character, RigProfiles.Marine);

    /// <summary>The data from a profile's rig file (already read).</summary>
    public static HipFireAnimations From(HavokCharacterFile character, RigProfile rig)
    {
        var graphs = HavokBehaviorDetailReader.Read(character.Pack);
        var names = new HashSet<string>(character.AnimationNames, StringComparer.OrdinalIgnoreCase);
        return From(graphs, character.Skeleton.BoneNames,
            clip => character.LoadAnimation(clip).Animation.Duration,
            clip => names.Contains(clip) ? character.LoadBlendHint(clip) : null, rig.HipFire, rig.BaseGraph);
    }

    /// <summary>The data from already-read graphs (testable without an install).</summary>
    /// <param name="animationLength">Seconds of an animation by name.</param>
    /// <param name="blendHint">An animation's binding blend hint, null when unknown.</param>
    /// <param name="baseGraphName">The graph holding the weapon state machines (null: <see cref="BaseGraph"/>).</param>
    public static HipFireAnimations From(IReadOnlyList<BehaviorGraphDetail> graphs, IReadOnlyList<string> boneNames,
        Func<string, float> animationLength, Func<string, AnimationBlendHint?> blendHint, IEnumerable<ClassMap>? maps = null, string? baseGraphName = null)
    {
        string baseName = baseGraphName ?? BaseGraph;
        BehaviorGraphDetail? Graph(string name) => graphs.FirstOrDefault(g => g.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var sets = new List<HipFireSet>();
        foreach (var map in maps ?? DefaultMaps)
        {
            var notes = new List<string>();
            var nav = Graph(map.NavGraph);
            if (nav is null) { notes.Add($"graph {map.NavGraph} not found"); continue; }

            var directions = new List<AimDirection>();
            var navState = nav.AllStates.FirstOrDefault(x => x.State.Name == map.NavState);
            if (navState.State is null) notes.Add($"{map.NavGraph}: no state '{map.NavState}'");
            else if (FindBlend(nav, navState.State.Generator, "FLT_Nav_Motion_Angle") is not { } byDirection)
                notes.Add($"{map.NavGraph}/{map.NavState}: no blend on the movement direction");
            else
                foreach (var child in byDirection.Children)
                {
                    // The direction blend is cyclic: the back clips are listed at -180 and again at +180; keep the first.
                    float angle = child.Weight;
                    if (directions.Any(d => SameAngle(d.AngleDegrees, angle))) continue;
                    directions.Add(new AimDirection(angle, Bands(nav, child.Generator, animationLength, notes)));
                }

            // The recoil: the state named "Recoil" under the fire graph's root (or the base graph's fire state).
            var baseGraph = Graph(baseName);
            var fireGraph = Graph(map.FireGraph);
            var machine = baseGraph?.StateMachines.FirstOrDefault(m => m.Name == map.Machine);
            int? fireRoot = map.FireGraph.Equals(baseName, StringComparison.OrdinalIgnoreCase)
                ? machine?.States.FirstOrDefault(s => s.Name == map.FireState)?.Generator
                : fireGraph?.Root;
            RecoilClip? recoil = null;
            float? raise = null;
            if (fireGraph is null) notes.Add($"graph {map.FireGraph} not found");
            else
            {
                var states = StatesUnder(fireGraph, fireRoot).ToList();
                if (states.FirstOrDefault(s => s.Name == map.RecoilState) is { } recoilState && SequencerUnder(fireGraph, recoilState.Generator) is { } rs)
                    recoil = Recoil(rs.Sequencer, animationLength, blendHint);
                else notes.Add($"{map.FireGraph}: no '{map.RecoilState}' state with a sequencer");
                if (states.FirstOrDefault(s => s.Name.StartsWith("Set Weapon Ready", StringComparison.Ordinal)) is { } readyState
                    && SequencerUnder(fireGraph, readyState.Generator) is { } ready && ready.Sequencer.Animations.Count > 0)
                    raise = BranchStart(ready.Sequencer, "BRANCH_to_Fire", animationLength);
            }

            // The base graph's weapon state machine: blends between movement and shot, and the sprint-to-shot rule.
            float? navToFire = null, fireToNav = null;
            string? sprintToFire = null;
            IReadOnlyDictionary<string, float>? upperBody = null;
            if (baseGraph is null || machine is null) notes.Add($"{baseName}: no state machine '{map.Machine}'");
            else
            {
                BehaviorState? State(string name) => machine.States.FirstOrDefault(s => s.Name == name);
                if (State(map.NavigationState) is { } navigation && State(map.FireState) is { } fire)
                {
                    // A transition without a blending effect switches at once (0 s).
                    if (navigation.Transitions.FirstOrDefault(t => t.ToStateId == fire.Id) is { } n2f) navToFire = baseGraph.EffectAt(n2f.Transition)?.Duration ?? 0f;
                    if (fire.Transitions.FirstOrDefault(t => t.ToStateId == navigation.Id && t.BranchEvent is not null) is { } f2n) fireToNav = baseGraph.EffectAt(f2n.Transition)?.Duration ?? 0f;
                    if (State(map.SprintState) is { } sprint && sprint.Transitions.FirstOrDefault(t => t.ToStateId == fire.Id) is { } s2f)
                        sprintToFire = (s2f.Event is { } e ? "on " + e : "") + (s2f.BranchEvent is { } b ? $" in {b}" : "") + (s2f.ConditionText is { } c ? $" if {c}" : "");
                }
                else notes.Add($"{map.Machine}: states '{map.NavigationState}' / '{map.FireState}' not found");
            }
            // The family's reload blend (movement + reload, masked): its upper-body child's mask. In the base graph it is read only when the
            // family's machine is there (as before profiles); a graph of its own (the Ork's reload graph) is read on its own.
            var reloadGraph = map.ReloadBlendGraph is { } rg ? Graph(rg) : baseGraph is not null && machine is not null ? baseGraph : null;
            if (reloadGraph is not null && map.ReloadBlend is { } reloadName && reloadGraph.Blenders.FirstOrDefault(b => b.Name == reloadName) is { Children.Count: 2 } reload
                && reloadGraph.BoneWeightsAt(reload.Children[1].BoneWeights) is { } upper)
                upperBody = boneNames.Select((n, i) => (n, upper.WeightOf(i))).ToDictionary(x => x.n, x => x.Item2, StringComparer.Ordinal);

            // Posture: relaxed → ready in the navigation graph.
            string? readyCondition = null;
            float? toReady = null, toRelaxed = null;
            foreach (var m in nav.StateMachines)
            {
                foreach (var s in m.States.Where(s => s.Name.Contains("Relaxed", StringComparison.Ordinal)))
                    foreach (var t in s.Transitions)
                        if (m.States.FirstOrDefault(x => x.Id == t.ToStateId) is { } to && to.Name.Contains("Ready", StringComparison.Ordinal) && readyCondition is null)
                        {
                            readyCondition = t.ConditionText;
                            toReady = nav.EffectAt(t.Transition)?.Duration;
                            var back = to.Transitions.FirstOrDefault(r => m.States.FirstOrDefault(x => x.Id == r.ToStateId)?.Name.Contains("Relaxed", StringComparison.Ordinal) == true);
                            toRelaxed = back is null ? null : nav.EffectAt(back.Transition)?.Duration;
                        }
                if (readyCondition is not null) break;
            }

            sets.Add(new HipFireSet(map.Class, map.NavGraph, map.NavState, directions, map.FireGraph, recoil, raise, navToFire, fireToNav, sprintToFire,
                readyCondition, toReady, toRelaxed, FireMask: null, upperBody, notes));
        }
        return new HipFireAnimations(sets);
    }

    internal static bool SameAngle(float a, float b) => Math.Abs(((a - b) % 360 + 540) % 360 - 180) < 0.01f;

    static List<AimSpeedBand> Bands(BehaviorGraphDetail g, int? node, Func<string, float> length, List<string> notes)
    {
        var bands = new List<AimSpeedBand>();
        if (FindBlend(g, node, "FLT_Input_Nav_Speed") is { } bySpeed)
            foreach (var c in bySpeed.Children) bands.Add(new AimSpeedBand(c.Weight, Pitches(g, c.Generator, length, notes)));
        else bands.Add(new AimSpeedBand(float.NaN, Pitches(g, node, length, notes)));
        return bands;
    }

    static List<AimClip> Pitches(BehaviorGraphDetail g, int? node, Func<string, float> length, List<string> notes)
    {
        var clips = new List<AimClip>();
        if (FindBlend(g, node, "FLT_Aim_Vertical_Deg", maxDepth: 2) is { } byPitch)
        {
            foreach (var c in byPitch.Children)
                if (FirstClip(g, c.Generator) is { } clip) clips.Add(new AimClip(c.Weight, clip.Animation, length(clip.Animation)));
        }
        else if (FirstClip(g, node) is { } single) clips.Add(new AimClip(0f, single.Animation, length(single.Animation)));
        else notes.Add($"no clip under @{node}");
        return clips;
    }

    const int MaxDepth = 32;

    /// <summary>The first blend (depth first) whose blend parameter is bound to a variable starting with <paramref name="variablePrefix"/>.</summary>
    internal static BehaviorBlender? FindBlend(BehaviorGraphDetail g, int? node, string variablePrefix, int maxDepth = MaxDepth)
    {
        foreach (var at in Walk(g, node, maxDepth))
            if (g.Nodes[at].Bindings.TryGetValue("blendParameter", out var v) && v.StartsWith(variablePrefix, StringComparison.Ordinal)
                && g.BlenderAt(at) is { } b) return b;
        return null;
    }

    internal static BehaviorClip? FirstClip(BehaviorGraphDetail g, int? node) => Walk(g, node, MaxDepth).Select(g.ClipAt).FirstOrDefault(c => c is not null);

    static (int Offset, BehaviorSequencer Sequencer)? SequencerUnder(BehaviorGraphDetail g, int? node)
    {
        foreach (var at in Walk(g, node, MaxDepth))
            if (g.Sequencers.TryGetValue(at, out var s)) return (at, s);
        return null;
    }

    static IEnumerable<BehaviorState> StatesUnder(BehaviorGraphDetail g, int? node)
    {
        foreach (var at in Walk(g, node, MaxDepth))
            if (g.MachineAt(at) is { } m)
                foreach (var s in m.States) yield return s;
    }

    /// <summary>Nodes reachable from <paramref name="node"/>, depth first, each once.</summary>
    static IEnumerable<int> Walk(BehaviorGraphDetail g, int? node, int maxDepth)
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

    static RecoilClip Recoil(BehaviorSequencer s, Func<string, float> length, Func<string, AnimationBlendHint?> hint)
    {
        string clip = s.Animations.Count > 0 ? s.Animations[0] : "";
        float speed = s.PlaybackSpeeds.Count > 0 && s.PlaybackSpeeds[0] > 0 && float.IsFinite(s.PlaybackSpeeds[0]) ? s.PlaybackSpeeds[0] : 1f;
        float len = clip.Length > 0 ? length(clip) : 0f;
        float? Branch(params string[] names) => s.Tags.Where(t => t.Kind == "Branch" && names.Contains(t.Name)).Select(t => (float?)(t.Start * len / speed)).FirstOrDefault();
        return new RecoilClip(s.Name, clip, len, speed, clip.Length > 0 && hint(clip) == AnimationBlendHint.Additive,
            Branch("BRANCH_Shot_Finished", "BRANCH_Fire_End"), Branch("BRANCH_Refire"));
    }

    static float? BranchStart(BehaviorSequencer s, string branch, Func<string, float> length)
    {
        float speed = s.PlaybackSpeeds.Count > 0 && s.PlaybackSpeeds[0] > 0 ? s.PlaybackSpeeds[0] : 1f;
        return s.Tags.Where(t => t.Kind == "Branch" && t.Name == branch).Select(t => (float?)(t.Start * length(s.Animations[0]) / speed)).FirstOrDefault();
    }
}
