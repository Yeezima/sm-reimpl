using SmNet;
using SmSim;

namespace SmNet.Tests;

/// <summary>
/// The end-of-match screens' rules (fidelity gap 4; ours, spec/behavior.md "Match flow: the score count-up and the summary"): the statistics a client
/// builds from the kill feed, the battle record, the split of the 15 s restart and the count-up. Synthetic events only.
/// </summary>
public class MatchSummaryTests
{
    const int Me = 1, Ally = 2, FoeA = 5, FoeB = 6;

    [Fact]
    public void Kills_deaths_and_streaks_follow_the_feed()
    {
        var s = new MatchStats();
        s.RecordKill(Me, FoeA, true, "bolter", 0);
        s.RecordKill(Me, FoeB, true, "bolter", 0);
        s.RecordKill(Me, FoeA, true, "chainsword", 0);
        s.RecordKill(FoeB, Me, true, "melta", 3);      // I die: the streak (3) ends
        s.RecordKill(Me, FoeA, true, "bolter", 0);

        var me = s.Of(Me);
        Assert.Equal(4, me.Kills);
        Assert.Equal(1, me.Deaths);
        Assert.Equal(3, me.BestStreak);
        Assert.Equal(1, me.Streak);
        Assert.Equal(("bolter", 3), me.Weapons.Best);
        Assert.Equal(3, s.Of(FoeA).Deaths);
    }

    [Fact]
    public void A_kill_of_ones_own_side_is_a_death_but_not_a_kill_and_leaves_the_streak_alone()
    {
        var s = new MatchStats();
        s.RecordKill(Me, FoeA, true, "bolter", 0);
        s.RecordKill(Me, Ally, false, "bolter", 0);    // friendly fire
        Assert.Equal(1, s.Of(Me).Kills);
        Assert.Equal(1, s.Of(Me).Streak);
        Assert.Equal(1, s.Of(Me).TeamKills);
        Assert.Equal(1, s.Of(Ally).Deaths);
        Assert.Empty(s.Of(Ally).KilledBy.Items);   // not an opponent: never a nemesis
    }

    [Fact]
    public void Falls_and_suicides_are_deaths_that_end_the_streak_and_credit_nobody()
    {
        var s = new MatchStats();
        s.RecordKill(Me, FoeA, true, "bolter", 0);
        s.RecordKill(0, Me, true, "fall", -1);         // the level
        s.RecordKill(FoeA, FoeA, true, "grenade", 1);  // own grenade
        Assert.Equal(1, s.Of(Me).Deaths);
        Assert.Equal(0, s.Of(Me).Streak);
        Assert.Equal(1, s.Of(Me).BestStreak);
        Assert.Equal(1, s.Of(FoeA).Suicides);
        Assert.Equal(0, s.Of(FoeA).Kills);
        Assert.False(s.Has(0));
    }

    [Fact]
    public void The_best_of_a_tie_is_the_first_seen()
    {
        var t = new Tally<string>();
        Assert.Null(t.Best);
        t.Add("b"); t.Add("a"); t.Add("a"); t.Add("b");
        Assert.Equal(("b", 2), t.Best);   // 2 : 2, b was seen first
        t.Add("a");
        Assert.Equal(("a", 3), t.Best);
    }

    [Fact]
    public void The_battle_record_names_the_best_weapon_class_and_the_nemesis()
    {
        var s = new MatchStats();
        s.RecordKill(Me, FoeA, true, "bolter", 0);
        s.RecordKill(Me, FoeB, true, "melta", 1);
        s.RecordKill(Me, FoeB, true, "melta", 1);
        s.RecordKill(FoeA, Me, true, "plasma", 2);
        s.RecordKill(FoeA, Me, true, "plasma", 2);
        s.RecordKill(FoeB, Me, true, "plasma", 2);
        s.RecordCapture(Team.SpaceMarines); s.RecordCapture(Team.Chaos); s.RecordCapture(Team.SpaceMarines);

        var r = BattleRecord.For(s, Me, kills: null, deaths: null, Team.SpaceMarines);
        Assert.Equal(3, r.EnemyKills);
        Assert.Equal(3, r.Deaths);
        Assert.Equal(1.0, r.KillRatio, 6);
        Assert.Equal("melta", r.BestWeapon);
        Assert.Equal(2, r.BestWeaponKills);
        Assert.Equal(1, r.BestClass);
        Assert.Equal(FoeA, r.Nemesis);          // killed me twice, FoeB once
        Assert.Equal(2, r.NemesisKills);
        Assert.Equal(1, r.KillsOfNemesis);
        Assert.Equal(2, r.TeamCaptures);
        Assert.Equal(1, BattleRecord.For(s, Me, null, null, Team.Chaos).TeamCaptures);
    }

    [Fact]
    public void The_scoreboard_totals_win_over_the_feed_for_kills_and_deaths()
    {
        var s = new MatchStats();
        s.RecordKill(Me, FoeA, true, "bolter", 0);     // a late joiner saw one kill
        var r = BattleRecord.For(s, Me, kills: 7, deaths: 4, Team.SpaceMarines);
        Assert.Equal(7, r.EnemyKills);
        Assert.Equal(4, r.Deaths);
        Assert.Equal(1.75, r.KillRatio, 6);
    }

