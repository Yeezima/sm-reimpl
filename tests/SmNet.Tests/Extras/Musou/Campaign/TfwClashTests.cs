using SmSim.Extras.Musou.Campaign;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Musou.Campaign;

/// <summary>Expansion S6: the far layer's clash (Lanchester with cohesion), by the brief's Monte Carlo over 1,000 even seeds per size.</summary>
public class TfwClashTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(200)]
    [InlineData(500)]
    [InlineData(1000)]
    public void Even_clashes_are_even_the_winner_keeps_a_quarter_and_the_loser_routs(int size)
    {
        int aWins = 0, routed = 0;
        float leastKept = 1f, meanKept = 0f, meanSeconds = 0f;
        const int seeds = 1000;
        for (uint seed = 1; seed <= seeds; seed++)
        {
            var r = TfwClash.Resolve(size, size, seed * 2654435761u);
            if (r.AWon) aWins++;
            if (r.LoserRouted) routed++;
            float kept = r.AWon ? r.AKept : r.BKept;
            leastKept = MathF.Min(leastKept, kept);
            meanKept += kept / seeds;
            meanSeconds += r.Seconds / seeds;
        }
        output.WriteLine($"{size} v {size}: A won {aWins}/{seeds}, loser routed {routed}, winner kept least {leastKept:P1} mean {meanKept:P1}, mean {meanSeconds:F0} s");
        Assert.InRange(aWins, 450, 550);
        Assert.Equal(seeds, routed);
        Assert.True(leastKept >= 0.25f, $"the winner kept only {leastKept:P1}");
    }

    [Fact]
    public void A_numerical_edge_wins_and_breaks_the_smaller_side_sooner()
    {
        int wins = 0;
        for (uint seed = 1; seed <= 200; seed++) if (TfwClash.Resolve(300, 200, seed).AWon) wins++;
        Assert.True(wins >= 190, $"300 beat 200 only {wins} of 200 times");
        Assert.True(TfwClash.Resolve(400, 200, 7u).Seconds < TfwClash.Resolve(200, 200, 7u).Seconds);
    }

    [Fact]
    public void Steady_units_never_rout_and_routed_armies_neither_fire_nor_take_losses()
    {
        Span<TfwClashUnit> a = [new TfwClashUnit(100, 100, steady: true)];
        Span<TfwClashUnit> b = [new TfwClashUnit(100, 100) { Routed = true }, new TfwClashUnit(100, 100)];
        for (int k = 0; k < 400 && b[1].Count > 0f && !b[1].Routed; k++) TfwClash.Step(a, b, TfwClashRules.StepSeconds, 0f, 0f);
        Assert.False(a[0].Routed);
        Assert.Equal(100f, b[0].Count);
        Assert.True(b[1].Routed || b[1].Count <= 0f);
    }

    [Fact]
    public void Cohesion_recovers_out_of_a_fight_up_to_full()
    {
        Assert.Equal(1f, TfwClash.Recover(0.99f, 10f));
        Assert.True(TfwClash.Recover(0.5f, 1f) > 0.5f);
    }
}
