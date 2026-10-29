using System.Globalization;
using System.Text.RegularExpressions;
using SmFormats.Havok;

namespace SmContent;

/// <summary>One column of an aim-offset idle: the clips for the aim pitch at one horizontal aim angle against the body (degrees, negative left, 0 straight ahead, positive right).</summary>
public sealed record AimOffsetColumn(float HorizontalDegrees, IReadOnlyList<AimClip> Pitches);

/// <summary>
/// The standing aim pose of a posture: the columns of the graph's blend on <c>FLT_Aim_Horizontal_Deg</c> (the rifle: left, ahead, right at -90 / 0 / +90, each a
/// blend on <c>FLT_Aim_Vertical_Deg</c>), or one column of pitch clips with the twist made by a <c>hkbTwistModifier</c> bound to the horizontal angle (the pistol
/// and the heavy bolter: <paramref name="ProceduralTwist"/>).
/// </summary>
public sealed record IdleAimSet(IReadOnlyList<AimOffsetColumn> Columns, bool ProceduralTwist);

/// <summary>A turn-in-place clip of a posture.</summary>
/// <param name="YawDegrees">The turn the clip's extracted root motion makes over its whole length, degrees, + counter-clockwise seen from above (a left turn): 135 for <c>SM_NAV_AimTurn_Left</c>.</param>
/// <param name="EndFraction">Start of the clip's <c>BRANCH_Turn_End</c> window (0 to 1): the turn can end, or restart, from there. 1 when the clip has none.</param>
/// <param name="LegMask">The turn child's bone mask in the state's blend: the pelvis and legs at 1 (the rest of the body follows the aim-offset idle). Null without one.</param>
/// <param name="RootYawDegrees">The extracted root rotation, degrees, evenly spaced over <paramref name="RootDuration"/> seconds (the first sample at 0; linear for the rifle's clip, eased for the heavy bolter's).</param>
public sealed record TurnClip(string Clip, float Length, float YawDegrees, float EndFraction, IReadOnlyDictionary<string, float>? LegMask, IReadOnlyList<float> RootYawDegrees, float RootDuration)
{
    /// <summary>The root rotation after <paramref name="time"/> seconds of the clip, degrees (linear between the samples, held after the last).</summary>
    public float YawAt(float time) => StartClip.Sample(RootYawDegrees, RootDuration, time);
}

/// <summary>
/// A standing posture's turn in place (<paramref name="Name"/>: relaxed or ready). The marine stands on the idle pose and turns when the aim is more than
/// <paramref name="EnterDegrees"/> off its body: the graph's transition from <paramref name="IdleState"/> to the turn states (<c>FLT_Aim_Horizontal_Deg(_DAMPED)</c> in
/// [-180, -90] or [90, 180] for the rifle, [-180, -75] and [75, 180] for the pistol and the heavy bolter).
/// </summary>
/// <param name="TurnBlend">The blend into a turn and from turn to turn, seconds (the transition effect of <paramref name="IdleState"/> to the turn state; 0.1 s).</param>
/// <param name="StopBlend">The blend from the posture's run to its idle when the marine stops, seconds (0.333 relaxed, 0.15 for the rifle's ready run, 0.333 for the pistol and the heavy bolter; <see cref="TurnAnimations.DefaultStopBlend"/> when the graph has none).</param>
public sealed record TurnPosture(string Name, string Graph, string IdleState, float EnterDegrees, IdleAimSet Idle, TurnClip? Left, TurnClip? Right, float TurnBlend, float StopBlend);

