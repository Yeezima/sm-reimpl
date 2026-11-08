using SmNet;
using SmNet.Extras.Musou;
using SmNet.Extras.Musou.Campaign;
using SmSim.Extras.Musou;

namespace SmServer;

/// <summary>
/// The Scrap Meks' contraptions on the wire (tag 17, <see cref="MekWire"/>; expansion S4, Musou Mode only): every contraption of the battle under way, to every client, in one section. Sent on a change at
/// most 10 times a second (a build's progress changes every tick), and once a second while any stands, so a lost datagram costs at most a second; one empty section when the last goes, so the clients clear.
/// </summary>
public sealed partial class GameServer
{
    byte[]? _mekSent;
    uint _mekTick;
    readonly List<MusouContraption> _mekViews = new();

    void SendTfwMeks(uint tick, int tickRate)
    {
        if (World.TfwMeks is { } meks) meks.CopyTo(_mekViews); else _mekViews.Clear();
        var bytes = MekWire.Write(_mekViews);
        bool empty = _mekViews.Count == 0;
        if (empty && (_mekSent is null || _mekSent.Length == 1)) return;
        uint every = (uint)Math.Max(1, tickRate / 10), second = (uint)Math.Max(1, tickRate);
        bool changed = _mekSent is null || !_mekSent.AsSpan().SequenceEqual(bytes);
        bool due = changed ? tick - _mekTick >= every || tick < _mekTick : tick - _mekTick >= second || tick < _mekTick;
        if (!due) return;
        SendExtras(new ExtrasUpdate(tick, [(TfwWire.MekTag, bytes)]));
        _mekSent = bytes;
        _mekTick = tick;
    }
}
