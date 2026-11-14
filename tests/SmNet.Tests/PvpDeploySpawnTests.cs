using System.Numerics;
using SmContent;
using SmNet;
using SmServer;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// fix/pvp-deploy-spawn (protocol 58; the owner, 2026-11-06; classic PvP = Annihilation and Seize Ground, never Musou Mode):
/// <list type="number">
/// <item>DEPLOY in the in-match customizer kills the living player (a suicide) and the player respawns the next tick with the new loadout.</item>
/// <item>No respawn next to an ally unless chosen: the spawn room, a control point the side holds, or a living ally carrying Teleport Homer; never inside another player.</item>
/// <item>The heavy bolter's 400 rounds are a cap: spent a round a shot, no reserve, a pickup refills up to it, and the HUD shows the count (not the endless sign).</item>
/// </list>
/// Synthetic classes, weapons and points (no game data), plus two install pins skipped without the player's install.
/// </summary>
public class PvpDeploySpawnTests(ITestOutputHelper output)
{
    const int SmTac = 0, CsmTac = 1, SmHeavy = 2;
    const int PlainPerk = 0, HomerPerk = 1;
    const int ChPlain = 0, ChHomer = 1;   // indices into PerkChoices

    static readonly WeaponSpec Rifle = new("test_rifle", WeaponSlot.Ranged, TriggerKind.SemiAuto, 20f, 0.2f, 30, 90, 1f, 250f, 250f, 250f, 1f, 1f, 1f, 1f, 1f);
    static readonly WeaponSpec RifleB = new("test_rifle_b", WeaponSlot.Ranged, TriggerKind.SemiAuto, 20f, 0.2f, 20, 60, 1f, 250f, 250f, 250f, 1f, 1f, 1f, 1f, 1f);
    static readonly WeaponSpec Sidearm = new("test_sidearm", WeaponSlot.Pistol, TriggerKind.SemiAuto, 10f, 0.2f, 10, 40, 1f, 250f, 250f, 250f, 1f, 1f, 1f, 1f, 1f);
    // As the install gives it: full auto, 0.233 s, clip 400, no reserve.
    static readonly WeaponSpec HeavyBolter = new("heavy_bolter", WeaponSlot.Ranged, TriggerKind.FullAuto, 35f, 0.233f, 400, 0, 1.5f, 250f, 250f, 250f, 1f, 1f, 1f, 0.7f, 0.7f)
    { AlwaysReady = true };

    static ClassLoadout Tac(string name, Team side) => new(name, 0, 2, SimConfig.PlaceholderMeleeSteps)
    { PrimaryChoices = [0, 1], PistolChoices = [2], Side = side, Role = "tactical", PerkChoices = [PlainPerk, HomerPerk], DefaultPerk = PlainPerk };

    static readonly SimConfig Config = new()
    {
        Weapons = [Rifle, RifleB, Sidearm, HeavyBolter],
        Classes = [Tac("sm - tactical", Team.SpaceMarines), Tac("csm - tactical", Team.Chaos),
            new ClassLoadout("sm - devastator", 3, 2, SimConfig.PlaceholderMeleeSteps) { PrimaryChoices = [3], PistolChoices = [2], Side = Team.SpaceMarines, Role = "heavy", Evade = -1 }],
        Perks = [new PerkSpec("mp_perk_plain", null, PerkKind.Class, 1), new PerkSpec(Loadouts.TeleportHomerPerk, null, PerkKind.Class, 1)],
        HealthMax = 40f, ShieldMax = 0f, HealthRegenDelay = 1e6f, RespawnTime = 5f, MaxRewindTicks = 40,
    };
    static readonly MatchRules Annihilation = new() { TimeLimit = 0f, ScoreLimit = 1000 };
    static readonly MatchRules Seize = MatchRules.For(GameMode.SeizeGround) with { TimeLimit = 0f };

    static readonly Vector3 MarineRoom = new(-60, 0, 0), ChaosRoom = new(60, 0, 0);
    static readonly ControlPoint A = new("mkr_objective_1", new Vector3(0, 30, 0), 5f, 10f);
    static readonly ControlPoint B = new("mkr_objective_2", new Vector3(0, -30, 0), 5f, 10f);

