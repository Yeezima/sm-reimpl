using System.Numerics;
using SmFormats.Havok;

namespace SmContent;

/// <summary>
/// The gait of a looping locomotion clip, measured from where its feet stand on the ground (M40-T4; client presentation, ours): the ground distance one
/// cycle moves the body (<see cref="Stride"/>) and the two foot-plant times the clip's phase is synchronised by. Engine-free; built from the toe and
/// ankle bones' positions over the cycle (<see cref="FromFeet"/>) or from a clip and the skeleton (<see cref="Analyze"/>).
/// <para>
/// Space: the character's own, X right, Y forward, Z up (the rig's). A foot is <b>planted</b> (flat on the ground) while its toe is within
/// <see cref="ToeBand"/> of that toe's lowest point in the cycle and its ankle within <see cref="AnkleBand"/> of the ankle's lowest (so the heel is down: the
/// toe-pivot at the end of a stance, with the heel lifting, is not a standing foot). A planted foot stands still on the ground, so it moves backward
/// against the body by exactly the body's ground velocity: <see cref="Stride"/> = minus the <b>median</b> planted toe velocity times the clip's length
/// (the median, because the toe rolls onto and off the ground at other speeds at the ends of the stretch). This is the quantity that stops a foot sliding when
/// the clip is played at <c>cadence x length</c> times its own speed and blended with others by their strides (<see cref="GaitSpace"/>). The foot-plant times
/// are the original's <c>SYNC_L1</c> / <c>SYNC_R1</c> events (declared in the navigation graphs, but no sequencer in the install carries them as a tag and
/// the clips have no annotation tracks, so they are derived from the motion: the circular mean of the planted times of each foot).
/// </para>
/// </summary>
/// <param name="Duration">Seconds (the loop).</param>
/// <param name="SyncLeft">Seconds into the clip at the middle of the left foot's planted stretch.</param>
/// <param name="SyncRight">Seconds into the clip at the middle of the right foot's planted stretch.</param>
/// <param name="Stride">Metres the body moves per cycle (X right, Y forward), from the planted feet.</param>
/// <param name="PlantedFraction">Mean fraction of the cycle a foot is planted (0 to 1).</param>
public sealed record ClipGait(float Duration, float SyncLeft, float SyncRight, Vector2 Stride, float PlantedFraction)
{
    /// <summary>A toe counts as planted within this height (m) of its lowest point in the cycle, an ankle within <see cref="AnkleBand"/> of its lowest.</summary>
    public const float ToeBand = 0.015f, AnkleBand = 0.04f;

    /// <summary>
    /// The planted foot's velocity is taken as <c>MeanShare</c> x the mean + (1 - MeanShare) x the median of its samples (ours). The median is the speed of the
    /// flat part of the stance, which is what a viewer sees slide; the mean is the speed that returns the foot to where it landed, which keeps the whole step from
    /// drifting (the toe rolls on and off the ground at other speeds, so the two differ by up to 1 m/s on the back run). Chosen on the M40-T4 sweep demo (spec/behavior.md
    /// "Locomotion blend space"): 0.0 left steps of 18 cm on the back headings of the rifle demo; 0.3 steps of 15.5 to 15.9 cm on two demos; 0.4 steps of 12.1 to 13.6 cm and the flat foot sliding at 0.28 to 0.30 m/s (the measure's noise is about 0.03 m/s). 0.4 is used.
    /// </summary>
    public const float MeanShare = 0.4f;

    /// <summary>The planted fraction of the cycle from which the full <see cref="MeanShare"/> applies (below it, in proportion; ours).</summary>
    public const float FullShareFraction = 0.3f;

    /// <summary>Metres per second the clip moves the body at its own speed (the planted feet's ground speed).</summary>
    public float Speed => Duration > 0f ? Stride.Length() / Duration : 0f;

    /// <summary>Whether the numbers can be used to blend (a real stride, both feet found, the sync times inside the clip).</summary>
    public bool Usable => float.IsFinite(Duration) && Duration > 0.05f && float.IsFinite(Stride.X) && float.IsFinite(Stride.Y) && Stride.Length() > 0.3f
        && float.IsFinite(SyncLeft) && float.IsFinite(SyncRight) && PlantedFraction is > 0.03f and < 0.95f;

