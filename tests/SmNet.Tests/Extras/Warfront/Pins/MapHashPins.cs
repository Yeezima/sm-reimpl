using SmContent.Extras.Musou;
using SmContent.Extras.Warfront;
using SmNet.Tests.Extras.Musou.Ashfall;
using SmSim.Extras.Musou.Ashfall;
using SmSim.Extras.Musou.Campaign;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Warfront.Pins;

/// <summary>
/// W0 pins (Warfront): the Ashfall maps' hashes as before Warfront (captured on the untouched baseline, commit 5428594e). The existing constants of the Ashfall tests are asserted again by building the
/// maps; the composed <see cref="TfwMapData.Hash"/> of tfw_war and tfw_war_front through <see cref="TfwWorld.Load"/> are new literals (no install needed for these names).
/// </summary>
public class MapHashPins(ITestOutputHelper output)
{
    const ulong WarMapHash = 0x5482BA77C0F41E16UL, FrontMapHash = 0x1F18C37E742EE31FUL;

    [Fact]
    public void Draft_13_the_default_war_map_builds_to_its_pin()
    {
        Assert.Equal(AshfallDraft13Tests.PinnedHeightsHash, AshfallMap.BuildDraft5(AshfallLayout.Heights).Level.Hash);
        var war = TfwWorld.Load("", out var problem, TfwMap.Name);
        Assert.Null(problem);
        output.WriteLine($"tfw_war composed 0x{war!.Hash:X16}UL");
        Assert.Equal(WarMapHash, war.Hash);
    }

    [Fact]
    [Trait("Speed", "Slow")]   // (a Trait only labels; the filter FullyQualifiedName~Warfront.Pins runs it too) the three front bands and the composed world (minutes on a cold build)
    public void The_front_bands_and_the_composed_front_build_to_their_pins()
    {
        for (int band = 0; band < AshfallFront.BandCount; band++)
            Assert.Equal(AshfallFrontTests.PinnedBandHashes[band], AshfallMap.BuildFrontBand(band).Level.Hash);
        Assert.Equal(AshfallFrontWorldTests.PinnedComposedHash, AshfallMap.BuildDraft5(AshfallLayout.Front).Level.Hash);
        var front = TfwWorld.Load("", out var problem, TfwMap.FrontName);
        Assert.Null(problem);
        output.WriteLine($"tfw_war_front composed 0x{front!.Hash:X16}UL");
        Assert.Equal(FrontMapHash, front.Hash);
    }

    /// <summary>W13b: the HQ defence map composed with the hub (the only pin this slice adds; no existing value moves).</summary>
    [Fact]
    public void The_hq_defence_map_composed_with_the_hub_builds_to_its_pin()
    {
        var hq = WarfrontMaps.Load(WarfrontMaps.HqName, out var problem);
        Assert.Null(problem);
        output.WriteLine($"wf_hq composed 0x{hq!.Hash:X16}UL");
        Assert.Equal(HqMapHash, hq.Hash);
    }

    const ulong HqMapHash = 0x0FC12BFD61DBE655UL;
}
