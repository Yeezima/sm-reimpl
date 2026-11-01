using System.Diagnostics;
using System.Numerics;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Musou.Campaign;

/// <summary>The Guardsmen (TF3, design section E): the squads against the Orks through the World, negative targets, formation, shooting and the cost.</summary>
public class GuardSimTests(ITestOutputHelper output)
{
    static readonly WeaponSpec Rifle = new("rifle", WeaponSlot.Ranged, TriggerKind.FullAuto, 9f, 0.12f, 30, 270, 1.2f, 250f, 45f, 70f, 0.8f, 2.5f, 1f, 0.5f, 0.5f);
    static readonly WeaponSpec Pistol = new("pistol", WeaponSlot.Pistol, TriggerKind.SemiAuto, 20f, 0.3f, 8, 64, 1.2f, 120f, 30f, 60f, 0.5f, 3f, 1f, 0.6f, 0.6f);
    static readonly MeleeStep Light = new(1f, 0.2f, 0.4f, 0.6f, 0.5f, 0.7f, 25f, 10f, 3.5f);
    static ClassLoadout Class(string name, Team side) => new(name, -1, 1, [Light]) { PrimaryChoices = [0], PistolChoices = [1], Side = side, Role = "tactical" };
    static readonly SimConfig Config = new()
    {
        Weapons = [Rifle, Pistol], Classes = [Class("sm - tactical", Team.SpaceMarines), Class("csm - tactical", Team.Chaos)], Extras = ContentExtras.Musou,
    };
    static MatchRules Rules => new() { CountdownTime = 0.1f, TimeLimit = 0f, ScoreLimit = int.MaxValue / 2 };

    /// <summary>The box: a floor with its graph, the one player far in a corner so the Orks go for the squads; in play.</summary>
    static World Box(float half = 60f)
    {
        var mesh = new NavigationTests.Level().Floor(-half, -half, half, half).Mesh();
        var collision = new CollisionWorld(mesh, mesh);
        var w = new World(Config, collision, [new SpawnPoint(new Vector3(-half + 2f, -half + 2f, 0f), 0f)]) { Rules = Rules };
        Assert.True(w.StartTfw(NavGraph.Build(mesh, null, NavSettings.From(Config)), TfwTestArena.Field));
        w.Spawn(1, w.AssignClass(0));
        for (int k = 0; k < 60 && w.State != MatchState.Playing; k++) w.Step(_ => default);
        Assert.Equal(MatchState.Playing, w.State);
        return w;
    }

    static List<ushort> Boyz(World w, int n, Vector3 at, int perRow = 10)
    {
        var ids = new List<ushort>();
        for (int k = 0; k < n; k++)
        {
            ushort id = w.Musou!.Spawn(MusouEnemyType.Boy, at + new Vector3((k % perRow) * 1.5f, (k / perRow) * 1.5f, 0f), MathF.PI, 85f);
            Assert.NotEqual((ushort)0, id);
            ids.Add(id);
        }
        return ids;
    }

    readonly record struct Battle(int Ticks, int Lost, int SquadsLeft, int Shots, int ShotsHit, int Strikes, int Tracers);

    static Battle SixSquadsAgainstSixtyBoyz(Action<string>? Log = null)
    {
        var w = Box();
        var guards = w.TfwGuards!;
        for (int s = 0; s < 6; s++) Assert.Equal(s, guards.SpawnSquad(new Vector3(-20f + s * 8f, -10f, 0f)));
        Boyz(w, 60, new Vector3(-7f, 30f, 0f));
        int strikes = 0, tick = 0;
        for (; tick < 90 * 30 && w.Musou!.Alive > 0; tick++)
        {
            w.Step(_ => default);
            foreach (var e in w.Musou.Events)
                if (e.Kind == MusouEventKind.Strike && e.Player == 0) strikes++;   // a squad is nobody on the player channel
            if (tick % 150 == 0) Log?.Invoke($"{tick / 30f:0} s: boyz {w.Musou.Alive}, guards {guards.Alive}, squads {guards.SquadsAlive}, player health {w.Players[1].Health}");
        }
        return new Battle(tick, guards.Lost, guards.SquadsAlive, guards.Shots, guards.ShotsHit, strikes, w.Musou!.Alive);
    }

