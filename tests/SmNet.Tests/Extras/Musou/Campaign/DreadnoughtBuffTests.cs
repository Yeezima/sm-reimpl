using System.Numerics;
using SmContent.Extras.Musou;
using ContentArsenal = SmContent.Extras.Musou.Arsenal.Arsenal;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Arsenal;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Musou.Campaign;

/// <summary>
/// The Musou Dreadnought buff (owner, 2026-11-15): melee 30 % faster with every enemy in reach struck; the cannon is the Musou autocannon's numbers at the braced heavy bolter's rate, with no ammunition and a heat meter that
/// overheats after the autocannon's own seconds of fire; the melta is the Tactical's scaled up (x1.5 damage, no fall-off, 0.6 reload, 1.5 reach, no ammunition). The sources below are the install's values written out as the reader gives them.
/// </summary>
public class DreadnoughtBuffTests
{
    static readonly WeaponSpec ChaosFile = new("csm_mp_exotic_auto_cannon", WeaponSlot.Ranged, TriggerKind.SemiAuto, 50f, 0.4f, 120, 0, 2f, 150f, 45f, 70f, 0.75f, 1f, 1f, 1f, 1f)
    {
        Heat = new HeatSpec(1f, 15f, 9f, 1f, 4.5f, 0.41f, 0.41f), BurstLength = 2, BurstInterval = 0.2f,
    };
    static readonly WeaponSpec HeavyBolter = new("sm_mp_exotic_heavy_bolter", WeaponSlot.Ranged, TriggerKind.FullAuto, 35f, 0.233f, 400, 0, 3f, 250f, 50f, 75f, 0.8f, 1f, 1f, 0.7f, 0.7f)
    {
        RaiseDelay = 0.333f, AlwaysReady = true, Brace = new BraceSpec(0.1f, 1.8f),
    };
    static readonly ConeSpec TacticalCone = new(20f, 10f, 20f, -20f, new DamageSpec(200f, 25f, 1f, 1f, 1f, 1f, false), [new(6.5f, 1f), new(10f, 0.5f), new(20f, 0.15f)], []);
    static readonly WeaponSpec TacticalMelta = new("sm_mp_ranged_melta", WeaponSlot.Ranged, TriggerKind.SemiAuto, 200f, 0.33f, 1, 12, 0.5f, 20f, 10f, 20f, 0.15f, 1f, 1f, 1f, 1f)
    {
        Delivery = DeliveryKind.Cone, Cone = TacticalCone, Recharge = new RechargeSpec(0.5f, 1f / 12f),
    };

    static readonly WeaponSpec Autocannon = ContentArsenal.MusouTwin(ChaosFile, HeavyBolter);
    static readonly WeaponSpec Cannon = DreadnoughtWeapons.CannonFromAutocannon(Autocannon, HeavyBolter.Brace!.ShotCooldown);
    static readonly WeaponSpec Melta = DreadnoughtWeapons.MeltaFromTactical(TacticalMelta);

    static readonly MusouRules Still = MusouRules.Default with { PlayerDamageScale = 0f, Types = [.. MusouRules.Default.Types.Select(t => t with { Speed = 0f })] };
    static readonly SimConfig Config = MusouLoadout.Prepare(TfwRig.Config with { Weapons = [.. TfwRig.Config.Weapons, Cannon, Melta], MusouRules = Still });
    static int Dread => Config.Classes.ToList().FindIndex(k => k.Name == DreadnoughtRules.ClassName);
    const int Human = TfwRig.Human;

    static World Landed(SimConfig? config = null)
    {
        config ??= Config;
        int dread = config.Classes.ToList().FindIndex(k => k.Name == DreadnoughtRules.ClassName);
        var w = TfwRig.War(null, config);
        Assert.NotNull(w.SetNextLoadout(Human, LoadoutChoice.Default with { Class = dread }));
        TfwRig.Command(w, TfwCommand.ChangeClass);
        TfwRig.Steps(w, 3);
        TfwRig.Deploy(w);
        TfwRig.Seconds(w, DreadnoughtRules.EntranceSeconds + 0.3f);
        w.Musou!.Frozen = true;
        return w;
    }

