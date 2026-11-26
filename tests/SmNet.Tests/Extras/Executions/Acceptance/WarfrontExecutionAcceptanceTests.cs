using System.Numerics;
using SmNet.Tests.Extras.Executions.Orks;
using SmNet.Tests.Extras.Musou.Campaign;
using SmSim;
using SmSim.Extras.Executions;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Executions.Acceptance;

/// <summary>
/// Executions X8 drop 2: the Warfront acceptance suite, written from the owner brief's Verify items (not from the implementers' unit tests). Everything is synthetic (no original bytes):
/// <see cref="ExecutionOrkKit"/>'s quiet Warfront rig (one human, one synthetic Boy row, one Gretchin row, a chainsword class) and <see cref="ExecutionFixtures"/>' levels. Expectations
/// are hard-coded here from the brief, never read back from the product's tables.
/// Known phase-1 gaps (X4 phase 2 and 3) are <c>Skip</c>ped with their exact expectation.
/// </summary>
public class WarfrontExecutionAcceptanceTests
{
    // ---- helpers (all local: the suite stands on the sim's public surface and the kit's rig) ----

    static BodyStats Body(World w, int id = 1) => Loadouts.Body(w.Config, w.Players[id]);

    /// <summary>Presses Execute beside a fresh Boy and runs the move out; the world, the Ork and the end. The executor's vitals are set first when given.</summary>
    static (World W, ushort Ork, ExecutionEnded End) Execute(World? world = null, float health = -1f, float shield = -1f, MusouEnemyType type = MusouEnemyType.Boy, float orkHealth = 100f, float side = 0f)
    {
        var w = world ?? ExecutionOrkKit.Rig();
        ushort ork = ExecutionOrkKit.OrkAhead(w, type, side: side, health: orkHealth);
        ExecutionOrkKit.Settle(w, ork);
        if (health >= 0f) w.ExecutionsSetVitalsForTests(1, health, shield);
        ExecutionOrkKit.Tap(w);
        Assert.Contains(w.ExecutionsStartedThisTick, r => r.Executor == 1 && r.Target == ork);
        var end = ExecutionOrkKit.RunOut(w);
        Assert.NotNull(end);
        return (w, ork, end!.Value);
    }

    // ---- eligibility ----

    /// <summary>
    /// The brief's table (owner 2026-11-25), hard-coded: Boy, Shoota Boy, 'Ard Boy, Rokkit Boy, a grounded Stormboy and (with rows) a Gretchin may be executed; never the Nob, the Big Shoota Nob,
    /// the Chaos Marine slot, the Warboss, the Boss Orks, the Weirdboy, the Bomb Squig or the Scrap Mek.
    /// </summary>
    public static TheoryData<MusouEnemyType, bool> Table()
    {
        var d = new TheoryData<MusouEnemyType, bool>();
        foreach (var t in Enum.GetValues<MusouEnemyType>())
            d.Add(t, t is MusouEnemyType.Boy or MusouEnemyType.ShootaBoy or MusouEnemyType.ArdBoy or MusouEnemyType.RokkitBoy or MusouEnemyType.Stormboy or MusouEnemyType.Gretchin);
        return d;
    }

    [Theory]
    [MemberData(nameof(Table))]
    public void EveryEnemyType_Eligibility_Table(MusouEnemyType type, bool executable)
    {
        var w = ExecutionOrkKit.Rig();
        bool big = type is MusouEnemyType.Warboss or MusouEnemyType.BossBoy or MusouEnemyType.BossShootaBoy or MusouEnemyType.BossArdBoy or MusouEnemyType.BossNob;
        ushort e = ExecutionOrkKit.OrkAhead(w, type, health: big ? 5000f : 100f);
        ExecutionOrkKit.Settle(w, e);
        Assert.True(w.Musou!.TryGet(e, out var ork));
        ExecutionOrkKit.Tap(w);
        bool started = w.ExecutionsStartedThisTick.Any(r => r.Executor == 1 && r.Target == e);
        if (type == MusouEnemyType.Stormboy && ork.State == MusouEnemyState.Airborne) executable = false;   // mid-hop: never (the brief: grounded only)
        Assert.Equal(executable, started);
        if (!executable) Assert.False(w.Musou.ExecutionsIsHeld(e), $"{type} must never be held");
    }

