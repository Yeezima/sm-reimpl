using System.Net;
using System.Numerics;
using SmContent;
using SmNet;
using SmServer;
using SmSim;

namespace SmNet.Tests;

/// <summary>
/// dev-tools (protocol 80; the owner 2026-11-17): the in-game developer tools. The server obeys a request only when it was started with the switch, listens on loopback and the request
/// comes from loopback; each effect is applied by the sim or the server's input step. Synthetic classes and weapons only (no game data).
/// </summary>
public class DevToolsTests
{
    static readonly LinkConditions Link = new(TimeSpan.FromMilliseconds(2), TimeSpan.Zero, 0);

    sealed class FakeBot(int id) : IDevBotProcess
    {
        public int Id { get; } = id;
        public bool Stopped;
        public bool Running => !Stopped;
        public void Stop() => Stopped = true;
    }

    sealed class FakeLauncher : IDevBotLauncher
    {
        public readonly List<BotSlot> Started = new();
        public readonly List<uint> Tokens = new();
        public readonly List<FakeBot> Bots = new();
        public bool Fail;
        public IDevBotProcess? Start(BotSlot slot, uint token, out string error)
        {
            error = Fail ? "no executable" : "";
            if (Fail) return null;
            Started.Add(slot); Tokens.Add(token);
            var bot = new FakeBot(1000 + Bots.Count);
            Bots.Add(bot);
            return bot;
        }
    }

    /// <summary>A server transport that reports one remote address for every connection (the real UDP transport always has one).</summary>
    sealed class AddressedTransport(IServerTransport inner, IPEndPoint remote) : IServerTransport
    {
        public void Send(int connection, byte[] packet) => inner.Send(connection, packet);
        public bool TryReceive(out int connection, out byte[] packet) => inner.TryReceive(out connection, out packet);
        public void Disconnect(int connection) => inner.Disconnect(connection);
        public void Pin(int connection) => inner.Pin(connection);
        public IPEndPoint? EndPointOf(int connection) => remote;
    }

    static DevToolsSetup Setup(IPAddress? bind = null, IDevBotLauncher? launcher = null) => new(bind ?? IPAddress.Loopback, launcher);

    static (Harness H, List<string> Log) Make(DevToolsSetup? setup, int clients = 1, MatchRules? match = null, IPEndPoint? remote = null, int maxPlayers = Protocol.MaxPlayers)
    {
        var options = new ServerOptions { DevTools = setup, Match = match, MaxPlayers = maxPlayers };
        var h = new Harness(Link, seed: 5, clients, serverOptions: options, wrapServer: remote is null ? null : t => new AddressedTransport(t, remote));
        var log = new List<string>();
        h.Server.Log += log.Add;
        return (h, log);
    }

    static void Settle(Harness h, double seconds = 1.5) => h.Run(TimeSpan.FromSeconds(seconds));

    // ---- the wire ----

    [Fact]
    public void The_protocol_is_80_and_the_two_messages_round_trip_and_refuse_nonsense()
    {
        Assert.Equal(87, Protocol.Version);
        var command = Assert.IsType<DevCommand>(Protocol.Read(Protocol.Write(new DevCommand(77u, DevCommandKind.SetFlag, (byte)DevToolsFlags.InfiniteAmmo, true))));
        Assert.Equal((77u, DevCommandKind.SetFlag, (byte)DevToolsFlags.InfiniteAmmo, true), (command.Session, command.Kind, command.Argument, command.Value));
        Assert.Equal(Protocol.DevCommandBytes, Protocol.Write(command).Length);
        var state = new DevState(DevToolsFlags.Invincible | DevToolsFlags.InfiniteJump, 2, 3, 5, 4, DevNote.SideFull, 9);
        Assert.Equal(state, Protocol.Read(Protocol.Write(state)));
        Assert.Equal(Protocol.DevStateBytes, Protocol.Write(state).Length);
        var bad = Protocol.Write(command); bad[5] = 99;   // an unknown command kind
        Assert.Null(Protocol.Read(bad));
        var badValue = Protocol.Write(command); badValue[7] = 2;
        Assert.Null(Protocol.Read(badValue));
        var badFlags = Protocol.Write(state); badFlags[1] = 0x80;
        Assert.Null(Protocol.Read(badFlags));
    }

