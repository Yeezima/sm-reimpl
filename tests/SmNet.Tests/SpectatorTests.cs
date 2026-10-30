using System.Numerics;
using SmNet;
using SmServer;
using SmSim;

namespace SmNet.Tests;

/// <summary>
/// Observer tooling (protocol 47; ours, docs/changes-from-original.md): a client that joins as a spectator gets an id and everyone's snapshots but no player: it never spawns, is in no
/// team or score, cannot be targeted or damaged (it is not in the world), takes no player slot, is listed as a spectator in the Scoreboard and does not stop a match from
/// balancing. Synthetic classes and weapons only (no game data).
/// </summary>
public class SpectatorTests
{
    static readonly LinkConditions Link = new(TimeSpan.FromMilliseconds(10), TimeSpan.Zero, 0);
    const int SmTac = 0, CsmTac = 1;

    static readonly WeaponSpec Rifle = new("test_rifle", WeaponSlot.Ranged, TriggerKind.SemiAuto, 20f, 0.2f, 30, 90, 1f, 250f, 250f, 250f, 1f, 1f, 1f, 1f, 1f);
    static readonly WeaponSpec Sidearm = new("test_sidearm", WeaponSlot.Pistol, TriggerKind.SemiAuto, 10f, 0.2f, 10, 40, 1f, 250f, 250f, 250f, 1f, 1f, 1f, 1f, 1f);
    static ClassLoadout Tac(string name, Team side) => new(name, 0, 1, SimConfig.PlaceholderMeleeSteps)
    { PrimaryChoices = [0], PistolChoices = [1], EquipmentChoices = [0], Side = side, Role = "tactical" };
    static readonly SimConfig Config = new()
    {
        Weapons = [Rifle, Sidearm],
        Thrown = [GrenadeTests.Frag],
        Classes = [Tac("sm - tactical", Team.SpaceMarines), Tac("csm - tactical", Team.Chaos)],
        HealthMax = 1000f, ShieldMax = 0f, HealthRegenDelay = 1e6f, MaxRewindTicks = 40,
    };
    static readonly MatchRules Rules = new() { TimeLimit = 0f, CountdownTime = 0.5f, RestartDelay = 0.2f };

    static ClientOptions Observer => new() { Observer = true };

    static Harness Match(ServerOptions? options = null) =>
        new(Link, seed: 5, clients: 0, Config, (options ?? new ServerOptions()) with { Match = Rules });

    [Fact]
    public void A_spectator_gets_an_id_but_no_player_team_or_score()
    {
        var h = Match();
        var player = h.AddClient(nonce: 100, options: new ClientOptions { RequestedClass = SmTac });
        var spectator = h.AddClient(nonce: 101, options: Observer);
        h.Run(TimeSpan.FromSeconds(1));
        Assert.Equal(ClientStatus.Connected, spectator.Status);
        Assert.True(spectator.Observer);
        Assert.NotEqual(player.PlayerId, spectator.PlayerId);
        Assert.Equal([player.PlayerId], h.Server.Players);
        Assert.Equal([spectator.PlayerId], h.Server.Spectators);
        Assert.Equal([player.PlayerId], h.Server.World.Players.Keys);        // never spawned
        Assert.False(h.Server.World.Scores.ContainsKey(spectator.PlayerId));
        Assert.Single(h.Server.World.Players);
        Assert.Equal(default, spectator.Predicted);                          // no prediction either
    }

    [Fact]
    public void A_spectator_receives_every_players_snapshots_and_no_own_record()
    {
        var h = Match();
        var a = h.AddClient(nonce: 100, options: new ClientOptions { RequestedClass = SmTac });
        var b = h.AddClient(nonce: 101, options: new ClientOptions { RequestedClass = CsmTac });
        var spectator = h.AddClient(nonce: 102, options: Observer);
        h.Run(TimeSpan.FromSeconds(2));
        var (tick, seen) = spectator.RemotePlayers();
        Assert.True(tick > 0);
        Assert.Equal([a.PlayerId, b.PlayerId], seen.Select(p => p.PlayerId).OrderBy(i => i));   // the full map, both sides: no team visibility
        Assert.DoesNotContain(spectator.PlayerId, seen.Select(p => p.PlayerId));
        Assert.True(spectator.LatestSnapshotTick > 0);
        Assert.Equal(ClientStatus.Connected, spectator.Status);                // its acknowledgements keep it connected past the peer timeout
    }

    [Fact]
    public void A_spectator_stays_connected_for_longer_than_the_peer_timeout_and_the_idle_kick()
    {
        var h = Match(new ServerOptions { IdleKick = TimeSpan.FromSeconds(1), LimitLoopback = true });
        var spectator = h.AddClient(nonce: 100, options: Observer);
        h.AddClient(nonce: 101, options: new ClientOptions { RequestedClass = SmTac });
        h.Run(TimeSpan.FromSeconds(12));
        Assert.Equal(ClientStatus.Connected, spectator.Status);
        Assert.Equal([spectator.PlayerId], h.Server.Spectators);
    }

