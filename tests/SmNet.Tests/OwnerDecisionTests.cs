using System.Numerics;
using SmContent;
using SmNet;
using SmServer;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// The owner's decisions of 2026-10-28 (custom classes, perks, bolters): everything unlocked; any perk of the class in either slot; the
/// pistol-slot bolters only with True Grit (rules that already held: pinned here); the standard bolter matching the bolt pistol's DPS and the
/// True Grit bolter 25 % above it (<see cref="OwnerBalance"/>, deliberate changes from the original). The blind grenade by facing: see
/// <see cref="BlindGrenadeTests"/>. Synthetic data only (weapons shaped like the install's numbers).
/// </summary>
public class OwnerDecisionTests(ITestOutputHelper output)
{
    static readonly SimConfig Custom = CustomFixture.Config;

    // ---------------------------------------------------------------- perks in either slot, everything unlocked

    [Theory]
    [InlineData(0.7f, 0f, 50f)]    // Impenetrable: the owner's target
    [InlineData(1f, 0f, 20f)]      // no perk
    public void A_stalker_headshot_on_full_armour(float received, float shield, float health)
    {
        var c = new SimConfig();
        var marine = new PlayerState { Alive = true, Health = 100f, Shield = 100f };
        var (after, _, _) = Combat.ApplyDamage(marine, 50f * received, OwnerBalance.StalkerShieldHead, 2f, c);
        Assert.Equal(shield, after.Shield, 3);
        Assert.Equal(health, after.Health, 3);
    }

