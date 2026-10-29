using System.Net;
using System.Numerics;
using SmNet;
using SmServer;
using SmSim;

namespace SmNet.Tests;

/// <summary>Review follow-ups: spoofing, sessions, timeouts, long round trips, and the real UDP transport.</summary>
public class SecurityTests
{
    static readonly LinkConditions Link = new(TimeSpan.FromMilliseconds(10), TimeSpan.Zero, 0);

    [Fact]
    public void Hello_without_the_cookie_creates_no_player_and_gets_a_small_challenge()
    {
        var h = new Harness(Link, seed: 1, clients: 0);
        var raw = h.Net.Connect();
        var hello = Protocol.Write(new Hello(Protocol.Version, Harness.Fingerprint, h.Config.Hash(), 5));
        raw.Send(hello);
        h.Run(TimeSpan.FromMilliseconds(200));
        Assert.Empty(h.Server.World.Players);
        Assert.True(raw.TryReceive(out var reply));
        Assert.IsType<Challenge>(Protocol.Read(reply));
        Assert.True(reply.Length <= hello.Length, "the challenge must not amplify the request");

        // A forged cookie is not accepted either.
        raw.Send(Protocol.Write(new Hello(Protocol.Version, Harness.Fingerprint, h.Config.Hash(), 5, 0, 0x1234567)));
        h.Run(TimeSpan.FromMilliseconds(200));
        Assert.Empty(h.Server.World.Players);
    }

    [Fact]
    public void Inputs_with_a_wrong_session_are_ignored()
    {
        var h = new Harness(Link, seed: 1, clients: 0, serverOptions: new ServerOptions { PeerTimeout = TimeSpan.FromMinutes(1) });
        var raw = h.Net.Connect();
        var (id, session) = RobustnessTests.ConnectRaw(h, raw);
        var spawn = h.Server.World.Players[id].Position;
        raw.Send(Protocol.Write(new InputBatch([new PlayerInput(h.Server.World.Tick + 5, 0, 1, 0, InputButtons.Sprint)], session ^ 0xFFFF)));
        h.Run(TimeSpan.FromSeconds(1));
        Assert.Equal(spawn, h.Server.World.Players[id].Position);
    }

    [Fact]
    public void Silent_player_times_out_and_frees_its_slot()
    {
        var h = new Harness(Link, seed: 1, clients: 0, serverOptions: new ServerOptions { MaxPlayers = 1, PeerTimeout = TimeSpan.FromSeconds(2) });
        var raw = h.Net.Connect();
        RobustnessTests.ConnectRaw(h, raw);
        Assert.Single(h.Server.World.Players);
        h.Run(TimeSpan.FromSeconds(3));
        Assert.Empty(h.Server.World.Players);
        Assert.Equal(1, h.Server.TimedOut);
        var c = h.AddClient();
        h.Run(TimeSpan.FromSeconds(1));
        Assert.Equal(ClientStatus.Connected, c.Status);
    }

    [Fact]
    public void Leave_removes_the_player_at_once()
    {
        var h = new Harness(Link, seed: 1, clients: 2);
        h.Run(TimeSpan.FromSeconds(1));
        Assert.Equal(2, h.Server.World.Players.Count);
        h.Clients[0].Leave();
        h.Run(TimeSpan.FromMilliseconds(100));
        Assert.Single(h.Server.World.Players);
        Assert.Equal(ClientStatus.Disconnected, h.Clients[0].Status);
        Assert.Equal(0, h.Server.TimedOut);
    }

    [Fact]
    public void Leave_with_a_wrong_session_is_ignored()
    {
        var h = new Harness(Link, seed: 1, clients: 0, serverOptions: new ServerOptions { PeerTimeout = TimeSpan.FromMinutes(1) });
        var raw = h.Net.Connect();
        var (_, session) = RobustnessTests.ConnectRaw(h, raw);
        raw.Send(Protocol.Write(new Leave(session ^ 1)));
        h.Run(TimeSpan.FromMilliseconds(100));
        Assert.Single(h.Server.World.Players);
    }

    [Fact]
    public void A_silent_player_stops_moving_before_it_times_out()
    {
        var h = new Harness(Link, seed: 1, clients: 0, serverOptions: new ServerOptions { PeerTimeout = TimeSpan.FromMinutes(1) });
        var raw = h.Net.Connect();
        var (id, session) = RobustnessTests.ConnectRaw(h, raw);
        raw.Send(Protocol.Write(new InputBatch([new PlayerInput(h.Server.World.Tick + 3, 0, 1, 0.7f, InputButtons.Sprint)], session)));
        // Short silences (lost packets) still repeat the last input, so the player keeps moving for a while.
        h.Run(TimeSpan.FromMilliseconds(400));
        Assert.True(h.Server.World.Players[id].Velocity.Length() > 1f, "stopped before NeutralInputAfter");
        h.Run(TimeSpan.FromMilliseconds(600));
        Assert.Equal(0.7f, h.Server.World.Players[id].Yaw, 4);   // heading kept
        Assert.True(h.Server.World.Players[id].Velocity.Length() < 0.01f, "still moving after 1 s of silence");
        var stopped = h.Server.World.Players[id].Position;
        h.Run(TimeSpan.FromSeconds(1));
        Assert.Equal(stopped, h.Server.World.Players[id].Position);
    }

