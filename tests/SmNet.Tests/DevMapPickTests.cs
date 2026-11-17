using System.Net;
using SmContent;
using SmNet;
using SmServer;
using SmSim;
using Xunit;

namespace SmNet.Tests;

/// <summary>
/// devtools-map-pick (protocol 85; the owner 2026-11-17): the developer tools' map picker. A pick from loopback on a <c>--dev-tools</c> server that can change its map ends the match at once and
/// leaves the server to its supervisor with the picked map (clients are told NEXT MAP and rejoin, as after a rotation); it is refused without the tools, from a non-loopback address and where the
/// server cannot change map. Synthetic data only.
/// </summary>
public sealed class DevMapPickTests
{
    static readonly LinkConditions Link = new(TimeSpan.FromMilliseconds(2), TimeSpan.Zero, 0);
    static readonly MatchRules Rules = new() { TimeLimit = 0f, ScoreLimit = 1000, CountdownTime = 0f, RestartDelay = 5f };
    static readonly string[] Maps = ["mp_tdm_09", "mp_tdm_17", "mp_tdm_01"];

    sealed class AddressedTransport(IServerTransport inner, IPEndPoint remote) : IServerTransport
    {
        public void Send(int connection, byte[] packet) => inner.Send(connection, packet);
        public bool TryReceive(out int connection, out byte[] packet) => inner.TryReceive(out connection, out packet);
        public void Disconnect(int connection) => inner.Disconnect(connection);
        public void Pin(int connection) => inner.Pin(connection);
        public IPEndPoint? EndPointOf(int connection) => remote;
    }

    static (Harness H, List<string> Log) Make(DevToolsSetup? setup, bool twoSides = true, IPEndPoint? remote = null)
    {
        var options = new ServerOptions { DevTools = setup, Match = Rules };
        var h = new Harness(Link, seed: 5, 1, serverOptions: options, wrapServer: remote is null ? null : t => new AddressedTransport(t, remote));
        var log = new List<string>();
        h.Server.Log += log.Add;
        if (twoSides) h.AddClient(nonce: 12, options: new ClientOptions { RequestedSide = Team.Chaos });
        h.Run(TimeSpan.FromSeconds(3));
        return (h, log);
    }

    static DevToolsSetup Setup(IReadOnlyList<string>? maps = null) => new(IPAddress.Loopback, null, null, maps ?? Maps);

    [Fact]
    public void A_pick_from_loopback_ends_the_match_and_hands_the_chosen_map_to_the_supervisor()
    {
        var (h, log) = Make(Setup());
        var me = h.Clients[0];
        Assert.True(me.DevActive);
        Assert.Equal(MatchState.Playing, h.Server.World.State);
        Assert.Null(h.Server.PickedMap);
        Assert.False(h.Server.RotationDue);

        me.SendDevCommand(DevCommandKind.PickMap, 1, true);
        h.Run(TimeSpan.FromSeconds(0.5));

        Assert.Equal("mp_tdm_17", h.Server.PickedMap);
        Assert.Equal(MatchState.Ended, h.Server.World.State);
        Assert.True(h.Server.RotationDue, "the end clock was hurried to the rotation's last second, so the server is due to leave");
        Assert.True(me.Match.NextMap, "the snapshots tell clients to rejoin, as for a rotation");
        Assert.Equal(DevNote.Applied, me.Dev!.Note);
        Assert.Contains(log, l => l.StartsWith("dev tools: map pick mp_tdm_17:"));
    }

    [Fact]
    public void A_pick_ends_a_match_that_is_only_waiting_for_players_too()
    {
        var (h, _) = Make(Setup(), twoSides: false);
        Assert.Equal(MatchState.Warmup, h.Server.World.State);
        h.Clients[0].SendDevCommand(DevCommandKind.PickMap, 2, true);
        h.Run(TimeSpan.FromSeconds(0.5));
        Assert.Equal("mp_tdm_01", h.Server.PickedMap);
        Assert.Equal(MatchState.Ended, h.Server.World.State);
        Assert.True(h.Server.RotationDue);
    }

    [Fact]
    public void A_second_pick_while_one_is_on_its_way_changes_nothing()
    {
        var (h, _) = Make(Setup());
        h.Clients[0].SendDevCommand(DevCommandKind.PickMap, 1, true);
        h.Run(TimeSpan.FromSeconds(0.3));
        h.Clients[0].SendDevCommand(DevCommandKind.PickMap, 2, true);
        h.Run(TimeSpan.FromSeconds(0.3));
        Assert.Equal("mp_tdm_17", h.Server.PickedMap);
    }

    [Fact]
    public void Without_the_tools_a_pick_does_nothing()
    {
        var (h, log) = Make(null);
        h.Clients[0].SendDevCommand(DevCommandKind.PickMap, 1, true);
        h.Run(TimeSpan.FromSeconds(0.5));
        Assert.Null(h.Server.PickedMap);
        Assert.Equal(MatchState.Playing, h.Server.World.State);
        Assert.False(h.Server.RotationDue);
        Assert.Contains(log, l => l.StartsWith("dev tools request ignored"));
    }

