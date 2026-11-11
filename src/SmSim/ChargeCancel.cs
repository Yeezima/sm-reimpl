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
    /// charge-ally-bump (protocol 66; the owner 2026-11-11: "no animation should play when hitting an ally"), now charge-ally-slide (protocol 67; "when a charge hits an ally it should
    /// slide past"): a charge passes an ally it cannot harm and plays on (<see cref="ChargeCancel.AllyPasses"/>). False (restore switch <c>--old-ally-bump</c> on the server and every client;
    /// config hash): the protocol 65 stop (the charger plays the collision stop, the wall-hit recoil). Hashed only when false.
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
    /// charge-ally-slide (protocol 67; the owner 2026-11-11: "when a charge hits an ally it should slide past"): whether a charge passes the players of its own side without a contact: no
    /// hit, no collision stop, no turn, no ally bump, the charge plays on (<see cref="ChargeLock.Find"/> leaves allies out). Every mode (Classic PvP and Musou Mode alike, whose co-op
    /// kept the collision stop at protocol 66) while <see cref="SimConfig.ChargeAllyBump"/> is on and the ally cannot be harmed (<paramref name="friendlyFire"/> off: with it on the ally
    /// takes the hit and the charger the stop, as before). The player collision (sim/player-collision) still keeps the two bodies from overlapping: the charger's move slides along the
    /// ally's capsule (the capsule's slide, no shove: an ally is not pushed more than any walking player is).
    /// </summary>
    public static bool AllyPasses(SimConfig c, bool friendlyFire) => c.ChargeAllyBump && !friendlyFire;
}
