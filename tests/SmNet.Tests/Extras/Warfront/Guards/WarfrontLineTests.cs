using SmNet.Tests.Extras.Musou.Campaign;
using SmNet.Tests.Extras.Warfront.Battle;
using SmSim;
using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront;

namespace SmNet.Tests.Extras.Warfront.Guards;

/// <summary>Guardsmen G1a/G1b: where the line runs, and hold, fall back and rally on the flat test arena (the trial's scenarios, <see cref="WarfrontLineTrial"/>).</summary>
public class WarfrontLineTests
{
    static (World W, TfwBattle B) Ready(bool off = false, int settle = 20)
    {
        var w = TfwRig.War(config: WarfrontBattleTests.WarfrontConfig);
        var b = TfwRig.Deploy(w);
        b.WarfrontLineOff = off;
        WarfrontLineTrial.Prepare(w, settle);
        return (w, b);
    }

    [Fact]
    public void The_line_runs_in_a_warfront_lane_battle_and_nowhere_else()
    {
        var (_, b) = Ready(settle: 2);
        Assert.True(b.WarfrontLineOn);
        b.WarfrontLineOff = true;
        Assert.False(b.WarfrontLineOn);

        var musou = TfwRig.War();
        var m = TfwRig.Deploy(musou);
        Assert.False(m.WarfrontLineOn);
        WarfrontLineTrial.Prepare(musou, 2);
        WarfrontLineTrial.Run(musou, "elite", 1, 0, 60);
        Assert.Equal(0, m.WarfrontWithdrawals);
        Assert.Null(m.WarfrontVoxNewest);
        Assert.Empty(m.WarfrontWithheld());
    }

    [Fact]
    public void The_restore_switch_makes_no_line_no_vox_and_no_withheld_squad()
    {
        var (w, b) = Ready(off: true);
        WarfrontLineTrial.Run(w, "collapse", 1, 0, 90);
        Assert.Equal(0, b.WarfrontWithdrawals);
        Assert.Equal(0, b.WarfrontLineRallies);
        Assert.Null(b.WarfrontVoxNewest);
        Assert.Empty(b.WarfrontWithheld());
    }

    [Fact]
    public void The_same_seed_gives_the_same_run_with_the_line_on()
    {
        var (w1, _) = Ready();
        var (w2, _) = Ready();
        Assert.Equal(WarfrontLineTrial.Run(w1, "elite", 3, 0, 60).Hash, WarfrontLineTrial.Run(w2, "elite", 3, 0, 60).Hash);
    }

    static void KillNear(World w, System.Numerics.Vector3 point, float radius)
    {
        var all = new SmSim.Extras.Musou.MusouEnemy[SmSim.Extras.Musou.MusouSim.Capacity];
        int n = w.Musou!.CopyTo(all);
        for (int k = 0; k < n; k++)
            if (all[k].Alive && new System.Numerics.Vector2(all[k].Position.X - point.X, all[k].Position.Y - point.Y).Length() <= radius)
                w.Musou.Damage(all[k].Id, WarfrontLineTrial.Human, 1e6f, System.Numerics.Vector3.Zero, out _);
    }

    [Fact]
    public void An_overwhelmed_line_breaks_then_falls_back_together_to_a_friendly_point()
    {
        var (w, b) = Ready();
        var states = new List<WarfrontLineState>();
        var withheld = new HashSet<int>();
        int dest = -1;
        var kinds = new List<WarfrontVoxKind>();
        ushort seen = 0;
        WarfrontLineTrial.Run(w, "elite", 1, 0, 150, t =>
        {
            if (b.WarfrontVoxNewest is { } v && v.Sequence != seen) { seen = v.Sequence; if (v.From == 0) kinds.Add(v.Kind); }
            if (t % 30 != 0) return;
            var s = b.WarfrontLineOf(0);
            if (states.Count == 0 || states[^1] != s) states.Add(s);
            foreach (var (sq, origin, d) in b.WarfrontWithheld()) if (origin == 0) { withheld.Add(sq); dest = d; }
        });
        Assert.True(b.WarfrontWithdrawals >= 1, "the line fell back");
        int breaking = states.IndexOf(WarfrontLineState.Breaking);
        Assert.True(breaking >= 0 && states.IndexOf(WarfrontLineState.Withdrawn) > breaking, string.Join(",", states));
        Assert.NotEmpty(withheld);
        Assert.True(dest is 1 or 2 or 3, $"destination {dest}");
        Assert.Contains(WarfrontVoxKind.LineBreaking, kinds);
        Assert.Contains(WarfrontVoxKind.FallingBack, kinds);
        Assert.True(kinds.IndexOf(WarfrontVoxKind.FallingBack) > kinds.IndexOf(WarfrontVoxKind.LineBreaking));
    }