    [Fact]
    public void Client_notices_a_silent_server()
    {
        var h = new Harness(Link, seed: 1, clients: 1);
        h.Run(TimeSpan.FromSeconds(1));
        Assert.Equal(ClientStatus.Connected, h.Clients[0].Status);
        h.Net.Conditions = new LinkConditions(Link.Latency, Link.Jitter, 1.0);   // everything is lost from now on
        h.Run(TimeSpan.FromSeconds(6));
        Assert.Equal(ClientStatus.Disconnected, h.Clients[0].Status);
    }

    [Fact]
    public void Round_trip_longer_than_the_hello_interval_is_measured_and_inputs_arrive_in_time()
    {
        // 200 ms each way = 400 ms round trip, beyond the 250 ms Hello resend interval.
        var h = new Harness(new LinkConditions(TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(10), 0), seed: 3, clients: 1);
        h.Run(TimeSpan.FromSeconds(12), Harness.Scripted);
        var c = h.Clients[0];
        Assert.Equal(ClientStatus.Connected, c.Status);
        Assert.InRange(c.HandshakeRoundTrip.TotalMilliseconds, 370, 440);
        var errors = c.CorrectionErrors.ToArray();
        Assert.NotEmpty(errors);
        Assert.True(Harness.Percentile(errors, 0.95) < 0.05f, $"p95 {Harness.Percentile(errors, 0.95)}");
    }

    [Fact]
    public void Real_udp_on_localhost_connects_and_refuses_wrong_content()
    {
        var clock = new SystemClock();
        var config = new SimConfig();
        int port = 28000 + Random.Shared.Next(1000);
        using var serverTransport = new UdpServerTransport(new IPEndPoint(IPAddress.Loopback, port));
        var server = new GameServer(config, 7, serverTransport, clock);
        using var good = new UdpClientTransport(new IPEndPoint(IPAddress.Loopback, port));
        using var bad = new UdpClientTransport(new IPEndPoint(IPAddress.Loopback, port));
        var a = new GameClient(config, 7, good, clock, nonce: 1);
        var b = new GameClient(config, 8, bad, clock, nonce: 2);
        var until = clock.Now + TimeSpan.FromSeconds(2);
        while (clock.Now < until)
        {
            server.Update();
            a.Update(t => new PlayerInput(t, 0, 1, 0, InputButtons.None));
            b.Update(t => default);
            Thread.Sleep(1);
        }
        Assert.Equal(ClientStatus.Connected, a.Status);
        Assert.Equal(ClientStatus.Rejected, b.Status);
        Assert.Equal(RejectReason.ContentMismatch, b.RejectedBecause);
        Assert.True(server.World.Players[a.PlayerId].Position.Length() > 1f, "the UDP client's inputs moved its player");
    }

    [Fact]
    public void Address_flood_does_not_evict_a_connected_player()
    {
        var clock = new SystemClock();
        var config = new SimConfig();
        int port = 30000 + Random.Shared.Next(1000);
        using var serverTransport = new UdpServerTransport(new IPEndPoint(IPAddress.Loopback, port), maxAddresses: 4);
        var server = new GameServer(config, 7, serverTransport, clock);
        using var good = new UdpClientTransport(new IPEndPoint(IPAddress.Loopback, port));
        var a = new GameClient(config, 7, good, clock, nonce: 1);
        var flood = Enumerable.Range(0, 40).Select(_ => new UdpClientTransport(new IPEndPoint(IPAddress.Loopback, port))).ToList();
        var until = clock.Now + TimeSpan.FromSeconds(3);
        var lastSecond = until - TimeSpan.FromSeconds(1);
        int round = 0, snapshotsBeforeLastSecond = -1;
        while (clock.Now < until)
        {
            if (a.Status == ClientStatus.Connected && round++ % 5 == 0)
                foreach (var f in flood) f.Send([0xFF, 1, 2]);   // ten times the address cap, between the player's packets
            server.Update();
            a.Update(t => new PlayerInput(t, 0, 1, 0, InputButtons.None));
            if (snapshotsBeforeLastSecond < 0 && clock.Now >= lastSecond) snapshotsBeforeLastSecond = a.CorrectionErrors.Count;
            Thread.Sleep(1);
        }
        Assert.Equal(ClientStatus.Connected, a.Status);
        Assert.Single(server.World.Players);
        // If the flood had evicted the player's address, snapshots to it would stop (and its inputs would be ignored).
        int snapshotsInLastSecond = a.CorrectionErrors.Count - snapshotsBeforeLastSecond;
        Assert.True(snapshotsInLastSecond >= 10, $"only {snapshotsInLastSecond} snapshots reached the player in the last second of the flood");
        Assert.True(serverTransport.KnownAddresses <= 4);
        foreach (var f in flood) f.Dispose();
    }

    [Fact]
    public void Udp_server_forgets_addresses_beyond_its_cap()
    {
        int port = 29000 + Random.Shared.Next(1000);
        using var server = new UdpServerTransport(new IPEndPoint(IPAddress.Loopback, port), maxAddresses: 4);
        var senders = Enumerable.Range(0, 10).Select(_ => new UdpClientTransport(new IPEndPoint(IPAddress.Loopback, port))).ToList();
        foreach (var s in senders) s.Send([0xFF, 0, 0]);
        var until = DateTime.UtcNow.AddSeconds(1);
        int received = 0;
        while (DateTime.UtcNow < until && received < senders.Count)
            if (server.TryReceive(out _, out _)) received++; else Thread.Sleep(1);
        Assert.Equal(senders.Count, received);
        Assert.True(server.KnownAddresses <= 4);
        foreach (var s in senders) s.Dispose();
    }
}
