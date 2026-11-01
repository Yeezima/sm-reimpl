namespace SmNet;

/// <summary>
/// anim/hammer-swing (the owner, 2026-11-01: "there needs to be a separate air slash landing for hammer ... the same move in terms of damage, stagger, visual effects, it just has a different
/// player model animation"; client presentation only: no simulation, wire or config-hash change): where the jump pack's air slash clip is posed when the marine holds the thunder hammer
/// or the daemon maul. The jump-pack graph (<c>MP_SM_NAV_JP_Sim</c>, <c>smtool graph-dump MP_SM_NAV_JP_Sim</c>) has a separate "Hammer Dash State" beside the "Non-Hammer Dash State"
/// (start state chosen by <c>BOOL_Thunderhammer</c>), as the ground pound has its "Hammer GP":
/// <list type="bullet">
/// <item>"Hammer Dash Flight" (<c>SequencerGenerator13</c>): <c>SM_MEL_TH_XATK_Combo3</c> (the hammer's second standard swing, 2.333 s) at speed 0.2, the clip generator's crop 0.4333 s from the
/// start and 1.8 s off the end: the clip from 0.4333 to 0.5333 s, the hammer raised for the blow, slowly (0.5 s for the 0.1 s).</item>
/// <item>"Hammer Dash Impact" ("Hammer Dash Sequence"): the same clip at speed 0.8, crop 0.5667 s from the start (the swing's own hit is at 0.567 s: the blow lands as the clip starts), to its end
/// (2.333 s); its hit events at 0.0875 of the sequence, <c>BRANCH_Run</c> from 0.2642, <c>BRANCH_Jump_To_Melee</c> from 0.6, <c>BRANCH_Jump_To_Idle</c> from 0.6857. The simulation's hit
/// and recovery stay the sword slash's (<c>SlashSpec</c>: the owner, "the same move in terms of damage, stagger, visual effects").</item>
/// </list>
/// The sword's slash plays <c>SM_MEL_CS_XATK_Hv1_LS_2_7m</c> in <see cref="SlashPose"/>'s fitted curves; the hammer's own clips play at the data's rates, held at the wind-up's end in a long dive,
/// and a dive that lands early catches up to the strike over <see cref="SlashPose.EasedCatchUp"/> on the smooth curve (as the sword's). The numbers are read off the graph by hand (the reader
/// takes the sword's sequencers only): data; the playback is the graph's.
/// </summary>
public static class HammerSlashPose
{
    /// <summary>The clip both sequencers play.</summary>
    public const string Clip = "SM_MEL_TH_XATK_Combo3";
    /// <summary>The flight sequencer's crop: the clip time it starts at and ends at (s of the clip), and its speed.</summary>
    public const float FlightStart = 0.4333f, FlightEnd = 0.5333f, FlightRate = 0.2f;
    /// <summary>The impact sequencer's crop start (s of the clip) and speed.</summary>
    public const float ImpactStart = 0.5667f, ImpactRate = 0.8f;

    /// <summary>True for the wargear that takes the hammer's dash state (<c>BOOL_Thunderhammer</c>): the thunder hammer and the daemon maul (the Raptor's twin).</summary>
    public static bool IsHammer(string? wargear) =>
        wargear is not null && (wargear.Contains("thunder_hammer", StringComparison.OrdinalIgnoreCase) || wargear.Contains("power_mace", StringComparison.OrdinalIgnoreCase));

    /// <summary>The clip time <paramref name="sinceDive"/> s into the dive: from <see cref="FlightStart"/> at <see cref="FlightRate"/>, held at <see cref="FlightEnd"/>.</summary>
    public static float DiveTime(float sinceDive) => MathF.Min(FlightStart + FlightRate * MathF.Max(0f, sinceDive), FlightEnd);

    /// <summary>
    /// The clip time <paramref name="sinceImpact"/> s after the landing: <see cref="ImpactStart"/> plus <see cref="ImpactRate"/> times the time, at most <paramref name="clipLength"/>. A dive that
    /// landed with the clip at <paramref name="landedAt"/> (before <see cref="ImpactStart"/>) moves from there to the strike's time over <paramref name="catchUp"/> s on the smooth curve
    /// (never backwards), so the landing has no pop.
    /// </summary>
    public static float ImpactTime(float sinceImpact, float landedAt, float clipLength, float catchUp = SlashPose.EasedCatchUp)
    {
        float since = MathF.Max(0f, sinceImpact), target = ImpactStart + ImpactRate * since;
        if (float.IsFinite(landedAt) && landedAt < ImpactStart && catchUp > 0f && since < catchUp)
        {
            float x = since / catchUp, k = x * x * (3f - 2f * x);
            target = landedAt + (target - landedAt) * k;
        }
        return Math.Clamp(target, 0f, MathF.Max(clipLength, ImpactStart));
    }

    /// <summary>Seconds after the landing until the clip's end (what the get-up can show of it).</summary>
    public static float AfterImpact(float clipLength) => MathF.Max(0f, clipLength - ImpactStart) / ImpactRate;
}
