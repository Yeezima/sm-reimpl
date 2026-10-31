using System.Numerics;
using SmNet.Extras.Musou;
using SmNet.Extras.Musou.Campaign;
using SmServer;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Musou.Campaign;

/// <summary>
/// The Three-Front War's contracts (TF0): the generated map and how a server picks it (Musou mode is the war; a named map keeps the 5-wave run), a client joining it as a Space
/// Marine in the Stormbird, the AI Marines as players without a peer (input, name, loadout, the ids a join skips), the commands of message kind 17, the war's respawn, the wire's tags.
/// </summary>
public class TfwContractTests
{
    static readonly WeaponSpec Rifle = new("rifle", WeaponSlot.Ranged, TriggerKind.FullAuto, 9f, 0.12f, 30, 270, 1.2f, 250f, 45f, 70f, 0.8f, 2.5f, 1f, 0.5f, 0.5f);
    static readonly WeaponSpec Pistol = new("pistol", WeaponSlot.Pistol, TriggerKind.SemiAuto, 20f, 0.3f, 8, 64, 1.2f, 120f, 30f, 60f, 0.5f, 3f, 1f, 0.6f, 0.6f);
    static readonly MeleeStep Light = new(1f, 0.2f, 0.4f, 0.6f, 0.5f, 0.7f, 25f, 10f, 3.5f);

    /// <summary>As the match tuning prepares a Musou config: no class has a primary by default (<see cref="MusouLoadout.Prepared"/>).</summary>
    static ClassLoadout Class(string name, Team side) => new(name, -1, 1, [Light]) { PrimaryChoices = [0], PistolChoices = [1], Side = side, Role = "tactical" };

    static readonly SimConfig Config = new()
    {
        Weapons = [Rifle, Pistol], Classes = [Class("sm - tactical", Team.SpaceMarines), Class("csm - tactical", Team.Chaos)], Extras = ContentExtras.Musou,
    };
    static MatchRules Rules => new() { CountdownTime = 1f, TimeLimit = 0f, ScoreLimit = int.MaxValue / 2 };

    static GameMap WarMap()
    {
        string? name = null;
        var map = GameServer.GeneratedMap(ContentExtras.Musou, ref name, out string? error);
        Assert.Null(error);
        Assert.Equal(TfwMap.Name, name);
        return Assert.IsType<GameMap>(map);
    }

    static Harness WarServer(int clients = 1)
    {
        var map = WarMap();
        return new Harness(LinkConditions.Perfect, 5, clients, Config, new ServerOptions { Match = Rules }, map: map,
            clientOptions: new ClientOptions { LoadMap = _ => new LoadedMap(map.Collision, map.Hash) });
    }

    static bool InStormbird(Vector3 p) =>
        MathF.Abs(p.X - TfwMap.StormbirdCentre.X) <= TfwMap.StormbirdSize.X / 2f && MathF.Abs(p.Y - TfwMap.StormbirdCentre.Y) <= TfwMap.StormbirdSize.Y / 2f;

    // ---------------------------------------------------------------- the map and its choice

    [Fact]
    public void The_map_is_generated_the_same_every_time_and_holds_the_layout()
    {
        var a = TfwMap.Build();
        var b = TfwMap.Build();
        Assert.Equal(a.Hash, b.Hash);
        Assert.True(Protocol.IsMapName(TfwMap.Name));
        Assert.All(a.Spawns, s => Assert.True(InStormbird(s.Position), $"{s.Position}"));
        Assert.Equal(0f, a.Spawns[0].Yaw);   // facing north
        Assert.Equal(80f, -TfwMap.ArenaHalf - TfwMap.StormbirdCentre.Y);   // 80 m from the arena
        Assert.Contains(a.StartAreas, s => s.Side == Team.SpaceMarines && s.Center == TfwMap.StormbirdCentre);
        foreach (var at in new[] { TfwMap.Position1, TfwMap.Position2, TfwMap.Position3, TfwMap.Hq, TfwMap.LandingZone, TfwMap.DestinationStation, TfwMap.EquipmentStation, TfwMap.StormbirdCentre }.Concat(TfwMap.Sources))
            Assert.NotNull(a.Collision.Character.Raycast(at + new Vector3(0f, 0f, 1f), -Vector3.UnitZ, 2f));   // every place stands on the floor
    }