    /// <summary>A 10 x 10 m spawn room per side (a 2 m grid of spots), two control points.</summary>
    static World Arena(MatchRules rules, SimConfig? config = null) => new(config ?? Config)
    {
        Rules = rules, ControlPoints = [A, B],
        StartAreas = [new StartArea(Team.SpaceMarines, MarineRoom, new Vector3(10, 10, 2), 0f), new StartArea(Team.Chaos, ChaosRoom, new Vector3(10, 10, 2), MathF.PI)],
    };

    static Func<int, PlayerInput> Idle(World w) => id => new PlayerInput(0, 0, 0, w.Players[id].Yaw, InputButtons.None);
    static void Run(World w, int ticks, Func<int, PlayerInput>? input = null) { for (int i = 0; i < ticks; i++) w.Step(input ?? Idle(w)); }

    /// <summary>Player <paramref name="id"/> walks along <paramref name="yaw"/> for <paramref name="ticks"/> ticks; everyone else stands.</summary>
    static void Walk(World w, int id, float yaw, int ticks) =>
        Run(w, ticks, p => p == id ? new PlayerInput(0, 0f, 1f, yaw, InputButtons.Sprint) : new PlayerInput(0, 0, 0, w.Players[p].Yaw, InputButtons.None));

    static float YawTo(Vector3 from, Vector3 to) => MathF.Atan2(-(to.X - from.X), to.Y - from.Y);

    static bool InRoom(Vector3 p, Vector3 room) => MathF.Abs(p.X - room.X) <= 5f && MathF.Abs(p.Y - room.Y) <= 5f;

    /// <summary>Kills a living player through a Deploy of its current loadout (the quickest death a test can cause), and steps until it is back.</summary>
    static PlayerState DieAndRespawn(World w, int id)
    {
        var p = w.Players[id];
        Assert.NotNull(w.SetNextLoadout(id, new LoadoutChoice(p.Class, Primary: p.Primary, Perk1: p.Perk1)));
        Assert.True(w.RequestDeploy(id));
        Run(w, 1);
        Assert.False(w.Players[id].Alive);
        Run(w, 1);
        Assert.True(w.Players[id].Alive);
        return w.Players[id];
    }

    // ================================================================= 1. Deploy

    [Fact]
    public void Deploy_kills_the_living_player_at_once_and_it_respawns_the_next_tick_with_the_new_loadout_in_its_spawn_room()
    {
        var w = Arena(Annihilation);
        w.Spawn(1, SmTac); w.Spawn(2, CsmTac);
        Run(w, 2);
        Assert.Equal(MatchState.Playing, w.State);
        Walk(w, 1, YawTo(w.Players[1].Position, Vector3.Zero), 60);   // out of the room
        Assert.False(InRoom(w.Players[1].Position, MarineRoom));
        Assert.Equal(0, w.Players[1].Primary);
        int chaosBefore = w.ScoreOf(Team.Chaos);

        Assert.NotNull(w.SetNextLoadout(1, new LoadoutChoice(SmTac, Primary: 1)));
        Assert.True(w.RequestDeploy(1));
        Run(w, 1);
        var dead = w.Players[1];
        Assert.False(dead.Alive);
        Assert.Equal(0f, dead.RespawnLeft);                                        // no respawn delay
        var kill = Assert.Single(w.LastKills);
        Assert.Equal((0, 1, KillCause.World), (kill.Killer, kill.Victim, kill.Cause));   // a suicide in the feed
        Assert.Equal(1, w.Scores[1].Deaths);
        Assert.Equal(0, w.Scores[2].Kills);
        Assert.Equal(chaosBefore + 1, w.ScoreOf(Team.Chaos));                     // Annihilation: the other side scores, as for any death (ours)
        Assert.Contains(1, w.LastDeployDeaths);

        Run(w, 1);
        var back = w.Players[1];
        Assert.True(back.Alive);
        Assert.Contains(1, w.LastRespawns);
        Assert.Equal(1, back.Primary);                                       // the new loadout
        Assert.Equal(RifleB.ClipSize, back.Clip);
        Assert.True(InRoom(back.Position, MarineRoom), $"respawned at {back.Position}");
        Assert.Null(w.NextLoadout(1));
    }