    [Fact]
    public void Gretchin_NeedsItsRows()
    {
        var w = ExecutionOrkKit.Rig(ExecutionOrkKit.Config(ExecutionOrkKit.OrkSet(gretchin: false)));
        ushort g = ExecutionOrkKit.OrkAhead(w, MusouEnemyType.Gretchin);
        ExecutionOrkKit.Settle(w, g);
        ExecutionOrkKit.Tap(w);
        Assert.Empty(w.ExecutionsStartedThisTick);
    }

    // ---- any health, no stun ----

    [Theory]
    [InlineData(1f)]
    [InlineData(35f)]
    [InlineData(100f)]
    [InlineData(1e5f)]
    public void AnyHealth_NoStunNeeded(float orkHealth)
    {
        var w = ExecutionOrkKit.Rig();
        ushort boy = ExecutionOrkKit.OrkAhead(w, health: orkHealth);
        ExecutionOrkKit.Settle(w, boy);
        Assert.True(w.Musou!.TryGet(boy, out var before));
        Assert.NotEqual(MusouEnemyState.Stagger, before.State);   // not stunned, staggered or knocked: a plain fighting Ork
        Assert.NotEqual(MusouEnemyState.Knocked, before.State);
        ExecutionOrkKit.Tap(w);
        var run = Assert.Single(w.ExecutionsStartedThisTick);
        Assert.Equal(boy, (ushort)run.Target);
        var end = ExecutionOrkKit.RunOut(w);
        Assert.Equal(ExecutionEndReason.Done, end!.Value.Reason);
        Assert.True(!w.Musou.TryGet(boy, out var after) || !after.Alive, "every health dies at the kill frame");
    }

    // ---- no pull through terrain, slide cap 1 m ----

    static (ushort Id, ExecutionAlignResult R) Choose(World w, CollisionWorld level, float orkY, float orkZ = 0f)
    {
        var exec = w.Players[1] with { Position = Vector3.Zero, Yaw = 0f };
        var ork = new MusouEnemy(5, MusouEnemyType.Boy, MusouEnemyState.Seek, new Vector3(0f, orkY, orkZ), Vector2.Zero, MathF.PI, 100f, -1, 0, 0, 0);
        return ExecutionChoice.ChooseAlignedOrk(exec, 0f, "sm - tactical", "sm_mp_melee_chainsword", [ork], w.Config.ExecutionRows, w.Config, level);
    }

    [Fact]
    public void NoPullThroughTerrainOrAcrossGap()
    {
        var w = ExecutionOrkKit.Rig();
        var ok = Choose(w, ExecutionFixtures.Flat(), 2.3f);   // the control: flat floor, the Ork 2.3 m ahead, a 1.0 m slide to the row's 1.3 m gap
        Assert.Equal(5, ok.Id);
        Assert.True(ok.R.Ok, ok.R.Failure.ToString());
        Assert.True(ok.R.Slide <= ExecutionAlign.MaxSlide + 1e-3f, $"slide {ok.R.Slide}");
        Assert.Equal(1f, ExecutionAlign.MaxSlide);   // Warfront's cap, the brief

        (string Name, CollisionWorld Level, float Z)[] bad =
        [
            ("wall", ExecutionFixtures.Walled(), 0f),
            ("gap", ExecutionFixtures.Gapped(), 0f),
            ("ledge down", ExecutionFixtures.Stepped(0.9f, -1.0f), -1.0f),
            ("high step up", ExecutionFixtures.Stepped(0.9f, 0.6f), 0.6f),
            ("steep slope", ExecutionFixtures.Sloped(0f, 45f), ExecutionFixtures.SlopeZ(2.3f, 0f, 45f)),
        ];
        foreach (var (name, level, z) in bad)
        {
            var r = Choose(w, level, 2.3f, z);
            Assert.True(r.Id == 0 && !r.R.Ok, $"{name}: the move must be refused, never pulled through (picked {r.Id})");
        }
    }

    [Theory]
    [InlineData(2.2f, true)]    // 0.9 m slide
    [InlineData(2.45f, false)]  // 1.15 m slide (to the far variant) or 1.45 m (near): over the 1 m cap
    public void SlideCap_1m(float gap, bool starts)
    {
        var w = ExecutionOrkKit.Rig();
        ushort boy = ExecutionOrkKit.OrkAhead(w, gap: gap);
        ExecutionOrkKit.Settle(w, boy);
        ExecutionOrkKit.Tap(w);
        Assert.Equal(starts, w.ExecutionsStartedThisTick.Any(r => r.Target == boy));
        if (!starts) { Assert.Equal(ExecutionRefusal.NoFit, ExecutionOrkKit.RefusedLast(w)); return; }
        Assert.NotNull(ExecutionOrkKit.RunOut(w));
    }

