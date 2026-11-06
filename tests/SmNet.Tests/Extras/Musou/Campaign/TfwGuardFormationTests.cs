using System.Numerics;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Musou.Campaign;

/// <summary>
/// The owner's items of 2026-11-06: the Guardsmen back as formations that defend secured ground and never capture; only players, lords and retainers take a point, and they take it with Orks still on it;
/// a Boss Ork's blow cleaves Guardsmen as the players' cleave does Orks.
/// </summary>
public class TfwGuardFormationTests
{
    static (MusouSim Sim, GuardSim Guards, TfwBattle Battle, TfwLayout Layout) Frozen(bool final = false)
    {
        var sim = new MusouSim(null) { Frozen = true };
        var guards = new GuardSim(sim);
        var layout = TfwTestArena.Layout();
        var battle = new TfwBattle(sim, guards, layout, 100.0, 1, final, default, TfwBattleRules.EliteBase, guardsmen: true) { Trickle = false, BossOrksOn = false };
        battle.Start();
        return (sim, guards, battle, layout);
    }

    static void ClearPoint(MusouSim sim, TfwLayout layout, int k)
    {
        var ids = new ushort[MusouSim.Capacity];
        int n = sim.Query(layout.Positions[k], layout.Radius(k) + 2f, ids);
        for (int i = 0; i < n; i++) sim.Remove(ids[i]);
    }

    /// <summary>Secures position <paramref name="k"/> with a capturer on it.</summary>
    static void Capture(MusouSim sim, TfwBattle battle, TfwLayout layout, int k)
    {
        Vector3[] at = [layout.Positions[k]];
        for (int t = 0; t < 30 * 30 && battle.Objectives[k].State != TfwPositionState.Secured; t++) { ClearPoint(sim, layout, k); battle.Step(at); }
        Assert.Equal(TfwPositionState.Secured, battle.Objectives[k].State);
    }

    static void Seconds(TfwBattle battle, GuardSim guards, float seconds, ReadOnlySpan<Vector3> feet = default)
    {
        for (int t = 0; t < (int)MathF.Round(seconds * 30f); t++) { battle.Step(feet); guards.BeginTick((uint)t); guards.Step(false); }
    }

    [Fact]
    public void The_guardsmen_are_on_by_default()
    {
        Assert.True(TfwBattleRules.GuardsmenEnabled);
        var (_, guards, battle, _) = Frozen();
        Assert.True(battle.Squads >= TfwBattleRules.SquadsMin);
        Assert.Equal(Math.Min(battle.Squads, TfwBattleRules.FieldSquads), guards.SquadsAlive);
    }

    [Fact]
    public void Guardsmen_on_a_point_never_add_capture_progress()
    {
        var (sim, guards, battle, layout) = Frozen();
        ClearPoint(sim, layout, 0);
        int squad = guards.SpawnSquad(layout.Positions[0]);
        Assert.True(squad >= 0);
        Assert.Equal(GuardSim.SquadSize, guards.CountInside(layout.Positions[0], layout.Radius(0)));
        for (int t = 0; t < 30 * 30; t++) { ClearPoint(sim, layout, 0); battle.Step([]); }
        Assert.Equal(TfwPositionState.Held, battle.Objectives[0].State);
        Assert.Equal(0f, battle.Objectives[0].Progress);
    }

    [Fact]
    public void A_player_lord_or_retainer_on_a_point_secures_it_at_the_full_rate()
    {
        // The battle's feet are every living player, the AI Marines (lords and retainers) included: World.TfwStep passes them all.
        var (sim, _, battle, layout) = Frozen();
        ClearPoint(sim, layout, 0);
        Vector3[] one = [layout.Positions[0]];
        for (int t = 0; t < 5 * 30; t++) battle.Step(one);
        Assert.Equal(TfwPositionState.Securing, battle.Objectives[0].State);
        Assert.InRange(battle.Objectives[0].Progress, 5f / TfwBattleRules.SecureSeconds - 0.01f, 5f / TfwBattleRules.SecureSeconds + 0.01f);
        Capture(sim, battle, layout, 0);
    }

