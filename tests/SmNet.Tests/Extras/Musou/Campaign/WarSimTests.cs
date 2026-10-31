using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Musou.Campaign;

/// <summary>
/// The war between the battles (TF2): determinism, the design's calibration targets as scripted runs, the multipliers, the player's effects and the gather and wound timers. A run's
/// real time is counted in <see cref="WarSim.Step"/> calls (1 Hz).
/// </summary>
public class WarSimTests
{
    const int Minute = 60;
    const int Travel = 5, Pickup = 20;   // design section D: 5 s of travel, a free 20 s pickup after a win

    readonly record struct Times(int Siege, int Fall, sbyte HighestFront, int Unlock);

    /// <summary>Runs an unattended war in <paramref name="phase"/> until an HQ falls: the real seconds to the first siege and to the fall, the highest front any lane reached, the first unlock.</summary>
    static Times Unattended(TfwRunPhase phase, int limit = 8 * 3600)
    {
        var war = new WarSim();
        war.Enter(phase);
        int siege = -1, unlock = -1;
        sbyte highest = sbyte.MinValue;
        for (int t = 1; t <= limit; t++)
        {
            war.Step();
            foreach (TfwLane lane in Enum.GetValues<TfwLane>())
            {
                sbyte front = war.LaneState(lane).Front;
                highest = Math.Max(highest, front);
                if (front == WarRules.FrontBesieged && siege < 0) siege = t;
            }
            if (war.FinalLane is not null && unlock < 0) unlock = t;
            if (war.Phase == TfwRunPhase.Lost) return new Times(siege, t, highest, unlock);
        }
        throw new InvalidOperationException("no HQ fell");
    }

    static void Steps(WarSim war, TfwRunPhase phase, TfwLane lane, int seconds)
    {
        war.Enter(phase, lane);
        for (int t = 0; t < seconds; t++) war.Step();
    }

    /// <summary>A run of wins on one lane until the final assault opens: real seconds, or -1 when a lane is besieged first.</summary>
    static int WinsUntilUnlock(WarSim war, TfwLane lane, int battle, int hub, out int wins)
    {
        int real = 0;
        wins = 0;
        while (war.FinalLane is null)
        {
            Steps(war, TfwRunPhase.Hub, lane, hub);
            Steps(war, TfwRunPhase.Travel, lane, Travel);
            Steps(war, TfwRunPhase.Battle, lane, battle);
            war.PlayerWon(lane);
            wins++;
            Steps(war, TfwRunPhase.Pickup, lane, Pickup);
            real += hub + Travel + battle + Pickup;
            if (AnyBesieged(war)) return -1;
            if (wins > 8) throw new InvalidOperationException("no unlock");
        }
        return real;
    }

    static bool AnyBesieged(WarSim war) => Enum.GetValues<TfwLane>().Any(l => war.LaneState(l).Front == WarRules.FrontBesieged);

    [Fact]
    public void StartsAsDesigned()
    {
        var war = new WarSim();
        Assert.Equal(new TfwLaneState(1, 0f, 100f, 110f, 0b001, 0f, WarRules.LordPresent), war.LaneState(TfwLane.A));
        Assert.Equal(new TfwLaneState(0, 0f, 100f, 110f, 0b000, 0f, WarRules.LordPresent), war.LaneState(TfwLane.B));
        Assert.Equal(new TfwLaneState(1, 0f, 100f, 110f, 0b001, 0f, WarRules.LordPresent), war.LaneState(TfwLane.C));
        Assert.Equal(TfwRunPhase.Hub, war.Phase);
        Assert.Equal(0.25, war.TimeScale);
        Assert.Null(war.FinalLane);
    }

    static WarSim Scripted()
    {
        var war = new WarSim();
        Steps(war, TfwRunPhase.Hub, TfwLane.A, 90);
        Steps(war, TfwRunPhase.Battle, TfwLane.B, 400);
        war.WoundLord(TfwLane.C);
        war.PlayerLeft(TfwLane.B, 2, 130, 12);
        Steps(war, TfwRunPhase.Travel, TfwLane.A, 200);
        Steps(war, TfwRunPhase.Battle, TfwLane.A, 500);
        war.PlayerWon(TfwLane.A);
        Steps(war, TfwRunPhase.Pickup, TfwLane.A, 20);
        Steps(war, TfwRunPhase.Hub, TfwLane.A, 3000);
        return war;
    }