    static PlayerState Me(World w) => w.Players[Human];
    static void Step(World w, InputButtons b = InputButtons.None) => w.Step(_ => new PlayerInput(0, 0f, 0f, 0f, b));
    static ushort Boy(World w, Vector3 at) { ushort id = w.Musou!.Spawn(MusouEnemyType.Boy, at, 0f, 100000f); Assert.NotEqual(0, id); return id; }
    static float Health(World w, ushort id) => w.Musou!.TryGet(id, out var e) ? e.Health : 0f;

    // ---------------------------------------------------------------- melee

    [Fact]
    public void Every_strike_time_is_the_datas_divided_by_1_3()
    {
        var data = DreadnoughtMelee.DataLight().Concat(DreadnoughtMelee.DataHeavy()).ToList();
        var fast = DreadnoughtMelee.Light().Concat(DreadnoughtMelee.Heavy()).ToList();
        Assert.Equal(data.Count, fast.Count);
        for (int i = 0; i < data.Count; i++)
        {
            Assert.Equal(data[i].Duration / 1.3f, fast[i].Duration, 4);
            Assert.Equal(data[i].HitTime / 1.3f, fast[i].HitTime, 4);
            Assert.Equal(data[i].ComboOpen / 1.3f, fast[i].ComboOpen, 4);
            Assert.Equal(data[i].ComboClose / 1.3f, fast[i].ComboClose, 4);
            Assert.Equal(data[i].MoveAt / 1.3f, fast[i].MoveAt, 4);
            Assert.Equal(data[i].YComboOpen / 1.3f, fast[i].YComboOpen, 4);
            Assert.Equal(data[i].Damage, fast[i].Damage);   // damage and shield damage untouched
            Assert.Equal(data[i].ShieldDamage, fast[i].ShieldDamage);
            Assert.Equal(1.3f, data[i].HitTime / fast[i].HitTime, 3);   // the clip played at 1.3 x lands its hit frame at the sim's hit time
        }
        Assert.Equal(1.3f, DreadnoughtRules.MeleeSpeed);
    }

    // The war's allies (Guardsmen, AI Marines) chip the frozen test Orks for 5 to 15 a hit: the strike's own hit is 50 to 125.
    const float Chip = 40f;

    [Fact]
    public void A_light_strike_lands_its_hit_and_ends_at_the_scaled_times_in_a_world()
    {
        var w = Landed();
        var boy = Boy(w, Me(w).Position + new Vector3(0f, 1.8f, 0f));
        float before = Health(w, boy);
        int hit = -1, end = -1, which = 0;
        for (int k = 0; k < 120; k++)
        {
            Step(w, k == 0 ? InputButtons.Melee : InputButtons.None);
            if (k == 1) which = Me(w).MeleeStep;
            if (hit < 0 && Health(w, boy) < before - 90f) hit = k;   // the primary hit: 100 or 125
            if (end < 0 && k > 3 && Me(w).MeleeStep == 0) { end = k; break; }
        }
        var step = DreadnoughtMelee.Light()[which - 1];
        float tick = 1f / 30f;
        Assert.True(hit >= 0, "the strike hit the Ork");
        Assert.InRange(hit * tick, step.HitTime - 3 * tick, step.HitTime + 3 * tick);   // 0.867 / 1.3 = 0.667 s, or X02's 0.6 / 1.3
        Assert.InRange(end * tick, step.Duration - 3 * tick, step.Duration + 3 * tick);
    }