    /// <summary>
    /// Whether a clip that steps on the spot (the walk-on-the-spot clips, animation pass 1) can be blended as the zero-speed end of a speed blend: its planted feet do not move
    /// (<see cref="Stride"/> under 0.3 m a cycle, so its <see cref="Speed"/> is about 0 m/s: measured 0.01 m/s on <c>SM_NAV_WOS_F_01</c>, 1.94 m/s on <c>SM_NAV_Walk_F_01</c>), both feet are found
    /// and the sync times are inside the clip, so the cycle can share the walk's foot-plant phase.
    /// </summary>
    public bool UsableInPlace => float.IsFinite(Duration) && Duration > 0.05f && float.IsFinite(Stride.X) && float.IsFinite(Stride.Y) && Stride.Length() <= InPlaceStride
        && float.IsFinite(SyncLeft) && float.IsFinite(SyncRight) && PlantedFraction is > 0.03f and < 0.95f;

    /// <summary>The largest stride (m a cycle) of a clip that counts as stepping in place.</summary>
    public const float InPlaceStride = 0.3f;

    /// <summary>
    /// The gait from the feet's positions at <c>N</c> evenly spaced times over one cycle (sample <c>i</c> at <c>i x duration / N</c>; the cycle wraps):
    /// both toes and, when given, both ankles. Null when there are too few samples or a foot is never planted.
    /// </summary>
    public static ClipGait? FromFeet(float duration, IReadOnlyList<Vector3> leftToe, IReadOnlyList<Vector3> rightToe,
        IReadOnlyList<Vector3>? leftAnkle = null, IReadOnlyList<Vector3>? rightAnkle = null, float toeBand = ToeBand, float ankleBand = AnkleBand)
    {
        int n = leftToe.Count;
        if (n < 8 || rightToe.Count != n || !(duration > 0f) || !float.IsFinite(duration)) return null;
        if ((leftAnkle is not null && leftAnkle.Count != n) || (rightAnkle is not null && rightAnkle.Count != n)) return null;
        float dt = duration / n;
        var toes = new[] { leftToe, rightToe };
        var ankles = new[] { leftAnkle, rightAnkle };
        var sync = new float[2];
        var vx = new List<float>();
        var vy = new List<float>();
        int plantedTotal = 0;
        for (int f = 0; f < 2; f++)
        {
            var p = toes[f];
            var ankle = ankles[f];
            float minToe = float.MaxValue, minAnkle = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                minToe = MathF.Min(minToe, p[i].Z);
                if (ankle is not null) minAnkle = MathF.Min(minAnkle, ankle[i].Z);
            }
            var planted = new bool[n];
            double sx = 0, sy = 0;
            int count = 0;
            for (int i = 0; i < n; i++)
            {
                planted[i] = p[i].Z < minToe + toeBand && (ankle is null || ankle[i].Z < minAnkle + ankleBand);
                if (!planted[i]) continue;
                count++;
                double a = 2 * Math.PI * i / n;
                sx += Math.Cos(a); sy += Math.Sin(a);
            }
            if (count == 0) return null;
            plantedTotal += count;
            double angle = Math.Sqrt(sx * sx + sy * sy) < 1e-6 ? 0.0 : Math.Atan2(sy, sx);
            sync[f] = (float)(((angle / (2 * Math.PI)) % 1.0 + 1.0) % 1.0) * duration;
            for (int i = 0; i < n; i++)
            {
                int a = (i + n - 1) % n, b = (i + 1) % n;
                if (!planted[a] || !planted[i] || !planted[b]) continue;
                vx.Add((p[b].X - p[a].X) / (2f * dt));
                vy.Add((p[b].Y - p[a].Y) / (2f * dt));
            }
        }
        if (vx.Count == 0) return null;
        float plantedFraction = plantedTotal / (2f * n);
        // The mean's share shrinks with a short planted stretch (the sprint's is a tenth of the cycle): there the few samples at the ends, where the heel lifts, weigh
        // too much in the mean and the flat part would slide.
        float share = MeanShare * Math.Clamp(plantedFraction / FullShareFraction, 0f, 1f);
        var stride = -new Vector2(share * vx.Average() + (1 - share) * Median(vx), share * vy.Average() + (1 - share) * Median(vy)) * duration;
        return new ClipGait(duration, sync[0], sync[1], stride, plantedFraction);
    }

    static float Median(List<float> values)
    {
        values.Sort();
        int n = values.Count;
        return n % 2 == 1 ? values[n / 2] : 0.5f * (values[n / 2 - 1] + values[n / 2]);
    }

    /// <summary>The names of the toe and ankle bones of the marine rig (the contact points the gait is measured at).</summary>
    public const string LeftToe = "RIG_L_Toe", RightToe = "RIG_R_Toe", LeftAnkle = "RIG_L_Foot", RightAnkle = "RIG_R_Foot";

    /// <summary>
    /// The gait of a clip on a skeleton: the clip is sampled <paramref name="samples"/> times over its length, the skeleton posed (forward
    /// kinematics from its reference pose, bones the clip does not drive keep it) and the feet's model-space positions handed to <see cref="FromFeet"/>.
    /// Null when the skeleton has no such bones or the clip has no planted feet.
    /// </summary>
    public static ClipGait? Analyze(HavokSkeleton skeleton, HavokBoundAnimation clip, int samples = 64, FootBones? feet = null)
    {
        feet ??= FootBones.Marine;
        int lt = IndexOf(skeleton, feet.LeftToe), rt = IndexOf(skeleton, feet.RightToe), la = IndexOf(skeleton, feet.LeftAnkle), ra = IndexOf(skeleton, feet.RightAnkle);
        if (lt < 0 || rt < 0 || la < 0 || ra < 0 || samples < 8) return null;
        float duration = clip.Animation.Duration;
        var lToe = new Vector3[samples];
        var rToe = new Vector3[samples];
        var lAnkle = new Vector3[samples];
        var rAnkle = new Vector3[samples];
        var local = new HavokTransform[skeleton.BoneNames.Count];
        for (int i = 0; i < samples; i++)
        {
            for (int b = 0; b < local.Length; b++) local[b] = skeleton.ReferencePose[b];
            var sampled = clip.Animation.Sample(duration * i / samples);
            for (int t = 0; t < sampled.Length; t++)
                if ((uint)clip.TrackToBone[t] < (uint)local.Length) local[clip.TrackToBone[t]] = sampled[t];
            var model = skeleton.ModelSpace(local);
            lToe[i] = model[lt].Translation; rToe[i] = model[rt].Translation;
            lAnkle[i] = model[la].Translation; rAnkle[i] = model[ra].Translation;
        }
        return FromFeet(duration, lToe, rToe, lAnkle, rAnkle);
    }

    static int IndexOf(HavokSkeleton skeleton, string bone)
    {
        for (int i = 0; i < skeleton.BoneNames.Count; i++)
            if (string.Equals(skeleton.BoneNames[i], bone, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }
}

/// <summary>
/// The shared phase of a gait (M40-T4): 0 at the left foot's plant, 0.5 at the right foot's, back to 1 at the next left plant, with the clip's time
/// mapped piecewise-linearly between the two sync points. Blended clips that play at the same phase therefore have the same foot on the ground, whatever
/// their lengths or where in the file the stride starts (what the original's <c>SYNC_L1</c> / <c>SYNC_R1</c> events were for).
/// </summary>
public static class GaitPhase
{
    /// <summary>The phase (0 to 1) of a time in the clip.</summary>
    public static float PhaseAt(ClipGait clip, float time)
    {
        float t = Wrap(time - clip.SyncLeft, clip.Duration);
        float first = FirstSpan(clip);
        float phase = t < first ? 0.5f * t / first : 0.5f + 0.5f * (t - first) / (clip.Duration - first);
        return Wrap(phase, 1f);
    }

    /// <summary>The time in the clip (0 to its length) at a phase.</summary>
    public static float TimeAt(ClipGait clip, float phase)
    {
        phase = Wrap(phase, 1f);
        float first = FirstSpan(clip);
        float t = phase < 0.5f ? phase / 0.5f * first : first + (phase - 0.5f) / 0.5f * (clip.Duration - first);
        return Wrap(clip.SyncLeft + t, clip.Duration);
    }

    /// <summary>Seconds from the left plant to the right plant; half the clip when the two sync times are too close to each other for a plausible stride (bad data).</summary>
    static float FirstSpan(ClipGait clip)
    {
        float first = Wrap(clip.SyncRight - clip.SyncLeft, clip.Duration);
        return first < 0.2f * clip.Duration || first > 0.8f * clip.Duration ? 0.5f * clip.Duration : first;
    }

    /// <summary>The value wrapped into [0, period).</summary>
    public static float Wrap(float value, float period)
    {
        if (!(period > 0f) || !float.IsFinite(value)) return 0f;
        float r = value - period * MathF.Floor(value / period);
        return r >= period ? 0f : r;
    }
}
