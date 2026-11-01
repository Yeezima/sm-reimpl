using System.Net;
using System.Net.Sockets;
using SmNet;
using SmSim;

namespace SmNet.Tests;

/// <summary>
/// fix/connect-timeout (the owner's report of the tester release: the client sat on "Connecting to 127.0.0.1:27015 ..." forever, talking to an old server that drops
/// this version's Hello unread): a connecting client gives up after <see cref="ConnectFailure.DefaultTimeout"/> with no answer, the map load a Challenge starts is not
/// counted, a refusal stops the wait at once, and the end-of-match UI waits for a match state.
/// </summary>
public class ConnectTimeoutTests
{
    static readonly SimConfig Config = new();
    static PlayerInput Idle(uint t) => new(t, 0, 0, 0, InputButtons.None);

    [Fact]
    public void A_client_whose_server_never_answers_stops_with_NoAnswer_after_the_timeout()
    {
        // A real UDP socket on 127.0.0.1 that receives and never answers (test ports 27580-27589 only; never every interface).
        using var silent = new UdpClient(new IPEndPoint(IPAddress.Loopback, 27581));
        using var transport = new UdpClientTransport(new IPEndPoint(IPAddress.Loopback, 27581));
        var clock = new ManualClock();
        var client = new GameClient(Config, 1, transport, clock, new ClientOptions(), nonce: 9);
        int received = 0;
        var step = TimeSpan.FromMilliseconds(100);
        for (int i = 0; i < 99; i++)   // 9.9 s
        {
            client.Update(Idle);
            clock.Advance(step);
            while (silent.Available > 0) { IPEndPoint? from = null; silent.Receive(ref from); received++; }
        }
        Assert.Equal(ClientStatus.Connecting, client.Status);
        Assert.True(received >= 10, $"the Hellos reached the socket ({received})");   // resent every 250 ms
        for (int i = 0; i < 5; i++) { client.Update(Idle); clock.Advance(step); }   // past 10 s
        Assert.Equal(ClientStatus.NoAnswer, client.Status);

        // Stopped: no more Hellos.
        while (silent.Available > 0) { IPEndPoint? from = null; silent.Receive(ref from); }
        for (int i = 0; i < 10; i++) { client.Update(Idle); clock.Advance(step); }
        System.Threading.Thread.Sleep(50);
        Assert.Equal(0, silent.Available);
    }

    [Fact]
    public void The_map_load_a_Challenge_starts_does_not_count_toward_the_timeout()
    {
        var clock = new ManualClock();
        var net = new LoopbackNetwork(clock, LinkConditions.Perfect);
        // The load takes 30 s of clock time (a slow disk); the wait for the Welcome starts again after it.
        var options = new ClientOptions { LoadMap = _ => { clock.Advance(TimeSpan.FromSeconds(30)); return null; } };
        var client = new GameClient(Config, 1, net.Connect(), clock, options, nonce: 5);
        var server = net.Server;
        void Step(int ms) { for (int i = 0; i < ms / 10; i++) { clock.Advance(TimeSpan.FromMilliseconds(10)); client.Update(Idle); } }
        Step(10);
        Assert.True(server.TryReceive(out _, out var first));
        var hello = Assert.IsType<Hello>(Protocol.Read(first));
        server.Send(0, Protocol.Write(new Challenge(hello.Nonce, 77, hello.SentMs, "mp_tdm_09")));
        Step(20);   // the Challenge arrives, the map "loads" for 30 s
        Assert.Equal(ClientStatus.Connecting, client.Status);
        Step(9_500);
        Assert.Equal(ClientStatus.Connecting, client.Status);
        Step(1_000);
        Assert.Equal(ClientStatus.NoAnswer, client.Status);
    }

    [Fact]
    public void A_refusal_ends_the_wait_with_its_reason()
    {
        var clock = new ManualClock();
        var net = new LoopbackNetwork(clock, LinkConditions.Perfect);
        var client = new GameClient(Config, 1, net.Connect(), clock, new ClientOptions(), nonce: 6);
        client.Update(Idle);
        Assert.True(net.Server.TryReceive(out _, out var first));
        var hello = Assert.IsType<Hello>(Protocol.Read(first));
        net.Server.Send(0, Protocol.Write(new Reject(RejectReason.ConfigMismatch, "content profile faithful", hello.Nonce)));
        clock.Advance(TimeSpan.FromMilliseconds(5));
        client.Update(Idle);
        Assert.Equal(ClientStatus.Rejected, client.Status);
        Assert.Equal(RejectReason.ConfigMismatch, client.RejectedBecause);
        clock.Advance(TimeSpan.FromSeconds(30));
        client.Update(Idle);
        Assert.Equal(ClientStatus.Rejected, client.Status);   // a refusal is never turned into "no answer"
        Assert.Contains("Enable extended mode before reconnecting to the server", ConnectFailure.RejectText(client.RejectedBecause, client.RejectDetail));
    }

    [Fact]
    public void A_matching_server_is_joined_well_inside_the_timeout()
    {
        var h = new Harness(new LinkConditions(TimeSpan.FromMilliseconds(40), TimeSpan.Zero, 0), seed: 1, clients: 1);
        h.Run(TimeSpan.FromSeconds(2));
        Assert.Equal(ClientStatus.Connected, h.Clients[0].Status);
    }

    [Fact]
    public void Timeout_rule_and_texts()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), ConnectFailure.DefaultTimeout);
        Assert.False(ConnectFailure.TimedOut(TimeSpan.FromSeconds(10), TimeSpan.Zero, ConnectFailure.DefaultTimeout));
        Assert.True(ConnectFailure.TimedOut(TimeSpan.FromSeconds(10.01), TimeSpan.Zero, ConnectFailure.DefaultTimeout));
        Assert.Equal("No answer from 127.0.0.1:27015. Is a server running there, and is it the same version as yours?", ConnectFailure.NoAnswerText("127.0.0.1:27015"));
        Assert.Contains("another version", ConnectFailure.RejectText(RejectReason.ProtocolMismatch, "server protocol 39"));
        Assert.Contains("server protocol 39", ConnectFailure.RejectText(RejectReason.ProtocolMismatch, "server protocol 39"));
        Assert.Equal("The server is full.", ConnectFailure.RejectText(RejectReason.ServerFull, ""));
        foreach (var reason in Enum.GetValues<RejectReason>()) Assert.False(string.IsNullOrWhiteSpace(ConnectFailure.RejectText(reason, null)));
    }

    [Fact]
    public void The_refusal_layout_is_fixed_so_any_version_can_read_why()
    {
        // Kind 3, reason, nonce (u32 LE), detail length (u16 LE), UTF-8 detail: unchanged since protocol 1; a server of any version refuses another version's
        // Hello this way (GameServer.RefuseOldHello; servers from before 2026-10-28 dropped it silently, which the timeout now covers).
        var bytes = Protocol.Write(new Reject(RejectReason.ProtocolMismatch, "v", 0x01020304));
        Assert.Equal(new byte[] { 3, 1, 4, 3, 2, 1, 1, 0, (byte)'v' }, bytes);
    }

    [Fact]
    public void End_of_match_screens_wait_for_a_match_state()
    {
        Assert.False(MatchFlowGate.HasMatchState(default));   // connecting: the client's match is the default block
        Assert.True(MatchFlowGate.HasMatchState(new MatchView(MatchState.Warmup, Team.None, 0, 0, 0)));
        Assert.True(MatchFlowGate.HasMatchState(new MatchView(MatchState.Ended, Team.Chaos, 30, 0, 0)));
    }
}
