using SmContent;

namespace SmFormats.Tests;

/// <summary>
/// ui/hud-2: the reward lines of the own kills (<see cref="RewardTracker"/>, the data's rules of <c>xp_settings_pvp/rewards</c>), the bar clips' frame rules (<see cref="VitalsRules"/>) and the grenade
/// launcher's round display (<see cref="WeaponBoxRules"/>). Pure rules: no install and no original bytes.
/// </summary>
public class HudRewardTests
{
    static string[] Names(IReadOnlyList<RewardEarned> l) => [.. l.Select(e => e.Spec.Key)];

    [Fact]
    public void AKillIsAKillLineAlone()
    {
        var t = new RewardTracker();
        var r = t.OwnKill("P2", 10.0, headshot: false, ownAlive: true);
        Assert.Equal(["kill"], Names(r));
        Assert.Equal(50, r[0].Spec.Experience);
    }

    [Fact]
    public void AHeadshotReplacesTheKillLine()
    {
        var r = new RewardTracker().OwnKill("P2", 1.0, headshot: true, ownAlive: true);
        Assert.Equal(["headshot kill"], Names(r));   // ui/hud-camera-2: one line, not the kill line as well
        Assert.Equal(85, r[0].Spec.Experience);
    }

    [Fact]
    public void KillsWithinTwoSecondsMakeDoubleTripleQuadAndMegaKills()
    {
        var t = new RewardTracker();
        Assert.Equal(["kill"], Names(t.OwnKill("A", 0.0, false, true)));
        Assert.Equal(["kill", "double kill"], Names(t.OwnKill("B", 1.9, false, true)));
        Assert.Equal(["kill", "triple kill", "killstreak x3"], Names(t.OwnKill("C", 3.8, false, true)));
        Assert.Equal(["kill", "quad kill", "killstreak x4"], Names(t.OwnKill("D", 5.7, false, true)));
        Assert.Equal(["kill", "mega kill", "killstreak x5"], Names(t.OwnKill("E", 7.6, false, true)));
        // A gap of more than two seconds starts the count again (the streak goes on: no death).
        Assert.Equal(["kill", "killstreak x6"], Names(t.OwnKill("F", 12.0, false, true)));
        Assert.Equal(100, RewardTable.DoubleKill.Experience); Assert.Equal(300, RewardTable.TripleKill.Experience);
        Assert.Equal(400, RewardTable.QuadKill.Experience); Assert.Equal(500, RewardTable.MegaKill.Experience);
    }

    [Fact]
    public void KillstreakStepsPayTheDatasExperienceAndADeathEndsTheStreak()
    {
        var t = new RewardTracker();
        for (int i = 1; i <= 2; i++) t.OwnKill("V" + i, i * 10.0, false, true);
        var third = t.OwnKill("V3", 30.0, false, true);
        Assert.Equal(["kill", "killstreak x3"], Names(third));
        Assert.Equal(50, third[1].Spec.Experience);
        Assert.Equal(3, third[1].Count);
        Assert.Equal("Killstreak", third[1].Spec.Name);          // the displayed entry's name
        Assert.Equal(75, t.OwnKill("V4", 40.0, false, true)[1].Spec.Experience);
        for (int i = 5; i <= 9; i++) t.OwnKill("V" + i, i * 10.0, false, true);
        Assert.Equal(["kill", "killmax"], Names(t.OwnKill("V10", 100.0, false, true)));
        Assert.Equal(["kill", "killstreak cap"], Names(t.OwnKill("V11", 110.0, false, true)));
        t.OwnDied("X");
        Assert.Equal(0, t.Streak);
        Assert.Equal(["kill"], Names(t.OwnKill("V12", 200.0, false, true)));
    }

    [Fact]
    public void RevengeIsKillingWhoeverKilledYouLastUnlessTheyDiedInTheInterim()
    {
        var t = new RewardTracker();
        t.OwnDied("Killer");
        Assert.Equal(["kill", "revenge"], Names(t.OwnKill("Killer", 5.0, false, true)));
        Assert.Equal(["kill"], Names(t.OwnKill("Killer", 20.0, false, true)));   // paid once
        var u = new RewardTracker();
        u.OwnDied("Killer");
        u.Died("Killer");                                                          // a teammate got them first
        Assert.Equal(["kill"], Names(u.OwnKill("Killer", 5.0, false, true)));
        Assert.Equal(50, RewardTable.Revenge.Experience);
    }

    [Fact]
    public void AvengerIsKillingAnOpponentWhoKilledATeammateWithinFiveSeconds()
    {
        var t = new RewardTracker();
        t.TeammateKilledBy("Foe", 10.0);
        Assert.Equal(["kill", "avenger"], Names(t.OwnKill("Foe", 14.9, false, true)));
        var u = new RewardTracker();
        u.TeammateKilledBy("Foe", 10.0);
        Assert.Equal(["kill"], Names(u.OwnKill("Foe", 15.5, false, true)));       // too late
        Assert.Equal(["kill"], Names(u.OwnKill("Other", 30.0, false, true)));     // not them
        Assert.Equal(25, RewardTable.Avenger.Experience);
    }

