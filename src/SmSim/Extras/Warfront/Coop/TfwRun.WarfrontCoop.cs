using SmSim.Extras.Warfront.Coop;

namespace SmSim.Extras.Musou.Campaign;

/// <summary>
/// Warfront's co-op part of the run (slice W12): the answers the run's hooks ask the co-op rules. Plain members, not hook bodies: the hooks themselves (<c>WarfrontJoinLate</c>, W3b's input lock and damage
/// hooks) are W3b's declarations; their one-line bodies call these (listed in the W12 report). Every member answers "no change" unless <see cref="WarfrontCoopRules"/> is set (a Warfront run only).
/// </summary>
public sealed partial class TfwRun
{
    /// <summary>The run's co-op rules (null: not a Warfront run; set by the server beside <c>Warfront</c>).</summary>
    public WarfrontCoop? WarfrontCoopRules { get; set; }

    /// <summary>The body of <c>WarfrontJoinLate(player, ref allow)</c>: false for a spectating joiner (it stays aboard), true otherwise and in every run without the rules.</summary>
    public bool WarfrontCoopAllowsLate(int player) => WarfrontCoopRules?.AllowsLateJoin(player) ?? true;

    /// <summary>The body of the input-lock and no-damage hooks: a spectating joiner is locked and unharmed aboard (ruling 1).</summary>
    public bool WarfrontCoopHeld(int player) => WarfrontCoopRules?.IsHeld(player) ?? false;
}