    [Fact]
    public void A_swing_strikes_every_enemy_in_its_reach_and_not_the_one_outside()
    {
        var w = Landed();
        var shape = MusouCleaveShape.Of(w.Config, DreadnoughtMelee.Light()[0], war: true, dreadnought: true);   // his reach is 1.5 times the ordinary (2026-11-17)
        Assert.True(shape.Most >= 64, $"no cap on the strike: {shape.Most}");
        var at = Me(w).Position;
        var inside = new List<ushort>();
        for (int k = 0; k < 8; k++)
        {
            float a = (-100f + 200f * k / 7f) * MathF.PI / 180f;   // inside the half angle, ahead of him (+Y)
            float r = shape.Reach * 0.85f;
            inside.Add(Boy(w, at + new Vector3(MathF.Sin(a) * r, MathF.Cos(a) * r, 0f)));
        }
        ushort outside = Boy(w, at + new Vector3(0f, shape.Reach + 1.5f, 0f));
        var before = inside.ToDictionary(i => i, i => Health(w, i));
        float outsideBefore = Health(w, outside);
        float biggestDrop = 0f, last = outsideBefore;
        for (int k = 0; k < 40; k++)
        {
            Step(w, k == 0 ? InputButtons.Melee : InputButtons.None);
            biggestDrop = MathF.Max(biggestDrop, last - Health(w, outside)); last = Health(w, outside);
        }
        foreach (var id in inside) Assert.True(Health(w, id) < before[id] - 49f, $"Ork {id} inside the reach was struck ({before[id] - Health(w, id)})");
        Assert.True(biggestDrop < Chip, $"the Ork outside took a hit of {biggestDrop} in one tick (the allies' chips are under {Chip}, the swing's are 50 or more)");
    }

    // ---------------------------------------------------------------- the cannon

    [Fact]
    public void The_cannon_is_the_autocannons_numbers_at_the_braced_heavy_bolters_rate()
    {
        Assert.Equal(DreadnoughtRules.CannonWargear, Cannon.Name);
        Assert.Equal(AutocannonRules.DirectDamage, Cannon.Damage);
        Assert.Equal((Autocannon.MaxRange, Autocannon.FallOffStart, Autocannon.FallOffEnd, Autocannon.FallOffEndMultiplier), (Cannon.MaxRange, Cannon.FallOffStart, Cannon.FallOffEnd, Cannon.FallOffEndMultiplier));
        Assert.Equal((Autocannon.HeadMultiplier, Autocannon.BodyMultiplier, Autocannon.Delivery, Autocannon.Trigger), (Cannon.HeadMultiplier, Cannon.BodyMultiplier, Cannon.Delivery, Cannon.Trigger));
        Assert.Equal(HeavyBolter.Brace!.ShotCooldown, Cannon.ShotCooldown);
        Assert.Equal(0.1f, Cannon.ShotCooldown);
        Assert.True(Cannon.ShotCooldown < Autocannon.ShotCooldown);
        Assert.True(Config.EndlessClip(Cannon) && Config.EndlessClip(Melta));
        Assert.True(Config.Classes[Dread].PrimaryChoices.Contains(Config.Weapons.ToList().FindIndex(x => x.Name == Cannon.Name)));
    }

    [Fact]
    public void A_round_hits_and_blasts_as_the_autocannons_does()
    {
        var (dDirect, dBlast) = OneRound(Landed());
        var (aDirect, aBlast) = OneRound(AutocannonWorld());
        Assert.True(dDirect > 100f, $"direct {dDirect}");
        Assert.Equal(aDirect, dDirect, 0.01f);   // the same round and the same blast at a Boy 12 m ahead and one 2 m beside it
        Assert.Equal(aBlast, dBlast, 0.01f);
        Assert.True(dBlast >= AutocannonRules.BlastDamage * TfwCombatRules.Default.PlayerRangedDamageScale - 0.5f, $"blast {dBlast}");
    }

    static (float Direct, float Blast) OneRound(World w)
    {
        var at = Me(w).Position;
        var target = Boy(w, at + new Vector3(0f, 12f, 0f));
        var neighbour = Boy(w, at + new Vector3(2f, 12f, 0f));
        float t0 = Health(w, target), n0 = Health(w, neighbour);
        Step(w, InputButtons.Fire);
        Step(w);
        return (t0 - Health(w, target), n0 - Health(w, neighbour));
    }

