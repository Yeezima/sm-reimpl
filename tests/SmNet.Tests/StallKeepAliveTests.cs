using System.Net;
using System.Numerics;
using SmNet;
using SmServer;
using SmSim;

namespace SmNet.Tests;

/// <summary>
/// fix/stall-keepalive (ours; no protocol change): while the client's main thread is blocked, a background thread repeats the newest input batch (made neutral) so the server
/// does not time the player out; a stall past the bound still times out; a repeated or late batch applies no input twice. The timing tests drive
/// <see cref="GameClient.KeepAliveStep"/> with the manual clock (what the thread does every 100 ms); the thread itself runs in one test over a real UDP socket on 127.0.0.1.
/// </summary>
public class StallKeepAliveTests
{
    static readonly LinkConditions Link = new(TimeSpan.FromMilliseconds(10), TimeSpan.Zero, 0);
    static readonly ClientOptions KeepAlive = new() { StallKeepAlive = true };

    static PlayerInput Forward(uint t) => new(t, 0, 1, 0, InputButtons.None);

    /// <summary>One connected client walking forward for two seconds.</summary>
    static (Harness H, GameClient Client) Connected(ClientOptions? options)
    {
        var h = new Harness(Link, seed: 3, clients: 0);
        var client = h.AddClient(nonce: 77, options: options);
        h.Run(TimeSpan.FromSeconds(2), (_, t) => Forward(t));
        Assert.Equal(ClientStatus.Connected, client.Status);
        Assert.Single(h.Server.Players);
        return (h, client);
    }

    /// <summary>The main thread is blocked for <paramref name="stall"/>: the server runs on, the client's Update is not called; the keep-alive is looked at every 100 ms, as its thread does.</summary>
    static void Stall(Harness h, GameClient client, TimeSpan stall, bool keepAlive, Action? every100Ms = null)
    {
        int ms = (int)stall.TotalMilliseconds;
        for (int i = 1; i <= ms; i++)
        {
            h.Clock.Advance(TimeSpan.FromMilliseconds(1));
            h.Server.Update();
            if (i % 100 == 0)
            {
                if (keepAlive) client.KeepAliveStep(h.Clock.Now);
                every100Ms?.Invoke();
            }
        }
    }

    [Fact]
    public void A_client_that_stalls_10_s_with_the_keep_alive_stays_connected_and_resumes()
    {
        var (h, client) = Connected(KeepAlive);
        int id = client.PlayerId;
        var before = h.Server.World.Players[id].Position;

        Stall(h, client, TimeSpan.FromSeconds(10), keepAlive: true);

        Assert.Equal(0, h.Server.TimedOut);
        Assert.Equal([id], h.Server.Players);
        Assert.InRange(client.KeepAlivesSent, 17, 20);   // from 1 s of stall, every 0.5 s
        // It resumes: the snapshots that queued up are read, the connection holds, the client plays on and the server hears real input again.
        h.Run(TimeSpan.FromSeconds(3), (_, t) => Forward(t));
        Assert.Equal(ClientStatus.Connected, client.Status);
        Assert.Equal(0, h.Server.TimedOut);
        Assert.Equal([id], h.Server.Players);
        Assert.True(Vector3.Distance(h.Server.World.Players[id].Position, before) > 1f, "it walks on after the stall");
    }

    [Fact]
    public void The_same_stall_without_the_keep_alive_times_the_player_out()
    {
        var (h, client) = Connected(new ClientOptions());
        Stall(h, client, TimeSpan.FromSeconds(10), keepAlive: false);
        Assert.Equal(1, h.Server.TimedOut);
        Assert.Empty(h.Server.Players);
    }

    [Fact]
    public void A_stall_longer_than_the_bound_stops_the_keep_alive_and_times_out()
    {
        var (h, client) = Connected(KeepAlive);
        Stall(h, client, TimeSpan.FromSeconds(45), keepAlive: true);
        Assert.Equal(1, h.Server.TimedOut);                 // a hung client is not held for ever
        Assert.InRange(client.KeepAlivesSent, 55, 60);      // 1 s .. 30 s of stall, every 0.5 s: 59
    }

    [Fact]
    public void No_keep_alive_before_the_stall_threshold_or_while_updates_run()
    {
        var (h, client) = Connected(KeepAlive);
        Assert.Equal(0, client.KeepAlivesSent);
        // Updates keep coming: the thread's looks send nothing.
        for (int i = 0; i < 3000; i++)
        {
            h.Step((_, t) => Forward(t));
            if (i % 100 == 0) Assert.False(client.KeepAliveStep(h.Clock.Now));
        }
        // 0.9 s of stall is no stall yet.
        h.Clock.Advance(TimeSpan.FromMilliseconds(900));
        Assert.False(client.KeepAliveStep(h.Clock.Now));
        h.Clock.Advance(TimeSpan.FromMilliseconds(200));
        Assert.True(client.KeepAliveStep(h.Clock.Now));
        Assert.False(client.KeepAliveStep(h.Clock.Now));    // and not again inside the interval
        Assert.Equal(1, client.KeepAlivesSent);
    }

