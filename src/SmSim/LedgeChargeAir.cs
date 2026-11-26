namespace SmSim;

public sealed partial record SimConfig
{
    /// <summary>
    /// sim/ledge-charge-air (protocol 90; the owner 2026-11-23, brief <c>2026-11-23-series-36-ledge-charge</c>, answer 2): in Classic PvP a bull rush held in the air after running off a ledge falls under
    /// <see cref="LedgeChargeAir.GravityScale"/> x gravity at its unchanged horizontal speed, so its travel from the ledge to the first landing is about 0.71 of the protocol 89 travel (<see cref="LedgeChargeAir"/>; protocol 92 halved
    /// protocol 90's 4 x gravity to 2 x, owner 2026-11-24).
    /// False (restore switch <c>--old-ledge-charge-air</c> on the server and every client; config hash): the protocol 89 fall. Hashed only when false.
    /// </summary>
    public bool LedgeChargeHalfAir { get; init; } = true;
}

/// <summary>
/// sim/ledge-charge-air (protocol 90, gravity 2 x since protocol 92; ours, a deliberate change from the original). The brief (owner 2026-11-23): "In Classic PvP only, when an already active grounded charge goes off a ledge, reduce horizontal travel
/// from ledge departure to first landing to 50% of current behavior". The owner's answer 2 (same day): "cancel the thing about keeping the speed of the ledge charge the same. keep the speed the same, but the player drops
/// out of the air faster. make it so that the player goes half the distance he did before following those requirements. / the hop is normal speed, unchanged."
/// After the playtest (owner 2026-11-24, decision <c>2026-11-24-series-37-playtest-fixes</c> item 1): "Adjust the charge gravity change to be reduced by 50% of what it is in the playtest. The player drops too fast at
/// the moment." On the card he chose "2x (halve the gravity)": protocol 90's 4 x became 2 x at protocol 92.
/// <para>
/// The bull rush held in the air (<see cref="Rush.Held"/>) keeps moving at its held horizontal speed every tick, as at protocol 89, but falls under <see cref="Gravity"/> (2) x gravity: from a ledge with no vertical
/// speed the time to fall any height h is √(2h / 2g) = 1/√2 (about 0.71) of √(2h / g), so the horizontal travel to the first landing is about 0.71 of protocol 89's (to the 30 Hz tick; protocol 90-91's 4 x gave half). Only the held ticks: the departure tick (the clip's motion, normal
/// gravity) and everything after the charge ends in the air (a hop, a light melee, a reaction) fall as before. The charge's landing rule keeps its drop height (<see cref="Rush.EndsOnLanding"/> under the same gravity).
/// The landing comes at about √2 (1.41) x the protocol 89 downward speed.
/// </para>
/// Only Classic PvP (Annihilation, Seize Ground: no <see cref="ContentExtras.Musou"/> bit, the same test as <see cref="CancelGate.Active"/>); Musou Mode and Warfront (Musou | Warfront) unchanged. The dash attack
/// (begun inside a jump-pack hop, not a grounded charge) is unchanged. Damage, contacts, hit rules and collision are unchanged.
/// </summary>
public static class LedgeChargeAir
{
    /// <summary>The gravity multiplier on a Classic PvP bull rush held in the air (2: about 0.71 of the protocol 89 air time and travel from a ledge; owner 2026-11-24 "2x (halve the gravity)", after protocol 90's 4 x
    /// for answer 2's "half the distance" dropped too fast; kind ours).</summary>
    public const float Gravity = 2f;

    /// <summary>Whether the rule acts for this config: switched on and Classic PvP (no Musou bit).</summary>
    public static bool Active(SimConfig c) => c.LedgeChargeHalfAir && (c.Extras & ContentExtras.Musou) == 0;

    /// <summary>The gravity factor for a charge tick held in the air (<see cref="Rush.Move"/>, <see cref="Rush.EndsOnLanding"/>): <see cref="Gravity"/> for the bull rush in Classic PvP, else 1.</summary>
    public static float GravityScale(PlayerState s, SimConfig c) => Active(c) && s.Rush == RushPhase.Rush ? Gravity : 1f;
}