    [Fact]
    public void TwoRunsHashEqual()
    {
        Assert.Equal(Scripted().StateHash(), Scripted().StateHash());
        Assert.Equal(Scripted().View(5, 4, 7), Scripted().View(5, 4, 7));
    }

    [Fact]
    public void TheHashSeesAChange()
    {
        var other = Scripted();
        other.Step();
        Assert.NotEqual(Scripted().StateHash(), other.StateHash());
    }

    [Fact]
    public void AnUnattendedHqFallsIn35To50Minutes()
    {
        Times times = Unattended(TfwRunPhase.Travel);
        Assert.InRange(times.Fall, 35 * Minute, 50 * Minute);
    }

    [Fact]
    public void AnUnattendedWarNeverOpensTheFinalAssault()
    {
        Times times = Unattended(TfwRunPhase.Travel);
        Assert.Equal(-1, times.Unlock);
        Assert.True(times.HighestFront < WarRules.FrontStronghold);
    }

    [Fact]
    public void TheHubIsAboutFourTimesSlower()
    {
        double ratio = Unattended(TfwRunPhase.Hub).Fall / (double)Unattended(TfwRunPhase.Travel).Fall;
        Assert.InRange(ratio, 3.9, 4.1);
    }

    [Fact]
    public void ThreeWinsOnOneLaneUnlockTheFinalBeforeAnySiege()
    {
        var war = new WarSim();
        int real = WinsUntilUnlock(war, TfwLane.B, 8 * Minute, 1 * Minute, out int wins);
        Assert.Equal(3, wins);
        Assert.Equal(TfwLane.B, war.FinalLane);
        Assert.InRange(real, 26 * Minute, 30 * Minute);   // the -1 of a siege fails here too
    }

    [Theory]
    [InlineData(TfwLane.A)]
    [InlineData(TfwLane.C)]
    public void ALaneAlreadyHoldingANodeNeedsFewerWins(TfwLane lane)
    {
        var war = new WarSim();
        int real = WinsUntilUnlock(war, lane, 8 * Minute, 1 * Minute, out int wins);
        Assert.True(real > 0, "a lane was besieged first");
        Assert.InRange(wins, 1, 2);
    }

    [Fact]
    public void AWholeRunTakes30To45Minutes()
    {
        var war = new WarSim();
        int real = WinsUntilUnlock(war, TfwLane.B, 8 * Minute, 1 * Minute, out _);
        real += 1 * Minute + Travel;                               // the hub before the final assault
        Steps(war, TfwRunPhase.Hub, TfwLane.B, 1 * Minute);
        double clock = war.Clock;
        Steps(war, TfwRunPhase.FinalAssault, TfwLane.B, 8 * Minute);
        real += 8 * Minute;                                        // one battle's length, an assumption until TF4 plays one
        Assert.Equal(clock, war.Clock);                            // all lanes wait for it
        Assert.InRange(real, 30 * Minute, 45 * Minute);
        Assert.Equal(TfwRunPhase.FinalAssault, war.Phase);
    }

    [Fact]
    public void TheBattlesOwnLaneIsFrozenAndTheOthersRun()
    {
        var war = new WarSim();
        TfwLaneState before = war.LaneState(TfwLane.B);
        Steps(war, TfwRunPhase.Battle, TfwLane.B, 120);
        Assert.Equal(before, war.LaneState(TfwLane.B));
        Assert.NotEqual(0f, war.LaneState(TfwLane.A).Progress);
        Assert.Equal(120.0, war.Clock);
    }

    [Theory]
    [InlineData(TfwRunPhase.Pickup)]
    [InlineData(TfwRunPhase.Travel)]
    public void TheLaneRunsInTravelAndPickup(TfwRunPhase phase)
    {
        var war = new WarSim();
        Steps(war, phase, TfwLane.B, 10);
        Assert.NotEqual(0f, war.LaneState(TfwLane.B).Progress);
    }

    [Theory]
    [InlineData(TfwRunPhase.FinalAssault)]
    [InlineData(TfwRunPhase.Won)]
    public void TheFinalAssaultAndTheEndStopTheWar(TfwRunPhase phase)
    {
        var war = new WarSim();
        war.Enter(phase);
        ulong before = war.StateHash();
        for (int t = 0; t < 100; t++) war.Step();
        Assert.Equal(before, war.StateHash());
        Assert.Equal(0.0, war.TimeScale);
    }

