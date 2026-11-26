using SmSim.Extras.Musou;
using SmSim.Extras.Warfront.Chaos;
using SmSim.Extras.Warfront.Faction;

namespace SmClient;

/// <summary>
/// Warfront's answers to added content outside its folders that asks for the army the client's config names (C3's latch; the series-37 integration) and, for the executions marker (X4b, owner
/// 2026-11-26), which Chaos unit each type byte is (<see cref="WarfrontChaosRoster.ExecutionKey"/>, the server's own) and the finale duel's gate as the duel state reads it
/// (<see cref="SmClient.Extras.Warfront.Chaos.Finale.ChaosDuelModel.ExecutionGate"/>).
/// </summary>
public partial class NetworkView
{
    partial void WarfrontChaosArmyShown(ref bool chaos)
    {
        if (_config?.WarfrontFaction == WarfrontFaction.Chaos) chaos = true;
    }

    partial void WarfrontChaosUnitShown(MusouEnemyType type, ref string? key) => key = WarfrontChaosRoster.ExecutionKey(type);

    partial void WarfrontDuelGateShown(int me, ref bool duel, ref ushort lord) => (duel, lord) = _wfDuel.ExecutionGate(me, _connectedFor);
}
