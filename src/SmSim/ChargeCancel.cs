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

    /// <summary>
    /// charge-responsive (protocol 69; the owner 2026-11-12: "you should immediately be able to charge after a melee attack, or vice versa"): a charge (Shift + W + F) starts in the middle of a melee
    /// attack or combo once that attack's hit has landed (<see cref="ChargeCancel.CanChargeFromMelee"/>), ending the attack and its combo and clearing the melee wait; before this the charge waited for
    /// the attack's whole clip (<c>Rush.CanStart</c> refuses a body with a melee attack under way; the press waited in the input buffer, 0.3 s). False (restore switch
    /// <c>--old-charge-after-melee</c> on the server and every client; config hash): that wait. Hashed only when false.
    /// </summary>
    public bool ChargeAfterMelee { get; init; } = true;

    /// <summary>
    /// charge-responsive (protocol 69; the owner 2026-11-12: "the charge should be cancelled instantaneously as soon as the S key or back on left joystick is triggered"): the S cancel
    /// (<see cref="ChargeBackCancel"/>) works from the charge's second tick on (the first tick after its start), not from the rush's stick branch (0.467 s). False (restore switch
    /// <c>--old-charge-cancel-delay</c> on the server and every client; config hash): the branch's wait. Hashed only when false.
    /// </summary>
    public bool ChargeInstantCancel { get; init; } = true;

    /// <summary>
    /// charge-melee-chain (protocol 70; the owner 2026-11-12: "if I charge, then right click for melee light attack mid charge, it should happen immediately ... I should be able to charge>melee>charge>melee and chain
    /// them together endlessly without any delays or cooldowns"): a light melee press ends the bull rush at once on any tick, with no wait after the swing (<see cref="ChargeCancel.MeleeEndsCharge"/>), and any
    /// melee attack that starts lifts the 1 s spacing between charges (<c>Rush.Begin</c>'s RushLock). False (restore switch <c>--old-charge-melee-chain</c> on the server and every client; config hash): the
    /// protocol 69 rules (only the jump-pack and roll classes cancel, with MeleeCancel's wait; the spacing is only the clock). Hashed only when false.
    /// </summary>
    public bool ChargeMeleeChain { get; init; } = true;

    /// <summary>
    /// musou/melee-spam restore switch (<c>--musou-melee-cooldown</c> on the server and every client; ours): true brings Musou Mode's melee waits back (the combo window, the cancel wait) instead of the owner's
    /// no-cooldown spam (2026-11-12). Lives here so a build without added content compiles; read only by the Musou hook (<see cref="SimConfig.MeleeSpam"/>) and hashed in Musou's block only when true.
    /// </summary>
    public bool MusouMeleeCooldown { get; init; }
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
    /// charge-responsive: the stick's length from which the instant cancel counts as back (0.5 of full; ours; a keyboard's S gives 1), so a pad stick's drift or a loose return through the
    /// middle does not cancel; the sectors are <see cref="BackHeld"/>'s. The cancel from the branch (<see cref="RushMove.MoveAt"/>) keeps the dead zone.
    /// </summary>
    public const float InstantBackStick = 0.5f;

    /// <summary>charge-responsive: whether the stick is held back firmly enough for the instant cancel (<see cref="InstantBackStick"/>, then <see cref="BackHeld"/>'s sectors).</summary>
    public static bool BackInstant(PlayerInput sanitized) =>
        sanitized.MoveX * sanitized.MoveX + sanitized.MoveY * sanitized.MoveY >= InstantBackStick * InstantBackStick && BackHeld(sanitized);

    /// <summary>
    /// charge-responsive: whether a Shift + W + F press this tick may end the melee attack under way and charge (<see cref="SimConfig.ChargeAfterMelee"/>): the attack's hit has landed (the
    /// attack time before this tick is at least its <see cref="MeleeStep.HitTime"/>, so a swing is never thrown away unhit; a heavy strike counts too, the in-place stomp has no charge
    /// class). The caller tests the rest of the start gate (<see cref="Rush.CanStart"/>) on the state with the attack ended.
    /// </summary>
    public static bool CanChargeFromMelee(SimConfig c, PlayerState s) =>
        c.ChargeAfterMelee && s.Alive && s.Rush == RushPhase.None && s.MeleeStep > 0 && Combat.MeleeStepOf(c, s) is { InPlace: false } m && s.MeleeTime + 1e-5f >= m.HitTime;

    /// <summary>charge-melee-chain: whether a light melee press ends the bull rush under way at once (<see cref="SimConfig.ChargeMeleeChain"/>; any class, any tick of the rush, its last included).</summary>
    public static bool MeleeEndsCharge(SimConfig c, PlayerState s) => c.ChargeMeleeChain && s.Alive && s.Rush == RushPhase.Rush;

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
