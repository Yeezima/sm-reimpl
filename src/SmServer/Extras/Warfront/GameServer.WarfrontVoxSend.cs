using SmNet;
using SmNet.Extras.Warfront;
using SmSim.Extras.Musou.Campaign;

namespace SmServer;

/// <summary>
/// Guardsmen G2: the vox section (tag <see cref="WarfrontWire.VoxTag"/>, <see cref="WarfrontVoxWire"/>). The battle's newest line (<see cref="TfwBattle.WarfrontVoxNewest"/>) goes to every identified client the tick it
/// is made, then once a second for <see cref="WarfrontVoxWire.RepeatSeconds"/> s (the section rides unreliable datagrams). Nothing is sent outside a Warfront battle, so Classic and Musou servers never send it.
/// </summary>
public sealed partial class GameServer
{
    TfwBattle? _wfVoxBattle;
    ushort _wfVoxSequence;
    uint _wfVoxMade;

    void WarfrontVoxSend()
    {
        if (_wf is not { } s || World.Tfw is not { Battle: { } battle } run || run.Warfront is not { InBattle: true })
        {
            _wfVoxBattle = null;
            return;
        }
        if (!ReferenceEquals(battle, _wfVoxBattle)) { _wfVoxBattle = battle; _wfVoxSequence = 0; }
        if (battle.WarfrontVoxNewest is not { } line) return;
        uint tick = World.Tick;
        if (line.Sequence != _wfVoxSequence) { _wfVoxSequence = line.Sequence; _wfVoxMade = tick; }
        uint since = tick - _wfVoxMade, rate = (uint)Math.Max(1, WarfrontRate);
        if (since > WarfrontVoxWire.RepeatSeconds * rate || since % rate != 0) return;
        byte[] bytes = WarfrontVoxWire.Encode(line);
        foreach (int id in s.KeyOf.Keys.ToList())
            SendExtras(new ExtrasUpdate(tick, [(WarfrontWire.VoxTag, bytes)]), id);
    }
}
