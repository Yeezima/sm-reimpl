using System.Numerics;
using SmContent;
using SmServer;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Musou.Campaign;

/// <summary>
/// The AI Marines (TF5; since 2026-11-08 the owner's allied pair, <see cref="AllyRules"/>): the brain's decisions, the pair as the world runs it (two at most, together, to its goal, back 30 s
/// after a death), the revive against a fake of the run's window, the reserved ids, and a loopback server with the pair for 10 simulated minutes.
/// </summary>
public class MarineBrainTests(ITestOutputHelper output)
{
    static readonly WeaponSpec Rifle = new("rifle", WeaponSlot.Ranged, TriggerKind.FullAuto, 40f, 0.12f, 30, 270, 1.2f, 250f, 45f, 70f, 0.8f, 2.5f, 1f, 0.5f, 0.5f);
    static readonly WeaponSpec Pistol = new("pistol", WeaponSlot.Pistol, TriggerKind.SemiAuto, 20f, 0.3f, 8, 64, 1.2f, 120f, 30f, 60f, 0.5f, 3f, 1f, 0.6f, 0.6f);
    static readonly MeleeStep Light = new(1f, 0.2f, 0.4f, 0.6f, 0.5f, 0.7f, 60f, 10f, 3.5f);

    static ClassLoadout Class(string name, string role, bool primary = true) =>
        new(name, -1, 1, [Light]) { PrimaryChoices = primary ? [0] : [], PistolChoices = [1], Side = Team.SpaceMarines, Role = role };

    static readonly SimConfig Config = new()
    {
        Weapons = [Rifle, Pistol],
        Classes = [Class("sm - tactical", "tactical"), Class("sm - assault", "assault", primary: false), Class("sm - devastator", "heavy")],
        Extras = ContentExtras.Musou, RespawnTime = 60f,
    };
    static MatchRules Rules => new() { CountdownTime = 1f, TimeLimit = 0f, ScoreLimit = int.MaxValue / 2 };

    const int Human = 1;
    static readonly Vector3 Lz = TfwTestArena.LandingZone;

    static World War()
    {
        var map = TfwTestArena.Build();
        var w = new World(Config, map.Collision, map.Spawns) { Rules = Rules };
        Assert.True(w.StartTfw(null, TfwTestArena.Field));
        w.Tfw!.Idle = true;   // the Marines alone: these tests set the goal and the window themselves (TF4's run would)
        return w;
    }

    /// <summary>A war in play with a human at <paramref name="at"/>.</summary>
    static World Playing(Vector3 at)
    {
        var w = War();
        w.Spawn(Human, 0);
        Place(w, Human, at);
        for (int k = 0; k < 200 && w.State != MatchState.Playing; k++) Tick(w);
        Assert.Equal(MatchState.Playing, w.State);
        Place(w, Human, at);
        return w;
    }

    static void Tick(World w, Func<PlayerState, PlayerInput>? human = null) =>
        w.Step(id => human is not null && id == Human ? human(w.Players[id]) : new PlayerInput(0, 0f, 0f, w.Players[id].Yaw, InputButtons.None));

    static void Run(World w, float seconds, Func<PlayerState, PlayerInput>? human = null, Action? each = null)
    {
        int ticks = (int)(seconds * w.Config.TickRate);
        for (int k = 0; k < ticks; k++) { Tick(w, human); each?.Invoke(); }
    }

    static void Place(World w, int id, Vector3 at) => w.TfwRespawnAt(id, at, 0f);

    static float Flat(World w, int a, int b) => MarineBrain.Flat(w.Players[a].Position, w.Players[b].Position);

    static void Kill(World w, int id, float respawn = 60f) => SetPlayer(w, id, Combat.Killed(w.Players[id], w.Config) with { RespawnLeft = respawn });

