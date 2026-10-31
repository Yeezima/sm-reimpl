using System.Numerics;
using SmContent;
using SmServer;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Musou.Campaign;

/// <summary>
/// The AI Marines (TF5): the brain's decisions, Follow, Hold and Attack as the world runs them, the revive against a fake of the run (TF4 is not in yet), the lords, the reserved ids, and
/// a loopback server with 7 puppets for 10 simulated minutes.
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
    static readonly Vector3 Lz = TfwMap.LandingZone;

    static World War()
    {
        var map = TfwMap.Build();
        var w = new World(Config, map.Collision, map.Spawns) { Rules = Rules };
        Assert.True(w.StartTfw(null));
        w.Tfw!.Idle = true;   // the Marines alone: these tests set the order, the place and the clock themselves (TF4's run would)
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
    public void A_revive_comes_before_any_order()
    {
        var body = new Vector3(0f, 30f, 0f);
        foreach (var order in new[] { MarineOrder.Follow, MarineOrder.Hold, MarineOrder.Attack })
        {
            var goal = MarineBrain.Plan(new MarineSituation(order, Vector3.Zero, body, true, new Vector3(9f, 9f, 0f), new Vector3(-50f, 0f, 0f), new Vector3(5f, 5f, 0f), false));
            Assert.Equal(body, goal!.Value.Point);
            Assert.True(goal.Value.Sprint);
        }
    }

    [Fact]
    public void Orders_are_deduplicated_by_their_sequence()
    {
        var o = new MarineOrders();
        Assert.Equal(MarineOrder.Follow, o.Of(Human));
        Assert.True(o.Apply(Human, new TfwCommandRecord(65535, TfwCommand.Hold, 0)));
        Assert.False(o.Apply(Human, new TfwCommandRecord(65535, TfwCommand.Attack, 0)));   // a resend
        Assert.False(o.Apply(Human, new TfwCommandRecord(65534, TfwCommand.Attack, 0)));   // older
        Assert.Equal(MarineOrder.Hold, o.Of(Human));
        Assert.True(o.Apply(Human, new TfwCommandRecord(0, TfwCommand.Attack, 0)));        // wraps
        Assert.Equal((MarineOrder.Attack, (ushort)0), (o.Of(Human), o.Echo(Human)));
        Assert.False(o.Apply(Human, new TfwCommandRecord(1, TfwCommand.Deploy, 0)));
    }

    // ---------------------------------------------------------------- the world: orders

    [Fact]
    public void Follow_keeps_four_to_eight_metres_of_a_moving_owner()
    {
        var w = Playing(Lz);
        int r = w.TfwSpawnRetainer(Human, Lz + new Vector3(25f, 0f, 0f))!.Value;
        Assert.Equal(TfwContracts.PuppetIdFirst, r);
        Run(w, 6f);
        Assert.InRange(Flat(w, r, Human), MarineRules.FollowMin, MarineRules.FollowMax);

        Func<PlayerState, PlayerInput> walk = _ => new PlayerInput(0, 0f, 1f, 0f, InputButtons.None);
        Run(w, 3f, walk);   // settling behind him
        float min = float.MaxValue, max = 0f;
        Run(w, 10f, walk, () => { float d = Flat(w, r, Human); min = MathF.Min(min, d); max = MathF.Max(max, d); });
        Assert.True(min >= MarineRules.FollowMin && max <= MarineRules.FollowMax, $"{min} to {max}");

        Place(w, r, w.Players[Human].Position + new Vector3(0.5f, 0.5f, 0f));   // too near: it backs off
        Run(w, 3f);
        Assert.InRange(Flat(w, r, Human), MarineRules.FollowMin, MarineRules.FollowMax);
    }

    [Fact]
    public void Follow_shoots_within_twenty_metres_without_leaving_the_owner()
    {
        var w = Playing(Lz);
        int r = w.TfwSpawnRetainer(Human, Lz + new Vector3(0f, -5f, 0f))!.Value;
        var musou = w.Musou!;
        ushort far = musou.Spawn(MusouEnemyType.Boy, w.Players[r].Position + new Vector3(30f, 0f, 0f), 0f, 1f);
        Run(w, 0.5f);
        Assert.True(w.Tfw!.MarineOf(r)!.Target == 0 || w.Tfw.MarineOf(r)!.Target != far);
        musou.Clear();
        musou.Spawn(MusouEnemyType.Boy, w.Players[r].Position + new Vector3(15f, 0f, 0f), 0f, 85f);
        Run(w, 5f);
        Assert.True(Kills(w, r) >= 1);
        Assert.InRange(Flat(w, r, Human), 0f, MarineRules.FollowMax + 8f);   // it may give ground to a Boy on it, but stays by him
    }

    [Fact]
    public void A_puppets_shot_counts_once_in_the_tick()
    {
        var w = Playing(Lz);
        int r = w.TfwSpawnRetainer(Human, Lz + new Vector3(5f, 0f, 0f))!.Value;
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
    public void Hold_stays_where_ordered_and_engages_within_its_radius()
    {
        var w = Playing(Lz);
        int r = w.TfwSpawnRetainer(Human, Lz + new Vector3(0f, -6f, 0f))!.Value;
        Run(w, 0.5f);
        Assert.True(w.Tfw!.Enqueue(Human, new TfwCommandRecord(1, TfwCommand.Hold, 0)));
        Run(w, 0.1f);
        Assert.Equal(MarineOrder.Hold, w.Tfw.Orders.Of(Human));
        var post = w.Tfw.MarineOf(r)!.HoldAt;
        Func<PlayerState, PlayerInput> walk = _ => new PlayerInput(0, 0f, 1f, 0f, InputButtons.None);
        Run(w, 6f, walk);   // the owner walks away; it stays
        Assert.True(Flat(w, r, Human) > 20f);
        Assert.True(MarineBrain.Flat(w.Players[r].Position, post) <= MarineRules.HoldStand + 0.5f);
        w.Musou!.Spawn(MusouEnemyType.Boy, post + new Vector3(20f, 0f, 0f), 0f, 85f);
        Run(w, 4f);
        Assert.True(Kills(w, r) >= 1);
    }

    [Fact]
    public void Attack_goes_to_the_active_position_and_a_resend_is_ignored()
    {
        var w = Playing(Lz);
        int r = w.TfwSpawnRetainer(Human, Lz + new Vector3(0f, -6f, 0f))!.Value;
        var run = w.Tfw!;
        run.ActivePosition = TfwMap.Position1;
        var attack = new TfwCommandRecord(1, TfwCommand.Attack, 0);
        run.Enqueue(Human, attack);
        run.Enqueue(Human, attack);
        run.Enqueue(Human, new TfwCommandRecord(2, TfwCommand.Deploy, 1));   // not an order: it stays in the queue for the run
        Run(w, 15f);
        Assert.Equal(MarineOrder.Attack, run.Orders.Of(Human));
        Assert.True(MarineBrain.Flat(w.Players[r].Position, TfwMap.Position1) <= MarineRules.ArriveRadius);
        Assert.Equal(1, run.QueuedCommands);
        Assert.True(run.TryDequeue(out _, out var left));
        Assert.Equal(TfwCommand.Deploy, left.Command);
    }

    [Fact]
    public void A_player_orders_only_its_own_retainer()
    {
        var w = Playing(Lz);
        int r = w.TfwSpawnRetainer(Human, Lz)!.Value;
        int lord = w.TfwSpawnLord(TfwLane.B, Lz)!.Value;
        const int other = 2;
        w.Spawn(other, 0);
        int theirs = w.TfwSpawnRetainer(other, Lz + new Vector3(3f, 0f, 0f))!.Value;
        w.TfwOrderMarines(Human, new TfwCommandRecord(1, TfwCommand.Hold, 0));
        var run = w.Tfw!;
        Assert.Equal(MarineOrder.Hold, run.Orders.Of(Human));
        Assert.Equal(MarineOrder.Follow, run.Orders.Of(other));
        Assert.Equal(Lz, run.MarineOf(r)!.HoldAt);
        Assert.Equal(new Vector3(3f, 0f, 0f) + Lz, run.MarineOf(theirs)!.HoldAt);
        var lordRetainer = run.Marines.Single(m => m.Role == MarineRole.LordRetainer);
        Assert.Equal(lord, lordRetainer.Owner);
    }

    // ---------------------------------------------------------------- the revive, against a fake of the run

    [Fact]
    public void A_retainer_revives_within_six_seconds_of_arrival_at_half_health_and_shield()
    {
        var w = Playing(Lz);
        int r = w.TfwSpawnRetainer(Human, Lz + new Vector3(40f, 0f, 0f))!.Value;
        w.TfwOrderMarines(Human, new TfwCommandRecord(1, TfwCommand.Hold, 0));   // an order does not stop it
        Run(w, 0.2f);
        var run = w.Tfw!;
        var carried = w.Players[Human];
        Kill(w, Human);
        run.ReviveWindow = id => id == Human;      // the fake: TF4 keeps the window open
        Assert.True(run.ReviveWanted(Human));

        float arrived = -1f, clock = 0f, revived = -1f;
        float dt = 1f / w.Config.TickRate;
        while (clock < 15f && revived < 0f)
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
        Run(w, 0.5f);
        Assert.True(w.Players[Human].Alive);
        Assert.Equal(carried.Slot, w.Players[Human].Slot);
    }

    [Fact]
    public void A_closed_window_brings_no_revive()
    {
        var w = Playing(Lz);
        w.TfwSpawnRetainer(Human, Lz + new Vector3(0f, -1f, 0f));
        Kill(w, Human);
        w.Tfw!.ReviveWindow = _ => false;
        Run(w, 5f);
        Assert.False(w.Players[Human].Alive);
    }

    [Fact]
    public void Leaving_the_body_restarts_the_revive()
    {
        var w = Playing(Lz);
        int r = w.TfwSpawnRetainer(Human, Lz + new Vector3(0f, -1f, 0f))!.Value;
        Kill(w, Human);
        w.Tfw!.ReviveWindow = _ => true;
        Run(w, 2f);
        Assert.True(w.Tfw.ReviveProgress(Human, w.Config.TickRate) > 0.4f);
        Place(w, r, Lz + new Vector3(30f, 0f, 0f));
        Tick(w);
        Assert.Equal(0f, w.Tfw.ReviveProgress(Human, w.Config.TickRate));
    }

    [Fact]
    public void A_dead_retainer_recovers_three_war_minutes_on_the_war_clock()
    {
        var w = Playing(Lz);
        int r = w.TfwSpawnRetainer(Human, Lz)!.Value;
        var run = w.Tfw!;
        run.WarTimeScale = 18f;   // 180 war seconds in 10 s
        Kill(w, r, 0.5f);
        Run(w, 5f);
        Assert.False(w.Players[r].Alive);                        // the core never respawns it: the timer is held
        Assert.InRange(run.RecoveryLeft(Human), 60f, 120f);
        Assert.Null(w.TfwSpawnRetainer(Human, Lz));              // one retainer
        Run(w, 5.5f);
        Assert.True(w.Players[r].Alive);
        Assert.Equal(0f, run.RecoveryLeft(Human));
    }

    [Fact]
    public void A_retainers_owner_leaving_takes_it_out_and_a_recovery_outlives_the_battle()
    {
        var w = Playing(Lz);
        int r = w.TfwSpawnRetainer(Human, Lz)!.Value;
        var run = w.Tfw!;
        Kill(w, r);
        Run(w, 0.2f);
        w.TfwDismissMarines();
        Assert.Empty(run.Marines);
        Assert.False(w.Players.ContainsKey(r));
        Assert.True(run.RecoveryLeft(Human) > 0f);
        Assert.Null(w.TfwSpawnRetainer(Human, Lz));              // not back before its time
        run.WarTimeScale = 1000f;
        Run(w, 0.5f);
        r = w.TfwSpawnRetainer(Human, Lz)!.Value;
        w.Remove(Human);
        Tick(w);
        Assert.False(w.Players.ContainsKey(r));
        Assert.Empty(run.Marines);
    }

    // ---------------------------------------------------------------- the lords

    [Fact]
    public void A_lord_reaches_the_active_position_and_kills_boyz()
    {
        foreach (var lane in new[] { TfwLane.A, TfwLane.B, TfwLane.C })
        {
            var w = Playing(new Vector3(0f, -200f, 0f));   // the player stays in the Stormbird: the lord acts alone
            int lord = w.TfwSpawnLord(lane, Lz)!.Value;
            var run = w.Tfw!;
            Assert.Equal(lane switch { TfwLane.A => "assault", TfwLane.B => "tactical", _ => "heavy" }, w.Config.Classes[w.Players[lord].Class].Role);
            run.ActivePosition = TfwMap.Position1;
            Run(w, 20f);
            Assert.True(MarineBrain.Flat(w.Players[lord].Position, TfwMap.Position1) <= MarineRules.ArriveRadius, $"{lane} stopped at {w.Players[lord].Position}");
            var marines = run.Marines.Select(m => m.Id).ToList();
            for (int k = 0; k < 6; k++)
                w.Musou!.Spawn(MusouEnemyType.Boy, TfwMap.Position1 + new Vector3(4f + k, 5f, 0f), 0f, 85f);
            Run(w, 30f);
            Assert.True(Kills(w, lord) >= 1, $"lord of {lane} killed none");
            Assert.True(marines.Sum(id => Kills(w, id)) >= 4, $"{lane}: {marines.Sum(id => Kills(w, id))} kills");
        }
    }

    [Fact]
    public void One_lord_per_lane_named_as_the_design_with_a_retainer_each()
    {
        var w = Playing(Lz);
        foreach (var lane in new[] { TfwLane.A, TfwLane.B, TfwLane.C }) Assert.NotNull(w.TfwSpawnLord(lane, Lz));
        Assert.Null(w.TfwSpawnLord(TfwLane.A, Lz));
        var run = w.Tfw!;
        Assert.Equal(6, run.Marines.Count);
        Assert.Equal(["Brother-Captain Aurex", "Retainer of Aurex", "Brother-Captain Tolmar", "Retainer of Tolmar", "Brother-Captain Severin", "Retainer of Severin"],
            run.Marines.Select(m => run.PuppetName(m.Id)));
    }

    [Fact]
    public void A_fallen_lord_goes_to_the_war_once_and_stays_down_and_his_retainer_fights_on()
    {
        var w = Playing(new Vector3(0f, -200f, 0f));
        int lord = w.TfwSpawnLord(TfwLane.C, Lz)!.Value;
        var run = w.Tfw!;
        run.ActivePosition = TfwMap.Position1;
        var fallen = new List<TfwLane>();
        run.LordFallen = fallen.Add;
        Kill(w, lord, 0.5f);
        Run(w, 10f);
        Assert.Equal([TfwLane.C], fallen);
        Assert.False(w.Players[lord].Alive);
        var retainer = run.Marines.Single(m => m.Role == MarineRole.LordRetainer).Id;
        Assert.True(MarineBrain.Flat(w.Players[retainer].Position, Lz) > 20f, "it went on to the objective");
    }

    [Fact]
    public void An_empty_gun_is_topped_up_so_a_puppet_keeps_fighting()
    {
        var w = Playing(new Vector3(0f, -200f, 0f));
        int r = w.TfwSpawnRetainer(Human, Lz)!.Value;
        SetPlayer(w, r, w.Players[r] with { Reserve = 0 });
        Run(w, 0.1f);
        Assert.Equal(Rifle.ReserveSize, w.Players[r].Reserve);
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
    public void Each_lord_of_the_real_classes_reaches_the_position_and_kills_boyz()
    {
        var config = GameplayTuning.LoadForMatch(Game.Value!, ContentExtras.Musou).Config;
        foreach (var lane in new[] { TfwLane.A, TfwLane.B, TfwLane.C })
        {
            var map = TfwMap.Build();
            var w = new World(config, map.Collision, map.Spawns) { Rules = Rules };
            Assert.True(w.StartTfw(null));
            w.Tfw!.Idle = true;   // the Marines alone: these tests set the order, the place and the clock themselves (TF4's run would)
            w.Spawn(Human, 0);
            for (int k = 0; k < 400 && w.State != MatchState.Playing; k++) Tick(w);
            Assert.Equal(MatchState.Playing, w.State);
            int lord = w.TfwSpawnLord(lane, Lz)!.Value;
            int retainer = w.Tfw!.Marines.Single(m => m.Role == MarineRole.LordRetainer).Id;
            w.Tfw.ActivePosition = TfwMap.Position1;
            Run(w, 25f);
            Assert.True(MarineBrain.Flat(w.Players[lord].Position, TfwMap.Position1) <= MarineRules.ArriveRadius, $"{lane} stopped at {w.Players[lord].Position}");
            for (int k = 0; k < 6; k++) w.Musou!.Spawn(MusouEnemyType.Boy, TfwMap.Position1 + new Vector3(4f + k, 5f, 0f), 0f, 85f);
            Run(w, 45f);
            var p = w.Players[lord];
            output.WriteLine($"{lane}: {config.Classes[p.Class].Name}, primary {p.Primary}, slot {p.Slot}, alive {p.Alive}, lord kills {Kills(w, lord)}, retainer kills {Kills(w, retainer)}, left {w.Musou!.Alive}");
            Assert.True(Kills(w, lord) + Kills(w, retainer) >= 3, $"{lane}: {Kills(w, lord)} + {Kills(w, retainer)}");
            Assert.True(Kills(w, lord) >= 1, $"lord of {lane} killed none");
        }
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
    public void Seven_puppets_run_ten_simulated_minutes_on_a_loopback_server()
    {
        string? name = null;
        var map = GameServer.GeneratedMap(ContentExtras.Musou, ref name, out _)!;
        var h = new Harness(LinkConditions.Perfect, 5, 1, Config, new ServerOptions { Match = Rules }, map: map,
            clientOptions: new ClientOptions { LoadMap = _ => new LoadedMap(map.Collision, map.Hash) });
        h.Run(TimeSpan.FromSeconds(3));
        var w = h.Server.World;
        var run = w.Tfw!;
        run.Idle = true;   // the Marines alone
        int human = Assert.Single(h.Server.Players);
        Assert.NotNull(w.TfwSpawnRetainer(human, Lz));
        foreach (var lane in new[] { TfwLane.A, TfwLane.B, TfwLane.C }) Assert.NotNull(w.TfwSpawnLord(lane, Lz + new Vector3((int)lane * 4f - 4f, 0f, 0f)));
        Assert.Equal(7, run.Marines.Count);
        Assert.Null(w.TfwSpawnLord(TfwLane.A, Lz));
        run.ActivePosition = TfwMap.Position2;
        run.WarTimeScale = 1f;

        uint started = w.Tick;
        var rng = new Random(7);
        for (int second = 0; second < 600; second += 5)
        {
            h.Run(TimeSpan.FromSeconds(5));
            if (second == 300)   // deaths on the server too: a lord stays down, a retainer comes back on the war clock
                foreach (var m in run.Marines.Where(m => m.Role != MarineRole.LordRetainer || m.Lane == TfwLane.A).ToList()) if (m.Role != MarineRole.Lord || m.Lane == TfwLane.B) Kill(w, m.Id, 0.2f);
            run.ActivePosition = (second / 60 % 3) switch { 0 => TfwMap.Position1, 1 => TfwMap.Position2, _ => TfwMap.Position3 };
            for (int k = 0; k < 4 && w.Musou!.Alive < 40; k++)
                w.Musou.Spawn(k == 0 ? MusouEnemyType.ShootaBoy : MusouEnemyType.Boy, run.ActivePosition!.Value + new Vector3(rng.NextSingle() * 30f - 15f, rng.NextSingle() * 30f - 15f, 0f), 0f, 85f);
        }

        Assert.True(w.Tick - started >= 600 * w.Config.TickRate - 1, $"{w.Tick - started} ticks");
        Assert.Equal(0, h.Server.Faults);
        Assert.Equal(ClientStatus.Connected, h.Clients[0].Status);
        Assert.Equal(7, run.Marines.Count);
        Assert.Equal(1, run.Marines.Count(m => !w.Players[m.Id].Alive));   // Tolmar; the retainers are back (180 war s < 300 s left)
        output.WriteLine($"{w.Tick - started} ticks, kills {string.Join(", ", run.Marines.Select(m => $"{m.Name} {w.Scores.GetValueOrDefault(m.Id).Kills}/{w.Scores.GetValueOrDefault(m.Id).Deaths}"))}");
        Assert.True(run.Marines.Sum(m => w.Scores.GetValueOrDefault(m.Id).Kills) > 50, "the puppets fought");

        var board = h.Server.ScoreboardOf();
        string[] names = ["Brother-Captain Aurex", "Retainer of Aurex", "Brother-Captain Tolmar", "Retainer of Tolmar", "Brother-Captain Severin", "Retainer of Severin", "Retainer"];
        Assert.Equal(names.Order(), board.Players.Where(e => TfwContracts.IsPuppet(e.PlayerId)).Select(e => e.Name).Order());
        Assert.Equal("", board.Players.Single(e => e.PlayerId == human).Name);
        h.Run(TimeSpan.FromSeconds(3));   // the scoreboard goes out with them
        Assert.Equal(0, h.Server.Faults);
    }
}
