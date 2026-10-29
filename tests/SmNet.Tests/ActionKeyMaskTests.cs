using System.Net;
using SmServer;
using SmSim;

namespace SmNet.Tests;

/// <summary>
/// Added content's action key (<see cref="InputButtons.ExtraAction"/>, input bit 13) in the core server, so the faithful build tests it too: a server
/// whose match runs no added content that reads the key (<see cref="ContentExtras.ActionKeyUsers"/>) drops the bit from every input it stores, and
/// the bit alone is never play for the idle kick.
/// </summary>
public class ActionKeyMaskTests
{
    [Fact]
    public void The_key_alone_is_not_real_input()
    {
        Assert.False(GameServer.IsRealInput(new PlayerInput(1, 0, 0, 0, InputButtons.ExtraAction)));
        Assert.True(GameServer.IsRealInput(new PlayerInput(1, 0, 0, 0, InputButtons.ExtraAction | InputButtons.Fire)));
        Assert.True(GameServer.IsRealInput(new PlayerInput(1, 0.5f, 0, 0, InputButtons.ExtraAction)));
        Assert.False(GameServer.IsRealInput(new PlayerInput(1, 0, 0, 0, InputButtons.None)));
    }

    [Theory]
    [InlineData(ContentExtras.None)]
    [InlineData(ContentExtras.Orks | ContentExtras.Gore)]
    public void A_server_without_an_extra_that_reads_the_key_drops_it_from_the_stored_input(ContentExtras extras)
    {
        Assert.Equal(ContentExtras.None, extras & ContentExtras.ActionKeyUsers);
        var h = new Harness(LinkConditions.Perfect, 31, 1, new SimConfig { Extras = extras });
        h.Run(TimeSpan.FromSeconds(1.5));
        h.Run(TimeSpan.FromSeconds(0.5), (_, t) => new PlayerInput(t, 0, 0, 0, InputButtons.ExtraAction | InputButtons.Fire));
        var applied = h.Server.LastAppliedForTests(h.Clients[0].PlayerId);
        Assert.Equal(InputButtons.Fire, applied.Buttons & InputButtons.Fire);   // the input itself arrived
        Assert.Equal(InputButtons.None, applied.Buttons & InputButtons.ExtraAction);
    }

    [Fact]
    public void A_faithful_server_kicks_a_player_who_only_sends_the_key()
    {
        var h = new Harness(new LinkConditions(TimeSpan.FromMilliseconds(10), TimeSpan.Zero, 0), 32, 0, new SimConfig(), new ServerOptions { IdleKick = TimeSpan.FromSeconds(5) });
        foreach (var (ip, nonce) in new[] { ("10.0.0.1", 1u), ("10.0.0.2", 2u) })
            h.Clients.Add(new GameClient(h.Config, Harness.Fingerprint, h.Net.Connect(new IPEndPoint(IPAddress.Parse(ip), 1000)), h.Clock, nonce: nonce));
        h.Run(TimeSpan.FromSeconds(1));
        int keyOnly = h.Clients[0].PlayerId, moving = h.Clients[1].PlayerId;
        h.Run(TimeSpan.FromSeconds(7), (i, t) => i == 0
            ? new PlayerInput(t, 0, 0, 0, t % 2 == 0 ? InputButtons.ExtraAction : InputButtons.None)
            : new PlayerInput(t, (t / 60) % 2 == 0 ? 1 : -1, 0, 0, InputButtons.None));
        Assert.False(h.Server.World.Players.ContainsKey(keyOnly));
        Assert.True(h.Server.World.Players.ContainsKey(moving));
        Assert.Equal(1, h.Server.IdleKicked);
    }
}
