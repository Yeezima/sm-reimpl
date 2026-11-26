using SmClient.Customizer.Model;
using Xunit;

namespace SmFormats.Tests;

/// <summary>The Armoury's faction / mode pills: equal shares while the labels fit them (Classic unchanged), shares by need only when an equal share would overflow.</summary>
public class PillSharesTests
{
    [Fact]
    public void Labels_that_fit_equal_shares_keep_the_equal_layout()
    {
        Assert.Null(PillShares.Shares([180f, 140f], 500f));    // two faction pills, each under 250
        Assert.Null(PillShares.Shares([250f, 250f], 500f));    // exactly an equal share still fits
        Assert.Null(PillShares.Shares([], 500f));
    }

    [Fact]
    public void One_label_over_its_equal_share_shares_the_row_by_need()
    {
        var shares = PillShares.Shares([200f, 100f, 120f, 130f], 500f);   // 200 > 125: the four Warfront pills
        Assert.NotNull(shares);
        Assert.Equal(500f, shares!.Sum(), 3);
        Assert.Equal(200f / 550f * 500f, shares[0], 3);
        Assert.True(shares[0] > shares[1]);
    }

    [Fact]
    public void Needs_of_nothing_keep_the_equal_layout() => Assert.Null(PillShares.Shares([0f, 0f], 10f));
}
