using SmSim.Extras.Warfront.Faction;

namespace SmSim;

/// <summary>Warfront's answer to added content outside its folders that asks for the stage's army (executions X4 phase 2: no executions on a Chaos stage; the series-37 integration).</summary>
public sealed partial class World
{
    partial void WarfrontChaosArmy(ref bool chaos)
    {
        if (Config.WarfrontFaction == WarfrontFaction.Chaos) chaos = true;
    }
}
