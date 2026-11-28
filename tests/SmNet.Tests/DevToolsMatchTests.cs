using System.Net;
using System.Numerics;
using SmNet;
using SmServer;
using SmSim;

namespace SmNet.Tests;

/// <summary>
/// dev-tools, second batch (owner 2026-11-17): the indefinite match, end, restart and skip, teleport, kill me and kill bots, instant respawn, the bots' live difficulty, and the time scale.
/// Each has its gate test (refused without the tools) and its effect test (in the sim, then through a real server). Synthetic classes, weapons and points only.
/// </summary>
public class DevToolsMatchTests
{
    static readonly LinkConditions Link = new(TimeSpan.FromMilliseconds(2), TimeSpan.Zero, 0);

    static readonly WeaponSpec Rifle = new("test_rifle", WeaponSlot.Ranged, TriggerKind.SemiAuto, 20f, 0.2f, 30, 90, 1f, 250f, 250f, 250f, 1f, 1f, 1f, 1f, 1f);
    static ClassLoadout Tac(string name, Team side) => new(name, 0, -1, SimConfig.PlaceholderMeleeSteps) { PrimaryChoices = [0], Side = side, Role = "tactical" };
    static readonly SimConfig Config = new()
    {
        Weapons = [Rifle],
        Classes = [Tac("sm - tactical", Team.SpaceMarines), Tac("csm - tactical", Team.Chaos)],
        HealthMax = 10f, ShieldMax = 0f, HealthRegenDelay = 1e6f, RespawnTime = 5f, MaxRewindTicks = 40,
    };
    static readonly Vector3 MarineRoom = new(-60, 0, 0), ChaosRoom = new(60, 0, 0);
    static readonly ControlPoint A = new("mkr_objective_1", new Vector3(0, 30, 0), 5f, 10f);
    static readonly ControlPoint B = new("mkr_objective_2", new Vector3(0, -30, 0), 5f, 10f);

    static World Arena(MatchRules rules) => new(Config)
    {
        Rules = rules, ControlPoints = [A, B],
        StartAreas = [new StartArea(Team.SpaceMarines, MarineRoom, new Vector3(10, 10, 2), 0f), new StartArea(Team.Chaos, ChaosRoom, new Vector3(10, 10, 2), MathF.PI)],
    };

    static World Playing(MatchRules rules, out int marine, out int chaos)
    {
        var w = Arena(rules);
        marine = 1; chaos = 2;
        w.Spawn(1, 0, team: Team.SpaceMarines); w.Spawn(2, 1, team: Team.Chaos);
        Run(w, 3);
        return w;
    }

    static void Run(World w, int ticks) { for (int i = 0; i < ticks; i++) w.Step(id => new PlayerInput(0, 0, 0, w.Players[id].Yaw, InputButtons.None)); }

    static readonly MatchRules Short = new() { TimeLimit = 2f, ScoreLimit = 1000, CountdownTime = 0f, RestartDelay = 1f };

    // ---- the wire ----

    [Fact]
    public void The_new_commands_and_state_fields_round_trip_and_refuse_nonsense()
    {
        var command = new DevCommand(9u, DevCommandKind.Teleport, (byte)DevTeleportKind.Player, true, 513);
        Assert.Equal(command, Protocol.Read(Protocol.Write(command)));
        Assert.Equal(Protocol.DevCommandBytes, Protocol.Write(command).Length);
        foreach (var kind in Enum.GetValues<DevCommandKind>())
            Assert.NotNull(Protocol.Read(Protocol.Write(new DevCommand(1, kind, 0, false, 7))));
        var state = new DevState(DevToolsFlags.Indefinite | DevToolsFlags.InstantRespawn, 1, 2, 3, 4, DevNote.TeleportBlocked, 6, 3, 450, 340, 80);
        Assert.Equal(state, Protocol.Read(Protocol.Write(state)));
        Assert.Equal(Protocol.DevStateBytes, Protocol.Write(state).Length);
        Assert.Equal(18, Protocol.DevStateBytes);   // protocol 95 (devtools-god-custommap): the self byte and the two text lengths (15 before)
        var unset = new DevState(DevToolsFlags.None, 0, 0, 0, 0, DevNote.None, 0);
        Assert.Equal((Protocol.DevNoValue, 1000, 0, 0), (unset.BotAccuracy, unset.BotDamage, unset.BotRange, unset.TimeScale));
        var bytes = Protocol.Write(state);
        var badScale = (byte[])bytes.Clone(); badScale[8] = 9;
        Assert.Null(Protocol.Read(badScale));
        var badKind = Protocol.Write(command); badKind[5] = 18;   // 14 is the cancel table's sync request (cancel-editor), 15 the map pick (protocol 85), 16 god mode and 17 the map-file pick (protocol 95)
        Assert.Null(Protocol.Read(badKind));
        for (int length = 1; length < bytes.Length; length++) Assert.Null(Protocol.Read(bytes[..length]));
    }