    [Fact]
    public void The_stalled_player_stands_still_instead_of_running_on_its_last_input()
    {
        var (h, client) = Connected(KeepAlive);
        int id = client.PlayerId;
        Vector3 at3 = default, at9 = default;
        int step = 0;
        Stall(h, client, TimeSpan.FromSeconds(10), keepAlive: true, () =>
        {
            step++;
            if (step == 30) at3 = h.Server.World.Players[id].Position;
            if (step == 90) at9 = h.Server.World.Players[id].Position;
        });
        Assert.Equal(0, h.Server.TimedOut);
        Assert.True(Vector3.Distance(at3, at9) < 0.01f, $"moved {Vector3.Distance(at3, at9)} m between 3 s and 9 s of the stall");
    }

    sealed class SpyTransport(IClientTransport inner) : IClientTransport
    {
        public List<InputBatch> Batches { get; } = new();
        public void Send(byte[] packet) { if (Protocol.Read(packet) is InputBatch b) Batches.Add(b); inner.Send(packet); }
        public bool TryReceive(out byte[] packet) => inner.TryReceive(out packet);
    }

    [Fact]
    public void The_keep_alive_is_not_play_for_the_idle_kick_and_keeps_the_ticks_and_the_heading()
    {
        // The keep-alive repeats the newest batch with its buttons and movement cleared: GameServer.IsRealInput sees none.
        var h = new Harness(Link, seed: 3, clients: 0);
        var spy = new SpyTransport(h.Net.Connect());
        var client = new GameClient(h.Config, Harness.Fingerprint, spy, h.Clock, KeepAlive, nonce: 8);
        h.Clients.Add(client);
        h.Run(TimeSpan.FromSeconds(2), (_, t) => new PlayerInput(t, 0.5f, 1f, 1.25f, InputButtons.Sprint | InputButtons.Fire));
        var newest = spy.Batches[^1];
        Assert.Contains(newest.Inputs, GameServer.IsRealInput);   // the ordinary batch is play
        h.Clock.Advance(TimeSpan.FromSeconds(1.5));
        Assert.True(client.KeepAliveStep(h.Clock.Now));
        var repeated = spy.Batches[^1];
        Assert.NotSame(newest, repeated);
        Assert.Equal(newest.Inputs.Select(i => i.Tick), repeated.Inputs.Select(i => i.Tick));
        Assert.Equal(newest.Session, repeated.Session);
        Assert.Equal(newest.SnapshotTick, repeated.SnapshotTick);
        Assert.NotEmpty(repeated.Inputs);
        Assert.All(repeated.Inputs, i => { Assert.False(GameServer.IsRealInput(i)); Assert.Equal(InputButtons.None, i.Buttons); Assert.Equal(0f, i.MoveX); Assert.Equal(0f, i.MoveY); });
        Assert.Equal(newest.Inputs.Select(i => i.Yaw), repeated.Inputs.Select(i => i.Yaw), new FloatNear());
    }

    sealed class FloatNear : IEqualityComparer<float>
    {
        public bool Equals(float x, float y) => MathF.Abs(x - y) < 0.01f;
        public int GetHashCode(float f) => 0;
    }

    // ---- a repeated or late batch applies nothing twice ----

    /// <summary>Sends each batch twice at once and again 150 ms later (a duplicated and a late packet), as a network can.</summary>
    sealed class RepeatingTransport(IClientTransport inner, IClock clock, bool repeat) : IClientTransport
    {
        readonly Queue<(TimeSpan Due, byte[] Packet)> _late = new();
        public void Send(byte[] packet)
        {
            inner.Send(packet);
            if (!repeat) return;
            inner.Send(packet);
            _late.Enqueue((clock.Now + TimeSpan.FromMilliseconds(150), packet));
            while (_late.TryPeek(out var next) && next.Due <= clock.Now) { inner.Send(next.Packet); _late.Dequeue(); }
        }
        public bool TryReceive(out byte[] packet) => inner.TryReceive(out packet);
    }

