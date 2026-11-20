using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront;

namespace SmSim;

public sealed partial class World
{
    /// <summary>Warfront (owner 2026-11-20): the Guardsmen's rules at the Orks' favour (<see cref="WarfrontGuardRules"/>).</summary>
    partial void WarfrontGuardRulesHook(ref GuardRules? rules)
    {
        if (!WarfrontGuardRules.Old && Config.Extras.HasFlag(ContentExtras.Warfront)) rules = WarfrontGuardRules.Rules;
    }
}