    // ---- the gate ----

    [Fact]
    public void The_gate_wants_the_switch_a_loopback_bind_and_a_loopback_requester()
    {
        Assert.False(DevToolsGate.Enabled(null));
        Assert.False(DevToolsGate.Enabled(Setup(new IPAddress(new byte[4]))));
        Assert.False(DevToolsGate.Enabled(Setup(new IPAddress(new byte[16]))));
        Assert.False(DevToolsGate.Enabled(Setup(IPAddress.Parse("192.168.1.20"))));
        Assert.True(DevToolsGate.Enabled(Setup(IPAddress.Loopback)));
        Assert.True(DevToolsGate.Enabled(Setup(IPAddress.IPv6Loopback)));
        Assert.True(DevToolsGate.Allows(Setup(), new IPEndPoint(IPAddress.Loopback, 1234)));
        Assert.True(DevToolsGate.Allows(Setup(), new IPEndPoint(IPAddress.IPv6Loopback, 1234)));
        Assert.False(DevToolsGate.Allows(Setup(), new IPEndPoint(IPAddress.Parse("10.0.0.7"), 1234)));
        Assert.False(DevToolsGate.Allows(Setup(new IPAddress(new byte[4])), new IPEndPoint(IPAddress.Loopback, 1234)));
        Assert.False(DevToolsGate.Allows(null, new IPEndPoint(IPAddress.Loopback, 1234)));
    }

    [Fact]
    public void Without_the_switch_every_request_is_ignored_logged_once_and_nothing_is_announced()
    {
        var (h, log) = Make(null);
        Settle(h);
        var client = h.Clients[0];
        Assert.Equal(ClientStatus.Connected, client.Status);
        for (int i = 0; i < 3; i++) { client.SendDevCommand(DevCommandKind.SetFlag, (byte)DevToolsFlags.Invincible, true); Settle(h, 0.1); }
        client.SendDevCommand(DevCommandKind.AddBot, DevSides.Chaos, true);
        Settle(h);
        Assert.Equal(DevToolsFlags.None, h.Server.World.DevFlags);
        Assert.False(h.Server.DevToolsOn);
        Assert.Null(client.Dev);
        Assert.False(client.DevActive);
        Assert.Equal(1, log.Count(l => l.StartsWith("dev tools request ignored")));
    }

    [Fact]
    public void A_server_bound_to_every_interface_ignores_the_tools_even_with_the_switch()
    {
        var launcher = new FakeLauncher();
        var (h, log) = Make(Setup(new IPAddress(new byte[4]), launcher));
        Settle(h);
        h.Clients[0].SendDevCommand(DevCommandKind.SetFlag, (byte)DevToolsFlags.Invincible, true);
        h.Clients[0].SendDevCommand(DevCommandKind.AddBot, DevSides.Loyalists, true);
        Settle(h);
        Assert.False(h.Server.DevToolsOn);
        Assert.Equal(DevToolsFlags.None, h.Server.World.DevFlags);
        Assert.Empty(launcher.Started);
        Assert.Null(h.Clients[0].Dev);
        Assert.Contains(log, l => l.StartsWith("dev tools request ignored"));
    }

    [Fact]
    public void A_request_from_a_non_loopback_address_is_ignored_and_that_player_is_sent_no_state()
    {
        var launcher = new FakeLauncher();
        var (h, log) = Make(Setup(IPAddress.Loopback, launcher), remote: new IPEndPoint(IPAddress.Parse("203.0.113.9"), 40000));
        Settle(h, 2.5);
        h.Clients[0].SendDevCommand(DevCommandKind.SetFlag, (byte)DevToolsFlags.Invincible, true);
        h.Clients[0].SendDevCommand(DevCommandKind.AddBot, DevSides.Loyalists, true);
        Settle(h);
        Assert.True(h.Server.DevToolsOn);
        Assert.Equal(DevToolsFlags.None, h.Server.World.DevFlags);
        Assert.Empty(launcher.Started);
        Assert.Null(h.Clients[0].Dev);   // never announced to a remote address
        Assert.Contains(log, l => l.Contains("not a loopback address"));
    }

