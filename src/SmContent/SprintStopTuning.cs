using SmFormats.Havok;

namespace SmContent;

/// <summary>
/// The sprint stop's clip from the marine rig's sprint graph (feel/locks-sprint-stop): the "Sprint to Idle" sequencer of <see cref="Graph"/> playing
/// <see cref="Clip"/>, its length, its <c>BRANCH_Sprint_Finished</c> and <c>BRANCH_Evade</c> starts and its root motion's distance.
/// </summary>
/// <param name="Length">The clip's length, seconds.</param>
/// <param name="FinishedAt"><c>BRANCH_Sprint_Finished</c>: seconds into the clip (0.625 × 1.333 = 0.833 on the install); the sprint stop's time
/// (<see cref="SmSim.SimConfig.SprintStopTime"/>).</param>
/// <param name="EvadeAt"><c>BRANCH_Evade</c>, seconds (0.417 on the install; recorded: the owner's rule lets Jump cancel the stop at once).</param>
/// <param name="Distance">The root motion's horizontal distance over the whole clip, metres (5.79 on the install; recorded).</param>
/// <param name="DistanceAtFinished">… and by <paramref name="FinishedAt"/> (5.55; recorded).</param>
public sealed record SprintStopTiming(string Clip, float Length, float FinishedAt, float EvadeAt, float Distance, float DistanceAtFinished);

public static class SprintStopTuning
{
    public const string Graph = RushTuning.Graph;
    public const string Clip = "SM_NAV_Bullrush2Idle_F_01";
    /// <summary>The stop's time when the graph cannot be read: the install's 0.625 × 1.333 s (placeholder).</summary>
    public const float FallbackTime = 0.625f * 4f / 3f;

    /// <summary>The stop clip's timing, or null with a note when the graph, the sequencer, the tag or the clip's length is missing.</summary>
    public static SprintStopTiming? Read(IReadOnlyList<BehaviorGraph> graphs, Func<string, float> animationLength, Func<string, HavokExtractedMotion?>? extractedMotion,
        List<string> notes)
    {
        var graph = graphs.FirstOrDefault(g => g.Name.Equals(Graph, StringComparison.OrdinalIgnoreCase));
        var seq = graph?.Sequencers.FirstOrDefault(s => s.Animations.Any(a => a.Equals(Clip, StringComparison.OrdinalIgnoreCase)));
        if (seq is null) { notes.Add($"sprint stop: no {Graph} sequencer plays {Clip}; {FallbackTime:F3} s (placeholder)"); return null; }
        float length;
        try { length = animationLength(Clip); }
        catch (Exception e) when (e is InvalidDataException or KeyNotFoundException or NotSupportedException or ArgumentException or IndexOutOfRangeException)
        { notes.Add($"sprint stop: {Clip}: {e.Message}; {FallbackTime:F3} s (placeholder)"); return null; }
        if (!float.IsFinite(length) || length <= 0f) { notes.Add($"sprint stop: {Clip} has no usable length; {FallbackTime:F3} s (placeholder)"); return null; }
        float? Start(string branch) => seq.Tags.Where(t => t.Kind == "Branch" && t.Name == branch).Select(t => (float?)t.Start).Min();
        if (Start("BRANCH_Sprint_Finished") is not float finished || !float.IsFinite(finished) || finished <= 0f || finished > 1f)
        { notes.Add($"sprint stop: {Clip} has no usable BRANCH_Sprint_Finished; {FallbackTime:F3} s (placeholder)"); return null; }
        float evade = Math.Clamp(Start("BRANCH_Evade") ?? 1f, 0f, 1f) * length;
        float distance = 0f, atFinished = 0f;
        try
        {
            if (extractedMotion?.Invoke(Clip) is { Samples.Count: >= 2 } motion)
            {
                var first = motion.Samples[0];
                float Dist(System.Numerics.Vector4 p) => new System.Numerics.Vector2(p.X - first.X, p.Y - first.Y).Length();
                distance = Dist(motion.Samples[^1]);
                int at = (int)MathF.Round(finished * (motion.Samples.Count - 1));
                atFinished = Dist(motion.Samples[Math.Clamp(at, 0, motion.Samples.Count - 1)]);
            }
        }
        catch (Exception e) when (e is InvalidDataException or KeyNotFoundException or NotSupportedException or ArgumentException or IndexOutOfRangeException)
        { notes.Add($"sprint stop: {Clip} root motion: {e.Message}; not recorded"); }
        return new SprintStopTiming(Clip, length, finished * length, evade, distance, atFinished);
    }
}
