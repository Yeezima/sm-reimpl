using SmSim;
using SmSim.Extras.Executions;

namespace SmContent.Extras.Executions;

/// <summary>
/// Executions X2: the hit volumes of a character playing an execution clip, posed tick by tick (<see cref="PosedHitVolumes"/>, as
/// <see cref="FlightHitVolumes"/> poses the flight clips), so a Classic executor (who takes normal damage) is hit where the drawn body is. That hits follow the
/// animated pose is the same <b>interpretation</b> as the flight volumes'; the clips, bones and ragdoll are the data's; the step (one tick) is ours.
/// </summary>
public static class ExecutionHitVolumes
{
    /// <summary>Most sets one clip may give (a bound: a 10 s clip at 30 Hz; longer ones are left out with a note).</summary>
    public const int MaxSets = 301;

    /// <summary>
    /// The posed volumes of each of <paramref name="clips"/> (distinct, case aside, in the order given) in <paramref name="rig"/>, one set every
    /// <paramref name="tickSeconds"/> from 0 to the clip's end; a clip that is missing, does not decode or gives an unusable pose is left out with a note.
    /// </summary>
    public static IReadOnlyList<ExecutionPose> Build(PosedHitVolumes rig, IEnumerable<string> clips, List<string> notes, float tickSeconds = 1f / 30f)
    {
        var poses = new List<ExecutionPose>();
        if (!(tickSeconds > 0f) || !float.IsFinite(tickSeconds)) { notes.Add("execution hit volumes: no usable tick length"); return poses; }
        var names = new HashSet<string>(rig.Character.AnimationNames, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var clip in clips)
        {
            if (string.IsNullOrWhiteSpace(clip) || !seen.Add(clip)) continue;
            if (!names.Contains(clip)) { notes.Add($"execution hit volumes: '{clip}' is not in the rig; left out"); continue; }
            try
            {
                float duration = rig.Character.LoadAnimation(clip).Animation.Duration;
                int count = (int)MathF.Ceiling(duration / tickSeconds - 1e-4f) + 1;
                if (!float.IsFinite(duration) || duration <= 0f || count > MaxSets) { notes.Add($"execution hit volumes: '{clip}' lasts {duration} s; left out"); continue; }
                var sets = new List<IReadOnlyList<HitVolume>>(count);
                for (int k = 0; k < count; k++) sets.Add(rig.Pose(rig.ClipPoseAt(clip, MathF.Min(k * tickSeconds, duration), seconds: true)));
                var pose = new ExecutionPose(clip, tickSeconds, sets);
                if (!pose.IsValid) { notes.Add($"execution hit volumes: a pose of '{clip}' is unusable; left out"); continue; }
                poses.Add(pose);
            }
            catch (Exception e) when (e is InvalidDataException or NotSupportedException or KeyNotFoundException or ArgumentException)
            {
                notes.Add($"execution hit volumes: '{clip}' not decoded ({e.Message}); left out");
            }
        }
        return poses;
    }

    /// <summary>
    /// The posed volumes of every row's attacker clips, the reference clip and each distance variant (the executor is the one who can be hit; Classic victims are
    /// corpses, Warfront ones crowd Orks). A blended pose is hit with the nearer variant's volumes (X3's choice to make; ours).
    /// </summary>
    public static IReadOnlyList<ExecutionPose> Build(PosedHitVolumes rig, ExecutionCatalogue catalogue, List<string> notes, float tickSeconds = 1f / 30f) =>
        Build(rig, catalogue.Rows.SelectMany(r => r.AttackerVariants.Select(v => v.Clip).Prepend(r.AttackerClip)), notes, tickSeconds);
}
