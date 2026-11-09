using System.Numerics;
using SmNet.Extras.Musou.Campaign;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Musou.Campaign;

/// <summary>The roaming Boss Orks and the Warboss changes (owner, 2026-11-06; docs/musou-boss-orks-design.md): the kinds' numbers, hyper armour, when and where they come, the retinue, the cap, the commander as the Warboss, the HUD's tracker and the pinned bars.</summary>
public class TfwBossOrkTests
{
    /// <summary>A player far from every spawn point of the test arena (each is at least 40 m from it), so a due Boss Ork always finds a place.</summary>
    static readonly Vector3[] FarPlayer = [new(0f, -230f, 0f)];

    static (MusouSim Sim, TfwBattle Battle, TfwLayout Layout) Battle(bool final = false, List<TfwBossOrkArrival>? arrivals = null)
    {
        var sim = new MusouSim(null) { Frozen = true };
        var layout = TfwTestArena.Layout();
        var battle = new TfwBattle(sim, new GuardSim(sim), layout, 100.0, 1, final, default, TfwBattleRules.EliteBase, guardsmen: false) { Trickle = false, Patrols = false };
        if (arrivals is not null) battle.OnBossOrk = arrivals.Add;
        battle.OnSwarm = s => { foreach (ushort id in s.Ids) sim.Remove(id); };   // keep the field small: every swarm goes at once (a frozen pool never frees its dying, so they are removed)
        battle.Start();
        return (sim, battle, layout);
    }

    static int Ticks(float seconds) => (int)MathF.Round(seconds * 30f);

    [Fact]
    public void Each_boss_kind_is_its_base_at_three_times_the_size_ten_times_the_health_at_least_3000_and_twice_the_damage()
    {
        var rules = MusouRules.Default;
        foreach (var boss in MusouBossOrks.Kinds)
        {
            var b = rules.Of(MusouBossOrks.BaseOf(boss));
            var s = rules.Of(boss);
            Assert.Equal(boss, s.Type);
            Assert.True(MusouBossOrks.Is(boss));
            Assert.Equal(boss, MusouBossOrks.BossOf(MusouBossOrks.BaseOf(boss)));
            Assert.Equal(MathF.Max(3000f, b.Health * 10f), s.Health);
            Assert.Equal(b.Radius * 3f, s.Radius, 4);
            Assert.Equal(b.Height * 3f, s.Height, 4);
            Assert.Equal(b.MeleeDamage * 2f, s.MeleeDamage);
            Assert.Equal(b.RangedDamage * 2f, s.RangedDamage);
            Assert.Equal(b.MeleeRange * 1.5f, s.MeleeRange, 4);
            Assert.Equal(1000, s.Points);
            Assert.True(s.Speed <= 4.4f);
        }
        Assert.Equal([3000f, 3000f, 3000f, 6000f], MusouBossOrks.Kinds.Select(k => rules.Of(k).Health));
        Assert.Equal(3f * 0.6f, new MusouSim(null).Body(MusouEnemyType.BossNob).Radius, 4);
        Assert.False(MusouBossOrks.Is(MusouEnemyType.Nob));
        Assert.False(MusouBossOrks.Is(MusouEnemyType.Warboss));
        Assert.Null(MusouBossOrks.BossOf(MusouEnemyType.Warboss));
    }

    [Fact]
    public void The_Warboss_is_noticeably_stronger_than_any_Boss_Ork()
    {
        var rules = MusouRules.Default;
        Assert.Equal(15000f, rules.BossHealth);
        Assert.Equal(60f, rules.Of(MusouEnemyType.Warboss).MeleeDamage);
        Assert.Equal(60f, rules.Boss.SlamDamage);
        Assert.Equal(75f, rules.Boss.ChargeDamage);
        Assert.True(rules.BossHealth >= 2f * MusouBossOrks.Kinds.Max(k => rules.Of(k).Health));
    }

    [Fact]
    public void A_Boss_Ork_never_staggers_and_never_falls()
    {
        var sim = new MusouSim(null) { Frozen = true };
        foreach (var kind in MusouBossOrks.Kinds)
        {
            ushort id = sim.Spawn(kind, new Vector3(0f, 0f, 0f), 0f, 3000f);
            Assert.NotEqual(0, id);
            Assert.False(sim.Stagger(id, Vector2.UnitX, 2f));
            Assert.False(MusouSim.Fodder(kind));
        }
        ushort nob = sim.Spawn(MusouEnemyType.Nob, new Vector3(5f, 0f, 0f), 0f, 600f);
        Assert.True(sim.Stagger(nob, Vector2.UnitX, 2f));   // a Nob still does
    }

