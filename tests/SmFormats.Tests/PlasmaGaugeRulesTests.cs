using SmContent;

namespace SmFormats.Tests;

/// <summary>
/// fix/plasma-visuals: the plasma gauges at the crosshair (<see cref="PlasmaGaugeRules"/>): which families show them, which label and arc each picks, the frames for the heat, the charge
/// and the overheat flash; and (with the player's install, read-only, only names, labels and counts compared) that the movie has the pieces with the frames the rules were written for.
/// </summary>
public class PlasmaGaugeRulesTests
{
    [Theory]
    [InlineData("plasma_gun", true, "normal", "PlasmaCharge_mc")]
    [InlineData("plasma_pistol", true, "normal", "PlasmaCharge_mc")]
    [InlineData("plasma_cannon", true, "plasmacannon", "PlasmaCannonCharge_mc")]
    [InlineData("heavy_bolter", false, "normal", "PlasmaCharge_mc")]
    [InlineData("bolter", false, "normal", "PlasmaCharge_mc")]
    public void Families_pick_their_heat_label_and_charge_arc(string family, bool plasma, string label, string arc)
    {
        Assert.Equal(plasma, PlasmaGaugeRules.IsPlasma(family));
        Assert.Equal(label, PlasmaGaugeRules.HeatLabel(family));
        Assert.Equal(arc, PlasmaGaugeRules.ChargeArc(family));
    }

    [Theory]
    [InlineData(0f, 100f, 0)]
    [InlineData(50f, 100f, 30)]
    [InlineData(100f, 100f, 59)]
    [InlineData(140f, 100f, 59)]
    [InlineData(-5f, 100f, 0)]
    [InlineData(float.NaN, 100f, 0)]
    [InlineData(50f, 0f, 0)]
    public void The_heat_meter_runs_over_its_60_frames(float heat, float overheat, int frame) => Assert.Equal(frame, PlasmaGaugeRules.MeterFrame(heat, overheat, 60));

    [Theory]
    [InlineData(-1f, 0)]
    [InlineData(0f, 0)]
    [InlineData(0.5f, 30)]
    [InlineData(1f, 59)]
    [InlineData(3f, 59)]
    [InlineData(float.NaN, 0)]
    public void The_charge_arc_fills_with_the_charge(float charge, int frame) => Assert.Equal(frame, PlasmaGaugeRules.ChargeFrame(charge, 60));

    [Fact]
    public void The_overheat_flash_loops_its_danger_frames_at_30_fps()
    {
        Assert.Equal(0, PlasmaGaugeRules.OverheatFrame(false, 1.0, 1, 12));
        Assert.Equal(1, PlasmaGaugeRules.OverheatFrame(true, 0.0, 1, 12));
        Assert.Equal(1 + 5, PlasmaGaugeRules.OverheatFrame(true, 5.0 / 30 + 1e-4, 1, 12));
        Assert.Equal(1, PlasmaGaugeRules.OverheatFrame(true, 11.0 / 30 + 1e-4, 1, 12));   // 11 danger frames, then round again
        Assert.Equal(0, PlasmaGaugeRules.OverheatFrame(true, 1.0, 1, 1));
        Assert.Equal(3, PlasmaGaugeRules.Looped(13.0 / 30 + 1e-4, 10));
        Assert.Equal(0, PlasmaGaugeRules.Looped(double.NaN, 10));
    }

    [HudMoviesInstallPinTests.InstallFact]
    public void The_movie_has_the_gauges_the_rules_name()
    {
        var hud = HudMovies.Load(GameInstall.TryLocate()!.Dir).Hud!;
        string heat = $"{ReticleRules.Container}/{PlasmaGaugeRules.Heat}";
        int heatSprite = hud.Find(heat)!.Value.Placement.Character;
        var labels = hud.Labels(heatSprite);
        Assert.True(labels.ContainsKey("normal") && labels.ContainsKey("plasmacannon"), string.Join(",", labels.Keys));
        Assert.Equal(60, hud.FrameCount(hud.Find($"{heat}/{PlasmaGaugeRules.Meter}")!.Value.Placement.Character));
        Assert.NotNull(hud.Find($"{heat}/{PlasmaGaugeRules.Meter}/vent_txt"));
        int flash = hud.Find($"{heat}/{PlasmaGaugeRules.Overheat}")!.Value.Placement.Character;
        Assert.Equal(1, hud.Labels(flash)[PlasmaGaugeRules.DangerLabel]);
        foreach (var family in new[] { "plasma_gun", "plasma_cannon" })
        {
            string arc = $"{ReticleRules.Container}/{PlasmaGaugeRules.ChargeArc(family)}";
            Assert.Equal(60, hud.FrameCount(hud.Find(arc)!.Value.Placement.Character));
            Assert.NotNull(hud.RenderAt(arc, ReticleRules.Container, 1.0, 59));
        }
        // The heat arc draws under the reticle's centre (its box centre about 40 movie pixels below the container's origin).
        var pic = hud.RenderAt($"{heat}/{PlasmaGaugeRules.Meter}", ReticleRules.Container, 1.0, 59);
        Assert.NotNull(pic);
        Assert.InRange(pic!.Height / 2.0 - pic.OriginY, 20.0, 60.0);
    }
}