    [Fact]
    public void GrudgeIsTheFifthKillOfTheSameOpponentInARoundAndAResetStartsItAgain()
    {
        var t = new RewardTracker();
        for (int i = 1; i <= 4; i++) Assert.DoesNotContain("grudge", Names(t.OwnKill("Rival", i * 30.0, false, true)));
        Assert.Contains("grudge", Names(t.OwnKill("Rival", 150.0, false, true)));
        Assert.DoesNotContain("grudge", Names(t.OwnKill("Rival", 180.0, false, true)));   // equal to 5: once
        t.Reset();
        for (int i = 1; i <= 4; i++) t.OwnKill("Rival", i * 30.0, false, true);
        Assert.Contains("grudge", Names(t.OwnKill("Rival", 150.0, false, true)));         // a new round counts from the first kill
    }

    [Fact]
    public void AKillAfterTheOwnDeathIsVictoryInDeathAndDoesNotCountForTheStreak()
    {
        var t = new RewardTracker();
        var r = t.OwnKill("P2", 10.0, false, ownAlive: false);
        Assert.Equal(["kill", "victory in death"], Names(r));
        Assert.Equal(0, t.Streak);
        Assert.Equal(50, RewardTable.VictoryInDeath.Experience);
    }

    [Fact]
    public void EverySpecHasAFrameANameIdAndExperienceFromTheData()
    {
        Assert.All(RewardTable.All, s =>
        {
            Assert.False(string.IsNullOrEmpty(s.Frame)); Assert.False(string.IsNullOrEmpty(s.Name)); Assert.True(s.NameId > 100000); Assert.True(s.Experience > 0);
        });
        Assert.Equal(2.0, RewardTable.MultiKillInterval);
        Assert.Equal([50, 75, 100, 125, 150, 175, 200], Enumerable.Range(3, 7).Select(n => RewardTable.KillstreakStep(n).Experience));
        Assert.Equal("mp_bonus_killstreak_5", RewardTable.KillstreakStep(5).Frame);
    }

    [Fact]
    public void TheBarClipsFramesFollowTheFractions()
    {
        Assert.Equal(0, VitalsRules.HealthFrame(1f, true));
        Assert.Equal(99, VitalsRules.HealthFrame(0f, true));
        Assert.Equal(99, VitalsRules.HealthFrame(1f, false));            // dead: the empty bar
        Assert.InRange(VitalsRules.HealthFrame(0.5f, true), 49, 50);
        Assert.Equal(99, VitalsRules.HealthFrame(float.NaN, true));      // an unknown value is read as empty, never an out-of-range frame
        Assert.Equal(VitalsRules.ArmourFull, VitalsRules.ArmourFrame(1f, true));
        Assert.Equal(VitalsRules.ArmourEmpty, VitalsRules.ArmourFrame(0f, true));
        Assert.Equal(VitalsRules.ArmourEmpty, VitalsRules.ArmourFrame(1f, false));
        // Below full the cut bar starts after the whole-bar frame and only gets shorter.
        int last = VitalsRules.ArmourFull;
        for (float f = 0.99f; f > 0.01f; f -= 0.01f)
        {
            int frame = VitalsRules.ArmourFrame(f, true);
            Assert.InRange(frame, VitalsRules.ArmourFull + 1, VitalsRules.ArmourEmpty);
            Assert.True(frame >= last);
            last = frame;
        }
    }

    [Fact]
    public void TheLauncherDisplaysRoundsFollowTheRoundsLeft()
    {
        Assert.Equal(18, WeaponBoxRules.LauncherSlots);
        Assert.Equal(0, WeaponBoxRules.LauncherRoundIndex("loadout_grenaderounddisplay_mc/round00"));
        Assert.Equal(17, WeaponBoxRules.LauncherRoundIndex("loadout_grenaderounddisplay_mc/round17"));
        Assert.Null(WeaponBoxRules.LauncherRoundIndex("loadout_grenaderounddisplay_mc/vengeance_reload"));
        Assert.Null(WeaponBoxRules.LauncherRoundIndex("Ammo_Stalker_mc/round03"));
        Assert.Null(WeaponBoxRules.LauncherRoundIndex("loadout_grenaderounddisplay_mc/round"));
        Assert.All(Enumerable.Range(0, 18), i => Assert.True(WeaponBoxRules.LauncherRoundLoaded(i, 18)));
        Assert.Equal(7, Enumerable.Range(0, 18).Count(i => WeaponBoxRules.LauncherRoundLoaded(i, 7)));
        Assert.Equal(0, Enumerable.Range(0, 18).Count(i => WeaponBoxRules.LauncherRoundLoaded(i, 0)));
        Assert.Equal(18, Enumerable.Range(0, 18).Count(i => WeaponBoxRules.LauncherRoundLoaded(i, 40)));   // more rounds than slots: all shown
        Assert.False(WeaponBoxRules.LauncherRoundLoaded(-1, 5));
    }

    [Fact]
    public void TheScopeSpreadFrameUsesTheSameRuleAsTheHipReticles()
    {
        // The stalker scope's clip: 40 frames, min_aim 0, max_normal 20, max_aim 39 (aim mode: min_aim .. max_aim).
        var labels = new Dictionary<string, int> { ["min_aim"] = 0, ["max_normal"] = 20, ["max_aim"] = 39 };
        var range = ReticleRules.SpreadRange(labels, 40, aiming: true);
        Assert.Equal((0, 39), range);
        Assert.Equal(0, ReticleRules.SpreadFrame(range, 0f));
        Assert.Equal(39, ReticleRules.SpreadFrame(range, 1f));
        Assert.InRange(ReticleRules.SpreadFrame(range, 0.5f), 19, 20);
    }
}
