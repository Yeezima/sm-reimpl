using System.Numerics;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Musou.Campaign;

/// <summary>
/// The players' retainers, restored beside the allied pair (owner 2026-11-08: "return the player retainer bots with orders as well"; they went with a4a4b393): one per human, Follow, Hold and Attack
/// as the world runs them, the revive of the owner, the 180 war-second recovery, and the owner leaving. Ported from the retainer tests before a4a4b393 (the lords' left out: they stay gone).
/// </summary>
public class TfwRetainerTests
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
        w.Tfw!.Idle = true;   // the Marines alone: these tests set the order, the place and the clock themselves (the run would)
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

    [Fact]
    public void A_revive_comes_before_any_order()
    {
        var body = new Vector3(0f, 30f, 0f);
        foreach (var order in new[] { MarineOrder.Follow, MarineOrder.Hold, MarineOrder.Attack })
        {
            var goal = MarineBrain.PlanRetainer(new RetainerSituation(order, Vector3.Zero, body, true, new Vector3(9f, 9f, 0f), new Vector3(-50f, 0f, 0f), new Vector3(5f, 5f, 0f), false));
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
    public void One_retainer_per_player_beside_the_pair()
    {
        var w = Playing(Lz);
        int r = w.TfwSpawnRetainer(Human, Lz + new Vector3(3f, 0f, 0f))!.Value;
        Assert.Null(w.TfwSpawnRetainer(Human, Lz));                          // one each
        Assert.Equal(AllyRules.Count, w.TfwSpawnAllies(Lz));
        var run = w.Tfw!;
        Assert.Equal(AllyRules.Count + 1, run.Marines.Count);
        Assert.Equal(AllyRules.Count, run.Pair.Count);
        Assert.Equal(MarineRole.Retainer, run.MarineOf(r)!.Role);
        Assert.Equal((Human, false), (run.MarineOf(r)!.Owner, run.MarineOf(r)!.Leads));
        Assert.Equal("Retainer", run.PuppetName(r));
        Assert.Equal("tactical", w.Config.Classes[w.Players[r].Class].Role);
        Assert.Equal(1, run.Pair.Count(m => m.Leads));
        Assert.Null(run.PartnerOf(run.MarineOf(r)!));
        Assert.Null(w.TfwSpawnRetainer(run.Pair[0].Id, Lz));                 // a puppet has none
    }

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
        run.ActivePosition = TfwTestArena.Position1;
        var attack = new TfwCommandRecord(1, TfwCommand.Attack, 0);
        run.Enqueue(Human, attack);
        run.Enqueue(Human, attack);
        run.Enqueue(Human, new TfwCommandRecord(2, TfwCommand.Deploy, 1));   // not an order: it stays in the queue for the run
        Run(w, 15f);
        Assert.Equal(MarineOrder.Attack, run.Orders.Of(Human));
        Assert.True(MarineBrain.Flat(w.Players[r].Position, TfwTestArena.Position1) <= MarineRules.ArriveRadius);
        Assert.Equal(1, run.QueuedCommands);
        Assert.True(run.TryDequeue(out _, out var left));
        Assert.Equal(TfwCommand.Deploy, left.Command);
    }

    [Fact]
    public void A_player_orders_only_its_own_retainer_and_never_the_pair()
    {
        var w = Playing(Lz);
        int r = w.TfwSpawnRetainer(Human, Lz)!.Value;
        const int other = 2;
        w.Spawn(other, 0);
        int theirs = w.TfwSpawnRetainer(other, Lz + new Vector3(3f, 0f, 0f))!.Value;
        int ally = w.TfwSpawnAlly(Lz + new Vector3(-3f, 0f, 0f))!.Value;
        Assert.True(w.TfwOrderMarines(Human, new TfwCommandRecord(1, TfwCommand.Hold, 0)));
        Assert.True(w.TfwOrderMarines(Human, new TfwCommandRecord(2, TfwCommand.Attack, 0, Lz + new Vector3(0f, 30f, 0f))));
        var run = w.Tfw!;
        Assert.Equal(MarineOrder.Attack, run.Orders.Of(Human));
        Assert.Equal(MarineOrder.Follow, run.Orders.Of(other));
        Assert.Equal(Lz, run.MarineOf(r)!.HoldAt);
        Assert.Equal(Lz + new Vector3(0f, 30f, 0f), run.MarineOf(r)!.AttackAt);
        Assert.Equal(new Vector3(3f, 0f, 0f) + Lz, run.MarineOf(theirs)!.HoldAt);
        Assert.Null(run.MarineOf(theirs)!.AttackAt);
        Assert.Null(run.MarineOf(ally)!.AttackAt);
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
        run.ReviveWindow = id => id == Human;      // the fake: the run keeps the window open
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

    // ---------------------------------------------------------------- the run (the real TF4)

    [Fact]
    public void A_deploy_lands_a_retainer_per_human_and_exactly_two_allied_marines_and_the_retainer_revives()
    {
        var w = TfwRig.War();
        var run = w.Tfw!;
        run.AllyUp = null;
        foreach (var m in new[] { 2, 3 }) w.Spawn(m, w.AssignClass(0));
        TfwRig.Steps(w, 2);
        TfwRig.Deploy(w);
        Assert.Equal(3, run.InField.Count);
        Assert.Equal(AllyRules.Count, run.Pair.Count);
        Assert.Equal(AllyRules.Count + 3, run.Marines.Count);
        Assert.All(run.InField, id => Assert.Equal(id, run.RetainerOf(id)!.Owner));
        Assert.Equal(TfwRetainerState.Ready, w.TfwOwn(TfwRig.Human).Retainer);
        foreach (var m in run.Pair.ToList()) Assert.True(w.TfwKillForTests(m.Id));   // the pair down: the retainer alone keeps the window open
        TfwRig.Steps(w, 2);
        Assert.True(w.TfwKillForTests(TfwRig.Human));
        TfwRig.Steps(w, 2);
        Assert.True(run.ReviveWanted(TfwRig.Human));
        for (int t = 0; t < 25 * 30 && !w.Players[TfwRig.Human].Alive; t++) w.Step(_ => default);
        Assert.True(w.Players[TfwRig.Human].Alive, "the retainer came and revived");
    }
}