    [Fact]
    public void Deploy_is_refused_for_a_dead_player_without_a_waiting_loadout_after_the_match_and_outside_classic_PvP()
    {
        var w = Arena(Annihilation);
        w.Spawn(1, SmTac); w.Spawn(2, CsmTac);
        Run(w, 2);
        Assert.False(w.RequestDeploy(1));                                          // nothing waiting
        Assert.False(w.RequestDeploy(99));                                         // unknown
        DieAndRespawn(w, 2);
        Assert.NotNull(w.SetNextLoadout(2, new LoadoutChoice(CsmTac, Primary: 1)));
        Assert.True(w.RequestDeploy(2));
        Run(w, 1);
        Assert.False(w.Players[2].Alive);
        Assert.False(w.RequestDeploy(2));                                          // dead: the change waits for the respawn as ever
        // No rules (a free-for-all test world): no Deploy.
        var free = new World(Config, null, [new SpawnPoint(Vector3.Zero, 0f)]);
        free.Spawn(1, SmTac);
        free.SetNextLoadout(1, new LoadoutChoice(SmTac, Primary: 1));
        Assert.False(free.ClassicPvp);
        Assert.False(free.RequestDeploy(1));
        // Musou Mode: no Deploy suicide (its own class change stays; MusouIsolation).
        var musou = Arena(Annihilation, Config with { Extras = ContentExtras.Musou });
        musou.Spawn(1, SmTac);
        musou.SetNextLoadout(1, new LoadoutChoice(SmTac, Primary: 1));
        Assert.False(musou.ClassicPvp);
        Assert.False(musou.RequestDeploy(1));
    }

    [Fact]
    public void A_Deploy_suicide_drops_no_pickup_where_a_death_in_a_fight_does()
    {
        var pickups = new PickupSpec([new PickupType("pickup_mp_drop_ammo", PickupKind.Ammo, 1, 4f, 25f, [0, 0, 0, 100], [])]);   // every death drops one
        var w = Arena(Annihilation, Config with { Pickups = pickups });
        w.Spawn(1, SmTac); w.Spawn(2, CsmTac);
        Run(w, 2);
        DieAndRespawn(w, 1);
        Assert.Empty(w.LivePickups);
        // Control: player 1 shoots player 2 dead; that death drops a box.
        var d = w.Players[2].Position - w.Players[1].Position;
        float yaw = MathF.Atan2(-d.X, d.Y);
        for (int k = 0; k < 600 && w.Players[2].Alive; k++)
            w.Step(id => id == 1 ? new PlayerInput(0, 0, 0, yaw, k % 2 == 0 ? InputButtons.Fire : InputButtons.None) : new PlayerInput(0, 0, 0, w.Players[id].Yaw, InputButtons.None));
        Assert.False(w.Players[2].Alive);
        Assert.Single(w.LivePickups);
    }

    [Fact]
    public void The_Deploy_flag_travels_in_the_Setup_and_an_unknown_flag_is_refused()
    {
        var setup = new Setup(7, 3, new LoadoutChoice(1, Primary: 2), default, default, Races.OrkLookAutomatic, Setup.DeployNow);
        var bytes = Protocol.Write(setup);
        Assert.Equal(Protocol.SetupBytes, bytes.Length);
        var read = Assert.IsType<Setup>(Protocol.Read(bytes));
        Assert.True(read.Deploy);
        Assert.False(Assert.IsType<Setup>(Protocol.Read(Protocol.Write(setup with { Flags = 0 }))).Deploy);
        bytes[^1] = 2;
        Assert.Null(Protocol.Read(bytes));
        Assert.Equal(73, Protocol.Version);
    }

    [Fact]
    public void Over_the_wire_DEPLOY_kills_a_living_player_and_respawns_it_with_the_new_class_and_a_plain_change_waits()
    {
        var options = new ServerOptions { Match = new MatchRules { TimeLimit = 0f, ScoreLimit = 1000 } };
        var h = new Harness(LinkConditions.Perfect, seed: 5, clients: 0, Config, options);
        var me = h.AddClient(options: new ClientOptions { RequestedClass = SmTac }, nonce: 1);
        var foe = h.AddClient(options: new ClientOptions { RequestedClass = CsmTac }, nonce: 2);
        Assert.True(CustomFixture.RunUntil(h, () => h.Clients.All(c => c.Status == ClientStatus.Connected), TimeSpan.FromSeconds(5)));
        h.Run(TimeSpan.FromSeconds(1));
        var world = h.Server.World;
        Assert.True(world.ClassicPvp);
        // A plain change (SET ACTIVE, between lives): nothing happens until a death.
        me.ChangeSetup(new LoadoutChoice(SmTac, Primary: 1), default, default);
        h.Run(TimeSpan.FromSeconds(1));
        Assert.True(world.Players[me.PlayerId].Alive);
        Assert.Equal(0, world.Players[me.PlayerId].Primary);
        Assert.Equal(0, world.Scores[me.PlayerId].Deaths);
        // DEPLOY: dead and back within a few ticks, with the new primary.
        h.Run(TimeSpan.FromSeconds(4));   // past the server's setup interval
        me.ChangeSetup(new LoadoutChoice(SmTac, Primary: 1), default, default, deploy: true);
        bool died = false;
        Assert.True(CustomFixture.RunUntil(h, () => { died |= !world.Players[me.PlayerId].Alive; return died && world.Players[me.PlayerId].Alive; }, TimeSpan.FromSeconds(2)));
        Assert.Equal(1, world.Players[me.PlayerId].Primary);
        Assert.Equal(1, world.Scores[me.PlayerId].Deaths);
        Assert.True(foe.Status == ClientStatus.Connected);
    }

