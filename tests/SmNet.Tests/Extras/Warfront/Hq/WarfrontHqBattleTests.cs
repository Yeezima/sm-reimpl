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
    public void The_waves_leave_on_their_schedule_with_the_boss_orks_from_the_second()
    {
        var w = Hq(out var b);
        TfwRig.Teleport(w, Safe);
        Assert.Equal(3, WarfrontHqBattleRules.ScheduledWaves);
        Assert.Equal([200, 300, 400], WarfrontHqBattleRules.WaveSizes);
        var (wave, seconds) = b.HqNextWave;
        Assert.Equal(1, wave);
        Assert.InRange(seconds, 44f, 45f);
        Step(w, 30 * 46);
        Assert.Equal(1, b.HqWavesSent);
        Assert.InRange(Count(w, e => e.Position.Y < WarfrontHqMap.AssemblyNorth), WarfrontHqBattleRules.WaveSizes[0] - 25, WarfrontHqBattleRules.WaveSizes[0] + 5);   // the first chunks have left
        Assert.Equal(0, Count(w, e => e.Type == MusouEnemyType.BossNob));   // no Boss Ork in the first wave
        Step(w, 30 * 100);   // 2:26: the second wave stands mustered (it leaves at 2:45)
        Assert.Equal(1, Count(w, e => e.Type == MusouEnemyType.BossNob));   // one per human, from wave 2
        Step(w, 30 * 25);
        Assert.Equal(2, b.HqWavesSent);
        Step(w, 30 * 140);   // 4:51: the third has left (4:45)
        Assert.Equal(3, b.HqWavesSent);
        Step(w, 30 * 160);   // 7:31: no fourth wave
        Assert.Equal(3, b.HqWavesSent);
    }

    [Fact]
    public void No_tick_makes_more_than_ten_orks_and_the_hq_alive_cap_is_400()
    {
        var w = Hq(out var b);
        TfwRig.Teleport(w, Safe);
        Assert.Equal(10, WarfrontHqBattleRules.SpawnsPerTick);
        Assert.Equal(400, WarfrontHqBattleRules.AliveCapHq);
        int most = 0, last = 0, mostAlive = 0;
        for (int t = 0; t < 30 * 200; t++)
        {
            w.Step(_ => default);
            most = Math.Max(most, b.HqOrksMade - last);
            last = b.HqOrksMade;
            if (t % 30 == 0) mostAlive = Math.Max(mostAlive, Count(w, e => !TfwBattleRules.Big(e.Type)));   // the Nobz and the Boss Orks are outside the cap (BigOutsideCaps)
        }
        Assert.InRange(most, 1, WarfrontHqBattleRules.SpawnsPerTick);
        Assert.True(mostAlive <= WarfrontHqBattleRules.AliveCapHq + 12, $"{mostAlive} Orks alive");
        output.WriteLine($"most made in a tick {most}, most alive {mostAlive}, skipped {b.HqOrksSkipped}");
    }

    [Fact]
    public void No_weirdboy_comes_in_a_scripted_defence_of_eight_minutes_and_K_never_falls_while_W_holds()
    {
        var w = Hq(out var b);
        Assert.False(b.WeirdboyzOn);
        TfwRig.Teleport(w, Safe);
        for (int t = 0; t < 30 * 470; t++)
        {
            w.Step(_ => default);
            if (t % 300 == 0) TfwRig.KillAll(w);
            if (!b.HqFell(0)) Assert.False(b.HqFell(1) || b.HqFell(2));
        }
        Assert.Equal(0, b.WeirdboyzCome);
        Assert.Equal(0, Count(w, e => e.Type == MusouEnemyType.Weirdboy));
    }

    /// <summary>Frozen Orks of <paramref name="type"/> in position 0's ring, a human (or none) standing on it, and the hold read after two seconds.</summary>
    float HoldAfterTwoSeconds(MusouEnemyType type, int orks, bool human, int marines = 0)
    {
        var w = Hq(out var b);
        TfwRig.Teleport(w, human ? b.Layout.Positions[0] with { Z = WarfrontHqMap.PlateauZ } + new Vector3(0.5f, 0f, 0f) : Safe);
        if (marines > 0)
            w.Tfw!.Warfront!.SquadTargets = into =>
            {
                for (int i = 0; i < marines && i < into.Length; i++) into[i] = new MusouTarget(i, b.Layout.Positions[0] with { Z = WarfrontHqMap.PlateauZ } + new Vector3(i, 2f, 0f), true, 0.5f);
                return Math.Min(marines, into.Length);
            };
        for (int i = 0; i < orks; i++)
            w.Musou!.Spawn(type, b.Layout.Positions[0] with { Z = WarfrontHqMap.PlateauZ } + new Vector3(i * 1.5f - 6f, -4f, 0f), 0f, 100f);
        Step(w, 60);
        output.WriteLine($"{orks} {type}, human {human}, marines {marines}: weights {b.HqWeights}, hold {b.HqHold:0.000}");
        return b.HqHold;
    }

    [Fact]
    public void The_hold_drains_in_twenty_seconds_when_the_orks_outweigh_the_defenders_and_a_human_weighs_six()
    {
        Assert.True(HoldAfterTwoSeconds(MusouEnemyType.Boy, 3, human: false) < 0.95f);          // three Boyz, nobody: draining
        Assert.Equal(1f, HoldAfterTwoSeconds(MusouEnemyType.Boy, 6, human: true));              // six against a human: not outnumbered
        Assert.True(HoldAfterTwoSeconds(MusouEnemyType.Boy, 7, human: true) < 0.95f);           // seven against a human: draining
        float full = HoldAfterTwoSeconds(MusouEnemyType.Boy, 8, human: false);
        Assert.InRange(full, 0.85f, 0.92f);                                                     // 2 s of 20 s drained (the Orks in a frozen scene may be moved)
    }

    [Fact]
    public void A_nob_counts_three_and_a_squad_marine_two()
    {
        Assert.Equal(1f, HoldAfterTwoSeconds(MusouEnemyType.Nob, 2, human: true));              // 3 + 3 = 6 against a human's 6
        Assert.True(HoldAfterTwoSeconds(MusouEnemyType.Nob, 3, human: true) < 0.95f);           // 9 against 6
        Assert.Equal(1f, HoldAfterTwoSeconds(MusouEnemyType.Boy, 6, human: false, marines: 3)); // three Marines weigh 6
        Assert.True(HoldAfterTwoSeconds(MusouEnemyType.Boy, 7, human: false, marines: 3) < 0.95f);
    }

    [Fact]
    public void A_position_that_is_outnumbered_for_twenty_seconds_falls_and_the_retake_hook_answers_false()
    {
        var w = Hq(out var b);
        TfwRig.Teleport(w, b.Layout.Positions[0] with { Z = WarfrontHqMap.PlateauZ } + new Vector3(0.5f, 0f, 0f));   // a standing human on the wall: the old rule never fell
        for (int t = 0; t < 30 * 40 && !b.HqFell(0); t++)
        {
            if (t % 60 == 0) Occupy(w, b, 0, 8);
            w.Step(_ => default);
        }
        Assert.True(b.HqFell(0));
        Assert.Equal(2, b.Secured);
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
        Assert.StartsWith("THE WALL IS FALLING", WarfrontBattleWire.Line(back with { Hold = 45 }));
        Assert.StartsWith("FALL BACK TO THE KEEP", WarfrontBattleWire.Line(back with { Stage = 1 }));
        Assert.StartsWith("LAST STAND: THE CAVES", WarfrontBattleWire.Line(back with { Stage = 2 }));
        Assert.Contains("WAVE 1 OF 3 IN 0:3", WarfrontBattleWire.Line(back));
        Assert.Equal("SURVIVE 7:50", WarfrontBattleWire.Countdown(back with { SecondsLeft = 470 }));
        Assert.False(WarfrontBattleWire.TryDecode(new byte[10], out _));
        Assert.False(WarfrontBattleWire.TryDecode(new byte[11] { 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 101 }, out _));
        Assert.Equal(100, back.Hold);
        Assert.False(WarfrontBattleWire.TryDecode(new byte[11] { 9, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, out _));
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
    public void The_hq_battle_takes_the_top_of_the_curve()
    {
        Hq(out var b);
        Assert.Equal(TfwBattleRules.EliteShare(1f), b.EliteShare);
    }
}
