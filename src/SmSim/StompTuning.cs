namespace SmSim;

/// <summary>
/// sim/stomp-release (the owner, 2026-11-14: "allow a devastator/havoc player in both game modes to be able to act after a stomp move (F) 1.2 seconds into the animation. shortly after the stomp
/// hits and triggers, the bolt pistol should be able to fire as an example. currently the lock out restriction is a bit too long."): the Devastator's and the Havoc's stomp (the in-place stun strike,
/// <see cref="MeleeStep.InPlace"/>) ends <see cref="ReleaseAt"/> seconds into its clip, in Classic PvP and Musou Mode: from that tick the player is free (fire, aim, move, switch, reload, grenade, melee,
/// charge), as after the clip's end. Before it, sim/stomp-uninterruptible still holds (only the hop cancels the stomp). The standard stomp's blast is at 0.767 s, so the release follows the hit.
/// </summary>
public static class StompTuning
{
    /// <summary>Seconds from the stomp's start (the clip's own time, so Fast Stomp's quicker clip, 1.111 s, ends before it) at which the player is released (the owner's number).</summary>
    public const float ReleaseAt = 1.2f;

    /// <summary>Whether the in-place strike <paramref name="m"/> has played to its release at clip time <paramref name="time"/> (never before its hit has landed).</summary>
    public static bool Released(MeleeStep m, float time, float epsilon) =>
        m.InPlace && time + epsilon >= MathF.Max(ReleaseAt, m.HitTime);
}

public sealed partial record SimConfig
{
    /// <summary>
    /// sim/stomp-release (the owner, 2026-11-14): the stomp frees the player <see cref="StompTuning.ReleaseAt"/> seconds in (<see cref="StompTuning"/>). False (restore switch
    /// <c>--old-stomp-release</c> on the server and every client; config hash): the stomp locks the player to the end of its clip. Hashed only when false.
    /// </summary>
    public bool StompRelease { get; init; } = true;
}