    [Fact]
    public void An_empty_record_has_no_best_anything_and_no_division_by_zero()
    {
        var r = BattleRecord.For(new MatchStats(), Me, null, null, Team.None);
        Assert.Null(r.BestWeapon);
        Assert.Null(r.BestClass);
        Assert.Null(r.Nemesis);
        Assert.Equal(0, r.EnemyKills);
        Assert.Equal(0.0, r.KillRatio);
        Assert.Equal(5.0, BattleRecord.Ratio(5, 0));   // no deaths: the kills themselves
    }

    [Fact]
    public void Reset_forgets_everything()
    {
        var s = new MatchStats();
        s.RecordKill(Me, FoeA, true, "bolter", 0);
        s.RecordCapture(Team.Chaos);
        s.Reset();
        Assert.False(s.Has(Me));
        Assert.Equal(0, s.Captures(Team.Chaos));
    }

    [Fact]
    public void The_screens_fit_the_restart_with_time_left_for_the_scoreboard()
    {
        Assert.Equal(6.5, MatchEndTimeline.SplashEnd);
        Assert.Equal(MatchEndPhase.Splash, MatchEndTimeline.PhaseAt(0));
        Assert.Equal(MatchEndPhase.Splash, MatchEndTimeline.PhaseAt(6.49));
        Assert.Equal(MatchEndPhase.CountUp, MatchEndTimeline.PhaseAt(6.5));
        Assert.Equal(MatchEndPhase.Summary, MatchEndTimeline.PhaseAt(MatchEndTimeline.CountUpEnd + 0.01));
        Assert.Equal(MatchEndPhase.Scoreboard, MatchEndTimeline.PhaseAt(MatchEndTimeline.SummaryEnd));
        // The server's restart is 15 s; the scoreboard with the NEXT MAP timer keeps at least 2.5 s.
        Assert.Equal(15f, new MatchRules().RestartDelay);
        Assert.True(MatchEndTimeline.ScoreboardSeconds(new MatchRules().RestartDelay) >= 2.5);
        Assert.Equal(0.0, MatchEndTimeline.TimeInPhase(MatchEndTimeline.SplashEnd), 9);
        Assert.Equal(1.0, MatchEndTimeline.TimeInPhase(MatchEndTimeline.CountUpEnd + 1.0), 9);
    }

    [Fact]
    public void The_count_up_reaches_its_target_exactly_and_never_runs_backwards()
    {
        int last = -1;
        for (double t = 0; t <= CountUp.StageSeconds; t += 0.01)
        {
            int v = CountUp.Value(37, t);
            Assert.InRange(v, last, 37);
            last = v;
        }
        Assert.Equal(0, CountUp.Value(37, 0.0));
        Assert.Equal(37, CountUp.Value(37, CountUp.IntroSeconds + CountUp.CountSeconds));
        Assert.Equal(37, CountUp.Value(37, CountUp.StageSeconds));
        Assert.Equal(0, CountUp.Value(0, 1.0));
        Assert.Equal(0, CountUp.Value(-3, 1.0));
        Assert.Equal((0, 0.0), CountUp.StageAt(-1));
        Assert.Equal((1, 0.25), CountUp.StageAt(CountUp.StageSeconds + 0.25));
        Assert.Equal(CountUp.Stages - 1, CountUp.StageAt(100).Stage);
        Assert.Equal(1.0, CountUp.Intro(10.0));
    }

    [Fact]
    public void The_count_up_lists_the_best_four_of_the_viewers_side_and_the_viewer()
    {
        var all = new[]
        {
            new ScoreEntry(1, Team.SpaceMarines, 0, 1, 5), new ScoreEntry(2, Team.SpaceMarines, 0, 9, 2), new ScoreEntry(3, Team.SpaceMarines, 0, 7, 1),
            new ScoreEntry(4, Team.SpaceMarines, 0, 7, 3), new ScoreEntry(7, Team.SpaceMarines, 0, 6, 0), new ScoreEntry(9, Team.Chaos, 1, 20, 0),
        };
        var rows = CountUpRows.Pick(all, true, Team.SpaceMarines, me: 1);
        Assert.Equal([2, 3, 4, 1], rows.Select(r => r.PlayerId));     // the viewer (1 kill) replaces the fourth row (player 7)
        Assert.Equal([2, 3, 4, 7], CountUpRows.Pick(all, true, Team.SpaceMarines, me: 4).Select(r => r.PlayerId));
        Assert.Equal([9], CountUpRows.Pick(all, true, Team.Chaos, me: 9).Select(r => r.PlayerId));
        Assert.Equal(4, CountUpRows.Pick(all, false, Team.None, me: 9).Count);
        Assert.Empty(CountUpRows.Pick([], true, Team.Chaos, me: 1));
    }
}
