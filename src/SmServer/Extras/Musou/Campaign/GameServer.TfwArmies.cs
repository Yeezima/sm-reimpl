using SmNet;
using SmNet.Extras.Musou.Campaign;

namespace SmServer;

/// <summary>
/// The battle flow's armies on the wire (tag 18, <see cref="TfwArmyWire"/>; expansion S6; Musou Mode only): every army of the battle under way, to every client in one section, twice a second (the far layer's own
/// rate; a client draws the aggregate between two sections). Nothing is sent while there is none, and one empty section when the last one goes, so the clients clear. At most 761 bytes: about 1.5 KB/s a client.
/// </summary>
public sealed partial class GameServer
{
    byte[]? _armiesSent;
    uint _armiesTick;

    void SendTfwArmies(uint tick, int tickRate)
    {
        var armies = World.Tfw?.Battle?.ArmyList;
        var bytes = TfwArmyWire.Write(armies is null ? [] : TfwArmyWire.Views(armies));
        bool empty = bytes.Length == 1;
        if (empty && (_armiesSent is null || _armiesSent.Length == 1)) return;
        uint every = (uint)Math.Max(1, tickRate / 2);
        if (!empty && _armiesSent is not null && tick - _armiesTick < every && tick >= _armiesTick) return;
        SendExtras(new ExtrasUpdate(tick, [(TfwWire.ArmiesTag, bytes)]));
        _armiesSent = bytes;
        _armiesTick = tick;
    }
}
