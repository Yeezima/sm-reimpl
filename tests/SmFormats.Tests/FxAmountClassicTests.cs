using System.Text.RegularExpressions;
using SmContent;

namespace SmFormats.Tests;

/// <summary>
/// gore-original: Musou Mode's 1.5x blood leaves classic PvP (Annihilation, Seize Ground) as it was. The effect player's two new fields keep the data's look by default (amount 1,
/// smoke rule applied), and only Musou's own files under <c>client/Extras/Musou/</c> set them; the copies rule draws one particle for a factor of 1.
/// </summary>
public class FxAmountClassicTests
{
    static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "SmReimpl.slnx"))) d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("SmReimpl.slnx");
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.5f)]
    [InlineData(0.999f)]
    public void A_factor_of_one_draws_the_datas_count(float roll)
    {
        Assert.Equal(1, FxAmount.Copies(1f, roll));
        Assert.Equal(1, FxAmount.Copies(0.5f, roll));
        Assert.Equal(1, FxAmount.Copies(float.NaN, roll));
    }

    [Fact]
    public void One_and_a_half_draws_half_again_on_average()
    {
        int total = 0;
        for (int i = 0; i < 1000; i++) total += FxAmount.Copies(1.5f, (i + 0.5f) / 1000f);
        Assert.Equal(1500, total);
        Assert.Equal(FxAmount.MaxCopies, FxAmount.Copies(100f, 0f));
    }

    [Fact]
    public void The_effect_player_defaults_to_the_datas_look()
    {
        string player = File.ReadAllText(Path.Combine(Root(), "client", "FxPlayer.cs"));
        Assert.Contains("public float Amount = 1f;", player);
        Assert.Contains("public bool KeepSmoke;", player);
        Assert.Contains("public bool GammaColours;", player);
    }

    [Fact]
    public void Only_musous_files_set_the_amount_or_keep_the_smoke()
    {
        var setter = new Regex(@"\b(Amount|KeepSmoke|GammaColours)\s*=(?!=)", RegexOptions.CultureInvariant);   // a member set, an initializer or a with
        string client = Path.Combine(Root(), "client");
        var offenders = Directory.EnumerateFiles(client, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}.godot{Path.DirectorySeparatorChar}") && setter.IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(client, f).Replace('\\', '/'))
            .Where(f => !f.StartsWith("Extras/Musou/", StringComparison.Ordinal) && f != "FxPlayer.cs" && f != "Extras/Weather/WeatherRig.cs")   // WeatherRig.cs sets a Godot particle node's Amount (its rain count), not an FxEffect's   // FxPlayer.cs declares the defaults (checked above); its one core exception is KeepGrenadeSmoke, whose callers the next test pins
            .ToList();
        Assert.Empty(offenders);
    }

    /// <summary>
    /// grenade-effects (owner 2026-11-17, card G1 "Smoke on frags only"): the one sanctioned core exception to the no-smoke rule. <c>FxEffect.KeepGrenadeSmoke</c> (FxPlayer.cs) sets <c>KeepSmoke</c> for the
    /// frag blasts; exactly one place outside FxPlayer.cs and Musou's files may call it (<c>FxDetonation</c>, behind <c>GrenadeFx.KeepsSmoke</c>), so a second Classic path cannot lift the rule unseen.
    /// </summary>
    [Fact]
    public void Exactly_one_core_caller_lifts_the_no_smoke_rule_for_the_frag_blasts()
    {
        string client = Path.Combine(Root(), "client");
        var callers = Directory.EnumerateFiles(client, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}.godot{Path.DirectorySeparatorChar}"))
            .Select(f => (File: Path.GetRelativePath(client, f).Replace('\\', '/'), Count: Regex.Matches(File.ReadAllText(f), @"\.KeepGrenadeSmoke\(").Count))
            .Where(x => x.Count > 0 && x.File != "FxPlayer.cs" && !x.File.StartsWith("Extras/Musou/", StringComparison.Ordinal))
            .ToList();
        var only = Assert.Single(callers);
        Assert.Equal("NetworkView.Fx.cs", only.File);
        Assert.Equal(1, only.Count);
    }
}
