using System.Net;
using SmNet.Extras.Weather;
using SmServer;
using SmSim;
using SmSim.Extras.Weather;

namespace SmNet.Tests.Extras.Weather;

/// <summary>
/// Weather W1 on a real server over the loopback network: the state reaches every client (a late joiner too) and the developer-tools override obeys the gate: enabled, loopback bind, loopback requester;
/// an ordinary client, a non-loopback client and a server without the tools are refused. Presentation only: the match's state is the same with any weather.
/// </summary>
public class WeatherServerTests
{
    static readonly LinkConditions Link = new(TimeSpan.FromMilliseconds(2), TimeSpan.Zero, 0);
    static readonly SimConfig Config = new() { Extras = ContentExtras.Musou };

    /// <summary>A server transport that reports one remote address for every connection (the real UDP transport always has one).</summary>
    sealed class AddressedTransport(IServerTransport inner, IPEndPoint remote) : IServerTransport
    {
        public void Send(int connection, byte[] packet) => inner.Send(connection, packet);
        public bool TryReceive(out int connection, out byte[] packet) => inner.TryReceive(out connection, out packet);
        public void Disconnect(int connection) => inner.Disconnect(connection);
        public void Pin(int connection) => inner.Pin(connection);
        public IPEndPoint? EndPointOf(int connection) => remote;
    }

    static DevToolsSetup Setup(IPAddress? bind = null) => new(bind ?? IPAddress.Loopback, null);

    static (Harness H, List<string> Log) Make(DevToolsSetup? setup, WeatherId baseline = WeatherId.Rain, IPEndPoint? remote = null, int clients = 1)
    {
        var h = new Harness(Link, seed: 9, clients, Config, new ServerOptions { DevTools = setup }, wrapServer: remote is null ? null : (Func<IServerTransport, IServerTransport>)(t => new AddressedTransport(t, remote)));
        var log = new List<string>();
        h.Server.Log += log.Add;
        h.Server.WeatherBindForTests(baseline);
        return (h, log);
    }

    static void Settle(Harness h, double seconds = 1.0) => h.Run(TimeSpan.FromSeconds(seconds));

    static List<WeatherStateWire> States(GameClient c)
    {
        var list = new List<WeatherStateWire>();
        foreach (var s in c.TakeExtras())
            if (s.Tag == WeatherWire.StateTag && WeatherWire.TryDecode(s.Payload, out WeatherStateWire w)) list.Add(w);
        return list;
    }

    static void Send(GameClient c, WeatherCommand cmd) => Assert.True(c.SendExtrasCommand(WeatherWire.CommandTag, WeatherWire.Encode(cmd)));

    [Fact]
    public void Every_client_hears_the_match_weather_and_a_late_joiner_gets_the_current_phase_at_once()
    {
        var (h, _) = Make(null, WeatherId.LowFog);
        Settle(h, 3);
        var first = States(h.Clients[0]);
        Assert.NotEmpty(first);
        Assert.All(first, s => Assert.Equal((WeatherId.LowFog, false), (s.Id, s.Overridden)));
        Assert.True(first[^1].ElapsedMs > 1000);
        var late = h.AddClient(nonce: 500);
        Settle(h, 0.6);   // well inside the two-second repeat: the joiner is told when it joins
        var heard = States(late);
        Assert.NotEmpty(heard);
        Assert.Equal(WeatherId.LowFog, heard[^1].Id);
        Assert.True(heard[^1].ElapsedMs >= first[^1].ElapsedMs);
    }

    [Fact]
    public void The_developer_tools_set_none_and_follow_and_every_client_sees_the_same()
    {
        var (h, log) = Make(Setup(), WeatherId.Rain, clients: 2);
        Settle(h, 3);
        States(h.Clients[0]); States(h.Clients[1]);
        Send(h.Clients[0], new WeatherCommand(WeatherCommandKind.Set, WeatherId.Clear));
        Settle(h, 0.3);
        foreach (var c in h.Clients)
        {
            var s = States(c)[^1];
            Assert.Equal((WeatherId.Clear, WeatherPhase.Opening, true), (s.Id, s.Phase, s.Overridden));
        }
        Assert.Equal(WeatherId.Clear, h.Server.Weather!.Override);
        Send(h.Clients[0], new WeatherCommand(WeatherCommandKind.Set, WeatherId.None));
        Settle(h, 0.3);
        Assert.Equal((WeatherId.None, true), (States(h.Clients[1])[^1].Id, true));
        Send(h.Clients[1], new WeatherCommand(WeatherCommandKind.Follow));
        Settle(h, 0.3);
        var back = States(h.Clients[0])[^1];
        Assert.Equal((WeatherId.Rain, false), (back.Id, back.Overridden));
        Assert.True(back.ElapsedMs > 3000);   // the match's own clock kept running underneath
        Assert.Equal(3, h.Server.WeatherCommandsApplied);
        Assert.Contains(log, l => l.Contains("weather follows the match again"));
    }

    [Fact]
    public void An_ordinary_server_without_the_tools_refuses_the_override()
    {
        var (h, log) = Make(null);
        Settle(h);
        Send(h.Clients[0], new WeatherCommand(WeatherCommandKind.Set, WeatherId.Clear));
        Settle(h, 0.3);
        Assert.Null(h.Server.Weather!.Override);
        Assert.Equal((0, 1), (h.Server.WeatherCommandsApplied, h.Server.WeatherCommandsRefused));
        Assert.Contains(log, l => l.Contains("not started with --dev-tools"));
    }

