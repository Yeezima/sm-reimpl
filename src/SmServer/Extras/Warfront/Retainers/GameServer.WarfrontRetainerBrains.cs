using SmServer.Extras.Warfront.Retainers;
using SmSim.Extras.Warfront.Squad;

namespace SmServer;

/// <summary>Phase B T4: the server's retainer brains (<see cref="WarfrontRetainerBrains"/>), built with the squad wiring; Warfront only.</summary>
public sealed partial class GameServer
{
    WarfrontRetainerBrains? _wfBrains;
    /// <summary>The retainers' brains (tests and the tick-cost log), null outside Warfront.</summary>
    internal WarfrontRetainerBrains? WarfrontBrainsForTests => _wfBrains;

    /// <summary>The Classic bot brain drives every retainer of <paramref name="wiring"/>, on the stage map's walking graph.</summary>
    void WarfrontAttachRetainerBrains(WarfrontSquadWiring wiring)
    {
        _wfBrains = new WarfrontRetainerBrains(World, wiring.Retainers, () => _wfNav, () => Map?.Name ?? "", WfLog).Attach();
        WfLog("retainers: the Classic bot brain drives them (Phase B T4)");
    }
}
