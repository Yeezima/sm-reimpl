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
        Assert.Equal(0.867f / DreadnoughtRules.MeleeSpeed, c.Melee[0].HitTime, 4);   // the data's tag at the buff's 1.3 x (2026-11-15)
        Assert.Equal(2, c.MeleeHeavy.Count);
        var stomp = c.MeleeHeavy[0];
        var slam = c.MeleeHeavy[1];
        Assert.Equal((10f, 50f), (stomp.Stomp!.Radius, stomp.Stomp.Damage.Damage));
        Assert.Equal((10f, 500f, 50f), (slam.Stomp!.Radius, slam.Stomp.Damage.Damage, slam.Damage));
        Assert.Equal(2, c.Melee[0].HeavyNext);   // X then Y: the slam strike
        Assert.Equal(1, slam.YNext);              // Y then Y: the stomp again
        Assert.Empty(c.MeleeChoices);
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
        Assert.True(standing <= before + 0.01f);
    }

    static float Boy(World w, Vector3 at, out ushort id) { id = w.Musou!.Spawn(MusouEnemyType.Boy, at, 0f, 100000f); Assert.NotEqual((ushort)0, id); return 100000f; }
    static float HealthOf(World w, ushort id) => w.Musou!.TryGet(id, out var e) ? e.Health : -1f;

    /// <summary>Damage the strikes deal an Ork 3 m ahead (Musou's own scales on top: the cleave, the stim, the war's multipliers), by the step run out.</summary>
    [Fact]
    public void The_strikes_deal_the_datas_damage_or_more_to_an_Ork_in_reach_the_slam_500_and_the_stomp_50()
    {
        float Dealt(Func<World, bool> act)
        {
            var w = Landed();
            var at = Me(w).Position;
            Boy(w, at + new Vector3(0f, 3f, 0f), out ushort boy);
            Assert.True(act(w));
            TfwRig.Seconds(w, 4.5f);
            return 100000f - HealthOf(w, boy);
        }
        // X from rest: X01 (100) or X02 (125), the cleave and Musou's scales on top
        float light = Dealt(w => { Press(w, InputButtons.Melee); return true; });
        Assert.True(light >= 100f - 0.5f, $"a light strike dealt {light}");
        // Y from rest: the stomp's 50 in 10 m (the Ork at 3 m takes it whole: no fall-off is read)
        float stomp = Dealt(w => { Press(w, InputButtons.MeleeHeavy); return true; });
        Assert.True(stomp >= 50f - 0.5f, $"the stomp dealt {stomp}");
        // X then Y: Y01 with the slam's 500 (and its own 50)
        float slam = Dealt(w =>
        {
            Press(w, InputButtons.Melee);
            for (int k = 0; k < 32; k++) Press(w, InputButtons.None);
            Press(w, InputButtons.MeleeHeavy);
            return Me(w).MeleeHeavy;
        });
        Assert.True(slam >= 500f, $"X then Y dealt {slam}");
        Assert.True(slam > stomp + 400f);
    }

    [Fact]
    public void The_slam_and_the_footfalls_never_hurt_a_marine_standing_next_to_him()
    {
        var w = Landed();
        var at = Me(w).Position;
        w.Spawn(7, 0);
        w.MusouJoined(7);
        TfwRig.Steps(w, 3);
        Assert.True(w.Players.ContainsKey(7));
        Assert.True(w.TfwRespawnAt(7, at + new Vector3(3f, 0f, 0f), 0f));
        TfwRig.Seconds(w, 6f);   // past the other's own protection
        float before = w.Players[7].Health + w.Players[7].Shield;
        Press(w, InputButtons.Melee);
        for (int k = 0; k < 32; k++) Press(w, InputButtons.None);
        Press(w, InputButtons.MeleeHeavy);
        for (int k = 0; k < 30 * 3; k++) w.Step(i => i == Human ? new PlayerInput(0, 0f, 1f, 0f, InputButtons.None) : default);   // and a walk: footfalls
        var after = w.Players[7];
        Assert.True(after.Alive);
        Assert.True(after.Health + after.Shield >= before - 0.01f, $"the marine went from {before} to {after.Health + after.Shield}");
    }

    [Fact]
    public void The_rush_is_the_tacticals_path_with_the_datas_500_and_125_and_the_4_m_reach()
    {
        var move = new RushMove("rush", [Vector2.Zero, new Vector2(0f, 3f)], 1f, 1f, 0.5f, 0.5f, 0.5f);
        var dmg = new DamageSpec(75f, 25f, 1f, 1f, 1f, 1f, false);
        var rush = new RushSpec("bullrush", move, move, dmg, default, dmg, default, 3f, 1f);
        var raw = TfwRig.Config with { Weapons = [.. TfwRig.Config.Weapons, Cannon] };
        raw = raw with { Classes = raw.Classes.Select((k, n) => n == 0 ? k with { MeleeRush = rush } : k).ToList() };
        var prepared = MusouLoadout.Prepare(raw);
        var c = prepared.Classes.First(k => k.Name == DreadnoughtRules.ClassName);
        Assert.NotNull(c.MeleeRush);
        Assert.Equal((500f, 1000f, 125f, 75f, DreadnoughtMelee.Reach), (c.MeleeRush!.Primary.Damage, c.MeleeRush.Primary.ShieldDamage, c.MeleeRush.Secondary.Damage, c.MeleeRush.Secondary.ShieldDamage, c.MeleeRush.Range));
        Assert.Equal(75f, prepared.Classes[0].MeleeRush!.Primary.Damage);   // the Tactical's own is untouched
    }

    /// <summary>DA's stomp (the arm's Y from rest, a foot hit in a 10 m sphere; owner 2026-11-15: "Musou's Orks react to the stomp's knockback and interrupt"): an Ork 5 m off takes the 50 and is pushed away or loses its footing; one 40 m away does not.</summary>
    [Fact]
    public void The_stomp_pushes_back_and_hurts_the_Orks_in_its_10_m_and_leaves_the_far_ones()
    {
        var w = Landed();
        var at = Me(w).Position;
        Boy(w, at + new Vector3(0f, 5f, 0f), out var near); Boy(w, at + new Vector3(0f, 40f, 0f), out var far);
        w.Musou!.TryGet(near, out var n0);
        float d0 = (n0.Position - at).Length();
        Press(w, InputButtons.MeleeHeavy);
        Assert.True(Me(w).MeleeStep > 0 && Me(w).MeleeHeavy);
        float minHealth = n0.Health; bool displaced = false; MusouEnemyState seen = n0.State;
        for (int k = 0; k < 30 * 3; k++)
        {
            w.Step(_ => new PlayerInput(0, 0f, 0f, 0f, InputButtons.None));
            if (w.Musou!.TryGet(near, out var e)) { minHealth = MathF.Min(minHealth, e.Health); if ((e.Position - Me(w).Position).Length() > d0 + 0.3f || e.State != n0.State) displaced = true; }
        }
        Assert.True(minHealth <= n0.Health - 40f, $"the near Ork took {n0.Health - minHealth}");
        Assert.True(displaced, "the near Ork neither moved away nor changed state under the stomp");
        w.Musou!.TryGet(far, out var f);
        Assert.True(f.Health > 99000f, "the far Ork was hurt");
    }
}
