using System.Numerics;
using SmContent.Extras.Warfront;
using SmNet.Extras.Warfront;
using SmNet.Tests.Extras.Musou.Campaign;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront;
using SmSim.Extras.Warfront.Campaign;
using SmSim.Extras.Warfront.Hq;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Warfront.Hq;

/// <summary>
/// W13b: the HQ defence on the composed HQ map, scripted in-process with the Orks frozen where the test puts them (no walking graph: the throughput is measured by <c>smtool warfront-hq --measure</c>). A defence never fought loses W,
/// then K, then V; K cannot fall while W holds; a fallen position cannot be re-secured; the stages' sources stay closed until their position falls and never spawn near a human; the 8 minutes survived end it Won with the captures paid
/// once; a wipe ends it Lost at once; the lane battles' elite shares; the wire.
/// </summary>
public class WarfrontHqBattleTests(ITestOutputHelper output)
{
    static readonly Lazy<WarfrontLoadedMap> Loaded = new(() => WarfrontMaps.Load(WarfrontMaps.HqName, out _)!);

    /// <summary>The HQ's composed world with one human aboard and the run's target set to the HQ, deployed and landed.</summary>
    static World Hq(out TfwBattle battle, bool guardsmen = false, int humans = 1, bool frozen = true)
    {
        var map = Loaded.Value;
        var config = TfwRig.Config with { Extras = ContentProfiles.WarfrontProfile };
        var w = new World(config, map.Collision, map.Spawns) { Rules = new MatchRules { CountdownTime = 0.1f, TimeLimit = 0f, ScoreLimit = int.MaxValue / 2, RestartDelay = 2f }, StartAreas = map.StartAreas };
        Assert.True(w.StartTfw(null, map.Tfw!.Field));
        w.Tfw!.AllyUp = _ => false;
        w.Tfw.Guardsmen = guardsmen;
        w.Tfw.Patrols = false;
        w.TfwMeksOn = false;
        w.Spawn(TfwRig.Human, w.AssignClass(0));
        for (int id = 2; id <= humans; id++) w.Spawn(id, w.AssignClass(0));
        TfwRig.Steps(w, 30);
        w.Tfw.Warfront!.Target = WarfrontTargetKind.Hq;
        battle = TfwRig.Deploy(w);
        w.Musou!.Frozen = frozen;
        battle.WeirdboyzOn = false;   // a Weirdboy teleports in 25 to 40 m from a human, which can be inside a held ring
        return w;
    }

    static int Count(World w, Func<MusouEnemy, bool> pick)
    {
        var all = new MusouEnemy[MusouSim.Capacity];
        int n = w.Musou!.CopyTo(all);
        return all.Take(n).Count(e => e.Alive && pick(e));
    }

    static void Step(World w, int ticks) { for (int k = 0; k < ticks; k++) w.Step(_ => default); }

    static readonly Vector3 Safe = new(0f, 100f, WarfrontHqMap.PlateauZ);   // outside the rings of W, K and V

    /// <summary>Frozen Boyz standing in position <paramref name="k"/>'s ring.</summary>
    static void Occupy(World w, TfwBattle b, int k, int n = 3)
    {
        for (int i = 0; i < n; i++)
            w.Musou!.Spawn(MusouEnemyType.Boy, b.Layout.Positions[k] with { Z = WarfrontHqMap.PlateauZ } + new Vector3(i * 2f - 2f, 0f, 0f), 0f, 100f);
    }

    /// <summary>Keeps three frozen Boyz in each of the rings (the stranded-Ork failsafe moves a frozen Ork away after a few seconds).</summary>
    static void Keep(World w, TfwBattle b, int t, params int[] rings)
    {
        if (t % 60 != 0) return;
        foreach (int k in rings) if (!b.HqFell(k)) Occupy(w, b, k, 2);   // a fresh pair every 2 s: always some inside before the failsafe moves the older ones
    }

