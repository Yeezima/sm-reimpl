using SmSim.Extras.Warfront;

namespace SmNet.Tests.Extras.Warfront.Guards;

/// <summary>Guardsmen G1e: the vox rate limiter (pure, in ticks; 30 ticks a second here).</summary>
public class WarfrontVoxRulesTests
{
    const int Rate = 30;
    static uint S(float seconds) => (uint)MathF.Round(seconds * Rate);

    [Fact]
    public void A_first_line_goes_at_once_and_the_next_waits_the_spacing()
    {
        var v = new WarfrontVox(Rate);
        v.Offer(WarfrontVoxKind.LineBreaking, 1, WarfrontVox.None, S(10));
        Assert.Equal(new WarfrontVoxLine(1, WarfrontVoxKind.LineBreaking, 1, WarfrontVox.None, S(10)), v.Newest);
        v.Offer(WarfrontVoxKind.Rallying, 0, WarfrontVox.None, S(12));
        Assert.Equal(1, v.Newest!.Value.Sequence);   // waits: 6 s spacing
        Assert.Null(v.Step(S(15)));
        var made = v.Step(S(16));
        Assert.Equal(WarfrontVoxKind.Rallying, made!.Value.Kind);
        Assert.Equal(2, made.Value.Sequence);
    }

    [Fact]
    public void Falling_back_replaces_a_waiting_line_breaking_about_the_same_point()
    {
        var v = new WarfrontVox(Rate);
        v.Offer(WarfrontVoxKind.Flank, 2, WarfrontVox.None, 0);   // takes the first slot
        v.Offer(WarfrontVoxKind.LineBreaking, 1, WarfrontVox.None, S(1));
        v.Offer(WarfrontVoxKind.FallingBack, 1, 0, S(3));
        var made = v.Step(S(6));
        Assert.Equal(WarfrontVoxKind.FallingBack, made!.Value.Kind);
        Assert.Equal(0, made.Value.To);
        Assert.Null(v.Step(S(13)));   // nothing else waits
        Assert.Equal(2, v.Made);
    }

    [Fact]
    public void A_high_priority_line_waits_at_most_four_seconds_and_a_low_one_never_waits()
    {
        var v = new WarfrontVox(Rate);
        v.Offer(WarfrontVoxKind.LineBreaking, 0, WarfrontVox.None, 0);
        v.Offer(WarfrontVoxKind.Reinforcements, 1, WarfrontVox.None, S(1));   // blocked: dropped
        v.Offer(WarfrontVoxKind.Rallying, 2, WarfrontVox.None, S(1));         // waits until 5 s, then dropped (spacing ends at 6 s)
        for (int t = (int)S(1); t <= S(7); t++) v.Step((uint)t);
        Assert.Equal(1, v.Made);
        Assert.Equal(2, v.Dropped);
    }

    [Fact]
    public void One_kind_waits_twelve_seconds_anywhere_and_thirty_at_one_point()
    {
        var v = new WarfrontVox(Rate);
        v.Offer(WarfrontVoxKind.Flank, 0, WarfrontVox.None, 0);
        v.Offer(WarfrontVoxKind.Flank, 1, WarfrontVox.None, S(8));   // same kind 8 s later: dropped (low priority)
        Assert.Equal(1, v.Made);
        v.Offer(WarfrontVoxKind.Flank, 1, WarfrontVox.None, S(13));
        Assert.Equal(2, v.Made);
        v.Offer(WarfrontVoxKind.Flank, 0, WarfrontVox.None, S(26));   // point 0 again 26 s after its last: dropped
        Assert.Equal(2, v.Made);
        v.Offer(WarfrontVoxKind.Flank, 0, WarfrontVox.None, S(31));
        Assert.Equal(3, v.Made);
    }

    [Fact]
    public void A_burst_gives_at_most_five_lines_a_minute_six_seconds_apart_but_breaking_and_falling_back_may_pass_the_cap()
    {
        var v = new WarfrontVox(Rate);
        var made = new List<WarfrontVoxLine>();
        var kinds = new[] { WarfrontVoxKind.Rallying, WarfrontVoxKind.Reinforcements, WarfrontVoxKind.Flank };
        for (int t = 0; t < S(60); t++)
        {
            if (t % S(1.5f) == 0) v.Offer(kinds[(t / (int)S(1.5f)) % 3], (t / 7) % 3, WarfrontVox.None, (uint)t);
            v.Step((uint)t);
            if (v.Newest is { } line && (made.Count == 0 || made[^1].Sequence != line.Sequence)) made.Add(line);   // a line may be made inside Offer
        }
        int lowAndRally = made.Count(l => l.Kind > WarfrontVoxKind.FallingBack);
        Assert.InRange(lowAndRally, 1, WarfrontVox.PerMinute);
        for (int i = 1; i < made.Count; i++) Assert.True(made[i].Tick - made[i - 1].Tick >= S(6), "spacing");
        Assert.Equal(made.Select(l => l.Sequence), Enumerable.Range(1, made.Count).Select(i => (ushort)i));

        var urgent = new WarfrontVox(Rate);
        int n = 0;
        for (int t = 0; t <= S(60); t++)
        {
            if (t % S(6) == 0) urgent.Offer(t % S(12) == 0 ? WarfrontVoxKind.LineBreaking : WarfrontVoxKind.FallingBack, (t / (int)S(6)) % 3, WarfrontVox.LandingZone, (uint)t);
            urgent.Step((uint)t);
            n = urgent.Made;
        }
        Assert.True(n > WarfrontVox.PerMinute, $"{n} urgent lines");
    }

    [Theory]
    [InlineData(WarfrontVoxKind.LineBreaking, 1, 255, "LINE BREAKING AT B")]
    [InlineData(WarfrontVoxKind.FallingBack, 1, 2, "FALLING BACK TO C")]
    [InlineData(WarfrontVoxKind.FallingBack, 0, 3, "FALLING BACK TO THE LANDING ZONE")]
    [InlineData(WarfrontVoxKind.Rallying, 1, 255, "RALLYING ON B")]
    [InlineData(WarfrontVoxKind.Reinforcements, 0, 255, "REINFORCEMENTS ARRIVING AT A")]
    [InlineData(WarfrontVoxKind.Flank, 1, 255, "FLANK AT B")]
    public void The_lines_read_as_the_brief_writes_them(WarfrontVoxKind kind, byte from, byte to, string text) => Assert.Equal(text, WarfrontVox.Text(kind, from, to));

    /// <summary>G5 F6: a waiting Falling back line about a point whose record ended (the origin fell) is never said late.</summary>
    [Fact]
    public void A_waiting_line_about_a_retired_point_is_dropped()
    {
        var v = new WarfrontVox(Rate);
        v.Offer(WarfrontVoxKind.Flank, 2, WarfrontVox.None, 0);
        v.Offer(WarfrontVoxKind.FallingBack, 0, 1, S(1));   // waits for the spacing
        v.DropAbout(0);
        for (int t = (int)S(1); t <= S(40); t++) v.Step((uint)t);
        Assert.Equal(1, v.Made);
    }

    [Fact]
    public void Unknown_kinds_are_ignored()
    {
        var v = new WarfrontVox(Rate);
        v.Offer((WarfrontVoxKind)9, 0, 0, 0);
        v.Offer((WarfrontVoxKind)0, 0, 0, 0);
        Assert.Null(v.Newest);
    }
}