    [Fact]
    public void Six_squads_beat_sixty_boyz_in_the_box_within_90_s_with_deterministic_losses()
    {
        var a = SixSquadsAgainstSixtyBoyz(output.WriteLine);
        output.WriteLine($"{a} = {a.Ticks / 30f:0.0} s");
        Assert.Equal(0, a.Tracers);                 // no Boy left
        Assert.True(a.Ticks < 90 * 30, a.ToString());
        Assert.True(a.Strikes > 0, "the Orks struck");
        Assert.True(a.Lost is > 0 and < 60, a.ToString());
        Assert.Equal(a, SixSquadsAgainstSixtyBoyz());
    }

    [Fact]
    public void An_orks_strike_goes_through_musou_hit_to_the_nearest_guardsman()
    {
        var w = Box();
        var guards = w.TfwGuards!;
        int squad = guards.SpawnSquad(new Vector3(10f, 10f, 0f));
        var boy = Boyz(w, 1, new Vector3(10f, 13f, 0f))[0];
        var before = new TfwGuard[GuardSim.Capacity];
        Assert.Equal(10, guards.CopyTo(before));
        bool struck = false;
        for (int k = 0; k < 150 && !struck; k++)
        {
            w.Step(_ => default);
            foreach (var e in guards.Events) struck |= e.Kind == GuardEventKind.Hurt;
            if (struck)
            {
                Assert.Equal(TfwContracts.GuardTargetId(squad), w.Musou!.TargetOf(boy));
                Assert.True(w.Musou.TryGet(boy, out var view));
                Assert.Equal(0, view.TargetPlayer);   // the public view shows players only
                Assert.Contains(w.Musou.Events, e => e.Kind == MusouEventKind.Strike && e.Enemy == boy && e.Player == 0);
            }
        }
        Assert.True(struck, "the strike reached a Guardsman");
        Assert.Equal(Loadouts.Body(Config, w.Players[1]).HealthMax, w.Players[1].Health);   // and no player
    }

    [Fact]
    public void A_negative_target_survives_every_tick_and_a_token_is_never_lost_to_a_retarget()
    {
        var sim = new MusouSim(null);
        var id = sim.Spawn(MusouEnemyType.Boy, new Vector3(0f, 3f, 0f), 0f, 85f);
        MusouTarget[] targets = [new(TfwContracts.GuardTargetId(0), Vector3.Zero, true)];
        var attacks = new List<MusouAttack>();
        int strikes = 0;
        bool held = false;
        for (uint k = 1; k <= 300; k++)
        {
            sim.BeginTick(k);
            sim.Step(targets, null, true, attacks);
            if (k > 20)
            {
                Assert.Equal(TfwContracts.GuardTargetId(0), sim.TargetOf(id));
                Assert.True(sim.TryGet(id, out var e));
                if (held) Assert.True(e.AttackToken == MusouSim.MeleeToken, $"token lost at tick {k}");   // a lost target would drop it
                held |= e.AttackToken == MusouSim.MeleeToken;
            }
            foreach (var a in attacks) { strikes++; Assert.Equal(TfwContracts.GuardTargetId(0), a.Player); Assert.True(TfwContracts.IsGuardTarget(a.Player)); }
            attacks.Clear();
        }
        Assert.True(held);
        Assert.True(strikes >= 6, $"{strikes} strikes in 10 s");   // one every 1.4 s after the walk in
    }

    [Fact]
    public void Shooters_write_player_0_into_tracers_at_a_squad()
    {
        var sim = new MusouSim(null);
        sim.Spawn(MusouEnemyType.ShootaBoy, new Vector3(0f, 15f, 0f), 0f, 85f);
        MusouTarget[] targets = [new(-3, Vector3.Zero, true)];
        var attacks = new List<MusouAttack>();
        int tracers = 0;
        for (uint k = 1; k <= 300; k++)
        {
            sim.BeginTick(k);
            sim.Step(targets, null, true, attacks);
            foreach (var e in sim.Events)
                if (e.Kind is MusouEventKind.Tracer or MusouEventKind.Telegraph or MusouEventKind.Strike) { tracers++; Assert.Equal(0, e.Player); }
        }
        Assert.True(tracers > 0);
        Assert.NotEmpty(attacks);
        Assert.All(attacks, a => Assert.Equal(-3, a.Player));
    }

