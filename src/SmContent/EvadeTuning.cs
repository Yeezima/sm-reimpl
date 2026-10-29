using System.Numerics;
using SmFormats.Baf;
using SmFormats.Havok;
using SmSim;

namespace SmContent;

/// <summary>
/// Reads a class's evade (<see cref="EvadeSpec"/>, M21) from its core-ability wargear (<c>sm_mp_core_abil_evade</c>,
/// <c>sm_mp_core_abil_heavy_evade</c>): <c>ability_data/ability_definitions/ability_evade</c> (a <c>sprint</c>-type ability:
/// <c>special_nav_ability</c>'s <c>min_time_between_activations</c> and <c>max_horz_angle_for_wall_hit</c>,
/// <c>max_turn_rate</c>, <c>camera_curve</c>) and the fire mode's <c>animation_set</c>, which picks the clip set in the
/// marine rig's base graph <see cref="Graph"/> (<see cref="ClipSets"/>). Per direction: the sequencer playing the set's clip,
/// its length / playback speed, its branch tags (seconds) and the clip's extracted motion (root motion).
/// </summary>
public static class EvadeTuning
{
    public const string Ability = "ability_data/ability_definitions/ability_evade";
    /// <summary>The marine rig's base multiplayer graph, which holds the ranged evade sequencers.</summary>
    public const string Graph = "SM_Base_MP";
    public const string MoveBranch = "BRANCH_Run", RangedBranch = "BRANCH_ToRanged", SwitchBranch = "BRANCH_To_Weapon_Switch",
        MeleeBranch = "BRANCH_Melee_Combo", NextBranch = "BRANCH_Evade", AlignModifier = "Align To Nav Direction";