/// <summary>
/// The clip that starts a run from standing (<c>SM_TRN_BO_IdleAim_to_RunF</c>, the graphs' "Idle Relaxed To Run Relaxed" / "Idle Ready To Run Ready" states).
/// </summary>
/// <param name="EnterSpeed">The requested speed from which the idle goes to the start state instead of straight to the run (<c>FLT_Input_Nav_Speed_LOGIC</c> in [3, ...]), units/s.</param>
/// <param name="BlendIn">Idle into the start state, seconds ('Idle To Nav').</param>
/// <param name="BlendOut">From the start state's end (<c>BRANCH_Idle_To_Run_End</c>) into the run, seconds.</param>
/// <param name="BlendAbort">From the start state back to the idle when the speed drops, seconds.</param>
/// <param name="RootForward">The clip's extracted motion along the facing, metres, evenly spaced over <paramref name="RootDuration"/> (the first sample at 0).</param>
public sealed record StartClip(string Graph, string State, string Clip, float Length, float EnterSpeed, float BlendIn, float BlendOut, float BlendAbort, float EndFraction,
    IReadOnlyList<float> RootForward, float RootDuration)
{
    /// <summary>Metres of travel along the facing at <paramref name="time"/> seconds into the clip (linear between the samples, held after the last).</summary>
    public float DistanceAt(float time) => Sample(RootForward, RootDuration, time);

    /// <summary>The clip time at which it has travelled <paramref name="distance"/> metres (0 at or below the start; the last sample's time at or beyond the total).</summary>
    public float TimeAtDistance(float distance) => Invert(RootForward, RootDuration, distance);

    internal static float Sample(IReadOnlyList<float> samples, float duration, float time)
    {
        if (samples.Count == 0 || duration <= 0f) return 0f;
        if (samples.Count == 1 || time <= 0f) return samples[0];
        if (time >= duration) return samples[^1];
        float position = time / duration * (samples.Count - 1);
        int i = (int)position;
        float f = position - i;
        return samples[i] + (samples[Math.Min(i + 1, samples.Count - 1)] - samples[i]) * f;
    }

    internal static float Invert(IReadOnlyList<float> samples, float duration, float distance)
    {
        if (samples.Count < 2 || duration <= 0f) return 0f;
        if (distance <= samples[0]) return 0f;
        for (int i = 1; i < samples.Count; i++)
        {
            if (samples[i] < distance) continue;
            float span = samples[i] - samples[i - 1];
            float f = span > 1e-6f ? (distance - samples[i - 1]) / span : 0f;
            return (i - 1 + f) / (samples.Count - 1) * duration;
        }
        return duration;
    }
}

/// <summary>A weapon family's standing and starting animations (see <see cref="TurnAnimations"/>).</summary>
public sealed record TurnSet(HipFireWeaponClass Class, TurnPosture? Relaxed, TurnPosture? Ready, StartClip? RelaxedStart, StartClip? ReadyStart);

/// <summary>
/// Turn-in-place, aim-offset idle and run-start animation data from the marine rig's navigation graphs (M40-T5; <see cref="HavokBehaviorDetailReader"/>). Read-only.
/// <list type="bullet">
/// <item>The rifle's standing pose ("Idle Relaxed Point" relaxed, "Idle" ready; the same clips) blends 3 x 4 clips: the horizontal angle between the aim and
/// the body (<c>FLT_Aim_Horizontal_Deg_DAMPED</c>: left clips at -90, <c>SM_NAV_IdleL_S_Aim_F_01</c> ahead, right clips at +90) times the pitch (-80 down to +80 up). The
/// pistol's "Twist Idle" and the heavy bolter's "Heavy Idle" have the pitch clips only and a <c>hkbTwistModifier</c> bound to the horizontal angle.</item>
/// <item>When the aim is more than the threshold off the body (90 degrees; 75 for the pistol and the heavy bolter) the graph plays <c>SM_NAV_AimTurn_Left</c> /
/// <c>_Right</c> (0.833 s, the extracted root motion turns the body 135 degrees) on the pelvis and legs (the turn child's bone mask) while the rest follows the aim-offset idle;
/// the <c>BRANCH_Turn_End</c> window opens at 0.92 of the clip and takes the next turn or the idle.</item>
/// <item>From standing the rifle goes to <c>SM_TRN_BO_IdleAim_to_RunF</c> (0.7 s; its root motion carries the body 4.4 m) when the requested speed is 3 or more, and to the
/// run after <c>BRANCH_Idle_To_Run_End</c>; the pistol and the heavy bolter blend straight into the run.</item>
/// </list>
/// </summary>
public sealed record TurnAnimations(IReadOnlyList<TurnSet> Sets, IReadOnlyList<string> Notes)
{
    /// <summary>Where a posture's states sit in a navigation graph.</summary>
    /// <param name="RunState">The posture's run state and <paramref name="IdleParent"/> the state that holds the idle (the run leaves for it when the marine stops); null: no stop blend is read.</param>
    public sealed record PostureMap(HipFireWeaponClass Class, bool Ready, string Graph, string IdleState, string LeftState, string RightState, string? RunState = null, string? IdleParent = null);