    [Fact]
    public void The_command_line_takes_the_switch_and_the_host_panel_passes_it_only_for_this_pc_only()
    {
        Assert.True(ServerCommandLine.TryParse(["--bind", "127.0.0.1", "--dev-tools", "--dev-bot-exe", "godot.exe", "--dev-bot-project", "client"], out var parsed, out _));
        Assert.True(parsed.DevTools);
        Assert.Equal(("godot.exe", "client"), (parsed.DevBotExe, parsed.DevBotProject));
        Assert.True(ServerCommandLine.TryParse([], out var plain, out _));
        Assert.False(plain.DevTools);   // default off
        Assert.False(ServerCommandLine.TryParse(["--dev-bot-exe"], out _, out _));
        Assert.Contains("--dev-tools", ServerCommandLine.Usage);

        var local = ServerLaunch.Arguments(new HostOptions(ServerLaunch.ModeAnnihilation, 27590, true, "", DevTools: true, DevBotExe: "godot.exe", DevBotProject: "client"));
        Assert.Contains("--dev-tools", local);
        Assert.Equal("godot.exe", local[local.IndexOf("--dev-bot-exe") + 1]);
        Assert.Contains("127.0.0.1", local);
        Assert.DoesNotContain("--dev-tools", ServerLaunch.Arguments(new HostOptions(ServerLaunch.ModeAnnihilation, 27590, false, "", DevTools: true)));   // LAN / Internet: never
        Assert.DoesNotContain("--dev-tools", ServerLaunch.Arguments(new HostOptions(ServerLaunch.ModeAnnihilation, 27590, true, "")));   // default off
    }

    [Fact]
    public void The_host_panel_always_starts_the_tools_for_a_local_classic_match_and_nowhere_else()
    {
        Assert.True(ServerLaunch.HostStartsDevTools(localOnly: true, musou: false));    // This PC only, Classic PvP: on
        Assert.False(ServerLaunch.HostStartsDevTools(localOnly: false, musou: false));  // LAN / Internet: unchanged
        Assert.False(ServerLaunch.HostStartsDevTools(localOnly: true, musou: true));    // Musou: unchanged
        Assert.False(ServerLaunch.HostStartsDevTools(localOnly: false, musou: true));
        foreach (var mode in new[] { ServerLaunch.ModeAnnihilation, ServerLaunch.ModeSeize })
        {
            var args = ServerLaunch.Arguments(new HostOptions(mode, 27590, true, "", DevTools: ServerLaunch.HostStartsDevTools(true, false)));
            Assert.Contains("--dev-tools", args);
            Assert.Contains("127.0.0.1", args);   // the server obeys the switch only on this loopback bind
        }
        Assert.DoesNotContain("--dev-tools", ServerLaunch.Arguments(new HostOptions(ServerLaunch.ModeSeize, 27590, false, "", DevTools: ServerLaunch.HostStartsDevTools(false, false))));
    }

    // ---- the toggles through a real server ----

    [Fact]
    public void An_allowed_request_sets_the_flag_logs_it_and_the_client_is_told_the_state()
    {
        var (h, log) = Make(Setup());
        Settle(h, 2.5);
        var client = h.Clients[0];
        Assert.True(client.DevActive);   // announced once a second
        Assert.Equal(DevToolsFlags.None, client.Dev!.Flags);
        client.SendDevCommand(DevCommandKind.SetFlag, (byte)DevToolsFlags.Invincible, true);
        client.SendDevCommand(DevCommandKind.SetFlag, (byte)DevToolsFlags.InfiniteAmmo, true);
        Settle(h, 0.3);
        Assert.Equal(DevToolsFlags.Invincible | DevToolsFlags.InfiniteAmmo, h.Server.World.DevFlags);
        Assert.Equal(DevToolsFlags.Invincible | DevToolsFlags.InfiniteAmmo, client.Dev!.Flags);   // the panel shows what the server reports
        Assert.Equal(DevNote.Applied, client.Dev.Note);
        Assert.Contains(log, l => l == $"dev tools: invincible ON (player {client.PlayerId})");
        Assert.Contains(log, l => l == $"dev tools: infinite ammunition ON (player {client.PlayerId})");
        client.SendDevCommand(DevCommandKind.SetFlag, (byte)DevToolsFlags.Invincible, false);
        Settle(h, 0.3);
        Assert.Equal(DevToolsFlags.InfiniteAmmo, h.Server.World.DevFlags);
        Assert.Contains(log, l => l.StartsWith("dev tools: invincible OFF"));
        client.SendDevCommand(DevCommandKind.SetFlag, 3, true);   // two bits at once, or none: not a flag
        client.SendDevCommand(DevCommandKind.SetFlag, 0, true);
        Settle(h, 0.3);
        Assert.Equal(DevToolsFlags.InfiniteAmmo, h.Server.World.DevFlags);
    }

