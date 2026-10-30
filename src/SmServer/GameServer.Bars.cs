using SmNet;
using SmSim;

namespace SmServer;

/// <summary>
/// Tactical Readout and Signum Link on the wire (protocol 44; owner's decision of 2026-10-30): every <see cref="BarsInterval"/> ticks the server sends each
/// player whose perks show enemy bars (<see cref="Loadouts.ShowsEnemyBars"/>, living) an <see cref="EnemyBars"/> message with the health and armour fractions of
/// the living enemies (<see cref="World.Hostile"/>). A player without either perk is sent nothing, so nothing shows over enemies for it (no name, no bar).
/// </summary>
public sealed partial class GameServer
{
    /// <summary>Ticks between bar messages (3: ten per second at 30 Hz; ours).</summary>
    public const int BarsInterval = 3;

    /// <summary>The bars message a player with the readout sees now (null when its perks show none, or it is dead).</summary>
    public EnemyBars? BarsFor(int playerId)
    {
        if (!World.Players.TryGetValue(playerId, out var me) || !me.Alive || !Loadouts.ShowsEnemyBars(World.Config, me)) return null;
        var entries = new List<EnemyBarView>();
        foreach (var (id, p) in World.Players.OrderBy(kv => kv.Key))
        {
            if (id == playerId || !p.Alive || !World.Hostile(p.Team, me.Team) || id is < 1 or > ushort.MaxValue) continue;
            var (health, shield) = HitBars.Maxima(World.Config, p.Class);   // the class body, as the hit report's fractions
            entries.Add(new EnemyBarView(id, Protocol.BarByte(p.Health, health), Protocol.BarByte(p.Shield, shield)));
            if (entries.Count == Protocol.MaxPlayers) break;
        }
        return new EnemyBars(World.Tick, entries.ToArray());
    }

    /// <summary>Sends the bars messages when due.</summary>
    void SendBars()
    {
        if (World.Tick % BarsInterval != 0) return;
        foreach (var (connection, peer) in _peers)
            if (BarsFor(peer.PlayerId) is { } bars) _transport.Send(connection, Protocol.Write(bars));
    }
}