    // ---- invulnerable during, normal after ----

    public static TheoryData<MusouEnemyType, bool> Attackers() => new()
    {
        { MusouEnemyType.Boy, true }, { MusouEnemyType.ArdBoy, true }, { MusouEnemyType.Nob, true }, { MusouEnemyType.BossNob, true }, { MusouEnemyType.Warboss, true },
        { MusouEnemyType.ShootaBoy, false }, { MusouEnemyType.RokkitBoy, false }, { MusouEnemyType.BigShootaNob, false }, { MusouEnemyType.ChaosMarine, false },
    };

    [Theory]
    [MemberData(nameof(Attackers))]
    public void EveryDamageSource_DuringIsZero_AfterIsNormal(MusouEnemyType attacker, bool melee)
    {
        var w = ExecutionOrkKit.Rig();
        ushort boy = ExecutionOrkKit.OrkAhead(w);
        bool big = attacker is MusouEnemyType.BossNob or MusouEnemyType.Warboss;
        ushort other = ExecutionOrkKit.OrkAhead(w, attacker, gap: 1.2f, side: 1.0f, health: big ? 1e6f : 1e5f);
        ExecutionOrkKit.Settle(w, boy, other);
        Assert.Equal(1f, w.DamageTakenScale(1));
        ExecutionOrkKit.Tap(w);
        Assert.Contains(w.ExecutionsStartedThisTick, r => r.Target == boy);
        Assert.Equal(0f, w.DamageTakenScale(1));   // during: nothing gets through the scale every hit path uses
        w.Musou!.Frozen = false;
        float health = w.Players[1].Health, shield = w.Players[1].Shield;
        ExecutionEnded? end = null;
        for (int k = 0; k < 300 && end is null; k++)
        {
            ExecutionOrkKit.KillOthers(w, boy, other);
            ExecutionOrkKit.Idle(w);
            if (w.ExecutionsEndedThisTick.FirstOrDefault(e => e.Run.Executor == 1) is { Run.Executor: 1 } e) end = e;
            else { Assert.True(w.Players[1].Health >= health && w.Players[1].Shield >= shield, $"{attacker} hurt the executor during the move"); }
        }
        Assert.NotNull(end);
        Assert.Equal(1f, w.DamageTakenScale(1));   // after: normal
        if (!melee) return;
        // after the move the same Ork's blows land (melee attackers beside the executor)
        float full = Body(w).HealthMax + 2f * Body(w).ShieldMax;
        for (int k = 0; k < 300 && w.Players[1].Health + w.Players[1].Shield >= full - 0.5f; k++)
        {
            ExecutionOrkKit.KillOthers(w, other);
            ExecutionOrkKit.Idle(w);
        }
        Assert.True(w.Players[1].Health + w.Players[1].Shield < full - 0.5f, $"{attacker} never hurt the executor after the move");
    }

    // ---- reward ----

    [Theory]
    [InlineData(30f, 0f)]
    [InlineData(1f, 10f)]
    [InlineData(-1f, -1f)]   // full vitals to begin with
    public void Reward_Values_Exact(float health, float shield)
    {
        var (w, _, end) = Execute(health: health, shield: shield);
        Assert.Equal(ExecutionEndReason.Done, end.Reason);
        var body = Body(w);
        Assert.True(body.HealthMax > 0f && body.ShieldMax > 0f);
        Assert.Equal(body.HealthMax, w.Players[1].Health);
        Assert.Equal(2f * body.ShieldMax, w.Players[1].Shield);   // full armour plus one bar of overshield: 200 %
        var reward = Assert.Single(w.ExecutionsRewardedThisTick);
        Assert.Equal(body.HealthMax, reward.Health);
        Assert.Equal(2f * body.ShieldMax, reward.Shield);
    }