    [Fact]
    public void A_pick_from_a_non_loopback_address_is_ignored()
    {
        var (h, log) = Make(Setup(), twoSides: false, remote: new IPEndPoint(IPAddress.Parse("203.0.113.9"), 40000));   // (one address: one player)
        Assert.Equal(MatchState.Warmup, h.Server.World.State);
        h.Clients[0].SendDevCommand(DevCommandKind.PickMap, 1, true);
        h.Run(TimeSpan.FromSeconds(0.5));
        Assert.Null(h.Server.PickedMap);
        Assert.Equal(MatchState.Warmup, h.Server.World.State);   // a granted pick would have ended it
        Assert.Contains(log, l => l.Contains("not a loopback address"));
    }

    [Fact]
    public void A_server_that_cannot_change_map_or_a_bad_index_is_refused_and_the_match_goes_on()
    {
        var noMaps = Make(new DevToolsSetup(IPAddress.Loopback, null));
        noMaps.H.Clients[0].SendDevCommand(DevCommandKind.PickMap, 0, true);
        noMaps.H.Run(TimeSpan.FromSeconds(0.5));
        Assert.Null(noMaps.H.Server.PickedMap);
        Assert.Equal(MatchState.Playing, noMaps.H.Server.World.State);
        Assert.Equal(DevNote.NoMapChange, noMaps.H.Clients[0].Dev!.Note);

        var (h, _) = Make(Setup());
        h.Clients[0].SendDevCommand(DevCommandKind.PickMap, 9, true);
        h.Run(TimeSpan.FromSeconds(0.5));
        Assert.Null(h.Server.PickedMap);
        Assert.Equal(MatchState.Playing, h.Server.World.State);
        Assert.Equal(DevNote.BadValue, h.Clients[0].Dev!.Note);
    }

    [Fact]
    public void The_picker_lists_the_maps_of_the_rotation_default_first_then_by_display_name()
    {
        var catalog = new List<MapEntry>
        {
            new("mp_tdm_17", "Shattered Bridge", ["teamdeathmatch", "domination"], true),
            new("mp_tdm_09", "Manufactorum", ["teamdeathmatch", "domination"], true),
            new("mp_tdm_01", "Aquila", ["teamdeathmatch"], true),
            new("mp_tdm_20", "Unplayable", ["teamdeathmatch", "domination"], false),
            new("mp_dom_02", "Seize Only", ["domination"], true),
        };
        Assert.Equal(["mp_tdm_09", "mp_tdm_01", "mp_tdm_17"], DevMapPicks.For(catalog, GameMode.Annihilation).Select(m => m.Name));
        Assert.Equal(["mp_tdm_09", "mp_dom_02", "mp_tdm_17"], DevMapPicks.For(catalog, GameMode.SeizeGround).Select(m => m.Name));
        // the same set `--maps all` plays
        Assert.True(ServerCommandLine.TryParse(["--maps", "all"], out var cli, out _));
        Assert.True(RotationSupervisor.TryPlan(catalog, cli, out var plan, out _));
        Assert.Equal(plan.OrderBy(n => n), DevMapPicks.For(catalog, GameMode.Annihilation).Select(m => m.Name).OrderBy(n => n));
    }

    [Fact]
    public void The_supervisor_restarts_on_the_picked_map_and_reads_it_from_the_childs_file()
    {
        var args = new[] { "--bind", "127.0.0.1", "--port", "27401", "--dev-tools", "--map", "mp_tdm_01" };
        Assert.Equal(["--bind", "127.0.0.1", "--port", "27401", "--dev-tools", "--map", "mp_tdm_01", "--dev-supervised"], RotationSupervisor.DevChildArguments(args, null));
        Assert.Equal(["--bind", "127.0.0.1", "--port", "27401", "--dev-tools", "--map", "mp_tdm_17", "--dev-supervised"], RotationSupervisor.DevChildArguments(args, "mp_tdm_17"));
        Assert.True(ServerCommandLine.TryParse(RotationSupervisor.DevChildArguments(args, "mp_tdm_17"), out var child, out var error), error);
        Assert.True(child.DevSupervised);
        Assert.Equal("mp_tdm_17", child.Map);

        string file = Path.Combine(Path.GetTempPath(), $"sm-pick-test-{Guid.NewGuid():N}.txt");
        try
        {
            Assert.Null(RotationSupervisor.TakePick(file));
            File.WriteAllText(file, "mp_tdm_17\n");
            Assert.Equal("mp_tdm_17", RotationSupervisor.TakePick(file));
            Assert.False(File.Exists(file));
            File.WriteAllText(file, "../../evil");
            Assert.Null(RotationSupervisor.TakePick(file));
        }
        finally { File.Delete(file); }
    }
}
