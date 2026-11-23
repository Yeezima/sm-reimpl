using System.Numerics;

namespace SmSim;

public sealed partial record SimConfig
{
    /// <summary>
    /// sim/ledge-charge-air (protocol 90; the owner 2026-11-23, brief <c>2026-11-23-series-36-ledge-charge</c>): in Classic PvP a bull rush held in the air after running off a ledge moves
    /// <see cref="LedgeChargeAir.Scale"/> of its held speed per tick (<see cref="LedgeChargeAir"/>), so its travel from the ledge to the first landing is half the protocol 89 travel; its momentum (the held
    /// velocity the hop and the landing read) is unchanged. False (restore switch <c>--old-ledge-charge-air</c> on the server and every client; config hash): the protocol 89 travel. Hashed only when false.
    /// </summary>
    public bool LedgeChargeHalfAir { get; init; } = true;
}

/// <summary>
/// sim/ledge-charge-air (protocol 90; the owner 2026-11-23: "In Classic PvP only, when an already active grounded charge goes off a ledge, reduce horizontal travel from ledge departure to first landing to 50% of
/// current behavior"; ours, a deliberate change from the original). The bull rush held in the air (<see cref="Rush.Held"/>) moves each tick by its held horizontal velocity (the speed of the tick it left the ground on)
/// times <see cref="TravelScale"/>: <see cref="Scale"/> in Classic PvP. Gravity and the vertical speed are untouched, so the time in the air (and the drop) is the same and the air travel is halved.
/// <para>
/// The momentum is not cut (the owner 2026-11-23: "The hop itself should keep the same distance and momentum - only the charge should be slowed down. The slower charge should transition into the normal hop speed, or
/// light melee attack."): the held velocity in the state stays the full one (<see cref="KeepMomentum"/>), so the hop out of the charge (<see cref="LedgeHop"/>), the light-melee cancel
/// (<see cref="SimConfig.LedgeChargeCancel"/>), the landing's speed cap (<see cref="Rush.Landed"/>) and A / D air steering read exactly what they read at protocol 89. A Space tap can therefore win back distance (the owner's choice).
/// </para>
/// Only Classic PvP (Annihilation, Seize Ground: no <see cref="ContentExtras.Musou"/> bit, the same test as <see cref="CancelGate.Active"/>); Musou Mode and Warfront (Musou | Warfront) keep the full travel. The dash attack
/// (begun inside a jump-pack hop, not a grounded charge) is unchanged. Damage, contacts, hit rules and collision are unchanged.
/// </summary>
public static class LedgeChargeAir
{
    /// <summary>The share of the held speed a Classic PvP ledge charge travels per tick in the air (owner 2026-11-23: 50 %; kind ours).</summary>
    public const float Scale = 0.5f;

    /// <summary>Whether the rule acts for this config: switched on and Classic PvP (no Musou bit).</summary>
    public static bool Active(SimConfig c) => c.LedgeChargeHalfAir && (c.Extras & ContentExtras.Musou) == 0;

    /// <summary>The factor on a held charge tick's travel (<see cref="Rush.Move"/>): <see cref="Scale"/> for the bull rush in Classic PvP, else 1.</summary>
    public static float TravelScale(PlayerState s, SimConfig c) => Active(c) && s.Rush == RushPhase.Rush ? Scale : 1f;

    /// <summary>
    /// After a held tick's move: the horizontal velocity the capsule controller reports (the scaled travel's) restored to the full held one, so momentum is unchanged; a wall slide keeps its
    /// direction at the full speed. Any tick the factor did not scale is returned as it is.
    /// </summary>
    public static PlayerState KeepMomentum(PlayerState before, PlayerState next, SimConfig c)
    {
        float k = TravelScale(before, c);
        if (k == 1f) return next;
        var v = next.Velocity;
        return next with { Velocity = new Vector3(v.X / k, v.Y / k, v.Z) };
    }
}