    [Fact]
    public void A_broken_push_rallies_the_same_squads_back_to_their_point_and_locks_it()
    {
        var (w, b) = Ready();
        var point = b.Layout.Positions[0];
        int[]? withdrawn = null;
        int brokeAt = -1, heldAgain = -1;
        bool rallied = false, withdrewInLockout = false;
        var rejoined = new HashSet<int>();
        WarfrontLineTrial.Run(w, "elite", 1, 0, 150, t =>
        {
            var state = b.WarfrontLineOf(0);
            if (withdrawn is null && state == WarfrontLineState.Withdrawn) { withdrawn = b.WarfrontWithheld().Where(x => x.Origin == 0).Select(x => x.Squad).ToArray(); brokeAt = t; }
            if (brokeAt >= 0 && heldAgain < 0 && t % 15 == 0)   // the Marines break the push: the human on the point, the Orks round it dead
            {
                w.TfwRespawnAt(WarfrontLineTrial.Human, point, 0f);
                KillNear(w, point, 60f);
            }
            if (state == WarfrontLineState.Rallying) rallied = true;
            if (heldAgain >= 0 && t - heldAgain < (SmSim.Extras.Warfront.WarfrontLineRules.RallyLockoutSeconds - 1) * 30 && state is WarfrontLineState.Breaking or WarfrontLineState.Withdrawn) withdrewInLockout = true;
            if (rallied && heldAgain < 0 && state == WarfrontLineState.Holding)
            {
                heldAgain = t;
                for (int s = 0; s < SmSim.Extras.Musou.Campaign.GuardSim.MaxSquads; s++) if (w.TfwGuards!.AliveIn(s) > 0 && b.SquadPost(s) == 0) rejoined.Add(s);
            }
        });
        Assert.NotNull(withdrawn);
        Assert.NotEmpty(withdrawn!);
        Assert.True(rallied, "the line rallied");
        Assert.True(b.WarfrontLineRallies >= 1);
        Assert.True(heldAgain > brokeAt, "it re-formed");
        Assert.False(withdrewInLockout, "no new withdrawal in the lockout");
        var alive = withdrawn!.Where(s => w.TfwGuards!.AliveIn(s) > 0).ToArray();
        Assert.All(alive, s => Assert.Contains(s, rejoined.Concat(b.ArmyList.SelectMany(a => a.Squads))));
    }

    /// <summary>G5 F1: a reinforcement column broken on the march goes to the base, as before, and is not rallied back while its quiet destination stands.</summary>
    [Fact]
    public void A_column_broken_on_the_march_goes_home_and_is_not_rallied()
    {
        var (w, b) = Ready(settle: 4);
        var column = b.ReinforceForTests(1, 60);
        Assert.NotNull(column);
        TfwRig.Seconds(w, 3f);
        TfwBattle.LoseForTests(column!, 40);
        b.RetreatForTests(column!);
        Assert.Equal(TfwArmyOrder.Retreat, column!.Order);
        Assert.Equal(TfwBattle.LandingGoal, column.Goal);
        int rallies = b.WarfrontLineRallies;
        for (int t = 0; t < 20 * 30 && b.ArmyList.Contains(column); t++) w.Step(_ => default);
        Assert.Equal(rallies, b.WarfrontLineRallies);
        Assert.True(!b.ArmyList.Contains(column) || column.Order == TfwArmyOrder.Retreat, $"the column was rallied: {column.Order} goal {column.Goal}");
    }

    /// <summary>G5 F1/F2: a garrison's retreat goes to a friendly point; when that point falls on the way the army is sent on to the next place (or the base).</summary>
    [Fact]
    public void A_retreating_garrison_is_sent_on_when_its_fall_back_point_falls()
    {
        var (w, b) = Ready(settle: 12);   // the armies' routes are in after 5 s
        var garrison = b.AddArmyForTests(TfwArmySide.Guard, TfwArmyOrder.Hold, 0, 40, b.Layout.Positions[0]);
        Assert.NotNull(garrison);
        b.RetreatForTests(garrison!);
        int dest = garrison!.Goal;
        Assert.True(dest is 1 or 2, $"a friendly point first: goal {dest}");
        b.LosePointForTests(dest);
        TfwRig.Seconds(w, 1.5f);
        Assert.True(!b.ArmyList.Contains(garrison) || garrison.Goal != dest, $"still bound for the fallen point {dest}");
        if (b.ArmyList.Contains(garrison)) Assert.True(garrison.Goal == TfwBattle.LandingGoal || b.Objectives[garrison.Goal].State == TfwPositionState.Secured);
    }
}
