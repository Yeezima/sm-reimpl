using System.Net;
using SmNet;

namespace SmServer;

/// <summary>
/// The owner's "count it" (2026-11-08, musou/perf-pass): in a Musou match the core messages a client is sent (snapshots, scoreboards, bars, pickups: every message but the added content's envelope)
/// count inside its Musou send budget, so Musou's, the war's and the core's bytes together stay near 60 KB/s (<see cref="MusouInterest.CoreBytes"/>). Counted only while the match runs Musou Mode; a
/// Classic PvP server counts nothing and sends as before.
/// </summary>
public sealed partial class GameServer
{
    readonly Dictionary<int, long> _musouCoreBytes = new();   // per connection: core bytes since that client's last Musou plan
    bool MusouCountsCore => World.Config.Extras.HasFlag(SmSim.ContentExtras.Musou);

    IServerTransport CoreCounted(IServerTransport transport) => new CoreCountingTransport(transport, this);

    void CountCore(int connection, byte[] packet)
    {
        if (packet.Length == 0 || packet[0] == 16 || !MusouCountsCore) return;   // 16: the envelope, which the Musou and war budgets count themselves
        _musouCoreBytes[connection] = _musouCoreBytes.GetValueOrDefault(connection) + packet.Length;
    }

    /// <summary>The core bytes sent to <paramref name="connection"/> since the last call (then 0 again).</summary>
    long TakeCoreBytes(int connection) => _musouCoreBytes.Remove(connection, out long bytes) ? bytes : 0L;

    sealed class CoreCountingTransport(IServerTransport inner, GameServer server) : IServerTransport
    {
        public void Send(int connection, byte[] packet) { server.CountCore(connection, packet); inner.Send(connection, packet); }
        public bool TryReceive(out int connection, out byte[] packet) => inner.TryReceive(out connection, out packet);
        public void Disconnect(int connection) { server._musouCoreBytes.Remove(connection); inner.Disconnect(connection); }
        public void Pin(int connection) => inner.Pin(connection);
        public IPEndPoint? EndPointOf(int connection) => inner.EndPointOf(connection);
    }
}
