using SmSim.Extras.Warfront;

namespace SmSim.Extras.Musou.Campaign;

/// <summary>
/// Reinforcement priority and the arrival note (Guardsmen G1e). The budget, cadence and routes are unchanged (every 60 s, the men of <c>ReinforceMenNow</c> and the men home, in whole squads); only the split moves:
/// each point's share is weighted by 1 + 2 x min(1, pressure / 40), a Breaking or Withdrawn point counting as fully threatened. Owner 2026-11-25 ("Reinforce anyway"): a point whose line fell back still gets its
/// share, so no need is zeroed, and the field's reserves top up while its survivors are away (withheld squads are not field squads). The cap of a share stays what the point lacks.
/// </summary>
public sealed partial class TfwBattle
{
    /// <summary>H4: point <paramref name="k"/>'s weight in the reinforcement split (<paramref name="weight"/> is its need on entry).</summary>
    partial void WarfrontReinforceNeed(int k, int need, ref float weight)
    {
        if (!WfLine || need <= 0) return;
        float threat = _wfLine[k] is WarfrontLineState.Breaking or WarfrontLineState.Withdrawn ? 1f : Math.Min(1f, _wfPressure[k] / WarfrontLineRules.ReinforceThreatFull);
        weight = need * (1f + WarfrontLineRules.ReinforceThreatWeight * threat);
    }

    /// <summary>H5: a reinforcement merged into point <paramref name="k"/>'s garrison: the Reinforcements line when the point is threatened, Breaking, or withdrawn in the last 20 s (no line for a routine arrival).</summary>
    partial void WarfrontReinforced(TfwArmy a, int k)
    {
        if (!WfLine || a.Order != TfwArmyOrder.March || k is < 0 or >= Positions) return;
        bool threatened = _wfPressure[k] >= WarfrontLineRules.ReinforceVoxPressure || _wfLine[k] != WarfrontLineState.Holding
            || _now - _wfWithdrawnAt[k] <= Ticks(WarfrontLineRules.ReinforceVoxWithdrawnSeconds);
        if (threatened) WfVox(WarfrontVoxKind.Reinforcements, k);
    }
}