    [Theory]
    [InlineData(true, 1, 1.25)]
    [InlineData(false, 1, 1.0)]
    [InlineData(true, -1, 1.875)]
    [InlineData(false, -1, 1.5)]
    [InlineData(true, 0, 1.25)]
    public void TheMultiplierIsTheLordTimesTheHq(bool lord, sbyte front, double expected) =>
        Assert.Equal(expected, WarRules.Multiplier(lord, front), 9);

    [Fact]
    public void TheAdvantageFormula()
    {
        Assert.Equal((125.0 - 110.0) / (125.0 + 110.0), WarRules.Advantage(1.25, 100.0, 110.0), 12);
        Assert.Equal(0.0, WarRules.Advantage(1.25, 0.0, 0.0));
        Assert.Equal(-1.0, WarRules.Advantage(1.0, 0.0, 10.0));
    }

    [Fact]
    public void AWoundedLordSlowsTheLaneAndComesBackAfterFiveWarMinutes()
    {
        var withLord = new WarSim();
        var wounded = new WarSim();
        wounded.WoundLord(TfwLane.A);
        Assert.Equal(WarRules.LordWounded, wounded.LaneState(TfwLane.A).LordState);
        Steps(withLord, TfwRunPhase.Travel, TfwLane.A, 1);
        Steps(wounded, TfwRunPhase.Travel, TfwLane.A, 1);

        // First step, from the start values: with the lord +(125-110)/235/30, without him (100-110)/210/30.
        Assert.Equal(15.0 / 235.0 / 30.0, withLord.LaneState(TfwLane.A).Progress, 5);
        Assert.Equal(-10.0 / 210.0 / 30.0, wounded.LaneState(TfwLane.A).Progress, 5);

        Steps(wounded, TfwRunPhase.Travel, TfwLane.A, 298);
        Assert.Equal(WarRules.LordWounded, wounded.LaneState(TfwLane.A).LordState);
        Steps(wounded, TfwRunPhase.Travel, TfwLane.A, 1);
        Assert.Equal(WarRules.LordPresent, wounded.LaneState(TfwLane.A).LordState);
    }

    [Fact]
    public void ALordsWoundCountsWarMinutesEvenInItsOwnBattleAndFourTimesSlowerInTheHub()
    {
        var battle = new WarSim();
        battle.WoundLord(TfwLane.A);
        Steps(battle, TfwRunPhase.Battle, TfwLane.A, 60);
        Assert.Equal(240.0, battle.LordWoundLeft(TfwLane.A), 9);

        var hub = new WarSim();
        hub.WoundLord(TfwLane.A);
        Steps(hub, TfwRunPhase.Hub, TfwLane.A, 60);
        Assert.Equal(285.0, hub.LordWoundLeft(TfwLane.A), 9);
    }

    [Fact]
    public void TheHqLastStandHoldsLongerThanWithoutIt()
    {
        // Besieged with the lord: m 1.875. 100 Imperial against 150 Orks is an Imperial advantage there, an Ork one at any other front.
        Assert.True(WarRules.Advantage(WarRules.Multiplier(true, -1), 100.0, 150.0) > 0.0);
        Assert.True(WarRules.Advantage(WarRules.Multiplier(true, 0), 100.0, 150.0) < 0.0);
    }

    [Fact]
    public void StrengthsFollowAttritionAndReinforcement()
    {
        var war = new WarSim();
        Steps(war, TfwRunPhase.Travel, TfwLane.A, 1);
        TfwLaneState lane = war.LaneState(TfwLane.A);
        Assert.Equal(100.0 + 0.03 - 0.0002 * 110.0, lane.Imperial, 4);
        Assert.Equal(110.0 + 0.035 - 0.0002 * 100.0, lane.Ork, 4);
    }

    [Fact]
    public void TheOrksReinforcementDoublesAt30WarMinutes()
    {
        Assert.Equal(0.035, WarRules.OrkReinforcementAt(0.0), 12);
        Assert.Equal(0.07, WarRules.OrkReinforcementAt(1800.0), 12);
    }

    [Fact]
    public void StrengthsStayUnderTheirCaps()
    {
        var war = new WarSim();
        for (int i = 0; i < 10; i++) war.PlayerWon(TfwLane.A);
        Assert.Equal(200f, war.LaneState(TfwLane.A).Imperial, 3);

        var late = new WarSim();
        Steps(late, TfwRunPhase.Travel, TfwLane.A, 30 * Minute);
        for (int t = 0; t < 60 * Minute && late.Phase != TfwRunPhase.Lost; t++) late.Step();
        foreach (TfwLane lane in Enum.GetValues<TfwLane>()) Assert.True(late.LaneState(lane).Ork <= 250f);
    }