    // ---- the clock ----

    [Fact]
    public void The_scalable_clock_runs_at_its_fraction_stands_still_paused_and_never_jumps()
    {
        var real = new ManualClock();
        var clock = new ScalableClock(real);
        real.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(TimeSpan.FromSeconds(10), clock.Now);   // at scale 1 it is the inner clock
        clock.Scale = 0.25;
        Assert.Equal(TimeSpan.FromSeconds(10), clock.Now);   // no jump at the change
        real.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(TimeSpan.FromSeconds(11), clock.Now);
        clock.Scale = 0;
        real.Advance(TimeSpan.FromSeconds(100));
        Assert.Equal(TimeSpan.FromSeconds(11), clock.Now);   // paused
        clock.Scale = 1;
        real.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.FromSeconds(12), clock.Now);
        clock.Scale = double.NaN;
        Assert.Equal(1.0, clock.Scale);
    }

    // ---- the match, in the sim ----

    [Fact]
    public void An_indefinite_match_ends_neither_by_time_nor_by_score_and_the_next_match_is_normal()
    {
        // Control: the 2 s limit ends the match.
        var control = Playing(Short, out _, out _);
        Run(control, 70);
        Assert.True(control.State == MatchState.Ended, $"state {control.State} left {control.TicksLeft} tick {control.Tick}");

        var w = Playing(Short with { ScoreLimit = 1 }, out _, out _);
        w.DevFlags |= DevToolsFlags.Indefinite;
        Run(w, 120);   // far past the 2 s limit
        Assert.Equal(MatchState.Playing, w.State);
        Assert.True(w.DevEndMatch());   // an explicit end still works
        Assert.Equal(MatchState.Ended, w.State);
        Run(w, 60);   // the 1 s end screen runs out and the next match starts
        Assert.True(w.State is MatchState.Playing or MatchState.Countdown or MatchState.Warmup);
        Assert.Equal(DevToolsFlags.None, w.DevFlags & DevToolsFlags.Indefinite);   // the next match is not indefinite
        Run(w, 45);
        Assert.Equal(MatchState.Ended, w.State);   // and ends by its clock again
    }

    [Fact]
    public void An_indefinite_match_also_ignores_the_score_limit()
    {
        var w = Playing(Short with { TimeLimit = 0f, ScoreLimit = 1 }, out _, out _);
        w.DevFlags |= DevToolsFlags.Indefinite;
        w.DevKill(2); Run(w, 3);   // a death scores 1 for the other side: the limit is reached
        Assert.True(w.ScoreOf(Team.SpaceMarines) >= 1);
        Assert.Equal(MatchState.Playing, w.State);
        var control = Playing(Short with { TimeLimit = 0f, ScoreLimit = 1 }, out _, out _);
        control.DevKill(2); Run(control, 3);
        Assert.Equal(MatchState.Ended, control.State);
    }

    [Fact]
    public void End_restart_and_skip_use_the_normal_match_flow()
    {
        var w = Playing(Short with { TimeLimit = 0f }, out _, out _);
        w.DevKill(2); Run(w, 3);
        int before = w.ScoreOf(Team.SpaceMarines);
        Assert.True(before > 0);
        Assert.True(w.DevEndMatch());
        Assert.Equal((MatchState.Ended, Team.SpaceMarines), (w.State, w.Winner));
        Assert.False(w.DevEndMatch());   // not twice

        var skip = Playing(Short with { TimeLimit = 0f, RestartDelay = 15f }, out _, out _);
        Assert.True(skip.DevSkipMatch(MatchFlowWire.RotationRejoinTicks));
        Assert.Equal(MatchState.Ended, skip.State);
        Assert.True(skip.TicksLeft <= MatchFlowWire.RotationRejoinTicks && skip.TicksLeft > 0);   // the end screen, hurried

        var restart = Playing(Short with { TimeLimit = 0f }, out _, out _);
        restart.DevKill(2); Run(restart, 3);
        Assert.True(restart.ScoreOf(Team.SpaceMarines) > 0);
        restart.DevFlags |= DevToolsFlags.Indefinite;
        Assert.True(restart.DevRestartMatch());
        Assert.Equal((0, 0), (restart.ScoreOf(Team.SpaceMarines), restart.ScoreOf(Team.Chaos)));
        Assert.True(restart.State is MatchState.Playing or MatchState.Countdown);
        Assert.Equal(DevToolsFlags.None, restart.DevFlags & DevToolsFlags.Indefinite);
        Assert.All(restart.Players.Values, p => Assert.True(p.Alive));

        Assert.False(new World(Config).DevEndMatch());   // no rules, no match
        Assert.False(new World(Config).DevRestartMatch());
    }

    // ---- teleport ----

    [Fact]
    public void Teleport_goes_to_the_spawn_room_a_point_or_a_player_on_free_spots_only()
    {
        var w = Playing(Short with { TimeLimit = 0f }, out int marine, out int chaos);
        Assert.True(w.DevTeleport(marine, DevTeleportKind.SpawnRoom, 1, out _));
        var inChaosRoom = w.Players[marine].Position;
        Assert.True(MathF.Abs(inChaosRoom.X - ChaosRoom.X) <= 5.01f && MathF.Abs(inChaosRoom.Y - ChaosRoom.Y) <= 5.01f, $"{inChaosRoom} is outside the Chaos room");
        Assert.True(w.DevTeleport(marine, DevTeleportKind.ControlPoint, 1, out _));
        Assert.True(Vector2.Distance(new Vector2(w.Players[marine].Position.X, w.Players[marine].Position.Y), new Vector2(B.Center.X, B.Center.Y)) <= B.Radius);
        Assert.Equal(Vector3.Zero, w.Players[marine].Velocity);
        Assert.True(w.DevTeleport(marine, DevTeleportKind.Player, chaos, out _));
        float apart = Vector3.Distance(w.Players[marine].Position, w.Players[chaos].Position);
        Assert.InRange(apart, 0.9f, 8f);   // next to it, not inside it
        // Refusals move nothing.
        var at = w.Players[marine].Position;
        Assert.False(w.DevTeleport(marine, DevTeleportKind.ControlPoint, 9, out string why)); Assert.NotEmpty(why);
        Assert.False(w.DevTeleport(marine, DevTeleportKind.Player, marine, out _));
        Assert.False(w.DevTeleport(marine, DevTeleportKind.Player, 77, out _));
        Assert.False(w.DevTeleport(77, DevTeleportKind.SpawnRoom, 0, out _));
        Assert.Equal(at, w.Players[marine].Position);
    }

    // ---- kill and respawn ----

    [Fact]
    public void Kill_me_is_the_deploy_suicide_and_instant_respawn_ends_every_death_timer()
    {
        var w = Playing(Short with { TimeLimit = 0f }, out int marine, out _);
        Assert.True(w.DevKill(marine));
        Run(w, 1);
        Assert.Contains(w.LastKills, k => k.Victim == marine);
        Run(w, 2);
        Assert.True(w.Players[marine].Alive, "a suicide respawns at once, as the Deploy does");
        Assert.False(w.DevKill(99));

        // A real death carries the 5 s timer; instant respawn ends it.
        static World WithDeadMarine(DevToolsFlags flags)
        {
            var world = Playing(Short with { TimeLimit = 0f }, out int m, out int c);
            world.DevFlags = flags;
            var eye = world.Players[c].Position + new Vector3(0, 0, Config.EyeHeight);
            var aim = Vector3.Normalize(world.Players[m].Position + new Vector3(0, 0, 1f) - eye);
            float yaw = MathF.Atan2(-aim.X, aim.Y), pitch = MathF.Asin(aim.Z);
            world.Step(id => id == c ? new PlayerInput(0, 0, 0, yaw, InputButtons.Fire, pitch, world.Tick) : new PlayerInput(0, 0, 0, 0, InputButtons.None));
            world.Step(id => new PlayerInput(0, 0, 0, 0, InputButtons.None));
            return world;
        }
        // Friendly fire is off between sides? The sides are enemies; the shot kills the 10-health marine.
        var control = WithDeadMarine(DevToolsFlags.None);
        Assert.False(control.Players[1].Alive);
        control.ApplyDevTools();
        Run(control, 20);
        Assert.False(control.Players[1].Alive, "without the flag the timer runs");
        var instant = WithDeadMarine(DevToolsFlags.InstantRespawn);
        Assert.False(instant.Players[1].Alive);
        instant.ApplyDevTools();
        Run(instant, 2);
        Assert.True(instant.Players[1].Alive);
    }

    // ---- the server: gate and effects ----

    static (Harness H, List<string> Log, ScalableClock? Clock) Server(DevToolsSetup? setup, MatchRules? rules = null, int clients = 1, bool scalable = false)
    {
        ScalableClock? clock = null;
        var options = new ServerOptions { DevTools = setup, Match = rules };
        var h = new Harness(Link, seed: 5, clients, serverOptions: options, serverClock: scalable ? real => clock = new ScalableClock(real) : null);
        var log = new List<string>(); h.Server.Log += log.Add;
        return (h, log, clock);
    }

    static DevToolsSetup Setup(ScalableClock? clock = null) => new(IPAddress.Loopback, null, clock);

    static void Settle(Harness h, double seconds = 1.5) => h.Run(TimeSpan.FromSeconds(seconds));

    static readonly DevCommandKind[] NewKinds =
    [
        DevCommandKind.EndMatch, DevCommandKind.RestartMatch, DevCommandKind.SkipMatch, DevCommandKind.Teleport, DevCommandKind.KillMe, DevCommandKind.KillBots,
        DevCommandKind.SetTimeScale, DevCommandKind.SetBotAccuracy, DevCommandKind.SetBotDamage, DevCommandKind.SetBotRange,
    ];

    [Fact]
    public void Without_the_tools_none_of_the_new_requests_does_anything()
    {
        var (h, log, _) = Server(null, Short with { TimeLimit = 0f });
        var peer2 = h.AddClient(nonce: 12, options: new ClientOptions { RequestedSide = Team.Chaos });
        Settle(h, 2.5);
        var state = h.Server.World.State;
        var scores = (h.Server.World.ScoreOf(Team.SpaceMarines), h.Server.World.ScoreOf(Team.Chaos));
        var at = h.Server.World.Players[h.Clients[0].PlayerId].Position;
        foreach (var kind in NewKinds)
        {
            h.Clients[0].SendDevCommand(kind, 1, true, 500);
            Settle(h, 0.15);
        }
        h.Clients[0].SendDevCommand(DevCommandKind.SetFlag, (byte)DevToolsFlags.Indefinite, true);
        h.Clients[0].SendDevCommand(DevCommandKind.SetFlag, (byte)DevToolsFlags.InstantRespawn, true);
        Settle(h, 0.3);
        Assert.Equal(DevToolsFlags.None, h.Server.World.DevFlags);
        Assert.Equal(state, h.Server.World.State);
        Assert.Equal(scores, (h.Server.World.ScoreOf(Team.SpaceMarines), h.Server.World.ScoreOf(Team.Chaos)));
        Assert.Equal(at, h.Server.World.Players[h.Clients[0].PlayerId].Position);
        Assert.True(h.Server.World.Players[h.Clients[0].PlayerId].Alive);
        Assert.Null(h.Clients[0].Dev);
        Assert.Single(log, l => l.StartsWith("dev tools request ignored"));
        Assert.NotNull(peer2);
    }

    [Fact]
    public void The_server_ends_restarts_and_skips_the_match_kills_and_teleports_and_logs_each()
    {
        var (h, log, _) = Server(Setup(), Short with { TimeLimit = 0f, RestartDelay = 15f });
        var other = h.AddClient(nonce: 12, options: new ClientOptions { RequestedSide = Team.Chaos });
        Settle(h, 3);
        var me = h.Clients[0];
        Assert.True(me.DevActive);
        Assert.Equal(MatchState.Playing, h.Server.World.State);

        me.SendDevCommand(DevCommandKind.SetFlag, (byte)DevToolsFlags.Indefinite, true);
        me.SendDevCommand(DevCommandKind.SetFlag, (byte)DevToolsFlags.InstantRespawn, true);
        Settle(h, 0.3);
        Assert.Contains(log, l => l.StartsWith("dev tools: indefinite match ON"));
        Assert.Contains(log, l => l.StartsWith("dev tools: instant respawn ON"));
        Assert.Equal(DevToolsFlags.Indefinite | DevToolsFlags.InstantRespawn, me.Dev!.Flags);

        me.SendDevCommand(DevCommandKind.Teleport, (byte)DevTeleportKind.Player, true, other.PlayerId);
        Settle(h, 0.3);
        Assert.Contains(log, l => l.StartsWith($"dev tools: teleport player {me.PlayerId} to player {other.PlayerId}:"));
        me.SendDevCommand(DevCommandKind.Teleport, (byte)DevTeleportKind.SpawnRoom, true, 1);   // this synthetic map has no spawn room
        Settle(h, 0.3);
        Assert.Contains(log, l => l.Contains("refused: this map has no spawn room"));
        me.SendDevCommand(DevCommandKind.Teleport, (byte)DevTeleportKind.ControlPoint, true, 3);   // the arena has none
        Settle(h, 0.3);
        Assert.Contains(log, l => l.Contains("refused: no such control point"));
        Assert.Equal(DevNote.TeleportBlocked, me.Dev!.Note);

        me.SendDevCommand(DevCommandKind.KillMe, 0, true);
        Settle(h, 0.3);
        Assert.Contains(log, l => l == $"dev tools: kill me (player {me.PlayerId})");
        me.SendDevCommand(DevCommandKind.KillBots, 2, true);   // the other client is no bot: nothing to kill
        Settle(h, 0.3);
        Assert.Contains(log, l => l.Contains("kill bots (both sides): 0 killed"));

        me.SendDevCommand(DevCommandKind.RestartMatch, 0, true);
        Settle(h, 0.3);
        Assert.Contains(log, l => l.StartsWith("dev tools: restart match"));
        Assert.Equal(0, h.Server.World.DevFlags.HasFlag(DevToolsFlags.Indefinite) ? 1 : 0);   // a restart is a new match
        me.SendDevCommand(DevCommandKind.EndMatch, 0, true);
        Settle(h, 0.3);
        Assert.Equal(MatchState.Ended, h.Server.World.State);
        Assert.Contains(log, l => l.StartsWith("dev tools: end match now"));
        me.SendDevCommand(DevCommandKind.EndMatch, 0, true);   // already ended
        Settle(h, 0.3);
        Assert.Equal(DevNote.NotPlaying, me.Dev!.Note);
        me.SendDevCommand(DevCommandKind.SkipMatch, 0, true);
        Settle(h, 2.5);
        Assert.Contains(log, l => l.StartsWith("dev tools: skip to the next match"));
        Assert.NotEqual(MatchState.Ended, h.Server.World.State);   // the hurried end screen has run out
        Assert.NotNull(other);
    }

    [Fact]
    public void Bot_tuning_is_stored_validated_and_sent_to_bots_and_the_damage_reaches_the_world()
    {
        var (h, log, _) = Server(Setup(), Short with { TimeLimit = 0f }, clients: 0);
        var human = h.AddClient(nonce: 11);
        var bot = h.AddClient(nonce: 12, options: new ClientOptions { Bot = true });
        Settle(h, 2.5);
        Assert.Equal((Protocol.DevNoValue, 1000, 0, 0), (human.Dev!.BotAccuracy, human.Dev.BotDamage, human.Dev.BotRange, human.Dev.TimeScale));
        human.SendDevCommand(DevCommandKind.SetBotAccuracy, 0, true, 250);
        human.SendDevCommand(DevCommandKind.SetBotDamage, 0, true, 340);
        human.SendDevCommand(DevCommandKind.SetBotRange, 0, true, 80);
        Settle(h, 0.5);
        foreach (var c in new[] { human, bot }) Assert.Equal((250, 340, 80), (c.Dev!.BotAccuracy, c.Dev.BotDamage, c.Dev.BotRange));   // the bot is told too
        Assert.Equal(0.34f, h.Server.World.DamageScaleOf(bot.PlayerId), 3);
        Assert.Equal(1f, h.Server.World.DamageScaleOf(human.PlayerId));   // humans deal full damage
        Assert.Contains(log, l => l.StartsWith("dev tools: bot accuracy 0.25"));
        Assert.Contains(log, l => l.StartsWith("dev tools: bot damage x 0.34"));
        Assert.Contains(log, l => l.StartsWith("dev tools: bot range 80 m"));
        var late = h.AddClient(nonce: 13, options: new ClientOptions { Bot = true });
        Settle(h, 2.5);
        Assert.Equal(0.34f, h.Server.World.DamageScaleOf(late.PlayerId), 3);   // a bot that joins later gets it
        human.SendDevCommand(DevCommandKind.SetBotAccuracy, 0, true, 5000);   // out of range
        human.SendDevCommand(DevCommandKind.SetBotRange, 0, true, 2);
        Settle(h, 0.3);
        Assert.Equal((250, 80), (human.Dev!.BotAccuracy, human.Dev.BotRange));
        Assert.Equal(DevNote.BadValue, human.Dev.Note);
        human.SendDevCommand(DevCommandKind.SetBotAccuracy, 0, true, Protocol.DevNoValue);
        human.SendDevCommand(DevCommandKind.SetBotRange, 0, true, 0);
        Settle(h, 0.3);
        Assert.Equal((Protocol.DevNoValue, 0), (human.Dev!.BotAccuracy, human.Dev.BotRange));
    }

    [Fact]
    public void Kill_bots_kills_only_bots_of_the_chosen_side()
    {
        var (h, log, _) = Server(Setup(), Short with { TimeLimit = 0f, HonorSideRequests = true }, clients: 0);
        var human = h.AddClient(nonce: 11, options: new ClientOptions { RequestedSide = Team.SpaceMarines });
        var chaosBot = h.AddClient(nonce: 12, options: new ClientOptions { Bot = true, RequestedSide = Team.Chaos });
        var marineBot = h.AddClient(nonce: 13, options: new ClientOptions { Bot = true, RequestedSide = Team.SpaceMarines });
        Settle(h, 3);
        human.SendDevCommand(DevCommandKind.KillBots, 1, true);
        Settle(h, 0.25);
        Assert.Contains(log, l => l.Contains("kill bots (chaos): 1 killed"));
        Assert.True(h.Server.World.LastKills.Count + h.Server.World.RecentKills.Count >= 1 || !h.Server.World.Players[chaosBot.PlayerId].Alive || h.Server.World.Scores[chaosBot.PlayerId].Deaths >= 1);
        Assert.True(h.Server.World.Players[marineBot.PlayerId].Alive);
        Assert.True(h.Server.World.Players[human.PlayerId].Alive);
        Assert.True(h.Server.World.Scores[chaosBot.PlayerId].Deaths >= 1);
        Assert.Equal(0, h.Server.World.Scores[marineBot.PlayerId].Deaths);
    }

    [Fact]
    public void A_server_built_with_its_scalable_clock_ticks_at_the_scale_and_not_at_all_when_paused()
    {
        // The server, its setup and the test's clock are one object, as in Program.cs.
        var manual = new ManualClock();
        var scalable = new ScalableClock(manual);
        var net = new LoopbackNetwork(manual, Link, 5);
        var server = new GameServer(new SimConfig(), Harness.Fingerprint, net.Server, scalable, new ServerOptions { DevTools = new DevToolsSetup(IPAddress.Loopback, null, scalable) }, null);
        var client = new GameClient(new SimConfig(), Harness.Fingerprint, net.Connect(), manual, null, nonce: 7);
        void Run(int ms) { for (int i = 0; i < ms; i++) { manual.Advance(TimeSpan.FromMilliseconds(1)); server.Update(); client.Update(t => new PlayerInput(t, 0, 0, 0, InputButtons.None)); } }
        Run(2500);
        Assert.Equal(ClientStatus.Connected, client.Status);
        uint t0 = server.World.Tick; Run(1000); uint normal = server.World.Tick - t0;
        Assert.InRange(normal, 28u, 32u);
        client.SendDevCommand(DevCommandKind.SetTimeScale, 2, true);   // 1/4
        Run(300);
        Assert.Equal(2, client.Dev!.TimeScale);
        uint t1 = server.World.Tick; Run(2000); uint quarter = server.World.Tick - t1;
        Assert.InRange(quarter, 13u, 17u);   // 2 s at a quarter: about 15 ticks
        client.SendDevCommand(DevCommandKind.SetTimeScale, 4, true);   // paused
        Run(300);
        Assert.Equal(4, client.Dev!.TimeScale);   // the state reaches the client although no tick runs
        uint t2 = server.World.Tick; Run(2000);
        Assert.Equal(t2, server.World.Tick);
        client.SendDevCommand(DevCommandKind.SetTimeScale, 0, true);
        Run(1000);
        Assert.Equal(0, client.Dev!.TimeScale);
        Assert.True(server.World.Tick > t2 + 20);
    }

    [Fact]
    public void A_paused_server_can_still_be_unpaused_after_many_requests_and_keeps_telling_clients_it_lives()
    {
        var manual = new ManualClock();
        var scalable = new ScalableClock(manual);
        var net = new LoopbackNetwork(manual, Link, 5);
        var server = new GameServer(new SimConfig(), Harness.Fingerprint, net.Server, scalable, new ServerOptions { DevTools = new DevToolsSetup(IPAddress.Loopback, null, scalable) }, null);
        var clientClock = new ScalableClock(manual);   // a client scales its own clock with the server's, as NetworkView does
        var client = new GameClient(new SimConfig(), Harness.Fingerprint, net.Connect(), clientClock, null, nonce: 7) { UnscaledClock = manual };
        void Run(int ms)
        {
            for (int i = 0; i < ms; i++)
            {
                manual.Advance(TimeSpan.FromMilliseconds(1)); server.Update(); client.Update(t => new PlayerInput(t, 0, 0, 0, InputButtons.None));
                clientClock.Scale = client.Dev is { } d ? DevTools.TimeScales[d.TimeScale] : 1.0;
            }
        }
        Run(2500);
        client.SendDevCommand(DevCommandKind.SetTimeScale, 4, true);   // paused
        Run(200);
        Assert.Equal(4, client.Dev!.TimeScale);
        // More requests than the bucket holds, while paused: the limit counts real time, so it refills.
        for (int i = 0; i < 12; i++) { client.SendDevCommand(DevCommandKind.SetFlag, (byte)DevToolsFlags.Invincible, i % 2 == 0); Run(5); }
        Run(2000);   // two real seconds: the bucket refills (6 a second) although the server's own clock stands still
        client.SendDevCommand(DevCommandKind.SetFlag, (byte)DevToolsFlags.FreezeBots, true);
        Run(200);
        Assert.True(server.World.DevFlags.HasFlag(DevToolsFlags.FreezeBots), "a flag request after the refill is obeyed while paused");
        // Changing the time scale is never limited: a burst of them, the last one wins.
        for (int i = 0; i < 12; i++) { client.SendDevCommand(DevCommandKind.SetTimeScale, (byte)(i % 2 == 0 ? 4 : 0), true); Run(1); }
        client.SendDevCommand(DevCommandKind.SetTimeScale, 0, true);
        Run(200);
        Assert.Equal(0, client.Dev!.TimeScale);
        Assert.NotEqual(DevNote.TooFast, client.Dev.Note);
        // Paused again: the state still arrives every real second (the keep-alive), so the client's copy stays fresh on real time.
        client.SendDevCommand(DevCommandKind.SetTimeScale, 4, true);
        Run(200);
        uint tick = server.World.Tick;
        Run(6000);
        Assert.Equal(tick, server.World.Tick);
        Assert.NotNull(client.Dev);   // 6 real seconds later, still fresh
        client.SendDevCommand(DevCommandKind.SetTimeScale, 0, true);   // unpause: accepted
        Run(1000);
        Assert.Equal(0, client.Dev!.TimeScale);
        Assert.True(server.World.Tick > tick + 20);
    }

    [Fact]
    public void The_state_expires_on_real_time_when_the_server_stops_sending_it()
    {
        var manual = new ManualClock();
        var scalable = new ScalableClock(manual);
        var net = new LoopbackNetwork(manual, Link, 5);
        var server = new GameServer(new SimConfig(), Harness.Fingerprint, net.Server, scalable, new ServerOptions { DevTools = new DevToolsSetup(IPAddress.Loopback, null, scalable) }, null);
        var client = new GameClient(new SimConfig(), Harness.Fingerprint, net.Connect(), scalable, null, nonce: 7) { UnscaledClock = manual };
        void Run(int ms, bool serverUp = true) { for (int i = 0; i < ms; i++) { manual.Advance(TimeSpan.FromMilliseconds(1)); if (serverUp) server.Update(); client.Update(t => new PlayerInput(t, 0, 0, 0, InputButtons.None)); } }
        Run(2500);
        client.SendDevCommand(DevCommandKind.SetTimeScale, 4, true);
        Run(200);
        scalable.Scale = 0;   // the client's own clock stands still too, as the client does on a paused server
        Assert.NotNull(client.Dev);
        Run(5000, serverUp: false);   // the server is gone
        Assert.Null(client.Dev);   // expired on real time although the client's scaled clock never moved
    }

    [Fact]
    public void A_bot_client_cannot_command_the_tools()
    {
        var (h, log, _) = Server(Setup(), Short with { TimeLimit = 0f }, clients: 0);
        var human = h.AddClient(nonce: 11);
        var bot = h.AddClient(nonce: 12, options: new ClientOptions { Bot = true });
        Settle(h, 2.5);
        bot.SendDevCommand(DevCommandKind.SetFlag, (byte)DevToolsFlags.Invincible, true);
        bot.SendDevCommand(DevCommandKind.SetTimeScale, 2, true);
        bot.SendDevCommand(DevCommandKind.EndMatch, 0, true);
        Settle(h, 0.5);
        Assert.Equal(DevToolsFlags.None, h.Server.World.DevFlags);
        Assert.Equal(0, human.Dev!.TimeScale);
        Assert.DoesNotContain(log, l => l.StartsWith("dev tools: invincible") || l.StartsWith("dev tools: time scale") || l.StartsWith("dev tools: end match"));
        human.SendDevCommand(DevCommandKind.SetFlag, (byte)DevToolsFlags.Invincible, true);   // a human still can
        Settle(h, 0.3);
        Assert.Equal(DevToolsFlags.Invincible, h.Server.World.DevFlags);
    }

    [Fact]
    public void A_restart_that_clears_the_indefinite_toggle_says_so()
    {
        var (h, log, _) = Server(Setup(), Short with { TimeLimit = 0f }, clients: 1);
        h.AddClient(nonce: 12, options: new ClientOptions { RequestedSide = Team.Chaos });
        Settle(h, 3);
        var me = h.Clients[0];
        me.SendDevCommand(DevCommandKind.SetFlag, (byte)DevToolsFlags.Indefinite, true);
        Settle(h, 0.3);
        me.SendDevCommand(DevCommandKind.RestartMatch, 0, true);
        Settle(h, 0.5);
        Assert.Contains(log, l => l.StartsWith("dev tools: indefinite match OFF (the match was restarted"));
        Assert.Equal(DevToolsFlags.None, me.Dev!.Flags & DevToolsFlags.Indefinite);
    }
}
