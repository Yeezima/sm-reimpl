using System.Numerics;

namespace SmSim;

public sealed partial record SimConfig
{
    /// <summary>
    /// charge-cancel (protocol 66; the owner 2026-11-11: "throwing a grenade no longer cancels a bull rush/charge animation. what should cancel a charge animation is tapping or
    /// holding the S key (backpedal)"): S cancels the bull rush and no Grenade press ends a charge phase (<see cref="ChargeCancel"/>). False (restore switch <c>--old-charge-cancel</c>
    /// on the server and every client; config hash): the protocol 65 rules (a Grenade press ends every charge phase from its stick branch, also with no grenade left; S does not end the
    /// rush). Hashed only when false.
    /// </summary>
    public bool ChargeBackCancel { get; init; } = true;

    /// <summary>
    /// charge-ally-bump (protocol 66; the owner 2026-11-11: "if you bull rush or charge and hit a friendly, your player character will play the 'recoil' or stun animation. no
    /// animation should play when hitting an ally"): in Classic PvP a charge that meets an ally it cannot harm ends where it met him, with no collision stop and nothing on the ally
    /// (<see cref="ChargeCancel.Bumped"/>). False (restore switch <c>--old-ally-bump</c> on the server and every client; config hash): the protocol 65 stop (the charger plays the
    /// collision stop, the wall-hit recoil). Hashed only when false.
    /// </summary>
    public bool ChargeAllyBump { get; init; } = true;
}

/// <summary>
/// charge-cancel and charge-ally-bump (protocol 66; the owner's requests of 2026-11-11; rules in spec/behavior.md "Charge cancel and the ally bump"). Shared by the server and the
/// own client's prediction (pure).
/// <para>
/// <b>Why a grenade cancelled the charge</b>: the bull rush's data branch <c>BRANCH_Attack2Idle</c> (<see cref="RushMove.MoveAt"/>, × the lock scale) lets "the stick, Fire, Aim or a
/// Grenade press" end it. Protocol 48 took Fire and Aim off it and protocol 53 the stick, which left the Grenade press (input-buffered for 0.3 s, so a press up to 0.3 s before the branch
/// counted too) as the one way to cut the rush short, and the throw's own test for a grenade in hand comes after it: a press with none left ended the rush and threw nothing.
/// </para>
/// <para>
/// <b>Now</b> (<see cref="SimConfig.ChargeBackCancel"/>): a Grenade press ends no charge phase (the bull rush, its collision stop, the dash attack's states); the press is kept by the
/// input buffer as any blocked press, so a throw (when a grenade is left) starts on the tick the charge ends if that is within the buffer's window. The stick held back (S, S + A,
/// S + D: <see cref="BackHeld"/>) ends the rush from the same branch, tapped or held; the other phases keep the stick rule they had (any direction from their stick branch).
/// The cancel is the rush's other early ends: the charge is over at once and the tick moves the body as normal movement (no stop clip, no reaction, hyper armour and the spacing as
/// before).
/// </para>
/// </summary>
public static class ChargeCancel
{
    /// <summary>
    /// Whether the stick is held back for the cancel: beyond the dead zone and in one of the three back sectors of eight around the view (S, S + A, S + D on a keyboard; a pad's
    /// stick from 67.5 degrees either side of straight back). A or D alone, W, or no stick: false.
    /// </summary>
    public static bool BackHeld(PlayerInput sanitized) =>
        (sanitized.MoveX * sanitized.MoveX + sanitized.MoveY * sanitized.MoveY >= Evade.DeadZone * Evade.DeadZone)
        && Evade.DirectionOf(sanitized.MoveX, sanitized.MoveY, diagonals: true) is EvadeDirection.Back or EvadeDirection.BackLeft or EvadeDirection.BackRight;

    /// <summary>
    /// The input that ends a charge phase from its stick branch (<see cref="RushMove.MoveAt"/>; <c>Rush.Begin</c>'s other input) this tick, for state <paramref name="s"/> (its phase
    /// after the last tick). With <see cref="SimConfig.ChargeBackCancel"/>: the rush the stick held back, every other phase any stick direction, never a Grenade press. Without it (the
    /// protocol 65 rules): any stick direction outside the rush, or a Grenade press.
    /// </summary>
    public static bool EndsAtBranch(PlayerState s, PlayerInput sanitized, bool grenadePress, SimConfig c)
    {
        bool stick = sanitized.MoveX != 0f || sanitized.MoveY != 0f;
        if (!c.ChargeBackCancel) return (stick && s.Rush != RushPhase.Rush) || grenadePress;
        return s.Rush == RushPhase.Rush ? BackHeld(sanitized) : stick;
    }

    /// <summary>
    /// Whether a charge's contact with a player of its own side ends it with no stop (<see cref="Bumped"/>; <see cref="SimConfig.ChargeAllyBump"/>): Classic PvP (not Musou Mode,
    /// whose co-op keeps the collision stop on an ally) and an ally the charge cannot harm (<paramref name="friendlyFire"/> off: with it on the ally takes the hit and the charger the
    /// stop, as before). The caller checks the sides (<see cref="World.Hostile"/> false).
    /// </summary>
    public static bool AllyBumps(SimConfig c, bool friendlyFire) => c.ChargeAllyBump && !friendlyFire && !c.Extras.HasFlag(ContentExtras.Musou);

    /// <summary>
    /// The charger's state after an ally bump (ours): the charge over where it met the ally (no collision stop, no turn to him), the horizontal velocity gone (the ally's body blocks
    /// the way, as the stop zeroed it before), normal movement from the next tick. The spacing lock stays. A charge that is no longer in its charging phase (a wall stopped it this
    /// tick, a landing or a reaction ended it) is left as it is.
    /// </summary>
    public static PlayerState Bumped(PlayerState s) =>
        Rush.Charging(s.Rush) ? Rush.Cleared(s) with { Velocity = new Vector3(0f, 0f, s.Velocity.Z) } : s;
}
