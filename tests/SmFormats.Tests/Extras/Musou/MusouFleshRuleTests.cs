using SmContent;
using SmSim.Extras.Musou;

namespace SmFormats.Tests;

/// <summary>Musou Mode (owner, 2026-11-04): a hit on an unarmoured Ork always sounds as the flesh case; armoured Orks and the Chaos Marine keep the usual rule.</summary>
public class MusouFleshRuleTests
{
    [Theory]
    [InlineData(MusouEnemyType.Boy, true)]
    [InlineData(MusouEnemyType.ShootaBoy, true)]
    [InlineData(MusouEnemyType.Gretchin, true)]
    [InlineData(MusouEnemyType.ArdBoy, false)]
    [InlineData(MusouEnemyType.Nob, false)]
    [InlineData(MusouEnemyType.Warboss, false)]
    [InlineData(MusouEnemyType.ChaosMarine, false)]
    public void Only_the_unarmoured_ork_types_always_sound_as_flesh(MusouEnemyType type, bool flesh) =>
        Assert.Equal(flesh, MusouFleshRule.AlwaysFlesh(type));

    [Theory]
    [InlineData(MusouEnemyType.Boy, false)]
    [InlineData(MusouEnemyType.ShootaBoy, false)]
    [InlineData(MusouEnemyType.Gretchin, false)]
    [InlineData(MusouEnemyType.ArdBoy, null)]
    [InlineData(MusouEnemyType.Nob, null)]
    [InlineData(MusouEnemyType.Warboss, null)]
    [InlineData(MusouEnemyType.ChaosMarine, null)]
    [InlineData(MusouEnemyType.BossBoy, false)]
    [InlineData(MusouEnemyType.BossShootaBoy, false)]
    [InlineData(MusouEnemyType.BossArdBoy, null)]
    [InlineData(MusouEnemyType.BossNob, null)]
    public void Material_is_flesh_for_unarmoured_and_both_layers_for_armoured(MusouEnemyType type, bool? material) =>
        Assert.Equal(material, MusouFleshRule.Material(type));

    [Fact]
    public void Every_enemy_type_is_decided_so_a_new_type_is_a_conscious_choice()
    {
        var decided = new HashSet<MusouEnemyType>([MusouEnemyType.Boy, MusouEnemyType.ShootaBoy, MusouEnemyType.Gretchin, MusouEnemyType.ArdBoy, MusouEnemyType.Nob, MusouEnemyType.Warboss, MusouEnemyType.ChaosMarine,
            MusouEnemyType.BossBoy, MusouEnemyType.BossShootaBoy, MusouEnemyType.BossArdBoy, MusouEnemyType.BossNob]);   // the Boss Orks sound as their base (2026-11-06)
        Assert.All(Enum.GetValues<MusouEnemyType>(), t => Assert.Contains(t, decided));
    }
}
