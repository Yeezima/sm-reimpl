using SmNet.Tests.Extras.Musou.Campaign;
using SmNet.Tests.Extras.Warfront.Battle;
using SmNet.Tests.Extras.Warfront.Chaos;
using SmSim;
using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront;

namespace SmNet.Tests.Extras.Warfront.Guards;

/// <summary>
/// Guardsmen G3 isolation pins (design N1 to N4, N9): Musou Mode, Classic, the HQ defence and the fortress take none of the Guardsmen's line; a Chaos lane stage does (owner 2026-11-25, "New in both");
/// and the restore switch gives back the battles as they were before the line (the digests pinned by the Chaos base's own pins, claude/chaos-base-1125 @ 6f1cfe8c).
/// </summary>
public class WarfrontLineIsolationTests
{
    // The pre-line digests (WarfrontChaosOrkPins): a 20 s Orks lane battle with Guardsmen, the HQ defence and the fortress.
    const ulong LanePin = 0x3920808E3E1C112DUL, HqPin = 0xA0BAD633A482EE15UL, FortressPin = 0xB14E5D60A2AF5BE5UL;

    [Fact]
    public void N1_musou_mode_runs_no_line_and_its_battle_does_not_change_when_the_line_is_switched()
    {
        ulong[] digest = new ulong[2];
        for (int i = 0; i < 2; i++)
        {
            var w = TfwRig.War();   // Musou Mode: no Warfront rules
            var b = TfwRig.Deploy(w);
            b.WarfrontLineOff = i == 1;
            Assert.Null(w.Tfw!.Warfront);
            Assert.False(b.WarfrontLineOn);
            TfwRig.Seconds(w, 40f);
            Assert.Equal(0, b.WarfrontWithdrawals + b.WarfrontLineRallies + b.WarfrontFlanksRaised);
            Assert.Null(b.WarfrontVoxNewest);
            Assert.Empty(b.WarfrontWithheld());
            digest[i] = WarfrontChaosOrkPins.Digest(w);
        }
        Assert.Equal(digest[0], digest[1]);
    }

    [Fact]
    public void N2_the_hq_defence_takes_none_of_the_line()
    {
        ulong[] digest = new ulong[2];
        for (int i = 0; i < 2; i++)
        {
            var (w, b) = WarfrontChaosOrkPins.Hq(WarfrontBattleTests.WarfrontConfig);
            b.WarfrontLineOff = i == 1;
            Assert.True(b.WarfrontHq);
            Assert.False(b.WarfrontLineOn);
            TfwRig.Seconds(w, 20f);
            Assert.Null(b.WarfrontVoxNewest);
            digest[i] = WarfrontChaosOrkPins.Digest(w);
        }
        Assert.Equal(digest[0], digest[1]);
        Assert.Equal(HqPin, digest[0]);
    }

    [Fact]
    public void N2_the_fortress_takes_none_of_the_line_and_keeps_its_rows()
    {
        ulong[] digest = new ulong[2];
        for (int i = 0; i < 2; i++)
        {
            var (w, b) = WarfrontChaosOrkPins.Fortress(WarfrontBattleTests.WarfrontConfig);
            b.WarfrontLineOff = i == 1;
            Assert.True(b.IsWarfrontFortress);
            Assert.False(b.WarfrontLineOn);
            TfwRig.Seconds(w, 20f);
            Assert.Equal(0, b.WarfrontWithdrawals);
            Assert.Null(b.WarfrontVoxNewest);
            digest[i] = WarfrontChaosOrkPins.Digest(w);
        }
        Assert.Equal(digest[0], digest[1]);
        Assert.Equal(FortressPin, digest[0]);
    }

    /// <summary>N3 (changed by the owner 2026-11-25): a Chaos lane stage takes the line too: the gate is on, and an elite push breaks the line and calls it.</summary>
    [Fact]
    public void N3_a_chaos_lane_stage_runs_the_line()
    {
        var (w, b) = GuardsmenRig.Lane(chaos: true);
        Assert.NotNull(w.Tfw!.Warfront!.ChaosRoster);
        Assert.True(b.WarfrontLineOn);
        var (r, log) = GuardsmenRig.Observe(w, b, "elite", 1, 0, 170, true);
        Assert.True(b.WarfrontWithdrawals >= 1 || log.Transitions.Any(t => t.To == WarfrontLineState.Breaking), "the Chaos line broke: " + r.Outcome);
        Assert.NotEmpty(log.Vox);
    }

    /// <summary>N4: Classic has no battle at all, so no line, no vox; the vox tag is Warfront's own.</summary>
    [Fact]
    public void N4_classic_has_no_battle_to_run_a_line_in()
    {
        var map = TfwTestArena.Build();
        var w = new World(TfwRig.Config with { Extras = ContentExtras.None }, map.Collision, TfwTestArena.Spawns);
        Assert.Null(w.Tfw);
        Assert.Null(w.TfwGuards);
        Assert.Equal(52, SmNet.Extras.Warfront.WarfrontWire.VoxTag);
    }

    /// <summary>N9: the restore switch is the battles as before the line: the pre-line digest, with the line off.</summary>
    [Fact]
    public void N9_with_the_line_off_the_lane_battle_is_the_pre_line_battle()
    {
        var (w, b) = WarfrontChaosOrkPins.Lane(WarfrontBattleTests.WarfrontConfig, guardsmen: true);
        b.WarfrontLineOff = true;
        TfwRig.Seconds(w, 20f);
        Assert.Equal(LanePin, WarfrontChaosOrkPins.Digest(w));
    }

    /// <summary>N9: with the line on, a quiet first 20 s (no push yet) is already the same: the line moves nothing until a push is on.</summary>
    [Fact]
    public void N9_with_the_line_on_a_quiet_first_twenty_seconds_changes_nothing()
    {
        var (w, _) = WarfrontChaosOrkPins.Lane(WarfrontBattleTests.WarfrontConfig, guardsmen: true);
        TfwRig.Seconds(w, 20f);
        Assert.Equal(LanePin, WarfrontChaosOrkPins.Digest(w));
    }

    /// <summary>N9 with the environment variable: QA runs this class with SM_WF_GUARD_AI_OLD=1 too; the default battle (no code switch) is then the pre-line one.</summary>
    [Fact]
    public void N9_the_environment_switch_gives_the_pre_line_battle_by_default()
    {
        var (w, b) = WarfrontChaosOrkPins.Lane(WarfrontBattleTests.WarfrontConfig, guardsmen: true);
        TfwRig.Seconds(w, 20f);
        Assert.Equal(LanePin, WarfrontChaosOrkPins.Digest(w));
        Assert.Equal(WarfrontLineRules.Old, b.WarfrontLineOff);
    }
}
