using SmContent;
using SmFormats.Baf;

namespace SmFormats.Tests;

/// <summary>
/// sim/stormbolter-ammo (the owner, 2026-11-01: "the storm bolter for tactical is currently not using a bullet every shot"): the rounds one shot spends are the
/// delivery's <c>ammo_used</c> amount plus every <c>consume_ammo</c> of its <c>fire_sim</c> (the storm bolters' second barrel: a <c>delayed_action</c> that consumes
/// 1 more after 0.05 s). Synthetic wargear only.
/// </summary>
public class ShotAmmoTests
{
    static BafDocument Gun((int Amount, float Delay)? consume = null, bool heat = false) =>
        BafDocument.Read(SyntheticBafWriter.RangedWeapon(22f, 0.1f, 100, 500, 2f, 250f, [], 1f, 1f, 0.625f, 0.625f,
            heat: heat ? (3f, 100f, 75f, 20f, 25f, 0.3f, 0f) : null, consumeAmmo: consume));

    [Fact]
    public void A_fire_sim_consume_ammo_adds_its_rounds_to_each_shot()
    {
        var notes = new List<string>();
        var storm = WeaponTuning.Read("storm", Gun((1, 0.05f)), notes)!;
        Assert.Equal(2, storm.AmmoPerShot);                       // ammo_used 1 + the delayed consume_ammo 1; the other ammunition's 5 not counted
        Assert.Equal((100, 500, 0.1f), (storm.ClipSize, storm.ReserveSize, storm.ShotCooldown));   // the clip and rate as read
        Assert.Contains(notes, n => n.Contains("storm") && n.Contains("consume_ammo 1 more per shot (2 in all)") && n.Contains("0.05 s"));
    }

    [Fact]
    public void Without_a_consume_ammo_a_shot_spends_the_ammo_used_amount_alone()
    {
        var notes = new List<string>();
        Assert.Equal(1, WeaponTuning.Read("bolter", Gun(), notes)!.AmmoPerShot);
        Assert.Equal(1, WeaponTuning.Read("hot", Gun(heat: true), notes)!.AmmoPerShot);   // a fire_sim with heat only
        Assert.DoesNotContain(notes, n => n.Contains("consume_ammo"));
        // Heat and a consume_ammo together: both are read.
        var both = WeaponTuning.Read("both", Gun((3, 0f), heat: true), notes)!;
        Assert.Equal((4, 3f), (both.AmmoPerShot, both.Heat!.PerShot));
    }
}
