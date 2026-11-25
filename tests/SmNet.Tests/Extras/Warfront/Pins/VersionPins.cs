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
        Assert.Equal(93, (int)Protocol.Version);   // 93: sim/hammer-180 (Classic PvP only; Warfront unchanged); 92: sim/ledge-charge-air's 2 x charge fall (Classic PvP only; Warfront unchanged); 91: sim/stab-key-only (Classic PvP only); 90: sim/ledge-charge-air (Classic PvP only; Warfront unchanged); recaptured 2026-11-19 on main 04b94563 (1809fa31 when first recaptured; Protocol, MusouSim and TfwRules did not move with the merge) (it was 80, 61 and 86 on 5428594e); Warfront moves none of them
        Assert.Equal(2u, ContentExtras.WireVersion);
        Assert.Equal(63u, MusouSim.Version);
        Assert.Equal(88u, TfwRules.Version);
        Assert.Equal(223u, (uint)ContentExtras.Known);   // executions X1 (2026-11-25): + ContentExtras.Executions (128, in All; hashed under its own flag); 95 before it; 31 before weather W3 added ContentExtras.Weather (64, in All; left out of the config hash)
        Assert.Equal(215u, (uint)ContentExtras.All);   // 87 before executions X1 (2026-11-25); 23 before weather W3
    }

    [Fact]
    public void The_Warfront_rules_version_and_wire_format_are_pinned()
    {
        Assert.Equal(23u, SmSim.Extras.Warfront.WarfrontRules.Version);   // no Squigs in Warfront (2026-11-25: WarfrontRules.NoSquigs) (22: the weather finish (2026-11-23): the active weather lifecycle (21: codebase fix 6 (2026-11-22): projectiles test the current pose (20: codebase fix 1, the progress file, the Progress command and the ProgressSave section)
        Assert.Equal(2, SmNet.Extras.Warfront.WarfrontWire.Format);
        Assert.Equal(49, SmNet.Extras.Warfront.WarfrontProgressSaveWire.EnvelopeTag);
    }
}
