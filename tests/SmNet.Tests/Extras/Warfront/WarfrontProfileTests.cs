using SmSim;
using SmSim.Extras.Warfront;

namespace SmNet.Tests.Extras.Warfront;

/// <summary>Warfront W1: the <c>warfront</c> profile, its hash block and the versions that must not move.</summary>
public class WarfrontProfileTests
{
    static readonly SimConfig Config = new();

    // Captured on the tree before W1 (claude/warfront 5428594e, the W0 baseline): the switch must not move any of them.
    const uint MusouSimVersion = 63, ExtrasWireVersion = 2;   // main's numbers since 2026-11-19 (were 61 and 80 on the Warfront branch)
    const ushort ProtocolVersion = 88;

    [Fact]
    public void The_profile_is_musou_and_warfront_and_not_in_extended()
    {
        Assert.Equal(32u, (uint)ContentExtras.Warfront);
        Assert.Equal(ContentExtras.Musou | ContentExtras.Warfront, ContentProfiles.WarfrontProfile);
        Assert.False(ContentExtras.All.HasFlag(ContentExtras.Warfront));
        Assert.Equal("warfront", ContentProfiles.Name(ContentProfiles.WarfrontProfile));
        Assert.Contains("warfront", ContentProfiles.Expected);
    }

    [Theory]
    [InlineData("warfront")]
    [InlineData(" Warfront ")]
    public void The_profile_parses(string text)
    {
        Assert.True(ContentProfiles.TryParse(text, out var extras, out string error), error);
        Assert.Equal(ContentExtras.Musou | ContentExtras.Warfront, extras);
        Assert.Contains("warfront", ContentProfiles.Describe(extras));
    }

    [Theory]
    [InlineData("warfront,orks")]
    [InlineData("orks,warfront")]
    [InlineData("warfront,musou")]
    [InlineData("musou,warfront")]
    [InlineData("warfront,warfront")]
    public void The_profile_with_any_other_flag_is_refused(string text)
    {
        Assert.False(ContentProfiles.TryParse(text, out var extras, out string error));
        Assert.Equal(ContentExtras.None, extras);
        Assert.StartsWith("--content", error);
        Assert.Contains("warfront", error);
    }

    [Fact]
    public void Musou_alone_still_parses_as_musou()
    {
        Assert.True(ContentProfiles.TryParse("musou", out var extras, out _));
        Assert.Equal(ContentExtras.Musou, extras);
        Assert.Equal("musou", ContentProfiles.Name(extras));
    }

    [Fact]
    public void The_hash_differs_from_musou_and_every_other_profile_and_is_stable()
    {
        ulong musou = (Config with { Extras = ContentExtras.Musou }).Hash();
        ulong warfront = (Config with { Extras = ContentProfiles.WarfrontProfile }).Hash();
        Assert.NotEqual(musou, warfront);
        Assert.NotEqual(Config.Hash(), warfront);
        Assert.NotEqual((Config with { Extras = ContentExtras.All }).Hash(), warfront);
        Assert.Equal(warfront, (Config with { Extras = ContentProfiles.WarfrontProfile }).Hash());
    }

    [Fact]
    public void The_rules_version_is_one_and_the_other_versions_are_untouched()
    {
        Assert.Equal(21u, WarfrontRules.Version);
        Assert.Equal(ExtrasWireVersion, ContentExtras.WireVersion);
        Assert.Equal(ProtocolVersion, Protocol.Version);
        Assert.Equal(MusouSimVersion, SmSim.Extras.Musou.MusouSim.Version);
        Assert.Equal(88u, SmSim.Extras.Musou.Campaign.TfwRules.Version);
        // Only the faithful hash is literal-pinned elsewhere (ContentProfileTests); literal pins of the extended and Musou hashes come in the separate W0 pins slice.
    }

    [Theory]
    [InlineData(ContentExtras.Warfront)]
    [InlineData(ContentExtras.Warfront | ContentExtras.Orks)]
    [InlineData(ContentExtras.Warfront | ContentExtras.Musou | ContentExtras.Gore)]
    public void A_flag_set_with_the_warfront_bit_always_names_warfront(ContentExtras extras)
    {
        Assert.Contains("warfront", ContentProfiles.Name(extras));
        Assert.Contains("warfront", ContentProfiles.Describe(extras));
    }
}
