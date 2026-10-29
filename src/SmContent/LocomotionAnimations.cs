using SmFormats.Havok;

namespace SmContent;

/// <summary>One clip of a relaxed movement direction at one speed.</summary>
/// <param name="Speed">The blend weight on <c>FLT_Input_Nav_Speed_DAMPED</c>, units/s: 2 for the walk clips, 4.5 (sides and back) or 5.5 / 6.5
/// (forward and diagonals) for the run clips; the speed the clip is authored for.</param>
/// <param name="Length">Seconds (the clip at playback speed 1).</param>
/// <param name="SpeedBound">The clip's <c>playbackSpeed</c> is bound to a graph variable (<c>FLT_RunSpeedMultiplier</c> on the run clips).</param>
public sealed record LocoBand(float Speed, string Clip, float Length, bool SpeedBound);

/// <summary>One movement direction of the relaxed run: the walk and the run clip (the graph blends between them by the movement speed; below the walk it blends toward a walk-on-the-spot clip, not used here).</summary>
/// <param name="AngleDegrees">Degrees from the facing, 0 forward, +90 right, ±180 back (blend weight on <c>FLT_Nav_Motion_Angle_DAMPED</c>).</param>
public sealed record LocoDirection(float AngleDegrees, LocoBand? Walk, LocoBand? Run)
{
    /// <summary>
    /// The speed 0 band of this direction in the graph's speed blend: the walk-on-the-spot clip (<c>SM_NAV_WOS_F_01</c> forward on the rifle, <c>SM_1HR_NAV_WOS_F_01</c> on the pistol; the other directions
    /// of the rifle graph repeat it): the pose the blend gives a marine whose requested speed is between the idle's cut-off (0.5) and the walk's (2). Null when the graph has no such band.
    /// </summary>
    public LocoBand? WalkOnSpot { get; init; }

    /// <summary>"F", "FR", "R", "BR", "B", "BL", "L", "FL" for the eight 45° directions; the angle otherwise.</summary>
    public string Label => Math.Round(AngleDegrees) switch
    {
        0 => "F", 45 => "FR", 90 => "R", 135 => "BR", 180 or -180 => "B", -135 => "BL", -90 => "L", -45 => "FL",
        var a => $"{a}°",
    };
}

/// <summary>The relaxed (weapon lowered, not aiming) ground movement of a weapon family: eight directions with a walk and a run clip each.</summary>
public sealed record RelaxedMoveSet(HipFireWeaponClass Class, string Graph, string State, IReadOnlyList<LocoDirection> Directions);

/// <summary>The sprint clip at one blend angle.</summary>
/// <param name="AngleDegrees">Blend weight on <c>FLT_Nav_Motion_Angle_SPRINT_DAMP</c>: -45 (strafe left), 0 (forward), +45 (strafe right).</param>
public sealed record SprintClip(float AngleDegrees, string Clip, float Length);

/// <summary>The weapon groups whose sprint graphs differ: <c>MP_SM_Bolter_NuSprint</c> (bolters, pistols, plasma, melta, grenade launcher and the rest),
/// <c>SM_Heavy_Bolter_NuSprint</c> (heavy bolter), <c>SM_Lascannon_Plasma_NuSprint</c> (lascannon, plasma cannon); anim/melee-flow: <c>SM_TH_NuSprint</c> (the thunder hammer
/// and the daemon maul held in both hands: <c>MP_SM_MEL_ThunderHammer</c> "ThunderHammer Sprint").</summary>
public enum SprintFamily { Rifle, Heavy, Cannon, Hammer }

/// <summary>A sprint loop: a three-way blend over the movement angle (forward and a strafing clip to each side).</summary>
/// <param name="EnterBlend">Seconds: the blend from the navigation state into the sprint (the base graph's transition; 0.1 s, "Default 3 Frame Transition", read where the family has one).</param>
/// <param name="PlaybackVariable">The graph variable the clips' playback speed is bound to (<c>FLT_Sprint_Speed</c>), or null.</param>
public sealed record SprintSet(SprintFamily Family, string Graph, string State, IReadOnlyList<SprintClip> Clips, string? PlaybackVariable, float EnterBlend);

