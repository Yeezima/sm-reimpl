using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using SmFormats.Havok;

namespace SmContent;

/// <summary>
/// feel/melee-lock: what a melee attack's sequencer says about reaching its target, read from the behaviour graph (read-only):
/// the <c>AttackAlignment</c> window (the attacker turns to its target), the <c>AttackDistance</c> window and distance, the
/// <c>AttackDistanceValidation</c> window, and the distance variants the graph blends on <c>FLT_MeleeTargetDistance</c>
/// (<c>…_1m</c>, <c>…_3.5m</c>, <c>…_7m</c>) with each clip's extracted root motion (forward metres from the clip's start, by time).
/// Times are seconds of the playing clip (fractions × length / playback speed). The meaning of the three windows is an
/// **interpretation** (their names; the code that reads them is in the executable).
/// </summary>
public static partial class MeleeMotion
{
    /// <summary>One distance variant: its clip, the distance its name gives (metres; 0 when it names none) and its root motion.</summary>
    /// <param name="Forward">Forward root motion (metres along the character's facing) at each motion sample, evenly spaced over <see cref="Length"/>; empty: none.</param>
    public sealed record Variant(string Clip, float Distance, float Length, IReadOnlyList<float> Forward)
    {
        /// <summary>Forward root motion at <paramref name="time"/> seconds (linear between samples; clamped to the clip).</summary>
        public float ForwardAt(float time)
        {
            if (Forward.Count == 0) return 0f;
            if (Forward.Count == 1 || Length <= 0f) return Forward[0];
            float f = Math.Clamp(time / Length, 0f, 1f) * (Forward.Count - 1);
            int i = Math.Min((int)f, Forward.Count - 2);
            return Forward[i] + (Forward[i + 1] - Forward[i]) * (f - i);
        }
    }

    /// <summary>A sequencer's reach data (seconds; a missing window is (0, 0)).</summary>
    public sealed record Reach(string Graph, string Sequencer, float Length, (float From, float To) Alignment, (float From, float To) DistanceWindow,
        float Distance, (float From, float To) Validation, float HitTime, IReadOnlyList<Variant> Variants)
    {
        /// <summary>The first run branch (<c>BRANCH_Melee_Draw_Run</c>, <c>BRANCH_Melee_Run</c>, <c>BRANCH_Run</c>), seconds; the length without one.</summary>
        public float MoveAt { get; init; }
    }

    [GeneratedRegex(@"_(\d+(?:\.\d+)?)m(?:_|$)", RegexOptions.IgnoreCase)]
    private static partial Regex DistanceName();