    [Fact]
    public void The_hq_is_composed_with_the_hub_and_the_defence_is_the_hq_battle()
    {
        var w = Hq(out var b);
        Assert.NotNull(Loaded.Value.Tfw);
        Assert.True(b.WarfrontHq && b.WarfrontNode);
        Assert.True(w.Tfw!.Warfront!.InBattle);
        for (int k = 0; k < 3; k++) Assert.Equal(TfwPositionState.Secured, b.Objectives[k].State);
        Assert.Equal(3, b.Secured);
        Assert.Equal(TfwPositionState.Locked, b.Objectives[TfwBattle.CommanderObjective].State);
        Assert.Equal(0, b.BossId);
        Assert.Equal(WarfrontHqLayout.Build.LandingZone, b.Layout.LandingZone);
        Assert.Equal(0, Count(w, e => e.Type == MusouEnemyType.BossNob || e.Type == MusouEnemyType.Warboss));   // no garrison, no commander
        Assert.True(Vector3.Distance(w.Players[TfwRig.Human].Position, b.Layout.LandingZone) < 20f);
        Assert.False(b.SourceActive(0) || b.SourceActive(1) || b.SourceActive(2) || b.SourceActive(3));
    }

    [Fact]
    public void An_undefended_defence_loses_W_then_K_then_V_in_that_order_and_ends_lost()
    {
        var w = Hq(out var b);
        TfwRig.Teleport(w, Safe);
        TfwMapResult? ended = null;
        w.Tfw!.Warfront!.NodeEnded = (_, _, r) => ended = r;
        for (int k = 0; k < 3; k++) Occupy(w, b, k);
        var fell = new List<int>();
        for (int t = 0; t < 30 * 90 && w.Tfw.Battle is not null; t++)
        {
            Keep(w, b, t, 0, 1, 2);
            w.Step(_ => default);
            for (int k = 0; k < 3; k++) if (b.HqFell(k) && !fell.Contains(k)) { fell.Add(k); output.WriteLine($"{k} fell at {t / 30f:0.0} s"); }
            if (t % 300 == 0) output.WriteLine($"{t / 30} s: states {b.Objectives[0].State}/{b.Objectives[1].State}/{b.Objectives[2].State} p {b.Objectives[1].Progress:0.00} {b.Objectives[2].Progress:0.00} orksOn {b.OrksOn(1)} {b.OrksOn(2)} alive {w.Players[1].Alive}");
            if (!fell.Contains(0)) { Assert.Equal(TfwPositionState.Secured, b.Objectives[1].State); Assert.Equal(TfwPositionState.Secured, b.Objectives[2].State); }   // K and V cannot fall while W holds
        }
        Assert.Equal([0, 1, 2], fell);
        Assert.True(b.HqLost);
        Assert.Null(w.Tfw.Battle);
        Assert.Equal(TfwRunPhase.Hub, w.Tfw.Phase);
        Assert.Equal(TfwMapResult.Lost, ended);
    }

    [Fact]
    public void A_fallen_position_is_lost_for_the_battle_and_cannot_be_re_secured()
    {
        var w = Hq(out var b);
        TfwRig.Teleport(w, Safe);
        Occupy(w, b, 0);
        for (int t = 0; t < 30 * 25; t++) { Keep(w, b, t, 0); w.Step(_ => default); }
        Assert.True(b.HqFell(0));
        Assert.Equal(TfwPositionState.Held, b.Objectives[0].State);
        Assert.Equal(1, b.HqFallenCount);
        Assert.Equal(0b110, b.HqHeldBits);
        TfwRig.KillAll(w);
        TfwRig.Teleport(w, b.Layout.Positions[0] with { Z = WarfrontHqMap.PlateauZ } + new Vector3(0.5f, 0f, 0f));
        Step(w, 30 * 40);   // a human stands on W with no Ork there for 40 s (securing takes 10)
        Assert.NotEqual(TfwPositionState.Secured, b.Objectives[0].State);
        Assert.Equal(2, b.Secured);
    }

    [Fact]
    public void Stage_sources_stay_closed_until_their_position_falls_and_never_spawn_within_30_m_of_a_human()
    {
        var w = Hq(out var b);
        Assert.True(b.HqSourceOpen(0));
        Assert.False(b.HqSourceOpen(1));
        Assert.False(b.HqSourceOpen(2));
        TfwRig.Teleport(w, Safe);
        Step(w, 30 * 30);   // wave 1 stands mustered on the assembly ground only
        Assert.Equal(0, Count(w, e => e.Position.Y > WarfrontHqMap.RampNorth));
        Occupy(w, b, 0);
        Step(w, 30 * 25);
        Assert.True(b.HqFell(0));
        Assert.True(b.HqSourceOpen(1));
        Assert.False(b.HqSourceOpen(2));
        int flood = Count(w, e => e.Position.Y > WarfrontHqMap.WallNorth && e.Position.Y < WarfrontHqMap.KeepSouth && e.Position.Z > 5f);
        Assert.True(flood >= WarfrontHqBattleRules.BreachFlood - 3, $"{flood} Orks came into the courtyard");
        Assert.All(WarfrontHqLayout.SourcesOf(1), s => Assert.True(Vector3.Distance(s, Safe) > 30f));
        output.WriteLine($"courtyard flood {flood}");
    }

