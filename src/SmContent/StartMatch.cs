using System.Numerics;
using SmFormats.Havok;

namespace SmContent;

/// <summary>
/// Pose matching for the hand-over from the run-start clip to the run cycle (M40-T5; ours): the start clip (<c>SM_TRN_BO_IdleAim_to_RunF</c>) ends in a stride, and the
/// run cycle it hands over to must continue from the same foot. The model-space toe positions of the clip's last frame are compared with the cycle's, sampled at
/// regular times, and the nearest time is the cycle's position to start at (<see cref="GaitPhase.PhaseAt"/> turns it into the shared phase of the blend space).
/// Engine-free so the match can be tested on synthetic feet.
/// </summary>
public static class StartMatch
{
    /// <summary>Weight of the toes' height against their horizontal position (a foot in the air against a foot on the ground is a worse mismatch than a few centimetres along the ground; ours).</summary>
    public const float HeightWeight = 2f;

    /// <summary>
    /// The index of the candidate (left toe, right toe) pair nearest to <paramref name="target"/>: the sum of the squared distances of the two toes, the height counted
    /// <see cref="HeightWeight"/> times. -1 for an empty list.
    /// </summary>
    public static int Nearest(IReadOnlyList<(Vector3 Left, Vector3 Right)> candidates, (Vector3 Left, Vector3 Right) target)
    {
        int best = -1;
        float bestCost = float.MaxValue;
        for (int i = 0; i < candidates.Count; i++)
        {
            float cost = Cost(candidates[i].Left, target.Left) + Cost(candidates[i].Right, target.Right);
            if (cost < bestCost) { bestCost = cost; best = i; }
        }
        return best;
    }

    static float Cost(Vector3 a, Vector3 b)
    {
        var d = a - b;
        return d.X * d.X + d.Y * d.Y + HeightWeight * HeightWeight * d.Z * d.Z;
    }

    /// <summary>The model-space toe positions of <paramref name="clip"/> at <paramref name="time"/> seconds (forward kinematics from the skeleton's reference pose; bones the clip does not drive keep it); null when the skeleton has no such toes.</summary>
    public static (Vector3 Left, Vector3 Right)? Toes(HavokSkeleton skeleton, HavokBoundAnimation clip, float time)
    {
        int lt = IndexOf(skeleton, ClipGait.LeftToe), rt = IndexOf(skeleton, ClipGait.RightToe);
        if (lt < 0 || rt < 0) return null;
        var local = new HavokTransform[skeleton.BoneNames.Count];
        for (int b = 0; b < local.Length; b++) local[b] = skeleton.ReferencePose[b];
        var sampled = clip.Animation.Sample(Math.Clamp(time, 0f, clip.Animation.Duration));
        for (int t = 0; t < sampled.Length; t++)
            if ((uint)clip.TrackToBone[t] < (uint)local.Length) local[clip.TrackToBone[t]] = sampled[t];
        var model = skeleton.ModelSpace(local);
        return (model[lt].Translation, model[rt].Translation);
    }

    /// <summary>
    /// The shared gait phase (<see cref="GaitPhase"/>) at which the run cycle <paramref name="run"/> (with its measured <paramref name="gait"/>) has its feet where the start clip
    /// has them at <paramref name="startTime"/>; null when the toes cannot be read.
    /// </summary>
    public static float? PhaseAfter(HavokSkeleton skeleton, HavokBoundAnimation start, float startTime, HavokBoundAnimation run, ClipGait gait, int samples = 256)
    {
        if (Toes(skeleton, start, startTime) is not { } target) return null;
        var candidates = new List<(Vector3, Vector3)>(samples);
        for (int i = 0; i < samples; i++)
            if (Toes(skeleton, run, run.Animation.Duration * i / samples) is { } toes) candidates.Add(toes);
        int best = Nearest(candidates, target);
        return best < 0 ? null : GaitPhase.PhaseAt(gait, run.Animation.Duration * best / samples);
    }

    static int IndexOf(HavokSkeleton skeleton, string bone)
    {
        for (int i = 0; i < skeleton.BoneNames.Count; i++)
            if (string.Equals(skeleton.BoneNames[i], bone, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }
}
