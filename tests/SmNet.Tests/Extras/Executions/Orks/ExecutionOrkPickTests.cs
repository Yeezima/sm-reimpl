using System.Numerics;
using SmNet.Tests.Extras.Musou.Campaign;
using SmSim;
using SmSim.Extras.Executions;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Executions.Orks;

/// <summary>Executions X4: which Ork a Warfront press takes, and when a press does nothing (spec "Added content: Executions: Warfront (X4)").</summary>
public class ExecutionOrkPickTests
{
    static ExecutionRun Started(World w, int id = 1) => Assert.Single(w.ExecutionsStartedThisTick, r => r.Executor == id);

    [Fact]
    public void A_tap_beside_a_boy_at_full_health_starts_on_it()
    {
        var w = ExecutionOrkKit.Rig();
        ushort boy = ExecutionOrkKit.OrkAhead(w);
        ExecutionOrkKit.Settle(w, boy);
        ExecutionOrkKit.Tap(w);
        Assert.Null(ExecutionOrkKit.RefusedLast(w));
        var run = Started(w);
        Assert.Equal(ExecutionTargetKind.Enemy, run.TargetKind);
        Assert.Equal(boy, run.Target);
        Assert.Equal(-1, run.VictimClass);
        Assert.True(w.Musou!.ExecutionsIsHeld(boy));
    }

    [Fact]
    public void PicksSameOrkAsPrompt()
    {
        var w = ExecutionOrkKit.Rig();
        ushort near = ExecutionOrkKit.OrkAhead(w, gap: 1.9f, side: 0.4f), far = ExecutionOrkKit.OrkAhead(w, gap: 1.3f, side: -0.3f);
        ExecutionOrkKit.Settle(w, near, far);
        var (prompt, result, candidates) = w.ExecutionsWarfrontChoose(1, w.Players[1], prompt: true);
        Assert.True(result.Ok);
        Assert.Equal(2, candidates);
        ExecutionOrkKit.Tap(w);
        Assert.Equal(prompt, (ushort)Started(w).Target);
    }

    public static TheoryData<MusouEnemyType> Types() { var d = new TheoryData<MusouEnemyType>(); foreach (var t in Enum.GetValues<MusouEnemyType>()) d.Add(t); return d; }

    /// <summary>One case per enemy type: only the mapped, non-elite, non-boss kinds with rows start (the Boy's kinds and the Gretchin here); never an elite, a boss, the Bomb Squig or the Scrap Mek.</summary>
    [Theory]
    [MemberData(nameof(Types))]
    public void NeverElitesBossesSquigs(MusouEnemyType type)
    {
        var w = ExecutionOrkKit.Rig();
        ushort e = ExecutionOrkKit.OrkAhead(w, type);
        ExecutionOrkKit.Settle(w, e);
        Assert.True(w.Musou!.TryGet(e, out var ork));
        ExecutionOrkKit.Tap(w);
        bool expected = ExecutionChoice.Eligible(ork, w.Config.ExecutionRows);
        Assert.Equal(expected, w.ExecutionsStartedThisTick.Any(r => r.Target == e));
        if (MusouPowerupRules.Elite(type) || MusouPowerupRules.Boss(type) || type is MusouEnemyType.BombSquig or MusouEnemyType.ScrapMek) Assert.False(expected);
        if (type is MusouEnemyType.Boy or MusouEnemyType.ShootaBoy or MusouEnemyType.Gretchin) Assert.True(expected, $"{type} should be executable");
    }

    [Fact]
    public void GretchinOnlyWithRows()
    {
        var with = ExecutionOrkKit.Rig();
        ushort g = ExecutionOrkKit.OrkAhead(with, MusouEnemyType.Gretchin);
        ExecutionOrkKit.Settle(with, g);
        ExecutionOrkKit.Tap(with);
        Assert.Equal(g, (ushort)Started(with).Target);

        var without = ExecutionOrkKit.Rig(ExecutionOrkKit.Config(ExecutionOrkKit.OrkSet(gretchin: false)));
        ushort g2 = ExecutionOrkKit.OrkAhead(without, MusouEnemyType.Gretchin);
        ExecutionOrkKit.Settle(without, g2);
        ExecutionOrkKit.Tap(without);
        Assert.Empty(without.ExecutionsStartedThisTick);
        Assert.Equal(ExecutionRefusal.NoTarget, ExecutionOrkKit.RefusedLast(without));
    }

    [Fact]
    public void ScrapMekNever()
    {
        var w = ExecutionOrkKit.Rig();
        w.TfwMeksOn = true;
        ushort m = ExecutionOrkKit.OrkAhead(w, MusouEnemyType.ScrapMek);
        ExecutionOrkKit.Settle(w, m);
        ExecutionOrkKit.Tap(w);
        Assert.Empty(w.ExecutionsStartedThisTick);
        Assert.Null(ExecutionTargets.Of(MusouEnemyType.ScrapMek) is { } kind && w.Config.ExecutionRows.Any(r => r.Target == kind) ? "rows" : null);
    }

