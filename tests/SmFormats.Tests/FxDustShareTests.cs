using SmContent;

namespace SmFormats.Tests;

/// <summary>musou/autocannon-dust: the dust share's roll, and that only Musou's autocannon files set it (Classic PvP and every other effect keep the data's 1).</summary>
public class FxDustShareTests
{
    static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "SmReimpl.slnx"))) d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("SmReimpl.slnx");
    }

    [Theory]
    [InlineData(1f, 0.99f, true)]
    [InlineData(2f, 0.99f, true)]
    [InlineData(float.NaN, 0.99f, true)]
    [InlineData(0.5f, 0.49f, true)]
    [InlineData(0.5f, 0.5f, false)]
    [InlineData(0.5f, 0.99f, false)]
    [InlineData(0f, 0f, false)]
    public void The_roll_under_the_share_keeps_the_particle(float share, float roll, bool kept) => Assert.Equal(kept, FxAmount.Kept(share, roll));

    [Fact]
    public void Half_the_particles_stay_over_many_rolls()
    {
        int kept = 0;
        for (int i = 0; i < 1000; i++) if (FxAmount.Kept(0.5f, (i + 0.5f) / 1000f)) kept++;
        Assert.Equal(500, kept);
    }

    [Fact]
    public void Only_the_autocannons_extras_files_set_the_share()
    {
        string client = Path.Combine(Root(), "client");
        Assert.Contains("public float DustShare = 1f;", File.ReadAllText(Path.Combine(client, "FxPlayer.cs")));
        var setter = new System.Text.RegularExpressions.Regex(@"\.DustShare\s*=(?!=)");
        foreach (var file in Directory.EnumerateFiles(client, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) || file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)) continue;
            string text = File.ReadAllText(file);
            if (!setter.IsMatch(text)) continue;
            string rel = Path.GetRelativePath(client, file).Replace(Path.DirectorySeparatorChar, '/');
            Assert.True(rel is "NetworkView.Fx.cs", rel);   // the core sets only what the Musou hook / argument hands it (1 everywhere else)
        }
        Assert.Contains("DustShare = 0.5f", File.ReadAllText(Path.Combine(client, "Extras/Musou/Arsenal/Autocannon/NetworkView.AutocannonDust.cs")).Replace("internal const float ", ""));
        Assert.Contains("musou-old-autocannon-fx", File.ReadAllText(Path.Combine(Root(), "src/SmNet/LeanBot.cs")));
    }
}
