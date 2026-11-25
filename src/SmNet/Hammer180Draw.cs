using SmSim;

namespace SmNet;

/// <summary>
/// sim/hammer-180 (review fixes; presentation only, no simulation or wire change): how the client draws a Dir 180 strike (<see cref="MeleeStep.TurnsBack"/>) round its clip's root turn.
/// <para>
/// The clips (<c>SM_MEL_TH_XATK_180_MP</c>, <c>SM_MEL_TH_XATK_180_combo2</c>, 30 samples a second) turn their extracted root half round in one sample (<see cref="MeleeStep.TurnAt"/>, 0.7 and 0.633 s) and their
/// bones the opposite half turn in the same sample. The spline sampler interpolates the bones across that sample (measured on the install: the pelvis spins about 160 degrees between 0.667 and 0.700 s of
/// the 180, between 0.600 and 0.633 s of the Left Side), while the drawn root turns at once: drawn as it was, the body spun most of a half turn in one frame and popped back. <see cref="ClipTime"/> shows the
/// clip at the jump's two ends only (the sample before it for the first half of the interval, the jump's own sample for the second half) and <see cref="Unturned"/> turns the drawn root at the interval's middle,
/// so pose and root change together: at most half a sample (17 ms) held, no spin.
/// </para>
/// </summary>
public static class Hammer180Draw
{
    /// <summary>The clips' sample interval (both clips: 1/30 s, read on the install, <c>Hammer180Tests</c>).</summary>
    public const float SampleSeconds = 1f / 30f;

    /// <summary>The clip time at which the drawn body takes the strike's heading (before it: the heading less half a turn): the middle of the sample interval that ends at <see cref="MeleeStep.TurnAt"/>.</summary>
    public static float FlipAt(MeleeStep m) => m.TurnAt - SampleSeconds / 2f;

    /// <summary>Whether a <see cref="MeleeStep.TurnsBack"/> strike drawn at clip time <paramref name="clipTime"/> is still drawn facing the press-time way (the heading less half a turn).</summary>
    public static bool Unturned(MeleeStep m, float clipTime) => m.TurnsBack && float.IsFinite(m.TurnAt) && clipTime < FlipAt(m);

    /// <summary>
    /// The clip time to sample a strike at: inside the sample interval that ends at a turn strike's <see cref="MeleeStep.TurnAt"/>, the interval's start before <see cref="FlipAt"/> and its end from it
    /// (the bones' jump is not interpolated); any other time, and any other strike, unchanged.
    /// </summary>
    public static float ClipTime(MeleeStep m, float clipTime)
    {
        if (!m.TurnsBack || !float.IsFinite(m.TurnAt) || !float.IsFinite(clipTime)) return clipTime;
        float start = m.TurnAt - SampleSeconds;
        if (clipTime <= start || clipTime >= m.TurnAt) return clipTime;
        return clipTime < FlipAt(m) ? start : m.TurnAt;
    }

    /// <summary>
    /// A remote player's yaw between two snapshots: interpolated, except across a change of strike that starts or leaves a Dir 180 one (the facing flips half round there while the strike drawn is the
    /// older snapshot's): the older snapshot's yaw, so the body does not sweep a half turn through the interval and pop back at its end. Every other case (and a <paramref name="config"/> of null) as before.
    /// </summary>
    public static bool HoldsYaw(SimConfig? config, PlayerState a, PlayerState b)
    {
        if (config is null || (a.MeleeStep == b.MeleeStep && a.MeleeHeavy == b.MeleeHeavy && !(b.MeleeStep > 0 && b.MeleeTime + 1e-4f < a.MeleeTime))) return false;
        return Combat.MeleeStepOf(config, a) is { TurnsBack: true } || Combat.MeleeStepOf(config, b) is { TurnsBack: true };
    }
}