    [Fact]
    public void A_human_in_the_courtyard_closes_the_stage_one_sources_and_the_flood_is_made_on_the_assembly_ground()
    {
        var w = Hq(out var b);
        var human = new Vector3(0f, 0f, WarfrontHqMap.PlateauZ);
        TfwRig.Teleport(w, human);
        Occupy(w, b, 0);
        Step(w, 30 * 25);
        Assert.True(b.HqFell(0));
        Assert.Equal(0, Count(w, e => e.Position.Y > -20f && e.Position.Z > 5f && Vector3.Distance(e.Position, human) < 60f));   // none near him
        Assert.True(Count(w, e => e.Position.Y < WarfrontHqMap.AssemblyNorth) >= 60);
        Assert.True(b.HqOrksMade > 0);
    }

    [Fact]
    public void Surviving_eight_minutes_wins_pays_the_held_positions_once_and_returns_the_party_aboard()
    {
        var w = Hq(out var b);
        TfwRig.Teleport(w, Safe);
        var paid = new List<int>();
        w.Tfw!.Warfront!.ObjectiveSecured = k => paid.Add(k);
        TfwMapResult? result = null;
        w.Tfw.Warfront.NodeEnded = (_, _, r) => result = r;
        int sent = 0;
        for (int t = 0; t < 30 * 470; t++)
        {
            w.Step(_ => default);
            if (t % 300 == 0) { TfwRig.KillAll(w); sent = Math.Max(sent, b.HqWavesSent); }
        }
        Assert.Null(result);
        Assert.NotNull(w.Tfw.Battle);
        Assert.InRange(b.HqSecondsLeft, 5f, 15f);
        Assert.Equal(3, b.HqWavesSent);
        Step(w, 30 * 15);
        Assert.Equal(TfwMapResult.Won, result);
        Assert.Null(w.Tfw.Battle);
        Assert.Equal([0, 1, 2], paid.OrderBy(x => x).ToArray());   // the three positions still held, once each, and no D
        Assert.True(Loaded.Value.Tfw!.Field.Hub.Contains(w.Players[TfwRig.Human].Position));
        output.WriteLine($"orks made {b.HqOrksMade}, skipped {b.HqOrksSkipped}");
    }

