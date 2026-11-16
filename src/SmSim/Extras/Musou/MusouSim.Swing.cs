namespace SmSim.Extras.Musou;

/// <summary>
/// An Ork's melee swing commits to a direction (Musou Mode, owner 2026-11-12: "Make Ork attacks commit to a direction ... I'd test bounded early tracking followed by a committed swing, so positioning and
/// dodging feel more trustworthy"). Before, the wind-up turned the Ork straight at its target every tick, up to the very tick that checked whether the blow connected, so the cone check (which reads
/// the Ork's facing) could never fail: circling an Ork never dodged its swing, only stepping out of its reach did. Now the first <see cref="TrackShare"/> (30 %) of the wind-up turns toward the target at
/// <see cref="TrackTurnDegrees"/> and the rest holds the facing it reached; the blow lands on a target still within reach and within the swing's cone (<c>MeleeConeDegrees</c>, 60 each side) of that locked
/// facing. Every value ours, an estimate to check in play (spec "Musou Mode: Ork swing commit", refine register). Covers every melee Ork that winds up through <c>Behave</c>: the Boyz, Gretchin, Nobs,
/// the Boss Orks and the Warboss's ordinary swing; his ground slam (a ring round his feet, no direction) and charge (its line locked at its start) have their own rules in <c>MusouBoss</c> and stay as they
/// were. A war's pool only (the 5-wave run, without the crowd rules, as pinned); the restore switch <c>--musou-ork-swing-tracking</c> (<c>SimConfig.MusouOrkSwingTracking</c>, hashed only when on) brings the
/// old rule back.
/// </summary>
public static class MusouSwingRules
{
    /// <summary>The share of the wind-up (from its start) in which the Ork still turns toward its target; after it the facing is locked (ours: 4 of the Boys' 15 ticks, 0.13 s of 0.5 s; the 0.4 first tried left a player circling a Boy at a run, 5 m/s at 1.2 m, still inside the cone).</summary>
    public const float TrackShare = 0.3f;
    /// <summary>How fast it turns while tracking (degrees a second, ours): faster than an engaged Ork's 480 for walking, so a swing begun slightly off is still brought round (up to 72 degrees in the 0.13 s).</summary>
    public const float TrackTurnDegrees = 540f;

    /// <summary>The wind-up ticks (counted from the tick after it starts; at least 1, so a very short wind-up still turns) in which the Ork still tracks, of a wind-up of <paramref name="windUpTicks"/>.</summary>
    public static int TrackTicks(int windUpTicks) => Math.Clamp((int)MathF.Round(TrackShare * windUpTicks), 1, Math.Max(1, windUpTicks));
}

public sealed partial class MusouSim
{
    /// <summary>Restore switch (<c>--musou-ork-swing-tracking</c>; <c>SimConfig.MusouOrkSwingTracking</c>): a melee Ork's wind-up faces its target every tick until the blow, as before 2026-11-12. Tests set it per pool.</summary>
    public bool OldSwingTracking { get; set; }

    /// <summary>Whether the swing commits to a direction (a war's pool, not the restore switch).</summary>
    bool SwingRules => !OldSwingTracking && Swarm is not null;

    /// <summary>One wind-up tick: turns toward the target at <see cref="MusouSwingRules.TrackTurnDegrees"/> while the tracking share lasts, then keeps the facing it has.</summary>
    void TrackSwing(int i, System.Numerics.Vector2 toward, int windUpTicks)
    {
        if (_stateTicks[i] <= MusouSwingRules.TrackTicks(windUpTicks)) TurnTo(i, toward, MusouSwingRules.TrackTurnDegrees);
    }

    /// <summary>Test aid: whether enemy <paramref name="id"/> is in the locked part of a melee wind-up (it no longer follows its target).</summary>
    public bool SwingLocked(ushort id, int windUpTicks) => id >= 1 && id <= Capacity && _used[id - 1] && _state[id - 1] == MusouEnemyState.WindUp && _stateTicks[id - 1] > MusouSwingRules.TrackTicks(windUpTicks);
}