    [Fact]
    public void A_capture_goes_on_with_orks_on_the_point_and_stops_when_no_player_is_on_it()
    {
        var (sim, _, battle, layout) = Frozen();
        Assert.True(battle.OrksOn(0) > 0);   // the garrison stands on it (frozen)
        Vector3[] one = [layout.Positions[0]];
        for (int t = 0; t < 5 * 30; t++) battle.Step(one);
        Assert.True(battle.OrksOn(0) > 0);
        Assert.Equal(TfwPositionState.Clearing, battle.Objectives[0].State);
        float made = battle.Objectives[0].Progress;
        Assert.InRange(made, 5f / TfwBattleRules.SecureSeconds - 0.01f, 5f / TfwBattleRules.SecureSeconds + 0.01f);
        for (int t = 0; t < 5 * 30; t++) battle.Step([]);
        Assert.Equal(TfwPositionState.Held, battle.Objectives[0].State);
        Assert.InRange(battle.Objectives[0].Progress, made - 5f * TfwBattleRules.SecureDecayPerSecond - 0.01f, made - 5f * TfwBattleRules.SecureDecayPerSecond + 0.01f);   // decays, as an empty point always did
        for (int t = 0; t < 30 * 30 && battle.Objectives[0].State != TfwPositionState.Secured; t++) battle.Step(one);
        Assert.Equal(TfwPositionState.Secured, battle.Objectives[0].State);   // taken with the Orks still on it
        Assert.True(battle.OrksOn(0) > 0);
    }

    [Fact]
    public void Squads_hold_the_landing_zone_until_a_point_is_secured_then_go_to_it_and_hold_it()
    {
        var (sim, guards, battle, layout) = Frozen();
        Seconds(battle, guards, 2f);
        for (int s = 0; s < GuardSim.MaxSquads; s++) if (guards.AliveIn(s) > 0) Assert.Equal(TfwBattle.LandingGoal, battle.SquadPost(s));
        for (int s = 0; s < GuardSim.MaxSquads; s++)
            if (guards.AliveIn(s) > 0) Assert.True(Vector2.Distance(new(guards.Centre(s).X, guards.Centre(s).Y), new(layout.LandingZone.X, layout.LandingZone.Y)) < 20f);
        Capture(sim, battle, layout, 0);
        ClearPoint(sim, layout, 0);
        Seconds(battle, guards, 60f);
        int squads = 0;
        for (int s = 0; s < GuardSim.MaxSquads; s++)
        {
            if (guards.AliveIn(s) == 0) continue;
            squads++;
            Assert.Equal(0, battle.SquadPost(s));
        }
        Assert.True(squads >= TfwBattleRules.SquadsMin);
        Assert.Equal(squads * GuardSim.SquadSize, guards.CountInside(layout.Positions[0], layout.Radius(0) + 2f));
        Assert.Equal(TfwPositionState.Held, battle.Objectives[1].State);   // nobody took the others
        Assert.Equal(0f, battle.Objectives[1].Progress);
        Assert.Equal(TfwPositionState.Held, battle.Objectives[2].State);
    }

    [Fact]
    public void Squads_are_shared_over_the_secured_points_by_threat()
    {
        var (sim, guards, battle, layout) = Frozen();
        Capture(sim, battle, layout, 0);
        Capture(sim, battle, layout, 1);
        ClearPoint(sim, layout, 0);
        ClearPoint(sim, layout, 1);
        Seconds(battle, guards, 2f);
        int Posted(int k) { int n = 0; for (int s = 0; s < GuardSim.MaxSquads; s++) if (guards.AliveIn(s) > 0 && battle.SquadPost(s) == k) n++; return n; }
        Assert.True(Math.Abs(Posted(0) - Posted(1)) <= 1, $"{Posted(0)} and {Posted(1)}");   // no threat: an even share
        float boy = sim.Rules.Of(MusouEnemyType.Boy).Health;
        for (int n = 0; n < 40; n++) sim.Spawn(MusouEnemyType.Boy, layout.Positions[1] + new Vector3(20f + n % 5, n / 5 * 1.5f, 0f), 0f, boy);
        Seconds(battle, guards, 2f);
        Assert.True(Posted(1) > Posted(0), $"{Posted(0)} and {Posted(1)}");
        Assert.True(Posted(0) >= 1);
    }

