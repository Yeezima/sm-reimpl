namespace SmSim;

/// <summary>
/// sim/kick-push (owner 2026-11-14: "only the devastator/havoc kick move (right mouse button) knocks back too far. reduce the knockback by 60% - the kick move only. don't touch the stomp"): the heavy classes'
/// kick (<see cref="MeleeStep.Kick"/>, <c>ability_displace_kick</c>) knocks the player it hits <see cref="PushScale"/> as far as the data does. Only that step: no other attack, no stomp; Musou's enemies take their
/// own knockback (not read from the step's reaction), so Musou's is unchanged.
/// </summary>
public static class KickTuning
{
    /// <summary>The factor on the kick's knockback distance (the hit reaction's backward metres), 1 - 0.6.</summary>
    public const float PushScale = 0.4f;

    /// <summary>The kick's hit reaction with its knockback cut to <see cref="PushScale"/> (kind, up, right and duration unchanged); <paramref name="r"/> itself when the cut is off.</summary>
    public static ReactionEntry WithPush(ReactionEntry r, SimConfig c) => c.KickPushCut ? r with { Backward = r.Backward * PushScale } : r;
}

public sealed partial record SimConfig
{
    /// <summary>sim/kick-push (owner 2026-11-14): the Devastator's and Havoc's kick pushes <see cref="KickTuning.PushScale"/> as far. False (restore switch <c>--old-kick-push</c> on the server and every client; config hash): the data's full knockback. Hashed only when false.</summary>
    public bool KickPushCut { get; init; } = true;
}