    [Fact]
    public void Frozen_bots_stand_still_and_the_flag_leaves_humans_and_unfrozen_bots_alone()
    {
        static Vector3 Run(bool freeze)
        {
            var (h, _) = Make(Setup(), clients: 0);
            var human = h.AddClient(nonce: 11);
            var bot = h.AddClient(nonce: 12, options: new ClientOptions { Bot = true });
            Settle(h, 2.5);
            if (freeze) { human.SendDevCommand(DevCommandKind.SetFlag, (byte)DevToolsFlags.FreezeBots, true); Settle(h, 0.5); }
            Assert.Equal(freeze, (h.Server.World.DevFlags & DevToolsFlags.FreezeBots) != 0);
            var botStart = h.Server.World.Players[bot.PlayerId].Position;
            var humanStart = h.Server.World.Players[human.PlayerId].Position;
            h.Run(TimeSpan.FromSeconds(2), (i, t) => new PlayerInput(t, 0, 1, 0, InputButtons.None));   // everyone pushes forward
            Assert.True(Vector3.Distance(humanStart, h.Server.World.Players[human.PlayerId].Position) > 1f, "a human is never frozen");
            Assert.NotNull(bot.Dev);   // a bot gets the state too: it takes the bot values and the time scale from it
            return h.Server.World.Players[bot.PlayerId].Position - botStart;
        }
        var frozen = Run(freeze: true);
        var free = Run(freeze: false);
        Assert.True(new Vector2(frozen.X, frozen.Y).Length() < 0.01f, $"a frozen bot moved {frozen}");
        Assert.True(new Vector2(free.X, free.Y).Length() > 1f, $"a free bot moved only {free}");
    }

    // ---- bots ----

    static MatchRules Teams => new() { HonorSideRequests = true, TimeLimit = 0f };

    [Fact]
    public void Adding_a_bot_starts_one_process_for_that_side_and_removing_it_stops_that_process_and_its_player()
    {
        var launcher = new FakeLauncher();
        var (h, log) = Make(Setup(launcher: launcher), match: Teams);
        Settle(h, 2.5);
        var human = h.Clients[0];
        human.SendDevCommand(DevCommandKind.AddBot, DevSides.Chaos, true);
        Settle(h, 0.3);
        var slot = Assert.Single(launcher.Started);
        Assert.Equal((HostBots.Chaos, "chaos tactical"), (slot.Side, slot.Class));
        Assert.Equal(DevNote.BotStarting, human.Dev!.Note);
        Assert.Contains(log, l => l.StartsWith("dev tools: add bot (chaos, class 'chaos tactical') started, pid 1000"));
        // The process becomes a player on its side; the tools tie the two together.
        var foreign = h.AddClient(nonce: 77, options: new ClientOptions { Bot = true, RequestedSide = Team.Chaos });   // a bots.cmd bot of the same side that joined first
        Settle(h, 1.0);
        var bot = h.AddClient(nonce: launcher.Tokens[0], options: new ClientOptions { Bot = true, RequestedSide = Team.Chaos });   // the process the tools started: its Hello nonce is its token
        Settle(h, 2.5);
        Assert.Equal(Team.Chaos, h.Server.World.Players[bot.PlayerId].Team);
        Assert.Equal(1, human.Dev!.ToolBotsChaos);
        Assert.Equal(2, human.Dev.PlayersChaos);
        human.SendDevCommand(DevCommandKind.RemoveBot, DevSides.Loyalists, true);   // none on that side
        Settle(h, 0.3);
        Assert.Equal(DevNote.NoBotToRemove, human.Dev!.Note);
        Assert.False(launcher.Bots[0].Stopped);
        human.SendDevCommand(DevCommandKind.RemoveBot, DevSides.Chaos, true);
        Settle(h, 0.5);
        Assert.True(launcher.Bots[0].Stopped);
        Assert.DoesNotContain(bot.PlayerId, h.Server.Players);   // removed at once, not after the idle timeout
        Assert.Contains(foreign.PlayerId, h.Server.Players);   // the other bot of that side is not the tools' to remove
        Assert.Equal(DevNote.BotRemoved, human.Dev!.Note);
        Assert.Equal(0, human.Dev.ToolBotsChaos);
        Assert.Contains(log, l => l.StartsWith("dev tools: remove bot (chaos) stopped pid 1000, player"));
    }

