using System.Text.RegularExpressions;
using SmContent;
using Xunit;

namespace SmNet.Tests.Extras.Musou;

/// <summary>
/// musou/sound-ab (owner, 2026-11-10; test aids, Musou Mode only): the names of <c>--musou-sound-off</c>, and the pins that keep Annihilation and Seize Ground's sound as it was: the sound
/// system's A/B switches default to the behaviour before the branch, and only Musou's own files under <c>client/Extras/Musou/</c> set them.
/// </summary>
public class MusouSoundAbTests
{
    static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "SmReimpl.slnx"))) d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("SmReimpl.slnx");
    }

    [Theory]
    [InlineData(null, MusouSoundOff.None)]
    [InlineData("", MusouSoundOff.None)]
    [InlineData("none", MusouSoundOff.None)]
    [InlineData("duck", MusouSoundOff.Duck)]
    [InlineData("Duck, LIMITER", MusouSoundOff.Duck | MusouSoundOff.Limiter)]
    [InlineData("steal,instlimit,ambience", MusouSoundOff.Steal | MusouSoundOff.InstLimit | MusouSoundOff.Ambience)]
    [InlineData("lowpass,hitfilter,crowdvoices,blast", MusouSoundOff.LowPass | MusouSoundOff.HitFilter | MusouSoundOff.CrowdVoices | MusouSoundOff.Blast)]
    [InlineData("allylayers", MusouSoundOff.AllyLayers)]
    [InlineData("all", MusouSoundOff.All)]
    public void The_names_parse(string? text, MusouSoundOff expected)
    {
        Assert.True(MusouSoundAb.TryParse(text, out var off, out string? error), error);
        Assert.Equal(expected, off);
    }

    [Theory]
    [InlineData("ducking")]
    [InlineData("duck,limiters")]
    public void A_typo_is_an_error_and_turns_nothing_off(string text)
    {
        Assert.False(MusouSoundAb.TryParse(text, out var off, out string? error));
        Assert.Equal(MusouSoundOff.None, off);
        Assert.Contains("is not one of", error);
    }

    [Fact]
    public void Every_system_has_a_name_and_all_is_their_sum()
    {
        var sum = MusouSoundAb.Names.Aggregate(MusouSoundOff.None, (a, n) => a | n.Flag);
        Assert.Equal(MusouSoundOff.All, sum);
        Assert.Equal(MusouSoundAb.Names.Count, MusouSoundAb.Names.Select(n => n.Flag).Distinct().Count());
        foreach (var (name, flag) in MusouSoundAb.Names)
        {
            Assert.Equal(name, MusouSoundAb.Describe(flag));
            Assert.True(MusouSoundAb.TryParse(MusouSoundAb.Describe(flag), out var back, out _));
            Assert.Equal(flag, back);
        }
        Assert.Equal("none", MusouSoundAb.Describe(MusouSoundOff.None));
        Assert.Equal("all", MusouSoundAb.Describe(MusouSoundOff.All));
        Assert.Equal("duck,limiter", MusouSoundAb.Describe(MusouSoundOff.Duck | MusouSoundOff.Limiter));
    }

    [Fact]
    public void The_lifted_cap_is_above_the_pool()
    {
        Assert.True(MusouSoundAb.LiftedCap3D > 48);
        Assert.True(MusouSoundAb.LiftedCap2D > 16);
    }

    [Fact]
    public void The_sound_systems_switches_default_to_the_behaviour_as_it_was()
    {
        string sound = File.ReadAllText(Path.Combine(Root(), "client", "SoundSystem.cs"));
        foreach (var plain in new[] { "public bool IgnoreBusActions { get; set; }", "public bool NoLowPass { get; set; }", "public bool NoHitFilter { get; set; }", "public bool NoInstanceLimits { get; set; }" })
            Assert.Matches(Regex.Escape(plain) + @"\s*\r?\n", sound);   // no initializer: false
        Assert.Contains("public int Cap3D { get; set; } = Max3D;", sound);
        Assert.Contains("public int Cap2D { get; set; } = Max2D;", sound);
        Assert.Contains("public const int Max3D = 48, Max2D = 16;", sound);
    }

    [Fact]
    public void Only_musous_files_set_the_switches()
    {
        var setter = new Regex(@"\b(IgnoreBusActions|NoLowPass|NoHitFilter|NoInstanceLimits|Cap3D|Cap2D|MixLog|MixStats)\s*=(?!=)|\b(SetLimitersBypassed|AddMeters)\s*\(", RegexOptions.CultureInvariant);
        string client = Path.Combine(Root(), "client");
        var offenders = Directory.EnumerateFiles(client, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}.godot{Path.DirectorySeparatorChar}") && setter.IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(client, f).Replace('\\', '/'))
            .Where(f => !f.StartsWith("Extras/Musou/", StringComparison.Ordinal) && f != "SoundSystem.cs")   // SoundSystem.cs declares them (checked above) and restores the limiters on exit
            .ToList();
        Assert.Empty(offenders);
    }

    [Fact]
    public void The_live_changes_report_value_and_weight()
    {
        var mods = new SoundModifiers();
        mods.Set(SoundProperty.Lpf, 0x0f736776, 0, 60f, offset: false, now: 0, fadeSeconds: 1.0);
        var half = Assert.Single(mods.Live(0.5));
        Assert.Equal((SoundProperty.Lpf, 0x0f736776u, 0UL), (half.Property, half.Target, half.Scope));
        Assert.Equal(60f, half.Value);
        Assert.Equal(0.5f, half.Weight, 3);
        mods.Reset(SoundProperty.Lpf, 0x0f736776, 0, now: 1.0, fadeSeconds: 0.5);
        Assert.Equal(0f, Assert.Single(mods.Live(1.5)).Weight, 3);
    }
}
