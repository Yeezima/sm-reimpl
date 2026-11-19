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
        foreach (float acc in new[] { 0.3f, 0.49f, SquadAim.ClassicAccuracy, 0.988f })
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
    public void The_Classic_accuracy_is_the_clients_default()
    {
        string src = File.ReadAllText(Path.Combine(RepoRoot(), "client", "NetworkView.BotAim.cs"));
        var m = Regex.Match(src, @"DefaultBotAccuracy\s*=\s*([0-9.]+)f");
        Assert.True(m.Success);
        Assert.Equal(float.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), SquadAim.ClassicAccuracy);
    }

    [Fact]
    public void A_retainers_misses_are_forty_percent_of_a_Classic_shots_and_it_beats_the_flat_weapon_chance_at_every_range()
    {
        foreach (float weapon in new[] { 0.5f, 0.65f, 0.7f, 0.8f, 0.9f })   // the ranged weapons run 0.65 to 0.8; a flat 1.0 could not be beaten by 1 - 0.4 x misses
            foreach (float d in new[] { 1f, 3f, 8f, 15f, 30f, 60f, 100f })
            {
                float classic = SquadAim.ClassicHitChance(weapon, d), retainer = SquadAim.HitChance(weapon, melee: false, d);
                Assert.Equal((1f - classic) * 0.4f, 1f - retainer, 5);
                Assert.True(retainer > weapon - 1e-6f, $"weapon {weapon} at {d} m: {retainer}");   // beats today's flat HitChance
                Assert.InRange(retainer, 0f, 1f);
            }
        Assert.Equal(0.87f, SquadAim.HitChance(0.7f, melee: false, 15f), 2);
    }

    [Fact]
    public void A_blow_keeps_the_weapons_own_chance()
    {
        Assert.Equal(0.7f, SquadAim.HitChance(0.7f, melee: true, 2f));
    }

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SmReimpl.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