    [Fact]
    public void The_cannon_fires_ten_rounds_a_second_and_spends_no_ammunition()
    {
        var w = Landed();
        int clip = Me(w).Clip;
        var heat = new List<float>();
        for (int k = 0; k < 30; k++) { Step(w, InputButtons.Fire); heat.Add(Me(w).Heat); }
        float perShot = Cannon.Heat!.PerShot;
        float rounds = Me(w).Heat / perShot;
        Assert.InRange(rounds, 9f, 11f);   // 0.1 s apart: 10 a second (the autocannon's 2.5)
        Assert.Equal(clip, Me(w).Clip);
        Assert.Equal(DreadnoughtRules.CannonWargear, Loadouts.InHand(w.Config, Me(w))!.Name);
    }

    [Fact]
    public void Heat_per_shot_makes_continuous_fire_overheat_after_the_autocannons_own_seconds()
    {
        // summed as the sim sums it: round 57 reaches the overheat, round 56 does not
        float sum = 0f, perShot = Cannon.Heat!.PerShot;
        for (int k = 1; k <= 56; k++) sum += perShot;
        Assert.True(sum < Cannon.Heat.Overheat, $"56 rounds heat {sum} of {Cannon.Heat.Overheat}");
        sum += perShot;
        Assert.True(sum >= Cannon.Heat.Overheat, $"57 rounds heat {sum} of {Cannon.Heat.Overheat}");
        Assert.Equal(Autocannon.Heat!.Overheat, Cannon.Heat.Overheat);
        Assert.Equal((Autocannon.Heat.CoolRate, Autocannon.Heat.CoolDelay, Autocannon.Heat.Recovery), (Cannon.Heat.CoolRate, Cannon.Heat.CoolDelay, Cannon.Heat.Recovery));
        // the dreadnought's overheat tick against the autocannon's, in worlds
        int dread = OverheatTick(Landed(), null);
        int auto = OverheatTick(AutocannonWorld(), null);
        Assert.InRange(dread, 1, 600);
        Assert.Equal(auto, dread);   // the same tick
        Assert.InRange(auto / 30f, 5.3f, 6.0f);
    }

    static int OverheatTick(World w, object? unused)
    {
        for (int k = 1; k <= 600; k++) { Step(w, InputButtons.Fire); if (Me(w).Overheated) return k; }
        return -1;
    }

    /// <summary>The same Dreadnought carrying the autocannon itself (0.4 s between rounds, its heat) in the cannon's place: the comparison for the cannon's blast and overheat.</summary>
    static World AutocannonWorld() => Landed(Config with { Weapons = [.. Config.Weapons.Select(x => x.Name == DreadnoughtRules.CannonWargear ? Autocannon with { Name = DreadnoughtRules.CannonWargear } : x)] });

    [Fact]
    public void Every_rule_that_treats_a_gun_as_the_autocannon_treats_his_cannon_so()
    {
        Assert.True(DreadnoughtRules.IsCannon(Cannon.Name) && !DreadnoughtRules.IsCannon(Melta.Name) && DreadnoughtRules.IsMelta(Melta.Name));
        Assert.Equal(TfwCombatRules.Pierced(Autocannon), TfwCombatRules.Pierced(Cannon));   // 5 Orks
        Assert.Equal(5, TfwCombatRules.Pierced(Cannon));
        Assert.Equal(SmNet.Extras.Musou.MusouOrkFlinchRules.ShotStrength(Autocannon.Name), SmNet.Extras.Musou.MusouOrkFlinchRules.ShotStrength(Cannon.Name));   // 1.8
        Assert.Equal(1.8f, SmNet.Extras.Musou.MusouOrkFlinchRules.ShotStrength(Cannon.Name));
        Assert.True(Config.EndlessClip(Cannon));
        // its blast: ArsenalShotBlast, proven by the neighbour's damage in A_round_hits_and_blasts_as_the_autocannons_does
    }