    // ================================================================= 2. Spawn rules

    [Fact]
    public void Without_a_choice_a_classic_respawn_is_in_the_spawn_room_never_next_to_a_Teleport_Homer_carrier()
    {
        var w = Arena(Annihilation);
        w.Spawn(1, SmTac, perk1Choice: ChHomer);   // the carrier
        w.Spawn(2, SmTac);
        w.Spawn(3, CsmTac);
        Run(w, 2);
        Walk(w, 1, YawTo(w.Players[1].Position, Vector3.Zero), 90);
        Assert.False(InRoom(w.Players[1].Position, MarineRoom));
        Assert.Equal(SpawnChoice.SpawnRoom, w.SpawnChoiceOf(2));
        var back = DieAndRespawn(w, 2);
        output.WriteLine($"carrier at {w.Players[1].Position}, respawned at {back.Position}");
        Assert.True(InRoom(back.Position, MarineRoom));
        Assert.True(Vector3.Distance(back.Position, w.Players[1].Position) > 10f);
    }

    [Fact]
    public void A_chosen_living_Teleport_Homer_ally_is_a_spawn_and_a_dead_or_perkless_ally_or_an_enemy_is_never_offered_or_used()
    {
        var w = Arena(Annihilation);
        w.Spawn(1, SmTac, perk1Choice: ChHomer);   // SM carrier
        w.Spawn(2, SmTac);                         // the one who respawns
        w.Spawn(3, SmTac);                         // SM without the perk
        w.Spawn(4, CsmTac, perk1Choice: ChHomer);  // an enemy carrier
        Run(w, 2);
        Walk(w, 1, YawTo(w.Players[1].Position, Vector3.Zero), 90);
        var carrier = w.Players[1];

        Assert.Equal([SpawnChoice.SpawnRoom, new SpawnChoice(SpawnChoiceKind.Homer, 1)], w.SpawnOptions(2));   // not 3 (no perk), not 4 (enemy), not itself
        Assert.Equal([SpawnChoice.SpawnRoom], w.SpawnOptions(1));                                                // the carrier has no other carrier
        Assert.True(w.CarriesHomer(1) && !w.CarriesHomer(3));
        Assert.False(w.SetSpawnChoice(2, new SpawnChoice(SpawnChoiceKind.Homer, 2)));                           // not itself

        // Chosen: next to the carrier, clear of it.
        Assert.True(w.SetSpawnChoice(2, new SpawnChoice(SpawnChoiceKind.Homer, 1)));
        var back = DieAndRespawn(w, 2);
        float d = Vector3.Distance(back.Position, w.Players[1].Position);
        output.WriteLine($"carrier at {carrier.Position}, respawned at {back.Position}, {d:0.00} m");
        Assert.InRange(d, 2f * Config.CapsuleRadius + 0.1f, 3.2f);
        Assert.Equal(SpawnChoiceKind.Homer, w.SpawnChoiceOf(2).Kind);   // the choice stands

        // A perkless ally chosen (a stale or forged choice): the spawn room.
        Assert.True(w.SetSpawnChoice(2, new SpawnChoice(SpawnChoiceKind.Homer, 3)));
        Assert.True(InRoom(DieAndRespawn(w, 2).Position, MarineRoom));
        // An enemy carrier chosen: the spawn room.
        Assert.True(w.SetSpawnChoice(2, new SpawnChoice(SpawnChoiceKind.Homer, 4)));
        Assert.True(InRoom(DieAndRespawn(w, 2).Position, MarineRoom));

        // The carrier dead: neither offered nor used.
        Assert.True(w.SetSpawnChoice(2, new SpawnChoice(SpawnChoiceKind.Homer, 1)));
        w.SetNextLoadout(1, new LoadoutChoice(SmTac, Perk1: ChHomer));
        w.RequestDeploy(1);
        Run(w, 1);
        Assert.False(w.Players[1].Alive);
        Assert.False(w.CarriesHomer(1));
        Assert.DoesNotContain(new SpawnChoice(SpawnChoiceKind.Homer, 1), w.SpawnOptions(2));
        w.SetNextLoadout(2, new LoadoutChoice(SmTac));
        w.RequestDeploy(2);
        Run(w, 1);   // 2 dies while 1 respawns this tick: 1 is back in its room
        Run(w, 1);
        Assert.True(w.Players[2].Alive);
        Assert.True(InRoom(w.Players[2].Position, MarineRoom));
    }

