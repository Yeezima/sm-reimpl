using System.Text.RegularExpressions;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Warfront.Guards;

/// <summary>
/// Guardsmen depth (QA 2026-11-20): the client drew at most 120 Guardsmen (<c>GuardCrowd.DefaultVisible</c>), so with Warfront's doubled numbers (240 and more known) the far rows vanished from the screen.
/// Warfront's client draws every Guardsman the sim can field; Musou and Classic keep the 120. The client is not a test reference, so the constants are read as source text.
/// </summary>
public class WarfrontGuardDrawCapTests
{
    static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "SmReimpl.slnx"))) d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("SmReimpl.slnx not found above the test assembly");
    }

    [Fact]
    public void Warfront_draws_every_guardsman_the_sim_can_field_and_the_old_modes_keep_the_cap()
    {
        string crowd = File.ReadAllText(Path.Combine(RepoRoot(), "client", "Extras", "Musou", "Tfw", "GuardCrowd.cs"));
        string view = File.ReadAllText(Path.Combine(RepoRoot(), "client", "Extras", "Musou", "Tfw", "NetworkView.TfwGuards.cs"));
        var m = Regex.Match(crowd, @"WarfrontVisible = (\d+)");
        Assert.True(m.Success, "GuardCrowd.WarfrontVisible is missing");
        Assert.True(int.Parse(m.Groups[1].Value) >= GuardSim.MaxSquads * GuardSim.SquadSize, "Warfront must draw all 400 Guardsmen of the sim's 40 squads");
        Assert.Matches(@"DefaultVisible = 120\b", crowd);   // Musou and Classic unchanged
        Assert.Contains("GuardCrowd.VisibleOverride > 0 ? GuardCrowd.VisibleOverride : GuardCrowd.DefaultVisible", view);
        Assert.DoesNotContain("WarfrontOn", view);   // the Musou file builds without Warfront
        string perf = File.ReadAllText(Path.Combine(RepoRoot(), "client", "Extras", "Warfront", "NetworkView.WarfrontPerf.cs"));
        Assert.Contains("GuardCrowd.VisibleOverride = WarfrontOn ? GuardCrowd.WarfrontVisible : 0", perf);
    }
}
