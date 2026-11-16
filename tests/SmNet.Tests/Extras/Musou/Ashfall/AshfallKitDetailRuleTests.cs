using SmSim.Extras.Musou.Ashfall;

namespace SmNet.Tests.Extras.Musou.Ashfall;

/// <summary>The kit's detail choice (kit-hi-res; owner 2026-11-10: "Use low detail ashfall textures for old gpu mode users"): arena models are high detail only in Forward+ without the test aid.</summary>
public class AshfallKitDetailRuleTests
{
    [Theory]
    [InlineData(false, false, true)]    // Forward+: high detail
    [InlineData(false, true, false)]    // old-GPU mode (Compatibility renderer / safe mode): low detail
    [InlineData(true, false, false)]    // --ashfall-kit-detail old
    [InlineData(true, true, false)]
    public void Arena_models_are_high_detail_only_outside_old_gpu_mode(bool oldFlag, bool oldGpu, bool high)
        => Assert.Equal(high, AshfallKitDetailRule.HighDetail(oldFlag, oldGpu));

    [Fact]
    public void Old_gpu_mode_keeps_the_low_detail_textures_and_outside_models_stay_low()
    {
        Assert.Equal(2048, AshfallKitDetailRule.TextureMax(insideArena: true, oldFlag: false, oldGpuMode: false));
        Assert.Equal(1024, AshfallKitDetailRule.TextureMax(insideArena: true, oldFlag: false, oldGpuMode: true));
        Assert.Equal(1024, AshfallKitDetailRule.TextureMax(insideArena: true, oldFlag: true, oldGpuMode: false));
        Assert.Equal(1024, AshfallKitDetailRule.TextureMax(insideArena: false, oldFlag: false, oldGpuMode: false));
    }
}