    [Fact]
    public void In_Seize_Ground_a_point_the_side_holds_is_offered_and_used_and_one_it_does_not_hold_is_not()
    {
        var w = Arena(Seize);
        w.Spawn(1, SmTac); w.Spawn(2, SmTac); w.Spawn(3, CsmTac);
        Run(w, 1);
        Assert.Equal(MatchState.Playing, w.State);
        Assert.Equal([SpawnChoice.SpawnRoom], w.SpawnOptions(2));
        Assert.True(w.SetSpawnChoice(2, new SpawnChoice(SpawnChoiceKind.ControlPoint, 0)));   // asked before it is held
        Assert.True(InRoom(DieAndRespawn(w, 2).Position, MarineRoom));                       // not held: the spawn room
        Assert.False(w.SetSpawnChoice(2, new SpawnChoice(SpawnChoiceKind.ControlPoint, 5)));  // no such point

        // Player 1 captures A.
        var from = w.Players[1].Position;
        int walk = 0;
        while (!A.Contains(w.Players[1].Position) && walk++ < 900) Walk(w, 1, YawTo(w.Players[1].Position, A.Center), 1);
        Walk(w, 1, YawTo(w.Players[1].Position, A.Center), 5);
        for (int k = 0; k < 700 && w.Points[0].Owner != Team.SpaceMarines; k++) Run(w, 1, p => new PlayerInput(0, 0, 0, w.Players[p].Yaw, InputButtons.None));
        Assert.Equal(Team.SpaceMarines, w.Points[0].Owner);
        Assert.Equal([SpawnChoice.SpawnRoom, new SpawnChoice(SpawnChoiceKind.ControlPoint, 0)], w.SpawnOptions(2));
        Assert.Equal([SpawnChoice.SpawnRoom], w.SpawnOptions(3));   // Chaos does not hold it

        var back = DieAndRespawn(w, 2);
        output.WriteLine($"walked from {from}; capturer at {w.Players[1].Position}; respawned at {back.Position}");
        Assert.True(A.Contains(back.Position), $"{back.Position}");
        float gap = 2f * Config.CapsuleRadius + 0.1f;
        Assert.True(Vector2.Distance(new(back.Position.X, back.Position.Y), new(w.Players[1].Position.X, w.Players[1].Position.Y)) >= gap);   // not inside the capturer

        // A Chaos player asking for A gets its room.
        Assert.True(w.SetSpawnChoice(3, new SpawnChoice(SpawnChoiceKind.ControlPoint, 0)));
        Assert.True(InRoom(DieAndRespawn(w, 3).Position, ChaosRoom));
    }

    [Fact]
    public void Annihilation_offers_no_control_point()
    {
        var w = Arena(Annihilation);
        w.Spawn(1, SmTac); w.Spawn(2, CsmTac);
        Run(w, 2);
        Assert.Equal([SpawnChoice.SpawnRoom], w.SpawnOptions(1));
        Assert.True(w.SetSpawnChoice(1, new SpawnChoice(SpawnChoiceKind.ControlPoint, 0)));
        Assert.True(InRoom(DieAndRespawn(w, 1).Position, MarineRoom));
    }

