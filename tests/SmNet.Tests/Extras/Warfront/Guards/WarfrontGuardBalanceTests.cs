using SmNet.Tests.Extras.Musou.Campaign;
using SmSim;
using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Warfront.Guards;

/// <summary>
/// Warfront's Guardsmen against the Orks (owner 2026-11-20: "buff guardsmen-ork balance to be 30-60 ork favored"; answered: the Orks win about 2 of every 3 even fights, the Guardsmen at about half the Orks' strength).
/// The measure is <see cref="GuardHoldTrial"/> (the night batch's bounded, deterministic headless fight: 60 Guardsmen on a secured point, no player, 60 Orks of one crowd's mix coming from a seeded bearing, 300 simulated
/// seconds at most): <c>Held</c> is a Guardsman standing at the end. 30 seeds a setting; Musou's own Guardsmen are the "before" row.
/// </summary>
public class WarfrontGuardBalanceTests(ITestOutputHelper output)
{
    const int Seeds = 30;

    static int Held(GuardRules? rules, int crowd = GuardHoldTrial.Men) => GuardHoldTrial.RunSeeds(Seeds, rules, crowd).Count(o => o.Held);

    [Fact]
    [Trait("Speed", "Slow")]
    public void Even_fights_favour_the_orks_about_two_to_one()
    {
        int before = Held(null), after = Held(WarfrontGuardRules.Rules);
        output.WriteLine($"Musou's Guardsmen: held {before} of {Seeds}; Warfront's (x{WarfrontGuardRules.StrengthScale}): held {after} of {Seeds}");
        Assert.InRange(after, 7, 13);   // the Orks win 57 to 77 % (about 2 of 3: 20 of 30)
        Assert.True(after < before, $"Warfront's Guardsmen must be weaker than Musou's ({after} vs {before})");
    }

    [Fact]
    public void The_guardsmen_have_the_scaled_numbers_and_the_orks_are_untouched()
    {
        Assert.Equal(85f * GuardRules.Strength * WarfrontGuardRules.StrengthScale, WarfrontGuardRules.Rules.Health, 3);
        Assert.Equal(5f * GuardRules.Strength * WarfrontGuardRules.StrengthScale, WarfrontGuardRules.Rules.Damage, 3);
        Assert.Equal(GuardRules.Default.Range, WarfrontGuardRules.Rules.Range);
        Assert.Equal(GuardRules.Default.FirePeriod, WarfrontGuardRules.Rules.FirePeriod);
        Assert.Equal(2, WarfrontGuardRules.NumberScale);
    }

    /// <summary>The tuning aid: SM_WF_GUARD_SWEEP=1,0.9,0.8 prints the held count of 30 seeds at each scale (over the Warfront-free Strength); SM_WF_GUARD_CROWD sets the sides' size (60).</summary>
    [Fact]
    public void Sweep()
    {
        if (Environment.GetEnvironmentVariable("SM_WF_GUARD_SWEEP") is not { Length: > 0 } list) return;
        int crowd = int.TryParse(Environment.GetEnvironmentVariable("SM_WF_GUARD_CROWD"), out int c) && c > 0 ? c : GuardHoldTrial.Men;
        foreach (float f in list.Split(',').Select(x => float.Parse(x, System.Globalization.CultureInfo.InvariantCulture)))
        {
            var rules = new GuardRules { Health = 85f * GuardRules.Strength * f, Damage = 5f * GuardRules.Strength * f };
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var all = GuardHoldTrial.RunSeeds(Seeds, rules, crowd);
            output.WriteLine($"{crowd} Orks, scale x{f}: held {all.Count(o => o.Held)}/{Seeds}, mean {all.Average(o => o.Seconds):0} s, {watch.Elapsed.TotalSeconds:0.0} s wall");
        }
    }
}
