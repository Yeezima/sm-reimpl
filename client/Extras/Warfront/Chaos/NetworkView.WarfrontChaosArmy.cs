using SmSim.Extras.Warfront.Faction;

namespace SmClient;

/// <summary>Warfront's answer to added content outside its folders that asks for the army the client's config names (C3's latch; the executions marker hides in a Chaos run; the series-37 integration).</summary>
public partial class NetworkView
{
    partial void WarfrontChaosArmyShown(ref bool chaos)
    {
        if (_config?.WarfrontFaction == WarfrontFaction.Chaos) chaos = true;
    }
}