    [Fact]
    public void The_first_comes_one_to_two_minutes_after_the_landing_with_twenty_elites_who_fight_on_after_it_dies()
    {
        var arrivals = new List<TfwBossOrkArrival>();
        var (sim, battle, layout) = Battle(arrivals: arrivals);
        int tick = 0;
        while (arrivals.Count == 0 && tick < Ticks(130f)) { battle.Step(FarPlayer); tick++; }
        Assert.Single(arrivals);
        Assert.InRange(tick, Ticks(60f * TfwBossOrkRules.Interval), Ticks(120f * TfwBossOrkRules.Interval) + 1);   // 30 to 60 s since 2026-11-10 (half the waits)
        var a = arrivals[0];
        Assert.True(MusouBossOrks.Is(a.Type));
        Assert.Equal(sim.Rules.Of(a.Type).Health, a.MaxHealth);
        Assert.Equal(a.MaxHealth, battle.BossOrkMaxHealth(a.Id));
        Assert.Equal([a.Id], battle.BossOrks);
        int nobs = a.Retinue.Count(id => sim.TryGet(id, out var g) && g.Type == MusouEnemyType.Nob);
        Assert.Equal(TfwBossOrkRules.Retinue + nobs / TfwBattleRules.BigTwins * (TfwBattleRules.BigTwins - 1), a.Retinue.Length);   // and a twin of each Nob since 2026-11-10
        foreach (ushort id in a.Retinue)
        {
            Assert.True(sim.TryGet(id, out var e) && e.Alive);
            Assert.True(e.Type is MusouEnemyType.ArdBoy or MusouEnemyType.Nob);
            Assert.False(sim.Leashed(id));
            Assert.InRange(Vector2.Distance(new(e.Position.X, e.Position.Y), new(a.Position.X, a.Position.Y)), 2.9f, 6.1f);
        }
        Assert.Contains(a.Retinue, id => sim.TryGet(id, out var e) && e.Type == MusouEnemyType.Nob);
        // Far from every player, at a spawn point that sends.
        Assert.True(battle.SourceActive(a.Point));
        Assert.True(Vector3.Distance(a.Position, FarPlayer[0]) >= TfwBossOrkRules.MinPlayerDistance);

        sim.Damage(a.Id, 1, 1e7f, Vector3.Zero, out bool killed);
        Assert.True(killed);
        battle.Step(FarPlayer);
        Assert.Empty(battle.BossOrks);
        Assert.Null(battle.BossOrkMaxHealth(a.Id));
        Assert.All(a.Retinue, id => Assert.True(sim.TryGet(id, out var e) && e.Alive));   // the retinue fights on
    }

    [Fact]
    public void The_same_battle_brings_the_same_Boss_Orks_at_the_same_ticks()
    {
        List<(int Tick, TfwBossOrkArrival A)> Run()
        {
            var seen = new List<(int, TfwBossOrkArrival)>();
            var (_, battle, _) = Battle();
            int tick = 0;
            battle.OnBossOrk = a => seen.Add((tick, a));
            for (; tick < Ticks(420f); tick++) battle.Step(FarPlayer);
            return seen;
        }
        var one = Run();
        var two = Run();
        Assert.True(one.Count >= 2);
        Assert.Equal(one.Select(x => (x.Tick, x.A.Id, x.A.Type, x.A.Point, x.A.Position)), two.Select(x => (x.Tick, x.A.Id, x.A.Type, x.A.Point, x.A.Position)));
        // The next one 1.5 to 3 min after the last came (45 to 90 s since 2026-11-10).
        Assert.InRange(one[1].Tick - one[0].Tick, Ticks(90f * TfwBossOrkRules.Interval), Ticks(180f * TfwBossOrkRules.Interval) + 1);
    }

    [Fact]
    public void At_most_two_live_at_once_and_a_due_one_waits_for_a_free_place()
    {
        var arrivals = new List<TfwBossOrkArrival>();
        var (sim, battle, _) = Battle(arrivals: arrivals);
        for (int t = 0; t < Ticks(12f * 60f); t++)
        {
            battle.Step(FarPlayer);
            Assert.True(battle.BossOrks.Count <= TfwBossOrkRules.MostAlive);
        }
        int most = TfwBossOrkRules.MostAlive;   // 4 since 2026-11-10 (2 before)
        Assert.Equal(most, arrivals.Count);   // 12 min with none killed: the most came, the next waits
        Assert.True(battle.BossOrkDueTick <= battle.BattleTicks);
        sim.Damage(arrivals[0].Id, 1, 1e7f, Vector3.Zero, out _);
        battle.Step(FarPlayer);
        battle.Step(FarPlayer);
        Assert.Equal(most + 1, arrivals.Count);   // the waiting one comes once there is room
        Assert.Equal(most, battle.BossOrks.Count);
    }

    [Fact]
    public void None_come_while_nobody_is_in_the_field_nor_in_the_final_assault()
    {
        var arrivals = new List<TfwBossOrkArrival>();
        var (_, battle, _) = Battle(arrivals: arrivals);
        for (int t = 0; t < Ticks(150f); t++) battle.Step([]);
        Assert.Empty(arrivals);
        battle.Step(FarPlayer);
        Assert.Single(arrivals);   // the one due came as soon as someone was there

        var final = new List<TfwBossOrkArrival>();
        var (_, assault, _) = Battle(final: true, arrivals: final);
        for (int t = 0; t < Ticks(150f); t++) assault.Step(FarPlayer);
        Assert.Empty(final);
    }

