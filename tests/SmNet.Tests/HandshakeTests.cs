using SmNet;
using SmServer;
using SmSim;

namespace SmNet.Tests;

/// <summary>M4 acceptance: the handshake refuses mismatched protocol, content and config, and a full server.</summary>
public class HandshakeTests
{
    static readonly LinkConditions Link = new(TimeSpan.FromMilliseconds(20), TimeSpan.Zero, 0);

    /// <summary>Sends one packet from a raw endpoint and steps until that endpoint receives a parsable reply.</summary>
    static Message? Exchange(Harness h, IClientTransport raw, Message send, int maxMs = 1000)
    {
        raw.Send(Protocol.Write(send));
        for (int i = 0; i < maxMs; i++)
        {
            h.Step();
            if (raw.TryReceive(out var packet)) return Protocol.Read(packet);
        }
        return null;
    }

    [Fact]
    public void WrongProtocolVersion_IsRejected()
    {
        var h = new Harness(Link, seed: 1, clients: 0);
        var raw = h.Net.Connect();
        // Refusals come only after the cookie round, so a spoofed source never gets more bytes than it sent.
        var challenge = Assert.IsType<Challenge>(Exchange(h, raw, new Hello((ushort)(Protocol.Version + 1), Harness.Fingerprint, h.Config.Hash(), 77)));
        var reply = Exchange(h, raw, new Hello((ushort)(Protocol.Version + 1), Harness.Fingerprint, h.Config.Hash(), 77, 0, challenge.Cookie));
        var reject = Assert.IsType<Reject>(reply);
        Assert.Equal(RejectReason.ProtocolMismatch, reject.Reason);
        Assert.Equal(77u, reject.Nonce);
        Assert.Equal(1, h.Server.Rejected);
        Assert.Empty(h.Server.Players);
        Assert.Empty(h.Server.World.Players);
    }

    [Fact]
    public void ContentFingerprintMismatch_IsRejected()
    {
        var h = new Harness(Link, seed: 1, clients: 0);
        var c = h.AddClient(fingerprint: Harness.Fingerprint ^ 1);
        h.Run(TimeSpan.FromSeconds(1));
        Assert.Equal(ClientStatus.Rejected, c.Status);
        Assert.Equal(RejectReason.ContentMismatch, c.RejectedBecause);
        Assert.True(h.Server.Rejected >= 1);
        Assert.Empty(h.Server.Players);
        Assert.Empty(h.Server.World.Players);
        Assert.Equal(-1, c.PlayerId);
    }

    [Fact]
    public void ConfigMismatch_IsRejected()
    {
        var h = new Harness(Link, seed: 1, clients: 0);
        var c = h.AddClient(config: new SimConfig { WalkSpeed = 5.5f });
        h.Run(TimeSpan.FromSeconds(1));
        Assert.Equal(ClientStatus.Rejected, c.Status);
        Assert.Equal(RejectReason.ConfigMismatch, c.RejectedBecause);
        Assert.True(h.Server.Rejected >= 1);
        Assert.Empty(h.Server.Players);
        Assert.Empty(h.Server.World.Players);
    }

    [Fact]
    public void MatchingClient_IsAccepted()
    {
        var h = new Harness(Link, seed: 1, clients: 1);
        h.Run(TimeSpan.FromSeconds(1));
        var c = h.Clients[0];
        Assert.Equal(ClientStatus.Connected, c.Status);
        Assert.Null(c.RejectedBecause);
        Assert.Equal(0, h.Server.Rejected);
        Assert.Equal([c.PlayerId], h.Server.Players);
        Assert.True(h.Server.World.Players.ContainsKey(c.PlayerId));
        Assert.InRange(c.HandshakeRoundTrip.TotalMilliseconds, 39, 45);
    }

    [Fact]
    public void ServerFull_RefusesExtraClient()
    {
        var h = new Harness(Link, seed: 1, clients: 2, serverOptions: new ServerOptions { MaxPlayers = 2 });
        h.Run(TimeSpan.FromSeconds(1));
        var extra = h.AddClient(nonce: 555);
        h.Run(TimeSpan.FromSeconds(1));
        Assert.Equal(ClientStatus.Connected, h.Clients[0].Status);
        Assert.Equal(ClientStatus.Connected, h.Clients[1].Status);
        Assert.Equal(ClientStatus.Rejected, extra.Status);
        Assert.Equal(RejectReason.ServerFull, extra.RejectedBecause);
        Assert.Equal(2, h.Server.Players.Count);
        Assert.Equal(2, h.Server.World.Players.Count);
        Assert.True(h.Server.Rejected >= 1);
    }

    [Fact]
    public void Handshake_CompletesWhenEarlyPacketsAreLost()
    {
        var h = new Harness(new LinkConditions(TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(10), 1.0), seed: 3, clients: 2);
        h.Run(TimeSpan.FromMilliseconds(400));          // every early Hello is lost
        Assert.All(h.Clients, c => Assert.Equal(ClientStatus.Connecting, c.Status));
        h.Net.Conditions = h.Net.Conditions with { Loss = 0.5 };
        h.Run(TimeSpan.FromMilliseconds(600));          // 50% loss until 1 s
        h.Net.Conditions = h.Net.Conditions with { Loss = 0.05 };
        h.Run(TimeSpan.FromSeconds(4));
        Assert.All(h.Clients, c => Assert.Equal(ClientStatus.Connected, c.Status));
        Assert.Equal(2, h.Server.Players.Count);        // resent Hellos did not spawn duplicates
        Assert.Equal(2, h.Server.World.Players.Count);
    }

    [Fact]
    public void Handshake_CompletesWhenWelcomeIsLost()
    {
        DropFirstWelcomes? dropper = null;
        var h = new Harness(Link, seed: 1, clients: 1, wrapServer: t => dropper = new DropFirstWelcomes(t, 2));
        h.Run(TimeSpan.FromSeconds(2));
        Assert.Equal(2, dropper!.Dropped);
        Assert.Equal(ClientStatus.Connected, h.Clients[0].Status);
        Assert.Single(h.Server.Players);
        Assert.Single(h.Server.World.Players);
    }
}
