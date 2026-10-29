using SmContent;

namespace SmNet.Tests;

/// <summary>feel/scope-lascannon: which weapons show the scope overlay.</summary>
public class ScopeKindTests
{
    [Theory]
    [InlineData("sm_mp_ranged_sniper_rifle", ScopeKind.Stalker)]
    [InlineData("csm_mp_ranged_sniper_rifle", ScopeKind.Stalker)]
    [InlineData("sm_mp_exotic_las_cannon", ScopeKind.Lascannon)]
    [InlineData("csm_mp_exotic_las_cannon", ScopeKind.Lascannon)]
    public void TheStalkerBolterAndTheLascannonHaveAScope(string wargear, ScopeKind kind) => Assert.Equal(kind, ScopeArt.KindOf(wargear));

    [Theory]
    [InlineData("sm_mp_ranged_bolter")]
    [InlineData("sm_mp_exotic_heavy_bolter")]
    [InlineData("sm_mp_exotic_plasma_cannon")]
    [InlineData("sm_mp_pistol_bolt_pistol")]
    [InlineData("")]
    [InlineData(null)]
    public void OtherWeaponsHaveNone(string? wargear) => Assert.Null(ScopeArt.KindOf(wargear));
}