/// <summary>
/// Sprint and relaxed-run animation data from the marine rig's behaviour graphs (M36; <see cref="HavokBehaviorDetailReader"/>). The
/// relaxed run (<c>SM_Bolter_NAV</c> and <c>SM_Pistol_NAV</c>, state "Run Relaxed") blends over the movement direction
/// (<c>FLT_Nav_Motion_Angle_DAMPED</c>: eight directions, back included) and the speed (<c>FLT_Input_Nav_Speed_DAMPED</c>: walk at 2, run at
/// 4.5 or 6.5 units/s); the heavy graph has no relaxed state (its weapon is always ready). The sprint graphs (state "Sprint Loop")
/// blend three looping clips over <c>FLT_Nav_Motion_Angle_SPRINT_DAMP</c> (-45, 0, +45), so a sprint is forward only. Read-only; nothing is written.
/// </summary>
public sealed record LocomotionAnimations(IReadOnlyList<RelaxedMoveSet> Relaxed, IReadOnlyList<SprintSet> Sprints, IReadOnlyList<string> Notes)
{
    /// <summary>
    /// anim/melee-flow (the owner, 2026-10-29: "Assault Hammer running animation missing"): the thunder hammer's (and the daemon maul's) own navigation, <c>SM_TH_NAV</c> "Run" (eight directions,
    /// <c>SM_2HM_NAV_TH_MeleeWalk_*</c> at 2 and <c>SM_2HM_NAV_TH_MeleeRun_*</c> at 4.5 / 5.5 units/s), which the hammer graph's "ThunderHammer Nav" references: a marine carrying the two-handed hammer
    /// stands and runs with it in both hands whenever its pistol is not up (the base graph's "Melee - ThunderHammer" state). Its <see cref="RelaxedMoveSet.Class"/> carries no meaning. Null when not read.
    /// </summary>
    public RelaxedMoveSet? Hammer { get; init; }

    /// <summary>anim/melee-flow: the hammer navigation's idle (<c>SM_TH_NAV</c> "Idle": <c>SM_2HM_NAV_TH_IdleL_S_01</c>); null when not read.</summary>
    public string? HammerIdle { get; init; }

    /// <summary>anim/melee-flow: where the hammer's navigation sits (<see cref="Hammer"/>).</summary>
    public static readonly RelaxedMap HammerRelaxed = new(HipFireWeaponClass.Pistol, "SM_TH_NAV", "Run");
    /// <summary>anim/melee-flow: the hammer navigation's standing state (<see cref="HammerIdle"/>).</summary>
    public const string HammerIdleState = "Idle";

    /// <summary>Where a family's relaxed run sits in the graphs.</summary>
    public sealed record RelaxedMap(HipFireWeaponClass Class, string Graph, string State)
    {
        /// <summary>
        /// A speed band that is itself an aim-pitch blend (the Ork's 2HR aim run, which has no relaxed stance) gives its level clip (the
        /// child nearest 0°) instead of the first clip under it. False (the marine): the first clip, as the relaxed clips are plain.
        /// </summary>
        public bool PreferLevelPitch { get; init; }
    }

    /// <summary>Where a sprint family's loop sits: its graph and state, and the base graph's navigation and sprint states of one weapon for the entry blend.</summary>
    public sealed record SprintMap(SprintFamily Family, string Graph, string State, string Machine, string NavigationState, string SprintState)
    {
        /// <summary>
        /// The sprint is a single forward clip, not a blend over the angle (the Ork: a blend over the speed, run 6 → sprint 9.5): the
        /// fastest clip of the state's speed blend (or its only clip) is the 0° clip.
        /// </summary>
        public bool ForwardOnly { get; init; }
        /// <summary>The graph holding <see cref="Machine"/> for the entry blend; null: the base graph (the marine).</summary>
        public string? TransitionGraph { get; init; }
    }