    [Fact]
    public void Guardsmen_keep_a_secured_point_and_it_falls_once_they_are_gone()
    {
        var (sim, guards, battle, layout) = Frozen();
        Capture(sim, battle, layout, 0);
        guards.SpawnSquad(layout.Positions[0]);
        float boy = sim.Rules.Of(MusouEnemyType.Boy).Health;
        sim.Spawn(MusouEnemyType.Boy, layout.Positions[0] + new Vector3(2f, 0f, 0f), 0f, boy);
        for (int t = 0; t < 25 * 30; t++) battle.Step([]);
        Assert.Equal(TfwPositionState.Secured, battle.Objectives[0].State);
        guards.KillWithin(layout.Positions[0], layout.Radius(0) + 5f);
        for (int t = 0; t < (TfwBattleRules.RetakeSeconds + 1f) * 30; t++) battle.Step([]);
        Assert.Equal(TfwPositionState.Held, battle.Objectives[0].State);
    }

    [Fact]
    public void A_cleave_strikes_the_men_in_its_arc_nearest_first_and_spares_those_behind()
    {
        var sim = new MusouSim(null) { Frozen = true };
        var guards = new GuardSim(sim);
        guards.SpawnSquad(Vector3.Zero);   // two rows of five, 1.5 m apart
        guards.BeginTick(1);
        int struck = guards.Cleave(new Vector3(0f, -2f, 0f), Vector2.UnitY, 4f, 0f, 20, 1000f, TfwCombatRules.Default);
        Assert.True(struck >= 5, $"{struck}");
        Assert.Equal(GuardSim.SquadSize - struck, guards.Alive);
        Assert.Equal(0, guards.Cleave(new Vector3(0f, -2f, 0f), -Vector2.UnitY, 4f, 0f, 20, 1000f, TfwCombatRules.Default));   // all of them behind it
        Assert.Equal(0, guards.Cleave(new Vector3(0f, 30f, 0f), -Vector2.UnitY, 4f, 0f, 20, 1000f, TfwCombatRules.Default));   // none in reach

        var other = new GuardSim(sim);
        other.SpawnSquad(Vector3.Zero);
        other.BeginTick(1);
        Assert.Equal(3, other.Cleave(new Vector3(0f, -2f, 0f), Vector2.UnitY, 6f, 0f, 3, 1f, TfwCombatRules.Default));   // at most 3; a light blow knocks them down
        var men = new TfwGuard[GuardSim.Capacity];
        int n = other.CopyTo(men);
        int down = men.Take(n).Count(m => m.State == (byte)GuardState.Knocked), dead = men.Take(n).Count(m => m.State == (byte)GuardState.Dying);
        Assert.Equal(3, down + dead);
        Assert.All(men.Take(n).Where(m => m.State is (byte)GuardState.Knocked or (byte)GuardState.Dying), m => Assert.True(m.Position.Y < 0.1f));   // the front row, the nearer
    }

    [Fact]
    public void A_boss_ork_blow_on_a_squad_cleaves_several_guardsmen()
    {
        var w = TfwRig.War();
        var battle = TfwRig.Deploy(w);
        battle.BossOrksOn = false;
        var guards = w.TfwGuards!;
        TfwRig.KillAll(w);
        TfwRig.Teleport(w, TfwTestArena.Hq);   // far from the squads, so the boss goes for them
        int squad = guards.NearestSquad(battle.Layout.LandingZone);
        Assert.True(squad >= 0);
        var at = guards.Centre(squad) + new Vector3(3f, 0f, 0f);
        float hp = w.Musou!.Rules.Of(MusouEnemyType.BossNob).Health;
        ushort boss = w.Musou.Spawn(MusouEnemyType.BossNob, at, MathF.PI / 2f, hp);
        Assert.NotEqual((ushort)0, boss);
        int lostBefore = guards.Lost;
        for (int t = 0; t < 20 * 30 && w.TfwBestGuardCleave < 3; t++) w.Step(_ => default);
        Assert.True(w.TfwBestGuardCleave >= 3, $"best cleave {w.TfwBestGuardCleave}, lost {guards.Lost - lostBefore}");
    }

    [Fact]
    public void An_ordinary_orks_blow_still_lands_on_one_man()
    {
        var sim = new MusouSim(null) { Frozen = true };
        var guards = new GuardSim(sim);
        int s = guards.SpawnSquad(Vector3.Zero);
        guards.BeginTick(1);
        Assert.True(guards.Hit(s, 1000f, new Vector3(0f, -5f, 0f)));
        Assert.Equal(GuardSim.SquadSize - 1, guards.Alive);
    }
}