    [Fact]
    public void His_swing_has_no_cap_outside_the_war_too_in_the_5_wave_run()
    {
        // the 5-wave run's cleave takes 8 enemies (MusouCombatRules.CleaveTargets); the Dreadnought's takes every one in reach, a Tactical's still 8
        Assert.Equal(8, MusouRules.Default.Combat.CleaveTargets);
        int Struck(string cls)
        {
            var rifle = TfwRig.Config.Weapons[0];
            var tactical = new ClassLoadout("sm - tactical", 0, -1, [new MeleeStep(1f, 0.2f, 0.4f, 0.6f, 0.5f, 0.7f, 25f, 10f, 3.5f)]) { PrimaryChoices = [0], EquipmentChoices = [], Side = Team.SpaceMarines, Role = "tactical" };
            var config = MusouLoadout.Prepare(MusouSimTests.Base with { Weapons = [rifle, Cannon, Melta], Thrown = [], Classes = [tactical], MusouRules = Still, Extras = ContentExtras.Musou });
            var w = new World(config) { Rules = MusouSimTests.Endless };
            Assert.True(w.StartMusou(null));
            w.MusouDirector!.Hold = true;
            int index = config.Classes.ToList().FindIndex(c => c.Name == cls);
            Assert.True(index >= 0, cls);
            w.Spawn(1, index);
            for (int t = 0; t < 160; t++) w.Step(_ => new PlayerInput(0, 0f, 0f, 0f, 0));   // past his entrance's lock
            var at = w.Players[1].Position;
            var ids = new List<ushort>();
            for (int k = 0; k < 14; k++) ids.Add(w.Musou!.Spawn(MusouEnemyType.Boy, at + new Vector3((k - 6.5f) * 0.12f, 1.4f + (k % 3) * 0.1f, 0f), 0f, 100000f));
            for (int t = 0; t < 40; t++) w.Step(_ => new PlayerInput(0, 0f, 0f, 0f, t == 0 ? InputButtons.Melee : 0));
            return ids.Count(i => w.Musou!.TryGet(i, out var e) && e.Health < 100000f - 1f);
        }
        Assert.Equal(14, Struck(DreadnoughtRules.ClassName));
        Assert.InRange(Struck("sm - tactical"), 1, 8);
    }

    // ---------------------------------------------------------------- the melta

    [Fact]
    public void The_melta_is_the_tacticals_scaled_up()
    {
        Assert.Equal(DreadnoughtRules.MeltaWargear, Melta.Name);
        Assert.Equal(WeaponSlot.Pistol, Melta.Slot);
        Assert.Equal(TacticalMelta.Damage * 1.5f, Melta.Damage);
        Assert.Equal(TacticalCone.Damage.Damage * 1.5f, Melta.Cone!.Damage.Damage);
        Assert.Equal(TacticalCone.Length * 1.5f, Melta.Cone.Length);
        Assert.Equal(TacticalMelta.MaxRange * 1.5f, Melta.MaxRange);
        Assert.Equal(TacticalMelta.ReloadTime * 0.6f, Melta.ReloadTime, 4);
        Assert.Equal(TacticalMelta.Recharge!.RoundTime * 0.6f, Melta.Recharge!.RoundTime, 3);
        // full damage at every distance out to the end of the cone: no fall-off
        foreach (float d in new[] { 0f, 6.5f, 15f, Melta.Cone.Length })
            Assert.Equal(1f, Combat.SplashIntensity(Melta.Cone.FallOff, d), 4);
        Assert.All(Melta.Cone.FallOff, p => Assert.Equal(1f, p.Value));
        Assert.Equal(1f, Melta.FallOffEndMultiplier);
        Assert.True(Config.EndlessClip(Melta));
    }

    [Fact]
    public void The_melta_spends_no_round_and_shoots_again_at_its_cooldown()
    {
        var w = Landed();
        Step(w, InputButtons.SlotPistol);
        TfwRig.Seconds(w, 1.5f);
        Assert.Equal(WeaponSlot.Pistol, Me(w).Slot);
        int clip = Me(w).Clip;
        for (int k = 0; k < 30 * 3; k++) { Step(w, k % 12 == 0 ? InputButtons.Fire : InputButtons.None); }
        Assert.Equal(clip, Me(w).Clip);
    }

    // ---------------------------------------------------------------- Classic

    [Fact]
    public void Classic_has_no_endless_dreadnought_gun()
    {
        var classic = TfwRig.Config with { Extras = ContentExtras.None };
        Assert.False(classic.EndlessClip(Cannon));
        Assert.False(classic.EndlessClip(Melta));
    }
}