    [Fact]
    public void The_win_pays_only_the_positions_still_held()
    {
        var w = Hq(out var b);
        TfwRig.Teleport(w, Safe);
        var paid = new List<int>();
        w.Tfw!.Warfront!.ObjectiveSecured = k => paid.Add(k);
        Occupy(w, b, 0);
        for (int t = 0; t < 30 * 25; t++) { Keep(w, b, t, 0); w.Step(_ => default); }
        Assert.True(b.HqFell(0));
        TfwRig.KillAll(w);
        TfwMapResult? result = null;
        w.Tfw.Warfront.NodeEnded = (_, _, r) => result = r;
        int tEnd = 0;
        for (int t = 0; t < 30 * 460 && w.Tfw.Battle is not null; t++) { w.Step(_ => default); if (t % 100 == 0) TfwRig.KillAll(w); tEnd = t; }
        Assert.Null(w.Tfw.Battle);
        Assert.True(result == TfwMapResult.Won, $"{result} after {tEnd / 30f:0} s, V {b.HqFell(2)} K {b.HqFell(1)}");
        Assert.Equal([1, 2], paid.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void A_wipe_ends_the_defence_lost_at_once_when_the_last_window_closes()
    {
        var w = Hq(out _);
        TfwMapResult? result = null;
        w.Tfw!.Warfront!.NodeEnded = (_, _, r) => result = r;
        Assert.True(w.TfwKillForTests(TfwRig.Human));
        Step(w, 30 * 25 + 60);
        Assert.Equal(TfwMapResult.Lost, result);
        Assert.Null(w.Tfw.Battle);
    }

    [Fact]
    public void The_waves_leave_on_their_schedule_with_the_boss_orks_in_the_third()
    {
        var w = Hq(out var b);
        TfwRig.Teleport(w, Safe);
        Assert.Equal(3, WarfrontHqBattleRules.ScheduledWaves);
        var (wave, seconds) = b.HqNextWave;
        Assert.Equal(1, wave);
        Assert.InRange(seconds, 44f, 45f);
        Step(w, 30 * 46);
        Assert.Equal(1, b.HqWavesSent);
        Assert.InRange(Count(w, e => e.Position.Y < WarfrontHqMap.AssemblyNorth), WarfrontHqBattleRules.WaveSizes[0] - 15, WarfrontHqBattleRules.WaveSizes[0] + 5);   // the first chunks have left
        Assert.Equal(0, Count(w, e => e.Type == MusouEnemyType.BossNob));
        Step(w, 30 * 150);
        Assert.Equal(2, b.HqWavesSent);
        Step(w, 30 * 130);   // 5:26: the third wave stands mustered (it leaves at 5:45)
        Assert.Equal(1, Count(w, e => e.Type == MusouEnemyType.BossNob));   // one per human
        Step(w, 30 * 25);
        Assert.Equal(3, b.HqWavesSent);
    }

    [Fact]
    public void Only_the_front_most_position_can_be_retaken_and_the_guardsmen_re_post_when_it_falls()
    {
        var w = Hq(out var b, guardsmen: true);
        TfwRig.Teleport(w, Safe);
        Step(w, 30 * 5);
        var squads = Enumerable.Range(0, GuardSim.MaxSquads).Where(s => w.TfwGuards!.AliveIn(s) > 0).ToList();
        Assert.Equal(WarfrontHqBattleRules.SquadsAt.Sum(), squads.Count);
        Assert.Equal(WarfrontHqBattleRules.SquadsAt[0], squads.Count(s => Vector3.Distance(w.TfwGuards!.Centre(s), b.Layout.Positions[0]) < 45f));
        Assert.Equal(0, b.HqFront);
    }

    [Fact]
    public void The_battle_section_round_trips_and_says_what_the_hud_shows()
    {
        var w = Hq(out var b);
        TfwRig.Teleport(w, Safe);
        Step(w, 30 * 10);
        var section = WarfrontBattleWire.FromBattle(b, WarfrontTargetKind.Hq);
        Assert.True(WarfrontBattleWire.TryDecode(WarfrontBattleWire.Encode(section), out var back));
        Assert.Equal(section, back);
        Assert.Equal(1, back.Kind);
        Assert.Equal(1, back.Wave);
        Assert.Equal(3, back.Waves);
        Assert.InRange((int)back.SecondsToWave, 34, 36);
        Assert.InRange((int)back.SecondsLeft, 469, 471);
        Assert.Equal(0b111, back.HeldBits);
        Assert.StartsWith("HOLD THE WALL", WarfrontBattleWire.Line(back));
        Assert.Contains("WAVE 1 OF 3 IN 0:3", WarfrontBattleWire.Line(back));
        Assert.Equal("SURVIVE 7:50", WarfrontBattleWire.Countdown(back with { SecondsLeft = 470 }));
        Assert.False(WarfrontBattleWire.TryDecode(new byte[9], out _));
        Assert.False(WarfrontBattleWire.TryDecode(new byte[10] { 9, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, out _));
        Assert.Equal(WarfrontBattleWire.Length, WarfrontBattleWire.Encode(section).Length);
        var node = WarfrontBattleWire.FromBattle(b, WarfrontTargetKind.Node);
        Assert.Equal(0, node.Kind);
    }

    [Theory]
    [InlineData(1, 0f)]
    [InlineData(2, 0.5f)]
    [InlineData(3, 1f)]
    public void Lane_battles_take_their_elite_share_from_the_node(int node, float progress)
    {
        var w = SmNet.Tests.Extras.Warfront.Battle.WarfrontBattleTests.War();
        var run = w.Tfw!;
        run.War.SetFrontsForTests((sbyte)(node - 1));
        run.Warfront!.Target = WarfrontTargetKind.Node;
        var battle = TfwRig.Deploy(w);
        Assert.Equal(TfwBattleRules.EliteShare(progress), battle.EliteShare);
    }

    [Fact]
    public void The_hq_battle_takes_three_quarters_of_the_curve()
    {
        Hq(out var b);
        Assert.Equal(TfwBattleRules.EliteShare(0.75f), b.EliteShare);
    }
}
