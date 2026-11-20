namespace SmSim;

/// <summary>
/// sim/stab-release (the owner, 2026-11-20): "currently when using the T 'stab' move with power sword ... the player character cannot initiate another action for a long time after the stab animation
/// finishes playing. allow the player to act again as soon as the stab animation releases and the 'hit or miss' moment has occurred." The stab (<see cref="MeleeStep.Stab"/>: the power sword's X,F thrust
/// clip, 1.818 s with its hit at 0.636 s) locked the player to the end of its clip (shots from its BRANCH_ToRanged and the hop from its branch, the stick from its BRANCH_Run at 0.909 s, everything else, a switch, a grenade,
/// a second melee press or stab, not until the clip's end at 1.818 s, then the weapon's raise on top). Now the stab ends at its <see cref="ReleaseAt"/>: the later of its hit and its data's
/// move-on branch (<see cref="MeleeStep.MoveAt"/>, the clip's visual release: the thrust is over and the body is walking again); from that tick the player is free (move, sprint, fire, aim, switch,
/// grenade, melee, charge, with the weapon up as after a released stomp), the evade one tick later (it starts from a state with no attack). Classic PvP's stab only (Musou Mode and Warfront have no
/// stab step; their X,F thrust shares the clip but chains into a finisher through its Y window and is unchanged).
/// </summary>
public static class StabTuning
{
    /// <summary>Seconds from the stab's start at which the player is released: its move-on branch, never before its hit has landed.</summary>
    public static float ReleaseAt(MeleeStep m) => MathF.Max(m.HitTime, m.MoveAt);

    /// <summary>Whether the stab <paramref name="m"/> has played to its release at clip time <paramref name="time"/>.</summary>
    public static bool Released(MeleeStep m, float time, float epsilon) => m.Stab && time + epsilon >= ReleaseAt(m);
}

public sealed partial record SimConfig
{
    /// <summary>
    /// sim/stab-release (the owner, 2026-11-20; protocol 88): the stab frees the player at <see cref="StabTuning.ReleaseAt"/> (<see cref="StabTuning"/>). False (restore switch
    /// <c>--old-stab-release</c> on the server and every client; config hash): the stab locks the player to the end of its clip. Hashed only when false.
    /// </summary>
    public bool StabRelease { get; init; } = true;
}