    [Fact]
    public void APlayerWinCapturesTheNode()
    {
        var war = new WarSim();
        war.Enter(TfwRunPhase.Battle, TfwLane.B);
        war.PlayerWon(TfwLane.B);
        Assert.Equal(new TfwLaneState(1, 0f, 120f, 66f, 0b001, 30f, WarRules.LordPresent), war.LaneState(TfwLane.B));
    }

    [Fact]
    public void AWinFromASiegeOnlyLiftsIt()
    {
        var war = new WarSim();
        war.Enter(TfwRunPhase.Battle, TfwLane.A);
        while (war.LaneState(TfwLane.B).Front != WarRules.FrontBesieged) war.Step();
        war.PlayerWon(TfwLane.B);
        TfwLaneState lane = war.LaneState(TfwLane.B);
        Assert.Equal(0, lane.Front);
        Assert.Equal(0, lane.OwnedMask);
    }

    [Theory]
    [InlineData(0, -0.2f)]
    [InlineData(1, 0.05f)]
    [InlineData(2, 0.3f)]
    [InlineData(3, 0.55f)]
    [InlineData(9, 0.55f)]    // no more than the three positions
    [InlineData(-4, -0.2f)]
    public void LeavingABattleSetsTheProgressFromTheSecuredPositions(int secured, float progress)
    {
        var war = new WarSim();
        war.PlayerLeft(TfwLane.C, secured, 0, 0);
        Assert.Equal(progress, war.LaneState(TfwLane.C).Progress, 5);
    }

    [Fact]
    public void LeavingABattleCountsKillsAndGuardLosses()
    {
        var war = new WarSim();
        war.PlayerLeft(TfwLane.C, 1, 200, 30);
        TfwLaneState lane = war.LaneState(TfwLane.C);
        Assert.Equal(100f, lane.Ork, 5);
        Assert.Equal(97f, lane.Imperial, 5);
        Assert.Equal(1, lane.Front);
        Assert.Equal(0f, lane.GatherLeft);
    }

    [Fact]
    public void StrengthsNeverGoNegativeAndASpentLaneStaysFinite()
    {
        var war = new WarSim();
        war.PlayerLeft(TfwLane.A, 0, 1_000_000, 1_000_000);
        war.PlayerLeft(TfwLane.A, 0, -5, -5);
        TfwLaneState lane = war.LaneState(TfwLane.A);
        Assert.Equal(0f, lane.Ork);
        Assert.Equal(0f, lane.Imperial);
        Steps(war, TfwRunPhase.Travel, TfwLane.A, 5);
        Assert.True(float.IsFinite(war.LaneState(TfwLane.A).Progress));
    }

    [Fact]
    public void ANodeLostGathersSixtyWarSecondsAndHoldsTheProgress()
    {
        var war = new WarSim();
        while (war.LaneState(TfwLane.B).Front == 0 || war.LaneState(TfwLane.B).GatherLeft == 0f) { war.Enter(TfwRunPhase.Travel, TfwLane.A); war.Step(); }
        TfwLaneState taken = war.LaneState(TfwLane.B);
        Assert.Equal(1, taken.Front);
        Assert.Equal(0b001, taken.OwnedMask);
        Assert.Equal(60f, taken.GatherLeft);
        Assert.Equal(0f, taken.Progress);

        Steps(war, TfwRunPhase.Travel, TfwLane.A, 60);
        Assert.Equal(0f, war.LaneState(TfwLane.B).GatherLeft);
        Assert.Equal(0f, war.LaneState(TfwLane.B).Progress);   // the 60th second ends the gather
        Assert.NotEqual(taken.Ork, war.LaneState(TfwLane.B).Ork);   // the strengths ran on
        war.Step();
        Assert.NotEqual(0f, war.LaneState(TfwLane.B).Progress);
    }

    [Fact]
    public void GatherTimersCountWarSeconds()
    {
        var hub = new WarSim();
        hub.PlayerWon(TfwLane.A);
        Steps(hub, TfwRunPhase.Hub, TfwLane.A, 4);
        Assert.Equal(29f, hub.LaneState(TfwLane.A).GatherLeft);
        Steps(hub, TfwRunPhase.Hub, TfwLane.A, 116);
        Assert.Equal(0f, hub.LaneState(TfwLane.A).GatherLeft);

        var frozen = new WarSim();
        frozen.PlayerWon(TfwLane.A);
        Steps(frozen, TfwRunPhase.Battle, TfwLane.A, 50);
        Assert.Equal(30f, frozen.LaneState(TfwLane.A).GatherLeft);
    }

