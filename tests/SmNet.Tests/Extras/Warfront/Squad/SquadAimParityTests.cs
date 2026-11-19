using System.Text.RegularExpressions;
using SmSim.Extras.Warfront.Squad;

namespace SmNet.Tests.Extras.Warfront.Squad;

/// <summary>Retainer AI slice S1: <see cref="SquadAim"/> is a copy of <see cref="BotAimModel"/>'s hit-chance formula (SmSim cannot reference SmNet); this pins the two together and the retainer's 60 % fewer misses.</summary>
public class SquadAimParityTests
{
    [Fact]
    public void The_copied_constants_and_formula_match_BotAimModel_at_every_range()
    {
        Assert.Equal(BotAimModel.ReferenceDistance, SquadAim.ReferenceDistance);
        Assert.Equal(BotAimModel.BodyRadius, SquadAim.BodyRadius);
        Assert.Equal(BotAimModel.MinScaleDistance, SquadAim.MinScaleDistance);
        Assert.Equal(BotAimModel.MinScale, SquadAim.MinScale);
        Assert.Equal(BotAimModel.MaxScale, SquadAim.MaxScale);
        foreach (float acc in new[] { 0.3f, 0.49f, SquadAim.ClassicAccuracy, SquadAim.RetainerAccuracy })
        {
            Assert.Equal(BotAimModel.SigmaFor(acc), SquadAim.SigmaFor(acc), 6);
            foreach (float d in new[] { 0.5f, 2f, 3f, 3.75f, 8f, 15f, 25f, 50f, 100f, 200f })
            {
                Assert.Equal(BotAimModel.DistanceScale(d), SquadAim.DistanceScale(d), 6);
                float expect = BotAimModel.HitChance(BotAimModel.SigmaFor(acc) * BotAimModel.DistanceScale(d), d);
                Assert.Equal(expect, SquadAim.AimChance(acc, d), 5);
            }
        }
    }

    [Fact]
    public void The_Classic_accuracy_is_the_clients_default_and_a_retainer_misses_forty_percent_as_often()
    {
        string src = File.ReadAllText(Path.Combine(RepoRoot(), "client", "NetworkView.BotAim.cs"));
        var m = Regex.Match(src, @"DefaultBotAccuracy\s*=\s*([0-9.]+)f");
        Assert.True(m.Success);
        Assert.Equal(float.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), SquadAim.ClassicAccuracy);
        Assert.Equal(0.988f, SquadAim.RetainerAccuracy, 5);
        Assert.Equal(0.988f, SquadAim.RetainerAimChance(15f), 4);
        foreach (float d in new[] { 8f, 15f, 30f, 60f })
        {
            float classicMiss = 1f - SquadAim.AimChance(SquadAim.ClassicAccuracy, d), retainerMiss = 1f - SquadAim.RetainerAimChance(d);
            Assert.InRange(retainerMiss / classicMiss, 0.38f, 0.42f);   // 0.4 at the reference range
        }
    }

    [Fact]
    public void A_blow_keeps_the_weapons_own_chance_and_a_round_is_the_weapons_chance_times_the_aim()
    {
        Assert.Equal(0.7f, SquadAim.HitChance(0.7f, melee: true, 2f));
        Assert.Equal(0.7f * 0.988f, SquadAim.HitChance(0.7f, melee: false, 15f), 4);
    }

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SmReimpl.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