    [Fact]
    public void Bots_are_refused_when_the_server_the_side_or_the_launcher_has_no_room()
    {
        var launcher = new FakeLauncher();
        // A server of two: the human and one more player.
        var (h, _) = Make(Setup(launcher: launcher), clients: 2, match: Teams, maxPlayers: 2);
        Settle(h, 2.5);
        h.Clients[0].SendDevCommand(DevCommandKind.AddBot, DevSides.Loyalists, true);
        Settle(h, 0.3);
        Assert.Equal(DevNote.ServerFull, h.Clients[0].Dev!.Note);
        Assert.Empty(launcher.Started);

        // A side of one.
        var (h2, _) = Make(Setup(launcher: launcher), match: Teams with { MaxPerSide = 1 });
        Settle(h2, 2.5);
        var side = h2.Server.World.Players[h2.Clients[0].PlayerId].Team;
        h2.Clients[0].SendDevCommand(DevCommandKind.AddBot, side == Team.Chaos ? DevSides.Chaos : DevSides.Loyalists, true);
        Settle(h2, 0.3);
        Assert.Equal(DevNote.SideFull, h2.Clients[0].Dev!.Note);
        Assert.Empty(launcher.Started);

        // No Godot executable: the tools say so and start nothing.
        var (h3, _) = Make(Setup(launcher: null), match: Teams);
        Settle(h3, 2.5);
        h3.Clients[0].SendDevCommand(DevCommandKind.AddBot, DevSides.Chaos, true);
        Settle(h3, 0.3);
        Assert.Equal(DevNote.LauncherMissing, h3.Clients[0].Dev!.Note);

        // A launch that fails.
        var failing = new FakeLauncher { Fail = true };
        var (h4, log4) = Make(Setup(launcher: failing), match: Teams);
        Settle(h4, 2.5);
        h4.Clients[0].SendDevCommand(DevCommandKind.AddBot, DevSides.Chaos, true);
        Settle(h4, 0.3);
        Assert.Equal(DevNote.LaunchFailed, h4.Clients[0].Dev!.Note);
        Assert.Contains(log4, l => l.Contains("failed: no executable"));
    }

    [Fact]
    public void Stopping_the_tools_bots_stops_every_process_they_started()
    {
        var launcher = new FakeLauncher();
        var (h, _) = Make(Setup(launcher: launcher), match: Teams);
        Settle(h, 2.5);
        h.Clients[0].SendDevCommand(DevCommandKind.AddBot, DevSides.Chaos, true);
        Settle(h, 0.3);
        h.Clients[0].SendDevCommand(DevCommandKind.AddBot, DevSides.Loyalists, true);
        Settle(h, 0.3);
        Assert.Equal(2, launcher.Bots.Count);
        Assert.Equal(2, h.Server.ToolBotCount);
        h.Server.StopToolBots();
        Assert.All(launcher.Bots, b => Assert.True(b.Stopped));
        Assert.Equal(0, h.Server.ToolBotCount);
    }

    [Fact]
    public void A_tool_bot_that_never_joins_is_stopped_after_the_timeout_and_frees_its_slot()
    {
        var launcher = new FakeLauncher();
        var (h, log) = Make(Setup(launcher: launcher), match: Teams);
        Settle(h, 2.5);
        h.Clients[0].SendDevCommand(DevCommandKind.AddBot, DevSides.Chaos, true);
        Settle(h, 0.5);
        Assert.Equal(1, h.Server.ToolBotCount);
        Settle(h, GameServer.ToolBotJoinTimeout.TotalSeconds + 2);
        Assert.True(launcher.Bots[0].Stopped);
        Assert.Equal(0, h.Server.ToolBotCount);
        Assert.Contains(log, l => l.Contains("did not join within"));
    }