    public static readonly RelaxedMap[] DefaultRelaxed =
    [
        new(HipFireWeaponClass.Rifle, "SM_Bolter_NAV", "Run Relaxed"),
        new(HipFireWeaponClass.Pistol, "SM_Pistol_NAV", "Run Relaxed"),
    ];

    public static readonly SprintMap[] DefaultSprints =
    [
        new(SprintFamily.Rifle, "MP_SM_Bolter_NuSprint", "Sprint Loop", "BolterBehavior", "Bolter Navigation", "Bolter Sprint"),
        new(SprintFamily.Heavy, "SM_Heavy_Bolter_NuSprint", "Sprint Loop", "Behavior_RangedHeavyBolter", "Heavy Bolter Nav", "Heavy Bolter Sprint"),
        new(SprintFamily.Cannon, "SM_Lascannon_Plasma_NuSprint", "Sprint Loop", "", "", ""),
        // anim/melee-flow: the hammer's sprint (SM_NAV_TH_Sprint_*); the hammer graph enters it from "ThunderHammer Nav" over 'Blend Three Frame' 0.1 s, the default blend (as the cannons, no base-graph lookup).
        new(SprintFamily.Hammer, "SM_TH_NuSprint", "Sprint Loop", "", "", ""),
    ];

    /// <summary>The blend into a sprint when the graph names none (the base graph's "Default 3 Frame Transition": 3 frames at 30 fps).</summary>
    public const float DefaultEnterBlend = 0.1f;

    public RelaxedMoveSet? RelaxedFor(HipFireWeaponClass weaponClass) => Relaxed.FirstOrDefault(s => s.Class == weaponClass);

    public SprintSet? SprintFor(SprintFamily family) => Sprints.FirstOrDefault(s => s.Family == family);

    /// <summary>
    /// The sprint family of a weapon, by its wargear name: heavy bolter, lascannon and plasma cannon have their own sprint graphs
    /// (<c>SM_Base_MP</c> "Heavy Bolter Sprint", "PlasmaCannon Sprint" and <c>MP_SM_LasCannon</c> "LasCannon Sprint"); every other weapon
    /// (the bolt pistol's "BoltPistol Sprint" included) references <c>MP_SM_Bolter_NuSprint</c>.
    /// </summary>
    public static SprintFamily SprintFamilyOf(string weaponName) =>
        weaponName.Contains("heavy_bolter", StringComparison.OrdinalIgnoreCase) ? SprintFamily.Heavy
        : weaponName.Contains("las_cannon", StringComparison.OrdinalIgnoreCase) || weaponName.Contains("lascannon", StringComparison.OrdinalIgnoreCase)
          || weaponName.Contains("plasma_cannon", StringComparison.OrdinalIgnoreCase) ? SprintFamily.Cannon
        : SprintFamily.Rifle;

    public static LocomotionAnimations Load(string gameDir) => Load(gameDir, RigProfiles.Marine);

    /// <summary>A profile's locomotion (Ork O2).</summary>
    public static LocomotionAnimations Load(string gameDir, RigProfile rig) => From(rig.LoadCharacter(gameDir), rig);

    /// <summary>The data from the marine rig's file (already read).</summary>
    public static LocomotionAnimations From(HavokCharacterFile character) => From(character, RigProfiles.Marine);

    /// <summary>The data from a profile's rig file (already read).</summary>
    public static LocomotionAnimations From(HavokCharacterFile character, RigProfile rig)
    {
        var graphs = HavokBehaviorDetailReader.Read(character.Pack);
        var result = From(graphs, clip => character.LoadAnimation(clip).Animation.Duration, rig.Relaxed, rig.Sprints, rig.BaseGraph);
        return ReferenceEquals(rig, RigProfiles.Marine) ? WithHammer(result, graphs, clip => character.LoadAnimation(clip).Animation.Duration) : result;
    }