    [Fact]
    public void A_server_not_bound_to_loopback_refuses_the_override_even_with_the_switch()
    {
        var (h, _) = Make(Setup(new IPAddress(new byte[4])));
        Settle(h);
        Send(h.Clients[0], new WeatherCommand(WeatherCommandKind.Set, WeatherId.Clear));
        Settle(h, 0.3);
        Assert.Null(h.Server.Weather!.Override);
        Assert.Equal(1, h.Server.WeatherCommandsRefused);
    }

    [Fact]
    public void A_non_loopback_client_is_refused_the_override_but_still_hears_the_weather()
    {
        var (h, log) = Make(Setup(), WeatherId.Clear, remote: new IPEndPoint(IPAddress.Parse("203.0.113.9"), 40000));
        Settle(h, 2.5);
        Assert.Equal(WeatherId.Clear, States(h.Clients[0])[^1].Id);
        Send(h.Clients[0], new WeatherCommand(WeatherCommandKind.Set, WeatherId.Rain));
        Settle(h, 0.3);
        Assert.True(h.Server.DevToolsOn);
        Assert.Null(h.Server.Weather!.Override);
        Assert.Equal((0, 1), (h.Server.WeatherCommandsApplied, h.Server.WeatherCommandsRefused));
        Assert.Contains(log, l => l.Contains("not a loopback address"));
    }

    [Fact]
    public void A_bot_and_a_spectator_never_command_the_weather_even_under_the_gate()
    {
        var (h, _) = Make(Setup(), clients: 0);
        var bot = h.AddClient(nonce: 31, options: new ClientOptions { Bot = true });
        var watcher = h.AddClient(nonce: 32, options: new ClientOptions { Observer = true });
        Settle(h, 1.5);
        Send(bot, new WeatherCommand(WeatherCommandKind.Set, WeatherId.Clear));
        Send(watcher, new WeatherCommand(WeatherCommandKind.Set, WeatherId.Clear));
        Settle(h, 0.5);
        Assert.Null(h.Server.Weather!.Override);
        Assert.Equal((0, 1), (h.Server.WeatherCommandsApplied, h.Server.WeatherCommandsRefused));   // the bot reaches the weather gate; the spectator is dropped before it by the envelope
        Assert.True(h.Server.ExtrasCommandsDropped >= 1);
    }

    [Fact]
    public void A_malformed_weather_command_changes_nothing()
    {
        var (h, _) = Make(Setup());
        Settle(h);
        Assert.True(h.Clients[0].SendExtrasCommand(WeatherWire.CommandTag, new byte[] { 1, 99 }));
        Assert.True(h.Clients[0].SendExtrasCommand(WeatherWire.CommandTag, new byte[] { 7 }));
        Settle(h, 0.3);
        Assert.Null(h.Server.Weather!.Override);
        Assert.Equal(2, h.Server.WeatherCommandsRefused);
    }

    [Fact]
    public void A_new_map_or_match_clears_the_override_and_starts_the_new_baseline_from_its_opening()
    {
        var (h, _) = Make(Setup(), WeatherId.Rain);
        Settle(h, 2);
        Send(h.Clients[0], new WeatherCommand(WeatherCommandKind.Set, WeatherId.LowFog));
        Settle(h, 0.3);
        Assert.True(h.Server.Weather!.Overridden);
        h.Server.Weather.Reset(WeatherId.Clear);
        Assert.False(h.Server.Weather.Overridden);
        Assert.Equal((WeatherId.Clear, WeatherPhase.Opening), (h.Server.Weather.Current.Id, h.Server.Weather.Current.Phase));
    }

    [Fact]
    public void Weather_is_presentation_only_the_world_is_the_same_with_any_weather()
    {
        static string Run(WeatherId id, bool force)
        {
            var (h, _) = Make(Setup(), id);
            Settle(h, 1);
            if (force) Send(h.Clients[0], new WeatherCommand(WeatherCommandKind.Set, WeatherId.Rain));
            Settle(h, 1);
            return $"{h.Server.World.Tick}|" + string.Join(';', h.Server.World.Players.Select(p => $"{p.Key}:{p.Value.Position}:{p.Value.Health}"));
        }
        Assert.Equal(Run(WeatherId.None, false), Run(WeatherId.Rain, false));
        Assert.Equal(Run(WeatherId.None, false), Run(WeatherId.Clear, true));
    }

    [Fact]
    public void Classic_pvp_is_untouched_no_weather_section_and_no_command_can_be_sent()
    {
        var h = new Harness(Link, 9, 1, new SimConfig(), new ServerOptions { DevTools = Setup() });
        Settle(h, 2.5);
        Assert.Null(h.Server.Weather);
        Assert.Empty(States(h.Clients[0]));
        Assert.False(h.Clients[0].SendExtrasCommand(WeatherWire.CommandTag, WeatherWire.Encode(new WeatherCommand(WeatherCommandKind.Follow))));   // a faithful client has no command channel at all
        Assert.Equal(0, h.Server.WeatherCommandsApplied + h.Server.WeatherCommandsRefused);
        Assert.Equal(0, h.Server.ExtrasMessagesSent);
    }

    [Fact]
    public void A_server_that_carries_no_weather_sends_none_and_ignores_the_command()
    {
        var h = new Harness(Link, 9, 1, Config, new ServerOptions { DevTools = Setup() });
        Settle(h, 2.5);
        Assert.Null(h.Server.Weather);
        Assert.Empty(States(h.Clients[0]));
        Send(h.Clients[0], new WeatherCommand(WeatherCommandKind.Set, WeatherId.Clear));
        Settle(h, 0.3);
        Assert.Null(h.Server.Weather);
    }
}