    [Fact]
    public void A_spectator_takes_no_player_slot_and_has_its_own_limit()
    {
        var h = Match(new ServerOptions { MaxPlayers = 1, MaxSpectators = 2 });
        var player = h.AddClient(nonce: 100, options: new ClientOptions { RequestedClass = SmTac });
        var s1 = h.AddClient(nonce: 101, options: Observer);
        var s2 = h.AddClient(nonce: 102, options: Observer);
        var s3 = h.AddClient(nonce: 103, options: Observer);
        var second = h.AddClient(nonce: 104, options: new ClientOptions { RequestedClass = CsmTac });
        h.Run(TimeSpan.FromSeconds(1.5));
        Assert.Equal(ClientStatus.Connected, player.Status);
        Assert.Equal(ClientStatus.Connected, s1.Status);
        Assert.Equal(ClientStatus.Connected, s2.Status);
        Assert.Equal(ClientStatus.Rejected, s3.Status);                      // the spectators' limit (2)
        Assert.Equal(RejectReason.ServerFull, s3.RejectedBecause);
        Assert.Equal(ClientStatus.Rejected, second.Status);                  // the players' limit (1): the spectators did not use it up, and did not free it
        Assert.Equal(RejectReason.ServerFull, second.RejectedBecause);
        Assert.Equal(2, h.Server.Spectators.Count);
    }

    [Fact]
    public void A_server_with_no_spectator_slots_refuses_them()
    {
        var h = Match(new ServerOptions { MaxSpectators = 0 });
        var spectator = h.AddClient(nonce: 100, options: Observer);
        h.Run(TimeSpan.FromSeconds(1));
        Assert.Equal(ClientStatus.Rejected, spectator.Status);
        Assert.Empty(h.Server.Spectators);
    }

    [Fact]
    public void Spectators_do_not_change_the_balance_or_the_scores_and_are_listed_as_spectators()
    {
        var h = Match();
        var a = h.AddClient(nonce: 100, options: new ClientOptions { RequestedClass = SmTac });
        var s = h.AddClient(nonce: 101, options: Observer);
        var b = h.AddClient(nonce: 102, options: new ClientOptions { RequestedClass = SmTac });   // the smaller side is Chaos: balance moves it there
        h.Run(TimeSpan.FromSeconds(3));
        Assert.Equal(Team.SpaceMarines, h.Server.World.Players[a.PlayerId].Team);
        Assert.Equal(Team.Chaos, h.Server.World.Players[b.PlayerId].Team);
        var board = a.Scoreboard;
        Assert.NotNull(board);
        Assert.Equal([a.PlayerId, b.PlayerId], board!.Players.Select(r => r.PlayerId).OrderBy(i => i));
        var listed = Assert.Single(board.Spectators!);
        Assert.Equal(s.PlayerId, listed.PlayerId);
        Assert.Equal($"Spectator {s.PlayerId}", listed.Name);
        Assert.Equal(0, h.Server.World.ScoreOf(Team.SpaceMarines) + h.Server.World.ScoreOf(Team.Chaos));
        // The spectator sees the same board.
        Assert.Equal(board.Spectators, s.Scoreboard!.Spectators);
    }

    [Fact]
    public void Nothing_a_spectator_sends_acts_on_the_world_or_piles_up()
    {
        var h = Match();
        var player = h.AddClient(nonce: 100, options: new ClientOptions { RequestedClass = SmTac });
        var spectator = h.AddClient(nonce: 101, options: Observer);
        h.Run(TimeSpan.FromSeconds(1));
        var before = h.Server.World.Players[player.PlayerId];
        // A hostile spectator that sends fire inputs under its own session (its own id) and, for good measure, under the player's id.
        var raw = h.Net.Connect();
        raw.Send(Protocol.Write(new Hello(Protocol.Version, Harness.Fingerprint, h.Config.Hash(), 555)));
        Challenge? challenge = null;
        for (int i = 0; i < 200 && challenge is null; i++) { h.Step(); if (raw.TryReceive(out var p) && Protocol.Read(p) is Challenge c) challenge = c; }
        Assert.NotNull(challenge);
        raw.Send(Protocol.Write(new Hello(Protocol.Version, Harness.Fingerprint, h.Config.Hash(), 555, 0, challenge!.Cookie, 0, Protocol.ServerDefaultClass,
            Protocol.DefaultChoice, Protocol.DefaultChoice, Protocol.DefaultChoice, Protocol.DefaultChoice, 0, Protocol.DefaultChoice, Protocol.DefaultChoice,
            default, default, Races.OrkLookAutomatic, Hello.SpectatorFlag, Protocol.DefaultChoice)));
        Welcome? welcome = null;
        for (int i = 0; i < 200 && welcome is null; i++) { h.Step(); if (raw.TryReceive(out var p) && Protocol.Read(p) is Welcome w) welcome = w; }
        Assert.NotNull(welcome);
        for (int round = 0; round < 40; round++)
        {
            var inputs = Enumerable.Range(0, 8).Select(k => new PlayerInput(h.Server.World.Tick + 1 + (uint)k, 1f, 1f, 1f, InputButtons.Fire | InputButtons.Jump)).ToArray();
            raw.Send(Protocol.Write(new InputBatch(inputs, welcome!.Session, 0)));
            for (int i = 0; i < 40; i++) { h.Step(); while (raw.TryReceive(out _)) { } }
        }
        Assert.True(h.Server.World.Players.ContainsKey(player.PlayerId));
        Assert.Single(h.Server.World.Players);                                // one player in the world, nothing for either spectator
        Assert.Equal(before.Health, h.Server.World.Players[player.PlayerId].Health);
        Assert.Equal(2, h.Server.Spectators.Count);
        Assert.DoesNotContain(spectator.PlayerId, h.Server.World.Players.Keys);
        Assert.DoesNotContain(welcome!.PlayerId, h.Server.World.Players.Keys);
    }