    /// <summary>
    /// anim/melee-flow: <paramref name="data"/> with the hammer's navigation (<see cref="Hammer"/>, <see cref="HammerIdle"/>) read from <paramref name="graphs"/>; unchanged but for a note when
    /// the graph or a state is missing. Testable without an install.
    /// </summary>
    public static LocomotionAnimations WithHammer(LocomotionAnimations data, IReadOnlyList<BehaviorGraphDetail> graphs, Func<string, float> animationLength)
    {
        var read = From(graphs, animationLength, [HammerRelaxed], [], null);
        var notes = data.Notes.Concat(read.Notes.Select(n => "hammer navigation: " + n)).ToList();
        string? idle = null;
        if (graphs.FirstOrDefault(g => g.Name.Equals(HammerRelaxed.Graph, StringComparison.OrdinalIgnoreCase)) is { } nav
            && nav.AllStates.FirstOrDefault(x => x.State.Name == HammerIdleState).State is { } idleState)
            idle = HipFireAnimations.FirstClip(nav, idleState.Generator)?.Animation;
        if (idle is null) notes.Add($"hammer navigation: no '{HammerIdleState}' clip in {HammerRelaxed.Graph}");
        return data with { Hammer = read.Relaxed.FirstOrDefault(), HammerIdle = idle, Notes = notes };
    }

