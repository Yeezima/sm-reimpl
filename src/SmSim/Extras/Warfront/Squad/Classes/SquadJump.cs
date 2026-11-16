using System.Numerics;

namespace SmSim.Extras.Warfront.Squad;

/// <summary>
/// A jump-pack Marine's pack as the squad rules use it, read from the class's <see cref="JumpPackSpec"/> (so a class with a pack, the Assault/Raptor, has it; every other class has none): the pack's
/// power and recharge, what a full ("max") jump costs, its height, the range and airtime the physics give, and the ground pound's blast. These are the existing ability limits the squad keeps.
/// </summary>
/// <param name="MaxPower">The pack's power (fuel) at most.</param>
/// <param name="Recharge">Power regained a second.</param>
/// <param name="FullCost">The power a full jump costs (the data's power-used at the longest hold).</param>
/// <param name="Height">The full jump's height (the data's height at the longest hold).</param>
/// <param name="Range">The farthest a full jump flies flat (forward speed x airtime) [estimate].</param>
/// <param name="Duration">The airtime of a full jump [estimate].</param>
/// <param name="ForwardSpeed">The horizontal speed in flight.</param>
/// <param name="PoundRadius">The ground pound's blast radius (0: the class has no pound).</param>
/// <param name="PoundDamage">The ground pound's damage to an enemy in the blast.</param>
/// <param name="PoundRecovery">Seconds a Marine is still after its pound's impact.</param>
/// <param name="PoundEnabled">Whether the pack pounds.</param>
public sealed record SquadJump(float MaxPower, float Recharge, float FullCost, float Height, float Range, float Duration, float ForwardSpeed, float PoundRadius, float PoundDamage, float PoundRecovery, bool PoundEnabled);

/// <summary>One landing of a jump-pound plan: Marine <see cref="Index"/> (slot <see cref="Slot"/>) lands at <see cref="Landing"/> and its blast goes off at <see cref="ImpactTick"/>; a Marine that walks instead has <see cref="Leaps"/> false and tick 0.</summary>
public readonly record struct SquadPoundEntry(int Index, int Slot, Vector3 Landing, uint ImpactTick, bool Leaps);

/// <summary>The plan the jump-pound order made: the marked point's floor and each Marine's entry, in slot order.</summary>
public sealed record SquadPoundPlan(Vector3 Mark, IReadOnlyList<SquadPoundEntry> Entries);

/// <summary>A ground pound's blast that went off this tick (for the wire and the tests).</summary>
public readonly record struct SquadPoundImpact(ulong Key, int Slot, int Index, Vector3 Position, uint Tick, int Hits);