    [Fact]
    public void Any_perk_of_the_class_goes_in_either_slot_and_only_a_repeat_is_dropped()
    {
        foreach (int cls in new[] { CustomFixture.SmTac, CustomFixture.SmAssault, CustomFixture.CsmTac, CustomFixture.CsmRaptor })
        {
            var perks = Loadouts.PerkChoices(Custom, cls);
            for (int a = 0; a < perks.Count; a++)
                for (int b = 0; b < perks.Count; b++)
                {
                    var resolved = Loadouts.Resolve(Custom, cls, new LoadoutChoice(Perk1: a, Perk2: b));
                    Assert.Equal(a, resolved.Perk1);
                    Assert.Equal(a == b ? -1 : b, resolved.Perk2);   // a perk twice: slot 2 dropped
                    var state = new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = cls, Perk1 = perks[a], Perk2 = a == b ? -1 : perks[b] };
                    Assert.Equal(perks[a], Loadouts.PerkIndex(Custom, state, 1));
                    Assert.Equal(a == b ? -1 : perks[b], Loadouts.PerkIndex(Custom, state, 2));
                }
            // Slot 2 may hold the class default while slot 1 holds another perk.
            int other = perks.ToList().FindIndex(p => p != Loadouts.DefaultPerk(Custom, cls));
            int dflt = perks.ToList().IndexOf(Loadouts.DefaultPerk(Custom, cls));
            Assert.Equal((other, dflt), (Loadouts.Resolve(Custom, cls, new LoadoutChoice(Perk1: other, Perk2: dflt)).Perk1, Loadouts.Resolve(Custom, cls, new LoadoutChoice(Perk1: other, Perk2: dflt)).Perk2));
            // An unset slot 1 is the class default, so the default in slot 2 alone is a repeat.
            Assert.Equal(-1, Loadouts.Resolve(Custom, cls, new LoadoutChoice(Perk2: dflt)).Perk2);
        }
    }

    [Fact]
    public void The_server_takes_True_Grit_in_slot_1_with_the_default_perk_in_slot_2()
    {
        // The reverse order (default in slot 1, True Grit in slot 2) is AppearanceReplicationTests' case.
        var h = new Harness(LinkConditions.Perfect, seed: 3, clients: 0, Custom, CustomFixture.Options);
        var c = h.AddClient(options: new ClientOptions { RequestedClass = CustomFixture.SmAssault, RequestedPistol = 2, RequestedPerk1 = 1, RequestedPerk2 = 0 }, nonce: 1);
        Assert.True(CustomFixture.RunUntil(h, () => c.Status == ClientStatus.Connected, TimeSpan.FromSeconds(2)));
        var p = h.Server.World.Players[c.PlayerId];
        Assert.Equal((CustomFixture.PistolBolter, CustomFixture.TrueGrit, CustomFixture.Charge), (p.Pistol, p.Perk1, p.Perk2));
        // A Setup with the perks swapped round applies at the respawn.
        Assert.Equal(new LoadoutChoice(CustomFixture.SmAssault, -1, 2, -1, -1, 0, 1),
            h.Server.World.SetNextLoadout(c.PlayerId, new LoadoutChoice(CustomFixture.SmAssault, Pistol: 2, Perk1: 0, Perk2: 1)));
    }

    [Fact]
    public void Everything_is_unlocked_by_default_on_the_server_and_in_the_creator()
    {
        Assert.Equal(UnlockMode.All, new ServerOptions().Unlocks);
        Assert.Equal(UnlockMode.All, new ServerArguments().Unlocks);
        Assert.True(ServerCommandLine.TryParse([], out var parsed, out _));
        Assert.Equal(UnlockMode.All, parsed.Unlocks);
        Assert.Equal(new[] { UnlockMode.All }, Enum.GetValues<UnlockMode>());   // the only mode: no level progression (owner, 2026-10-28)
    }

    // ---------------------------------------------------------------- the True Grit gate

    [Fact]
    public void A_pistol_slot_bolter_needs_True_Grit_in_either_slot()
    {
        Assert.True(Custom.PistolBoltersNeedTrueGrit);   // on by default (and hashed)
        const int cls = CustomFixture.SmAssault, bolter = 2, truegrit = 1, charge = 0;   // choice indices: pistols [sidearm, plasma, bolter]; perks [Charge, True Grit]
        Assert.Equal(-1, Loadouts.Resolve(Custom, cls, new LoadoutChoice(Pistol: bolter)).Pistol);
        Assert.Equal(-1, Loadouts.Resolve(Custom, cls, new LoadoutChoice(Pistol: bolter, Perk1: charge)).Pistol);
        Assert.Equal(bolter, Loadouts.Resolve(Custom, cls, new LoadoutChoice(Pistol: bolter, Perk1: truegrit)).Pistol);
        Assert.Equal(bolter, Loadouts.Resolve(Custom, cls, new LoadoutChoice(Pistol: bolter, Perk1: charge, Perk2: truegrit)).Pistol);
        // Other pistols never need it; without the rule the bolter is free.
        Assert.Equal(1, Loadouts.Resolve(Custom, cls, new LoadoutChoice(Pistol: 1)).Pistol);
        Assert.Equal(bolter, Loadouts.Resolve(Custom with { PistolBoltersNeedTrueGrit = false }, cls, new LoadoutChoice(Pistol: bolter)).Pistol);
        Assert.NotEqual(Custom.Hash(), (Custom with { PistolBoltersNeedTrueGrit = false }).Hash());
        // The state rule the spawn applies.
        var without = new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = cls, Perk1 = CustomFixture.Charge, Perk2 = -1 };
        Assert.False(Loadouts.PistolPerkRule(Custom, without, CustomFixture.PistolBolter));
        Assert.True(Loadouts.PistolPerkRule(Custom, without with { Perk2 = CustomFixture.TrueGrit }, CustomFixture.PistolBolter));
        Assert.True(Loadouts.PistolPerkRule(Custom, without with { Perk1 = CustomFixture.TrueGrit }, CustomFixture.PistolBolter));
        Assert.True(Loadouts.PistolPerkRule(Custom, without, CustomFixture.Sidearm));
    }

    // ---------------------------------------------------------------- the owner's bolter balance

    const float Tick = 1f / 30f, Shield = 100f, Health = 100f;

    /// <summary>A hit-scan gun with the install's shape: (name, slot, damage, cooldown, clip, reload, shield body multiplier); fall-off ×1 to 30 m.</summary>
    static WeaponSpec Gun(string name, WeaponSlot slot, float damage, float cooldown, int clip, float reload, float shieldBody) =>
        new(name, slot, TriggerKind.FullAuto, damage, cooldown, clip, clip * 9, reload, 250f, 30f, 40f, 0.78f, 2.5f, 1f, 0.5f, shieldBody);

    // The install's numbers (smtool weapon-dps auto --data): bolt pistol 40 / 0.25 s / 10 / 0.75 s, shield x0.7; bolter 22 / 0.133 s / 30 / 1.3 s,
    // shield x0.505 (Kraken Bolts: 0.155 s, shield x0.8); pistol-slot bolter 22 / 0.155 s / 30 / 1.5 s.
    static readonly WeaponSpec BoltPistol = Gun("sm_mp_pistol_bolt_pistol", WeaponSlot.Pistol, 40f, 0.25f, 10, 0.75f, 0.7f);
    static readonly WeaponSpec Bolter = Gun("sm_mp_ranged_bolter", WeaponSlot.Ranged, 22f, 0.133f, 30, 1.3f, 0.505f) with
    {
        Variants = [new UpgradeVariant<WeaponSpec>(["mp_perk_weap_bolter_kraken_rounds"], Gun("sm_mp_ranged_bolter", WeaponSlot.Ranged, 22f, 0.155f, 30, 1.3f, 0.8f))],
    };
    static readonly WeaponSpec PistolBolter = Gun("sm_mp_pistol_bolter", WeaponSlot.Pistol, 22f, 0.155f, 30, 1.5f, 0.505f) with
    {
        Variants = [new UpgradeVariant<WeaponSpec>(["mp_perk_weap_bolter_kraken_rounds"], Gun("sm_mp_pistol_bolter", WeaponSlot.Pistol, 22f, 0.155f, 30, 1.5f, 0.8f))],
    };
    static readonly WeaponSpec[] Data = [BoltPistol, Bolter, PistolBolter, Gun("sm_mp_ranged_storm_bolter", WeaponSlot.Ranged, 22f, 0.1f, 100, 2f, 0.5f)];

    static WeaponDps Dps(WeaponSpec w) => OwnerBalance.Measure(w, OwnerBalance.MediumRange, Tick, Shield, Health);

    [Fact]
    public void The_data_bolter_out_damages_the_pistol_on_health_but_kills_a_shielded_marine_slower()
    {
        var pistol = Dps(BoltPistol);
        var bolter = Dps(Bolter);
        output.WriteLine($"pistol burst {pistol.Burst:0.0} sustained {pistol.Sustained:0.0}; vs marine {pistol.BurstVsMarine:0.0} / {pistol.SustainedVsMarine:0.0}");
        output.WriteLine($"bolter burst {bolter.Burst:0.0} sustained {bolter.Sustained:0.0}; vs marine {bolter.BurstVsMarine:0.0} / {bolter.SustainedVsMarine:0.0}");
        Assert.Equal(150f, pistol.Burst, 1);       // 40 every 8 ticks
        Assert.Equal(117.1f, pistol.Sustained, 1);
        Assert.Equal(165f, bolter.Burst, 1);       // 22 every 4 ticks
        Assert.Equal(124.5f, bolter.Sustained, 1);
        Assert.True(bolter.BurstVsMarine < pistol.BurstVsMarine && bolter.SustainedVsMarine < pistol.SustainedVsMarine);
        // With no shield the two measures agree.
        var bare = OwnerBalance.Measure(Bolter, 15f, Tick, 0f, Health);
        Assert.Equal((bare.Burst, bare.Sustained), (bare.BurstVsMarine, bare.SustainedVsMarine));
    }

    [Fact]
    public void The_standard_bolter_matches_the_pistol_and_the_True_Grit_bolter_is_25_percent_above_within_3_percent()
    {
        var (weapons, rows) = OwnerBalance.Apply(Data, Tick, Shield, Health);
        var pistol = Dps(BoltPistol);
        foreach (var row in rows)
            output.WriteLine($"{row.Data.Name}: {row.Data.Damage} -> {row.Result.Damage} (x{row.Ratio}); {row.Decision}");

        Assert.Equal(4, rows.Count);   // only rules whose weapon and reference exist: each weapon and its perk variant
        Assert.Equal(["default", "mp_perk_weap_bolter_kraken_rounds", "default", "mp_perk_weap_bolter_kraken_rounds"], rows.Select(r => r.Variant));
        var bolter = Dps(weapons[1]);
        var grit = Dps(weapons[2]);
        var bolterKraken = Dps(weapons[1].Variants[0].Spec);
        var gritKraken = Dps(weapons[2].Variants[0].Spec);
        void Meets(WeaponDps w, float ratio)
        {
            float burst = w.BurstVsMarine / (ratio * pistol.BurstVsMarine), sustained = w.SustainedVsMarine / (ratio * pistol.SustainedVsMarine);
            output.WriteLine($"  {w.Weapon}: burst x{burst:0.000}, sustained x{sustained:0.000} of the target");
            Assert.True(burst >= 1f - 1e-4f && sustained >= 1f - 1e-4f);                 // both at least the target
            Assert.InRange(MathF.Min(burst, sustained), 1f, 1.03f);                         // the binding one within 3 %
            Assert.InRange(MathF.Max(burst, sustained), 1f, 1.05f);                         // the other close above
        }
        Meets(bolter, OwnerBalance.BolterRatio);
        Meets(grit, OwnerBalance.TrueGritRatio);
        // Each perk variant against the same target on its own (not the base's factor, which put True Grit + Kraken at about 1.68 x the pistol).
        Meets(bolterKraken, OwnerBalance.BolterRatio);
        Meets(gritKraken, OwnerBalance.TrueGritRatio);
        Assert.Equal(25.4f, weapons[1].Damage, 3);
        Assert.Equal(39f, weapons[2].Damage, 3);
        Assert.Equal(23.2f, weapons[1].Variants[0].Spec.Damage, 3);
        Assert.Equal(29.4f, weapons[2].Variants[0].Spec.Damage, 3);
        // Everything else as read: the pistol, the storm bolter, the bolter's other fields.
        Assert.Same(Data[0], weapons[0]);
        Assert.Same(Data[3], weapons[3]);
        Assert.Equal(Bolter with { Damage = 25.4f, Variants = weapons[1].Variants }, weapons[1]);
        // The perk variant keeps its own fields and its upgrade strings.
        var kraken = Assert.Single(weapons[1].Variants);
        Assert.Equal(Bolter.Variants[0].Upgrades, kraken.Upgrades);
        Assert.Equal((0.155f, 0.8f), (kraken.Spec.ShotCooldown, kraken.Spec.ShieldBodyMultiplier));
    }

    [Fact]
    public void A_bolter_already_at_the_pistol_is_left_alone_and_missing_weapons_do_nothing()
    {
        var strong = Bolter with { Damage = 30f, Variants = [] };
        var (weapons, rows) = OwnerBalance.Apply([BoltPistol, strong], Tick, Shield, Health);
        Assert.Same(strong, weapons[1]);
        Assert.False(Assert.Single(rows).Changed);
        var (alone, none) = OwnerBalance.Apply([Bolter], Tick, Shield, Health);   // no bolt pistol: no reference
        Assert.Empty(none);
        Assert.Same(Bolter, alone[0]);
    }

    [Fact]
    public void The_override_changes_the_config_hash()
    {
        var (weapons, _) = OwnerBalance.Apply(Data, Tick, Shield, Health);
        Assert.NotEqual(new SimConfig { Weapons = Data }.Hash(), new SimConfig { Weapons = weapons }.Hash());
        var variantOnly = weapons.Select((w, i) => i == 1 ? w with { Variants = Data[1].Variants } : w).ToArray();
        Assert.NotEqual(new SimConfig { Weapons = variantOnly }.Hash(), new SimConfig { Weapons = weapons }.Hash());   // variants are hashed too
    }

    [Fact]
    public void A_variant_already_at_its_target_keeps_the_data_and_a_lower_one_is_raised_alone()
    {
        var strongKraken = Bolter with { Variants = [Bolter.Variants[0] with { Spec = Bolter.Variants[0].Spec with { Damage = 40f } }] };
        var (weapons, rows) = OwnerBalance.Apply([BoltPistol, strongKraken], Tick, Shield, Health);
        Assert.Equal(25.4f, weapons[1].Damage, 3);
        Assert.Equal(40f, weapons[1].Variants[0].Spec.Damage);   // never lowered
        Assert.False(rows[1].Changed);
    }

    [Fact]
    public void The_measure_counts_burst_rounds_and_a_zero_shield_multiplier_skips_the_shield()
    {
        // A two-round burst every 8 ticks: twice the rounds per cooldown; a 10-round clip is 5 pulls.
        var burst = BoltPistol with { BurstLength = 2, BurstInterval = 0.05f };
        var one = Dps(BoltPistol);
        var two = Dps(burst);
        Assert.Equal(2, two.Rounds);
        Assert.Equal(2f * one.Burst, two.Burst, 2);
        Assert.Equal(10f * 40f / (5f * 8f / 30f + 0.75f), two.Sustained, 2);
        // Shield multiplier 0: Combat.ApplyDamage never touches the shield, so the kill takes only the health (200 / time to kill 100 health).
        var noShield = Dps(BoltPistol with { ShieldBodyMultiplier = 0f });
        Assert.Equal(2f * one.Burst, noShield.BurstVsMarine, 2);
        var check = Combat.ApplyDamage(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Alive = true, Health = 100f, Shield = 100f }, 40f, 0f, 1f, new SimConfig());
        Assert.Equal((100f, 60f), (check.After.Shield, check.After.Health));
    }
}
