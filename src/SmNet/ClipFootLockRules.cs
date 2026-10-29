namespace SmNet;

/// <summary>
/// anim/melee-flow (the owner, 2026-10-29: the sword slash "feels quite janky"; "make sure the clips' root motion versus the simulated motion doesn't slide the feet"): the engine-free
/// decisions of the clip foot lock, client presentation only (ours throughout; the client's <c>CharacterViewer.ClipFootLock.cs</c> applies them).
/// <para>
/// The melee, charge and other action clips are in place: their root motion is extracted, and the simulation moves the body instead. Where the body does not travel what the clip's
/// root travelled (the attacker is stopped at its target, the lunge is longer or shorter than the clip's, the body turns to its target, a remote body's position steps with the
/// snapshots) a foot the clip holds still on the ground slides with the body: measured on a melee demo, planted feet under the attack clips moved at a median 1.9 m/s and a planted
/// stretch slid a median 57 cm. The lock holds such a foot where it was put down (two-bone IK to the held ankle) while the clip keeps it planted, and lets go over
/// <see cref="ReleaseSeconds"/> when the clip lifts it, so the step itself is the clip's.
/// </para>
/// </summary>
public static class ClipFootLockRules
{
    /// <summary>A foot is put down in the clip when its toe is lower than this above the marine's feet (m) and its ankle lower than <see cref="PlantAnkle"/>: the transition and animation
    /// probes' flat-foot test (toe 0.02 m, ankle 0.24 m) with a little margin, so the lock takes the foot the probe counts.</summary>
    public const float PlantToe = 0.03f, PlantAnkle = 0.24f;
    /// <summary>A held foot is let go when its toe rises above this (m) or its ankle above <see cref="LiftAnkle"/> (the hysteresis of the probe's exit heights).</summary>
    public const float LiftToe = 0.045f, LiftAnkle = 0.27f;
    /// <summary>How long a let-go foot takes to reach the clip's own foot (s; ours): short, the clip is already lifting it.</summary>
    public const float ReleaseSeconds = 0.1f;
    /// <summary>A held ankle the leg cannot reach (farther than its length times this from the hip) is dragged along on the edge of the reach instead (ours).</summary>
    public const float ReachFraction = 0.995f;

    /// <summary>Whether a foot counts as put down in the clip this frame (with hysteresis on <paramref name="wasPlanted"/>). Non-finite heights are never planted.</summary>
    public static bool Planted(bool wasPlanted, float toeHeight, float ankleHeight)
    {
        if (!float.IsFinite(toeHeight) || !float.IsFinite(ankleHeight)) return false;
        return wasPlanted ? toeHeight < LiftToe && ankleHeight < LiftAnkle : toeHeight < PlantToe && ankleHeight < PlantAnkle;
    }

    /// <summary>The share of the held position in the ankle <paramref name="since"/> seconds after the foot was let go: 1 at the release, 0 after <see cref="ReleaseSeconds"/>, on the smooth curve.</summary>
    public static float ReleaseHold(float since) => ReleaseHold(since, ReleaseSeconds);

    /// <summary>As <see cref="ReleaseHold(float)"/> over <paramref name="duration"/> seconds (<see cref="ReleaseDuration"/>).</summary>
    public static float ReleaseHold(float since, float duration)
    {
        if (!(since > 0f)) return 1f;
        if (!(duration > 0f) || since >= duration) return 0f;
        float x = since / duration;
        return 1f - x * x * (3f - 2f * x);
    }

    /// <summary>
    /// How long a let-go foot takes to go from where it was held to the clip's foot <paramref name="distance"/> metres away (s; ours): at most <see cref="ReleaseSpeed"/> on average, at least
    /// <see cref="ReleaseSeconds"/>, at most <see cref="MaxReleaseSeconds"/>. A combo's next strike that lifts a foot held half a metre from its own pose let it go at 5 m/s in 0.1 s
    /// (the transition probe: the power sword's second strike 6 → 27 cm per frame per frame at the toe).
    /// </summary>
    public static float ReleaseDuration(float distance) =>
        float.IsFinite(distance) && distance > 0f ? Math.Clamp(distance / ReleaseSpeed, ReleaseSeconds, MaxReleaseSeconds) : ReleaseSeconds;

    /// <summary>
    /// Whether a foot the clip puts down stands still where the clip was authored (its root moving): the clip's toe moves at (<paramref name="vx"/>, <paramref name="vy"/>) m/s in the
    /// in-place clip (x right, y forward, per second of the clip) while the extracted root moves forward at <paramref name="rootSpeed"/>; a foot planted in the authored world moves back at
    /// the root's speed in the in-place clip, a foot that skims or drags along the ground does not. Only such a foot starts a hold (a lunge's dragged foot is left to slide as authored:
    /// holding it stopped it dead in one frame, 45 cm per frame per frame at the power sword's second strike). Without a root speed (NaN) every foot down qualifies.
    /// </summary>
    public static bool AuthoredStill(float vx, float vy, float rootSpeed)
    {
        if (!float.IsFinite(rootSpeed)) return true;
        if (!float.IsFinite(vx) || !float.IsFinite(vy)) return false;
        float wy = vy + rootSpeed;
        return vx * vx + wy * wy < StillSpeed * StillSpeed;
    }

    /// <summary>The largest authored-world speed of a foot that counts as still (m/s; ours).</summary>
    public const float StillSpeed = 4f;

    /// <summary>The let-go foot's mean speed toward the clip's (m/s; ours) and the longest release (s; ours).</summary>
    public const float ReleaseSpeed = 2f, MaxReleaseSeconds = 0.35f;

    /// <summary>
    /// The distance (m) from the hip at which the ankle is put for a held position <paramref name="hipToHeld"/> away with a leg of <paramref name="legLength"/>: the held one, or the edge of the
    /// reach when it is farther (the foot is then dragged). <paramref name="dragged"/> says so.
    /// </summary>
    public static float Reach(float hipToHeld, float legLength, out bool dragged)
    {
        float max = legLength * ReachFraction;
        dragged = !(hipToHeld <= max);
        return dragged ? (float.IsFinite(max) && max > 0f ? max : 0f) : hipToHeld;
    }
}