    /// <summary>The data from already-read graphs (testable without an install).</summary>
    /// <param name="baseGraphName">The graph holding the sprint maps' machines (null: <see cref="HipFireAnimations.BaseGraph"/>).</param>
    public static LocomotionAnimations From(IReadOnlyList<BehaviorGraphDetail> graphs, Func<string, float> animationLength,
        IEnumerable<RelaxedMap>? relaxedMaps = null, IEnumerable<SprintMap>? sprintMaps = null, string? baseGraphName = null)
    {
        BehaviorGraphDetail? Graph(string name) => graphs.FirstOrDefault(g => g.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var notes = new List<string>();
        var relaxed = new List<RelaxedMoveSet>();
        foreach (var map in relaxedMaps ?? DefaultRelaxed)
        {
            if (Graph(map.Graph) is not { } nav) { notes.Add($"graph {map.Graph} not found"); continue; }
            var state = nav.AllStates.FirstOrDefault(x => x.State.Name == map.State).State;
            if (state is null) { notes.Add($"{map.Graph}: no state '{map.State}'"); continue; }
            if (HipFireAnimations.FindBlend(nav, state.Generator, "FLT_Nav_Motion_Angle") is not { } byDirection)
            {
                notes.Add($"{map.Graph}/{map.State}: no blend on the movement direction");
                continue;
            }
            var directions = new List<LocoDirection>();
            foreach (var child in byDirection.Children)
            {
                // The direction blend is cyclic: the back clips are listed at -180 and again at +180; keep the first.
                if (directions.Any(d => HipFireAnimations.SameAngle(d.AngleDegrees, child.Weight))) continue;
                var bands = new List<LocoBand>();
                if (HipFireAnimations.FindBlend(nav, child.Generator, "FLT_Input_Nav_Speed") is { } bySpeed)
                    foreach (var s in bySpeed.Children)
                        if ((map.PreferLevelPitch ? LevelClip(nav, s.Generator) : HipFireAnimations.FirstClip(nav, s.Generator)) is { } clip)
                            bands.Add(new LocoBand(s.Weight, clip.Animation, animationLength(clip.Animation), nav.Nodes.TryGetValue(clip.Offset, out var n) && n.Bindings.ContainsKey("playbackSpeed")));
                // The speed 0 band is the walk-on-the-spot clip (it steps in place: LocoDirection.WalkOnSpot); walk and run are the moving bands.
                var moving = bands.Where(b => b.Speed > 0f).ToList();
                directions.Add(new LocoDirection(child.Weight,
                    moving.Count >= 2 ? moving.MinBy(b => b.Speed) : null,
                    moving.Count > 0 ? moving.MaxBy(b => b.Speed) : null) { WalkOnSpot = bands.FirstOrDefault(b => b.Speed <= 0f) });
            }
            relaxed.Add(new RelaxedMoveSet(map.Class, map.Graph, map.State, directions));
        }

        var defaultBaseGraph = Graph(baseGraphName ?? HipFireAnimations.BaseGraph);
        var sprints = new List<SprintSet>();
        foreach (var map in sprintMaps ?? DefaultSprints)
        {
            if (Graph(map.Graph) is not { } g) { notes.Add($"graph {map.Graph} not found"); continue; }
            var state = g.AllStates.FirstOrDefault(x => x.State.Name == map.State).State;
            if (state is null) { notes.Add($"{map.Graph}: no state '{map.State}'"); continue; }
            var clips = new List<SprintClip>();
            string? variable = null;
            void Add(float angle, BehaviorClip clip)
            {
                clips.Add(new SprintClip(angle, clip.Animation, animationLength(clip.Animation)));
                if (variable is null && g.Nodes.TryGetValue(clip.Offset, out var n) && n.Bindings.TryGetValue("playbackSpeed", out var v)) variable = v;
            }
            if (map.ForwardOnly)
            {
                // One forward clip: the fastest band of a speed blend, else the state's first clip.
                var fastest = HipFireAnimations.FindBlend(g, state.Generator, "FLT_Input_Nav_Speed") is { Children.Count: > 0 } bySpeed
                    ? HipFireAnimations.FirstClip(g, bySpeed.Children.MaxBy(c => c.Weight)!.Generator)
                    : HipFireAnimations.FirstClip(g, state.Generator);
                if (fastest is null) { notes.Add($"{map.Graph}/{map.State}: no sprint clip"); continue; }
                Add(0f, fastest);
            }
            else
            {
                if (HipFireAnimations.FindBlend(g, state.Generator, "FLT_Nav_Motion_Angle") is not { } byAngle)
                {
                    notes.Add($"{map.Graph}/{map.State}: no blend on the sprint angle");
                    continue;
                }
                foreach (var child in byAngle.Children.OrderBy(c => c.Weight))
                    if (HipFireAnimations.FirstClip(g, child.Generator) is { } clip) Add(child.Weight, clip);
            }
            // The blend from the navigation state into the sprint: the base graph's transition of the weapon's machine (cannons: the default).
            float enter = DefaultEnterBlend;
            var baseGraph = map.TransitionGraph is { } own ? Graph(own) : defaultBaseGraph;
            var machine = baseGraph?.StateMachines.FirstOrDefault(m => m.Name == map.Machine);
            if (baseGraph is not null && machine is not null
                && machine.States.FirstOrDefault(s => s.Name == map.NavigationState) is { } navigation
                && machine.States.FirstOrDefault(s => s.Name == map.SprintState) is { } sprint
                && navigation.Transitions.FirstOrDefault(t => t.ToStateId == sprint.Id) is { } t2s && baseGraph.EffectAt(t2s.Transition) is { } effect)
                enter = effect.Duration;
            // A rig that names its own transition graph (the Ork) is told when it falls back; the marine's cannons fall back silently, as before.
            else if (map.TransitionGraph is { } named)
                notes.Add($"{map.Graph}/{map.State}: no blending transition '{map.NavigationState}' → '{map.SprintState}' in {named}/{map.Machine}; the default {DefaultEnterBlend} s blend in");
            sprints.Add(new SprintSet(map.Family, map.Graph, map.State, clips, variable, enter));
        }
        return new LocomotionAnimations(relaxed, sprints, notes);
    }

    /// <summary>The level-aim clip under a node: the child of an aim-pitch blend nearest 0° (else the first clip).</summary>
    static BehaviorClip? LevelClip(BehaviorGraphDetail g, int? node) =>
        HipFireAnimations.FindBlend(g, node, "FLT_Aim_Vertical_Deg", maxDepth: 2) is { Children.Count: > 0 } byPitch
            ? HipFireAnimations.FirstClip(g, byPitch.Children.MinBy(c => MathF.Abs(c.Weight))!.Generator)
            : HipFireAnimations.FirstClip(g, node);
}