    [Fact]
    public void A_squad_stands_in_two_rows_of_five_and_walks_to_its_order()
    {
        var sim = new MusouSim(null);
        var guards = new GuardSim(sim);
        int s = guards.SpawnSquad(new Vector3(0f, 0f, 0f));
        var men = new TfwGuard[10];
        Assert.Equal(10, guards.CopyTo(men));
        Assert.Equal(6f, men.Max(m => m.Position.X) - men.Min(m => m.Position.X), 3);   // 4 gaps of 1.5 m
        Assert.Equal(1.5f, men.Max(m => m.Position.Y) - men.Min(m => m.Position.Y), 3);
        Assert.True(guards.Order(s, new Vector3(0f, 30f, 0f)));
        uint k = 1;
        for (; k < 300 && !guards.Arrived(s); k++) { guards.BeginTick(k); guards.Step(true); }
        Assert.True(guards.Arrived(s));
        Assert.InRange(k / 30f, 30f / 4.5f, 30f / 4.5f / 0.9f + 0.3f);   // the men's 4.5 m/s, the squad a little under
        for (int t = 0; t < 30; t++) { guards.BeginTick(k + (uint)t); guards.Step(true); }
        Assert.InRange(guards.Centre(s).Y, 29.5f, 30.5f);
        Assert.Equal(10, guards.CountInside(new Vector3(0f, 30f, 0f), 5f));
    }

    [Fact]
    public void An_order_it_cannot_walk_to_ends_and_the_next_one_works()
    {
        var mesh = new NavigationTests.Level().Floor(-10f, -10f, 10f, 10f).Mesh();
        var sim = new MusouSim(NavGraph.Build(mesh, null, NavSettings.From(Config)));
        Assert.NotNull(sim.Walk);
        var guards = new GuardSim(sim);
        int s = guards.SpawnSquad(Vector3.Zero);
        Assert.True(guards.Order(s, new Vector3(0f, 100f, 0f)));   // off the floor
        uint k = 1;
        for (; k <= 300 && !guards.Arrived(s); k++) { guards.BeginTick(k); guards.Step(false); }
        Assert.True(guards.Arrived(s), "still walking at the edge of the floor");
        Assert.InRange(k / 30f, 1f, 10f / 4.05f + 1.5f);           // the walk to the edge, plus the 1 s it gives it
        Assert.True(guards.Centre(s).Y > 5f, $"{guards.Centre(s)}");
        Assert.True(guards.Order(s, new Vector3(0f, 0f, 0f)));
        Assert.False(guards.Arrived(s));
        for (; k <= 600 && !guards.Arrived(s); k++) { guards.BeginTick(k); guards.Step(false); }
        Assert.True(guards.Arrived(s));
        Assert.Equal(10, guards.CountInside(Vector3.Zero, 6f));
    }

    [Fact]
    public void Squad_0_is_not_the_flow_fields_mark_for_an_unreached_node()
    {
        Assert.True(TfwContracts.GuardTargetId(0) < -1);
        Assert.False(TfwContracts.IsGuardTarget(-1));
        Assert.False(TfwContracts.IsGuardTarget(0));
        Assert.Equal(0, TfwContracts.SquadOf(TfwContracts.GuardTargetId(0)));
    }

    [Fact]
    public void A_blow_lands_on_the_nearest_man_and_a_wiped_squad_goes_and_frees_its_slots()
    {
        var sim = new MusouSim(null);
        var guards = new GuardSim(sim);
        int s = guards.SpawnSquad(Vector3.Zero);
        Assert.False(guards.Hit(s + 1, 5f, Vector3.Zero));
        Assert.False(guards.Hit(s, 0f, Vector3.Zero));
        var from = new Vector3(-20f, 0f, 0f);
        for (int k = 0; k < 17; k++) Assert.True(guards.Hit(s, 5f, from));
        Assert.Equal(9, guards.AliveIn(s));   // the 17th blow of 5 kills the nearest man of 85
        Assert.Equal(1, guards.Lost);
        var targets = new MusouTarget[GuardSim.MaxSquads];
        Assert.Equal(1, guards.AppendTargets(targets));
        Assert.Equal(TfwContracts.GuardTargetId(s), targets[0].Id);
        for (int k = 0; k < 9; k++) Assert.True(guards.Hit(s, 85f, from));
        Assert.Equal(0, guards.AliveIn(s));
        Assert.False(guards.Hit(s, 85f, from));
        Assert.Equal(0, guards.AppendTargets(targets));
        Assert.Equal(-1, guards.NearestSquad(from));
        for (uint k = 1; k <= 70; k++) { guards.BeginTick(k); guards.Step(true); }
        Assert.Equal(0, guards.Count);
        Assert.Equal(s, guards.SpawnSquad(Vector3.Zero));   // the slots are free
    }