    [Fact]
    public void AStrongholdHoldsItsFrontAtFullProgress()
    {
        var war = new WarSim();
        war.PlayerWon(TfwLane.A);
        war.PlayerWon(TfwLane.A);
        Assert.Equal(TfwLane.A, war.FinalLane);
        Assert.Equal(0b111, war.LaneState(TfwLane.A).OwnedMask);
        Steps(war, TfwRunPhase.Travel, TfwLane.B, 10 * Minute);
        Assert.Equal(WarRules.FrontStronghold, war.LaneState(TfwLane.A).Front);
        Assert.Equal(1f, war.LaneState(TfwLane.A).Progress);
    }

    [Fact]
    public void AnHqFallingLosesTheRunAndStopsIt()
    {
        var war = new WarSim();
        war.Enter(TfwRunPhase.Travel, TfwLane.B);
        while (war.Phase != TfwRunPhase.Lost) war.Step();
        Assert.Equal(WarRules.FrontBesieged, war.LaneState(war.FallenLane!.Value).Front);
        ulong hash = war.StateHash();
        war.Enter(TfwRunPhase.Hub);
        war.PlayerWon(TfwLane.A);
        war.PlayerLeft(TfwLane.B, 1, 1, 1);
        war.Step();
        Assert.Equal(TfwRunPhase.Lost, war.Phase);
        Assert.Equal(hash, war.StateHash());
    }

    [Fact]
    public void TheFinalBonusCountsTheOtherLanesAtNodeTwoOrBetter()
    {
        var war = new WarSim();
        war.PlayerWon(TfwLane.B);
        war.PlayerWon(TfwLane.B);
        war.PlayerWon(TfwLane.B);
        Assert.Equal(new WarFinalBonus(1f, 0), war.FinalBonus(TfwLane.B));

        war.PlayerWon(TfwLane.A);
        Assert.Equal(new WarFinalBonus(0.85f, 2), war.FinalBonus(TfwLane.B));

        war.PlayerWon(TfwLane.C);
        Assert.Equal(new WarFinalBonus(0.85f * 0.85f, 4), war.FinalBonus(TfwLane.B));
    }

    [Fact]
    public void TheViewCarriesTheWarAndTheRunsReserves()
    {
        var war = new WarSim();
        Steps(war, TfwRunPhase.Battle, TfwLane.C, 10);
        TfwWarView view = war.View(5, 4, 300);
        Assert.Equal(TfwRunPhase.Battle, view.Phase);
        Assert.Equal(TfwLane.C, view.BattleLane);
        Assert.Equal(10f, view.Clock);
        Assert.Equal(1f, view.TimeScale);
        Assert.Equal((byte)5, view.Transport);
        Assert.Equal((byte)4, view.Supplies);
        Assert.Equal((ushort)300, view.CommandEcho);
        Assert.Equal(war.LaneState(TfwLane.A), view.A);
        Assert.Equal(war.LaneState(TfwLane.B), view.B);
        Assert.Equal(war.LaneState(TfwLane.C), view.C);
    }

    /// <summary>The calibration's numbers for the record (visible with <c>--logger "console;verbosity=detailed"</c>).</summary>
    [Fact]
    public void CalibrationReport()
    {
        Times war = Unattended(TfwRunPhase.Travel), hub = Unattended(TfwRunPhase.Hub);
        var scripted = new WarSim();
        int unlock = WinsUntilUnlock(scripted, TfwLane.B, 8 * Minute, 1 * Minute, out int wins);
        string fronts = string.Join(",", Enum.GetValues<TfwLane>().Select(l => scripted.LaneState(l).Front));
        Console.WriteLine($"[calibration] unattended: siege {war.Siege / 60.0:F1} min, fall {war.Fall / 60.0:F1} min, highest front {war.HighestFront}; hub fall {hub.Fall / 60.0:F1} min (x{hub.Fall / (double)war.Fall:F2}); B: {wins} wins unlock at {unlock / 60.0:F1} min, fronts {fronts}");
        Assert.True(unlock > 0);
    }
}
