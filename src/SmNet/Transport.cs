using System.Net;

namespace SmNet;

/// <summary>Time source. Tests use <see cref="ManualClock"/> so runs are deterministic; servers use <see cref="SystemClock"/>.</summary>
public interface IClock { TimeSpan Now { get; } }

public sealed class ManualClock : IClock
{
    public TimeSpan Now { get; private set; }
    public void Advance(TimeSpan by) => Now += by;
}

public sealed class SystemClock : IClock
{
    readonly System.Diagnostics.Stopwatch _watch = System.Diagnostics.Stopwatch.StartNew();
    public TimeSpan Now => _watch.Elapsed;
}

/// <summary>Unreliable, unordered datagrams to and from one server.</summary>
public interface IClientTransport
{
    void Send(byte[] packet);
    bool TryReceive(out byte[] packet);
    /// <summary>
    /// True when <see cref="Send"/> may be called from a second thread while the main thread sends and receives (fix/stall-keepalive: the keep-alive thread
    /// of <see cref="GameClient"/>). Only the real UDP client says so; the in-process, recording and replay transports do not, and the keep-alive never runs on them.
    /// </summary>
    bool ThreadSafeSend => false;
}

/// <summary>Unreliable, unordered datagrams to and from many clients, identified by a connection id.</summary>
public interface IServerTransport
{
    void Send(int connection, byte[] packet);
    bool TryReceive(out int connection, out byte[] packet);
    /// <summary>Forgets a connection (timed out or left); later packets from that address get a new id.</summary>
    void Disconnect(int connection);
    /// <summary>Marks a connection as an authenticated player, so the transport never evicts it to make room.</summary>
    void Pin(int connection);
    /// <summary>
    /// The remote address of a connection, or null when the transport has none (loopback endpoints made without one, test
    /// wrappers). The server's per-address rules (player cap, join rate, bans) apply only to connections with an address.
    /// </summary>
    IPEndPoint? EndPointOf(int connection) => null;
}

/// <summary>One-way link conditions. Round-trip time is twice <see cref="Latency"/> on symmetric links.</summary>
public sealed record LinkConditions(TimeSpan Latency, TimeSpan Jitter, double Loss)
{
    public static readonly LinkConditions Perfect = new(TimeSpan.Zero, TimeSpan.Zero, 0);
}

/// <summary>
/// In-process network with simulated latency, jitter, and loss on every packet, driven by an <see cref="IClock"/>
/// and a seeded random generator, so a test run is reproducible. Jitter can reorder packets, as on a real network.
/// </summary>
public sealed class LoopbackNetwork
{
    readonly IClock _clock;
    readonly Random _random;
    readonly List<(TimeSpan Due, int Connection, bool ToServer, byte[] Packet)> _inFlight = new();
    readonly List<LoopbackClient> _clients = new();
    readonly Queue<(int, byte[])> _serverInbox = new();

    public LoopbackNetwork(IClock clock, LinkConditions conditions, int seed = 1)
    {
        _clock = clock;
        _random = new Random(seed);
        Conditions = conditions;
        Server = new LoopbackServer(this);
    }

    public LinkConditions Conditions { get; set; }
    public long PacketsSent { get; private set; }
    public long PacketsDropped { get; private set; }
    public IServerTransport Server { get; }

    /// <summary>A new client endpoint; its connection id on the server side is its creation index.</summary>
    public IClientTransport Connect() => Connect(null);

    /// <summary>A new client endpoint the server sees at <paramref name="endPoint"/> (<see cref="IServerTransport.EndPointOf"/>; null: no address).</summary>
    public IClientTransport Connect(IPEndPoint? endPoint)
    {
        var client = new LoopbackClient(this, _clients.Count, endPoint);
        _clients.Add(client);
        return client;
    }

    void Enqueue(int connection, bool toServer, byte[] packet)
    {
        // A real UDP transport drops anything larger (UdpServerTransport.MaxPacket); in tests an oversized packet is a bug.
        if (packet.Length > UdpServerTransport.MaxPacket)
            throw new InvalidOperationException($"packet of {packet.Length} bytes exceeds the {UdpServerTransport.MaxPacket}-byte limit");
        EnqueueChecked(connection, toServer, packet);
    }

    void EnqueueChecked(int connection, bool toServer, byte[] packet)
    {
        PacketsSent++;
        if (_random.NextDouble() < Conditions.Loss) { PacketsDropped++; return; }
        double jitter = (_random.NextDouble() * 2 - 1) * Conditions.Jitter.TotalMilliseconds;
        var delay = Conditions.Latency + TimeSpan.FromMilliseconds(Math.Max(-Conditions.Latency.TotalMilliseconds, jitter));
        _inFlight.Add((_clock.Now + delay, connection, toServer, (byte[])packet.Clone()));
    }

    /// <summary>Moves packets whose delivery time has come into the receivers' inboxes.</summary>
    void Deliver()
    {
        var now = _clock.Now;
        for (int i = 0; i < _inFlight.Count; i++)
        {
            var p = _inFlight[i];
            if (p.Due > now) continue;
            if (p.ToServer) _serverInbox.Enqueue((p.Connection, p.Packet));
            else _clients[p.Connection].Inbox.Enqueue(p.Packet);
            _inFlight.RemoveAt(i--);
        }
    }

    sealed class LoopbackServer(LoopbackNetwork net) : IServerTransport
    {
        public void Send(int connection, byte[] packet) => net.Enqueue(connection, toServer: false, packet);
        public void Disconnect(int connection) { }   // loopback ids are fixed per client endpoint
        public void Pin(int connection) { }
        public IPEndPoint? EndPointOf(int connection) => connection >= 0 && connection < net._clients.Count ? net._clients[connection].EndPoint : null;
        public bool TryReceive(out int connection, out byte[] packet)
        {
            net.Deliver();
            if (net._serverInbox.TryDequeue(out var item)) { (connection, packet) = item; return true; }
            (connection, packet) = (-1, []);
            return false;
        }
    }

    sealed class LoopbackClient(LoopbackNetwork net, int id, IPEndPoint? endPoint) : IClientTransport
    {
        public Queue<byte[]> Inbox { get; } = new();
        public IPEndPoint? EndPoint { get; } = endPoint;
        public void Send(byte[] packet) => net.Enqueue(id, toServer: true, packet);
        public bool TryReceive(out byte[] packet)
        {
            net.Deliver();
            return Inbox.TryDequeue(out packet!);
        }
    }
}