    [Theory]
    [InlineData(ContentExtras.Musou, null, true, TfwMap.Name)]        // Musou mode is the war
    [InlineData(ContentExtras.Musou, TfwMap.Name, true, TfwMap.Name)]
    [InlineData(ContentExtras.Musou, "mp_tdm_09", false, "mp_tdm_09")]   // the 5-wave run's test aid
    [InlineData(ContentExtras.Musou, "none", false, "none")]
    [InlineData(ContentExtras.None, null, false, null)]
    [InlineData(ContentExtras.Orks | ContentExtras.Gore | ContentExtras.LoyalistBarks, "mp_tdm_09", false, "mp_tdm_09")]
    public void A_server_generates_the_war_map_for_musou_alone(ContentExtras extras, string? asked, bool generated, string? named)
    {
        string? name = asked;
        var map = GameServer.GeneratedMap(extras, ref name, out string? error);
        Assert.Null(error);
        Assert.Equal(generated, map is not null);
        Assert.Equal(named, name);
    }

    [Theory]
    [InlineData(ContentExtras.None)]
    [InlineData(ContentExtras.Orks | ContentExtras.Gore | ContentExtras.LoyalistBarks)]
    [InlineData(ContentExtras.Orks)]
    public void The_war_map_without_musou_is_refused(ContentExtras extras)
    {
        string? name = TfwMap.Name;
        Assert.Null(GameServer.GeneratedMap(extras, ref name, out string? error));
        Assert.Contains("--content musou", error);
    }

    [Fact]
    public void Another_map_under_musou_keeps_the_five_wave_run()
    {
        var mesh = new NavigationTests.Level().Floor(-40f, -40f, 40f, 40f).Mesh();
        var collision = new CollisionWorld(mesh, mesh);
        var map = new GameMap("mp_tdm_09", collision, [new SpawnPoint(Vector3.Zero, 0f)], 77);
        var h = new Harness(LinkConditions.Perfect, 5, 0, Config, new ServerOptions { Match = Rules }, map: map);
        Assert.NotNull(h.Server.World.Musou);
        Assert.Null(h.Server.World.Tfw);
        Assert.Contains("the mode runs", h.Server.MusouNote);
    }

    [Fact]
    public void The_war_starts_only_under_the_flag_and_with_rules()
    {
        Assert.False(new World(Config with { Extras = ContentExtras.All }) { Rules = Rules }.StartTfw(null));
        Assert.False(new World(Config).StartTfw(null));
        var w = new World(Config) { Rules = Rules };
        Assert.True(w.StartTfw(null));
        var first = w.Tfw;
        Assert.NotNull(first);
        Assert.NotNull(w.Musou);
        Assert.True(w.StartTfw(null));
        Assert.Same(first, w.Tfw);
    }

    // ---------------------------------------------------------------- a client joins

    [Fact]
    public void A_client_joins_the_war_as_a_space_marine_in_the_stormbird_with_the_pistol()
    {
        var h = WarServer(0);
        h.AddClient(nonce: 1, options: new ClientOptions { RequestedSide = Team.Chaos, LoadMap = _ => new LoadedMap(h.Server.Map!.Collision, h.Server.Map.Hash) });
        Assert.NotNull(h.Server.World.Tfw);
        Assert.Contains("Three-Front War", h.Server.MusouNote);
        var log = new List<string>();
        h.Server.Log += log.Add;
        h.Run(TimeSpan.FromSeconds(3));
        Assert.Equal(ClientStatus.Connected, h.Clients[0].Status);
        Assert.Equal(MatchState.Playing, h.Server.World.State);
        var p = Assert.Single(h.Server.World.Players.Values);
        Assert.Equal(Team.SpaceMarines, p.Team);
        Assert.True(InStormbird(p.Position), $"spawned at {p.Position}");
        Assert.Equal((-1, WeaponSlot.Pistol), (p.Primary, p.Slot));
        Assert.Equal(0, h.Server.World.Musou!.Count);
        Assert.Equal(MusouPhase.Waiting, h.Server.World.MusouDirector!.Phase);   // the 5-wave director never starts
        Assert.Equal(default, h.Server.World.Musou.Director);                    // and nothing goes out for it
        Assert.Empty(h.Clients[0].TakeExtras());
        Assert.Contains(log, l => l.StartsWith("musou: the Three-Front War", StringComparison.Ordinal));
    }

