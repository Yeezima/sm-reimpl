using SmClient.ImpactRules;

namespace SmNet.Tests;

/// <summary>sound/original-hits: which server hit reports of the own player's melee swings are already sounded at the strike (<see cref="OwnSwingLedger"/>).</summary>
public class OwnSwingLedgerTests
{
    [Fact]
    public void A_report_of_a_swing_sounded_at_the_strike_is_silent_at_a_normal_round_trip()
    {
        var l = new OwnSwingLedger();
        l.Strike(10.0, heavy: false, step: 1, sounded: true, victim: 7);
        var v = l.Report(10.15, 7, false, 0);
        Assert.False(v.Sound);
        Assert.Equal((false, 1), (v.Heavy, v.Step));
    }

    [Fact]
    public void A_slow_round_trip_still_matches_its_swing()
    {
        var l = new OwnSwingLedger();
        l.Strike(10.0, false, 1, true, 7);
        Assert.False(l.Report(10.9, 7, false, 0).Sound);   // far past the old 0.35 s and 0.6 s windows
        Assert.False(l.Report(15.0, 7, false, 0).Sound);   // a repeat for a victim the swing has sounded is silent whenever it comes
    }

    [Fact]
    public void With_fast_combos_each_report_confirms_its_own_swing_in_order()
    {
        var l = new OwnSwingLedger();
        l.Strike(10.0, false, 1, true, 7);
        l.Strike(10.4, false, 2, true, 7);
        l.Strike(10.8, true, 3, true, 7);
        // The reports arrive 0.6 s after each blow, the first after the second swing's strike: all three silent, none sounds again as a later swing's primary.
        Assert.False(l.Report(10.6, 7, false, 1).Sound);
        Assert.False(l.Report(11.0, 7, false, 1).Sound);
        Assert.False(l.Report(11.4, 7, false, 1).Sound);
    }

    [Fact]
    public void A_further_victim_of_a_swing_sounds_once_as_a_secondary_target_with_the_swings_strike()
    {
        var l = new OwnSwingLedger();
        l.Strike(10.0, true, 2, true, 7);
        Assert.False(l.Report(10.2, 7, false, 1).Sound);
        var b = l.Report(10.21, 8, false, 1);
        Assert.True(b.Sound);
        Assert.True(b.Secondary);
        Assert.Equal((true, 2), (b.Heavy, b.Step));
        Assert.False(l.Report(10.3, 8, false, 1).Sound);   // never twice
        var c = l.Report(10.31, 9, false, 1);
        Assert.True(c.Sound);
        Assert.True(c.Secondary);
    }

    [Fact]
    public void A_victim_the_strike_did_not_pick_sounds_from_the_report_as_a_secondary_target_of_that_swing()
    {
        var l = new OwnSwingLedger();
        l.Strike(10.0, false, 3, true, 7);   // the strike picked 7; the server hit 9
        var v = l.Report(10.2, 9, false, 0);
        Assert.True(v.Sound);
        Assert.Equal((false, 3), (v.Heavy, v.Step));
        Assert.True(v.Secondary);   // 7 was sounded at the strike (ours: it may have dodged; the price of no delay)
    }

    [Fact]
    public void A_swing_that_found_nobody_gives_its_report_the_primary_sound_with_its_own_strike()
    {
        var l = new OwnSwingLedger();
        l.Strike(10.0, true, 2, sounded: false, victim: 0);
        var v = l.Report(10.3, 7, false, 1);
        Assert.True(v.Sound);
        Assert.False(v.Secondary);
        Assert.Equal((true, 2), (v.Heavy, v.Step));
        Assert.True(l.Report(10.31, 8, false, 1).Secondary);   // a second victim of the same swing
    }

    [Fact]
    public void A_report_with_no_swing_in_memory_starts_one_from_the_given_strike_and_old_swings_age_out()
    {
        var l = new OwnSwingLedger();
        var v = l.Report(5.0, 7, true, 4);
        Assert.True(v.Sound);
        Assert.Equal((true, 4), (v.Heavy, v.Step));
        Assert.False(l.Report(5.1, 7, true, 4).Sound);   // the same victim, same swing
        l.Strike(20.0, false, 1, true, 7);                 // the first has aged out (Memory)
        Assert.Equal(1, l.Count);
        Assert.False(l.Report(20.2, 7, false, 1).Sound);
    }

    [Fact]
    public void The_ledger_keeps_at_most_its_capacity()
    {
        var l = new OwnSwingLedger();
        for (int i = 0; i < OwnSwingLedger.Capacity + 5; i++) l.Strike(10.0 + i * 0.01, false, 1, true, 7);
        Assert.Equal(OwnSwingLedger.Capacity, l.Count);
    }

    [Fact]
    public void A_swing_after_a_confirmed_one_takes_its_own_secondary_report()
    {
        var l = new OwnSwingLedger();
        l.Strike(10.0, false, 1, true, 7);
        l.Strike(10.5, false, 2, true, 7);
        Assert.False(l.Report(10.2, 7, false, 1).Sound);   // swing 1 confirmed
        var other = l.Report(10.7, 8, false, 1);            // a second victim, while swing 2 still awaits its confirmation
        Assert.True(other.Sound);
        Assert.Equal(2, other.Step);
    }
}
