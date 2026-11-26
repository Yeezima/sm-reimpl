using SmSim.Extras.Musou;
using SmSim.Extras.Warfront.Chaos;
using SmSim.Extras.Warfront.Faction;

namespace SmSim;

/// <summary>
/// Warfront's answers to added content outside its folders that asks for the stage's army: executions X4 phase 2 (the series-37 integration) asks whether the stage is Chaos, and X4b (2026-11-26)
/// which Chaos unit each type byte is (<see cref="WarfrontChaosRoster.ExecutionKey"/>), so a Chaos stage's enemies take their own executions.
/// </summary>
public sealed partial class World
{
    partial void WarfrontChaosArmy(ref bool chaos)
    {
        if (Config.WarfrontFaction == WarfrontFaction.Chaos) chaos = true;
    }

    partial void WarfrontChaosUnit(MusouEnemyType type, ref string? key) => key = WarfrontChaosRoster.ExecutionKey(type);
}