    [Fact]
    public void Respawns_in_a_crowded_spawn_room_never_overlap_a_living_player()
    {
        // A 6 x 6 m room: a 2 m grid of nine spots, all within 2 m of someone once five stand in it; respawns must still take a capsule-free spot.
        var w = new World(Config)
        {
            Rules = Annihilation,
            StartAreas = [new StartArea(Team.SpaceMarines, MarineRoom, new Vector3(6, 6, 2), 0f), new StartArea(Team.Chaos, ChaosRoom, new Vector3(6, 6, 2), MathF.PI)],
        };
        for (int id = 1; id <= 6; id++) w.Spawn(id, SmTac);
        w.Spawn(20, CsmTac);
        Run(w, 2);
        float gap = 2f * Config.CapsuleRadius + 0.1f;
        for (int round = 0; round < 12; round++)
        {
            int id = 1 + round % 6;
            var back = DieAndRespawn(w, id);
            foreach (var (other, p) in w.Players)
                if (other != id && p.Alive)
                    Assert.True(Vector2.Distance(new(back.Position.X, back.Position.Y), new(p.Position.X, p.Position.Y)) >= gap - 1e-3f, $"{id} at {back.Position} overlaps {other} at {p.Position}");
        }
    }

    [Fact]
    public void Musou_Mode_keeps_the_automatic_Teleport_Homer_spawn_and_has_no_choices()
    {
        var w = Arena(Annihilation, Config with { Extras = ContentExtras.Musou });
        w.Spawn(1, SmTac, perk1Choice: ChHomer);
        w.Spawn(2, SmTac);
        w.Spawn(3, CsmTac);
        Run(w, 2);
        Assert.False(w.ClassicPvp);
        Assert.Empty(w.SpawnOptions(2));
        Assert.False(w.SetSpawnChoice(2, new SpawnChoice(SpawnChoiceKind.Homer, 1)));
        // The carrier walks out; the Chaos player shoots player 2 dead; it comes back next to the carrier without any choice (the rule of protocol 44).
        Walk(w, 1, YawTo(w.Players[1].Position, Vector3.Zero), 90);
        var d = w.Players[2].Position - w.Players[3].Position;
        float yaw = MathF.Atan2(-d.X, d.Y);
        for (int k = 0; k < 600 && w.Players[2].Alive; k++)
            w.Step(id => id == 3 ? new PlayerInput(0, 0, 0, yaw, k % 2 == 0 ? InputButtons.Fire : InputButtons.None) : new PlayerInput(0, 0, 0, w.Players[id].Yaw, InputButtons.None));
        Assert.False(w.Players[2].Alive);
        for (int k = 0; k < 6 * Config.TickRate && !w.Players[2].Alive; k++) Run(w, 1);
        Assert.True(w.Players[2].Alive);
        Assert.InRange(Vector3.Distance(w.Players[2].Position, w.Players[1].Position), 1f, 3.2f);
    }

    [Fact]
    public void A_classic_world_is_a_team_match_that_is_not_Musou_Mode()
    {
        Assert.True(Arena(Annihilation).ClassicPvp);
        Assert.True(Arena(Seize).ClassicPvp);
        Assert.False(Arena(Seize, Config with { Extras = ContentExtras.Musou }).ClassicPvp);
        Assert.True(Arena(Annihilation, Config with { Extras = ContentExtras.Orks | ContentExtras.Gore }).ClassicPvp);   // Extended's added content is still classic PvP
    }

    [Fact]
    public void The_spawn_request_and_the_scoreboard_carrier_flag_travel_and_bad_values_are_refused()
    {
        var request = new SpawnRequest(11, SpawnChoiceKind.Homer, 4242);
        var bytes = Protocol.Write(request);
        Assert.Equal(Protocol.SpawnRequestBytes, bytes.Length);
        Assert.Equal(request, Assert.IsType<SpawnRequest>(Protocol.Read(bytes)));
        bytes[5] = 3;   // unknown kind
        Assert.Null(Protocol.Read(bytes));
        Assert.Throws<ArgumentException>(() => Protocol.Write(new SpawnRequest(1, SpawnChoiceKind.Homer, 70000)));

        var board = new Scoreboard(5, true, 41, 0f, false, [new ScoreEntry(1, Team.SpaceMarines, 0, 0, 0, Homer: true), new ScoreEntry(2, Team.Chaos, 1, 0, 0)]);
        var read = Assert.IsType<Scoreboard>(Protocol.Read(Protocol.Write(board)));
        Assert.True(read.Players[0].Homer);
        Assert.False(read.Players[1].Homer);
    }

