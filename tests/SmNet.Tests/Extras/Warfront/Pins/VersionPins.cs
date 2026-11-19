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
        Assert.Equal(80, (int)Protocol.Version);
        Assert.Equal(2u, ContentExtras.WireVersion);
        Assert.Equal(61u, MusouSim.Version);
        Assert.Equal(86u, TfwRules.Version);
        Assert.Equal(31u, (uint)ContentExtras.Known);
        Assert.Equal(23u, (uint)ContentExtras.All);
    }
}
