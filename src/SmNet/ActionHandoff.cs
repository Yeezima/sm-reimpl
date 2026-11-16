namespace SmNet;

/// <summary>
/// The rules by which an action clip (a melee attack, a grenade throw) hands the legs to the locomotion blend while the body moves (M40-T4 follow-up; client
/// presentation, ours; engine-free so they can be tested like <see cref="ViewSmoothing"/>). The simulation and the protocol never use them.
/// </summary>
public static class ActionHandoff
{
    /// <summary>Ground speed (m/s) above which a marine under an action clip counts as moving.</summary>
    public const float MovingSpeed = 0.5f;
    /// <summary>The action clip's weight on the upper body falls to 0 over its last this many seconds.</summary>
    public const float TailSeconds = 0.15f;
    /// <summary>A throw that starts while the body moves lays the toss over the upper body in this long.</summary>
    public const float HeadSeconds = 0.1f;

    /// <summary>Whether the body moves for the hand-off: alive, on the ground, not in a jump-pack phase, faster than <see cref="MovingSpeed"/> over the ground.</summary>
    public static bool Moving(bool alive, bool grounded, bool jumpNone, float horizontalSpeed) =>
        alive && grounded && jumpNone && horizontalSpeed > MovingSpeed;

    /// <summary>
    /// Whether the attack the marine is in is a different one from the last frame's (a new step or heavy / light switch, or the attack time ran backward), so a
    /// hand-off made under the old one ends. The state's own time, not the posed (smoothed) one, tells.
    /// </summary>
    public static bool AttackChanged(int lastStep, bool lastHeavy, float lastTime, int step, bool heavy, float time) =>
        step != lastStep || heavy != lastHeavy || time + 1e-4f < lastTime;

    /// <summary>Whether an attack at posed time <paramref name="time"/> hands over: its movement lock (<paramref name="moveAt"/>) is over and the body moves.</summary>
    public static bool AttackHandsOver(float time, float moveAt, bool moving) => moving && time >= moveAt;

    /// <summary>The weight of the action clip on the upper body at <paramref name="time"/> of a clip of <paramref name="duration"/> seconds: 1, falling to 0 over the last <see cref="TailSeconds"/>.</summary>
    public static float TailWeight(float duration, float time) => Math.Clamp((duration - time) / TailSeconds, 0f, 1f);

    /// <summary>The weight of a toss at <paramref name="time"/>: the tail of <see cref="TailWeight"/>, rising over its first <see cref="HeadSeconds"/>.</summary>
    public static float ThrowWeight(float duration, float time) => MathF.Min(TailWeight(duration, time), time < HeadSeconds ? Math.Max(0f, time) / HeadSeconds : 1f);

    /// <summary>
    /// feel/grenades-quickdraw (the owner, 2026-11-06): the toss clip's time for a throw <paramref name="simTime"/> seconds in, when the simulation releases at <paramref name="simRelease"/> and
    /// the clip at <paramref name="clipRelease"/> (the toss graph's <c>Ability_Ranged_Fire</c>): the wind-up is played faster so the hand opens on the release tick; after it the clip runs at its own
    /// speed. Equal releases (or none): the throw's own time.
    /// </summary>
    public static float TossClipTime(float simTime, float simRelease, float clipRelease)
    {
        if (!float.IsFinite(simTime) || simTime < 0f) simTime = 0f;
        if (!(simRelease > 1e-4f) || !float.IsFinite(clipRelease) || clipRelease <= 0f || MathF.Abs(simRelease - clipRelease) < 1e-5f) return simTime;
        return simTime < simRelease ? simTime / simRelease * clipRelease : clipRelease + (simTime - simRelease);
    }

    /// <summary>
    /// Stream B (the owner, 2026-11-06 late: "let the grenade throw animation play out a little longer for both sides"): whether the toss clip's follow-through after the simulation's
    /// throw has ended goes on at clip time <paramref name="clipTime"/>: until the clip's end (<paramref name="clipEnd"/>, the toss graph's <c>BRANCH_UseEquipment_End</c>) unless
    /// another action shows (<paramref name="busy"/>).
    /// </summary>
    public static bool ThrowFollowContinues(float clipTime, float clipEnd, bool busy) =>
        !busy && float.IsFinite(clipTime) && float.IsFinite(clipEnd) && clipTime < clipEnd;

    /// <summary>Whether a throw at <paramref name="time"/> is a new throw (its time ran backward since the last frame).</summary>
    public static bool ThrowRestarted(float lastTime, float time) => time < lastTime;
}