    [Fact]
    public void StormboyInHopNever()
    {
        // The pool's hop state is the X2 eligibility rule's to refuse (RankOrks and Eligible skip Airborne); a grounded Stormboy plays the Boy's rows.
        var rows = ExecutionOrkKit.OrkSet().Rows;
        var grounded = new MusouEnemy(5, MusouEnemyType.Stormboy, MusouEnemyState.Seek, Vector3.Zero, Vector2.Zero, 0f, 100f, -1, 0, 0, 0);
        Assert.True(ExecutionChoice.Eligible(grounded, rows));
        Assert.False(ExecutionChoice.Eligible(grounded with { State = MusouEnemyState.Airborne }, rows));
        var w = ExecutionOrkKit.Rig();
        ushort s = ExecutionOrkKit.OrkAhead(w, MusouEnemyType.Stormboy);
        ExecutionOrkKit.Settle(w, s);
        Assert.True(w.Musou!.TryGet(s, out var e));
        ExecutionOrkKit.Tap(w);
        Assert.Equal(e.State != MusouEnemyState.Airborne, w.ExecutionsStartedThisTick.Any(r => r.Target == s));
    }

    /// <summary>A row with a 2.0 m gap (near variant 1.6 m) fits at 2.45 m with a 0.45 m slide; at 2.55 m the Ork is out of the 2.5 m range and never a candidate.</summary>
    [Theory]
    [InlineData(2.45f, true)]
    [InlineData(2.55f, false)]
    public void RangeBoundary2p5(float gap, bool starts)
    {
        var rows = ExecutionRowSet.OfAll([ExecutionTestKit.Row("O1.chainsword", ExecutionTargets.OrkBoy, gap: 2.0f, near: 1.6f)]);
        var w = ExecutionOrkKit.Rig(ExecutionOrkKit.Config(rows));
        ushort boy = ExecutionOrkKit.OrkAhead(w, gap: gap);
        ExecutionOrkKit.Settle(w, boy);
        var (_, _, candidates) = w.ExecutionsWarfrontChoose(1, w.Players[1], prompt: true);
        Assert.Equal(starts ? 1 : 0, candidates > 0 && w.ExecutionsWarfrontChoose(1, w.Players[1], true).Id == boy ? 1 : 0);
        ExecutionOrkKit.Tap(w);
        Assert.Equal(starts, w.ExecutionsStartedThisTick.Any(r => r.Target == boy));
        if (!starts) Assert.Equal(ExecutionRefusal.NoTarget, ExecutionOrkKit.RefusedLast(w));
    }

    [Fact]
    public void QueryNotFullScan()
    {
        var w = ExecutionOrkKit.Rig();
        ushort boy = ExecutionOrkKit.OrkAhead(w);
        ushort far = ExecutionOrkKit.OrkAhead(w, gap: 6f);
        ExecutionOrkKit.Settle(w, boy, far);
        int before = w.ExecutionsGathers;
        ExecutionOrkKit.Tap(w);
        Assert.Equal(before + 1, w.ExecutionsGathers);   // one gather (MusouSim.Query) per press, not a walk over the pool
        var (_, _, candidates) = w.ExecutionsWarfrontChoose(1, w.Players[1], prompt: true);
        Assert.Equal(1, candidates);   // the gather sees the Boy within 3 m, never the one 6 m off
    }

    [Fact]
    public void ReservedOrkSkipped()
    {
        var w = ExecutionOrkKit.Rig(humans: 2);
        var p1 = w.Players[1];
        ushort boy = ExecutionOrkKit.OrkAhead(w, gap: 1.3f);
        Assert.True(w.Musou!.TryGet(boy, out var b));
        ExecutionOrkKit.Place(w, 2, b.Position + new Vector3(1.3f, 0f, 0f), MathF.PI / 2f);   // on the Boy's other flank, facing it (-X)
        ExecutionOrkKit.Settle(w, boy);
        ExecutionOrkKit.Tap(w, 1);
        Assert.Equal(boy, (ushort)Started(w, 1).Target);
        ExecutionOrkKit.Tap(w, 2);
        Assert.DoesNotContain(w.ExecutionsStartedThisTick, r => r.Executor == 2);
        Assert.Equal(ExecutionRefusal.NoTarget, ExecutionOrkKit.RefusedLast(w, 2));
        _ = p1;
    }

    [Fact]
    public void RegularOrksDoNotBlockSlide()
    {
        Assert.False(ExecutionBystanders.Counts(MusouEnemyType.Boy));
        Assert.False(ExecutionBystanders.Counts(MusouEnemyType.Nob));
        var w = ExecutionOrkKit.Rig();
        ushort boy = ExecutionOrkKit.OrkAhead(w, gap: 2.1f);   // a 0.8 m slide
        ushort[] crowd = [ExecutionOrkKit.OrkAhead(w, gap: 0.9f, side: 0.5f), ExecutionOrkKit.OrkAhead(w, gap: 0.9f, side: -0.5f), ExecutionOrkKit.OrkAhead(w, gap: 0.4f, side: 0.6f)];
        ExecutionOrkKit.Settle(w, [boy, .. crowd]);
        ExecutionOrkKit.Tap(w);
        Assert.Contains(w.ExecutionsStartedThisTick, r => r.Target == boy || crowd.Contains((ushort)r.Target));
    }