    [Fact]
    public void The_client_rules_offer_the_room_held_points_and_allied_carriers_and_cycle_through_them()
    {
        var seize = new SeizeGroundInfo(20f, 10f, 1, 1f, [new ControlPointInfo("a", Vector3.Zero, 5f, 5f), new ControlPointInfo("b", Vector3.One, 5f, 5f), new ControlPointInfo("c", Vector3.UnitX, 5f, 5f)]);
        var rows = new[]
        {
            new ScoreEntry(1, Team.SpaceMarines, 0, 0, 0), new ScoreEntry(2, Team.SpaceMarines, 0, 0, 0, Name: "Brother Aurex", Homer: true),
            new ScoreEntry(3, Team.Chaos, 1, 0, 0, Homer: true), new ScoreEntry(4, Team.SpaceMarines, 0, 0, 0),
        };
        var board = new Scoreboard(1, true, 41, 0f, false, rows, seize);
        ControlPointView[] points = [new(Team.SpaceMarines, Team.None, 0f, false), new(Team.Chaos, Team.None, 0f, false), new(Team.SpaceMarines, Team.None, 0f, false)];
        var options = SpawnChoiceRules.Options(board, ContentExtras.None, 1, Team.SpaceMarines, points);
        Assert.Equal([SpawnChoice.SpawnRoom, new(SpawnChoiceKind.ControlPoint, 0), new(SpawnChoiceKind.ControlPoint, 2), new(SpawnChoiceKind.Homer, 2)], options);
        Assert.Equal([SpawnChoice.SpawnRoom], SpawnChoiceRules.Options(board, ContentExtras.None, 2, Team.SpaceMarines, []));   // itself, and no points known
        Assert.Empty(SpawnChoiceRules.Options(board, ContentExtras.Musou, 1, Team.SpaceMarines, points));
        Assert.Empty(SpawnChoiceRules.Options(board with { Teams = false }, ContentExtras.None, 1, Team.SpaceMarines, points));
        Assert.Equal(options[1], SpawnChoiceRules.Next(options, SpawnChoice.SpawnRoom));
        Assert.Equal(SpawnChoice.SpawnRoom, SpawnChoiceRules.Next(options, options[^1]));
        Assert.Equal(options[^1], SpawnChoiceRules.Next(options, SpawnChoice.SpawnRoom, -1));
        Assert.Equal(SpawnChoice.SpawnRoom, SpawnChoiceRules.Effective(options, new SpawnChoice(SpawnChoiceKind.Homer, 3)));
        Assert.Equal("POINT C", SpawnChoiceRules.Label(options[2], board));
        Assert.Equal("BROTHER AUREX (TELEPORT HOMER)", SpawnChoiceRules.Label(options[3], board));
        Assert.Equal("SPAWN ROOM", SpawnChoiceRules.Label(SpawnChoice.SpawnRoom, board));
    }

    [Fact]
    public void Over_the_wire_a_spawn_choice_reaches_the_world_and_the_scoreboard_marks_living_carriers()
    {
        var options = new ServerOptions { Match = new MatchRules { TimeLimit = 0f, ScoreLimit = 1000 } };
        var h = new Harness(LinkConditions.Perfect, seed: 6, clients: 0, Config, options);
        var carrier = h.AddClient(options: new ClientOptions { RequestedClass = SmTac, RequestedPerk1 = ChHomer, RequestedSide = Team.SpaceMarines }, nonce: 1);
        var foe = h.AddClient(options: new ClientOptions { RequestedClass = CsmTac }, nonce: 2);
        var me = h.AddClient(options: new ClientOptions { RequestedClass = SmTac }, nonce: 3);
        Assert.True(CustomFixture.RunUntil(h, () => h.Clients.All(c => c.Status == ClientStatus.Connected), TimeSpan.FromSeconds(5)));
        h.Run(TimeSpan.FromSeconds(2));
        var world = h.Server.World;
        var team = world.Players[me.PlayerId].Team;
        Assert.Equal(team, world.Players[carrier.PlayerId].Team);
        Assert.True(me.Scoreboard!.Players.Single(r => r.PlayerId == carrier.PlayerId).Homer);
        Assert.False(me.Scoreboard.Players.Single(r => r.PlayerId == me.PlayerId).Homer);
        Assert.True(me.SendSpawnChoice(new SpawnChoice(SpawnChoiceKind.Homer, carrier.PlayerId)));
        h.Run(TimeSpan.FromMilliseconds(200));
        Assert.Equal(new SpawnChoice(SpawnChoiceKind.Homer, carrier.PlayerId), world.SpawnChoiceOf(me.PlayerId));
        Assert.True(h.Server.SpawnRequestsAccepted >= 1);
        Assert.True(foe.SendSpawnChoice(new SpawnChoice(SpawnChoiceKind.ControlPoint, 9)));   // no such point: dropped
        h.Run(TimeSpan.FromMilliseconds(200));
        Assert.True(h.Server.SpawnRequestsDropped >= 1);
        Assert.Equal(SpawnChoice.SpawnRoom, world.SpawnChoiceOf(foe.PlayerId));
    }