    /// <summary>Where a family's run start sits: the idle state it leaves, the start state and the run state it leads to.</summary>
    public sealed record StartMap(HipFireWeaponClass Class, bool Ready, string Graph, string IdleState, string StartState, string RunState);

    public static readonly PostureMap[] DefaultPostures =
    [
        new(HipFireWeaponClass.Rifle, false, "SM_Bolter_NAV", "Idle Relaxed Point", "Idle Relaxed Turn Left", "Idle Relaxed Turn Right", "Run Relaxed", "Idle Relaxed"),
        new(HipFireWeaponClass.Rifle, true, "SM_Bolter_NAV", "Idle", "Idle Turn Left", "Idle Turn Right", "Run Ready", "Idle Ready"),
        new(HipFireWeaponClass.Pistol, true, "SM_Pistol_NAV", "Twist Idle", "Idle Turn Left", "Idle Turn Right", "Run", "Idle"),
        new(HipFireWeaponClass.Heavy, true, "SM_TB_NAV", "Heavy Idle", "Idle Turn Left", "Idle Turn Right", "Heavy Run", "Idle Ready"),
    ];

    public static readonly StartMap[] DefaultStarts =
    [
        new(HipFireWeaponClass.Rifle, false, "SM_Bolter_NAV", "Idle Relaxed", "Idle Relaxed To Run Relaxed", "Run Relaxed"),
        new(HipFireWeaponClass.Rifle, true, "SM_Bolter_NAV", "Idle Ready", "Idle Ready To Run Ready", "Run Ready"),
    ];

    /// <summary>The blend the graphs use between standing states when a transition names none (the base graph's "Default 3 Frame Transition").</summary>
    public const float DefaultBlend = 0.1f;

    /// <summary>The stop blend when the graph's transition cannot be read (the relaxed run's "Nav to Idle").</summary>
    public const float DefaultStopBlend = 0.333f;

    public TurnSet? For(HipFireWeaponClass weaponClass) => Sets.FirstOrDefault(s => s.Class == weaponClass);

    public static TurnAnimations Load(string gameDir) => Load(gameDir, RigProfiles.Marine);

    public static TurnAnimations Load(string gameDir, RigProfile rig) => From(rig.LoadCharacter(gameDir));

    /// <summary>The data from the marine rig's file (already read).</summary>
    public static TurnAnimations From(HavokCharacterFile character)
    {
        var graphs = HavokBehaviorDetailReader.Read(character.Pack);
        return From(graphs, character.Skeleton.BoneNames, clip => character.LoadAnimation(clip).Animation.Duration,
            clip => { try { return character.LoadExtractedMotion(clip); } catch (Exception ex) when (ex is KeyNotFoundException or NotSupportedException or InvalidDataException) { return null; } });
    }

