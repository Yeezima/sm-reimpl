using System.IO;
using System.Linq;
using SmClient.Customizer.Model;
using Xunit;

namespace SmFormats.Tests;

/// <summary>ui/lighting-presets (the owner's request of 2026-11-06): the ten looks of OPTIONS, GRAPHICS, their order, default, limits and persistence. Engine-free.</summary>
public sealed class LightingPresetsTests
{
    [Fact]
    public void Ten_presets_in_the_owners_order()
    {
        Assert.Equal(new[]
        {
            "Faithful Remaster", "Natural Cinema", "Pale Ashfall", "Golden Epic", "Blue-Hour Steel",
            "Stormbreak", "Gothic Chiaroscuro", "Forge Amber", "Bleach-Bypass Warfilm", "Ash + Color Balance",
        }, LightingPresets.Labels);
        Assert.Equal(10, System.Enum.GetValues<LightingPresetChoice>().Length);
    }

    [Fact]
    public void Faithful_Remaster_is_the_default_and_changes_nothing()
    {
        Assert.Equal(LightingPresetChoice.FaithfulRemaster, GameOptions.Default.Lighting);
        Assert.True(LightingPresets.For(LightingPresetChoice.FaithfulRemaster).IsIdentity);
    }

    [Fact]
    public void Every_other_preset_changes_the_look_within_readable_limits()
    {
        foreach (var choice in System.Enum.GetValues<LightingPresetChoice>().Skip(1))
        {
            var g = LightingPresets.For(choice);
            Assert.False(g.IsIdentity, choice.ToString());
            Assert.InRange(g.Exposure, 0.95f, 1.08f);
            Assert.InRange(g.Ambient, 0.9f, 1.2f);
            Assert.InRange(g.FogDensity, 0.8f, 1.35f);
            Assert.InRange(g.Contrast, 0.9f, 1.16f);
            Assert.InRange(g.Saturation, 0.55f, 1.1f);
            Assert.InRange(g.SunEnergy, 0.8f, 1.2f);
            Assert.InRange(g.FogTint, 0f, 0.6f);
        }
    }

    [Fact]
    public void The_choice_is_saved_and_a_bad_one_reads_as_the_default()
    {
        string dir = Directory.CreateTempSubdirectory("sm-presets").FullName;
        try
        {
            var settings = FrontEndSettings.Default with { Options = GameOptions.Default with { Lighting = LightingPresetChoice.BleachBypassWarfilm } };
            Assert.Null(settings.Save(dir));
            Assert.Equal(LightingPresetChoice.BleachBypassWarfilm, FrontEndSettings.Load(dir).Options.Lighting);
            File.WriteAllText(Path.Combine(dir, FrontEndSettings.FileName), "{\"options\":{\"lightingPreset\":\"Neon\"}}");
            Assert.Equal(LightingPresetChoice.FaithfulRemaster, FrontEndSettings.Load(dir).Options.Lighting);
            Assert.Equal(LightingPresetChoice.FaithfulRemaster, (GameOptions.Default with { Lighting = (LightingPresetChoice)42 }).Clamped().Lighting);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("1", LightingPresetChoice.FaithfulRemaster)]
    [InlineData("10", LightingPresetChoice.AshColorBalance)]
    [InlineData("goldenepic", LightingPresetChoice.GoldenEpic)]
    [InlineData("Blue-Hour Steel", LightingPresetChoice.BlueHourSteel)]
    [InlineData("ash + color balance", LightingPresetChoice.AshColorBalance)]
    public void Command_line_names(string text, LightingPresetChoice expected) => Assert.Equal(expected, LightingPresets.Parse(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("11")]
    [InlineData("neon")]
    public void Unknown_command_line_names(string? text) => Assert.Null(LightingPresets.Parse(text));
}
