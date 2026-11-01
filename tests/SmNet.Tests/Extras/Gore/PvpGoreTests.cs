using SmNet.Extras.Gore;
using SmSim;
using Xunit;

namespace SmNet.Tests.Extras.Gore;

/// <summary>The dark gore in classic PvP (Extended only; owner, 2026-11-01): who runs it, and what it makes.</summary>
public class PvpGoreTests
{
    [Theory]
    [InlineData(ContentExtras.None, false)]   // Faithful: no gore at all
    [InlineData(ContentExtras.Orks, false)]
    [InlineData(ContentExtras.LoyalistBarks, false)]
    [InlineData(ContentExtras.Orks | ContentExtras.LoyalistBarks, false)]
    [InlineData(ContentExtras.Gore, true)]
    [InlineData(ContentExtras.Orks | ContentExtras.Gore | ContentExtras.LoyalistBarks, true)]   // Extended
    [InlineData(ContentExtras.Musou, false)]   // Musou Mode has its own feed
    [InlineData(ContentExtras.Gore | ContentExtras.Musou, false)]
    public void Only_a_match_with_the_gore_flag_and_no_musou_runs_it(ContentExtras extras, bool expected) =>
        Assert.Equal(expected, PvpGoreRules.Enabled(extras));

    [Fact]
    public void Extended_has_the_gore_flag_and_never_the_musou_flag() =>
        Assert.True(ContentExtras.All.HasFlag(ContentExtras.Gore) && !ContentExtras.All.HasFlag(ContentExtras.Musou));

    [Fact]
    public void The_clients_pvp_feed_creates_its_view_only_after_asking_the_rule()
    {
        // A text guard (the real gate is faithful-build.cmd: no Extras/ folder compiles there): the one place the PvP gore view is made sits behind PvpGoreRules.Enabled.
        string root = FindRoot();
        string text = File.ReadAllText(Path.Combine(root, "client", "Extras", "Gore", "NetworkView.Gore.cs"));
        int enabled = text.IndexOf("PvpGoreRules.Enabled(", StringComparison.Ordinal);
        int create = text.IndexOf("new GoreView()", StringComparison.Ordinal);
        Assert.True(enabled > 0 && create > enabled);
        Assert.Equal(1, text.Split("new GoreView()").Length - 1);
        // and nothing outside an Extras folder makes one
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "client"), "*.cs", SearchOption.AllDirectories))
            if (!file.Contains($"{Path.DirectorySeparatorChar}Extras{Path.DirectorySeparatorChar}") && !file.Contains($"{Path.DirectorySeparatorChar}.godot{Path.DirectorySeparatorChar}") && !file.Contains($"{Path.DirectorySeparatorChar}.faithful-build{Path.DirectorySeparatorChar}"))
                Assert.DoesNotContain("GoreView", File.ReadAllText(file));
    }

    [Theory]
    [InlineData(KillCause.Melee, false, GoreClass.Gore)]
    [InlineData(KillCause.Weapon, false, GoreClass.Gore)]
    [InlineData(KillCause.Weapon, true, GoreClass.Explosion)]
    [InlineData(KillCause.Thrown, false, GoreClass.Explosion)]
    [InlineData(KillCause.JumpPack, false, GoreClass.Explosion)]
    [InlineData(KillCause.Perk, false, GoreClass.Explosion)]
    [InlineData(KillCause.World, false, GoreClass.None)]
    public void A_players_death_bursts_by_its_cause(KillCause cause, bool splash, GoreClass expected)
    {
        var burst = PvpGoreRules.Death(cause, splash, 5f, 1f);
        Assert.Equal(expected, burst.Class);
        Assert.False(burst.Gibbed);   // the ragdoll stays: no body is removed in PvP
        if (expected != GoreClass.None) Assert.True(burst.Mist >= 1 && burst.Droplets > 0);
    }

    [Fact]
    public void A_hits_size_follows_the_health_lost()
    {
        Assert.Equal(GoreField.HitKind.Shot, PvpGoreRules.HitKindOf(8f));
        Assert.Equal(GoreField.HitKind.Melee, PvpGoreRules.HitKindOf(35f));
        Assert.Equal(GoreField.HitKind.Blast, PvpGoreRules.HitKindOf(80f));
    }

    static string FindRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "SmReimpl.slnx"))) return d.FullName;
        throw new DirectoryNotFoundException("repository root");
    }
}