    [Fact]
    public void A_client_with_another_map_hash_is_refused()
    {
        var h = WarServer(0);
        var other = new StaticMesh([new(0f, 0f, 0f), new(1f, 0f, 0f), new(0f, 1f, 0f)], [0, 1, 2]);
        h.AddClient(nonce: 1, options: new ClientOptions { LoadMap = _ => new LoadedMap(new CollisionWorld(other, other), 123) });
        h.Run(TimeSpan.FromSeconds(1));
        Assert.Equal(ClientStatus.Rejected, h.Clients[0].Status);
    }

    // ---------------------------------------------------------------- the AI Marines: players without a peer

    [Fact]
    public void A_puppet_runs_without_a_peer_with_its_own_input_name_and_loadout()
    {
        var h = WarServer();
        var w = h.Server.World;
        const int puppet = TfwContracts.PuppetIdFirst;
        w.Spawn(puppet, 0, primaryChoice: 0);
        var asked = new List<int>();
        w.Tfw!.PuppetBrain = (id, state) => { asked.Add(id); return new PlayerInput(0, 0f, 1f, 0f, InputButtons.None); };
        var at = w.Players[puppet].Position;
        h.Run(TimeSpan.FromSeconds(3));
        Assert.Equal(0, h.Server.Faults);
        Assert.Equal(ClientStatus.Connected, h.Clients[0].Status);
        Assert.All(asked, id => Assert.Equal(puppet, id));
        Assert.NotEmpty(asked);
        Assert.True(w.Players[puppet].Position.Y > at.Y + 1f, "the brain's input moved it");
        Assert.Equal(0, w.Players[puppet].Primary);                              // keeps what it was given; the pistol-only rule is the humans'
        var human = w.Players.Keys.Single(id => id != puppet);
        Assert.Equal(-1, w.Players[human].Primary);
        var board = h.Server.ScoreboardOf();
        Assert.Equal("Marine 1", board.Players.Single(e => e.PlayerId == puppet).Name);
        Assert.Equal("", board.Players.Single(e => e.PlayerId == human).Name);   // a human's name is untouched
        w.Tfw.PuppetBrain = null;
        var still = w.Players[puppet];
        h.Run(TimeSpan.FromSeconds(1));
        Assert.Equal(still.Yaw, w.Players[puppet].Yaw);                          // no brain: it stands, keeping its heading
    }

    [Fact]
    public void A_joining_player_skips_an_id_a_puppet_holds()
    {
        var h = WarServer(0);
        h.Server.World.Spawn(TfwContracts.PuppetIdFirst, 0);
        h.Server.NextPlayerIdForTests = TfwContracts.PuppetIdFirst;
        h.AddClient(nonce: 1, options: new ClientOptions { LoadMap = _ => new LoadedMap(h.Server.Map!.Collision, h.Server.Map.Hash) });
        h.Run(TimeSpan.FromSeconds(1));
        Assert.Equal(TfwContracts.PuppetIdLast + 1, Assert.Single(h.Server.Players));   // TF5: past the whole reserved range, held or not
    }

    // ---------------------------------------------------------------- commands (message kind 17)

    [Fact]
    public void A_command_reaches_the_war_and_the_bad_ones_do_not()
    {
        var h = WarServer();
        h.Run(TimeSpan.FromSeconds(1));
        var client = h.Clients[0];
        var run = h.Server.World.Tfw!;
        var deploy = new TfwCommandRecord(300, TfwCommand.Deploy, (byte)TfwLane.B);
        Assert.True(client.SendExtrasCommand(TfwWire.CommandTag, TfwWire.WriteCommand(deploy)));
        Assert.True(client.SendExtrasCommand(TfwWire.CommandTag, [1, 2]));                 // malformed
        Assert.True(client.SendExtrasCommand(TfwWire.CommandTag, [1, 0, 99, 0]));          // an unknown command
        Assert.True(client.SendExtrasCommand(200, TfwWire.WriteCommand(deploy)));          // not the war's tag
        h.Run(TimeSpan.FromMilliseconds(200));
        Assert.Equal(4, h.Server.ExtrasCommandsAccepted);
        Assert.Equal(1, run.QueuedCommands);
        Assert.True(run.TryDequeue(out int player, out var command));
        Assert.Equal(client.PlayerId, player);
        Assert.Equal(deploy, command);
        Assert.False(run.TryDequeue(out _, out _));
    }

