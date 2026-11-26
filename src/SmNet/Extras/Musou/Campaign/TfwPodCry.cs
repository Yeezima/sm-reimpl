using SmSim.Extras.Musou.Campaign;

namespace SmNet.Extras.Musou.Campaign;

/// <summary>
/// The pod Marines' battle cry, driven by the pod (Phase B T2b review fix, 2026-11-25): the cry's <see cref="TfwDropPodRules.Marines"/> slots are timed from the pod's record alone (its tick, the step-out
/// and <see cref="TfwDropPodRules.ShoutDelay"/>), not from the Marines a snapshot happens to carry, and each slot's speaker is chosen when it begins: the first of the pod's Marines in sight (by id) that has
/// not cried yet. A crowded Warfront snapshot sweeps the pod Marines across snapshots (<see cref="LeanSchedule"/>), so a slot with none in sight there is cried from the pod itself
/// (<see cref="Speaker"/> null and <c>podVoice</c>); elsewhere (Musou Mode, every Marine always in the snapshot) such a slot stays silent, as before. Pure and engine-free.
/// </summary>
public static class TfwPodCry
{
    /// <summary>The tick each slot begins: the Marines' step-out (<paramref name="podTick"/> + door and step-out seconds) plus slot k's <see cref="TfwDropPodRules.ShoutDelay"/>, at <paramref name="rate"/> ticks a second.</summary>
    public static double[] SlotTicks(double podTick, int rate)
    {
        double spawn = podTick + (TfwDropPodRules.DoorSeconds + TfwDropPodRules.StepOutSeconds) * rate;
        var ticks = new double[TfwDropPodRules.Marines];
        for (int k = 0; k < ticks.Length; k++) ticks[k] = spawn + TfwDropPodRules.ShoutDelay((uint)podTick, k) * rate;
        return ticks;
    }

    /// <summary>A slot's speaker: the lowest id of <paramref name="inSight"/> (the pod's Marines drawn now) not in <paramref name="cried"/>; null when there is none (the pod's own voice, or silence).</summary>
    public static int? Speaker(IEnumerable<int> inSight, IReadOnlyCollection<int> cried)
    {
        int? best = null;
        foreach (int id in inSight)
            if (!cried.Contains(id) && (best is null || id < best)) best = id;
        return best;
    }
}