    [Fact]
    public void The_pool_holds_24_squads_and_refuses_the_bad_ones()
    {
        var guards = new GuardSim(new MusouSim(null));
        Assert.Equal(-1, guards.SpawnSquad(new Vector3(float.NaN, 0f, 0f)));
        for (int s = 0; s < GuardSim.MaxSquads; s++) Assert.Equal(s, guards.SpawnSquad(new Vector3(s * 10f, 0f, 0f)));
        Assert.Equal(-1, guards.SpawnSquad(Vector3.Zero));
        Assert.Equal(GuardSim.Capacity, guards.Alive);
        Assert.False(guards.Order(99, Vector3.Zero));
        Assert.False(guards.Order(0, new Vector3(float.PositiveInfinity, 0f, 0f)));
        guards.Clear();
        Assert.Equal(0, guards.Alive);
        Assert.Equal(0, guards.SpawnSquad(Vector3.Zero));
    }

    [Theory]
    [InlineData(5f, 0.65f)]
    [InlineData(38f, 0.23f)]   // 38 m so every man of the formation is in range
    public void The_hit_chance_follows_the_distance(float distance, float chance)
    {
        var sim = new MusouSim(null);
        var guards = new GuardSim(sim);
        guards.SpawnSquad(Vector3.Zero);
        for (int k = 0; k < 16; k++) sim.Spawn(MusouEnemyType.Warboss, new Vector3(0f, distance, 0f), 0f, 1e7f);
        for (uint k = 1; k <= 3000; k++) { sim.BeginTick(k); guards.BeginTick(k); guards.Step(true); }
        Assert.InRange(guards.Shots, 900, 1100);   // 10 men, 1 shot/s, 100 s
        Assert.InRange(guards.ShotsHit / (float)guards.Shots, chance - 0.07f, chance + 0.07f);
    }

    [Fact]
    public void No_fight_no_shots_and_a_war_without_guards_or_orks_stays_idle()
    {
        var sim = new MusouSim(null);
        var guards = new GuardSim(sim);
        guards.SpawnSquad(Vector3.Zero);
        sim.Spawn(MusouEnemyType.Boy, new Vector3(0f, 5f, 0f), 0f, 85f);
        for (uint k = 1; k <= 120; k++) { sim.BeginTick(k); guards.BeginTick(k); guards.Step(false); }
        Assert.Equal(0, guards.Shots);
        var w = Box(20f);
        w.Step(_ => default);
        Assert.Equal(0f, w.Musou!.LastStepMilliseconds);
        Assert.Equal(0, w.TfwGuards!.Count);
    }

    [Fact]
    public void Two_hundred_guards_stay_under_a_loose_cost_budget()
    {
        var w = Box(100f);
        var guards = w.TfwGuards!;
        for (int s = 0; s < 20; s++) guards.SpawnSquad(new Vector3(-80f + (s % 5) * 16f, -30f + (s / 5) * 12f, 0f));
        for (int k = 0; k < 3; k++) Boyz(w, 60, new Vector3(-70f + k * 50f, 40f, 0f));
        var times = new List<double>();
        for (int k = 0; k < 300; k++)
        {
            w.Step(_ => default);
            if (k >= 30) times.Add(guards.LastStepMilliseconds);   // past the JIT
        }
        times.Sort();
        double median = times[times.Count / 2], p99 = times[times.Count * 99 / 100];
        output.WriteLine($"guards alive {guards.Alive}, median {median:0.000} ms, p99 {p99:0.000} ms, max {times[^1]:0.000} ms");
        Assert.True(guards.Alive >= 150, $"{guards.Alive}");
        // The target is 0.5 ms (0.12 ms median measured, Release, quiet machine). This bound is 2 ms so a busy machine does not fail it: it only catches a change of scale.
        Assert.True(Debugger.IsAttached || median <= 2.0, $"median {median:0.000} ms");
    }
}
