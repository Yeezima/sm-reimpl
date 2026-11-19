using System.Numerics;
using SmNet.Tests.Extras.Warfront.Wiring;
using SmSim.Extras.Musou;
using SmSim.Extras.Warfront;
using SmSim.Extras.Warfront.Campaign;
using SmSim.Extras.Warfront.Progression;
using SmSim.Extras.Warfront.Squad;

namespace SmNet.Tests.Extras.Warfront.Squad;

/// <summary>Retainer AI slice S1 (owner 2026-11-19): a retainer's damage and health grow with its player's level on the Orks' curve (HP +0.08, damage +0.05 a level), on top of the cards' third.</summary>
public class SquadScalingTests
{
    static void LevelUp(WarfrontSquadWiringTests.Rig r, ulong key, WarfrontCard card, int picks)
    {
        r.Campaign.AddXp(key, WarfrontCampaign.XpForLevel(picks + 1) - r.Campaign.Player(key)!.Xp);
        for (int i = 0; i < picks; i++) Assert.True(r.Campaign.TryTakePick(key, (int)card));
    }

    [Fact]
    public void The_level_curve_is_the_Orks_own_and_level_one_is_exactly_one()
    {
        Assert.Equal((1f, 1f), WarfrontSquadWiring.SquadLevelScale(1));
        Assert.Equal((1f, 1f), WarfrontSquadWiring.SquadLevelScale(0));
        var (hp, dmg) = WarfrontSquadWiring.SquadLevelScale(10);
        Assert.Equal(1f + 0.08f * 9, hp, 4);
        Assert.Equal(1f + 0.05f * 9, dmg, 4);
        var orks = WarfrontOrkScaling.LevelScale(10);   // the Orks' curve, without their base knobs
        Assert.Equal(orks.Health / WarfrontProgressionRules.OrkHealthBase, hp, 4);
        Assert.Equal(orks.Damage / WarfrontProgressionRules.OrkDamageBase, dmg, 4);
    }

    /// <summary>The first blow's damage on a far-off tough Ork, as a multiple of the weapon's damage x DamageDealt.</summary>
    static float FirstBlow(WarfrontSquadWiringTests.Rig r)
    {
        var at = r.Sim.View(r.IndexOf(1, 0))!.Value.Position;
        ushort ork = r.W.Musou!.SpawnLeashed(MusouEnemyType.Nob, at + new Vector3(8f, 0f, 0f), 0f, 1e6f, 1, 10f);
        r.W.Musou.Frozen = true;
        float full = r.W.Musou.TryGet(ork, out var o0) ? o0.Health : 0f, drop = 0f;
        for (int t = 0; t < 240 && drop == 0f; t++) { r.Stand(1); if (r.W.Musou.TryGet(ork, out var e) && e.Health < full) drop = full - e.Health; }
        Assert.True(drop > 0f);
        return drop / (r.Wi.Rosters.Marine(1, 0)!.Loadout.Weapon.Damage * SquadRules.DamageDealt);
    }

    [Fact]
    public void A_level_one_retainer_deals_the_plain_damage_and_a_level_ten_one_deals_more_and_the_scale_follows_a_level_up()
    {
        var low = WarfrontSquadWiringTests.Rig.Make(marines: 2, deploy: false);
        low.Deploy();
        Assert.Equal(1f, FirstBlow(low), 2);

        var up = WarfrontSquadWiringTests.Rig.Make(marines: 2, deploy: false);
        up.Deploy();
        LevelUp(up, 1, WarfrontCard.CooldownReduction, 9);   // level 10; a cooldown card changes no retainer damage
        Assert.Equal(1f, WarfrontStatBonuses.ForRetainer(up.Campaign.Player(1)!.Cards).Damage, 3);
        up.Stand(1);   // the scale is re-applied when the level changed, the card count of the key notwithstanding
        Assert.Equal(1f + 0.05f * 9, FirstBlow(up), 2);
    }

    [Fact]
    public void Health_multiplies_the_cards_share_with_the_level()
    {
        var r = WarfrontSquadWiringTests.Rig.Make(marines: 2, deploy: false);
        LevelUp(r, 1, WarfrontCard.MaxHp, 9);   // level 10 with nine health cards
        r.Deploy();
        float share = WarfrontStatBonuses.ForRetainer(r.Campaign.Player(1)!.Cards).MaxHp;
        Assert.True(share > 1f);
        Assert.Equal(r.Wi.Rosters.Marine(1, 0)!.Loadout.HealthMax * share * (1f + 0.08f * 9), r.Sim.BodyOf(r.IndexOf(1, 0)).Health, 1);
    }
}
