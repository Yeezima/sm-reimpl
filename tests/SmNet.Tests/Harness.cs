using System.Numerics;
using SmNet;
using SmServer;
using SmSim;

namespace SmNet.Tests;

/// <summary>Synthetic match: one server and N clients on a loopback network, driven in 1 ms steps of a manual clock.</summary>
sealed class Harness
{
    public const ulong Fingerprint = 0x5EED_CAFE_F00D_0001UL;

    public ManualClock Clock { get; } = new();
    public LoopbackNetwork Net { get; }
    public GameServer Server { get; }
    public List<GameClient> Clients { get; } = new();
    public SimConfig Config { get; }

    readonly ClientOptions? _clientOptions;

    /// <param name="map">The server's map; clients get <paramref name="clientOptions"/> (e.g. a map loader).</param>
    public Harness(LinkConditions link, int seed, int clients, SimConfig? config = null, ServerOptions? serverOptions = null,
        Func<IServerTransport, IServerTransport>? wrapServer = null, GameMap? map = null, ClientOptions? clientOptions = null)
    {
        Config = config ?? new SimConfig();
        _clientOptions = clientOptions;
        Net = new LoopbackNetwork(Clock, link, seed);
        var serverTransport = wrapServer is null ? Net.Server : wrapServer(Net.Server);
        Server = new GameServer(Config, Fingerprint, serverTransport, Clock, serverOptions, map);
        for (int i = 0; i < clients; i++)
            Clients.Add(new GameClient(Config, Fingerprint, Net.Connect(), Clock, clientOptions, nonce: (uint)(100 + i)));
    }

    public GameClient AddClient(SimConfig? config = null, ulong? fingerprint = null, uint nonce = 999, ClientOptions? options = null)
    {
        var c = new GameClient(config ?? Config, fingerprint ?? Fingerprint, Net.Connect(), Clock, options ?? _clientOptions, nonce: nonce);
        Clients.Add(c);
        return c;
    }

    /// <summary>One 1 ms step: advance the clock, update the server, then every client.</summary>
    public void Step(Func<int, uint, PlayerInput>? input = null)
    {
        Clock.Advance(TimeSpan.FromMilliseconds(1));
        Server.Update();
        for (int i = 0; i < Clients.Count; i++)
        {
            int index = i;
            Clients[i].Update(t => input is null ? new PlayerInput(t, 0, 0, 0, InputButtons.None) : input(index, t));
        }
    }

    public void Run(TimeSpan duration, Func<int, uint, PlayerInput>? input = null, Action? after = null)
    {
        int steps = (int)duration.TotalMilliseconds;
        for (int i = 0; i < steps; i++) { Step(input); after?.Invoke(); }
    }

    /// <summary>Deterministic scripted input: varying stick and yaw, sprint phases, an occasional jump.</summary>
    public static PlayerInput Scripted(int client, uint tick)
    {
        float t = tick;
        float k = client * 1.7f + 0.3f;
        var buttons = InputButtons.None;
        if ((tick / 90 + (uint)client) % 2 == 0) buttons |= InputButtons.Sprint;
        if ((tick + (uint)client * 13) % 97 == 0) buttons |= InputButtons.Jump;
        return new PlayerInput(tick, MathF.Sin(t * 0.05f + k), MathF.Cos(t * 0.031f + k), MathF.Sin(t * 0.013f + k) * 2.5f, buttons);
    }

    public static double Percentile(IReadOnlyList<float> values, double p)
    {
        if (values.Count == 0) return double.NaN;
        var sorted = values.OrderBy(v => v).ToArray();
        int index = Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1);
        return sorted[index];
    }

    public static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}

/// <summary>Server transport wrapper that drops the first <c>n</c> Welcome packets it is asked to send.</summary>
sealed class DropFirstWelcomes(IServerTransport inner, int n) : IServerTransport
{
    int _remaining = n;
    public int Dropped { get; private set; }
    public void Send(int connection, byte[] packet)
    {
        if (_remaining > 0 && Protocol.Read(packet) is Welcome) { _remaining--; Dropped++; return; }
        inner.Send(connection, packet);
    }
    public bool TryReceive(out int connection, out byte[] packet) => inner.TryReceive(out connection, out packet);
    public void Disconnect(int connection) => inner.Disconnect(connection);
    public void Pin(int connection) => inner.Pin(connection);
}