    [Fact]
    public void InterruptedMove_GivesNoRewardAndNoCooldown()
    {
        var w = ExecutionOrkKit.Rig();
        ushort boy = ExecutionOrkKit.OrkAhead(w);
        ExecutionOrkKit.Settle(w, boy);
        w.ExecutionsSetVitalsForTests(1, 40f, 0f);
        ExecutionOrkKit.Tap(w);
        Assert.Single(w.ExecutionsStartedThisTick);
        ExecutionOrkKit.Idle(w);
        w.ExecutionsAbort(1, ExecutionEndReason.Disconnect);
        ExecutionOrkKit.Idle(w);
        Assert.Empty(w.ExecutionsRewardedThisTick);
        Assert.True(w.Players[1].Health < Body(w).HealthMax);
        Assert.True(w.Players[1].Shield <= Body(w).ShieldMax);
        Assert.Equal(0u, w.ExecutionsReadyAt(1));
        Assert.False(w.Musou!.ExecutionsIsHeld(boy));
    }

    [Fact]
    public void Depletion_Order_OvershieldThenArmourThenHealth()
    {
        var (w, _, _) = Execute(health: 30f, shield: 0f);
        var p = w.Players[1];
        var body = Body(w);
        // 1.5 bars: the overshield (one bar) and half the armour go; health is untouched
        var (a, _, hurt) = Combat.ApplyDamage(p, 1.5f * body.ShieldMax, 1f, 1f, w.Config);
        Assert.Equal(0f, hurt);
        Assert.Equal(body.HealthMax, a.Health);
        Assert.Equal(0.5f * body.ShieldMax, a.Shield, 2);
        // more than both bars: only then does health fall
        var (b, _, hurt2) = Combat.ApplyDamage(p, 2f * body.ShieldMax + 20f, 1f, 1f, w.Config);
        Assert.Equal(0f, b.Shield, 2);
        Assert.True(hurt2 > 0f && b.Health < body.HealthMax);
    }

    [Fact]
    public void NoDecay_120s()
    {
        var (w, _, _) = Execute();
        float shield = w.Players[1].Shield;
        Assert.Equal(2f * Body(w).ShieldMax, shield);
        for (int k = 0; k < 120 * w.Config.TickRate; k++) { ExecutionOrkKit.KillOthers(w); ExecutionOrkKit.Idle(w); }
        Assert.Equal(shield, w.Players[1].Shield);
        Assert.Equal(Body(w).HealthMax, w.Players[1].Health);
    }

    [Theory]
    [InlineData(-1f)]    // untouched 200 %
    [InlineData(1.4f)]   // 140 % left of 200 % (some overshield spent)
    [InlineData(0.3f)]   // armour spent, no overshield
    public void Refresh_NoStack(float shieldBars)
    {
        var (w, _, _) = Execute(health: 30f, shield: 0f);
        var body = Body(w);
        for (int k = 0; k < ExecutionRules.CooldownTicks(w.Config) + 2; k++) { ExecutionOrkKit.KillOthers(w); ExecutionOrkKit.Idle(w); }
        float start = shieldBars < 0f ? 2f * body.ShieldMax : shieldBars * body.ShieldMax;
        var (_, _, end) = Execute(w, body.HealthMax, start);
        Assert.Equal(ExecutionEndReason.Done, end.Reason);
        Assert.Equal(2f * body.ShieldMax, w.Players[1].Shield);   // refreshed to the cap, never 300 %
    }

    // ---- cooldown ----

    /// <summary>One completed execution, then a fresh Boy and a press exactly <paramref name="seconds"/> after the completion tick.</summary>
    static (World W, bool Started) PressAfter(float seconds)
    {
        var (w, _, end) = Execute();
        Assert.Equal(ExecutionEndReason.Done, end.Reason);
        uint at = end.Tick + (uint)MathF.Round(seconds * w.Config.TickRate);
        ushort boy = ExecutionOrkKit.OrkAhead(w, gap: 1.3f, side: 0.6f);
        ExecutionOrkKit.Settle(w, boy);
        while (w.Tick + 1 < at) { ExecutionOrkKit.KillOthers(w, boy); ExecutionOrkKit.Idle(w); }
        Assert.Equal(at, w.Tick + 1);
        ExecutionOrkKit.Tap(w);
        return (w, w.ExecutionsStartedThisTick.Any(r => r.Executor == 1));
    }

    [Fact]
    public void Cooldown_9p9_Refused()
    {
        var (w, started) = PressAfter(9.9f);
        Assert.False(started);
        Assert.Equal(ExecutionRefusal.Refused, ExecutionOrkKit.RefusedLast(w));
        Assert.Equal(ExecutionWarfrontRefusal.Cooldown, w.ExecutionsWarfrontRefusedLast(1));
    }