    [Theory]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("127.0.0.1")]
    public void Loopback_requesters_in_every_form_are_obeyed_and_a_mapped_foreign_address_is_not(string address)
    {
        Assert.True(DevToolsGate.Allows(Setup(), new IPEndPoint(IPAddress.Parse(address), 1)));
        var (h, _) = Make(Setup(), remote: new IPEndPoint(IPAddress.Parse(address), 40000));
        Settle(h, 2.5);
        h.Clients[0].SendDevCommand(DevCommandKind.SetFlag, (byte)DevToolsFlags.FreezeBots, true);
        Settle(h, 0.3);
        Assert.Equal(DevToolsFlags.FreezeBots, h.Server.World.DevFlags);
        Assert.False(DevToolsGate.Allows(Setup(), new IPEndPoint(IPAddress.Parse("::ffff:10.1.2.3"), 1)));
    }

    [Fact]
    public void Short_or_garbled_dev_packets_read_as_null_and_never_throw()
    {
        foreach (byte kind in new byte[] { 20, 21 })
        {
            byte[] good = Protocol.Write(kind == 20 ? new DevCommand(5, DevCommandKind.AddBot, 1, true) : new DevState(DevToolsFlags.None, 0, 0, 0, 0, DevNote.None, 0));
            for (int length = 1; length < good.Length; length++) Assert.Null(Protocol.Read(good[..length]));   // truncated at every length
            var random = new Random(kind);
            for (int i = 0; i < 300; i++)
            {
                var junk = new byte[random.Next(1, 14)];
                random.NextBytes(junk); junk[0] = kind;
                Protocol.Read(junk);   // null or a valid message, never an exception
            }
        }
        Assert.Null(Protocol.Read([20]));
        Assert.Null(Protocol.Read([21, 255, 255, 255, 255, 255, 255, 255]));
    }

    [Fact]
    public void A_garbled_dev_command_on_the_wire_changes_nothing_on_the_server()
    {
        var (h, _) = Make(Setup());
        Settle(h, 2.5);
        var raw = h.Net.Connect();
        raw.Send([20, 1, 2]);
        raw.Send([20, 9, 9, 9, 9, 99, 1, 1]);
        Settle(h, 0.3);
        Assert.Equal(DevToolsFlags.None, h.Server.World.DevFlags);
        Assert.Equal(1, h.Server.Players.Count);
    }

    [Fact]
    public void In_Musou_Mode_the_toggles_apply_and_bots_are_refused()
    {
        if (!ContentExtras.Compiled) return;   // a build without added content has no Musou Mode
        var launcher = new FakeLauncher();
        var config = new SimConfig { Extras = ContentExtras.Musou };
        var options = new ServerOptions { DevTools = Setup(launcher: launcher) };
        var h = new Harness(Link, seed: 5, clients: 1, config, options);
        var log = new List<string>(); h.Server.Log += log.Add;
        Settle(h, 2.5);
        var client = h.Clients[0];
        client.SendDevCommand(DevCommandKind.SetFlag, (byte)DevToolsFlags.Invincible, true);
        client.SendDevCommand(DevCommandKind.AddBot, DevSides.Chaos, true);
        Settle(h, 0.5);
        Assert.Equal(DevToolsFlags.Invincible, h.Server.World.DevFlags);
        Assert.Equal(DevNote.NotInThisMode, client.Dev!.Note);
        Assert.Empty(launcher.Started);
        Assert.Equal(0f, h.Server.World.DamageTakenScale(client.PlayerId));   // the added content's hook cannot undo it
    }

    // ---- the sim ----

    static readonly SimConfig MeleeConfig = new()
    {
        MeleeSteps = [new MeleeStep(1.0f, 0.2f, 0.4f, 0.6f, 0.5f, 0.7f, 10f, 5f, 3.5f), new MeleeStep(1.0f, 0.2f, 0.4f, 0.6f, 0.5f, 0.7f, 20f, 6f, 3.5f)],
        MeleeRange = 4f, MeleeConeDegrees = 60f, MeleeVerticalReach = 1.5f,
    };

    static (float Shield, float Health, int Hits) OneSwing(DevToolsFlags flags)
    {
        var world = new World(MeleeConfig, null, [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new Vector3(0, 2, 0), 0f)]) { DevFlags = flags };
        world.Spawn(1); world.Spawn(2);
        int hits = 0;
        for (int i = 0; i < 12; i++)
        {
            world.Step(id => id == 1 ? new PlayerInput(0, 0, 0, 0, i == 0 ? InputButtons.Melee : InputButtons.None) : new PlayerInput(0, 0, 0, 0, InputButtons.None));
            hits += world.LastHits.Count;
        }
        return (world.Players[2].Shield, world.Players[2].Health, hits);
    }

    [Fact]
    public void Invincible_holds_through_a_melee_blow_too_and_reactions_are_left_alone()
    {
        var normal = OneSwing(DevToolsFlags.None);
        Assert.True(normal.Hits >= 1 && normal.Shield < MeleeConfig.ShieldMax, "control: the blow hurts");
        var guarded = OneSwing(DevToolsFlags.Invincible);
        Assert.True(guarded.Hits >= 1, "the blow still lands (flinch, stagger and knockback are not damage and stay)");
        Assert.Equal((MeleeConfig.ShieldMax, MeleeConfig.HealthMax), (guarded.Shield, guarded.Health));
    }

    static readonly SimConfig Bolter = new()
    {
        Damage = 22f, ShotCooldown = 0.133f, ClipSize = 30, ReserveSize = 270, ReloadTime = 1.3f, MaxRange = 250f,
        FallOffStart = 45f, FallOffEnd = 70f, FallOffEndMultiplier = 0.8f,
        HeadMultiplier = 2.55f, BodyMultiplier = 1f, ShieldHeadMultiplier = 0.5f, ShieldBodyMultiplier = 0.505f, MaxRewindTicks = 8,
    };

    /// <summary>Player 1 shoots player 2 once; the victim's shield and health afterwards.</summary>
    static (float Shield, float Health, int Hits) OneShot(DevToolsFlags flags)
    {
        var world = new World(Bolter) { DevFlags = flags };
        world.Spawn(1); world.Spawn(2);
        PlayerInput Idle(int id) => new(0, 0, 0, 0, InputButtons.None);
        world.Step(Idle);
        var eye = world.Players[1].Position + new Vector3(0, 0, Bolter.EyeHeight);
        var aim = Vector3.Normalize(world.Players[2].Position + new Vector3(0, 0, 1.0f) - eye);
        float yaw = MathF.Atan2(-aim.X, aim.Y), pitch = MathF.Asin(aim.Z);
        world.Step(id => id == 1 ? new PlayerInput(0, 0, 0, yaw, InputButtons.Fire, pitch, world.Tick) : Idle(id));
        return (world.Players[2].Shield, world.Players[2].Health, world.LastHits.Count);
    }

    [Fact]
    public void Invincible_means_a_hit_that_lands_takes_nothing_and_off_it_does_take()
    {
        var normal = OneShot(DevToolsFlags.None);
        Assert.Equal(1, normal.Hits);
        Assert.True(normal.Shield < Bolter.ShieldMax, "the control shot must hurt");
        var protectedVictim = OneShot(DevToolsFlags.Invincible);
        Assert.Equal((Bolter.ShieldMax, Bolter.HealthMax), (protectedVictim.Shield, protectedVictim.Health));
        Assert.Equal(1f, new World(Bolter).DamageTakenScale(1));
        Assert.Equal(0f, new World(Bolter) { DevFlags = DevToolsFlags.Invincible }.DamageTakenScale(1));
    }

    static PlayerState Tactical(SimConfig c) => Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = 1 }, c);

    [Fact]
    public void Infinite_ammunition_refills_clips_reserves_heat_and_overheats_and_leaves_the_rest()
    {
        var c = JumpPackTests.Config;
        var spent = Tactical(c) with { Clip = 0, Reserve = 0, OtherClip = 1, OtherReserve = 2, Heat = 80f, Overheated = true, OtherHeat = 20f, OtherOverheated = true, Grenades = 0, Health = 12f, JumpPower = 3f };
        var full = Tactical(c);
        var topped = DevTools.TopUp(spent, c, DevToolsFlags.InfiniteAmmo);
        Assert.Equal((full.Clip, full.Reserve, full.OtherClip, full.OtherReserve), (topped.Clip, topped.Reserve, topped.OtherClip, topped.OtherReserve));
        Assert.Equal((0f, false, 0f, false), (topped.Heat, topped.Overheated, topped.OtherHeat, topped.OtherOverheated));
        Assert.True(topped.Grenades >= full.Grenades);
        Assert.Equal((12f, 3f), (topped.Health, topped.JumpPower));   // only the ammunition flag: health and jump power stay
        Assert.Equal(spent, DevTools.TopUp(spent, c, DevToolsFlags.None));
        Assert.Equal(spent, DevTools.TopUp(spent, c, DevToolsFlags.Invincible | DevToolsFlags.FreezeBots));
        var dead = spent with { Alive = false };
        Assert.Equal(dead, DevTools.TopUp(dead, c, DevTools.All));
    }

    [Fact]
    public void Infinite_jump_fuel_keeps_the_pack_full_and_only_the_pack()
    {
        var c = JumpPackTests.Config;
        var assault = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = 0 }, c) with { JumpPower = 5f, Clip = 1 };
        var topped = DevTools.TopUp(assault, c, DevToolsFlags.InfiniteJump);
        Assert.Equal(JumpPackTests.Pack.MaxPower, topped.JumpPower);
        Assert.Equal(1, topped.Clip);   // no ammunition flag
        Assert.Equal(5f, DevTools.TopUp(assault, c, DevToolsFlags.InfiniteAmmo).JumpPower);
        var noPack = Tactical(c) with { JumpPower = 0f };
        Assert.Equal(0f, DevTools.TopUp(noPack, c, DevToolsFlags.InfiniteJump).JumpPower);
    }

    [Fact]
    public void The_world_tops_every_living_player_up_after_a_tick_only_with_the_flags_and_the_server_does_so()
    {
        var c = JumpPackTests.Config;
        var world = new World(c);
        world.Spawn(1, 0); world.Spawn(2, 1);
        world.ApplyDevTools();   // no flags: nothing happens
        var before = world.Players[1];
        world.DevFlags = DevToolsFlags.InfiniteAmmo | DevToolsFlags.InfiniteJump;
        world.Step(id => new PlayerInput(0, 0, 0, 0, id == 2 ? InputButtons.Fire : InputButtons.None));
        Assert.True(world.Players[2].Clip < Tactical(c).Clip, "the shot spent a round inside the tick");   // the server's top-up runs after the step, not inside it
        world.ApplyDevTools();
        Assert.Equal(Tactical(c).Clip, world.Players[2].Clip);
        Assert.Equal(JumpPackTests.Pack.MaxPower, world.Players[1].JumpPower);
        Assert.Equal(before.Health, world.Players[1].Health);
    }

    [Fact]
    public void With_infinite_ammunition_a_server_run_never_empties_the_clip_and_with_the_flag_off_it_does()
    {
        static int ClipAfterFiring(bool infinite)
        {
            var (h, _) = Make(Setup(), clients: 1);
            Settle(h, 2.5);
            if (infinite) { h.Clients[0].SendDevCommand(DevCommandKind.SetFlag, (byte)DevToolsFlags.InfiniteAmmo, true); Settle(h, 0.3); }
            int lowest = int.MaxValue;
            h.Run(TimeSpan.FromSeconds(6), (_, t) => new PlayerInput(t, 0, 0, 0, InputButtons.Fire), () => lowest = Math.Min(lowest, h.Server.World.Players[h.Clients[0].PlayerId].Clip));
            return lowest;
        }
        Assert.True(ClipAfterFiring(infinite: false) < new SimConfig().ClipSize / 2, "control: firing for 6 s spends the clip");
        Assert.Equal(new SimConfig().ClipSize, ClipAfterFiring(infinite: true));
    }
}
