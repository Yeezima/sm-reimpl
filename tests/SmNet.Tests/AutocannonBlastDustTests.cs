using SmContent;
using SmSim;

namespace SmNet.Tests;

/// <summary>musou/autocannon-dust: which blasts lose their dust (the radius-to-share mapping the client's <c>BlastDustShareOf</c> applies) and that it is the autocannon's blast only.</summary>
public class AutocannonBlastDustTests
{
    static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "SmReimpl.slnx"))) d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("SmReimpl.slnx");
    }

    [Theory]
    [InlineData(3.5f, true)]    // the autocannon's blast (2.5 x 1.4, AoE pass 2026-11-14): drawn larger than the bolt blast, so it loses its dust
    [InlineData(2.5f, true)]    // the autocannon's blast before the AoE pass
    [InlineData(1.5625f, false)]  // the bolt blast itself (1.25 x 1.25)
    [InlineData(1.6f, false)]   // the wire rounds it to a decimetre
    [InlineData(1.25f, false)]  // the bolt blast before the AoE pass
    [InlineData(1.2f, false)]
    [InlineData(0f, false)]
    [InlineData(-3f, false)]
    [InlineData(float.NaN, false)]
    public void Only_a_blast_drawn_larger_than_the_bolt_blast_loses_its_dust(float radius, bool loses) => Assert.Equal(loses, AutocannonRules.LosesDust(radius));

    [Fact]
    public void The_client_mapping_gives_the_blast_no_dust_and_the_zero_share_keeps_nothing()
    {
        string text = File.ReadAllText(Path.Combine(Root(), "client", "Extras", "Musou", "Arsenal", "Autocannon", "NetworkView.AutocannonDust.cs"));
        Assert.Contains("!restored && SmSim.AutocannonRules.LosesDust(radius) ? BlastDust", text);
        Assert.Contains("internal const float BlastDust = 0f;", text);
        foreach (float roll in new[] { 0f, 0.3f, 0.999f }) Assert.False(FxAmount.Kept(0f, roll));
    }

    [Fact]
    public void A_bolt_blast_and_the_autocannon_blast_both_lose_their_dust_by_default()
    {
        // The pure mapping the client applies (client/Extras/Musou/Arsenal/Autocannon/NetworkView.AutocannonDust.cs BlastDustShareOf): the autocannon blast radius loses dust, the bolt blast keeps none either.
        Assert.True(AutocannonRules.LosesDust(3.5f));
        Assert.False(AutocannonRules.LosesDust(1.5625f));
        Assert.Equal(0f, SmContent.BoltSmoke.Share);
        Assert.Equal(0f, SmContent.BoltSmoke.ShareFor("sm_mp_ranged_bolter", old: false));
        Assert.Equal(1f, SmContent.BoltSmoke.ShareFor("sm_mp_ranged_bolter", old: true));
    }
}