    [Fact]
    public void Cooldown_10p0_Accepted()
    {
        var (_, started) = PressAfter(10.0f);
        Assert.True(started);
    }

    [Fact]
    public void Cooldown_ClearedByDeathAndRespawn()
    {
        var (w, _, _) = Execute();
        Assert.NotEqual(0u, w.ExecutionsReadyAt(1));
        Assert.True(w.TfwKillForTests(1));
        ExecutionOrkKit.Idle(w);   // the death is seen
        Assert.True(w.TfwRespawnAt(1, w.Players[1].Position, 0f, faceCentre: false));
        ushort boy = ExecutionOrkKit.OrkAhead(w, gap: 1.3f);
        ExecutionOrkKit.Settle(w, boy);
        Assert.Equal(0u, w.ExecutionsReadyAt(1));
        ExecutionOrkKit.Tap(w);
        Assert.Contains(w.ExecutionsStartedThisTick, r => r.Executor == 1);   // straight away, well inside 10 s
    }

    [Fact]
    public void Cooldown_ClearedByTheWarfrontRespawn()
    {
        var w = ExecutionOrkKit.Rig(humans: 2);
        ExecutionOrkKit.Place(w, 2, w.Players[1].Position + new Vector3(6f, 0f, 0f));
        Execute(w);
        Assert.NotEqual(0u, w.ExecutionsReadyAt(1));
        Assert.True(w.TfwKillForTests(1));
        bool back = false;
        for (int k = 0; k < 120 * w.Config.TickRate && !back; k++) { ExecutionOrkKit.KillOthers(w); ExecutionOrkKit.Idle(w); back = w.Players[1].Alive; }
        Assert.True(back, "the human never came back");
        Assert.Equal(0u, w.ExecutionsReadyAt(1));
    }

    // ---- retainers ----

    [Fact]
    public void Retainers_NeverExecute()
    {
        var w = ExecutionOrkKit.Rig();
        int puppet = TfwContracts.PuppetIdFirst;
        w.Spawn(puppet, w.AssignClass(0));
        ExecutionOrkKit.Place(w, puppet, w.Players[1].Position + new Vector3(3f, 0f, 0f), 0f);
        ushort boy = ExecutionOrkKit.OrkAhead(w, id: puppet);
        ExecutionOrkKit.Settle(w, boy);
        bool pressNow = true;
        w.Tfw!.PuppetBrain = (id, p) => new PlayerInput(0, 0f, 0f, p.Yaw, pressNow ? InputButtons.Execute : InputButtons.None, 0f, w.Tick);
        for (int k = 0; k < 40; k++)
        {
            pressNow = k % 2 == 0;   // Execute is an edge: press, release, press...
            ExecutionOrkKit.Idle(w);
            Assert.Empty(w.ExecutionsStartedThisTick);
            Assert.False(w.Musou!.ExecutionsIsHeld(boy));
        }
        pressNow = true;
        ExecutionOrkKit.Idle(w);
        Assert.Equal(ExecutionRefusal.Puppet, ExecutionOrkKit.RefusedLast(w, puppet));
        Assert.Empty(w.ExecutionsRewardedThisTick);
    }

    // ---- overshield lifetime ----

    [Fact]
    public void Overshield_LostOnDeath_AndStageChange_NotSaved()
    {
        var (w, _, _) = Execute();
        Assert.True(w.Players[1].Shield > Body(w).ShieldMax);
        // death
        Assert.True(w.TfwKillForTests(1));
        Assert.True(w.TfwRespawnAt(1, w.Players[1].Position, 0f, faceCentre: false));
        Assert.Equal(Body(w).ShieldMax, w.Players[1].Shield);
        // a stage change starts a fresh process: the new war's human has the plain bar
        var next = ExecutionOrkKit.Rig();
        Assert.Equal(Body(next).ShieldMax, next.Players[1].Shield);
        // never saved: the Warfront save holds no vitals (WarfrontExecutionSaveAcceptanceTests, in the Warfront test folder)
    }

    // ---- squigs ----

    static int Squigs(World w, int seconds)
    {
        w.Tfw!.Guardsmen = false;
        w.Tfw.Battle!.WeirdboyzOn = false;
        for (int k = 0; k < seconds * w.Config.TickRate; k++) { w.Step(_ => default); }
        return w.TfwSquigsDropped;
    }