    [Fact]
    public void Commands_are_cut_at_the_rate_and_the_queue_is_bounded()
    {
        var h = WarServer();
        h.Run(TimeSpan.FromSeconds(1));
        var payload = TfwWire.WriteCommand(new TfwCommandRecord(1, TfwCommand.Follow, 0));
        for (int k = 0; k < 30; k++) h.Clients[0].SendExtrasCommand(TfwWire.CommandTag, payload);
        h.Run(TimeSpan.FromMilliseconds(100));
        Assert.Equal(8, h.Server.ExtrasCommandsAccepted);    // the burst
        Assert.Equal(22, h.Server.ExtrasCommandsDropped);
        var run = new TfwRun();
        for (int k = 0; k < TfwRun.MaxQueuedCommands; k++) Assert.True(run.Enqueue(1, default));
        Assert.False(run.Enqueue(1, default));
    }

    // ---------------------------------------------------------------- the war's respawn

    [Fact]
    public void The_wars_respawn_applies_the_waiting_loadout_and_a_fresh_life_with_the_pistol()
    {
        var w = new World(Config, null, TfwMap.Spawns) { Rules = Rules };
        Assert.False(w.TfwRespawnAt(1, Vector3.Zero, 0f));   // outside a war
        Assert.True(w.StartTfw(null));
        Assert.False(w.TfwRespawnAt(1, Vector3.Zero, 0f));   // nobody of that id
        w.Spawn(1, 0);
        for (int k = 0; k < 60; k++) w.Step(_ => default);
        Assert.NotNull(w.SetNextLoadout(1, LoadoutChoice.Default with { Class = 0, Primary = 0 }));
        Assert.NotNull(w.NextLoadout(1));
        var lz = TfwMap.LandingZone;
        Assert.True(w.TfwRespawnAt(1, lz, 1f));
        Assert.Null(w.NextLoadout(1));                       // the waiting change is consumed, as the core's respawn does
        Assert.Contains(1, w.LastRespawns);                  // so the server promotes the pending look
        Assert.Equal(lz, w.Players[1].Position);
        Assert.Equal(1f, w.Players[1].Yaw);
        Assert.Equal(0, w.Players[1].Primary);               // the class change took
        w.Step(_ => new PlayerInput(0, 0f, 0f, 1f, InputButtons.None));
        Assert.Equal((-1, WeaponSlot.Pistol), (w.Players[1].Primary, w.Players[1].Slot));   // the next arm strips it: pistol-only per deploy
    }

    // ---------------------------------------------------------------- the wire

    [Fact]
    public void The_wars_tags_are_its_own_and_a_command_round_trips()
    {
        for (int tag = 0; tag <= 255; tag++)
            Assert.Equal(tag is >= 7 and <= 10, TfwWire.Owns((byte)tag));
        for (byte tag = 7; tag <= 10; tag++) Assert.False(MusouWire.Owns(tag));
        var c = new TfwCommandRecord(0xABCD, TfwCommand.Attack, 3);
        Assert.Equal(c, TfwWire.ReadCommand(TfwWire.WriteCommand(c)));
        Assert.Null(TfwWire.ReadCommand([1, 2, 3]));
        Assert.Null(TfwWire.ReadCommand([1, 2, 0, 3]));      // command 0 is none
        Assert.Null(TfwWire.ReadCommand([1, 2, 7, 3]));
    }

    [Fact]
    public void The_campaign_phase_travels_in_the_director()
    {
        var view = new MusouDirectorView(0, 0, MusouPhase.Campaign, 0, 0, 0, 0, 0f, 0f, 0, new MusouBox(new Vector3(-1f), new Vector3(1f)));
        Assert.Equal(6, (byte)MusouPhase.Campaign);
        Assert.Equal(view, MusouWire.ReadDirector(MusouWire.WriteDirector(view)));
    }

    [Fact]
    public void The_war_ids_read_back()
    {
        Assert.True(TfwContracts.IsGuardTarget(TfwContracts.GuardTargetId(0)));
        Assert.Equal(23, TfwContracts.SquadOf(TfwContracts.GuardTargetId(23)));
        Assert.False(TfwContracts.IsPuppet(TfwContracts.PuppetIdFirst - 1));
        Assert.True(TfwContracts.IsPuppet(TfwContracts.PuppetIdLast));
        Assert.False(TfwContracts.IsPuppet(TfwContracts.PuppetIdLast + 1));
    }
}