    /// <summary>The distance a clip's name gives (<c>…_3.5m</c>, <c>…_1m_miss</c>), or 0.</summary>
    public static float DistanceOf(string clip) =>
        DistanceName().Match(clip) is { Success: true } m && float.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float d) ? d : 0f;

    /// <summary>Reads one sequencer's reach data; null when it plays nothing.</summary>
    public static Reach? Read(BehaviorGraph graph, BehaviorSequencer seq, Func<string, float> animationLength, Func<string, HavokExtractedMotion?> motion)
    {
        if (seq.Animations.Count == 0) return null;
        float speed = seq.PlaybackSpeeds.Count > 0 && float.IsFinite(seq.PlaybackSpeeds[0]) && seq.PlaybackSpeeds[0] > 0f ? seq.PlaybackSpeeds[0] : 1f;
        float length = animationLength(seq.Animations[0]) / speed;
        (float, float) Window(string kind) => seq.Tags.Where(t => t.Kind == kind).OrderBy(t => t.Start).FirstOrDefault() is { } t ? (t.Start * length, t.End * length) : (0f, 0f);
        var distanceTag = seq.Tags.Where(t => t.Kind == "AttackDistance").OrderBy(t => t.Start).FirstOrDefault();
        var hit = seq.Tags.Where(t => t.Kind == "Event" && t.Name is "ACTN_Hit_Targets_Multiplayer" or "ACTN_Hit_Primary_Target").OrderBy(t => t.Start).FirstOrDefault();
        var variants = new List<Variant>();
        for (int i = 0; i < seq.Animations.Count; i++)
        {
            string clip = seq.Animations[i];
            float s = i < seq.PlaybackSpeeds.Count && float.IsFinite(seq.PlaybackSpeeds[i]) && seq.PlaybackSpeeds[i] > 0f ? seq.PlaybackSpeeds[i] : 1f;
            float len = animationLength(clip) / s;
            var forward = new List<float>();
            if (motion(clip) is { Samples.Count: > 0 } m)
            {
                var axis = m.Forward.LengthSquared() > 1e-8f ? Vector3.Normalize(m.Forward) : Vector3.UnitY;
                forward.AddRange(m.Samples.Select(q => Vector3.Dot(new Vector3(q.X, q.Y, q.Z), axis)));
            }
            variants.Add(new Variant(clip, DistanceOf(clip), len, forward));
        }
        return new Reach(graph.Name, seq.Name, length, Window("AttackAlignment"), distanceTag is null ? (0f, 0f) : (distanceTag.Start * length, distanceTag.End * length),
            distanceTag?.Distance is float d && float.IsFinite(d) ? d : 0f, Window("AttackDistanceValidation"), (hit?.Start ?? 0f) * length, variants)
        {
            MoveAt = (seq.Tags.Where(t => t.Kind == "Branch" && t.Name is "BRANCH_Melee_Draw_Run" or "BRANCH_Melee_Run" or "BRANCH_Run").OrderBy(t => t.Start).FirstOrDefault()?.Start ?? 1f) * length,
        };
    }

    /// <summary>
    /// What the simulation reads for an attack's reach (<see cref="SmSim.MeleeReach"/>), from the sequencer that plays it: the alignment and
    /// distance windows (seconds of the attack, <paramref name="duration"/> = the chosen clip's length over its speed), every clip it blends with
    /// its blend weight (<see cref="Source.ClipDistance"/>, else the distance its name gives) and forward root motion, and the graph's starting
    /// blend value (<paramref name="defaultDistance"/> when given: ours, for the Ork's AI graph).
    /// </summary>
    /// <param name="moveAt">The attack's <see cref="SmSim.MeleeStep.MoveAt"/>: the start blend is raised until the clip moves forward by then (below).</param>
    public static SmSim.MeleeReach ReachOf(string graph, BehaviorSequencer seq, float duration, Source source, float? defaultDistance, List<string> notes, float moveAt = float.NaN)
    {
        (float, float) Window(string kind) => seq.Tags.Where(t => t.Kind == kind).OrderBy(t => t.Start).FirstOrDefault() is { } t
            ? (Math.Clamp(t.Start, 0f, 1f) * duration, Math.Clamp(t.End, 0f, 1f) * duration) : (0f, 0f);
        var distanceTag = seq.Tags.Where(t => t.Kind == "AttackDistance").OrderBy(t => t.Start).FirstOrDefault();
        var (alignFrom, alignTo) = Window("AttackAlignment");
        if (alignTo <= alignFrom) notes.Add($"{seq.Name}: no AttackAlignment window; the view turns the attack");
        var variants = new List<SmSim.MeleeVariant>();
        foreach (string clip in seq.Animations.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            float distance = source.ClipDistance(graph, clip) ?? DistanceOf(clip);
            var forward = new List<float>();
            try
            {
                if (source.Motion(clip) is { Samples.Count: > 0 } m)
                {
                    var axis = m.Forward.LengthSquared() > 1e-8f ? Vector3.Normalize(m.Forward) : Vector3.UnitY;
                    forward.AddRange(m.Samples.Select(q => Vector3.Dot(new Vector3(q.X, q.Y, q.Z), axis)));
                }
            }
            catch (Exception e) when (e is InvalidDataException or NotSupportedException or KeyNotFoundException) { notes.Add($"{seq.Name}: {clip}: {e.Message}; no root motion"); }
            if (forward.Any(f => !float.IsFinite(f) || MathF.Abs(f) > 100f)) { notes.Add($"{seq.Name}: {clip}: root motion not usable; none"); forward.Clear(); }
            if (forward.Count == 0) notes.Add($"{seq.Name}: {clip}: no root motion");
            variants.Add(new SmSim.MeleeVariant(distance, forward, clip));
        }
        if (variants.Count > 1 && variants.Select(v => v.Distance).Distinct().Count() < variants.Count)
        {
            notes.Add($"{seq.Name}: blended clips without distinct distances; the first is used");
            variants = [variants[0]];
        }
        float start = defaultDistance ?? source.InitialDistance(graph) ?? 0f;
        if (!float.IsFinite(start)) start = 0f;
        variants = variants.OrderBy(v => v.Distance).ToList();
        var reach = new SmSim.MeleeReach(alignFrom, alignTo, (distanceTag?.Start ?? 0f) * duration, (distanceTag?.End ?? 0f) * duration,
            distanceTag?.Distance is float d && float.IsFinite(d) ? d : 0f, start, variants);
        // The owner (2026-10-29: "you should step forward when swinging your melee weapon or combat knife"): without a target the attack steps forward. Where
        // the graph's start blend picks a clip that backs off by the attack's MoveAt (the power sword's first attack, 0.7 m back), the start blend is raised
        // to the nearest farther variant that moves forward by then (ours).
        float until = float.IsFinite(moveAt) ? moveAt : duration;
        if (reach.ForwardAt(until, duration, start) <= 0f && variants.FirstOrDefault(v => v.Distance > start && v.At(until, duration) > 0f) is { } stepping)
        {
            notes.Add($"{seq.Name}: the start blend {start} backs off without a target; {stepping.Distance} (the first variant that steps forward; ours)");
            reach = reach with { DefaultDistance = stepping.Distance };
        }
        return reach;
    }

    /// <summary>
    /// Where <see cref="ReachOf"/> reads root motion and blend data: the rig's extracted motion, and per graph the starting value of
    /// <c>FLT_MeleeTargetDistance</c> (its <c>hkbVariableValueSet</c>) and the child weight of every clip under a blend bound to it.
    /// </summary>
    public sealed class Source
    {
        public required Func<string, HavokExtractedMotion?> Motion { get; init; }
        readonly Dictionary<string, float?> _initial = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<(string Graph, string Clip), float> _weights = new();

        /// <summary>The graph's starting blend value, or null.</summary>
        public float? InitialDistance(string graph) => _initial.GetValueOrDefault(graph);
        /// <summary>The weight the clip sits at in a blend on <c>FLT_MeleeTargetDistance</c> of the graph, or null.</summary>
        public float? ClipDistance(string graph, string clip) => _weights.TryGetValue((graph.ToLowerInvariant(), clip.ToLowerInvariant()), out float w) ? w : null;

        public const string Variable = "FLT_MeleeTargetDistance";

        /// <summary>Reads a character file's graphs (read-only); a graph whose details cannot be read gives none (noted).</summary>
        public static Source From(HavokCharacterFile character, List<string>? notes = null)
        {
            var cache = new Dictionary<string, HavokExtractedMotion?>(StringComparer.OrdinalIgnoreCase);   // perk-variant reads ask again
            var source = new Source { Motion = clip => cache.TryGetValue(clip, out var m) ? m : cache[clip] = character.LoadExtractedMotion(clip) };
            IReadOnlyList<BehaviorGraphDetail> details;
            try { details = HavokBehaviorDetailReader.Read(character.Pack); }
            catch (Exception e) when (e is InvalidDataException or KeyNotFoundException or ArgumentException or IndexOutOfRangeException)
            { notes?.Add($"melee reach: {e.GetType().Name}: {e.Message}; no blend data"); return source; }
            foreach (var g in details)
            {
                source._initial[g.Name] = BraceTuning.InitialValue(character.Pack, g, Variable);
                foreach (var b in g.Blenders)
                {
                    if (!g.Nodes.TryGetValue(b.Offset, out var node) || !node.Bindings.TryGetValue("blendParameter", out var bound) || bound != Variable) continue;
                    foreach (var child in b.Children)
                        if (child.Generator is int at && g.ClipAt(at) is { } clip && float.IsFinite(child.Weight))
                            source._weights[(g.Name.ToLowerInvariant(), clip.Animation.ToLowerInvariant())] = child.Weight;
                }
            }
            return source;
        }
    }

    /// <summary>Every sequencer of the graphs matching <paramref name="graphPrefix"/> that has an alignment or distance tag.</summary>
    public static IReadOnlyList<Reach> ReadAll(IReadOnlyList<BehaviorGraph> graphs, string graphPrefix, Func<string, float> animationLength, Func<string, HavokExtractedMotion?> motion)
    {
        var all = new List<Reach>();
        foreach (var g in graphs.Where(g => g.Name.StartsWith(graphPrefix, StringComparison.OrdinalIgnoreCase)))
            foreach (var s in g.Sequencers.Where(s => s.Tags.Any(t => t.Kind is "AttackAlignment" or "AttackDistance")))
                if (Read(g, s, animationLength, motion) is { } r) all.Add(r);
        return all;
    }
}