    /// <summary>The data from already-read graphs (testable without an install).</summary>
    public static TurnAnimations From(IReadOnlyList<BehaviorGraphDetail> graphs, IReadOnlyList<string> boneNames, Func<string, float> animationLength,
        Func<string, HavokExtractedMotion?> motion, IEnumerable<PostureMap>? postureMaps = null, IEnumerable<StartMap>? startMaps = null)
    {
        var notes = new List<string>();
        BehaviorGraphDetail? Graph(string name) => graphs.FirstOrDefault(g => g.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var postures = new Dictionary<(HipFireWeaponClass, bool), TurnPosture>();
        foreach (var map in postureMaps ?? DefaultPostures)
        {
            if (Graph(map.Graph) is not { } g) { notes.Add($"graph {map.Graph} not found"); continue; }
            var idle = g.AllStates.FirstOrDefault(x => x.State.Name == map.IdleState);
            if (idle.State is null) { notes.Add($"{map.Graph}: no state '{map.IdleState}'"); continue; }
            var aim = ReadIdle(g, idle.State, animationLength, notes, $"{map.Graph}/{map.IdleState}");
            if (aim is null) continue;
            var left = g.AllStates.FirstOrDefault(x => x.State.Name == map.LeftState).State;
            var right = g.AllStates.FirstOrDefault(x => x.State.Name == map.RightState).State;
            TurnClip? leftClip = left is null ? null : ReadTurn(g, left, boneNames, animationLength, motion, notes, $"{map.Graph}/{map.LeftState}");
            TurnClip? rightClip = right is null ? null : ReadTurn(g, right, boneNames, animationLength, motion, notes, $"{map.Graph}/{map.RightState}");
            if (left is null || right is null) notes.Add($"{map.Graph}: no turn states '{map.LeftState}' / '{map.RightState}'");
            // The twist modifier of the pistol and the heavy bolter sits in the idle state or in the turn states' upper-body blend.
            bool twist = aim.ProceduralTwist || (left is not null && Walk(g, left.Generator).Any(at => IsTwist(g, at)));
            if (twist != aim.ProceduralTwist) aim = aim with { ProceduralTwist = twist };
            float enter = EnterDegrees(g, idle.Machine, idle.State, left, right, notes, $"{map.Graph}/{map.IdleState}");
            float blend = idle.State.Transitions.Where(t => left is not null && t.ToStateId == left.Id).Select(t => g.EffectAt(t.Transition)?.Duration).FirstOrDefault(d => d is not null) ?? DefaultBlend;
            float stop = DefaultStopBlend;
            if (map.RunState is not null && map.IdleParent is not null)
            {
                var run = g.AllStates.FirstOrDefault(x => x.State.Name == map.RunState);
                var parent = run.Machine?.States.FirstOrDefault(x => x.Name == map.IdleParent);
                if (run.State is not null && parent is not null && run.State.Transitions.FirstOrDefault(t => t.ToStateId == parent.Id) is { } leave && g.EffectAt(leave.Transition) is { } effect) stop = effect.Duration;
                else notes.Add($"{map.Graph}: no stop transition '{map.RunState}' -> '{map.IdleParent}'; {DefaultStopBlend} s");
            }
            postures[(map.Class, map.Ready)] = new TurnPosture(map.Ready ? "ready" : "relaxed", map.Graph, map.IdleState, enter, aim, leftClip, rightClip, blend, stop);
        }

        var starts = new Dictionary<(HipFireWeaponClass, bool), StartClip>();
        foreach (var map in startMaps ?? DefaultStarts)
        {
            if (Graph(map.Graph) is not { } g) { notes.Add($"graph {map.Graph} not found"); continue; }
            var start = g.AllStates.FirstOrDefault(x => x.State.Name == map.StartState);
            var idle = start.Machine?.States.FirstOrDefault(s => s.Name == map.IdleState);
            var run = start.Machine?.States.FirstOrDefault(s => s.Name == map.RunState);
            if (start.State is null || idle is null || run is null) { notes.Add($"{map.Graph}: no run start '{map.IdleState}' -> '{map.StartState}' -> '{map.RunState}'"); continue; }
            var seq = SequencerUnder(g, start.State.Generator);
            if (seq is null || seq.Animations.Count == 0) { notes.Add($"{map.Graph}/{map.StartState}: no clip"); continue; }
            string clip = seq.Animations[0];
            float speed = seq.PlaybackSpeeds.Count > 0 && seq.PlaybackSpeeds[0] > 0f ? seq.PlaybackSpeeds[0] : 1f;
            float length = animationLength(clip) / speed;
            var m = motion(clip);
            if (m is null || m.Samples.Count < 2 || m.Duration <= 0f) { notes.Add($"{map.Graph}/{map.StartState}: clip {clip} has no extracted motion"); continue; }
            // The idle leaves for the start state when the requested speed is high enough (the transition with the start state as its target).
            var into = idle.Transitions.FirstOrDefault(t => t.ToStateId == start.State.Id);
            float enterSpeed = into?.ConditionText is { } cond && SpeedFrom(cond) is { } sp ? sp : 3f;
            float blendIn = into is null ? DefaultBlend : g.EffectAt(into.Transition)?.Duration ?? 0f;
            var toRun = start.State.Transitions.FirstOrDefault(t => t.ToStateId == run.Id && t.BranchEvent == "BRANCH_Idle_To_Run_End");
            var abort = start.State.Transitions.FirstOrDefault(t => t.ToStateId == idle.Id);
            float blendOut = toRun is null ? DefaultBlend : g.EffectAt(toRun.Transition)?.Duration ?? 0f;
            float blendAbort = abort is null ? DefaultBlend : g.EffectAt(abort.Transition)?.Duration ?? 0f;
            float end = seq.Tags.Where(t => t.Kind == "Branch" && t.Name == "BRANCH_Idle_To_Run_End").Select(t => t.Start).DefaultIfEmpty(1f).Min();
            starts[(map.Class, map.Ready)] = new StartClip(map.Graph, map.StartState, clip, length, enterSpeed, blendIn, blendOut, blendAbort, end,
                m.Samples.Select(s => s.Y).ToArray(), m.Duration);
        }

        var sets = new List<TurnSet>();
        foreach (var family in postures.Keys.Select(k => k.Item1).Concat(starts.Keys.Select(k => k.Item1)).Distinct())
            sets.Add(new TurnSet(family, postures.GetValueOrDefault((family, false)), postures.GetValueOrDefault((family, true)),
                starts.GetValueOrDefault((family, false)), starts.GetValueOrDefault((family, true))));
        return new TurnAnimations(sets, notes);
    }

    static IdleAimSet? ReadIdle(BehaviorGraphDetail g, BehaviorState state, Func<string, float> length, List<string> notes, string where)
    {
        bool twist = state.Name.Contains("Twist", StringComparison.Ordinal) || Walk(g, state.Generator).Any(at => IsTwist(g, at));   // the pistol's "Twist Idle" holds its modifier in a list the walk does not reach
        var columns = new List<AimOffsetColumn>();
        if (HipFireAnimations.FindBlend(g, state.Generator, "FLT_Aim_Horizontal_Deg") is { } byAngle)
        {
            foreach (var child in byAngle.Children.OrderBy(c => c.Weight))
                columns.Add(new AimOffsetColumn(child.Weight, PitchClips(g, child.Generator, length)));
        }
        else if (HipFireAnimations.FindBlend(g, state.Generator, "FLT_Aim_Vertical_Deg") is not null)
            columns.Add(new AimOffsetColumn(0f, PitchClips(g, state.Generator, length)));
        columns.RemoveAll(c => c.Pitches.Count == 0);
        if (columns.Count == 0) { notes.Add($"{where}: no aim-offset idle clips"); return null; }
        return new IdleAimSet(columns, twist);
    }

    static List<AimClip> PitchClips(BehaviorGraphDetail g, int? node, Func<string, float> length)
    {
        var clips = new List<AimClip>();
        if (HipFireAnimations.FindBlend(g, node, "FLT_Aim_Vertical_Deg", maxDepth: 2) is { } byPitch)
        {
            foreach (var c in byPitch.Children.OrderBy(c => c.Weight))
                if (HipFireAnimations.FirstClip(g, c.Generator) is { } clip) clips.Add(new AimClip(c.Weight, clip.Animation, length(clip.Animation)));
        }
        else if (HipFireAnimations.FirstClip(g, node) is { } single) clips.Add(new AimClip(0f, single.Animation, length(single.Animation)));
        return clips;
    }

    static TurnClip? ReadTurn(BehaviorGraphDetail g, BehaviorState state, IReadOnlyList<string> boneNames, Func<string, float> length, Func<string, HavokExtractedMotion?> motion,
        List<string> notes, string where)
    {
        foreach (var at in Walk(g, state.Generator))
        {
            if (g.BlenderAt(at) is not { } blend) continue;
            foreach (var child in blend.Children)
            {
                if (SequencerUnder(g, child.Generator) is not { } seq || seq.Animations.Count == 0 || !seq.Animations[0].Contains("Turn", StringComparison.OrdinalIgnoreCase)) continue;
                string clip = seq.Animations[0];
                float speed = seq.PlaybackSpeeds.Count > 0 && seq.PlaybackSpeeds[0] > 0f ? seq.PlaybackSpeeds[0] : 1f;
                var m = motion(clip);
                float yaw = m is { Samples.Count: > 0 } ? m.Samples[^1].W * 180f / MathF.PI : 0f;
                if (m is null || MathF.Abs(yaw) < 1f || m.Duration <= 0f) { notes.Add($"{where}: turn clip {clip} has no root rotation"); return null; }
                float end = seq.Tags.Where(t => t.Kind == "Branch" && t.Name == "BRANCH_Turn_End").Select(t => t.Start).DefaultIfEmpty(1f).Min();
                IReadOnlyDictionary<string, float>? mask = null;
                if (g.BoneWeightsAt(child.BoneWeights) is { } weights)
                {
                    var dict = new Dictionary<string, float>(StringComparer.Ordinal);
                    for (int i = 0; i < boneNames.Count; i++) dict[boneNames[i]] = weights.WeightOf(i);
                    mask = dict;
                }
                return new TurnClip(clip, length(clip) / speed, yaw, end, mask, m.Samples.Select(x => x.W * 180f / MathF.PI).ToArray(), m.Duration / speed);
            }
        }
        notes.Add($"{where}: no turn clip");
        return null;
    }

    /// <summary>The smaller angle among the idle's transitions into the turn states: the aim-to-body angle beyond which the graph turns (90 for the rifle, 75 for the pistol and the heavy bolter).</summary>
    static float EnterDegrees(BehaviorGraphDetail g, BehaviorStateMachine machine, BehaviorState idle, BehaviorState? left, BehaviorState? right, List<string> notes, string where)
    {
        var bounds = new List<float>();
        foreach (var t in idle.Transitions.Where(t => (left is not null && t.ToStateId == left.Id) || (right is not null && t.ToStateId == right.Id)))
            if (t.ConditionText is { } text && HorizontalRange(text) is { } r) bounds.Add(r.Lo < 0f && r.Hi < 0f ? -r.Hi : r.Lo > 0f ? r.Lo : MathF.Min(MathF.Abs(r.Lo), MathF.Abs(r.Hi)));
        if (bounds.Count == 0) { notes.Add($"{where}: no turn threshold in the transitions; using {DefaultEnterDegrees} degrees"); return DefaultEnterDegrees; }
        return bounds.Min();
    }

    /// <summary>Used when the graph's condition cannot be read.</summary>
    public const float DefaultEnterDegrees = 90f;

    static readonly Regex HorizontalRegex = new(@"FLT_Aim_Horizontal_Deg\w* in \[(-?[0-9.]+), (-?[0-9.]+)\]", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Regex SpeedRegex = new(@"FLT_Input_Nav_Speed_LOGIC in \[(-?[0-9.]+), (-?[0-9.]+)\]", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static (float Lo, float Hi)? HorizontalRange(string condition)
    {
        var m = HorizontalRegex.Match(condition);
        return m.Success && float.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var lo) && float.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var hi) ? (lo, hi) : null;
    }

    /// <summary>The lower bound of the requested-speed test in a start transition's condition ("(FLT_Input_Nav_Speed_LOGIC in [3, 10000])" -> 3).</summary>
    internal static float? SpeedFrom(string condition)
    {
        // The condition can be a multi condition with a wall test: take the speed range that is not the plain idle-to-run one (the largest lower bound).
        float? best = null;
        foreach (Match m in SpeedRegex.Matches(condition))
            if (float.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var lo) && (best is null || lo > best)) best = lo;
        return best;
    }

    static bool IsTwist(BehaviorGraphDetail g, int at) => g.Nodes.TryGetValue(at, out var n) && n.Class.EndsWith("TwistModifier", StringComparison.Ordinal);

    static BehaviorSequencer? SequencerUnder(BehaviorGraphDetail g, int? node)
    {
        foreach (var at in Walk(g, node))
            if (g.Sequencers.TryGetValue(at, out var s)) return s;
        return null;
    }

    static IEnumerable<int> Walk(BehaviorGraphDetail g, int? node)
    {
        var seen = new HashSet<int>();
        var stack = new Stack<(int At, int Depth)>();
        if (node is int start) stack.Push((start, 0));
        while (stack.Count > 0)
        {
            var (at, depth) = stack.Pop();
            if (!g.Nodes.TryGetValue(at, out var n) || !seen.Add(at)) continue;
            yield return at;
            if (depth >= 24) continue;
            for (int i = n.Children.Count - 1; i >= 0; i--)
                if (n.Children[i].Target is int c) stack.Push((c, depth + 1));
        }
    }
}