    /// <summary>Test seam: the world has no public setter for a player's state.</summary>
    static void SetPlayer(World w, int id, PlayerState state)
    {
        var field = typeof(World).GetField("_players", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        ((Dictionary<int, PlayerState>)field.GetValue(w)!)[id] = state;
    }

    static int Kills(World w, int id) => w.Scores.GetValueOrDefault(id).Kills;

    // ---------------------------------------------------------------- the brain alone

    static readonly PlayerState Self = new(TfwContracts.PuppetIdFirst, Vector3.Zero, Vector3.Zero, 0f, true) { Alive = true, Clip = 30, Reserve = 90 };

    [Fact]
    public void Moving_goes_along_the_world_direction_whatever_the_heading()
    {
        foreach (float yaw in new[] { 0f, 1f, -2f, 3f })
        {
            var goal = new MarineGoal(new Vector3(0f, 20f, 0f), 0f, 1f, false);
            var aim = new Vector3(20f, 0f, 1f);   // aims east, moves north: a strafe
            var input = MarineBrain.Decide(Self with { Yaw = yaw }, Rifle, goal, aim, 20f, 1.7f, 10);
            var forward = new Vector2(-MathF.Sin(input.Yaw), MathF.Cos(input.Yaw));
            var right = new Vector2(MathF.Cos(input.Yaw), MathF.Sin(input.Yaw));
            var world = forward * input.MoveY + right * input.MoveX;
            Assert.True(Vector2.Distance(world, Vector2.UnitY) < 1e-3f, $"{world}");
        }
    }

    [Fact]
    public void The_aim_error_stays_within_two_degrees_and_is_repeatable()
    {
        float limit = MarineRules.AimErrorDegrees * MathF.PI / 180f;
        float low = 0f, high = 0f;
        for (uint t = 0; t < 2000; t++)
        {
            float e = MarineBrain.AimError(65520, t, 0);
            Assert.InRange(e, -limit * 1.0001f, limit * 1.0001f);
            Assert.Equal(e, MarineBrain.AimError(65520, t, 0));
            low = MathF.Min(low, e); high = MathF.Max(high, e);
        }
        Assert.True(low < -limit * 0.8f && high > limit * 0.8f, "the error uses its range");
    }

    [Fact]
    public void The_trigger_is_held_while_the_clip_lasts_then_it_reloads()
    {
        var aim = new Vector3(0f, 15f, 1f);
        Assert.True(MarineBrain.Decide(Self, Rifle, null, aim, 15f, 1.7f, 4).Buttons.HasFlag(InputButtons.Fire));
        Assert.True(MarineBrain.Decide(Self with { FireHeld = true }, Rifle, null, aim, 15f, 1.7f, 5).Buttons.HasFlag(InputButtons.Fire));
        var empty = MarineBrain.Decide(Self with { Clip = 0 }, Rifle, null, aim, 15f, 1.7f, 4).Buttons;
        Assert.True(empty.HasFlag(InputButtons.Reload) && !empty.HasFlag(InputButtons.Fire));
        Assert.False(MarineBrain.Decide(Self with { FireHeld = true }, Pistol, null, aim, 15f, 1.7f, 5).Buttons.HasFlag(InputButtons.Fire));   // semi-automatic: a new press
        var braced = Rifle with { Brace = new BraceSpec(0.1f, 1f) };
        Assert.False(MarineBrain.Decide(Self with { Clip = 0 }, braced, null, aim, 15f, 1.7f, 4).Buttons.HasFlag(InputButtons.Reload));         // there Reload changes stance
        Assert.False(MarineBrain.Decide(Self with { Overheated = true }, Rifle, null, aim, 15f, 1.7f, 4).Buttons.HasFlag(InputButtons.Fire));
    }

    [Fact]
    public void An_enemy_in_reach_is_struck_and_a_sprint_never_aims()
    {
        var melee = MarineBrain.Decide(Self, Rifle, null, new Vector3(0f, 2f, 1f), MarineRules.MeleeRange, 1.7f, 4).Buttons;
        Assert.True(melee.HasFlag(InputButtons.Melee) && !melee.HasFlag(InputButtons.Fire));
        var sprint = MarineBrain.Decide(Self, Rifle, new MarineGoal(new Vector3(0f, 40f, 0f), 0f, 1f, true), new Vector3(30f, 0f, 1f), 30f, 1.7f, 4);
        Assert.True(sprint.Buttons.HasFlag(InputButtons.Sprint) && !sprint.Buttons.HasFlag(InputButtons.Fire));
        Assert.True(sprint.MoveY > 0.99f);
    }

    [Fact]
    public void A_follower_keeps_to_its_leader_and_a_leader_revives_before_its_goal()
    {
        var leader = new Vector3(0f, 30f, 0f);
        var follow = MarineBrain.Plan(new MarineSituation(Vector3.Zero, leader, new Vector3(9f, 9f, 0f), new Vector3(-50f, 0f, 0f), null, false))!.Value;
        Assert.Equal((leader, AllyRules.FollowNear, AllyRules.FollowFar, true), (follow.Point, follow.Near, follow.Far, follow.Sprint));   // far behind: it sprints after him
        var body = new Vector3(0f, 30f, 0f);
        var revive = MarineBrain.Plan(new MarineSituation(Vector3.Zero, null, body, new Vector3(-50f, 0f, 0f), new Vector3(5f, 5f, 0f), false))!.Value;
        Assert.Equal(body, revive.Point);
        Assert.True(revive.Sprint);
        var paired = MarineBrain.Plan(new MarineSituation(Vector3.Zero, null, null, new Vector3(-50f, 0f, 0f), null, false, MaySprint: false))!.Value;
        Assert.Equal(new Vector3(-50f, 0f, 0f), paired.Point);
        Assert.False(paired.Sprint);                                         // a leader with a follower never sprints away from him
    }

    // ---------------------------------------------------------------- the world: the pair

    static (int Leader, int Follower) Pair(World w, Vector3 at)
    {
        Assert.Equal(AllyRules.Count, w.TfwSpawnAllies(at));
        var run = w.Tfw!;
        return (run.Marines.Single(m => m.Leads).Id, run.Marines.Single(m => !m.Leads).Id);
    }

    [Fact]
    public void Two_at_most_named_and_led_by_the_first()
    {
        var w = Playing(new Vector3(0f, -200f, 0f));
        int first = w.TfwSpawnAlly(Lz)!.Value;
        int second = w.TfwSpawnAlly(Lz + new Vector3(2f, 0f, 0f))!.Value;
        Assert.Null(w.TfwSpawnAlly(Lz));
        Assert.Equal(0, w.TfwSpawnAllies(Lz));
        var run = w.Tfw!;
        Assert.Equal([first, second], run.Marines.Select(m => m.Id));
        Assert.Equal(TfwContracts.PuppetIdFirst, first);
        Assert.True(run.MarineOf(first)!.Leads && !run.MarineOf(second)!.Leads);
        Assert.Equal(["Brother Varro", "Brother Calix"], run.Marines.Select(m => run.PuppetName(m.Id)));
        Assert.Equal(["tactical", "heavy"], run.Marines.Select(m => w.Config.Classes[w.Players[m.Id].Class].Role));
    }

    [Fact]
    public void The_pair_roams_together_to_its_goal_and_fights_there()
    {
        var w = Playing(new Vector3(0f, -200f, 0f));   // the player stays in the Stormbird: the pair acts alone
        var (leader, follower) = Pair(w, Lz);
        var run = w.Tfw!;
        run.ActivePosition = TfwTestArena.Position1;
        float most = 0f;
        Run(w, 3f);
        Run(w, 25f, null, () => most = MathF.Max(most, Flat(w, leader, follower)));
        Assert.True(MarineBrain.Flat(w.Players[leader].Position, TfwTestArena.Position1) <= MarineRules.ArriveRadius + 1f, $"stopped at {w.Players[leader].Position}");
        Assert.True(most <= AllyRules.LeaderWait + 3f, $"{most} m apart on the way");
        Assert.InRange(Flat(w, leader, follower), 0f, AllyRules.FollowFar + 2f);

        run.ActivePosition = TfwTestArena.Position2;                             // the goal moves on: they go together
        most = 0f;
        Run(w, 25f, null, () => most = MathF.Max(most, Flat(w, leader, follower)));
        Assert.True(MarineBrain.Flat(w.Players[leader].Position, TfwTestArena.Position2) <= MarineRules.ArriveRadius + 1f, $"stopped at {w.Players[leader].Position}");
        Assert.True(most <= AllyRules.LeaderWait + 3f, $"{most} m apart on the way");

        for (int k = 0; k < 6; k++) w.Musou!.Spawn(MusouEnemyType.Boy, TfwTestArena.Position2 + new Vector3(4f + k, 5f, 0f), 0f, 85f);
        Run(w, 30f);
        Assert.True(Kills(w, leader) + Kills(w, follower) >= 4, $"{Kills(w, leader)} + {Kills(w, follower)} kills");
        Assert.True(Kills(w, follower) >= 1, "the follower fights too");
    }

    [Fact]
    public void A_puppets_shot_counts_once_in_the_tick()
    {
        var w = Playing(Lz);
        int r = w.TfwSpawnAlly(Lz + new Vector3(5f, 0f, 0f))!.Value;
        var musou = w.Musou!;
        musou.Spawn(MusouEnemyType.Boy, w.Players[r].Position + new Vector3(12f, 0f, 0f), 0f, 50000f);
        int shots = 0, hits = 0;
        Run(w, 1.5f, null, () =>
        {
            int shot = w.LastShots.Count(x => x.Shooter == r), hit = musou.Events.Count(e => e.Kind == MusouEventKind.Hit);
            Assert.True(hit <= shot, $"{hit} hits from {shot} shots in a tick");
            shots += shot; hits += hit;
        });
        Assert.True(hits > 0, $"{hits} hits from {shots} shots");
    }

    [Fact]
    public void A_dead_marine_is_back_30_s_later_and_follows_its_partner()
    {
        var w = Playing(new Vector3(0f, -200f, 0f));
        var (leader, follower) = Pair(w, Lz);
        var run = w.Tfw!;
        Kill(w, leader, 0.5f);
        Run(w, 1f);
        Assert.True(run.MarineOf(follower)!.Leads);                          // the survivor leads
        Run(w, AllyRules.RespawnSeconds - 1.5f);
        Assert.False(w.Players[leader].Alive);                               // the core never respawns it: the timer is held
        Run(w, 1f);
        Assert.True(w.Players[leader].Alive);
        Assert.True(MarineBrain.Flat(w.Players[leader].Position, run.Field.MarineStart) < 1f, $"{w.Players[leader].Position}");   // no battle: the war's start (a battle: its landing zone)
        Assert.False(run.MarineOf(leader)!.Leads);                           // back, it follows the one that stood
        Assert.Equal(1, run.Marines.Count(m => m.Leads));
    }

    // ---------------------------------------------------------------- the revive, against a fake of the run

    [Fact]
    public void The_leader_revives_within_six_seconds_of_arrival_at_half_health_and_shield()
    {
        var w = Playing(Lz);
        var (r, _) = Pair(w, Lz + new Vector3(40f, 0f, 0f));
        Run(w, 0.2f);
        var run = w.Tfw!;
        var carried = w.Players[Human];
        Kill(w, Human);
        run.ReviveWindow = id => id == Human;      // the fake: TF4 keeps the window open
        Assert.True(run.ReviveWanted(Human));

        float arrived = -1f, clock = 0f, revived = -1f;
        float dt = 1f / w.Config.TickRate;
        while (clock < 20f && revived < 0f)
        {
            Tick(w);
            clock += dt;
            if (arrived < 0f && Flat(w, r, Human) <= MarineRules.ReviveRadius) arrived = clock;
            if (w.Players[Human].Alive) revived = clock;
        }
        Assert.True(arrived > 0f, "it reached the body");
        Assert.True(revived > 0f, "the player came back");
        Assert.InRange(revived - arrived, MarineRules.ReviveSeconds - 0.2f, 6f);
        var back = w.Players[Human];
        var body = Loadouts.Body(w.Config, back);
        Assert.Equal(body.HealthMax * 0.5f, back.Health, 2);
        Assert.Equal(body.ShieldMax * 0.5f, back.Shield, 2);
        Assert.Equal(carried.Position, back.Position);                 // where it fell
        Assert.Equal((carried.Pistol, carried.Slot, carried.Clip), (back.Pistol, back.Slot, back.Clip));   // no new life
    }

    [Fact]
    public void A_closed_window_brings_no_revive()
    {
        var w = Playing(Lz);
        Pair(w, Lz + new Vector3(0f, -1f, 0f));
        Kill(w, Human);
        w.Tfw!.ReviveWindow = _ => false;
        Run(w, 5f);
        Assert.False(w.Players[Human].Alive);
    }

    [Fact]
    public void Leaving_the_body_restarts_the_revive()
    {
        var w = Playing(Lz);
        int r = w.TfwSpawnAlly(Lz + new Vector3(0f, -1f, 0f))!.Value;
        Kill(w, Human);
        w.Tfw!.ReviveWindow = _ => true;
        Run(w, 2f);
        Assert.True(w.Tfw.ReviveProgress(Human, w.Config.TickRate) > 0.4f);
        Place(w, r, Lz + new Vector3(30f, 0f, 0f));
        Tick(w);
        Assert.Equal(0f, w.Tfw.ReviveProgress(Human, w.Config.TickRate));
    }

    [Fact]
    public void An_empty_gun_is_topped_up_so_a_puppet_keeps_fighting()
    {
        var w = Playing(new Vector3(0f, -200f, 0f));
        int r = w.TfwSpawnAlly(Lz)!.Value;
        SetPlayer(w, r, w.Players[r] with { Reserve = 0 });
        Run(w, 0.1f);
        Assert.Equal(Rifle.ReserveSize, w.Players[r].Reserve);
    }

    [Fact]
    public void Dismissing_takes_the_pair_out()
    {
        var w = Playing(Lz);
        var (a, b) = Pair(w, Lz);
        var run = w.Tfw!;
        w.TfwDismissMarines();
        Assert.Empty(run.Marines);
        Assert.False(w.Players.ContainsKey(a) || w.Players.ContainsKey(b));
        Assert.Equal(AllyRules.Count, w.TfwSpawnAllies(Lz));                  // a new battle lands a new pair
    }

    // ---------------------------------------------------------------- the install's classes

    static readonly Lazy<string?> Game = new(() => GameInstall.TryLocate()?.Dir);

    public sealed class InstallFactAttribute : FactAttribute
    {
        public InstallFactAttribute()
        {
            if (Game.Value is null) Skip = "Space Marine install not found (the real classes need the player's install; never in CI)";
        }
    }

    [InstallFact]
    public void The_pair_of_the_real_classes_reaches_the_position_and_kills_boyz()
    {
        var config = GameplayTuning.LoadForMatch(Game.Value!, ContentExtras.Musou).Config;
        var map = TfwTestArena.Build();
        var w = new World(config, map.Collision, map.Spawns) { Rules = Rules };
        Assert.True(w.StartTfw(null, TfwTestArena.Field));
        w.Tfw!.Idle = true;   // the Marines alone
        w.Spawn(Human, 0);
        for (int k = 0; k < 400 && w.State != MatchState.Playing; k++) Tick(w);
        Assert.Equal(MatchState.Playing, w.State);
        Place(w, Human, new Vector3(0f, -200f, 0f));
        var (leader, follower) = Pair(w, Lz);
        w.Tfw.ActivePosition = TfwTestArena.Position1;
        Run(w, 30f);
        Assert.True(MarineBrain.Flat(w.Players[leader].Position, TfwTestArena.Position1) <= MarineRules.ArriveRadius + 1f, $"stopped at {w.Players[leader].Position}");
        for (int k = 0; k < 6; k++) w.Musou!.Spawn(MusouEnemyType.Boy, TfwTestArena.Position1 + new Vector3(4f + k, 5f, 0f), 0f, 85f);
        Run(w, 45f);
        output.WriteLine($"leader kills {Kills(w, leader)}, follower kills {Kills(w, follower)}, left {w.Musou!.Alive}");
        Assert.True(Kills(w, leader) + Kills(w, follower) >= 3, $"{Kills(w, leader)} + {Kills(w, follower)}");
    }

    // ---------------------------------------------------------------- ids

    [Fact]
    public void The_core_reserves_the_war_contracts_ids()
    {
        Assert.Equal(TfwContracts.PuppetIdFirst, GameServer.ReservedPlayerIdFirst);
        Assert.Equal(TfwContracts.PuppetIdLast, GameServer.ReservedPlayerIdLast);
    }

    [Fact]
    public void A_human_never_gets_a_reserved_id_even_with_no_war_running()
    {
        var config = Config with { Extras = ContentExtras.None };
        var h = new Harness(LinkConditions.Perfect, 5, 0, config, new ServerOptions { Match = Rules });
        h.Server.NextPlayerIdForTests = TfwContracts.PuppetIdFirst - 1;
        h.AddClient(nonce: 1);
        h.AddClient(nonce: 2);
        h.Run(TimeSpan.FromSeconds(1));
        Assert.Equal([TfwContracts.PuppetIdFirst - 1, TfwContracts.PuppetIdLast + 1], h.Server.Players.Order());
    }

    // ---------------------------------------------------------------- the server

    [Fact]
    public void The_pair_runs_ten_simulated_minutes_on_a_loopback_server()
    {
        var map = TfwTestArena.ServerMap();
        var h = new Harness(LinkConditions.Perfect, 5, 1, Config, new ServerOptions { Match = Rules }, map: map,
            clientOptions: new ClientOptions { LoadMap = _ => new LoadedMap(map.Collision, map.Hash) });
        h.Run(TimeSpan.FromSeconds(3));
        var w = h.Server.World;
        var run = w.Tfw!;
        run.Idle = true;   // the Marines alone
        int human = Assert.Single(h.Server.Players);
        Assert.Equal(AllyRules.Count, w.TfwSpawnAllies(Lz));
        Assert.Null(w.TfwSpawnAlly(Lz));
        run.ActivePosition = TfwTestArena.Position2;

        uint started = w.Tick;
        var rng = new Random(7);
        for (int second = 0; second < 600; second += 5)
        {
            h.Run(TimeSpan.FromSeconds(5));
            if (second == 300) foreach (var m in run.Marines.ToList()) Kill(w, m.Id, 0.2f);   // deaths on the server too: both back 30 s later
            run.ActivePosition = (second / 60 % 3) switch { 0 => TfwTestArena.Position1, 1 => TfwTestArena.Position2, _ => TfwTestArena.Position3 };
            for (int k = 0; k < 4 && w.Musou!.Alive < 40; k++)
                w.Musou.Spawn(k == 0 ? MusouEnemyType.ShootaBoy : MusouEnemyType.Boy, run.ActivePosition!.Value + new Vector3(rng.NextSingle() * 30f - 15f, rng.NextSingle() * 30f - 15f, 0f), 0f, 85f);
        }

        Assert.True(w.Tick - started >= 600 * w.Config.TickRate - 1, $"{w.Tick - started} ticks");
        Assert.Equal(0, h.Server.Faults);
        Assert.Equal(ClientStatus.Connected, h.Clients[0].Status);
        Assert.Equal(AllyRules.Count, run.Marines.Count);
        output.WriteLine($"{w.Tick - started} ticks, kills {string.Join(", ", run.Marines.Select(m => $"{m.Name} {w.Scores.GetValueOrDefault(m.Id).Kills}/{w.Scores.GetValueOrDefault(m.Id).Deaths}"))}");
        Assert.True(run.Marines.Sum(m => w.Scores.GetValueOrDefault(m.Id).Kills) > 20, "the pair fought");
        Assert.True(run.Marines.All(m => w.Players[m.Id].Alive), "both came back 30 s after their deaths at 300 s");

        var board = h.Server.ScoreboardOf();
        Assert.Equal(["Brother Calix", "Brother Varro"], board.Players.Where(e => TfwContracts.IsPuppet(e.PlayerId)).Select(e => e.Name).Order());
        Assert.Equal("", board.Players.Single(e => e.PlayerId == human).Name);
        h.Run(TimeSpan.FromSeconds(3));   // the scoreboard goes out with them
        Assert.Equal(0, h.Server.Faults);
    }
}