    [Fact]
    public void Squigs_PresentInMusou()
    {
        var w = TfwRig.War(config: TfwRig.Config);
        TfwRig.Deploy(w, TfwLane.B);
        Assert.True(Squigs(w, (int)MusouSquigRules.DropMaxSeconds + 10) > 0, "Musou Mode drops Bomb Squigs in a battle");
    }

    [Fact]
    public void Squigs_AbsentInWarfront()
    {
        var w = ExecutionOrkKit.Battle();
        Assert.Equal(0, Squigs(w, (int)MusouSquigRules.DropMaxSeconds + 10));
    }

    // ---- Chaos ----

    [Fact]
    public void ChaosRun_TakesTheChaosKindsRows()
    {
        // X4b (owner 2026-11-26): a Chaos stage is no longer refused; its Boy byte is a Cultist, which takes the renegade's rows: with the Ork rows alone nothing fits (NoTarget, no Warfront refusal).
        var w = ExecutionOrkKit.Rig();
        ushort boy = ExecutionOrkKit.OrkAhead(w);
        ExecutionOrkKit.Settle(w, boy);
        w.ExecutionsChaosStageForTests = true;
        ExecutionOrkKit.Tap(w);
        Assert.Empty(w.ExecutionsStartedThisTick);
        Assert.False(w.Musou!.ExecutionsIsHeld(boy));
        Assert.Null(w.ExecutionsWarfrontRefusedLast(1));
        Assert.Contains((1, ExecutionRefusal.NoTarget), w.ExecutionsRefusedThisTick);
        w.ExecutionsChaosStageForTests = false;
        ExecutionOrkKit.Idle(w);
        ExecutionOrkKit.Tap(w);
        Assert.Single(w.ExecutionsStartedThisTick);
    }

    // ---- phases ----

    [Fact(Skip = "Unreachable synthetically: Warfront drops RequestPickup, ReturnToShip and CancelPickup (TfwRun.Warfront.cs, WarfrontCommand) and the test aid PickupForTests leaves the battle at once, so the Pickup phase never occurs; the guard (World.ExecutionsInBattle allows Pickup) is still pinned by X4's own review. Expectation: a press in TfwRunPhase.Pickup starts a run beside a Boy")]
    public void Pickup_Phase_Allowed() { }

    [Fact]
    public void Hub_Refused()
    {
        var w = TfwRig.War(config: ExecutionOrkKit.Config());
        Assert.Equal(TfwRunPhase.Hub, w.Tfw!.Phase);
        ExecutionOrkKit.Tap(w);
        Assert.Empty(w.ExecutionsStartedThisTick);
        Assert.Equal(ExecutionWarfrontRefusal.NotInBattle, w.ExecutionsWarfrontRefusedLast(1));
    }

    // ---- bystanders: players and Warboss/Boss Orks only ----

    [Fact]
    public void Bystanders_AreOtherPlayersAndPlayerSolidBigOrksOnly()
    {
        foreach (var t in Enum.GetValues<MusouEnemyType>())
        {
            bool expected = t is MusouEnemyType.Warboss or MusouEnemyType.BossBoy or MusouEnemyType.BossShootaBoy or MusouEnemyType.BossArdBoy or MusouEnemyType.BossNob;
            Assert.Equal(expected, ExecutionBystanders.Counts(t));
        }
        var self = new PlayerState(1, new Vector3(0f, 0f, 0f), Vector3.Zero, 0f, true) { Alive = true };
        var live = new PlayerState(2, new Vector3(0.5f, 0f, 0f), Vector3.Zero, 0f, true) { Alive = true };
        var dead = new PlayerState(3, new Vector3(0.6f, 0f, 0f), Vector3.Zero, 0f, true) { Alive = false };
        var list = ExecutionBystanders.Warfront(1, [(1, self), (2, live), (3, dead)],
            [(MusouEnemyType.Boy, new Vector3(0f, 1f, 0f)), (MusouEnemyType.Nob, new Vector3(0f, 1.2f, 0f)), (MusouEnemyType.Warboss, new Vector3(0f, 2f, 0f))], Vector3.Zero, 10f);
        Assert.Equal([live.Position, new Vector3(0f, 2f, 0f)], list);   // not the executor, not the dead, not a Boy or a Nob
    }

