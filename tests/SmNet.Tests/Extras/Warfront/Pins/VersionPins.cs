using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Warfront.Pins;

/// <summary>
/// W0 pins (Warfront): the version numbers and flag sets of Classic and Musou Mode, captured on the untouched baseline (commit 5428594e, before any Warfront code). A Warfront change must not
/// move any of them; if one has to move for its own reason (a protocol or rules bump by another branch), recapture it with that change, never with Warfront's.
/// </summary>
public class VersionPins
{
    [Fact]
    public void The_versions_and_flag_sets_are_as_before_Warfront()
    {
        Assert.Equal(88, (int)Protocol.Version);   // recaptured 2026-11-19 on main 04b94563 (1809fa31 when first recaptured; Protocol, MusouSim and TfwRules did not move with the merge) (it was 80, 61 and 86 on 5428594e); Warfront moves none of them
        Assert.Equal(2u, ContentExtras.WireVersion);
        Assert.Equal(63u, MusouSim.Version);
        Assert.Equal(88u, TfwRules.Version);
        Assert.Equal(95u, (uint)ContentExtras.Known);   // 31 before weather W3 added ContentExtras.Weather (64, in All; left out of the config hash)
        Assert.Equal(87u, (uint)ContentExtras.All);   // 23 before weather W3
    }
}
