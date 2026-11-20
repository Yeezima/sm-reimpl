namespace SmSim;

/// <summary>
/// sim/stab-release (the owner, 2026-11-20): "currently when using the T 'stab' move with power sword ... the player character cannot initiate another action for a long time after the stab animation
/// finishes playing. allow the player to act again as soon as the stab animation releases and the 'hit or miss' moment has occurred." The stab (<see cref="MeleeStep.Stab"/>: the power sword's X,F thrust
/// clip, 1.818 s with its hit at 0.636 s) locked the player to the end of its clip (shots from its BRANCH_ToRanged and the hop from its branch, the stick from its BRANCH_Run at 0.909 s, everything else, a switch, a grenade,
/// a second melee press or stab, not until the clip's end at 1.818 s, then the weapon's raise on top). Now the stab ends at its <see cref="ReleaseAt"/>: the later of its hit and its data's
/// move-on branch (<see cref="MeleeStep.MoveAt"/>, the clip's visual release: the thrust is over and the body is walking again); from that tick the player is free (move, sprint, fire, aim, switch,
/// grenade, melee, charge, with the weapon up as after a released stomp), the evade one tick later (it starts from a state with no attack). In Musou Mode and Warfront (no stab step) the X,F thrust shares the clip and is released the same way
/// (<see cref="MeleeStep.ThrustRelease"/>) but keeps its finisher chain through its Y window (0.873 to 1.418 s) until another action is pressed or the window closes.
/// </summary>
public static class StabTuning
{
    /// <summary>Seconds from the stab's start at which the player is released: its move-on branch, never before its hit has landed.</summary>
    public static float ReleaseAt(MeleeStep m) => MathF.Max(m.HitTime, m.MoveAt);

    /// <summary>Whether the stab <paramref name="m"/> has played to its release at clip time <paramref name="time"/>.</summary>
    public static bool Released(MeleeStep m, float time, float epsilon) => (m.Stab || m.ThrustRelease) && time + epsilon >= ReleaseAt(m);

    /// <summary>Whether a queued press whose chain window is <paramref name="window"/> can no longer start its attack at <paramref name="time"/>: the window has closed (or never opens).</summary>
    public static bool StaleQueue((float Open, float Close) window, float time, float epsilon) => time + epsilon >= window.Close;

    /// <summary>
    /// Musou Mode and Warfront's thrust (<see cref="MeleeStep.ThrustRelease"/>): whether the released player ends the attack in this tick. It does when no finisher chain is left (its Y window has closed or it has none), or
    /// when the player presses another action (a shot press, aim, switch, grenade, reload, light melee, the evade's Jump; a held fire button does not count; the sim keeps no aim-held state, so aim is read as held, a known limitation); a heavy press or the stick alone keeps the chain open, so the finisher in its window still chains.
    /// </summary>
    public static bool Ends(MeleeStep m, float time, float epsilon, bool otherAction) =>
        !m.ThrustRelease || m.YNext <= 0 || time + epsilon >= m.YComboClose || otherAction;
}

public sealed partial record SimConfig
{
    /// <summary>
    /// sim/stab-release (the owner, 2026-11-20; protocol 88): the stab frees the player at <see cref="StabTuning.ReleaseAt"/> (<see cref="StabTuning"/>). False (restore switch
    /// <c>--old-stab-release</c> on the server and every client; config hash): the stab locks the player to the end of its clip. Hashed only when false.
    /// </summary>
    public bool StabRelease { get; init; } = true;
}