    /// <summary>
    /// <c>animation_set</c> → clip name prefix (then F, B, L or R and "_"): "evade" → the ranged rolls
    /// (<c>SM_EVD_R2R_Roll_*</c>, SM_Base_MP's "Ranged Evade … Sequencer"s); "heavy_evade" → the heavy evades of the heavy
    /// bolter set (<c>SM_EVD_TB_Evade_*</c>, also in <c>MP_SM_HB_Brace</c>; **interpretation**: SM_Base_MP also has a
    /// lascannon set, <c>SM_EVD_LC_Evade_*</c>, of the same length, timing and about the same distance; which one the
    /// original picks per weapon is not decoded, the heavy bolter is the heavy classes' default weapon).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> ClipSets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["evade"] = "SM_EVD_R2R_Roll_",
        ["heavy_evade"] = "SM_EVD_TB_Evade_",
    };

    /// <summary>
    /// The sets whose evade is a hop (user's knowledge of the original: Devastator and Havoc hop, the Tactical classes roll).
    /// Their clips' root motion has no height (Z 0 throughout), so the hop's height is the feet's clearance in the clip
    /// (<see cref="LowestFoot"/>, <see cref="Clearance"/>); a roll stays on the ground (its feet leave the ground as the body
    /// turns over, which is no lift).
    /// </summary>
    public static readonly IReadOnlySet<string> HopSets = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "heavy_evade" };

    /// <summary>Clearance below this (metres) counts as on the ground (ours: the standing pose's feet move a few millimetres).</summary>
    public const float HopThreshold = 0.02f;

    /// <summary>
    /// Where a rig's evades sit (Ork O2): the graph holding the evade sequencers, <c>animation_set</c> → clip prefix, the sets that hop,
    /// the wargear ability tables accepted (first found), a set used when the wargear names none, and the sets that have only a left
    /// and a right clip (<see cref="SideOnlySets"/>).
    /// </summary>
    public sealed record RigMap(string Graph, IReadOnlyDictionary<string, string> ClipSets, IReadOnlySet<string> HopSets)
    {
        public IReadOnlyList<string> Abilities { get; init; } = [Ability];
        /// <summary>The set when the wargear's fire mode names no <c>animation_set</c> (the Ork's evade names none); null: no evade then.</summary>
        public string? DefaultSet { get; init; }
        /// <summary>
        /// Sets with only side clips (the Ork's dodge hop, design section 1.5): the spec is <see cref="EvadeSpec.SideOnly"/> (Ork O4) and its
        /// forward and back moves are stand-ins (the right and left clips) the simulation never plays.
        /// </summary>
        public IReadOnlySet<string> SideOnlySets { get; init; } = new HashSet<string>();
    }

    /// <summary>The marine rig's evades (<see cref="Graph"/>, <see cref="ClipSets"/>, <see cref="HopSets"/>).</summary>
    public static readonly RigMap MarineRig = new(Graph, ClipSets, HopSets);

    /// <summary>The Ork's dodge hop set name (**ours**: the Ork's evade wargear names no <c>animation_set</c>).</summary>
    public const string OrkSideHop = "ork_side_hop";

    /// <summary>
    /// The Ork Boy's evade (Ork O2): <c>ork_coop_boy_evade_ranged</c>'s ability <c>evade</c> (no <c>animation_set</c>, no
    /// <c>special_nav_ability</c>) and <c>Base_RANGED_Coop</c>'s "Evade Left/Right Sequence", whose first clips are the 4 m side evades
    /// <c>BOY_2HR_NAV_Evade4m_L_01</c> / <c>_R_01</c> (the 6 m clips are the blend's other end). A hop from the feet's clearance; side only.
    /// </summary>
    public static readonly RigMap OrkRig = new("Base_RANGED_Coop",
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [OrkSideHop] = "BOY_2HR_NAV_Evade4m_" },
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { OrkSideHop })
    {
        Abilities = ["ability_data/ability_definitions/evade", Ability],
        DefaultSet = OrkSideHop,
        SideOnlySets = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { OrkSideHop },
    };

    /// <summary>
    /// The lowest foot or toe bone's model-space height (bones whose name contains "Foot" or "Toe") at <paramref name="samples"/>
    /// evenly spaced times over the clip (both ends included), or null when the rig has no such bone.
    /// </summary>
    public static float[]? LowestFoot(HavokCharacterFile character, string clip, int samples)
    {
        var skeleton = character.Skeleton;
        var feet = skeleton.BoneNames.Select((n, i) => (n, i))
            .Where(b => b.n.Contains("Foot", StringComparison.OrdinalIgnoreCase) || b.n.Contains("Toe", StringComparison.OrdinalIgnoreCase)).Select(b => b.i).ToArray();
        if (feet.Length == 0 || samples < 1) return null;
        var animation = character.LoadAnimation(clip);
        var result = new float[samples];
        for (int k = 0; k < samples; k++)
        {
            float time = samples == 1 ? 0f : animation.Animation.Duration * k / (samples - 1);
            var local = skeleton.ReferencePose.ToArray();
            var pose = animation.Animation.Sample(time);
            for (int t = 0; t < pose.Length; t++) local[animation.TrackToBone[t]] = pose[t];
            var model = skeleton.ModelSpace(local);
            result[k] = feet.Min(i => model[i].M43);
        }
        return result;
    }

    /// <summary>
    /// The hop curve from the lowest foot's heights (one per path sample). The clearance is each height above the first; the
    /// hop is the run of samples at or above <see cref="HopThreshold"/> around the highest one: it leaves the ground at the
    /// sample before that run, lands at the sample after it, and peaks at the highest clearance. Its shape is a parabola over
    /// that span (**ours**: the lowest foot alternates between the feet, so the raw clearance is jagged); 0 elsewhere.
    /// No clearance reaches the threshold: all zeros (no hop).
    /// </summary>
    public static float[] Clearance(IReadOnlyList<float> lowestFoot)
    {
        int n = lowestFoot.Count;
        var result = new float[n];
        if (n < 3) return result;
        var c = lowestFoot.Select(h => h - lowestFoot[0] is var v && float.IsFinite(v) ? v : 0f).ToArray();
        int p = Array.IndexOf(c, c.Max());
        float peak = c[p];
        if (peak < HopThreshold) return result;
        int a = p, b = p;
        while (a > 0 && c[a - 1] >= HopThreshold) a--;
        while (b < n - 1 && c[b + 1] >= HopThreshold) b++;
        int takeOff = Math.Max(0, a - 1), landing = Math.Min(n - 1, b + 1);
        for (int k = takeOff + 1; k < landing; k++)
        {
            float u = (float)(k - takeOff) / (landing - takeOff);
            result[k] = 4f * peak * u * (1f - u);
        }
        return result;
    }

    /// <summary>Whether a wargear file has an evade ability (the jump pack classes' <c>sm_mp_core_abil_jump_evade</c> has one too: read as their dash, <see cref="ReadDash"/>).</summary>
    public static bool IsEvade(BafDocument doc) => doc.Root.Table(Ability) is not null;

    // ---- M44: the jump-pack dash ----
    /// <summary>The dash's flight clips: <c>SM_JMP_Dash_</c> + F, B, L or R (exact names), in SM_Base_MP's "Jumppack Evade" state.</summary>
    public const string DashClipPrefix = "SM_JMP_Dash_";
    /// <summary>The dash's landing clips: <c>SM_JMP_Dash_Landing_Shallow_</c> + B, L or R; forward is <c>_01</c> (or <c>_F</c>).</summary>
    public const string DashLandingPrefix = "SM_JMP_Dash_Landing_Shallow_";
    /// <summary>Tags of the dash sequencers: the hold that turns the press into a jump, the flight's end (a falling state), the landing's end.</summary>
    public const string ToJumpBranch = "BRANCH_Evade_to_Jump", FallingModifier = "Falling", EndBranch = "BRANCH_Evade_End";
    /// <summary>Protocol 30: the dash flight's branch from which a melee press starts the dash attack (<see cref="EvadeMove.DashAttackAt"/>).</summary>
    public const string DashAttackBranch = "BRANCH_Dash_Attack";

    /// <summary>
    /// M44: the jump-pack classes' evade, the dash (<see cref="EvadeSpec.Dash"/>), from their core-ability file (<c>sm_mp_core_abil_jump_evade</c>:
    /// <c>ability_evade</c>'s <c>special_nav_ability</c>, <c>max_turn_rate</c>, <c>camera_curve</c>) and SM_Base_MP's dash sequencers. Per direction
    /// the flight (<see cref="EvadeMove.Flight"/>): the <c>SM_JMP_Dash_*</c> clip's root motion (X, Y and the signed height Z) up to its
    /// <see cref="FallingModifier"/> modifier (1.3 s on the install; the flight's end), and the landing part from the matching
    /// <c>SM_JMP_Dash_Landing_Shallow_*</c> sequencer (its motion and branch times up to <see cref="EndBranch"/>; <see cref="ToJumpBranch"/>
    /// as <see cref="EvadeMove.JumpAt"/>). <see cref="EvadeSpec.ToJumpAt"/> is the forward flight sequencer's <see cref="ToJumpBranch"/>. Eight
    /// directions (the owner's request, as the Tactical roll). Null with a note when the data are missing.
    /// </summary>
    public static EvadeSpec? ReadDash(string name, BafDocument doc, IReadOnlyList<BehaviorGraph> graphs, Func<string, float> animationLength,
        Func<string, HavokExtractedMotion?> extractedMotion, List<string> notes)
    {
        if (doc.Root.Table(Ability) is not { } ability) { notes.Add($"{name}: no {Ability}; no dash"); return null; }
        var graph = graphs.FirstOrDefault(g => g.Name == Graph);
        if (graph is null) { notes.Add($"{name}: graph {Graph} not found; no dash"); return null; }
        BehaviorSequencer? Playing(string clip) => graph.Sequencers.FirstOrDefault(s => s.Animations.Count > 0 && s.Animations[0].Equals(clip, StringComparison.OrdinalIgnoreCase));

        // One part: the sequencer's clip motion up to `cut` (seconds; snapped to a motion sample), heights when `flight`.
        EvadeMove? Part(BehaviorSequencer seq, Func<float, float> cut, bool flight)
        {
            string clip = seq.Animations[0];
            float speed = seq.PlaybackSpeeds.Count > 0 && float.IsFinite(seq.PlaybackSpeeds[0]) && seq.PlaybackSpeeds[0] > 0f ? seq.PlaybackSpeeds[0] : 1f;
            float length;
            HavokExtractedMotion? motion;
            try { length = animationLength(clip) / speed; motion = extractedMotion(clip); }
            catch (Exception e) when (e is InvalidDataException or KeyNotFoundException or NotSupportedException or ArgumentException
                or IndexOutOfRangeException or OverflowException)
            { notes.Add($"{name}: {clip}: {e.Message}; no dash"); return null; }
            if (!float.IsFinite(length) || length <= 0f || motion is null || motion.Samples.Count < 2)
            { notes.Add($"{name}: {clip} has no usable length or extracted motion; no dash"); return null; }
            if (Vector3.Dot(Vector3.Normalize(motion.Up), Vector3.UnitZ) < 0.999f || Vector3.Dot(Vector3.Normalize(motion.Forward), Vector3.UnitY) < 0.999f)
            { notes.Add($"{name}: {clip}'s motion frame is not +Z up, +Y forward; no dash"); return null; }
            float step = length / (motion.Samples.Count - 1);
            float Tag(string kind, string tag, float fallback) =>
                seq.Tags.Where(t => t.Kind == kind && t.Name == tag).OrderBy(t => t.Start).Select(t => Math.Clamp(t.Start, 0f, 1f) * length).DefaultIfEmpty(fallback).First();
            int last = Math.Clamp((int)MathF.Round(cut(length) / step), 1, motion.Samples.Count - 1);
            float duration = last * step;
            var first = motion.Samples[0];
            var path = motion.Samples.Take(last + 1).Select(v => new Vector2(v.X - first.X, v.Y - first.Y)).ToArray();
            float[] height = flight ? motion.Samples.Take(last + 1).Select(v => v.Z - first.Z).ToArray() : [];
            float B(string branch) => MathF.Min(Tag("Branch", branch, duration), duration);
            return flight
                ? new EvadeMove(clip, path, duration, duration, duration, duration, duration, duration) { Height = height, Flight = true }
                : new EvadeMove(clip, path, duration, B(MoveBranch), B(RangedBranch), B(SwitchBranch), B(MeleeBranch), B(NextBranch)) { JumpAt = B(ToJumpBranch) };
        }
        float toJumpAt = 0f;
        EvadeMove? Direction(char letter)
        {
            var seq = Playing(DashClipPrefix + letter);
            if (seq is null) { notes.Add($"{name}: no {Graph} sequencer plays {DashClipPrefix}{letter}; no dash"); return null; }
            var landSeq = Playing(DashLandingPrefix + letter) ?? (letter == 'F' ? Playing(DashLandingPrefix + "01") : null);
            if (landSeq is null) { notes.Add($"{name}: no {Graph} sequencer plays {DashLandingPrefix}{letter}; no dash"); return null; }
            float FlightEnd(float length) => seq.Tags.Where(t => t.Kind == "Modifier" && t.Name == FallingModifier).Select(t => Math.Clamp(t.Start, 0f, 1f) * length).DefaultIfEmpty(length).Min();
            if (Part(seq, FlightEnd, true) is not { } flightMove) return null;
            if (Part(landSeq, length => landSeq.Tags.Where(t => t.Kind == "Branch" && t.Name == EndBranch).Select(t => Math.Clamp(t.Start, 0f, 1f) * length).DefaultIfEmpty(length).Min(), false) is not { } landing) return null;
            if (letter == 'F')
            {
                float length = animationLength(seq.Animations[0]);
                toJumpAt = seq.Tags.Where(t => t.Kind == "Branch" && t.Name == ToJumpBranch).Select(t => Math.Clamp(t.Start, 0f, 1f) * length).DefaultIfEmpty(0f).Min();
                if (!(toJumpAt > 0f)) { notes.Add($"{name}: {seq.Name} has no {ToJumpBranch} after its start; no dash"); return null; }
            }
            // Protocol 30: the dash attack's window (BRANCH_Dash_Attack; the flight's time from which a melee press attacks).
            float flightSpeed = seq.PlaybackSpeeds.Count > 0 && float.IsFinite(seq.PlaybackSpeeds[0]) && seq.PlaybackSpeeds[0] > 0f ? seq.PlaybackSpeeds[0] : 1f;
            float dashAttackAt = seq.Tags.Where(t => t.Kind == "Branch" && t.Name == DashAttackBranch).Select(t => Math.Clamp(t.Start, 0f, 1f) * animationLength(seq.Animations[0]) / flightSpeed)
                .DefaultIfEmpty(float.PositiveInfinity).Min();
            return flightMove with { Landing = landing, DashAttackAt = dashAttackAt };
        }
        if (Direction('F') is not { } forward || Direction('B') is not { } back || Direction('L') is not { } left || Direction('R') is not { } right) return null;
        var forwardSeq = Playing(DashClipPrefix + "F")!;
        float PropertyEnd(string property) => forwardSeq.Tags.Where(t => t.Kind == "EntityProperty" && t.Name == property)
            .Select(t => Math.Clamp(t.End, 0f, 1f) * animationLength(forwardSeq.Animations[0])).DefaultIfEmpty(0f).Max();
        var nav = ability.Table("special_nav_ability");
        var spec = new EvadeSpec(name, forward, back, left, right)
        {
            MinInterval = Number(nav, "min_time_between_activations") is float m && float.IsFinite(m) && m >= 0f && m <= 8f ? m : 0.8f,
            WallHitAngle = Number(nav, "max_horz_angle_for_wall_hit") is float a && float.IsFinite(a) && a >= 0f && a <= 90f ? a : 25f,
            MaxTurnRate = Number(ability, "max_turn_rate") is float r && float.IsFinite(r) && r >= 0f ? r : 40f,
            TargetableModeTime = PropertyEnd("TargetableMode"), CollisionModeTime = PropertyEnd("CollisionMode"),
            AnimationSet = "jump_evade", Diagonals = true, Dash = true, ToJumpAt = toJumpAt,
            Camera = (ability.String("camera_curve") ?? "").Split('\\', '/')[^1],
        };
        notes.Add($"{name}: the jump-pack dash: forward {forward.Distance.Y:F1} m, peak {forward.PeakHeight:F2} m, flight up to {forward.Duration:F2} s, landing {forward.Landing!.Duration:F2} s; " +
                  $"a hold to {spec.ToJumpAt:F3} s jumps; eight directions (owner's request)");
        return spec;
    }

    /// <summary>Whether a wargear file has an evade ability a rig accepts (<see cref="RigMap.Abilities"/>).</summary>
    public static bool IsEvade(BafDocument doc, RigMap map) => map.Abilities.Any(a => doc.Root.Table(a) is not null);

    /// <summary>A float or int attribute as a float.</summary>
    static float? Number(BafTable? t, string name) => t?[name] switch
    {
        { Type: BafType.Float } e => e.AsFloat(),
        { Type: BafType.Int } e => e.Raw,
        _ => null,
    };

    /// <param name="extractedMotion">The extracted motion of a clip by name (null: none).</param>
    /// <param name="lowestFoot">For a hop set (<see cref="HopSets"/>): the lowest foot's heights of a clip at n evenly spaced
    /// times (<see cref="LowestFoot"/>); null, or a null result: the evade stays on the ground, with a note.</param>
    /// <returns>The evade, or null with a note when the data are missing or unusable.</returns>
    public static EvadeSpec? Read(string name, BafDocument doc, IReadOnlyList<BehaviorGraph> graphs, Func<string, float> animationLength,
        Func<string, HavokExtractedMotion?> extractedMotion, List<string> notes, Func<string, int, IReadOnlyList<float>?>? lowestFoot = null) =>
        Read(MarineRig, name, doc, graphs, animationLength, extractedMotion, notes, lowestFoot);

    /// <summary>A class's evade on a rig (Ork O2: the side-only dodge hop); as <see cref="Read(string, BafDocument, IReadOnlyList{BehaviorGraph}, Func{string, float}, Func{string, HavokExtractedMotion?}, List{string}, Func{string, int, IReadOnlyList{float}?}?)"/>.</summary>
    public static EvadeSpec? Read(RigMap map, string name, BafDocument doc, IReadOnlyList<BehaviorGraph> graphs, Func<string, float> animationLength,
        Func<string, HavokExtractedMotion?> extractedMotion, List<string> notes, Func<string, int, IReadOnlyList<float>?>? lowestFoot = null)
    {
        if (map.Abilities.Select(a => doc.Root.Table(a)).FirstOrDefault(t => t is not null) is not { } ability)
        { notes.Add($"{name}: no {string.Join(" or ", map.Abilities)}"); return null; }
        string? set = doc.Root.Table("common_data/fire_mode_primary")?.Entries
            .Where(e => e.Type == BafType.Table).Select(e => e.AsTable().String("animation_set")).FirstOrDefault(a => !string.IsNullOrEmpty(a));
        if (set is null && map.DefaultSet is { } fallback) { set = fallback; notes.Add($"{name}: no animation_set; the rig's '{fallback}' clips are used (ours)"); }
        if (set is null || !map.ClipSets.TryGetValue(set, out var prefix)) { notes.Add($"{name}: animation_set '{set}' has no known evade clips; no evade"); return null; }
        string graphName = map.Graph;
        var graph = graphs.FirstOrDefault(g => g.Name == graphName);
        if (graph is null) { notes.Add($"{name}: graph {graphName} not found; no evade"); return null; }
        bool sideOnly = map.SideOnlySets.Contains(set);

        EvadeMove? Move(char letter, out BehaviorSequencer? sequencer)
        {
            var seq = graph.Sequencers.FirstOrDefault(s => s.Animations.Count > 0 && s.Animations[0].StartsWith($"{prefix}{letter}_", StringComparison.OrdinalIgnoreCase));
            sequencer = seq;
            if (seq is null) { notes.Add($"{name}: no {graphName} sequencer plays {prefix}{letter}_*; no evade"); return null; }
            string clip = seq.Animations[0];
            float speed = seq.PlaybackSpeeds.Count > 0 && float.IsFinite(seq.PlaybackSpeeds[0]) && seq.PlaybackSpeeds[0] > 0f ? seq.PlaybackSpeeds[0] : 1f;
            float duration;
            HavokExtractedMotion? motion;
            try { duration = animationLength(clip) / speed; motion = extractedMotion(clip); }
            catch (Exception e) when (e is InvalidDataException or KeyNotFoundException or NotSupportedException or ArgumentException
                or IndexOutOfRangeException or OverflowException)   // ArgumentOutOfRangeException is an ArgumentException
            { notes.Add($"{name}: {clip}: {e.Message}; no evade"); return null; }
            if (!float.IsFinite(duration) || duration <= 0f) { notes.Add($"{name}: {clip} lasts {duration} s; no evade"); return null; }
            if (motion is null || motion.Samples.Count < 2) { notes.Add($"{name}: {clip} has no extracted motion; no evade"); return null; }
            if (Vector3.Dot(Vector3.Normalize(motion.Up), Vector3.UnitZ) < 0.999f || Vector3.Dot(Vector3.Normalize(motion.Forward), Vector3.UnitY) < 0.999f)
            { notes.Add($"{name}: {clip}'s motion frame (up {motion.Up}, forward {motion.Forward}) is not +Z up, +Y forward; no evade"); return null; }
            if (MathF.Abs(motion.Duration - duration * speed) > 1e-3f) notes.Add($"{name}: {clip}'s motion lasts {motion.Duration} s, the clip {duration * speed} s; the clip's is used");
            var first = new Vector2(motion.Samples[0].X, motion.Samples[0].Y);
            var path = motion.Samples.Select(v => new Vector2(v.X, v.Y) - first).ToArray();
            float Branch(string branch)
            {
                var tag = seq.Tags.Where(t => t.Kind == "Branch" && t.Name == branch).OrderBy(t => t.Start).FirstOrDefault();
                if (tag is null) { notes.Add($"{name}: {seq.Name} has no {branch}; the clip's end is used"); return duration; }
                return Math.Clamp(tag.Start * duration, 0f, duration);
            }
            float[] height = [];
            if (map.HopSets.Contains(set!))
            {
                if (motion.Samples.Any(v => MathF.Abs(v.Z - motion.Samples[0].Z) > 1e-3f)) notes.Add($"{name}: {clip}'s root motion has height; the feet's clearance is used");
                IReadOnlyList<float>? feet = null;
                try { feet = lowestFoot?.Invoke(clip, path.Length); }
                catch (Exception e) when (e is InvalidDataException or KeyNotFoundException or NotSupportedException or ArgumentException
                    or IndexOutOfRangeException or OverflowException)
                { notes.Add($"{name}: {clip}'s feet: {e.Message}"); }
                if (feet is null || feet.Count != path.Length) notes.Add($"{name}: {clip}'s feet not read; the hop stays on the ground");
                else height = Clearance(feet);
            }
            return new EvadeMove(clip, path, duration, Branch(MoveBranch), Branch(RangedBranch), Branch(SwitchBranch), Branch(MeleeBranch), Branch(NextBranch))
            {
                Height = height,
            };
        }
        EvadeMove? forward, back, left, right;
        BehaviorSequencer? forwardSequencer = null;
        if (sideOnly)
        {
            // Side clips only (the Ork's hop; EvadeSpec.SideOnly, O4): the right clip stands in for forward and the left for back (never
            // played); the sequencer of the right clip gives the align and property windows.
            left = Move('L', out _);
            right = left is null ? null : Move('R', out forwardSequencer);
            (forward, back) = (right, left);
        }
        else
        {
            forward = Move('F', out forwardSequencer);
            back = forward is null ? null : Move('B', out _);
            left = back is null ? null : Move('L', out _);
            right = left is null ? null : Move('R', out _);
        }
        if (forward is null || back is null || left is null || right is null || forwardSequencer is null) return null;
        if (sideOnly) notes.Add($"{name}: side-only evade ({prefix}L_ / R_): every press hops left or right (EvadeSpec.SideOnly)");

        var nav = ability.Table("special_nav_ability");
        float Value(BafTable? t, string field, float fallback, float max)
        {
            if (Number(t, field) is float v && float.IsFinite(v) && v >= 0f && v <= max) return v;
            notes.Add($"{name}: {field} missing or out of range; using {fallback}");
            return fallback;
        }
        float PropertyEnd(string property) =>
            forwardSequencer!.Tags.Where(t => t.Kind == "EntityProperty" && t.Name == property).Select(t => Math.Clamp(t.End, 0f, 1f) * forward.Duration).DefaultIfEmpty(0f).Max();
        var spec = new EvadeSpec(name, forward, back, left, right)
        {
            MinInterval = Value(nav, "min_time_between_activations", 0.8f, 8f),
            WallHitAngle = Value(nav, "max_horz_angle_for_wall_hit", 25f, 90f),
            MaxTurnRate = Value(ability, "max_turn_rate", 40f, 1e4f),
            AlignForward = forwardSequencer!.Tags.Any(t => t.Kind == "Modifier" && t.Name?.StartsWith(AlignModifier, StringComparison.OrdinalIgnoreCase) == true),
            TargetableModeTime = PropertyEnd("TargetableMode"),
            CollisionModeTime = PropertyEnd("CollisionMode"),
            AnimationSet = set,
            // M22: the roll takes diagonals (user's knowledge of the original); the heavy hop keeps four directions, the side hop two.
            Diagonals = !map.HopSets.Contains(set) && !sideOnly,
            SideOnly = sideOnly,
            Camera = (ability.String("camera_curve") ?? "").Split('\\', '/')[^1],
        };
        notes.Add($"{name}: max_turn_rate {spec.MaxTurnRate} not simulated (the body keeps its press-time facing); TargetableMode {spec.TargetableModeTime:F3} s and CollisionMode {spec.CollisionModeTime:F3} s: hyper armour against hit reactions (M28)");
        if (spec.Forward.Hops) notes.Add($"{name}: a hop: peak {new[] { forward, back, left, right }.Max(m => m.PeakHeight):F3} m");
        var evadeNames = map.Abilities.Select(a => a.Split('/')[^1]).ToHashSet(StringComparer.Ordinal);
        foreach (var other in doc.Root.Table("ability_data/ability_definitions")?.Entries.Where(e => e.Type == BafType.Table && !evadeNames.Contains(e.Name)) ?? [])
            notes.Add($"{name}: {other.Name} not simulated");
        foreach (var upgrade in doc.Root.Table("action_data/lifetime_actions/commands/apply_upgrade_string/strings")?.Entries.Where(e => e.Type == BafType.String) ?? [])
            notes.Add($"{name}: lifetime upgrade '{upgrade.AsString()}' not simulated (no fall damage exists)");
        return spec;
    }
}