    [Fact]
    public void BossOrkBlocksSlide()
    {
        Assert.True(ExecutionBystanders.Counts(MusouEnemyType.Warboss));
        Assert.True(ExecutionBystanders.Counts(MusouEnemyType.BossNob));
        var near = ExecutionBystanders.Warfront(1, [(1, default), (2, new PlayerState(2, new Vector3(0.5f, 0f, 0f), Vector3.Zero, 0f, true) { Alive = true })],
            [(MusouEnemyType.Boy, new Vector3(0f, 1f, 0f)), (MusouEnemyType.BossNob, new Vector3(0f, 2f, 0f))], Vector3.Zero, 10f);
        Assert.Equal([new Vector3(0.5f, 0f, 0f), new Vector3(0f, 2f, 0f)], near);

        var w = ExecutionOrkKit.Rig();
        ushort boy = ExecutionOrkKit.OrkAhead(w, gap: 2.2f);   // needs a 0.9 m slide
        ushort boss = ExecutionOrkKit.OrkAhead(w, MusouEnemyType.BossNob, gap: 1.0f, side: 0.35f, health: 5000f);
        ExecutionOrkKit.Settle(w, boy, boss);
        ExecutionOrkKit.Tap(w);
        Assert.Empty(w.ExecutionsStartedThisTick);
        Assert.Equal(ExecutionRefusal.NoFit, ExecutionOrkKit.RefusedLast(w));
    }

    [Fact]
    public void ChaosStageTakesChaosKindsSinceX4b()
    {
        // X4b: the Boy byte of a Chaos stage is a Cultist (the renegade's rows; this rig has only Ork rows): nothing fits, and no Warfront refusal any more.
        var w = ExecutionOrkKit.Rig();
        ushort boy = ExecutionOrkKit.OrkAhead(w);
        ExecutionOrkKit.Settle(w, boy);
        w.ExecutionsChaosStageForTests = true;
        ExecutionOrkKit.Tap(w);
        Assert.Empty(w.ExecutionsStartedThisTick);
        Assert.Equal(ExecutionRefusal.NoTarget, ExecutionOrkKit.RefusedLast(w));
        Assert.Null(w.ExecutionsWarfrontRefusedLast(1));
        w.ExecutionsChaosStageForTests = false;
        ExecutionOrkKit.Idle(w);
        ExecutionOrkKit.Tap(w);
        Assert.Equal(boy, (ushort)Started(w).Target);
    }

    [Fact]
    public void RetainerRefused()
    {
        var w = ExecutionOrkKit.Rig();
        int puppet = TfwContracts.PuppetIdFirst;
        w.Spawn(puppet, w.AssignClass(0));
        var at = w.Players[1].Position + new Vector3(3f, 0f, 0f);
        ExecutionOrkKit.Place(w, puppet, at, 0f);
        ushort boy = ExecutionOrkKit.OrkAhead(w, id: puppet);
        ExecutionOrkKit.Settle(w, boy);
        bool press = false;
        w.Tfw!.PuppetBrain = (id, p) => new PlayerInput(0, 0f, 0f, p.Yaw, press ? InputButtons.Execute : InputButtons.None, 0f, w.Tick);
        press = true;
        ExecutionOrkKit.Idle(w);
        Assert.Empty(w.ExecutionsStartedThisTick);
        Assert.Equal(ExecutionRefusal.Puppet, ExecutionOrkKit.RefusedLast(w, puppet));
    }

    [Fact]
    public void HubRefused()
    {
        var w = TfwRig.War(config: ExecutionOrkKit.Config());
        Assert.Equal(TfwRunPhase.Hub, w.Tfw!.Phase);
        ExecutionOrkKit.Tap(w);
        Assert.Empty(w.ExecutionsStartedThisTick);
        Assert.Equal(ExecutionRefusal.Refused, ExecutionOrkKit.RefusedLast(w));
        Assert.Equal(ExecutionWarfrontRefusal.NotInBattle, w.ExecutionsWarfrontRefusedLast(1));
    }

    [Fact]
    public void DownedRefused()
    {
        var w = ExecutionOrkKit.Rig();
        ushort boy = ExecutionOrkKit.OrkAhead(w);
        ExecutionOrkKit.Settle(w, boy);
        Assert.True(w.TfwKillForTests(1));
        ExecutionOrkKit.Tap(w);
        Assert.Empty(w.ExecutionsStartedThisTick);
        Assert.Equal(ExecutionRefusal.Dead, ExecutionOrkKit.RefusedLast(w));
    }
}
