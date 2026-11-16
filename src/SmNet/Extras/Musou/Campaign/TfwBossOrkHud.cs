using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Extras.Musou.Campaign;

/// <summary>One Boss Ork's bar on the HUD: its id, name ("BOSS NOB") and health as 0 to 1.</summary>
public readonly record struct TfwBossOrkBar(ushort Id, string Name, float Health);

/// <summary>
/// The client's view of the roaming Boss Orks (owner, 2026-11-06; engine-free): which bars the HUD draws and when to call one out. A Boss Ork's bar always travels in tag 13 (the server pins
/// it, <see cref="TfwElite.Pinned"/>), and its kind is the entity feed's (Boss Orks go first there, <c>MusouSendRules.IsLeader</c>). One that first shows in the bars is an arrival; one that
/// leaves them while the battle is on is a kill (a won or left battle clears the bars without a call-out).
/// </summary>
public sealed class TfwBossOrkHud
{
    readonly List<TfwBossOrkBar> _bars = new();
    readonly HashSet<ushort> _known = new();
    readonly HashSet<ushort> _seen = new();

    /// <summary>The bars to draw, in the order the bars list has them (the pool's id order).</summary>
    public IReadOnlyList<TfwBossOrkBar> Bars => _bars;

    /// <summary>
    /// One frame: <paramref name="bars"/> the newest tag 13 list, <paramref name="typeOf"/> an enemy's kind as the client knows it (null: not known yet), <paramref name="fighting"/> whether a
    /// battle is on (a battle not yet won). Returns the arrivals and kills of this frame (each once).
    /// </summary>
    public (int Arrived, int Slain) Update(IReadOnlyList<TfwBar> bars, Func<ushort, MusouEnemyType?> typeOf, bool fighting)
    {
        _bars.Clear();
        _seen.Clear();
        int arrived = 0, slain = 0;
        foreach (var bar in bars)
        {
            if (typeOf(bar.Enemy) is not { } type || !MusouBossOrks.Is(type)) continue;
            _seen.Add(bar.Enemy);
            _bars.Add(new TfwBossOrkBar(bar.Enemy, TfwHudModel.BossOrkName(type), bar.Health / 255f));
            if (_known.Add(bar.Enemy) && fighting) arrived++;
        }
        foreach (ushort id in _known)
            if (!_seen.Contains(id) && fighting) slain++;
        if (!fighting) _known.Clear();
        else _known.IntersectWith(_seen);
        return (arrived, slain);
    }
}
