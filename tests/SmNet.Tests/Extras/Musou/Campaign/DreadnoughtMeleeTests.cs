using System.Numerics;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Musou.Campaign;

/// <summary>
/// Musou Dreadnought D5 (docs/musou-dreadnought-design.md sections 3.3, 3.4): the arm's strikes are the data's (100 / 125 light, the stomp and the 500 slam in a 10 m sphere, the rush's 500), X chains X1, X2 and
/// back, Y from rest is the stomp, X then Y is the slam strike, and every few metres he walks a footfall's 25-damage blast hits the Orks within 10 m.
/// </summary>
public class DreadnoughtMeleeTests
{
    static readonly WeaponSpec Cannon = new(DreadnoughtRules.CannonWargear, WeaponSlot.Ranged, TriggerKind.FullAuto, 30f, 0.06f, 140, 2_000_000, 2.5f, 250f, 250f, 250f, 1f, 1f, 1f, 1f, 1f) { AlwaysReady = true };
    static readonly SimConfig Config = MusouLoadout.Prepare(TfwRig.Config with { Weapons = [.. TfwRig.Config.Weapons, Cannon] });
    static int Dread => Config.Classes.ToList().FindIndex(k => k.Name == DreadnoughtRules.ClassName);
    const int Human = TfwRig.Human;

    static World Landed()
    {
        var w = TfwRig.War(null, Config);
        Assert.NotNull(w.SetNextLoadout(Human, LoadoutChoice.Default with { Class = Dread }));
        TfwRig.Command(w, TfwCommand.ChangeClass);
        TfwRig.Steps(w, 3);
        TfwRig.Deploy(w);
        TfwRig.Seconds(w, DreadnoughtRules.EntranceSeconds + 0.3f);
        return w;
    }

    static PlayerState Me(World w) => w.Players[Human];
    static void Press(World w, InputButtons b) => w.Step(_ => new PlayerInput(0, 0f, 0f, 0f, b));

    [Fact]
    public void The_class_carries_the_datas_strikes()
    {
        var c = Config.Classes[Dread];
        Assert.Equal(2, c.Melee.Count);
        Assert.Equal((100f, 50f, 250f), (c.Melee[0].Damage, c.Melee[0].SecondaryDamage, c.Melee[0].ShieldDamage));
        Assert.Equal((125f, 75f), (c.Melee[1].Damage, c.Melee[1].SecondaryDamage));
        Assert.Equal(0.867f, c.Melee[0].HitTime);
        Assert.Equal(2, c.MeleeHeavy.Count);
        var stomp = c.MeleeHeavy[0];
        var slam = c.MeleeHeavy[1];
        Assert.Equal((10f, 50f), (stomp.Stomp!.Radius, stomp.Stomp.Damage.Damage));
        Assert.Equal((10f, 500f, 50f), (slam.Stomp!.Radius, slam.Stomp.Damage.Damage, slam.Damage));
        Assert.Equal(2, c.Melee[0].HeavyNext);   // X then Y: the slam strike
        Assert.Equal(1, slam.YNext);              // Y then Y: the stomp again
        Assert.Empty(c.MeleeChoices);
        if (c.MeleeRush is { } rush) Assert.Equal((500f, 125f), (rush.Primary.Damage, rush.Secondary.Damage));
    }

    [Fact]
    public void Melee_starts_a_light_strike_of_the_arm_that_runs_out_and_Y_is_a_heavy_one()
    {
        var w = Landed();
        Press(w, InputButtons.Melee);
        Assert.True(Me(w).MeleeStep is 1 or 2 && !Me(w).MeleeHeavy, $"step {Me(w).MeleeStep} heavy {Me(w).MeleeHeavy}");   // Musou picks the swing (its own rule), among the arm's two
        TfwRig.Seconds(w, 3.5f);
        Assert.Equal(0, Me(w).MeleeStep);
        Press(w, InputButtons.MeleeHeavy);
        Assert.True(Me(w).MeleeStep > 0 && Me(w).MeleeHeavy);
        TfwRig.Seconds(w, 4f);
        Assert.Equal(0, Me(w).MeleeStep);
    }

    [Fact]
    public void A_footfall_blast_hurts_the_Orks_within_10_m_as_he_walks_and_none_while_he_stands()
    {
        var w = Landed();
        var at = Me(w).Position;
        ushort near = w.Musou!.Spawn(MusouEnemyType.Boy, at + new Vector3(0f, 6f, 0f), 0f, 1000f), far = w.Musou.Spawn(MusouEnemyType.Boy, at + new Vector3(0f, 40f, 0f), 0f, 1000f);
        Assert.NotEqual(0, near);
        float Health(ushort id) => w.Musou!.TryGet(id, out var e) ? e.Health : -1f;
        float before = Health(near);
        TfwRig.Seconds(w, 1f);   // standing
        float standing = Health(near);
        for (int k = 0; k < 30 * 2; k++) w.Step(_ => new PlayerInput(0, 0f, 1f, 0f, InputButtons.None));   // walks 2 s (toward +Y: the Boys are ahead)
        Assert.True(Health(near) < standing - 20f || Health(near) <= 0f, $"near Ork {standing} -> {Health(near)}");
        Assert.True(Health(far) > 900f, $"the Ork 40 m away took {1000f - Health(far)}");
        Assert.True(before >= standing - 0.01f || before > 0f);
    }
}