    [Fact]
    public void AnotherPlayerOnThePath_BlocksTheSlide_ButOnlyThen()
    {
        // control: alone, a 0.9 m slide fits
        var alone = ExecutionOrkKit.Rig();
        ushort a = ExecutionOrkKit.OrkAhead(alone, gap: 2.2f);
        ExecutionOrkKit.Settle(alone, a);
        ExecutionOrkKit.Tap(alone);
        Assert.Single(alone.ExecutionsStartedThisTick);

        var w = ExecutionOrkKit.Rig(humans: 2);
        ushort boy = ExecutionOrkKit.OrkAhead(w, gap: 2.2f);
        var p1 = w.Players[1];
        var fwd = new Vector3(-MathF.Sin(p1.Yaw), MathF.Cos(p1.Yaw), 0f);
        ExecutionOrkKit.Place(w, 2, p1.Position + fwd * 1.1f + new Vector3(fwd.Y, -fwd.X, 0f) * 0.3f, p1.Yaw);
        ExecutionOrkKit.Settle(w, boy);
        ExecutionOrkKit.Tap(w);
        Assert.DoesNotContain(w.ExecutionsStartedThisTick, r => r.Executor == 1);
        Assert.Equal(ExecutionRefusal.NoFit, ExecutionOrkKit.RefusedLast(w));
    }

    [Theory]
    [InlineData(MusouEnemyType.Warboss)]
    [InlineData(MusouEnemyType.BossNob)]
    public void BigOrkOnThePath_BlocksTheSlide(MusouEnemyType big)
    {
        var w = ExecutionOrkKit.Rig();
        ushort boy = ExecutionOrkKit.OrkAhead(w, gap: 2.2f);
        ushort boss = ExecutionOrkKit.OrkAhead(w, big, gap: 1.1f, side: 0.3f, health: 5000f);
        ExecutionOrkKit.Settle(w, boy, boss);
        ExecutionOrkKit.Tap(w);
        Assert.DoesNotContain(w.ExecutionsStartedThisTick, r => r.Target == boy);
    }

    [Fact]
    public void RegularOrks_NeverBlock()
    {
        var w = ExecutionOrkKit.Rig();
        ushort boy = ExecutionOrkKit.OrkAhead(w, gap: 2.2f);
        ushort[] crowd = [ExecutionOrkKit.OrkAhead(w, gap: 1.1f, side: 0.3f), ExecutionOrkKit.OrkAhead(w, gap: 1.1f, side: -0.3f), ExecutionOrkKit.OrkAhead(w, MusouEnemyType.Nob, gap: 0.8f, side: 0f, health: 1e5f)];
        ExecutionOrkKit.Settle(w, [boy, .. crowd]);
        ExecutionOrkKit.Tap(w);
        Assert.Single(w.ExecutionsStartedThisTick);   // a crowd never refuses a press
        Assert.Equal(ExecutionEndReason.Done, ExecutionOrkKit.RunOut(w)!.Value.Reason);
    }

    // ---- known phase-1 gaps: the brief's expectation, skipped until the phase lands ----

    [Fact]   // X4 phase 2 (wired at the series-37 integration): a held Ork is immune to every other hit, stagger and knockdown until its kill frame
    public void HeldOrk_ImmuneToOtherDamage_UntilTheKillFrame()
    {
        var w = ExecutionOrkKit.Rig();
        ushort boy = ExecutionOrkKit.OrkAhead(w);
        ExecutionOrkKit.Settle(w, boy);
        ExecutionOrkKit.Tap(w);
        Assert.True(w.Musou!.ExecutionsIsHeld(boy));
        Assert.True(w.Musou.TryGet(boy, out var before));
        Assert.False(w.Musou.Damage(boy, 2, 5f, Vector3.Zero, out bool killed));
        Assert.False(killed);
        Assert.True(w.Musou.TryGet(boy, out var after));
        Assert.Equal(before.Health, after.Health);
        Assert.NotEqual(MusouEnemyState.Stagger, after.State);
        Assert.NotEqual(MusouEnemyState.Dying, after.State);
    }

    [Fact]   // X4 phase 3 (series-37 integration): a melee heal never trims the overshield
    public void MeleeHeal_KeepsOvershield() => new SmNet.Tests.Extras.Executions.Orks.ExecutionRewardTests().MeleeHealKeepsOvershield();

    [Fact]   // X4 phase 3 (series-37 integration): a level-up refill keeps the overshield: max(shield, the bar)
    public void LevelUpRefill_KeepsOvershield() => new SmNet.Tests.Extras.Executions.Orks.ExecutionRewardTests().LevelUpRefillKeepsOvershield();
}