    [Fact]
    public void No_Boss_Ork_comes_where_a_player_stands_near()
    {
        var arrivals = new List<TfwBossOrkArrival>();
        var (_, battle, layout) = Battle(arrivals: arrivals);
        // A player beside each sending point, outside its radius (on it he would take it, Orks or not, since 2026-11-06): none is 40 m clear of everyone.
        var beside = new Vector3(TfwTestArena.CaptureRadius + 4f, 0f, 0f);
        Vector3[] everywhere = [layout.Positions[0] + beside, layout.Positions[1] + beside, layout.Positions[2] + beside];
        for (int t = 0; t < Ticks(130f); t++) battle.Step(everywhere);
        Assert.Empty(arrivals);
    }

    [Fact]
    public void The_commander_is_the_Warboss_with_the_Warboss_health()
    {
        var sim = new MusouSim(null) { Frozen = true };
        var layout = TfwTestArena.Layout();
        var battle = new TfwBattle(sim, new GuardSim(sim), layout, 100.0, 1, guardsmen: false) { Trickle = false, Patrols = false, BossOrksOn = false };
        battle.Start();
        for (int k = 0; k < 3; k++)
        {
            Vector3[] at = [layout.Positions[k]];
            var ids = new ushort[MusouSim.Capacity];
            for (int t = 0; t < 30 * 30 && battle.Objectives[k].State != TfwPositionState.Secured; t++)
            {
                int n = sim.Query(layout.Positions[k], layout.Radius(k) + 2f, ids);
                for (int i = 0; i < n; i++) sim.Damage(ids[i], 1, 1e7f, Vector3.Zero, out _);
                battle.Step(at);
            }
        }
        Assert.NotEqual(0, battle.BossId);
        Assert.True(sim.TryGet(battle.BossId, out var boss));
        Assert.Equal(MusouEnemyType.Warboss, boss.Type);
        Assert.Equal(sim.Rules.BossHealth, battle.BossMaxHealth);
        Assert.True(sim.Leashed(battle.BossId));   // he keeps the HQ until someone comes, as the commander did
    }

    [Fact]
    public void The_HUD_tracker_calls_out_an_arrival_and_a_kill_once_and_names_the_kind()
    {
        var hud = new TfwBossOrkHud();
        var types = new Dictionary<ushort, MusouEnemyType> { [5] = MusouEnemyType.BossNob, [6] = MusouEnemyType.Nob, [7] = MusouEnemyType.BossShootaBoy };
        MusouEnemyType? TypeOf(ushort id) => types.TryGetValue(id, out var t) ? t : null;
        Assert.Equal((1, 0), hud.Update([new TfwBar(5, 255), new TfwBar(6, 255)], TypeOf, fighting: true));
        Assert.Equal([new TfwBossOrkBar(5, "BOSS NOB", 1f)], hud.Bars);
        Assert.Equal((0, 0), hud.Update([new TfwBar(5, 128), new TfwBar(6, 255)], TypeOf, fighting: true));
        Assert.Equal(128 / 255f, hud.Bars[0].Health, 4);
        Assert.Equal((1, 1), hud.Update([new TfwBar(7, 255)], TypeOf, fighting: true));   // 5 fell, 7 came
        Assert.Equal("BOSS SHOOTA BOY", hud.Bars[0].Name);
        Assert.Equal((0, 0), hud.Update([], TypeOf, fighting: false));   // the battle ended: no call-out for the bars going
        Assert.Equal((0, 0), hud.Update([], TypeOf, fighting: true));
    }

    [Fact]
    public void A_Boss_Ork_bar_is_always_sent_however_far()
    {
        var elites = new List<TfwElite>();
        for (int i = 0; i < 100; i++) elites.Add(new TfwElite((ushort)(i + 1), new Vector3(i, 0f, 0f), 200));
        elites.Add(new TfwElite(500, new Vector3(5000f, 0f, 0f), 90, Pinned: true));
        var into = new List<TfwBar>();
        TfwBars.Select(elites, Vector3.Zero, into);
        Assert.Equal(TfwBars.PerClient, into.Count);
        Assert.Contains(into, b => b.Enemy == 500 && b.Health == 90);
        Assert.True(TfwBars.IsElite(MusouEnemyType.BossBoy));
    }

    [Fact]
    public void The_boss_kinds_are_leaders_on_the_wire_and_always_leave_a_box()
    {
        foreach (var kind in MusouBossOrks.Kinds)
        {
            Assert.True(SmNet.Extras.Musou.MusouSendRules.IsLeader(kind));
            Assert.Equal(2, SmNet.Extras.Musou.MusouSendRules.Rank(kind));
            Assert.True(TfwRewardRules.Boxes(kind, 123u, 7));
            Assert.True(TfwRewardRules.Elite(kind));
        }
    }
}
