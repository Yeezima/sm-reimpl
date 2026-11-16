using SmSim.Extras.Warfront;

namespace SmSim.Extras.Musou.Campaign;

/// <summary>Warfront's progression in the run (slice W3b): the captures pay XP, a late human may be held back (W12's seam). Each hook returns at once without the Warfront object.</summary>
public sealed partial class TfwRun
{
    partial void WarfrontBattleChange(TfwBattleChange change)
    {
        if (Warfront is not { } rules || Battle is not { } battle) return;
        foreach (int index in rules.Progression.OnBattleChange(battle, change)) rules.ObjectiveSecured?.Invoke(index);
    }

    partial void WarfrontJoinLate(int player, ref bool allow)
    {
        if (Warfront is { LateJoinAllowed: { } ask } && !ask(player)) allow = false;
    }
}
