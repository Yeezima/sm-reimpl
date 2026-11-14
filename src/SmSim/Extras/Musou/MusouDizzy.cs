namespace SmSim.Extras.Musou;

/// <summary>
/// The stunned boss's "dizzy" look (Musou Mode, owner 2026-11-14: "bosses turn around 180 degrees when stunned - fix that. create a new 'dizzy' animation for them where they stand in place and
/// stars appear above their head circling around, until they are no longer stunned"). A boss (the Warboss or a Boss Ork) in <see cref="MusouEnemyState.Stunned"/> is drawn standing on its idle clip, facing where it
/// faced, swaying in a slow wobble, with stars circling above its head; both stop the tick the stun ends. The stagger clip it drew before is not used: the Warboss's pick, <c>WB_Att_Charge_wall_hit_exit</c>, turns
/// the body, and the sim's yaw never changed (<see cref="MusouSim.StaggerMelee"/>). **No original equivalent**: the original has no dizzy animation, so every value here is ours (spec "Musou Mode: bosses and
/// melee stuns", refine register). Client-only: the snapshot already carries the state.
/// </summary>
public static class MusouDizzyRules
{
    /// <summary>Whether an enemy of <paramref name="type"/> in <paramref name="state"/> is drawn dizzy: a boss in a stun. The Weirdboy's broken shield is a stun too, but he is no boss.</summary>
    public static bool Dizzy(MusouEnemyType type, MusouEnemyState state) =>
        state == MusouEnemyState.Stunned && (type == MusouEnemyType.Warboss || MusouBossOrks.Is(type));

    /// <summary>Stars circling the head (ours).</summary>
    public const int StarCount = 3;
    /// <summary>Seconds for a star to go once round (ours).</summary>
    public const float StarOrbitSeconds = 1.1f;
    /// <summary>The stars' height above the head's top, in body heights (ours).</summary>
    public const float StarLift = 0.07f;
    /// <summary>Where the head's top is, in body heights measured to the model's top (ours): the Warboss's back banner stands above his head, a Boss Ork's head is its top.</summary>
    public static float HeadFraction(MusouEnemyType type) => type == MusouEnemyType.Warboss ? 0.74f : 1f;
    /// <summary>The stars' height above the feet, in body heights.</summary>
    public static float StarHeight(MusouEnemyType type) => HeadFraction(type) + StarLift;
    /// <summary>The stars' orbit radius in body heights (ours).</summary>
    public const float StarOrbitRadius = 0.17f;
    /// <summary>A star's outer radius in body heights (ours).</summary>
    public const float StarSize = 0.06f;
    /// <summary>How far the body leans in its wobble (degrees, ours).</summary>
    public const float SwayDegrees = 4f;
    /// <summary>Seconds for the lean to go once round the body (ours).</summary>
    public const float SwaySeconds = 1.8f;
    /// <summary>Seconds the wobble takes to build up from standing straight (ours).</summary>
    public const float SwayRampSeconds = 0.15f;

    /// <summary>Star <paramref name="index"/>'s angle round the head (radians) <paramref name="seconds"/> into the stun: the stars are evenly spaced and circle together.</summary>
    public static float StarAngle(int index, float seconds) =>
        MathF.Tau * (seconds / StarOrbitSeconds + (float)index / StarCount);

    /// <summary>The body's lean <paramref name="seconds"/> into the stun: the angle (radians) and the direction it leans toward (radians in the ground plane, 0 along +X). A circular wobble, built up over <see cref="SwayRampSeconds"/>.</summary>
    public static (float Angle, float Toward) Sway(float seconds)
    {
        float ramp = Math.Clamp(seconds / SwayRampSeconds, 0f, 1f);
        return (SwayDegrees * MathF.PI / 180f * ramp, MathF.Tau * seconds / SwaySeconds);
    }
}
