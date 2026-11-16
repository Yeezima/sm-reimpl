using SmNet;
using SmSim;

namespace SmNet.Tests;

/// <summary>Polish pass P2: a map load off the main thread (ClientOptions.BeginLoadMap, IMapLoad): the Hello waits for it, the connect timeout waits for it, the hash goes out once it is done.</summary>
public class MapLoadAsyncTests
{
    static readonly SimConfig Config = new();
    static PlayerInput Idle(uint t) => new(t, 0, 0, 0, InputButtons.None);

    sealed class FakeLoad(int pollsUntilDone, LoadedMap? result, string? error = null) : IMapLoad
    {
        public int Polls;
        public bool Poll(out LoadedMap? loaded, out string? err)
        {
            loaded = null; err = null;
            if (++Polls < pollsUntilDone) return false;
            loaded = result; err = error;
            return true;
        }
    }

    static (GameClient Client, LoopbackNetwork Net, ManualClock Clock, Hello First) Connect(ClientOptions options)
    {
        var clock = new ManualClock();
        var net = new LoopbackNetwork(clock, LinkConditions.Perfect);
        var client = new GameClient(Config, 1, net.Connect(), clock, options, nonce: 9);
        clock.Advance(TimeSpan.FromMilliseconds(10)); client.Update(Idle);
        Assert.True(net.Server.TryReceive(out _, out var first));
        var hello = Assert.IsType<Hello>(Protocol.Read(first));
        net.Server.Send(0, Protocol.Write(new Challenge(hello.Nonce, 77, hello.SentMs, "mp_tdm_09")));
        return (client, net, clock, hello);
    }

    static void Step(GameClient client, ManualClock clock, int ms) { for (int i = 0; i < ms / 10; i++) { clock.Advance(TimeSpan.FromMilliseconds(10)); client.Update(Idle); } }

    [Fact]
    public void The_Hello_waits_for_the_load_and_then_carries_the_hash()
    {
        var world = new CollisionWorld(new StaticMesh([], []), new StaticMesh([], []));
        var load = new FakeLoad(pollsUntilDone: 500, new LoadedMap(world, 0xABCDEF));
        var (client, net, clock, _) = Connect(new ClientOptions { BeginLoadMap = _ => load, LoadMap = _ => throw new InvalidOperationException("the synchronous load must not run") });
        Step(client, clock, 20);   // the Challenge arrives: the load begins
        Assert.True(client.MapLoading);
        Assert.Equal("mp_tdm_09", client.MapName);
        // 4 s of loading: well past the 250 ms Hello interval, no Hello leaves (the server would read a hash of 0), and the status stays Connecting past the connect timeout's ten seconds.
        Step(client, clock, 4_000);
        Assert.True(client.MapLoading);
        Assert.False(net.Server.TryReceive(out _, out _));
        Step(client, clock, 7_000);
        Assert.False(client.MapLoading);
        Assert.Equal(ClientStatus.Connecting, client.Status);
        Assert.Null(client.MapError);
        Assert.Same(world, client.Collision);
        Assert.True(net.Server.TryReceive(out _, out var packet));
        var hello = Assert.IsType<Hello>(Protocol.Read(packet));
        Assert.Equal(0xABCDEFUL, hello.MapHash);
        Assert.Equal(77UL, hello.Cookie);
    }

    [Fact]
    public void A_failed_load_keeps_its_error_and_a_null_job_falls_back_to_the_synchronous_load()
    {
        var failing = new FakeLoad(pollsUntilDone: 3, null, "InvalidDataException: broken");
        var (client, _, clock, _) = Connect(new ClientOptions { BeginLoadMap = _ => failing });
        Step(client, clock, 100);
        Assert.False(client.MapLoading);
        Assert.Equal("InvalidDataException: broken", client.MapError);

        bool syncRan = false;
        var (client2, _, clock2, _) = Connect(new ClientOptions { BeginLoadMap = _ => null, LoadMap = _ => { syncRan = true; return new LoadedMap(new CollisionWorld(new StaticMesh([], []), new StaticMesh([], [])), 5); } });
        Step(client2, clock2, 50);
        Assert.True(syncRan);
        Assert.False(client2.MapLoading);
    }

    [Fact]
    public void The_connect_timeout_counts_from_the_end_of_the_load()
    {
        var load = new FakeLoad(pollsUntilDone: 2_000, new LoadedMap(new CollisionWorld(new StaticMesh([], []), new StaticMesh([], [])), 1));
        var (client, _, clock, _) = Connect(new ClientOptions { BeginLoadMap = _ => load });
        Step(client, clock, 20_200);   // 20 s of loading, then the Welcome never comes
        Assert.False(client.MapLoading);
        Assert.Equal(ClientStatus.Connecting, client.Status);
        Step(client, clock, 9_000);
        Assert.Equal(ClientStatus.Connecting, client.Status);
        Step(client, clock, 1_500);
        Assert.Equal(ClientStatus.NoAnswer, client.Status);
    }
}