    static (Vector3 Position, uint Tick) RunWalk(bool repeat)
    {
        var h = new Harness(Link, seed: 4, clients: 0, serverOptions: new ServerOptions { PeerTimeout = TimeSpan.FromMinutes(5) });
        var c = new GameClient(h.Config, Harness.Fingerprint, new RepeatingTransport(h.Net.Connect(), h.Clock, repeat), h.Clock, nonce: 5);
        h.Clients.Add(c);
        // A varying input, so a tick applied twice or in the wrong place would show in the position.
        h.Run(TimeSpan.FromSeconds(8), (_, t) => new PlayerInput(t, MathF.Sin(t * 0.07f), 1f, (t / 90) * 0.5f, InputButtons.None));
        Assert.Equal(ClientStatus.Connected, c.Status);
        return (h.Server.World.Players[c.PlayerId].Position, h.Server.World.Tick);
    }

    [Fact]
    public void A_duplicated_or_late_batch_applies_no_input_twice()
    {
        var plain = RunWalk(repeat: false);
        var repeated = RunWalk(repeat: true);
        Assert.Equal(plain.Tick, repeated.Tick);
        Assert.True(Vector3.Distance(plain.Position, repeated.Position) < 1e-3f, $"{plain.Position} against {repeated.Position}");
    }

    // ---- the thread, over a real UDP socket on 127.0.0.1 ----

    static (GameServer Server, UdpServerTransport Transport) RealServer(TimeSpan peerTimeout)
    {
        var transport = new UdpServerTransport(new IPEndPoint(IPAddress.Loopback, 0));
        var server = new GameServer(new SimConfig(), Harness.Fingerprint, transport, new SystemClock(), new ServerOptions { PeerTimeout = peerTimeout });
        return (server, transport);
    }

    static void Pump(GameServer server, GameClient? client, int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            server.Update();
            client?.Update(t => Forward(t));
            Thread.Sleep(2);
        }
    }

    [Fact]
    public void The_thread_keeps_a_frozen_client_connected_over_real_UDP_and_stops_on_Leave()
    {
        var (server, serverTransport) = RealServer(TimeSpan.FromSeconds(1));
        using var _ = serverTransport;
        using var transport = new UdpClientTransport(serverTransport.LocalEndPoint);
        var options = new ClientOptions { StallKeepAlive = true, KeepAliveAfter = TimeSpan.FromMilliseconds(250), KeepAliveInterval = TimeSpan.FromMilliseconds(100), KeepAliveBound = TimeSpan.FromSeconds(10) };
        var client = new GameClient(new SimConfig(), Harness.Fingerprint, transport, new SystemClock(), options, nonce: 11);
        Pump(server, client, 1500);
        Assert.Equal(ClientStatus.Connected, client.Status);
        Assert.True(client.KeepAliveRunning);

        Pump(server, null, 3000);   // the client's main thread is blocked for 3 s: three times the server's timeout
        Assert.Equal(0, server.TimedOut);
        Assert.Single(server.Players);
        Assert.True(client.KeepAlivesSent >= 10, $"sent {client.KeepAlivesSent}");

        Pump(server, client, 800);  // and it resumes
        Assert.Equal(ClientStatus.Connected, client.Status);
        Assert.Single(server.Players);

        client.Leave();
        var stopped = DateTime.UtcNow.AddSeconds(2);
        while (client.KeepAliveRunning && DateTime.UtcNow < stopped) Thread.Sleep(10);
        Assert.False(client.KeepAliveRunning);
        int sent = client.KeepAlivesSent;
        Pump(server, null, 500);
        Assert.Equal(sent, client.KeepAlivesSent);
        Assert.Empty(server.Players);   // the Leave removed it
        Assert.Equal(0, server.TimedOut);
    }

    [Fact]
    public void Without_the_keep_alive_the_same_freeze_over_real_UDP_times_out()
    {
        var (server, serverTransport) = RealServer(TimeSpan.FromSeconds(1));
        using var _ = serverTransport;
        using var transport = new UdpClientTransport(serverTransport.LocalEndPoint);
        var client = new GameClient(new SimConfig(), Harness.Fingerprint, transport, new SystemClock(), new ClientOptions(), nonce: 12);
        Pump(server, client, 1500);
        Assert.Equal(ClientStatus.Connected, client.Status);
        Assert.False(client.KeepAliveRunning);
        Pump(server, null, 2000);
        Assert.Equal(1, server.TimedOut);
        Assert.Equal(0, client.KeepAlivesSent);
    }

    [Fact]
    public void The_thread_does_not_run_on_a_transport_that_is_not_thread_safe()
    {
        var (h, client) = Connected(KeepAlive);   // the in-process loopback transport
        Assert.False(client.KeepAliveRunning);
    }
}