    // ================================================================= 3. Heavy bolter ammunition

    [Fact]
    public void The_heavy_bolter_fires_400_rounds_a_life_and_no_more_until_the_respawn()
    {
        var c = Config with { RespawnTime = 0.1f };
        var s = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = SmHeavy }, c);
        Assert.Equal((400, 0), (s.Clip, s.Reserve));
        int shots = 0;
        for (int k = 0; k < 200 * c.TickRate; k++)
        {
            s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, InputButtons.Fire), c, out var e);
            if (e.Fired) shots++;
        }
        Assert.Equal(400, shots);
        Assert.Equal(0, s.Clip);
        Assert.Equal(0f, s.ReloadLeft);   // no reserve: nothing to reload from
        Assert.Equal(400, Combat.Respawned(s, c).Clip);   // a new life is full again
    }

    [Fact]
    public void An_ammo_pickup_refills_the_heavy_bolter_up_to_400_and_never_beyond()
    {
        var box = new PickupType("pickup_mp_drop_ammo", PickupKind.Ammo, 1, 4f, 25f, [0, 0, 0, 100], []);   // the install's heavy bolter amount (+100)
        var full = Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = SmHeavy }, Config);
        var (same, gainFull) = Pickups.Collect(Config, full, box);
        Assert.Equal(400, same.Clip);
        var (topped, _) = Pickups.Collect(Config, full with { Clip = 350 }, box);
        Assert.Equal(400, topped.Clip);
        var (low, _) = Pickups.Collect(Config, full with { Clip = 120 }, box);
        Assert.Equal(220, low.Clip);
        output.WriteLine($"full gain {gainFull}");
    }

    [Fact]
    public void The_heavy_bolters_weapon_box_shows_the_rounds_left_not_the_endless_sign()
    {
        foreach (bool chaos in new[] { false, true })
        {
            var spec = WeaponBoxRules.For("sm_mp_exotic_heavy_bolter", "heavy_bolter", chaos)!;
            Assert.Equal("ammo_mc/ammo_txt", spec.Count);
            Assert.DoesNotContain("infinity_mc", spec.Show);
            Assert.Equal(WeaponBoxBar.Heat, spec.Value);
        }
    }

    static readonly Lazy<string?> Game = new(() => GameInstall.TryLocate()?.Dir);

    public sealed class InstallFactAttribute : FactAttribute
    {
        public InstallFactAttribute()
        {
            if (Game.Value is null) Skip = "Space Marine install not found (the pin needs the player's install; never in CI)";
        }
    }

    [InstallFact]
    public void On_the_install_both_heavy_bolters_hold_400_rounds_with_no_reserve_and_stop_after_400_shots()
    {
        var c = GameplayTuning.Load(Game.Value!).Config;
        var bolters = c.Weapons.Where(w => w.Name.EndsWith("_mp_exotic_heavy_bolter", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, bolters.Count);
        foreach (var w in bolters) Assert.Equal((400, 0, 1, (RechargeSpec?)null), (w.ClipSize, w.ReserveSize, w.AmmoPerShot, w.Recharge));
        int heavy = Enumerable.Range(0, c.Classes.Count).First(i => c.Classes[i].Role == "heavy" && c.Classes[i].Side == Team.SpaceMarines);
        var s = new World(c).Spawn(1, heavy);
        Assert.Equal("sm_mp_exotic_heavy_bolter", Loadouts.InHand(c, s)!.Name);
        int shots = 0;
        for (int k = 0; k < 200 * c.TickRate; k++)
        {
            s = Combat.StepPlayer(s, new PlayerInput(0, 0f, 0f, 0f, InputButtons.Fire), c, out var e);
            if (e.Fired) shots++;
        }
        output.WriteLine($"{shots} shots in 200 s of held fire (heat and overheats included)");
        Assert.Equal(400, shots);
        Assert.Equal(0, s.Clip);
    }
}