    [Fact]
    public void A_shot_through_a_spectators_place_hits_nobody_and_it_can_leave_cleanly()
    {
        var h = Match();
        var shooter = h.AddClient(nonce: 100, options: new ClientOptions { RequestedClass = SmTac });
        var spectator = h.AddClient(nonce: 101, options: Observer);
        h.Run(TimeSpan.FromSeconds(1.5), (client, tick) => new PlayerInput(tick, 0, 0, 0, client == 0 ? InputButtons.Fire : InputButtons.None));
        Assert.All(h.Server.World.LastHits, hit => Assert.NotEqual(spectator.PlayerId, hit.Target));
        spectator.Leave();
        h.Run(TimeSpan.FromMilliseconds(300));
        Assert.Empty(h.Server.Spectators);
        Assert.Equal([shooter.PlayerId], h.Server.Players);
    }

    [Fact]
    public void The_hello_flag_and_the_scoreboards_spectator_list_survive_the_wire()
    {
        var hello = new Hello(Protocol.Version, 1, 2, 3, Flags: Hello.SpectatorFlag);
        var read = Assert.IsType<Hello>(Protocol.Read(Protocol.Write(hello)));
        Assert.True(read.Spectator);
        Assert.False(read.Bot);
        Assert.False(Assert.IsType<Hello>(Protocol.Read(Protocol.Write(hello with { Flags = 0 }))).Spectator);

        var row = new ScoreEntry(1, Team.SpaceMarines, 0, 3, 2, 0, -1, "Aurex");
        var board = new Scoreboard(9, true, 50, 0f, true, [row], null, [new SpectatorEntry(7, "Spectator 7"), new SpectatorEntry(8, "Camera")]);
        var back = Assert.IsType<Scoreboard>(Protocol.Read(Protocol.Write(board)));
        Assert.Equal(board.Spectators, back.Spectators);
        Assert.Equal(board.Players, back.Players);
        // No spectators: the old layout (flag clear), read back as none.
        Assert.Null(Assert.IsType<Scoreboard>(Protocol.Read(Protocol.Write(board with { Spectators = null }))).Spectators);
        // More than the limit are cut to it; a list the reader cannot trust is refused.
        var many = Enumerable.Range(1, Protocol.MaxSpectators + 3).Select(i => new SpectatorEntry(i, "s" + i)).ToArray();
        Assert.Equal(Protocol.MaxSpectators, Assert.IsType<Scoreboard>(Protocol.Read(Protocol.Write(board with { Spectators = many }))).Spectators!.Length);
    }

    [Fact]
    public void The_scoreboard_with_the_most_players_a_long_seize_layout_and_every_spectator_fits_one_packet()
    {
        var rows = Enumerable.Range(1, Protocol.MaxPlayers).Select(i => new ScoreEntry(i, i % 2 == 0 ? Team.Chaos : Team.SpaceMarines, 0, 99, 99, 9, 1, new string('N', PlayerNames.MaxLength))).ToArray();
        var points = Enumerable.Range(0, World.MaxControlPoints).Select(i => new ControlPointInfo(new string('p', Protocol.MaxMapName), new Vector3(i, i, i), 5f, 3f)).ToArray();
        var spectators = Enumerable.Range(1, Protocol.MaxSpectators).Select(i => new SpectatorEntry(i, new string('S', PlayerNames.MaxLength))).ToArray();
        var board = new Scoreboard(9, true, 50, 10f, true, rows, new SeizeGroundInfo(10f, 5f, 1, 1f, points), spectators);
        var bytes = Protocol.Write(board);
        Assert.True(bytes.Length <= UdpServerTransport.MaxPacket, $"{bytes.Length} bytes");
    }
}
